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

    /// <summary>
    /// Name of the attached photo's blob in the private "treatment-photos" container,
    /// or null when the comment has no photo. Deliberately NOT a URL: the container is
    /// private, so a viewable link is a short-lived SAS minted per read
    /// (<see cref="Interfaces.IBlobStorageService.GetPhotoSasUrlAsync"/>). Storing a URL
    /// would either be permanently valid or expire in the database.
    /// </summary>
    public string? PhotoBlobName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public TreatmentPlanEntry TreatmentPlanEntry { get; set; } = null!;
    public User User { get; set; } = null!;
}
