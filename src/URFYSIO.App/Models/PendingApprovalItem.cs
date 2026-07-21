using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.Models;

/// <summary>
/// One row in the admin's unified "Pending approval" list.
///
/// Two different things can be waiting for an admin, and before this type they lived in
/// separate places (only the first was ever shown):
///   • A <see cref="RegistrationRequestDto"/> — somebody filled in the registration form.
///     Approving it creates the Auth0 account and sends a password-setup email.
///   • An inactive <see cref="UserDto"/> — somebody signed in with Google/Microsoft and
///     <c>Auth0UserSyncMiddleware</c> auto-created them with <c>IsActive = false</c>.
///     These never appeared anywhere, so SSO users were stuck on "pending admin approval"
///     with no way for an admin to find, let alone approve, them. Approving one is just
///     flipping IsActive — the Auth0 account already exists.
///
/// This wrapper lets a single CollectionView bind to both, with <see cref="IsRegistration"/>
/// telling the view model which underlying action to dispatch.
/// </summary>
public sealed class PendingApprovalItem
{
    public RegistrationRequestDto? Registration { get; private init; }
    public UserDto? User { get; private init; }

    public bool IsRegistration => Registration is not null;

    public string DisplayName { get; private init; } = string.Empty;
    public string Email { get; private init; } = string.Empty;

    /// <summary>How this person signed up, e.g. "Registration form" or "Google".</summary>
    public string SourceLabel { get; private init; } = string.Empty;

    /// <summary>Free-text message from the registration form; null for SSO sign-ups.</summary>
    public string? Note { get; private init; }

    public DateTime RequestedAt { get; private init; }

    /// <summary>
    /// "Approve" creates an account from a form submission; "Activate" only unlocks an
    /// account that already exists. Different enough that the button should say which.
    /// </summary>
    public string ApproveLabel => IsRegistration ? "Approve" : "Activate";

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public static PendingApprovalItem FromRegistration(RegistrationRequestDto dto) => new()
    {
        Registration = dto,
        DisplayName = $"{dto.FirstName} {dto.LastName}".Trim(),
        Email = dto.Email,
        SourceLabel = "Registration form",
        Note = dto.Message,
        RequestedAt = dto.CreatedAt
    };

    public static PendingApprovalItem FromUser(UserDto dto) => new()
    {
        User = dto,
        // FullName is already trimmed; fall back to the email when Auth0 gave us nothing
        // usable, so a row never renders as a blank line the admin can't identify.
        DisplayName = string.IsNullOrWhiteSpace(dto.FullName) ? dto.Email : dto.FullName,
        Email = dto.Email,
        SourceLabel = dto.AuthProviderLabel,
        RequestedAt = dto.CreatedAt
    };
}
