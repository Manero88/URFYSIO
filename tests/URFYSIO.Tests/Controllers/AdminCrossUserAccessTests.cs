using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using URFYSIO.API.Controllers;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;
using URFYSIO.Shared.DTOs.Availability;

namespace URFYSIO.Tests.Controllers;

/// <summary>
/// Authorization tests for the admin cross-user management features: admins may
/// read/manage other users' appointments and availability; non-admins may not
/// reach across to other users' data.
/// </summary>
public class AdminCrossUserAccessTests
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

    // Mimics what Auth0UserSyncMiddleware produces: a principal whose role claim is
    // the DB role (roleType wired to ClaimTypes.Role so IsInRole works) and the local
    // user id stashed in HttpContext.Items.
    private static ControllerContext ContextFor(Guid localUserId, string role)
    {
        var http = new DefaultHttpContext();
        http.Items["LocalUserId"] = localUserId;
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test",
            nameType: ClaimTypes.NameIdentifier,
            roleType: ClaimTypes.Role);
        http.User = new ClaimsPrincipal(identity);
        return new ControllerContext { HttpContext = http };
    }

    private static (User User, PhysiotherapistProfile Profile) SeedPhysio(AppDbContext db, string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Phys", LastName = "Io",
            Email = email, PasswordHash = "", Role = UserRole.Physiotherapist
        };
        var profile = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
        db.Users.Add(user);
        db.PhysiotherapistProfiles.Add(profile);
        return (user, profile);
    }

    private static (User User, ClientProfile Profile) SeedClient(AppDbContext db, string email)
    {
        var user = new User
        {
            Id = Guid.NewGuid(), FirstName = "Cli", LastName = "Ent",
            Email = email, PasswordHash = "", Role = UserRole.Client
        };
        var profile = new ClientProfile { Id = Guid.NewGuid(), UserId = user.Id, User = user };
        db.Users.Add(user);
        db.ClientProfiles.Add(profile);
        return (user, profile);
    }

    private static User SeedAdmin(AppDbContext db)
    {
        var admin = new User
        {
            Id = Guid.NewGuid(), FirstName = "Ad", LastName = "Min",
            Email = $"{Guid.NewGuid()}@admin.test", PasswordHash = "", Role = UserRole.Admin
        };
        db.Users.Add(admin);
        return admin;
    }

    // ===== FEATURE 1: admin fetches appointments filtered by physio / client =====

    [Fact]
    public async Task Admin_CanFetchAppointmentsByPhysioAndByClient()
    {
        using var db = GetDbContext();
        var (_, physio) = SeedPhysio(db, "p1@test.nl");
        var (_, client) = SeedClient(db, "c1@test.nl");
        var admin = SeedAdmin(db);
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = client.Id, PhysiotherapistProfileId = physio.Id,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            Status = AppointmentStatus.Scheduled
        });
        await db.SaveChangesAsync();

        var controller = new AppointmentsController(new AppointmentService(db), new UserService(db))
        {
            ControllerContext = ContextFor(admin.Id, "Admin")
        };

        var byPhysio = await controller.GetByPhysiotherapist(physio.Id);
        var okPhysio = Assert.IsType<OkObjectResult>(byPhysio);
        Assert.Single((IEnumerable<object>)okPhysio.Value!);

        var byClient = await controller.GetByClient(client.Id);
        var okClient = Assert.IsType<OkObjectResult>(byClient);
        Assert.Single((IEnumerable<object>)okClient.Value!);
    }

    [Fact]
    public async Task Client_CannotFetchAnotherClientsAppointments()
    {
        using var db = GetDbContext();
        var (callerUser, _) = SeedClient(db, "caller@test.nl");
        var (_, otherClientProfile) = SeedClient(db, "other@test.nl");
        await db.SaveChangesAsync();

        var controller = new AppointmentsController(new AppointmentService(db), new UserService(db))
        {
            ControllerContext = ContextFor(callerUser.Id, "Client")
        };

        var result = await controller.GetByClient(otherClientProfile.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    // ===== FEATURE 3: availability ownership — admin any, physio own only =====

    private static CreateAvailabilitySlotDto SlotDtoFor(Guid physioProfileId) => new()
    {
        PhysiotherapistProfileId = physioProfileId,
        StartTime = DateTime.UtcNow.AddDays(2).Date.AddHours(9),
        EndTime = DateTime.UtcNow.AddDays(2).Date.AddHours(9).AddMinutes(30)
    };

    [Fact]
    public async Task Admin_CanCreateSlotForAnyPhysio()
    {
        using var db = GetDbContext();
        var (_, physio) = SeedPhysio(db, "target@test.nl");
        var admin = SeedAdmin(db);
        await db.SaveChangesAsync();

        var controller = new AvailabilityController(new AvailabilityService(db), new UserService(db))
        {
            ControllerContext = ContextFor(admin.Id, "Admin")
        };

        var result = await controller.Create(SlotDtoFor(physio.Id));

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(1, await db.AvailabilitySlots.CountAsync(s => s.PhysiotherapistProfileId == physio.Id));
    }

    [Fact]
    public async Task Physio_CanCreateSlotForThemselves()
    {
        using var db = GetDbContext();
        var (physioUser, physioProfile) = SeedPhysio(db, "self@test.nl");
        await db.SaveChangesAsync();

        var controller = new AvailabilityController(new AvailabilityService(db), new UserService(db))
        {
            ControllerContext = ContextFor(physioUser.Id, "Physiotherapist")
        };

        var result = await controller.Create(SlotDtoFor(physioProfile.Id));

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task Physio_CannotCreateSlotForAnotherPhysio()
    {
        using var db = GetDbContext();
        var (callerUser, _) = SeedPhysio(db, "caller@test.nl");
        var (_, victimProfile) = SeedPhysio(db, "victim@test.nl");
        await db.SaveChangesAsync();

        var controller = new AvailabilityController(new AvailabilityService(db), new UserService(db))
        {
            ControllerContext = ContextFor(callerUser.Id, "Physiotherapist")
        };

        var result = await controller.Create(SlotDtoFor(victimProfile.Id));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.Equal(0, await db.AvailabilitySlots.CountAsync());
    }

    [Fact]
    public async Task Physio_CannotDeleteAnotherPhysiosSlot()
    {
        using var db = GetDbContext();
        var (callerUser, _) = SeedPhysio(db, "caller2@test.nl");
        var (_, victimProfile) = SeedPhysio(db, "victim2@test.nl");
        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = victimProfile.Id,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            IsBooked = false
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var controller = new AvailabilityController(new AvailabilityService(db), new UserService(db))
        {
            ControllerContext = ContextFor(callerUser.Id, "Physiotherapist")
        };

        var result = await controller.Delete(slot.Id);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.NotNull(await db.AvailabilitySlots.FindAsync(slot.Id));
    }

    [Fact]
    public async Task Admin_CanDeleteAnyPhysiosSlot()
    {
        using var db = GetDbContext();
        var (_, physioProfile) = SeedPhysio(db, "p3@test.nl");
        var admin = SeedAdmin(db);
        var slot = new AvailabilitySlot
        {
            Id = Guid.NewGuid(), PhysiotherapistProfileId = physioProfile.Id,
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            IsBooked = false
        };
        db.AvailabilitySlots.Add(slot);
        await db.SaveChangesAsync();

        var controller = new AvailabilityController(new AvailabilityService(db), new UserService(db))
        {
            ControllerContext = ContextFor(admin.Id, "Admin")
        };

        var result = await controller.Delete(slot.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.AvailabilitySlots.FindAsync(slot.Id));
    }
}
