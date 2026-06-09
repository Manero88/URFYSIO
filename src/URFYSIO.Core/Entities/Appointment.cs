using URFYSIO.Core.Enums;

namespace URFYSIO.Core.Entities;

public class Appointment
{
    public Guid Id { get; set; }
    public Guid ClientProfileId { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public Guid? AvailabilitySlotId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ClientProfile ClientProfile { get; set; } = null!;
    public PhysiotherapistProfile PhysiotherapistProfile { get; set; } = null!;
    public AvailabilitySlot? AvailabilitySlot { get; set; }
}
