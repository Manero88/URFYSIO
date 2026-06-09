using URFYSIO.API.Mapping;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Shared.DTOs.Users;
using SharedEnums = URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.Mapping;

public class MappingExtensionsTests
{
    [Fact]
    public void ToDto_User_MapsAllProperties()
    {
        var userId = Guid.NewGuid();
        var clientProfileId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "test@example.com",
            FirstName = "John",
            LastName = "Doe",
            PhoneNumber = "0612345678",
            Role = UserRole.Client,
            IsActive = true,
            CreatedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ClientProfile = new ClientProfile { Id = clientProfileId, UserId = userId }
        };

        var dto = user.ToDto();

        Assert.Equal(userId, dto.Id);
        Assert.Equal("test@example.com", dto.Email);
        Assert.Equal("John", dto.FirstName);
        Assert.Equal("Doe", dto.LastName);
        Assert.Equal("0612345678", dto.PhoneNumber);
        Assert.Equal(SharedEnums.UserRole.Client, dto.Role);
        Assert.True(dto.IsActive);
        Assert.Equal(clientProfileId, dto.ProfileId);
    }

    [Fact]
    public void ToDto_User_PhysioProfile_MapsProfileId()
    {
        var userId = Guid.NewGuid();
        var physioProfileId = Guid.NewGuid();
        var user = new User
        {
            Id = userId, Email = "physio@test.com", FirstName = "Dr", LastName = "Smith",
            Role = UserRole.Physiotherapist, CreatedAt = DateTime.UtcNow,
            PhysiotherapistProfile = new PhysiotherapistProfile { Id = physioProfileId, UserId = userId }
        };

        var dto = user.ToDto();

        Assert.Equal(physioProfileId, dto.ProfileId);
    }

    [Fact]
    public void ToDto_Appointment_MapsStatusCorrectly()
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            Status = AppointmentStatus.Cancelled,
            Notes = "Test note",
            CreatedAt = DateTime.UtcNow
        };

        var dto = appointment.ToDto();

        Assert.Equal(SharedEnums.AppointmentStatus.Cancelled, dto.Status);
        Assert.Equal("Test note", dto.Notes);
        Assert.Equal(appointment.Id, dto.Id);
    }

    [Fact]
    public void ToDto_AvailabilitySlot_MapsCorrectly()
    {
        var slotId = Guid.NewGuid();
        var physioId = Guid.NewGuid();
        var start = new DateTime(2025, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 7, 1, 10, 0, 0, DateTimeKind.Utc);

        var slot = new AvailabilitySlot
        {
            Id = slotId,
            PhysiotherapistProfileId = physioId,
            StartTime = start,
            EndTime = end,
            IsBooked = true
        };

        var dto = slot.ToDto();

        Assert.Equal(slotId, dto.Id);
        Assert.Equal(physioId, dto.PhysiotherapistProfileId);
        Assert.Equal(start, dto.StartTime);
        Assert.Equal(end, dto.EndTime);
        Assert.True(dto.IsBooked);
    }

    [Fact]
    public void ToDto_TreatmentPlan_MapsEntriesList()
    {
        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(),
            ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            Title = "Plan Title",
            Description = "Desc",
            CreatedAt = DateTime.UtcNow,
            Entries = new List<TreatmentPlanEntry>
            {
                new() { Id = Guid.NewGuid(), Title = "Entry 1", OrderIndex = 0, IsCompleted = false },
                new() { Id = Guid.NewGuid(), Title = "Entry 2", OrderIndex = 1, IsCompleted = true }
            }
        };

        var dto = plan.ToDto();

        Assert.Equal("Plan Title", dto.Title);
        Assert.Equal("Desc", dto.Description);
        Assert.Equal(2, dto.Entries.Count);
        Assert.Equal("Entry 1", dto.Entries[0].Title);
        Assert.True(dto.Entries[1].IsCompleted);
    }

    [Theory]
    [InlineData(UserRole.Client, SharedEnums.UserRole.Client)]
    [InlineData(UserRole.Physiotherapist, SharedEnums.UserRole.Physiotherapist)]
    [InlineData(UserRole.Admin, SharedEnums.UserRole.Admin)]
    public void EnumRoundtrip_UserRole_AllValuesMapped(UserRole coreRole, SharedEnums.UserRole expectedShared)
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Email = "e@e.com", FirstName = "A", LastName = "B",
            Role = coreRole, CreatedAt = DateTime.UtcNow
        };

        var dto = user.ToDto();

        Assert.Equal(expectedShared, dto.Role);
    }

    [Theory]
    [InlineData(AppointmentStatus.Scheduled, SharedEnums.AppointmentStatus.Scheduled)]
    [InlineData(AppointmentStatus.Completed, SharedEnums.AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled, SharedEnums.AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow, SharedEnums.AppointmentStatus.NoShow)]
    public void EnumRoundtrip_AppointmentStatus_AllValuesMapped(
        AppointmentStatus coreStatus, SharedEnums.AppointmentStatus expectedShared)
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1),
            Status = coreStatus, CreatedAt = DateTime.UtcNow
        };

        var dto = appointment.ToDto();

        Assert.Equal(expectedShared, dto.Status);
    }

    [Fact]
    public void ToEntity_CreateUserDto_MapsCorrectly()
    {
        var dto = new CreateUserDto
        {
            Email = "new@test.com", FirstName = "New", LastName = "User",
            PhoneNumber = "06", Role = SharedEnums.UserRole.Physiotherapist,
            Password = "pass123"
        };

        var entity = dto.ToEntity();

        Assert.Equal("new@test.com", entity.Email);
        Assert.Equal("New", entity.FirstName);
        Assert.Equal(UserRole.Physiotherapist, entity.Role);
    }
}
