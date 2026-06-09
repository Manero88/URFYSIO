using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using URFYSIO.API.Mapping;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Seeding;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController : BaseApiController
{
    private readonly IUserService _userService;
    private readonly IUserDeletionService _userDeletionService;
    private readonly IAuth0ManagementService _auth0Management;
    private readonly AppDbContext _db;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserService userService,
        IUserDeletionService userDeletionService,
        IAuth0ManagementService auth0Management,
        AppDbContext db,
        ILogger<UsersController> logger)
    {
        _userService = userService;
        _userDeletionService = userDeletionService;
        _auth0Management = auth0Management;
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll([FromQuery] Shared.Enums.UserRole? role)
    {
        var coreRole = role.HasValue ? (Core.Enums.UserRole)(int)role.Value : (Core.Enums.UserRole?)null;
        var users = await _userService.GetAllAsync(coreRole);
        return Ok(users.Select(u => u.ToDto()));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var user = await _userService.GetByIdAsync(id);
        return user is null ? NotFound() : Ok(user.ToDto());
    }

    /// <summary>Get all active physiotherapists. Accessible to any authenticated user (for booking).</summary>
    [HttpGet("physiotherapists")]
    public async Task<IActionResult> GetPhysiotherapists()
    {
        // Eager-load PhysiotherapistProfile so UserDto.ProfileId resolves to the
        // PhysiotherapistProfile.Id. Without this Include, IUserService.GetAllAsync
        // projects without navigations, ProfileId comes back null, and the client's
        // booking screen can't request that physio's slots — so no slots appear.
        // (Mirrors how GetClients eager-loads ClientProfile.)
        var physios = await _db.Users
            .Include(u => u.PhysiotherapistProfile)
            .Where(u => u.Role == Core.Enums.UserRole.Physiotherapist && u.IsActive)
            .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
            .ToListAsync();
        return Ok(physios.Select(u => u.ToDto()));
    }

    /// <summary>
    /// Get all active clients with their ClientProfile populated (so UserDto.ProfileId
    /// resolves to ClientProfile.Id). Used by physiotherapists/admins to pick a client
    /// when creating a treatment plan.
    /// </summary>
    [HttpGet("clients")]
    [Authorize(Roles = "Admin,Physiotherapist")]
    public async Task<IActionResult> GetClients()
    {
        // Use the DbContext directly so the ClientProfile navigation is eagerly loaded
        // — IUserService.GetAllAsync projects without Includes.
        var clients = await _db.Users
            .Include(u => u.ClientProfile)
            .Where(u => u.Role == Core.Enums.UserRole.Client && u.IsActive)
            .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
            .ToListAsync();
        return Ok(clients.Select(u => u.ToDto()));
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        var user = await _userService.GetByIdAsync(userId.Value);
        return user is null ? NotFound() : Ok(user.ToDto());
    }

    [HttpGet("me/profile")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        var user = await _userService.GetByIdAsync(userId.Value);
        return user is null ? NotFound() : Ok(user.ToProfileDto());
    }

    [HttpPut("me/profile")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateUserProfileDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        var user = await _userService.GetByIdAsync(userId.Value);
        if (user is null) return NotFound();

        user.FirstName = dto.FirstName;
        user.LastName = dto.LastName;
        user.PhoneNumber = dto.PhoneNumber;
        user.DateOfBirth = dto.DateOfBirth;
        user.Gender = dto.Gender;
        user.Street = dto.Street;
        user.HouseNumber = dto.HouseNumber;
        user.PostalCode = dto.PostalCode;
        user.City = dto.City;

        await _userService.UpdateAsync(user);
        return Ok(user.ToProfileDto());
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto)
    {
        var user = await _userService.CreateAsync(dto.ToEntity(), dto.Password);
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, user.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserDto dto)
    {
        var user = await _userService.GetByIdAsync(id);
        if (user is null) return NotFound();

        // Capture the *pre-change* role so we can tell whether we need to sync Auth0.
        var oldRole = user.Role;

        user.FirstName = dto.FirstName;
        user.LastName = dto.LastName;
        user.PhoneNumber = dto.PhoneNumber;
        user.IsActive = dto.IsActive;

        if (dto.Role.HasValue)
            user.Role = (Core.Enums.UserRole)(int)dto.Role.Value;

        await _userService.UpdateAsync(user);

        // If the role changed, we also need to update Auth0. Otherwise the user's next
        // JWT will still carry the old role claim (https://urfysio.nl/roles) and the
        // API's [Authorize(Roles=...)] checks will 403 them from their new features.
        // The local DB is authoritative for our own service (middleware re-reads it on
        // each request via Auth0UserSyncMiddleware), so if Auth0 sync fails we log and
        // continue — the user will just need to wait for an admin to retry before
        // their token reflects the new role.
        if (user.Role != oldRole && !string.IsNullOrEmpty(user.Auth0Id))
        {
            var newRoleName = user.Role.ToString();
            var oldRoleName = oldRole.ToString();

            var removed = await _auth0Management.RemoveRoleAsync(user.Auth0Id, oldRoleName);
            var assigned = await _auth0Management.AssignRoleAsync(user.Auth0Id, newRoleName);
            if (!removed || !assigned)
            {
                _logger.LogWarning(
                    "Auth0 role sync for user {UserId} ({Auth0Id}) partially failed: " +
                    "remove '{Old}'={Removed}, assign '{New}'={Assigned}. The local DB " +
                    "was updated, but the user must be re-synced in Auth0 manually or by " +
                    "retrying the update.",
                    user.Id, user.Auth0Id, oldRoleName, removed, newRoleName, assigned);
            }
            else
            {
                _logger.LogInformation(
                    "Synced role change to Auth0 for user {UserId}: {Old} -> {New}. " +
                    "User must log out and back in for their JWT to reflect the change.",
                    user.Id, oldRoleName, newRoleName);
            }
        }

        return Ok(user.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await _userService.DeactivateAsync(id);
        return result ? NoContent() : NotFound();
    }

    /// <summary>
    /// GDPR right-to-erasure: permanently delete a user and ALL their data, including
    /// the Auth0 account. Irreversible — distinct from the soft Deactivate above.
    /// Admin only. An admin cannot delete their own account this way. Refusals
    /// (e.g. a physiotherapist with upcoming appointments) surface as a
    /// DomainException → ProblemDetails with a clear message.
    /// </summary>
    [HttpDelete("{id:guid}/permanent")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeletePermanently(Guid id)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == id)
            return BadRequest(new { message = "You cannot permanently delete your own account." });

        await _userDeletionService.DeleteUserPermanentlyAsync(id);
        return Ok(new { message = "User and all associated data have been permanently deleted." });
    }

    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] ChangePasswordDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (string.IsNullOrEmpty(dto.NewPassword) || dto.NewPassword.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        if (dto.NewPassword != dto.ConfirmPassword)
            return BadRequest(new { message = "Passwords do not match." });

        var user = await _userService.GetByIdAsync(userId.Value);
        if (user is null) return NotFound();

        if (string.IsNullOrEmpty(user.Auth0Id) || !user.Auth0Id.StartsWith("auth0|"))
            return BadRequest(new { message = "Password change is only available for email/password accounts." });

        var success = await _auth0Management.ChangePasswordAsync(user.Auth0Id, dto.NewPassword);
        return success
            ? Ok(new { message = "Password changed successfully." })
            : StatusCode(500, new { message = "Failed to change password. Please try again." });
    }

    /// <summary>
    /// Admin diagnostic: counts users by role and flags users whose role doesn't have a
    /// matching profile record (Client without ClientProfile, or Physiotherapist without
    /// PhysiotherapistProfile). These are the users that would fail availability / booking
    /// flows due to the stale-profile bug.
    /// </summary>
    [HttpGet("diagnostics")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetDiagnostics()
    {
        var totalUsers = await _db.Users.CountAsync();
        var byRole = await _db.Users
            .GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key.ToString(), Count = g.Count() })
            .ToListAsync();

        // Users missing the profile that matches their current role.
        var physioUsersMissingProfile = await _db.Users
            .Where(u => u.Role == Core.Enums.UserRole.Physiotherapist
                     && !_db.PhysiotherapistProfiles.Any(p => p.UserId == u.Id))
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, Role = u.Role.ToString() })
            .ToListAsync();

        var clientUsersMissingProfile = await _db.Users
            .Where(u => u.Role == Core.Enums.UserRole.Client
                     && !_db.ClientProfiles.Any(c => c.UserId == u.Id))
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, Role = u.Role.ToString() })
            .ToListAsync();

        // Users with mismatched profiles (e.g. promoted from Client → Physio: still have
        // a ClientProfile, which is fine for historical appointments but worth reporting).
        var mismatchedProfiles = await _db.Users
            .Where(u =>
                (u.Role == Core.Enums.UserRole.Physiotherapist
                    && _db.ClientProfiles.Any(c => c.UserId == u.Id))
                || (u.Role == Core.Enums.UserRole.Client
                    && _db.PhysiotherapistProfiles.Any(p => p.UserId == u.Id)))
            .Select(u => new
            {
                u.Id,
                u.Email,
                Role = u.Role.ToString(),
                HasClientProfile = _db.ClientProfiles.Any(c => c.UserId == u.Id),
                HasPhysioProfile = _db.PhysiotherapistProfiles.Any(p => p.UserId == u.Id)
            })
            .ToListAsync();

        return Ok(new
        {
            totalUsers,
            byRole,
            physioUsersMissingProfile,
            clientUsersMissingProfile,
            mismatchedProfiles
        });
    }

    /// <summary>
    /// Admin trigger: runs the idempotent profile-backfill pass (same logic that runs at
    /// API startup). Useful for verifying the fix or repairing a hot database without a
    /// restart. Returns the number of profiles added.
    /// </summary>
    [HttpPost("backfill-profiles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> BackfillProfiles()
    {
        var added = await DbSeeder.BackfillMissingProfilesAsync(_db);
        return Ok(new { added, message = $"{added} profile(s) created." });
    }

    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AdminResetPassword(Guid id)
    {
        var user = await _userService.GetByIdAsync(id);
        if (user is null) return NotFound();

        if (string.IsNullOrEmpty(user.Auth0Id) || !user.Auth0Id.StartsWith("auth0|"))
            return BadRequest(new { message = "Password reset is only available for email/password accounts." });

        // Guard: rows created before the email-backfill fix carry a "{auth0Id}@auth0.local"
        // placeholder. Auth0 silently returns 200 for unknown addresses, so without this
        // check the admin sees a fake "success" message and the user wonders where the
        // email is. Telling the admin to ask the user to log in once is the cleanest
        // recovery — the middleware will rewrite the email from the JWT on login.
        if (string.IsNullOrEmpty(user.Email)
            || user.Email.EndsWith("@auth0.local", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message = "This account's email address is not set yet. Ask the user to log in once so the system can pick up their real email, then try again."
            });
        }

        var success = await _auth0Management.SendPasswordResetEmailAsync(user.Email);
        return success
            ? Ok(new { message = $"Password reset email sent to {user.Email}." })
            : StatusCode(500, new { message = "Failed to send password reset email." });
    }

}
