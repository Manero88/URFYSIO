using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

/// <summary>
/// Activation is how an admin approves a self-service SSO sign-up. Auth0UserSyncMiddleware
/// creates those users with IsActive=false and the login screen bounces them until an admin
/// flips it — before this existed there was no way to do that at all.
/// </summary>
public class UserActivationTests
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

    private static User InactiveSsoUser(UserRole role = UserRole.Client) => new()
    {
        Id = Guid.NewGuid(),
        Auth0Id = "google-oauth2|123",
        Email = "karin@gmail.com",
        FirstName = "Karin",
        LastName = "Jansen",
        PasswordHash = string.Empty,
        Role = role,
        IsActive = false
    };

    [Fact]
    public async Task ActivateAsync_SetsIsActiveTrue()
    {
        using var db = GetDbContext();
        var user = InactiveSsoUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new UserService(db);
        var result = await service.ActivateAsync(user.Id);

        Assert.True(result);
        var reloaded = await db.Users.FindAsync(user.Id);
        Assert.True(reloaded!.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_CreatesMissingClientProfile()
    {
        using var db = GetDbContext();
        // Middleware-created users go through UserService.CreateAsync which adds a profile,
        // but rows from older code paths can be missing one. Approval must leave the account
        // fully usable either way.
        var user = InactiveSsoUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Assert.False(await db.ClientProfiles.AnyAsync(p => p.UserId == user.Id));

        var service = new UserService(db);
        await service.ActivateAsync(user.Id);

        Assert.True(await db.ClientProfiles.AnyAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotDuplicateAnExistingProfile()
    {
        using var db = GetDbContext();
        var user = InactiveSsoUser();
        db.Users.Add(user);
        db.ClientProfiles.Add(new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id });
        await db.SaveChangesAsync();

        var service = new UserService(db);
        await service.ActivateAsync(user.Id);

        Assert.Equal(1, await db.ClientProfiles.CountAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task ActivateAsync_CreatesPhysiotherapistProfileForPhysioRole()
    {
        using var db = GetDbContext();
        var user = InactiveSsoUser(UserRole.Physiotherapist);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new UserService(db);
        await service.ActivateAsync(user.Id);

        Assert.True(await db.PhysiotherapistProfiles.AnyAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task ActivateAsync_ReturnsFalseForUnknownUser()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        Assert.False(await service.ActivateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActivateAsync_IsIdempotent()
    {
        using var db = GetDbContext();
        var user = InactiveSsoUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new UserService(db);
        await service.ActivateAsync(user.Id);
        await service.ActivateAsync(user.Id);

        var reloaded = await db.Users.FindAsync(user.Id);
        Assert.True(reloaded!.IsActive);
        Assert.Equal(1, await db.ClientProfiles.CountAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task DeactivateThenActivate_RoundTrips()
    {
        using var db = GetDbContext();
        var user = InactiveSsoUser();
        user.IsActive = true;
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new UserService(db);

        await service.DeactivateAsync(user.Id);
        Assert.False((await db.Users.FindAsync(user.Id))!.IsActive);

        await service.ActivateAsync(user.Id);
        Assert.True((await db.Users.FindAsync(user.Id))!.IsActive);
    }
}
