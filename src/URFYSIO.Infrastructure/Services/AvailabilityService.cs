using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Common;
using URFYSIO.Core.Entities;
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

    // Create/Update return ServiceResult instead of throwing DomainException for
    // validation failures. An overlapping slot is a perfectly normal user mistake —
    // throwing for it made Visual Studio break into the debugger on every routine
    // attempt, even though the API handled it correctly. Exceptions stay reserved
    // for genuinely unexpected situations.
    public async Task<ServiceResult<AvailabilitySlot>> CreateAsync(AvailabilitySlot slot)
    {
        if (slot.StartTime >= slot.EndTime)
            return ServiceResult<AvailabilitySlot>.Fail("Start time must be before end time.");

        if (slot.StartTime < DateTime.UtcNow)
            return ServiceResult<AvailabilitySlot>.Fail("Cannot create slots in the past.");

        // Check for overlapping slots for the same physio
        var hasConflict = await _db.AvailabilitySlots.AnyAsync(s =>
            s.PhysiotherapistProfileId == slot.PhysiotherapistProfileId &&
            s.StartTime < slot.EndTime &&
            s.EndTime > slot.StartTime);

        if (hasConflict)
            return ServiceResult<AvailabilitySlot>.Fail("This time slot overlaps with an existing availability slot.");

        slot.Id = Guid.NewGuid();
        slot.IsBooked = false;

        _db.AvailabilitySlots.Add(slot);
        await _db.SaveChangesAsync();

        return ServiceResult<AvailabilitySlot>.Ok(await GetByIdAsync(slot.Id) ?? slot);
    }

    public async Task<ServiceResult<AvailabilitySlot>> UpdateAsync(AvailabilitySlot slot)
    {
        if (slot.IsBooked)
            return ServiceResult<AvailabilitySlot>.Fail("Cannot modify a booked availability slot.");

        if (slot.StartTime >= slot.EndTime)
            return ServiceResult<AvailabilitySlot>.Fail("Start time must be before end time.");

        // Overlap check for the new time window, excluding the slot being edited.
        var hasConflict = await _db.AvailabilitySlots.AnyAsync(s =>
            s.Id != slot.Id &&
            s.PhysiotherapistProfileId == slot.PhysiotherapistProfileId &&
            s.StartTime < slot.EndTime &&
            s.EndTime > slot.StartTime);

        if (hasConflict)
            return ServiceResult<AvailabilitySlot>.Fail("This time slot overlaps with an existing availability slot.");

        _db.AvailabilitySlots.Update(slot);
        await _db.SaveChangesAsync();
        return ServiceResult<AvailabilitySlot>.Ok(slot);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var slot = await _db.AvailabilitySlots.FindAsync(id);
        // Booked slots are refused at the controller (clear 400 with message) before
        // we get here; the IsBooked re-check below is a defensive backstop only.
        if (slot is null || slot.IsBooked) return false;

        _db.AvailabilitySlots.Remove(slot);
        await _db.SaveChangesAsync();
        return true;
    }
}
