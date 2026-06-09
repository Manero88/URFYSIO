using URFYSIO.Shared.Enums;

namespace URFYSIO.Shared.DTOs.Registration;

public class RegistrationRequestDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Message { get; set; }
    public RegistrationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateRegistrationRequestDto
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Message { get; set; }
}
