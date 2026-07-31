using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

public class TreatmentPlanService : ITreatmentPlanService
{
    private readonly AppDbContext _db;

    public TreatmentPlanService(AppDbContext db) => _db = db;

    public async Task<TreatmentPlan?> GetByIdAsync(Guid id) =>
        await _db.TreatmentPlans
            .Include(t => t.ClientProfile).ThenInclude(c => c.User)
            .Include(t => t.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Include(t => t.Entries.OrderBy(e => e.OrderIndex))
            .FirstOrDefaultAsync(t => t.Id == id);

    public async Task<IReadOnlyList<TreatmentPlan>> GetByClientProfileIdAsync(Guid clientProfileId) =>
        await _db.TreatmentPlans
            .Include(t => t.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Include(t => t.Entries.OrderBy(e => e.OrderIndex))
            .Where(t => t.ClientProfileId == clientProfileId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<TreatmentPlan>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId) =>
        await _db.TreatmentPlans
            .Include(t => t.ClientProfile).ThenInclude(c => c.User)
            .Include(t => t.Entries.OrderBy(e => e.OrderIndex))
            .Where(t => t.PhysiotherapistProfileId == physioProfileId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

    public async Task<TreatmentPlan> CreateAsync(TreatmentPlan plan)
    {
        plan.Id = Guid.NewGuid();
        plan.CreatedAt = DateTime.UtcNow;

        _db.TreatmentPlans.Add(plan);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(plan.Id) ?? plan;
    }

    public async Task<TreatmentPlan> UpdateAsync(TreatmentPlan plan)
    {
        plan.UpdatedAt = DateTime.UtcNow;
        _db.TreatmentPlans.Update(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var plan = await _db.TreatmentPlans.FindAsync(id);
        if (plan is null) return false;
        _db.TreatmentPlans.Remove(plan);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<TreatmentPlan?> CompleteAsync(Guid id)
    {
        var plan = await _db.TreatmentPlans.FindAsync(id);
        if (plan is null) return null;

        // Idempotent — calling complete twice is harmless. We DON'T overwrite
        // CompletedAt on a second call so the original completion timestamp is
        // preserved (which is what the History tab displays).
        if (!plan.IsCompleted)
        {
            plan.IsCompleted = true;
            plan.CompletedAt = DateTime.UtcNow;
            plan.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return await GetByIdAsync(plan.Id) ?? plan;
    }

    public async Task<TreatmentPlan?> ReopenAsync(Guid id)
    {
        var plan = await _db.TreatmentPlans.FindAsync(id);
        if (plan is null) return null;

        if (plan.IsCompleted)
        {
            plan.IsCompleted = false;
            plan.CompletedAt = null;
            plan.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return await GetByIdAsync(plan.Id) ?? plan;
    }

    public async Task<TreatmentPlanEntry> AddEntryAsync(Guid planId, TreatmentPlanEntry entry)
    {
        var plan = await _db.TreatmentPlans.FindAsync(planId)
            ?? throw DomainException.NotFound("Treatment plan not found.");

        entry.Id = Guid.NewGuid();
        entry.TreatmentPlanId = planId;
        entry.CreatedAt = DateTime.UtcNow;

        _db.TreatmentPlanEntries.Add(entry);
        plan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return entry;
    }

    public async Task<TreatmentPlanEntry?> GetEntryByIdAsync(Guid entryId) =>
        await _db.TreatmentPlanEntries
            .Include(e => e.TreatmentPlan)
            .FirstOrDefaultAsync(e => e.Id == entryId);

    public async Task<TreatmentPlanEntry?> UpdateEntryAsync(TreatmentPlanEntry entry)
    {
        var existing = await _db.TreatmentPlanEntries.FindAsync(entry.Id);
        if (existing is null) return null;

        existing.Title = entry.Title;
        existing.Description = entry.Description;
        existing.OrderIndex = entry.OrderIndex;
        existing.IsCompleted = entry.IsCompleted;

        var plan = await _db.TreatmentPlans.FindAsync(existing.TreatmentPlanId);
        if (plan is not null) plan.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteEntryAsync(Guid entryId)
    {
        var entry = await _db.TreatmentPlanEntries.FindAsync(entryId);
        if (entry is null) return false;

        var plan = await _db.TreatmentPlans.FindAsync(entry.TreatmentPlanId);
        if (plan is not null) plan.UpdatedAt = DateTime.UtcNow;

        _db.TreatmentPlanEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Comments ---

    public async Task<IReadOnlyList<TreatmentPlanEntryComment>> GetCommentsForEntryAsync(Guid entryId) =>
        await _db.TreatmentPlanEntryComments
            .Include(c => c.User)
            .Where(c => c.TreatmentPlanEntryId == entryId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

    public async Task<TreatmentPlanEntryComment> AddCommentAsync(
        Guid entryId, Guid userId, string text, string? photoBlobName = null)
    {
        // Verify the entry exists — without this we'd accept comments referencing
        // deleted entries and the FK constraint would surface as a generic 500 instead
        // of a clean 404.
        var entry = await _db.TreatmentPlanEntries.FindAsync(entryId)
            ?? throw DomainException.NotFound("Treatment plan entry not found.");

        // A photo on its own is a valid contribution ("here's how it looks today"), so
        // text is only mandatory when there's no photo to carry the comment.
        var hasPhoto = !string.IsNullOrWhiteSpace(photoBlobName);
        if (string.IsNullOrWhiteSpace(text) && !hasPhoto)
            throw DomainException.Validation("Comment text is required.");
        if (text is { Length: > 1000 })
            throw DomainException.Validation("Comment text cannot exceed 1000 characters.");

        var comment = new TreatmentPlanEntryComment
        {
            Id = Guid.NewGuid(),
            TreatmentPlanEntryId = entryId,
            UserId = userId,
            Text = text?.Trim() ?? string.Empty,
            PhotoBlobName = hasPhoto ? photoBlobName : null,
            CreatedAt = DateTime.UtcNow
        };
        _db.TreatmentPlanEntryComments.Add(comment);
        await _db.SaveChangesAsync();

        // Re-fetch with the User navigation populated so the controller's mapper has
        // AuthorName/AuthorRole available without an extra round-trip.
        return await _db.TreatmentPlanEntryComments
            .Include(c => c.User)
            .FirstAsync(c => c.Id == comment.Id);
    }

    public async Task<IReadOnlyList<string>> GetCommentPhotoBlobNamesForEntriesAsync(IEnumerable<Guid> entryIds)
    {
        var ids = entryIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        return await _db.TreatmentPlanEntryComments
            .Where(c => ids.Contains(c.TreatmentPlanEntryId) && c.PhotoBlobName != null)
            .Select(c => c.PhotoBlobName!)
            .ToListAsync();
    }
}
