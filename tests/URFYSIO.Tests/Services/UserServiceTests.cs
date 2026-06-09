using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

public class UserServiceTests
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
    public async Task CreateAsync_WithValidData_ReturnsUser()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Email = "test@example.com",
            FirstName = "John",
            LastName = "Doe",
            Role = UserRole.Client
        };

        var result = await service.CreateAsync(user, "SecurePass123");

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("test@example.com", result.Email);
        Assert.NotEmpty(result.PasswordHash);
        Assert.NotEqual(default, result.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_ClientRole_CreatesClientProfile()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Email = "client@example.com",
            FirstName = "Jane",
            LastName = "Doe",
            Role = UserRole.Client
        };

        var result = await service.CreateAsync(user, "pass");

        var profile = await db.ClientProfiles.FirstOrDefaultAsync(p => p.UserId == result.Id);
        Assert.NotNull(profile);
    }

    [Fact]
    public async Task CreateAsync_PhysioRole_CreatesPhysioProfile()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Email = "physio@example.com",
            FirstName = "Dr",
            LastName = "Smith",
            Role = UserRole.Physiotherapist
        };

        var result = await service.CreateAsync(user, "pass");

        var profile = await db.PhysiotherapistProfiles.FirstOrDefaultAsync(p => p.UserId == result.Id);
        Assert.NotNull(profile);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateEmail_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Email = "existing@example.com",
            FirstName = "A", LastName = "B", Role = UserRole.Client,
            PasswordHash = "", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var duplicate = new User
        {
            Email = "existing@example.com",
            FirstName = "C", LastName = "D", Role = UserRole.Client
        };

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(duplicate, "pass"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_Auth0UserWithoutPassword_SetsEmptyHash()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Email = "auth0user@example.com", FirstName = "Auth0", LastName = "User",
            Role = UserRole.Client, Auth0Id = "auth0|123"
        };

        var result = await service.CreateAsync(user, string.Empty);

        Assert.Equal(string.Empty, result.PasswordHash);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var result = await service.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByAuth0IdAsync_WithValidAuth0Id_ReturnsUser()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), Email = "a0@test.com", FirstName = "A", LastName = "B",
            Role = UserRole.Client, Auth0Id = "auth0|abc123",
            PasswordHash = "", CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await service.GetByAuth0IdAsync("auth0|abc123");

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Id);
    }

    [Fact]
    public async Task GetByAuth0IdAsync_WithNonExistentAuth0Id_ReturnsNull()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var result = await service.GetByAuth0IdAsync("auth0|doesnotexist");

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_RolePromotedToPhysio_CreatesMissingPhysioProfile()
    {
        // Regression test for the "Failed to create slot. Check for overlapping times"
        // bug: when an admin promoted a user from Client to Physiotherapist, no
        // PhysiotherapistProfile was ever created, so the stale ClientProfile.Id leaked
        // into availability-slot inserts and got rejected by the FK constraint.
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), Email = "promote@test.com",
            FirstName = "Soon", LastName = "Physio", Role = UserRole.Client,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.ClientProfiles.Add(new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id });
        await db.SaveChangesAsync();

        // Act — admin flips the role.
        user.Role = UserRole.Physiotherapist;
        await service.UpdateAsync(user);

        // Assert — a PhysiotherapistProfile now exists for the user.
        var physioProfile = await db.PhysiotherapistProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        Assert.NotNull(physioProfile);
    }

    [Fact]
    public async Task EnsureProfileForRoleAsync_PhysioMissingProfile_CreatesIt()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), Email = "healme@test.com",
            FirstName = "Heal", LastName = "Me", Role = UserRole.Physiotherapist,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var created = await service.EnsureProfileForRoleAsync(user.Id);

        Assert.True(created);
        Assert.NotNull(await db.PhysiotherapistProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task EnsureProfileForRoleAsync_ProfileAlreadyExists_IsNoOp()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), Email = "already@test.com",
            FirstName = "Al", LastName = "Ready", Role = UserRole.Physiotherapist,
            PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.PhysiotherapistProfiles.Add(new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = user.Id });
        await db.SaveChangesAsync();

        var created = await service.EnsureProfileForRoleAsync(user.Id);

        Assert.False(created);
        Assert.Equal(1, await db.PhysiotherapistProfiles.CountAsync(p => p.UserId == user.Id));
    }

    [Fact]
    public async Task DeactivateAsync_WithValidId_SetsIsActiveFalse()
    {
        using var db = GetDbContext();
        var service = new UserService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), Email = "deact@test.com", FirstName = "A", LastName = "B",
            Role = UserRole.Client, PasswordHash = "", IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await service.DeactivateAsync(user.Id);

        Assert.True(result);
        var dbUser = await db.Users.FindAsync(user.Id);
        Assert.False(dbUser!.IsActive);
    }
}
