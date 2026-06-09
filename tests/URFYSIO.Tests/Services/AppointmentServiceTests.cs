using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

public class AppointmentServiceTests
{
    private static AppDbContext GetDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task CreateAsync_WithValidSlot_CreatesAppointment()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = false
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var appointment = new Appointment
        {
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            AvailabilitySlotId = slot.Id
        };

        var result = await service.CreateAsync(appointment);

        Assert.NotNull(result);
        Assert.Equal(AppointmentStatus.Scheduled, result.Status);
        Assert.Equal(slot.StartTime, result.StartTime);
        Assert.Equal(slot.EndTime, result.EndTime);

        var dbSlot = await db.AvailabilitySlots.FindAsync(slot.Id);
        Assert.True(dbSlot!.IsBooked);
    }

    [Fact]
    public async Task CreateAsync_WithAlreadyBookedSlot_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var appointment = new Appointment
        {
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            AvailabilitySlotId = slot.Id
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(appointment));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task CreateAsync_WithOverlappingAppointment_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddDays(1);

        // Pre-existing appointment (no slot)
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(),
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = start,
            EndTime = start.AddHours(1),
            Status = AppointmentStatus.Scheduled
        });
        await db.SaveChangesAsync();

        // Overlapping appointment without a slot
        var appointment = new Appointment
        {
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = start.AddMinutes(30),
            EndTime = start.AddHours(1).AddMinutes(30)
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(appointment));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("conflicts", ex.Message);
    }

    [Fact]
    public async Task CancelAsync_WithValidId_SetsStatusCancelled()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = slot.PhysiotherapistProfileId,
            AvailabilitySlotId = slot.Id,
            Status = AppointmentStatus.Scheduled
        };
        db.AvailabilitySlots.Add(slot);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var result = await service.CancelAsync(appointment.Id);

        Assert.True(result);
        var dbAppointment = await db.Appointments.FindAsync(appointment.Id);
        Assert.Equal(AppointmentStatus.Cancelled, dbAppointment!.Status);

        var dbSlot = await db.AvailabilitySlots.FindAsync(slot.Id);
        Assert.False(dbSlot!.IsBooked);
    }

    [Fact]
    public async Task CancelAsync_WithNonExistentId_ReturnsFalse()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);

        var result = await service.CancelAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task RescheduleAsync_WithValidData_UpdatesAppointment()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();

        var oldSlot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        var newSlot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(1),
            IsBooked = false
        };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId, AvailabilitySlotId = oldSlot.Id,
            StartTime = oldSlot.StartTime, EndTime = oldSlot.EndTime,
            Status = AppointmentStatus.Scheduled
        };

        db.AvailabilitySlots.AddRange(oldSlot, newSlot);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var result = await service.RescheduleAsync(appointment.Id, newSlot.Id);

        Assert.Equal(newSlot.Id, result.AvailabilitySlotId);
        Assert.Equal(newSlot.StartTime, result.StartTime);
    }

    [Fact]
    public async Task RescheduleAsync_FreesOldSlot_SetsIsBookedFalse()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();

        var oldSlot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        var newSlot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddHours(1),
            IsBooked = false
        };
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId, AvailabilitySlotId = oldSlot.Id,
            StartTime = oldSlot.StartTime, EndTime = oldSlot.EndTime,
            Status = AppointmentStatus.Scheduled
        };

        db.AvailabilitySlots.AddRange(oldSlot, newSlot);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        await service.RescheduleAsync(appointment.Id, newSlot.Id);

        var dbOldSlot = await db.AvailabilitySlots.FindAsync(oldSlot.Id);
        var dbNewSlot = await db.AvailabilitySlots.FindAsync(newSlot.Id);
        Assert.False(dbOldSlot!.IsBooked);
        Assert.True(dbNewSlot!.IsBooked);
    }

    [Fact]
    public async Task RescheduleAsync_CancelledAppointment_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new AppointmentService(db);
        var physioId = Guid.NewGuid();

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            Status = AppointmentStatus.Cancelled
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.RescheduleAsync(appointment.Id, Guid.NewGuid()));
        Assert.Equal(409, ex.StatusCode);
    }
}
