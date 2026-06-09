using URFYSIO.Shared.Enums;

namespace URFYSIO.Shared.DTOs.Appointments;

public class AppointmentDto
{
    public Guid Id { get; set; }
    public Guid ClientProfileId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public Guid PhysiotherapistProfileId { get; set; }
    public string PhysiotherapistName { get; set; } = string.Empty;
    public Guid? AvailabilitySlotId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateAppointmentDto
{
    public Guid ClientProfileId { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public Guid AvailabilitySlotId { get; set; }
    public string? Notes { get; set; }
}

public class UpdateAppointmentDto
{
    public string? Notes { get; set; }
    public AppointmentStatus? Status { get; set; }
}

public class RescheduleAppointmentDto
{
    public Guid NewAvailabilitySlotId { get; set; }
}
