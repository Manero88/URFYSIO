using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

public class RegistrationService : IRegistrationService
{
    private readonly AppDbContext _db;
    private readonly IUserService _userService;
    private readonly IAuth0ManagementService _auth0;
    private readonly ILogger<RegistrationService> _logger;

    public RegistrationService(
        AppDbContext db,
        IUserService userService,
        IAuth0ManagementService auth0,
        ILogger<RegistrationService> logger)
    {
        _db = db;
        _userService = userService;
        _auth0 = auth0;
        _logger = logger;
    }

    public async Task<RegistrationRequest?> GetByIdAsync(Guid id) =>
        await _db.RegistrationRequests.FirstOrDefaultAsync(r => r.Id == id);

    public async Task<IReadOnlyList<RegistrationRequest>> GetAllAsync(RegistrationStatus? statusFilter = null)
    {
        var query = _db.RegistrationRequests.AsQueryable();
        if (statusFilter.HasValue)
            query = query.Where(r => r.Status == statusFilter.Value);
        return await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<RegistrationRequest> CreateAsync(RegistrationRequest request)
    {
        // Check for an existing local user — differentiate so the registrant knows what to do.
        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (existingUser is not null)
        {
            if (existingUser.IsActive)
                throw DomainException.Conflict(
                    "This email address is already registered. Please log in instead.");
            else
                // Inactive row: typically auto-created by Auth0UserSyncMiddleware when someone
                // previously logged in via Google/Microsoft before they were approved. The account
                // exists but isn't active yet — direct them to log in (approval will activate it).
                throw DomainException.Conflict(
                    "This email address is already associated with an account that is pending activation. " +
                    "Please log in, or contact the practice if you need help.");
        }

        if (await _db.RegistrationRequests.AnyAsync(r => r.Email == request.Email && r.Status == RegistrationStatus.Pending))
            throw DomainException.Conflict(
                "A registration for this email is already pending approval. " +
                "Please wait for the administrator to review your request.");

        request.Id = Guid.NewGuid();
        request.Status = RegistrationStatus.Pending;
        request.CreatedAt = DateTime.UtcNow;

        _db.RegistrationRequests.Add(request);
        await _db.SaveChangesAsync();

        return request;
    }

    public async Task<RegistrationRequest> ApproveAsync(Guid id, Guid processedByUserId)
    {
        var request = await _db.RegistrationRequests.FindAsync(id)
            ?? throw DomainException.NotFound("Registration request not found.");

        if (request.Status != RegistrationStatus.Pending)
            throw DomainException.Conflict("This request has already been processed.");

        // Step 1: ensure an Auth0 account exists for this email — create one, or LINK
        // the one that's already there. Login goes through Auth0, so without an Auth0
        // identity the approved person could never sign in. We resolve this BEFORE
        // touching the DB so that if Auth0 fails we abort cleanly with nothing changed
        // (the request stays Pending and the admin sees why).
        var auth0Result = await _auth0.CreateUserAsync(request.Email, request.FirstName, request.LastName);

        string auth0Id;
        bool createdNewAuth0User;
        if (auth0Result.Success)
        {
            auth0Id = auth0Result.UserId!;
            createdNewAuth0User = true;
        }
        else if (auth0Result.AlreadyExists)
        {
            // The email already has an Auth0 identity — typically someone who
            // previously signed in with "Continue with Google" using the same Gmail.
            // Instead of failing the approval, look the identity up and link it.
            var existingId = await _auth0.GetUserIdByEmailAsync(request.Email);
            if (existingId is null)
                throw DomainException.Conflict(
                    "This email already has an account in the system (the user may have previously " +
                    "logged in with Google using this email), but it could not be retrieved from Auth0. " +
                    "Check the Auth0 dashboard for this email and try again.");

            auth0Id = existingId;
            createdNewAuth0User = false;
            _logger.LogInformation(
                "Approval: linking existing Auth0 account {Auth0Id} for {Email} instead of creating a new one.",
                auth0Id, request.Email);
        }
        else
        {
            // 502: the failure is upstream (Auth0), not the admin's request.
            throw new DomainException(
                auth0Result.Error ?? "Could not create the Auth0 account. Please try again.", 502);
        }

        // Step 2: mark approved and ensure a local user exists and is ACTIVE.
        request.Status = RegistrationStatus.Approved;
        request.ProcessedByUserId = processedByUserId;

        // When linking an existing Auth0 identity, a local row may already exist:
        // Auth0UserSyncMiddleware auto-creates an INACTIVE user the first time someone
        // logs in without a local record (e.g. their earlier Google login). Approving
        // the registration is exactly the admin action that should activate that row —
        // creating a second user would just blow up on the unique-email constraint.
        var localUser = await _userService.GetByAuth0IdAsync(auth0Id)
                     ?? await _userService.GetByEmailAsync(request.Email);

        if (localUser is not null)
        {
            localUser.Auth0Id ??= auth0Id;
            localUser.IsActive = true;
            // The registrant gave their real name on the form — better than the
            // "Unknown User" placeholder the middleware may have seeded.
            localUser.FirstName = request.FirstName;
            localUser.LastName = request.LastName;
            localUser.PhoneNumber ??= request.PhoneNumber;
            // Role is deliberately left as-is: auto-created rows default to Client,
            // and we must not downgrade an existing physio/admin. UpdateAsync also
            // ensures the role-matching profile (ClientProfile) exists.
            await _userService.UpdateAsync(localUser);
        }
        else
        {
            // Fresh local mirror record. Auth0Id is set so Auth0UserSyncMiddleware
            // matches this row by sub on first login and does NOT create a duplicate.
            // No password stored locally — Auth0 owns the credential.
            var user = new User
            {
                Auth0Id = auth0Id,
                Email = request.Email,
                FirstName = request.FirstName,
                LastName = request.LastName,
                PhoneNumber = request.PhoneNumber,
                Role = UserRole.Client,
                IsActive = true
            };
            await _userService.CreateAsync(user, string.Empty);
        }

        // Step 3: best-effort post-steps. A failure here shouldn't roll back the
        // approval — the account exists and is usable; these are recoverable by the
        // admin (re-trigger reset) or a re-login (role claim). Log and continue.
        if (!await _auth0.AssignRoleAsync(auth0Id, "Client"))
            _logger.LogWarning("Approval: failed to assign Client role to {Auth0Id} ({Email}).", auth0Id, request.Email);

        // Password-setup email only applies to database ("auth0|") identities. A
        // linked Google-only account has no Auth0 password — they keep logging in
        // with Google, and the /dbconnections/change_password call would be a no-op
        // or a confusing email.
        if (auth0Id.StartsWith("auth0|", StringComparison.Ordinal))
        {
            if (!await _auth0.SendPasswordResetEmailAsync(request.Email))
                _logger.LogWarning("Approval: failed to send password-setup email to {Email}.", request.Email);
        }
        else
        {
            _logger.LogInformation(
                "Approval: {Email} is linked to SSO identity {Auth0Id}; no password-setup email needed (they log in via their provider). " +
                "CreatedNewAuth0User={CreatedNew}", request.Email, auth0Id, createdNewAuth0User);
        }

        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<RegistrationRequest> RejectAsync(Guid id, Guid processedByUserId)
    {
        var request = await _db.RegistrationRequests.FindAsync(id)
            ?? throw DomainException.NotFound("Registration request not found.");

        if (request.Status != RegistrationStatus.Pending)
            throw DomainException.Conflict("This request has already been processed.");

        request.Status = RegistrationStatus.Rejected;
        request.ProcessedByUserId = processedByUserId;

        await _db.SaveChangesAsync();
        return request;
    }
}
