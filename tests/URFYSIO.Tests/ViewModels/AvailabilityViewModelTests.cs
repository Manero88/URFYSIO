#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.ViewModels;

public class AvailabilityViewModelTests
{
    private readonly Mock<IApiService> _apiServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();

    private AvailabilityViewModel CreateViewModel() => new(_apiServiceMock.Object, _authServiceMock.Object);

    private static Guid SetupPhysioUser(Mock<IApiService> apiMock)
    {
        var profileId = Guid.NewGuid();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), ProfileId = profileId,
            Email = "physio@test.com", FirstName = "Test", LastName = "Physio",
            Role = UserRole.Physiotherapist, IsActive = true
        };
        apiMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        return profileId;
    }

    private static List<AvailabilitySlotDto> CreateSlots(Guid physioProfileId) =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioProfileId,
            PhysiotherapistName = "Dr. Test",
            StartTime = DateTime.UtcNow.AddDays(1).Date.AddHours(9),
            EndTime = DateTime.UtcNow.AddDays(1).Date.AddHours(9).AddMinutes(30),
            IsBooked = false
        },
        new()
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = physioProfileId,
            PhysiotherapistName = "Dr. Test",
            StartTime = DateTime.UtcNow.AddDays(1).Date.AddHours(10),
            EndTime = DateTime.UtcNow.AddDays(1).Date.AddHours(10).AddMinutes(30),
            IsBooked = true
        }
    ];

    [Fact]
    public async Task LoadSlots_PopulatesSlotsList()
    {
        // Arrange
        var vm = CreateViewModel();
        var profileId = SetupPhysioUser(_apiServiceMock);
        var slots = CreateSlots(profileId);
        _apiServiceMock.Setup(a => a.GetPhysioSlotsAsync(profileId)).ReturnsAsync(slots);

        // Act
        await vm.LoadSlotsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, vm.Slots.Count);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task LoadSlots_OnError_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ThrowsAsync(new Exception("Server error"));

        // Act
        await vm.LoadSlotsCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Server error", vm.ErrorMessage);
        Assert.Empty(vm.Slots);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CreateSlot_WithValidData_CallsCreateApi()
    {
        // Arrange
        var vm = CreateViewModel();
        var profileId = SetupPhysioUser(_apiServiceMock);
        _apiServiceMock.Setup(a => a.GetPhysioSlotsAsync(profileId)).ReturnsAsync([]);
        _apiServiceMock.Setup(a => a.CreateAvailabilitySlotAsync(It.Is<CreateAvailabilitySlotDto>(d =>
            d.PhysiotherapistProfileId == profileId)))
            .ReturnsAsync(new AvailabilitySlotDto
            {
                Id = Guid.NewGuid(),
                PhysiotherapistProfileId = profileId,
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30)
            });

        // Act
        await vm.AddSlotCommand.ExecuteAsync(null);

        // Assert
        _apiServiceMock.Verify(a => a.CreateAvailabilitySlotAsync(It.Is<CreateAvailabilitySlotDto>(d =>
            d.PhysiotherapistProfileId == profileId)), Times.Once);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void EditSlot_SetsIsEditingTrueAndPopulatesForm()
    {
        // Arrange
        var vm = CreateViewModel();
        var futureDate = DateTime.UtcNow.AddDays(7).Date;
        var slot = new AvailabilitySlotDto
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = futureDate.AddHours(14),
            EndTime = futureDate.AddHours(14).AddMinutes(30),
            IsBooked = false
        };

        // Act
        vm.EditSlotCommand.Execute(slot);

        // Assert
        Assert.True(vm.IsEditing);
        Assert.Equal(slot, vm.EditingSlot);
        Assert.Equal(slot.StartTime.Date, vm.NewSlotDate);
        Assert.Equal(slot.StartTime.TimeOfDay, vm.NewSlotStartTime);
        // EndTime is auto-computed (start + 30 min) by OnNewSlotStartTimeChanged;
        // since the test slot is already 30 min, the stored and computed values match.
        Assert.Equal(slot.EndTime.TimeOfDay, vm.NewSlotEndTime);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void EditSlot_BookedSlot_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var slot = new AvailabilitySlotDto
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            IsBooked = true
        };

        // Act
        vm.EditSlotCommand.Execute(slot);

        // Assert
        Assert.False(vm.IsEditing);
        Assert.Null(vm.EditingSlot);
        Assert.Equal("Cannot edit a booked slot.", vm.ErrorMessage);
    }

    [Fact]
    public void CancelEdit_ResetsFormAndState()
    {
        // Arrange
        var vm = CreateViewModel();
        var futureDate = DateTime.UtcNow.AddDays(7).Date;
        var slot = new AvailabilitySlotDto
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            StartTime = futureDate.AddHours(14),
            EndTime = futureDate.AddHours(14).AddMinutes(30),
            IsBooked = false
        };
        vm.EditSlotCommand.Execute(slot);
        Assert.True(vm.IsEditing);

        // Act
        vm.CancelEditCommand.Execute(null);

        // Assert
        Assert.False(vm.IsEditing);
        Assert.Null(vm.EditingSlot);
        Assert.Equal(new TimeSpan(9, 0, 0), vm.NewSlotStartTime);
        Assert.Equal(new TimeSpan(9, 30, 0), vm.NewSlotEndTime);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task SaveEdit_WithValidData_CallsUpdateApi()
    {
        // Arrange
        var vm = CreateViewModel();
        var profileId = SetupPhysioUser(_apiServiceMock);
        // Use a future date so ValidateSlotTimes' "must be in the future" check passes.
        var futureDate = DateTime.UtcNow.AddDays(7).Date;
        var slot = new AvailabilitySlotDto
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = profileId,
            StartTime = futureDate.AddHours(14),
            EndTime = futureDate.AddHours(14).AddMinutes(30),
            IsBooked = false
        };

        vm.EditSlotCommand.Execute(slot);
        _apiServiceMock.Setup(a => a.UpdateAvailabilitySlotAsync(slot.Id, It.IsAny<UpdateAvailabilitySlotDto>()))
            .ReturnsAsync(slot);
        _apiServiceMock.Setup(a => a.GetPhysioSlotsAsync(profileId)).ReturnsAsync([]);

        // Act
        await vm.SaveEditCommand.ExecuteAsync(null);

        // Assert
        _apiServiceMock.Verify(a => a.UpdateAvailabilitySlotAsync(slot.Id, It.IsAny<UpdateAvailabilitySlotDto>()), Times.Once);
        Assert.False(vm.IsEditing); // CancelEdit called on success
        Assert.Null(vm.EditingSlot);
    }

    [Fact]
    public async Task AddSlot_NoProfile_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), ProfileId = null,
            Email = "physio@test.com", FirstName = "Test", LastName = "Physio",
            Role = UserRole.Physiotherapist, IsActive = true
        };
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);

        // Act
        await vm.AddSlotCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Physiotherapist profile not found.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }
}
#endif
