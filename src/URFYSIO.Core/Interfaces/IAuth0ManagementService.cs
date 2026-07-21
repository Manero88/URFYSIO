namespace URFYSIO.Core.Interfaces;

/// <summary>
/// Outcome of creating an Auth0 user. We use a small result type instead of a bare
/// <c>string?</c> so the caller (registration approval) can distinguish the three
/// cases it must handle differently:
///   • Success  — UserId is set, proceed with local user creation.
///   • Conflict — the email already exists in Auth0 (AlreadyExists=true). The admin
///                needs a different message ("already has an account") than a generic
///                failure.
///   • Failure  — anything else (scope missing, network, 500). Abort the approval.
/// </summary>
public sealed record Auth0UserResult(string? UserId, bool AlreadyExists, string? Error)
{
    public bool Success => UserId is not null;
    public static Auth0UserResult Created(string userId) => new(userId, false, null);
    public static Auth0UserResult Conflict(string error) => new(null, true, error);
    public static Auth0UserResult Failed(string error) => new(null, false, error);
}

/// <summary>
/// The subset of an Auth0 user profile the sync middleware needs. Social connections
/// (Google, Microsoft) put the user's real name here but NOT in the API access token,
/// so this lookup is the only way to learn it — without it every SSO sign-up lands in
/// the database as "Unknown User".
///
/// Any field may be null: Auth0 omits <c>given_name</c>/<c>family_name</c> for some
/// connections and only supplies the combined <c>name</c>, which is why
/// <see cref="Auth0UserProfile.SplitName"/> exists.
/// </summary>
public sealed record Auth0UserProfile(
    string? Email,
    string? GivenName,
    string? FamilyName,
    string? Name,
    string? Nickname)
{
    /// <summary>
    /// Resolves a (first, last) pair from whatever Auth0 supplied, in preference order:
    /// explicit given/family names → split the combined <c>name</c> on the last space →
    /// nickname as a first name. Returns nulls when nothing usable is present so callers
    /// can keep an existing value rather than overwrite it with a placeholder.
    /// </summary>
    public (string? First, string? Last) SplitName()
    {
        var given = Blank(GivenName) ? null : GivenName!.Trim();
        var family = Blank(FamilyName) ? null : FamilyName!.Trim();
        if (given is not null || family is not null)
            return (given, family);

        var full = Blank(Name) ? null : Name!.Trim();
        if (full is not null)
        {
            // Split on the LAST space: "Anna Maria de Vries" → ("Anna Maria de", "Vries").
            var idx = full.LastIndexOf(' ');
            if (idx > 0)
                return (full[..idx].Trim(), full[(idx + 1)..].Trim());
            return (full, null);
        }

        // Auth0 falls back to the email local-part as the nickname for some connections.
        // It's a poor last name but a reasonable first name, and far better than "Unknown".
        return Blank(Nickname) ? (null, null) : (Nickname!.Trim(), null);
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}

public interface IAuth0ManagementService
{
    Task<bool> ChangePasswordAsync(string auth0UserId, string newPassword);
    Task<bool> SendPasswordResetEmailAsync(string email);

    /// <summary>
    /// Creates a new user in the Auth0 database connection with a random strong
    /// password (the user never learns it — they set their own via the password-reset
    /// email triggered after creation). Returns the new Auth0 user_id on success,
    /// or a result flagged AlreadyExists / Error otherwise. Requires the M2M app to
    /// have the <c>create:users</c> scope.
    /// </summary>
    Task<Auth0UserResult> CreateUserAsync(string email, string firstName, string lastName);

    /// <summary>
    /// Permanently deletes the user from Auth0 (GDPR erasure). Returns true on success
    /// (2xx, including the idempotent case where the user was already gone), false on
    /// any failure. Requires the M2M app to have the <c>delete:users</c> scope.
    /// </summary>
    Task<bool> DeleteUserAsync(string auth0UserId);

    /// <summary>
    /// Looks up an existing Auth0 user by email and returns their user_id, or null if
    /// none exists / the lookup fails. When the same email has multiple identities
    /// (e.g. a Google SSO login AND a database account), the database ("auth0|")
    /// identity is preferred because password-reset emails only work for that
    /// connection. Used by registration approval to LINK an already-existing Auth0
    /// account (e.g. someone who previously signed in with Google) instead of failing
    /// with a duplicate-email conflict.
    /// </summary>
    Task<string?> GetUserIdByEmailAsync(string email);

    /// <summary>
    /// Looks up a user by Auth0 ID and returns the email Auth0 has on file. Returns
    /// <c>null</c> if the user doesn't exist or the Management API call fails.
    /// Used by the user-sync middleware to backfill the local <c>User.Email</c>
    /// when the bearer access token doesn't carry an <c>email</c> claim — Auth0's
    /// default access token only contains <c>sub</c>, so without this we'd never
    /// learn what the user actually signed up with.
    /// </summary>
    Task<string?> GetUserEmailAsync(string auth0UserId);

    /// <summary>
    /// Fetches email AND name fields for an Auth0 user in a single Management API call.
    /// Returns <c>null</c> if the user doesn't exist or the call fails. Used by the sync
    /// middleware so social sign-ups (Google/Microsoft) get their real name captured —
    /// access tokens carry neither <c>email</c> nor <c>given_name</c>, so without this a
    /// first-time SSO user is stored as "Unknown User".
    /// Requires the M2M app to have the <c>read:users</c> scope.
    /// </summary>
    Task<Auth0UserProfile?> GetUserProfileAsync(string auth0UserId);

    /// <summary>
    /// Assigns the named Auth0 role (e.g. "Admin", "Physiotherapist", "Client") to the user.
    /// Role IDs are looked up by name via the Management API and cached. Returns true on success.
    /// </summary>
    Task<bool> AssignRoleAsync(string auth0UserId, string roleName);

    /// <summary>
    /// Removes the named Auth0 role from the user. Returns true on success (including the
    /// no-op case where the user didn't have that role to begin with).
    /// </summary>
    Task<bool> RemoveRoleAsync(string auth0UserId, string roleName);
}
