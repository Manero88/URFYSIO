namespace URFYSIO.Core.Entities;

public class AvailabilitySlot
{
    public Guid Id { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsBooked { get; set; }

    // Navigation properties
    public PhysiotherapistProfile PhysiotherapistProfile { get; set; } = null!;
    public Appointment? Appointment { get; set; }
}
