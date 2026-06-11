namespace URFYSIO.App.Services;

public interface IAuthService
{
    Task<bool> LoginAsync();
    Task<(bool Success, string? ErrorMessage)> LoginWithPasswordAsync(string email, string password);
    Task<bool> LoginWithGoogleAsync();
    Task<bool> LoginWithMicrosoftAsync();
    Task<bool> SignUpAsync();
    Task LogoutAsync();
    Task<string?> GetTokenAsync();
    Task<bool> IsLoggedInAsync();
    string? CurrentRole { get; }
    Guid? CurrentUserId { get; }
    string? CurrentUserName { get; }

    /// <summary>
    /// Synchronously re-hits <c>/api/users/me</c> and updates the cached role/name/id so that
    /// subsequent reads of <see cref="CurrentRole"/> reflect the current DB value (not a stale
    /// login-time snapshot). Call this from screens that need to display a role that may have
    /// changed since login — e.g. the Dashboard's role badge.
    /// Silently no-ops on failure so a flaky network doesn't log the user out.
    /// </summary>
    Task RefreshFromApiAsync();
}
