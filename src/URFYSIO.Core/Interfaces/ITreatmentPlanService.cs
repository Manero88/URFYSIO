using URFYSIO.Core.Entities;

namespace URFYSIO.Core.Interfaces;

public interface ITreatmentPlanService
{
    Task<TreatmentPlan?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<TreatmentPlan>> GetByClientProfileIdAsync(Guid clientProfileId);
    Task<IReadOnlyList<TreatmentPlan>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId);
    Task<TreatmentPlan> CreateAsync(TreatmentPlan plan);
    Task<TreatmentPlan> UpdateAsync(TreatmentPlan plan);
    Task<bool> DeleteAsync(Guid id);

    /// <summary>
    /// Marks the plan complete: <c>IsCompleted=true</c>, <c>CompletedAt=now</c>.
    /// No-op (returns the plan unchanged) if it's already completed. Returns null
    /// if the plan doesn't exist.
    /// </summary>
    Task<TreatmentPlan?> CompleteAsync(Guid id);

    /// <summary>
    /// Reverses <see cref="CompleteAsync"/>: clears both flags so the plan returns
    /// to the Active list. Useful when a physio marked a plan complete by mistake.
    /// </summary>
    Task<TreatmentPlan?> ReopenAsync(Guid id);

    Task<TreatmentPlanEntry> AddEntryAsync(Guid planId, TreatmentPlanEntry entry);
    Task<TreatmentPlanEntry?> GetEntryByIdAsync(Guid entryId);
    Task<TreatmentPlanEntry?> UpdateEntryAsync(TreatmentPlanEntry entry);
    Task<bool> DeleteEntryAsync(Guid entryId);

    // --- Comments ---
    Task<IReadOnlyList<TreatmentPlanEntryComment>> GetCommentsForEntryAsync(Guid entryId);
    /// <summary>
    /// Adds a comment, optionally with an already-uploaded photo. The caller uploads to
    /// blob storage first and passes the resulting blob name — this method only persists
    /// the reference, so the service layer stays free of storage concerns.
    /// </summary>
    Task<TreatmentPlanEntryComment> AddCommentAsync(
        Guid entryId, Guid userId, string text, string? photoBlobName = null);

    /// <summary>
    /// Blob names for every photo attached to comments on the given entries. Used before
    /// deleting entries/plans so the corresponding blobs can be cleaned up rather than
    /// orphaned in storage.
    /// </summary>
    Task<IReadOnlyList<string>> GetCommentPhotoBlobNamesForEntriesAsync(IEnumerable<Guid> entryIds);
}
