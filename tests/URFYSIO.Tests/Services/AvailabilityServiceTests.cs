using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Exceptions;
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

        Assert.NotNull(result);
        Assert.False(result.IsBooked);
        Assert.NotEqual(Guid.Empty, result.Id);

        var dbSlot = await db.AvailabilitySlots.FindAsync(result.Id);
        Assert.NotNull(dbSlot);
    }

    [Fact]
    public async Task CreateAsync_WithOverlappingSlot_ThrowsDomainException()
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

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(overlapping));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("overlaps", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_WithPastSlot_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var pastSlot = new AvailabilitySlot
        {
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(-1).AddHours(1)
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(pastSlot));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task CreateAsync_WithStartAfterEnd_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new AvailabilityService(db);

        var slot = new AvailabilitySlot
        {
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1)
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(slot));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("before end time", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_BookedSlot_ThrowsDomainException()
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

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(slot));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("booked", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_BookedSlot_ThrowsDomainException()
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

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync(slot.Id));
        Assert.Equal(409, ex.StatusCode);
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
