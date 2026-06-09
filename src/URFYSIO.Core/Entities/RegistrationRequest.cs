using URFYSIO.Core.Enums;

namespace URFYSIO.Core.Entities;

public class RegistrationRequest
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Message { get; set; }
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ProcessedByUserId { get; set; }

    // Navigation property
    public User? ProcessedByUser { get; set; }
}
