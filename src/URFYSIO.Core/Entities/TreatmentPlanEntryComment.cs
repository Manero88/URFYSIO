namespace URFYSIO.Core.Entities;

/// <summary>
/// A note attached to a single treatment-plan entry. Both clients and physiotherapists
/// can post comments so each side has a running conversation per exercise — e.g. the
/// client logs "did this today, felt good" and the physio replies "increase reps next
/// week." Comments are immutable once written (no edit/delete endpoints yet); a fresh
/// comment is the way to update the thread.
///
/// Auth model: a client may only comment on entries that belong to plans for THEIR
/// own ClientProfile; a physio only on entries inside plans they own. Admins can
/// view any comment thread but the UI doesn't expose a posting flow for them.
/// </summary>
public class TreatmentPlanEntryComment
{
    public Guid Id { get; set; }
    public Guid TreatmentPlanEntryId { get; set; }
    public Guid UserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public TreatmentPlanEntry TreatmentPlanEntry { get; set; } = null!;
    public User User { get; set; } = null!;
}
