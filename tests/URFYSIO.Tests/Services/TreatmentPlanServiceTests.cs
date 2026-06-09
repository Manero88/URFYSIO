using Microsoft.EntityFrameworkCore;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Exceptions;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Services;

public class TreatmentPlanServiceTests
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
    public async Task CreateAsync_WithValidData_CreatesPlan()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "Back Pain Plan",
            Description = "A description"
        };

        var result = await service.CreateAsync(plan);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.NotEqual(default, result.CreatedAt);

        var dbPlan = await db.TreatmentPlans.FindAsync(result.Id);
        Assert.NotNull(dbPlan);
    }

    [Fact]
    public async Task GetByClientProfileIdAsync_ReturnsPlansForClient()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        // Seed related entities so Include/ThenInclude chains resolve in InMemory
        var physioUser = new User
        {
            Id = Guid.NewGuid(), FirstName = "Physio", LastName = "User",
            Email = "physio@test.com", PasswordHash = "", Role = Core.Enums.UserRole.Physiotherapist
        };
        db.Users.Add(physioUser);

        var physioProfile = new PhysiotherapistProfile
        {
            Id = Guid.NewGuid(), UserId = physioUser.Id, User = physioUser,
            Specialization = "General"
        };
        db.PhysiotherapistProfiles.Add(physioProfile);

        var clientUser = new User
        {
            Id = Guid.NewGuid(), FirstName = "Client", LastName = "User",
            Email = "client@test.com", PasswordHash = "", Role = Core.Enums.UserRole.Client
        };
        db.Users.Add(clientUser);

        var clientProfile = new ClientProfile
        {
            Id = Guid.NewGuid(), UserId = clientUser.Id, User = clientUser
        };
        db.ClientProfiles.Add(clientProfile);

        var otherClientUser = new User
        {
            Id = Guid.NewGuid(), FirstName = "Other", LastName = "Client",
            Email = "other@test.com", PasswordHash = "", Role = Core.Enums.UserRole.Client
        };
        db.Users.Add(otherClientUser);

        var otherClientProfile = new ClientProfile
        {
            Id = Guid.NewGuid(), UserId = otherClientUser.Id, User = otherClientUser
        };
        db.ClientProfiles.Add(otherClientProfile);
        await db.SaveChangesAsync();

        await service.CreateAsync(new TreatmentPlan
        {
            ClientProfileId = clientProfile.Id,
            PhysiotherapistProfileId = physioProfile.Id,
            Title = "Plan 1"
        });
        await service.CreateAsync(new TreatmentPlan
        {
            ClientProfileId = clientProfile.Id,
            PhysiotherapistProfileId = physioProfile.Id,
            Title = "Plan 2"
        });
        await service.CreateAsync(new TreatmentPlan
        {
            ClientProfileId = otherClientProfile.Id,
            PhysiotherapistProfileId = physioProfile.Id,
            Title = "Other Plan"
        });

        var result = await service.GetByClientProfileIdAsync(clientProfile.Id);

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.Equal(clientProfile.Id, p.ClientProfileId));
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_ReturnsFalse()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var result = await service.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingPlan_ReturnsTrue()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Delete me",
            CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var result = await service.DeleteAsync(plan.Id);

        Assert.True(result);
        Assert.Null(await db.TreatmentPlans.FindAsync(plan.Id));
    }

    [Fact]
    public async Task AddEntryAsync_WithValidPlanId_AddsEntry()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Test Plan",
            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var entry = new TreatmentPlanEntry { Title = "Exercise 1", Description = "Daily", OrderIndex = 1 };
        var initialUpdate = plan.UpdatedAt;

        var result = await service.AddEntryAsync(plan.Id, entry);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(plan.Id, result.TreatmentPlanId);

        var dbPlan = await db.TreatmentPlans.FindAsync(plan.Id);
        Assert.True(dbPlan!.UpdatedAt > initialUpdate);
    }

    [Fact]
    public async Task AddEntryAsync_WithInvalidPlanId_ThrowsDomainException()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var entry = new TreatmentPlanEntry { Title = "Exercise 1" };

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.AddEntryAsync(Guid.NewGuid(), entry));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetEntryByIdAsync_WithValidId_ReturnsEntryWithPlan()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Test Plan",
            CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id,
            Title = "Entry 1", OrderIndex = 0, CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        await db.SaveChangesAsync();

        var result = await service.GetEntryByIdAsync(entry.Id);

        Assert.NotNull(result);
        Assert.NotNull(result.TreatmentPlan);
        Assert.Equal(plan.Id, result.TreatmentPlan.Id);
    }

    [Fact]
    public async Task UpdateEntryAsync_WithValidEntry_UpdatesFields()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Test",
            CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id,
            Title = "Old Title", Description = "Old Desc",
            OrderIndex = 0, IsCompleted = false, CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        await db.SaveChangesAsync();

        var updated = new TreatmentPlanEntry
        {
            Id = entry.Id, Title = "New Title", Description = "New Desc",
            OrderIndex = 5, IsCompleted = true
        };

        var result = await service.UpdateEntryAsync(updated);

        Assert.NotNull(result);
        Assert.Equal("New Title", result.Title);
        Assert.Equal("New Desc", result.Description);
        Assert.Equal(5, result.OrderIndex);
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public async Task DeleteEntryAsync_WithValidId_DeletesEntry()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Test",
            CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id,
            Title = "Delete me", OrderIndex = 0, CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        await db.SaveChangesAsync();

        var result = await service.DeleteEntryAsync(entry.Id);

        Assert.True(result);
        Assert.Null(await db.TreatmentPlanEntries.FindAsync(entry.Id));
    }

    // ===== Completion =====

    [Fact]
    public async Task CompleteAsync_SetsIsCompletedAndCompletedAt()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "P", CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var result = await service.CompleteAsync(plan.Id);
        var after = DateTime.UtcNow;

        Assert.NotNull(result);
        Assert.True(result!.IsCompleted);
        Assert.NotNull(result.CompletedAt);
        Assert.InRange(result.CompletedAt!.Value, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task CompleteAsync_NonExistentPlan_ReturnsNull()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var result = await service.CompleteAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCompleted_PreservesOriginalCompletedAt()
    {
        // Idempotency check: calling complete twice should NOT slide CompletedAt
        // forward — the History tab orders by this timestamp and silently rewriting
        // it would shuffle the list every time a physio re-tapped the button.
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var originalCompletedAt = DateTime.UtcNow.AddDays(-5);
        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "P", CreatedAt = DateTime.UtcNow.AddDays(-10),
            IsCompleted = true, CompletedAt = originalCompletedAt
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var result = await service.CompleteAsync(plan.Id);

        Assert.NotNull(result);
        Assert.True(result!.IsCompleted);
        Assert.Equal(originalCompletedAt, result.CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_ClearsCompletionFields()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "P", CreatedAt = DateTime.UtcNow.AddDays(-1),
            IsCompleted = true, CompletedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var result = await service.ReopenAsync(plan.Id);

        Assert.NotNull(result);
        Assert.False(result!.IsCompleted);
        Assert.Null(result.CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_AlreadyActive_IsNoOp()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "P", CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();

        var result = await service.ReopenAsync(plan.Id);

        Assert.NotNull(result);
        Assert.False(result!.IsCompleted);
        Assert.Null(result.CompletedAt);
    }

    // ===== Entry comments =====

    [Fact]
    public async Task AddCommentAsync_CreatesCommentWithUserAndText()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);

        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Test", LastName = "Author",
            Email = "author@test.com", PasswordHash = "", Role = Core.Enums.UserRole.Client
        };
        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "Plan",
            CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id,
            Title = "Entry", OrderIndex = 0, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        await db.SaveChangesAsync();

        var comment = await service.AddCommentAsync(entry.Id, user.Id, "Felt good today");

        Assert.NotNull(comment);
        Assert.Equal(entry.Id, comment.TreatmentPlanEntryId);
        Assert.Equal(user.Id, comment.UserId);
        Assert.Equal("Felt good today", comment.Text);
        // User navigation should be loaded so the controller's mapper can emit AuthorName.
        Assert.NotNull(comment.User);
        Assert.Equal("Test", comment.User.FirstName);
    }

    [Fact]
    public async Task AddCommentAsync_TrimsWhitespace()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var entry = await SeedEntryAsync(db);
        // EF Core InMemory drops rows whose Include target doesn't exist (real SQL
        // Server returns the row with a null nav property); seed a real user so the
        // re-fetch inside AddCommentAsync finds the comment.
        var user = await SeedUserAsync(db);

        var comment = await service.AddCommentAsync(entry.Id, user.Id, "   hi   ");

        Assert.Equal("hi", comment.Text);
    }

    [Fact]
    public async Task AddCommentAsync_RejectsEmptyText()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var entry = await SeedEntryAsync(db);
        var user = await SeedUserAsync(db);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.AddCommentAsync(entry.Id, user.Id, "   "));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task AddCommentAsync_RejectsTooLongText()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var entry = await SeedEntryAsync(db);
        var user = await SeedUserAsync(db);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.AddCommentAsync(entry.Id, user.Id, new string('x', 1001)));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task AddCommentAsync_NonExistentEntry_ThrowsNotFound()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var user = await SeedUserAsync(db);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.AddCommentAsync(Guid.NewGuid(), user.Id, "Hi"));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetCommentsForEntryAsync_ReturnsCommentsOrderedByCreatedAt()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var entry = await SeedEntryAsync(db);
        var user = await SeedUserAsync(db);

        // Insert out-of-order so the service-side OrderBy is exercised.
        var c2 = new TreatmentPlanEntryComment
        {
            Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = user.Id,
            Text = "second", CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        var c1 = new TreatmentPlanEntryComment
        {
            Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = user.Id,
            Text = "first", CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        var c3 = new TreatmentPlanEntryComment
        {
            Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = user.Id,
            Text = "third", CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlanEntryComments.AddRange(c2, c3, c1);
        await db.SaveChangesAsync();

        var comments = await service.GetCommentsForEntryAsync(entry.Id);

        Assert.Equal(3, comments.Count);
        Assert.Equal("first", comments[0].Text);
        Assert.Equal("second", comments[1].Text);
        Assert.Equal("third", comments[2].Text);
        // User navigation populated for mapping to DTO.
        Assert.All(comments, c => Assert.NotNull(c.User));
    }

    [Fact]
    public async Task GetCommentsForEntryAsync_NoComments_ReturnsEmpty()
    {
        using var db = GetDbContext();
        var service = new TreatmentPlanService(db);
        var entry = await SeedEntryAsync(db);

        var comments = await service.GetCommentsForEntryAsync(entry.Id);

        Assert.Empty(comments);
    }

    // --- helpers ---

    private static async Task<TreatmentPlanEntry> SeedEntryAsync(AppDbContext db)
    {
        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(), Title = "P",
            CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id,
            Title = "E", OrderIndex = 0, CreatedAt = DateTime.UtcNow
        };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static async Task<User> SeedUserAsync(AppDbContext db)
    {
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "U", LastName = "Ser",
            Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "",
            Role = Core.Enums.UserRole.Client
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
