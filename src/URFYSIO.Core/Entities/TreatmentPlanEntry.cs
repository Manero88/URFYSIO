namespace URFYSIO.Core.Entities;

public class TreatmentPlanEntry
{
    public Guid Id { get; set; }
    public Guid TreatmentPlanId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OrderIndex { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public TreatmentPlan TreatmentPlan { get; set; } = null!;
    public ICollection<TreatmentPlanEntryComment> Comments { get; set; } = [];
}
