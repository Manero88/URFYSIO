using URFYSIO.Core.Enums;

namespace URFYSIO.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string? Auth0Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Street { get; set; }
    public string? HouseNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this account was last seen making an authenticated request. Maintained by
    /// <c>Auth0UserSyncMiddleware</c> and deliberately throttled (see
    /// <c>LastActiveThrottle</c> there) so it costs one write per quarter-hour of activity
    /// rather than one per request. That makes it a "last active" signal rather than a
    /// strict login timestamp. Null for accounts that haven't made a request since the
    /// column was added.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    // Navigation properties
    public ClientProfile? ClientProfile { get; set; }
    public PhysiotherapistProfile? PhysiotherapistProfile { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}
