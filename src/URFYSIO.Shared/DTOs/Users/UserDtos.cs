using URFYSIO.Shared.Enums;

namespace URFYSIO.Shared.DTOs.Users;

public class UserDto
{
    public Guid Id { get; set; }
    public Guid? ProfileId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string AuthProvider { get; set; } = string.Empty;

    /// <summary>
    /// Trimmed so a user with only one name part doesn't yield a stray leading/trailing
    /// space. This is load-bearing for the admin's type-to-confirm delete: it compares the
    /// typed text against FullName, and an untrimmed "Unknown " could never be matched by
    /// anything the admin typed — making such accounts undeletable.
    /// </summary>
    public string FullName => $"{FirstName} {LastName}".Trim();
    public bool IsEmailPasswordUser => string.Equals(AuthProvider, "email", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable sign-up source, e.g. "Google", for the admin's pending list.</summary>
    public string AuthProviderLabel => AuthProvider?.ToLowerInvariant() switch
    {
        "email" => "Email / password",
        "google" => "Google",
        "microsoft" => "Microsoft",
        "local" => "Created by admin",
        _ => "External provider"
    };
}

public class CreateUserDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public UserRole Role { get; set; }
}

public class UpdateUserDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public UserRole? Role { get; set; }
    public bool IsActive { get; set; }
}

public class ChangePasswordDto
{
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
