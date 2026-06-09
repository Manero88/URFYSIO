using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

public class AvailabilityService : IAvailabilityService
{
    private readonly AppDbContext _db;

    public AvailabilityService(AppDbContext db) => _db = db;

    public async Task<AvailabilitySlot?> GetByIdAsync(Guid id) =>
        await _db.AvailabilitySlots
            .Include(s => s.PhysiotherapistProfile).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<AvailabilitySlot>> GetByPhysiotherapistProfileIdAsync(
        Guid physioProfileId, DateTime? from = null, DateTime? to = null)
    {
        var now = DateTime.UtcNow;
        var query = _db.AvailabilitySlots
            .Include(s => s.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Where(s => s.PhysiotherapistProfileId == physioProfileId)
            // Auto-hide expired, never-booked slots: an unbooked slot whose window has
            // already passed is just clutter — it was offered, nobody took it. We keep
            // BOOKED past slots because they're appointment history the physio still
            // needs to see (the UI files them under a "Past" section).
            .Where(s => s.EndTime > now || s.IsBooked);

        if (from.HasValue) query = query.Where(s => s.StartTime >= from.Value);
        if (to.HasValue) query = query.Where(s => s.EndTime <= to.Value);

        return await query.OrderBy(s => s.StartTime).ToListAsync();
    }

    public async Task<IReadOnlyList<AvailabilitySlot>> GetAvailableSlotsAsync(
        Guid? physioProfileId = null, DateTime? from = null, DateTime? to = null)
    {
        var query = _db.AvailabilitySlots
            .Include(s => s.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Where(s => !s.IsBooked && s.StartTime > DateTime.UtcNow);

        if (physioProfileId.HasValue)
            query = query.Where(s => s.PhysiotherapistProfileId == physioProfileId.Value);
        if (from.HasValue) query = query.Where(s => s.StartTime >= from.Value);
        if (to.HasValue) query = query.Where(s => s.EndTime <= to.Value);

        return await query.OrderBy(s => s.StartTime).ToListAsync();
    }

    public async Task<AvailabilitySlot> CreateAsync(AvailabilitySlot slot)
    {
        if (slot.StartTime >= slot.EndTime)
            throw DomainException.Conflict("Start time must be before end time.");

        if (slot.StartTime < DateTime.UtcNow)
            throw DomainException.Conflict("Cannot create slots in the past.");

        // Check for overlapping slots for the same physio
        var hasConflict = await _db.AvailabilitySlots.AnyAsync(s =>
            s.PhysiotherapistProfileId == slot.PhysiotherapistProfileId &&
            s.StartTime < slot.EndTime &&
            s.EndTime > slot.StartTime);

        if (hasConflict)
            throw DomainException.Conflict("This time slot overlaps with an existing availability slot.");

        slot.Id = Guid.NewGuid();
        slot.IsBooked = false;

        _db.AvailabilitySlots.Add(slot);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(slot.Id) ?? slot;
    }

    public async Task<AvailabilitySlot> UpdateAsync(AvailabilitySlot slot)
    {
        if (slot.IsBooked)
            throw DomainException.Conflict("Cannot modify a booked availability slot.");

        if (slot.StartTime >= slot.EndTime)
            throw DomainException.Conflict("Start time must be before end time.");

        _db.AvailabilitySlots.Update(slot);
        await _db.SaveChangesAsync();
        return slot;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var slot = await _db.AvailabilitySlots.FindAsync(id);
        if (slot is null) return false;
        if (slot.IsBooked)
            throw DomainException.Conflict("Cannot delete a booked availability slot.");

        _db.AvailabilitySlots.Remove(slot);
        await _db.SaveChangesAsync();
        return true;
    }
}
