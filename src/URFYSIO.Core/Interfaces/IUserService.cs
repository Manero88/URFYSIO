using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;

namespace URFYSIO.Core.Interfaces;

public interface IUserService
{
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByAuth0IdAsync(string auth0Id);
    Task<User?> GetByEmailAsync(string email);
    Task<IReadOnlyList<User>> GetAllAsync(UserRole? roleFilter = null);
    Task<User> CreateAsync(User user, string password);
    Task<User> UpdateAsync(User user);
    Task<bool> DeactivateAsync(Guid id);

    /// <summary>
    /// Sets <c>IsActive = true</c>, which is how an admin approves a self-service SSO
    /// sign-up: <c>Auth0UserSyncMiddleware</c> auto-creates those users as inactive, and
    /// the login screen bounces them with "pending admin approval" until this flips.
    /// Also ensures the role-matching profile exists, so an account approved this way can
    /// immediately book or be booked without tripping the missing-profile path.
    /// Returns false if the user doesn't exist.
    /// </summary>
    Task<bool> ActivateAsync(Guid id);
    Task<User?> ValidateCredentialsAsync(string email, string password);

    /// <summary>
    /// Ensures the user has the profile entity matching their current role.
    /// For <see cref="UserRole.Client"/> a <c>ClientProfile</c> is created if missing;
    /// for <see cref="UserRole.Physiotherapist"/> a <c>PhysiotherapistProfile</c> is created if missing.
    /// No-op for <see cref="UserRole.Admin"/>. Persists changes if anything was added.
    /// Returns true when a profile was created, false otherwise.
    /// </summary>
    Task<bool> EnsureProfileForRoleAsync(Guid userId);
}
