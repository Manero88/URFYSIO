namespace URFYSIO.Shared.DTOs.Availability;

public class AvailabilitySlotDto
{
    public Guid Id { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public string PhysiotherapistName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool IsBooked { get; set; }
}

public class CreateAvailabilitySlotDto
{
    public Guid PhysiotherapistProfileId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public class UpdateAvailabilitySlotDto
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}
