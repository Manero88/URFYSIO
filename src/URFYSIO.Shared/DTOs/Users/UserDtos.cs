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
    public string FullName => $"{FirstName} {LastName}";
    public bool IsEmailPasswordUser => string.Equals(AuthProvider, "email", StringComparison.OrdinalIgnoreCase);
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
