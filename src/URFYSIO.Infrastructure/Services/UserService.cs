using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _db;

    public UserService(AppDbContext db) => _db = db;

    public async Task<User?> GetByIdAsync(Guid id) =>
        await _db.Users
            .Include(u => u.ClientProfile)
            .Include(u => u.PhysiotherapistProfile)
            .FirstOrDefaultAsync(u => u.Id == id);

    public async Task<User?> GetByAuth0IdAsync(string auth0Id) =>
        await _db.Users
            .Include(u => u.ClientProfile)
            .Include(u => u.PhysiotherapistProfile)
            .FirstOrDefaultAsync(u => u.Auth0Id == auth0Id);

    public async Task<User?> GetByEmailAsync(string email) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

    public async Task<IReadOnlyList<User>> GetAllAsync(UserRole? roleFilter = null)
    {
        var query = _db.Users.AsQueryable();
        if (roleFilter.HasValue)
            query = query.Where(u => u.Role == roleFilter.Value);
        return await query.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ToListAsync();
    }

    public async Task<User> CreateAsync(User user, string password)
    {
        if (await _db.Users.AnyAsync(u => u.Email == user.Email))
            throw DomainException.Conflict($"A user with email '{user.Email}' already exists.");

        user.Id = Guid.NewGuid();
        user.PasswordHash = string.IsNullOrEmpty(password) ? string.Empty : HashPassword(password);
        user.CreatedAt = DateTime.UtcNow;

        _db.Users.Add(user);

        // Auto-create profile based on role
        if (user.Role == UserRole.Client)
        {
            _db.ClientProfiles.Add(new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id });
        }
        else if (user.Role == UserRole.Physiotherapist)
        {
            _db.PhysiotherapistProfiles.Add(new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = user.Id });
        }

        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User> UpdateAsync(User user)
    {
        _db.Users.Update(user);
        // If an admin just flipped the role (e.g. Client -> Physiotherapist), the user
        // may not yet have the profile entity that matches their new role. Add it now
        // so downstream flows (availability slots, treatment plans, appointments) have
        // a valid FK target.
        AddMissingProfileForRole(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<bool> EnsureProfileForRoleAsync(Guid userId)
    {
        var user = await _db.Users
            .Include(u => u.ClientProfile)
            .Include(u => u.PhysiotherapistProfile)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return false;

        var added = false;
        if (user.Role == UserRole.Client && user.ClientProfile is null)
        {
            var profile = new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
            _db.ClientProfiles.Add(profile);
            // Explicitly wire the navigation property so callers that reuse this same
            // tracked User (e.g. later GetByIdAsync within the same request) will see
            // the new profile without EF Core re-querying. The identity map otherwise
            // keeps the cached User.ClientProfile == null.
            user.ClientProfile = profile;
            added = true;
        }
        else if (user.Role == UserRole.Physiotherapist && user.PhysiotherapistProfile is null)
        {
            var profile = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
            _db.PhysiotherapistProfiles.Add(profile);
            user.PhysiotherapistProfile = profile;
            added = true;
        }

        if (added) await _db.SaveChangesAsync();
        return added;
    }

    // Synchronous helper used inside UpdateAsync (which already owns a SaveChanges call).
    // Performs the "add missing profile" logic against the change tracker without an
    // extra database round-trip when we already know whether the profile exists.
    // Accepts the tracked User so we can set the navigation property directly — this
    // avoids EF Core's identity-map quirk where a later re-query returns the cached
    // User instance with stale (null) navigation properties.
    private void AddMissingProfileForRole(User user)
    {
        if (user.Role == UserRole.Client)
        {
            var existing = _db.ClientProfiles.Local.FirstOrDefault(c => c.UserId == user.Id)
                        ?? _db.ClientProfiles.FirstOrDefault(c => c.UserId == user.Id);
            if (existing is null)
            {
                var profile = new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
                _db.ClientProfiles.Add(profile);
                user.ClientProfile = profile;
            }
            else
            {
                user.ClientProfile = existing;
            }
        }
        else if (user.Role == UserRole.Physiotherapist)
        {
            var existing = _db.PhysiotherapistProfiles.Local.FirstOrDefault(p => p.UserId == user.Id)
                        ?? _db.PhysiotherapistProfiles.FirstOrDefault(p => p.UserId == user.Id);
            if (existing is null)
            {
                var profile = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
                _db.PhysiotherapistProfiles.Add(profile);
                user.PhysiotherapistProfile = profile;
            }
            else
            {
                user.PhysiotherapistProfile = existing;
            }
        }
    }

    public async Task<bool> DeactivateAsync(Guid id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return false;
        user.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ActivateAsync(Guid id)
    {
        var user = await _db.Users
            .Include(u => u.ClientProfile)
            .Include(u => u.PhysiotherapistProfile)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user is null) return false;

        user.IsActive = true;
        // SSO users are auto-created through the sync middleware; make sure the profile
        // matching their role exists before they can act, so approval alone is enough to
        // make the account fully usable.
        AddMissingProfileForRole(user);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<User?> ValidateCredentialsAsync(string email, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        if (user is null) return null;
        return VerifyPassword(password, user.PasswordHash) ? user : null;
    }

    // --- Password hashing using PBKDF2 ---
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expectedHash = Convert.FromBase64String(parts[1]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
