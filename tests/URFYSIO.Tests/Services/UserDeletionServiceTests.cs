using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using URFYSIO.API.Controllers;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;
using URFYSIO.Tests.Photos;

namespace URFYSIO.Tests.Services;

public class UserDeletionServiceTests
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

    private static (UserDeletionService Service, Mock<IAuth0ManagementService> Auth0) CreateService(AppDbContext db)
    {
        var (service, auth0, _) = CreateServiceWithBlobs(db);
        return (service, auth0);
    }

    /// <summary>
    /// Same service, but also handing back the blob double — GDPR erasure has to remove
    /// treatment photos as well as rows, so some tests need to assert on storage.
    /// </summary>
    private static (UserDeletionService Service, Mock<IAuth0ManagementService> Auth0, FakeBlobStorageService Blobs)
        CreateServiceWithBlobs(AppDbContext db)
    {
        var auth0 = new Mock<IAuth0ManagementService>();
        auth0.Setup(a => a.DeleteUserAsync(It.IsAny<string>())).ReturnsAsync(true);
        var blobs = new FakeBlobStorageService();
        var service = new UserDeletionService(db, auth0.Object, blobs, Mock.Of<ILogger<UserDeletionService>>());
        return (service, auth0, blobs);
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_RemovesUserRelatedDataAndCallsAuth0()
    {
        using var db = GetDbContext();
        var (service, auth0) = CreateService(db);

        // A physio (owns the plan) + the client we will delete.
        var physioUser = new User { Id = Guid.NewGuid(), FirstName = "P", LastName = "T", Email = "p@t.com", PasswordHash = "", Role = UserRole.Physiotherapist };
        var physio = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id };
        var clientUser = new User { Id = Guid.NewGuid(), Auth0Id = "auth0|client", FirstName = "C", LastName = "L", Email = "c@l.com", PasswordHash = "", Role = UserRole.Client };
        var client = new ClientProfile { Id = Guid.NewGuid(), UserId = clientUser.Id };
        db.Users.AddRange(physioUser, clientUser);
        db.PhysiotherapistProfiles.Add(physio);
        db.ClientProfiles.Add(client);

        // The client's appointment.
        var appt = new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = client.Id, PhysiotherapistProfileId = physio.Id,
            StartTime = DateTime.UtcNow.AddDays(-3), EndTime = DateTime.UtcNow.AddDays(-3).AddMinutes(30),
            Status = AppointmentStatus.Completed
        };
        db.Appointments.Add(appt);

        // The client's treatment plan with an entry, and a comment authored by the client.
        var plan = new TreatmentPlan { Id = Guid.NewGuid(), ClientProfileId = client.Id, PhysiotherapistProfileId = physio.Id, Title = "Plan", CreatedAt = DateTime.UtcNow };
        var entry = new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan.Id, Title = "E", OrderIndex = 0, CreatedAt = DateTime.UtcNow };
        var comment = new TreatmentPlanEntryComment { Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = clientUser.Id, Text = "did it", CreatedAt = DateTime.UtcNow };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        db.TreatmentPlanEntryComments.Add(comment);
        await db.SaveChangesAsync();

        await service.DeleteUserPermanentlyAsync(clientUser.Id);

        // User + profile + all related rows gone.
        Assert.Null(await db.Users.FindAsync(clientUser.Id));
        Assert.False(await db.ClientProfiles.AnyAsync(c => c.Id == client.Id));
        Assert.False(await db.Appointments.AnyAsync(a => a.Id == appt.Id));
        Assert.False(await db.TreatmentPlans.AnyAsync(t => t.Id == plan.Id));
        Assert.False(await db.TreatmentPlanEntries.AnyAsync(e => e.Id == entry.Id));
        Assert.False(await db.TreatmentPlanEntryComments.AnyAsync(c => c.Id == comment.Id));

        // The other (physio) user is untouched.
        Assert.NotNull(await db.Users.FindAsync(physioUser.Id));

        // Auth0 account deletion was triggered with the right id.
        auth0.Verify(a => a.DeleteUserAsync("auth0|client"), Times.Once);
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_AuthoredCommentsOnOthersPlans_AreRemoved()
    {
        using var db = GetDbContext();
        var (service, _) = CreateService(db);

        // The user we delete authored a comment on SOMEONE ELSE'S plan entry. Because
        // the User→Comment FK is Restrict, this must be removed or the delete would fail.
        var deletee = new User { Id = Guid.NewGuid(), FirstName = "D", LastName = "X", Email = "d@x.com", PasswordHash = "", Role = UserRole.Client };
        var deleteeProfile = new ClientProfile { Id = Guid.NewGuid(), UserId = deletee.Id };
        var otherPlan = new TreatmentPlan { Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(), PhysiotherapistProfileId = Guid.NewGuid(), Title = "Other", CreatedAt = DateTime.UtcNow };
        var otherEntry = new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = otherPlan.Id, Title = "OE", OrderIndex = 0, CreatedAt = DateTime.UtcNow };
        var authored = new TreatmentPlanEntryComment { Id = Guid.NewGuid(), TreatmentPlanEntryId = otherEntry.Id, UserId = deletee.Id, Text = "note", CreatedAt = DateTime.UtcNow };
        db.Users.Add(deletee);
        db.ClientProfiles.Add(deleteeProfile);
        db.TreatmentPlans.Add(otherPlan);
        db.TreatmentPlanEntries.Add(otherEntry);
        db.TreatmentPlanEntryComments.Add(authored);
        await db.SaveChangesAsync();

        await service.DeleteUserPermanentlyAsync(deletee.Id);

        Assert.Null(await db.Users.FindAsync(deletee.Id));
        Assert.False(await db.TreatmentPlanEntryComments.AnyAsync(c => c.Id == authored.Id));
        // The other person's plan/entry survive — only the authored comment was removed.
        Assert.True(await db.TreatmentPlans.AnyAsync(t => t.Id == otherPlan.Id));
        Assert.True(await db.TreatmentPlanEntries.AnyAsync(e => e.Id == otherEntry.Id));
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_PhysioWithFutureAppointments_IsRefused()
    {
        using var db = GetDbContext();
        var (service, auth0) = CreateService(db);

        var physioUser = new User { Id = Guid.NewGuid(), Auth0Id = "auth0|physio", FirstName = "P", LastName = "T", Email = "p@t.com", PasswordHash = "", Role = UserRole.Physiotherapist };
        var physio = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id };
        db.Users.Add(physioUser);
        db.PhysiotherapistProfiles.Add(physio);
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(), PhysiotherapistProfileId = physio.Id,
            StartTime = DateTime.UtcNow.AddDays(2), EndTime = DateTime.UtcNow.AddDays(2).AddMinutes(30),
            Status = AppointmentStatus.Scheduled
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.DeleteUserPermanentlyAsync(physioUser.Id));
        Assert.Equal(409, ex.StatusCode);

        // Nothing was deleted and Auth0 was never called — the refusal is clean.
        Assert.NotNull(await db.Users.FindAsync(physioUser.Id));
        auth0.Verify(a => a.DeleteUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_PhysioWithActivePlans_IsRefused()
    {
        using var db = GetDbContext();
        var (service, _) = CreateService(db);

        var physioUser = new User { Id = Guid.NewGuid(), FirstName = "P", LastName = "T", Email = "p2@t.com", PasswordHash = "", Role = UserRole.Physiotherapist };
        var physio = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id };
        db.Users.Add(physioUser);
        db.PhysiotherapistProfiles.Add(physio);
        db.TreatmentPlans.Add(new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(), PhysiotherapistProfileId = physio.Id,
            Title = "Active", IsCompleted = false, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.DeleteUserPermanentlyAsync(physioUser.Id));
        Assert.Equal(409, ex.StatusCode);
        Assert.NotNull(await db.Users.FindAsync(physioUser.Id));
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_LastAdmin_IsRefused()
    {
        using var db = GetDbContext();
        var (service, _) = CreateService(db);

        var admin = new User { Id = Guid.NewGuid(), FirstName = "A", LastName = "D", Email = "a@d.com", PasswordHash = "", Role = UserRole.Admin };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => service.DeleteUserPermanentlyAsync(admin.Id));
        Assert.Equal(409, ex.StatusCode);
        Assert.NotNull(await db.Users.FindAsync(admin.Id));
    }

    // ---- Controller-level: an admin cannot permanently delete their own account ----

    [Fact]
    public async Task DeletePermanently_Self_ReturnsBadRequest_AndDoesNotCallService()
    {
        using var db = GetDbContext();
        var deletion = new Mock<IUserDeletionService>();
        var controller = new UsersController(
            Mock.Of<IUserService>(), deletion.Object, Mock.Of<IAuth0ManagementService>(),
            db, Mock.Of<ILogger<UsersController>>());

        var adminId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items["LocalUserId"] = adminId;

        var result = await controller.DeletePermanently(adminId);

        Assert.IsType<BadRequestObjectResult>(result);
        deletion.Verify(d => d.DeleteUserPermanentlyAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeletePermanently_OtherUser_CallsServiceAndReturnsOk()
    {
        using var db = GetDbContext();
        var deletion = new Mock<IUserDeletionService>();
        var controller = new UsersController(
            Mock.Of<IUserService>(), deletion.Object, Mock.Of<IAuth0ManagementService>(),
            db, Mock.Of<ILogger<UsersController>>());

        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items["LocalUserId"] = adminId;

        var result = await controller.DeletePermanently(targetId);

        Assert.IsType<OkObjectResult>(result);
        deletion.Verify(d => d.DeleteUserPermanentlyAsync(targetId), Times.Once);
    }

    // ===== GDPR erasure covers treatment photos, not just rows =====

    [Fact]
    public async Task DeleteUserPermanentlyAsync_DeletesPhotosAttachedToTheirComments()
    {
        using var db = GetDbContext();
        var (service, _, blobs) = CreateServiceWithBlobs(db);

        var physioUser = new User { Id = Guid.NewGuid(), FirstName = "P", LastName = "T", Email = "p@t.com", PasswordHash = "", Role = UserRole.Physiotherapist };
        var physio = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id };
        var clientUser = new User { Id = Guid.NewGuid(), Auth0Id = "auth0|client", FirstName = "C", LastName = "L", Email = "c@l.com", PasswordHash = "", Role = UserRole.Client };
        var client = new ClientProfile { Id = Guid.NewGuid(), UserId = clientUser.Id };
        db.Users.AddRange(physioUser, clientUser);
        db.PhysiotherapistProfiles.Add(physio);
        db.ClientProfiles.Add(client);

        var plan = new TreatmentPlan { Id = Guid.NewGuid(), ClientProfileId = client.Id, PhysiotherapistProfileId = physio.Id, Title = "Plan" };
        var entry = new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan.Id, Title = "Entry" };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);

        // A photo the departing client posted, and one the physio posted on the same plan.
        db.TreatmentPlanEntryComments.AddRange(
            new TreatmentPlanEntryComment
            {
                Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = clientUser.Id,
                Text = "mine", PhotoBlobName = "client-photo.jpg"
            },
            new TreatmentPlanEntryComment
            {
                Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = physioUser.Id,
                Text = "theirs", PhotoBlobName = "physio-photo.jpg"
            });
        await db.SaveChangesAsync();

        await service.DeleteUserPermanentlyAsync(clientUser.Id);

        // Both comment rows go (the whole plan is erased), so both photos must go with them
        // — a photo left in storage after an erasure request is still retained personal data.
        Assert.Contains("client-photo.jpg", blobs.Deleted);
        Assert.Contains("physio-photo.jpg", blobs.Deleted);
    }

    [Fact]
    public async Task DeleteUserPermanentlyAsync_StillErasesLocalDataWhenPhotoDeletionFails()
    {
        using var db = GetDbContext();
        var (service, _, blobs) = CreateServiceWithBlobs(db);
        blobs.DeleteException = new InvalidOperationException("storage unavailable");

        var clientUser = new User { Id = Guid.NewGuid(), Auth0Id = "auth0|c", FirstName = "C", LastName = "L", Email = "c@l.com", PasswordHash = "", Role = UserRole.Client };
        var client = new ClientProfile { Id = Guid.NewGuid(), UserId = clientUser.Id };
        var physioUser = new User { Id = Guid.NewGuid(), FirstName = "P", LastName = "T", Email = "p2@t.com", PasswordHash = "", Role = UserRole.Physiotherapist };
        var physio = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id };
        db.Users.AddRange(clientUser, physioUser);
        db.ClientProfiles.Add(client);
        db.PhysiotherapistProfiles.Add(physio);

        var plan = new TreatmentPlan { Id = Guid.NewGuid(), ClientProfileId = client.Id, PhysiotherapistProfileId = physio.Id, Title = "Plan" };
        var entry = new TreatmentPlanEntry { Id = Guid.NewGuid(), TreatmentPlanId = plan.Id, Title = "Entry" };
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        db.TreatmentPlanEntryComments.Add(new TreatmentPlanEntryComment
        {
            Id = Guid.NewGuid(), TreatmentPlanEntryId = entry.Id, UserId = clientUser.Id,
            Text = "x", PhotoBlobName = "unreachable.jpg"
        });
        await db.SaveChangesAsync();

        // Storage being down must not abort an erasure that already committed — the
        // failure is logged for manual cleanup instead.
        await service.DeleteUserPermanentlyAsync(clientUser.Id);

        Assert.Empty(await db.Users.Where(u => u.Id == clientUser.Id).ToListAsync());
        Assert.Empty(await db.TreatmentPlanEntryComments.ToListAsync());
    }
}
