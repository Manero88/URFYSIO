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
        // Check for duplicate email
        if (await _db.Users.AnyAsync(u => u.Email == request.Email))
            throw DomainException.Conflict("A user with this email already exists.");

        if (await _db.RegistrationRequests.AnyAsync(r => r.Email == request.Email && r.Status == RegistrationStatus.Pending))
            throw DomainException.Conflict("A pending registration request for this email already exists.");

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

        // Step 1: create the Auth0 account FIRST. Login goes through Auth0, so without
        // an Auth0 user the approved person could never sign in. We do this before
        // touching the DB so that if Auth0 fails we abort cleanly with nothing changed
        // (transactional integrity — the request stays Pending and the admin sees why).
        var auth0Result = await _auth0.CreateUserAsync(request.Email, request.FirstName, request.LastName);
        if (!auth0Result.Success)
        {
            if (auth0Result.AlreadyExists)
                throw DomainException.Conflict(auth0Result.Error ?? "An Auth0 account already exists for this email.");
            // 502: the failure is upstream (Auth0), not the admin's request.
            throw new DomainException(
                auth0Result.Error ?? "Could not create the Auth0 account. Please try again.", 502);
        }

        var auth0Id = auth0Result.UserId!;

        // Step 2: now safe to mark approved and create the local mirror record.
        request.Status = RegistrationStatus.Approved;
        request.ProcessedByUserId = processedByUserId;

        // Auth0Id is set so Auth0UserSyncMiddleware matches this row by sub on the
        // user's first login (GetByAuth0IdAsync) and does NOT create a duplicate.
        // No password is stored locally — Auth0 owns the credential; PasswordHash
        // stays empty (CreateAsync skips hashing when the password is empty).
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

        // Step 3: best-effort post-creation steps. A failure here shouldn't roll back
        // the approval — the account exists and is usable; these are recoverable by
        // the admin (re-trigger reset) or a re-login (role claim). Log and continue.
        if (!await _auth0.AssignRoleAsync(auth0Id, "Client"))
            _logger.LogWarning("Approval: failed to assign Client role to {Auth0Id} ({Email}).", auth0Id, request.Email);

        if (!await _auth0.SendPasswordResetEmailAsync(request.Email))
            _logger.LogWarning("Approval: failed to send password-setup email to {Email}.", request.Email);

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
