namespace URFYSIO.Core.Entities;

public class PhysiotherapistProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Specialization { get; set; }
    public string? LicenseNumber { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public ICollection<AvailabilitySlot> AvailabilitySlots { get; set; } = [];
    public ICollection<Appointment> Appointments { get; set; } = [];
    public ICollection<TreatmentPlan> TreatmentPlans { get; set; } = [];
}
