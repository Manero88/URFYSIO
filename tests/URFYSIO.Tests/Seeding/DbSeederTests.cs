using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Seeding;

namespace URFYSIO.Tests.Seeding;

public class DbSeederTests
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
    public async Task BackfillMissingProfilesAsync_PhysioWithoutProfile_CreatesProfile()
    {
        // This reproduces the "admin promoted a user to Physiotherapist but never
        // created a PhysiotherapistProfile" state that ships of users arrived in.
        using var db = GetDbContext();
        var user = new User
        {
            Id = Guid.NewGuid(), Email = "orphan@test.com",
            FirstName = "No", LastName = "Profile", Role = UserRole.Physiotherapist,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var added = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(1, added);
        Assert.NotNull(await db.PhysiotherapistProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task BackfillMissingProfilesAsync_ClientWithoutProfile_CreatesProfile()
    {
        using var db = GetDbContext();
        var user = new User
        {
            Id = Guid.NewGuid(), Email = "noclient@test.com",
            FirstName = "No", LastName = "Client", Role = UserRole.Client,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var added = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(1, added);
        Assert.NotNull(await db.ClientProfiles.FirstOrDefaultAsync(c => c.UserId == user.Id));
    }

    [Fact]
    public async Task BackfillMissingProfilesAsync_PromotedUserKeepsClientProfileGetsPhysioProfile()
    {
        // The real-world broken state: a user registered as Client (has ClientProfile),
        // was then promoted to Physiotherapist — their Role is now Physiotherapist but
        // they still only have the ClientProfile.
        using var db = GetDbContext();
        var user = new User
        {
            Id = Guid.NewGuid(), Email = "promoted@test.com",
            FirstName = "Promoted", LastName = "User", Role = UserRole.Physiotherapist,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var originalClientProfile = new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id };
        db.Users.Add(user);
        db.ClientProfiles.Add(originalClientProfile);
        await db.SaveChangesAsync();

        var added = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(1, added);
        // New physio profile was created
        Assert.NotNull(await db.PhysiotherapistProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id));
        // Old client profile preserved for historical data
        Assert.NotNull(await db.ClientProfiles.FirstOrDefaultAsync(c => c.Id == originalClientProfile.Id));
    }

    [Fact]
    public async Task BackfillMissingProfilesAsync_AdminWithoutProfile_IsNoOp()
    {
        using var db = GetDbContext();
        var admin = new User
        {
            Id = Guid.NewGuid(), Email = "admin@test.com",
            FirstName = "Admin", LastName = "Person", Role = UserRole.Admin,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var added = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(0, added);
        Assert.False(await db.ClientProfiles.AnyAsync(c => c.UserId == admin.Id));
        Assert.False(await db.PhysiotherapistProfiles.AnyAsync(p => p.UserId == admin.Id));
    }

    [Fact]
    public async Task BackfillMissingProfilesAsync_AllUsersHealthy_Returns0()
    {
        using var db = GetDbContext();
        var client = new User
        {
            Id = Guid.NewGuid(), Email = "c@test.com", FirstName = "C", LastName = "C",
            Role = UserRole.Client, PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        var physio = new User
        {
            Id = Guid.NewGuid(), Email = "p@test.com", FirstName = "P", LastName = "P",
            Role = UserRole.Physiotherapist, PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(client, physio);
        db.ClientProfiles.Add(new ClientProfile { Id = Guid.NewGuid(), UserId = client.Id });
        db.PhysiotherapistProfiles.Add(new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physio.Id });
        await db.SaveChangesAsync();

        var added = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(0, added);
    }

    [Fact]
    public async Task BackfillMissingProfilesAsync_RunTwice_IsIdempotent()
    {
        using var db = GetDbContext();
        var user = new User
        {
            Id = Guid.NewGuid(), Email = "idemp@test.com",
            FirstName = "I", LastName = "D", Role = UserRole.Physiotherapist,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var firstRun = await DbSeeder.BackfillMissingProfilesAsync(db);
        var secondRun = await DbSeeder.BackfillMissingProfilesAsync(db);

        Assert.Equal(1, firstRun);
        Assert.Equal(0, secondRun);
        Assert.Equal(1, await db.PhysiotherapistProfiles.CountAsync(p => p.UserId == user.Id));
    }
}
