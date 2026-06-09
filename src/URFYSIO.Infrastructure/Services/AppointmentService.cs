using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;

namespace URFYSIO.Infrastructure.Services;

public class AppointmentService : IAppointmentService
{
    private readonly AppDbContext _db;

    public AppointmentService(AppDbContext db) => _db = db;

    public async Task<Appointment?> GetByIdAsync(Guid id) =>
        await _db.Appointments
            .Include(a => a.ClientProfile).ThenInclude(c => c.User)
            .Include(a => a.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Include(a => a.AvailabilitySlot)
            .FirstOrDefaultAsync(a => a.Id == id);

    public async Task<IReadOnlyList<Appointment>> GetByClientProfileIdAsync(Guid clientProfileId) =>
        await _db.Appointments
            .Include(a => a.PhysiotherapistProfile).ThenInclude(p => p.User)
            .Where(a => a.ClientProfileId == clientProfileId)
            .OrderByDescending(a => a.StartTime)
            .ToListAsync();

    public async Task<IReadOnlyList<Appointment>> GetByPhysiotherapistProfileIdAsync(Guid physioProfileId) =>
        await _db.Appointments
            .Include(a => a.ClientProfile).ThenInclude(c => c.User)
            .Where(a => a.PhysiotherapistProfileId == physioProfileId)
            .OrderByDescending(a => a.StartTime)
            .ToListAsync();

    public async Task<IReadOnlyList<Appointment>> GetAllAsync(DateTime? from = null, DateTime? to = null)
    {
        var query = _db.Appointments
            .Include(a => a.ClientProfile).ThenInclude(c => c.User)
            .Include(a => a.PhysiotherapistProfile).ThenInclude(p => p.User)
            .AsQueryable();

        if (from.HasValue) query = query.Where(a => a.StartTime >= from.Value);
        if (to.HasValue) query = query.Where(a => a.EndTime <= to.Value);

        return await query.OrderByDescending(a => a.StartTime).ToListAsync();
    }

    public async Task<Appointment> CreateAsync(Appointment appointment)
    {
        // Validate the availability slot
        if (appointment.AvailabilitySlotId.HasValue)
        {
            var slot = await _db.AvailabilitySlots.FindAsync(appointment.AvailabilitySlotId.Value)
                ?? throw DomainException.NotFound("The specified availability slot does not exist.");

            if (slot.IsBooked)
                throw DomainException.Conflict("The specified availability slot is already booked.");

            if (slot.PhysiotherapistProfileId != appointment.PhysiotherapistProfileId)
                throw DomainException.Conflict("The availability slot does not belong to the specified physiotherapist.");

            // Use the slot times
            appointment.StartTime = slot.StartTime;
            appointment.EndTime = slot.EndTime;

            // Mark slot as booked
            slot.IsBooked = true;
        }

        // Check for double booking (same physio, overlapping time, non-cancelled)
        var hasConflict = await _db.Appointments.AnyAsync(a =>
            a.PhysiotherapistProfileId == appointment.PhysiotherapistProfileId &&
            a.Status != AppointmentStatus.Cancelled &&
            a.StartTime < appointment.EndTime &&
            a.EndTime > appointment.StartTime);

        if (hasConflict)
            throw DomainException.Conflict("This time slot conflicts with an existing appointment.");

        appointment.Id = Guid.NewGuid();
        appointment.Status = AppointmentStatus.Scheduled;
        appointment.CreatedAt = DateTime.UtcNow;

        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(appointment.Id) ?? appointment;
    }

    public async Task<Appointment> UpdateAsync(Appointment appointment)
    {
        _db.Appointments.Update(appointment);
        await _db.SaveChangesAsync();
        return appointment;
    }

    public async Task<bool> CancelAsync(Guid id)
    {
        var appointment = await _db.Appointments
            .Include(a => a.AvailabilitySlot)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (appointment is null) return false;
        if (appointment.Status == AppointmentStatus.Cancelled) return false;

        appointment.Status = AppointmentStatus.Cancelled;

        // Free up the availability slot
        if (appointment.AvailabilitySlot is not null)
        {
            appointment.AvailabilitySlot.IsBooked = false;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Appointment> RescheduleAsync(Guid id, Guid newSlotId)
    {
        var appointment = await _db.Appointments
            .Include(a => a.AvailabilitySlot)
            .FirstOrDefaultAsync(a => a.Id == id)
            ?? throw DomainException.NotFound("Appointment not found.");

        if (appointment.Status == AppointmentStatus.Cancelled)
            throw DomainException.Conflict("Cannot reschedule a cancelled appointment.");

        var newSlot = await _db.AvailabilitySlots.FindAsync(newSlotId)
            ?? throw DomainException.NotFound("The new availability slot does not exist.");

        if (newSlot.IsBooked)
            throw DomainException.Conflict("The new availability slot is already booked.");

        if (newSlot.PhysiotherapistProfileId != appointment.PhysiotherapistProfileId)
            throw DomainException.Conflict("The new slot must belong to the same physiotherapist.");

        // Free old slot
        if (appointment.AvailabilitySlot is not null)
        {
            appointment.AvailabilitySlot.IsBooked = false;
        }

        // Assign new slot
        appointment.AvailabilitySlotId = newSlotId;
        appointment.StartTime = newSlot.StartTime;
        appointment.EndTime = newSlot.EndTime;
        newSlot.IsBooked = true;

        await _db.SaveChangesAsync();
        return await GetByIdAsync(appointment.Id) ?? appointment;
    }
}
