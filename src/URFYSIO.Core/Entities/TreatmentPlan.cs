namespace URFYSIO.Core.Entities;

public class TreatmentPlan
{
    public Guid Id { get; set; }
    public Guid ClientProfileId { get; set; }
    public Guid PhysiotherapistProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Plan lifecycle. A plan starts active (IsCompleted=false, CompletedAt=null) and
    // can be marked complete by the owning physiotherapist when the course of treatment
    // is done — completion moves it to the History tab in the UI. The pair is always
    // kept in sync: setting IsCompleted=true sets CompletedAt; reopening clears both.
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Navigation properties
    public ClientProfile ClientProfile { get; set; } = null!;
    public PhysiotherapistProfile PhysiotherapistProfile { get; set; } = null!;
    public ICollection<TreatmentPlanEntry> Entries { get; set; } = [];
}
