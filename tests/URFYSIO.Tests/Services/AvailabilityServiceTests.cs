using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

public class AvailabilityServiceTests
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
    public async Task CreateAsync_WithValidSlot_CreatesSlot()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        var result = await service.CreateAsync(slot);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.False(result.Value!.IsBooked);
        Assert.NotEqual(Guid.Empty, result.Value.Id);

        var dbSlot = await db.AvailabilitySlots.FindAsync(result.Value.Id);
        Assert.NotNull(dbSlot);
    }

    // Validation failures are returned as failed results, NOT thrown — overlapping
    // slots are routine user input, and the old DomainException made the debugger
    // break on every attempt (the original bug report).
    [Fact]
    public async Task CreateAsync_WithOverlappingSlot_ReturnsFailure()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);
        var physioId = Guid.NewGuid();

        db.AvailabilitySlots.Add(new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            IsBooked = false
        });
        await db.SaveChangesAsync();

        var overlapping = new AvailabilitySlot
        {
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(3)
        };

        var result = await service.CreateAsync(overlapping);

        Assert.False(result.Success);
        Assert.Contains("overlaps", result.Error);
        // Nothing was persisted.
        Assert.Equal(1, await db.AvailabilitySlots.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_WithPastSlot_ReturnsFailure()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var pastSlot = new AvailabilitySlot
        {
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(-1).AddHours(1)
        };

        var result = await service.CreateAsync(pastSlot);

        Assert.False(result.Success);
        Assert.Contains("past", result.Error);
    }

    [Fact]
    public async Task CreateAsync_WithStartAfterEnd_ReturnsFailure()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        var result = await service.CreateAsync(slot);

        Assert.False(result.Success);
        Assert.Contains("before end time", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_BookedSlot_ReturnsFailure()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var result = await service.UpdateAsync(slot);

        Assert.False(result.Success);
        Assert.Contains("booked", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_OverlappingAnotherSlot_ReturnsFailure()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);
        var physioId = Guid.NewGuid();

        var existing = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1).Date.AddHours(9),
            EndTime = DateTime.UtcNow.AddDays(1).Date.AddHours(10)
        };
        var toEdit = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioId,
            StartTime = DateTime.UtcNow.AddDays(1).Date.AddHours(11),
            EndTime = DateTime.UtcNow.AddDays(1).Date.AddHours(12)
        };
        db.AvailabilitySlots.AddRange(existing, toEdit);
        await db.SaveChangesAsync();

        // Move the edited slot onto the existing one.
        toEdit.StartTime = existing.StartTime.AddMinutes(30);
        toEdit.EndTime = existing.EndTime.AddMinutes(30);

        var result = await service.UpdateAsync(toEdit);

        Assert.False(result.Success);
        Assert.Contains("overlaps", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_MovingOwnSlot_DoesNotConflictWithItself()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1).Date.AddHours(9),
            EndTime = DateTime.UtcNow.AddDays(1).Date.AddHours(10)
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        // Shift by 15 minutes — still overlapping its OWN old window, which must
        // not count as a conflict (the self-exclusion in the overlap query).
        slot.StartTime = slot.StartTime.AddMinutes(15);
        slot.EndTime = slot.EndTime.AddMinutes(15);

        var result = await service.UpdateAsync(slot);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeleteAsync_BookedSlot_ReturnsFalseAndKeepsSlot()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = true
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var result = await service.DeleteAsync(slot.Id);

        Assert.False(result);
        Assert.NotNull(await db.AvailabilitySlots.FindAsync(slot.Id));
    }

    [Fact]
    public async Task DeleteAsync_UnbookedSlot_DeletesSuccessfully()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            IsBooked = false
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var result = await service.DeleteAsync(slot.Id);

        Assert.True(result);
        Assert.Null(await db.AvailabilitySlots.FindAsync(slot.Id));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentSlot_ReturnsFalse()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var result = await service.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }
}
