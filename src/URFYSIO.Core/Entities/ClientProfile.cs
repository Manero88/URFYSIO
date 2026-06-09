namespace URFYSIO.Core.Entities;

public class ClientProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Address { get; set; }
    public string? MedicalNotes { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public ICollection<Appointment> Appointments { get; set; } = [];
    public ICollection<TreatmentPlan> TreatmentPlans { get; set; } = [];
}
