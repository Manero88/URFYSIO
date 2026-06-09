#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.ViewModels;

public class BookAppointmentViewModelTests
{
    private readonly Mock<IApiService> _apiServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();

    private BookAppointmentViewModel CreateViewModel() => new(_apiServiceMock.Object, _authServiceMock.Object);

    private static List<AvailabilitySlotDto> CreateSlots() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            PhysiotherapistName = "Dr. Smith",
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            IsBooked = false
        },
        new()
        {
            Id = Guid.NewGuid(),
            PhysiotherapistProfileId = Guid.NewGuid(),
            PhysiotherapistName = "Dr. Jones",
            StartTime = DateTime.UtcNow.AddDays(2),
            EndTime = DateTime.UtcNow.AddDays(2).AddMinutes(30),
            IsBooked = false
        }
    ];

    [Fact]
    public async Task LoadPhysiotherapists_PopulatesList()
    {
        // Arrange
        var vm = CreateViewModel();
        var physios = new List<UserDto>
        {
            new() { Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(), FirstName = "Dr", LastName = "Smith", Email = "smith@test.com", Role = UserRole.Physiotherapist, IsActive = true },
            new() { Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(), FirstName = "Dr", LastName = "Jones", Email = "jones@test.com", Role = UserRole.Physiotherapist, IsActive = true }
        };
        _apiServiceMock.Setup(a => a.GetPhysiotherapistsAsync()).ReturnsAsync(physios);

        // Act
        await vm.LoadPhysiotherapistsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, vm.Physiotherapists.Count);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task LoadPhysiotherapists_OnError_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        _apiServiceMock.Setup(a => a.GetPhysiotherapistsAsync()).ThrowsAsync(new Exception("Connection timeout"));

        // Act
        await vm.LoadPhysiotherapistsCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Connection timeout", vm.ErrorMessage);
        Assert.Empty(vm.Physiotherapists);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task BookCommand_WithoutSelectedSlot_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedSlot = null;

        // Act
        await vm.BookSelectedSlotCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Please select a time slot.", vm.ErrorMessage);
        _apiServiceMock.Verify(a => a.CreateAppointmentAsync(It.IsAny<CreateAppointmentDto>()), Times.Never);
    }

    [Fact]
    public async Task BookCommand_WithSelectedSlot_CallsCreateAppointment()
    {
        // Arrange
        var vm = CreateViewModel();
        var slot = CreateSlots().First();
        vm.SelectedSlot = slot;

        var profileId = Guid.NewGuid();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), ProfileId = profileId,
            Email = "client@test.com", FirstName = "Test", LastName = "Client",
            Role = UserRole.Client, IsActive = true
        };

        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        _apiServiceMock
            .Setup(a => a.CreateAppointmentAsync(It.Is<CreateAppointmentDto>(d =>
                d.ClientProfileId == profileId &&
                d.PhysiotherapistProfileId == slot.PhysiotherapistProfileId &&
                d.AvailabilitySlotId == slot.Id)))
            .ReturnsAsync(new AppointmentDto
            {
                Id = Guid.NewGuid(), ClientProfileId = profileId,
                PhysiotherapistProfileId = slot.PhysiotherapistProfileId,
                StartTime = slot.StartTime, EndTime = slot.EndTime,
                Status = AppointmentStatus.Scheduled
            });

        // Act — Shell.Current is null, so DisplayAlertAsync/GoToAsync will throw
        try
        {
            await vm.BookSelectedSlotCommand.ExecuteAsync(null);
        }
        catch (NullReferenceException)
        {
            // Expected: Shell.Current is null in test context
        }

        // Assert — the API call was made with correct parameters
        _apiServiceMock.Verify(a => a.CreateAppointmentAsync(It.Is<CreateAppointmentDto>(d =>
            d.ClientProfileId == profileId &&
            d.AvailabilitySlotId == slot.Id)), Times.Once);
    }

    [Fact]
    public async Task BookCommand_NullUser_SetsNotLoggedInError()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedSlot = CreateSlots().First();
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync((UserDto?)null);

        // Act
        await vm.BookSelectedSlotCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Not logged in.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task BookCommand_NoProfile_SetsProfileError()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedSlot = CreateSlots().First();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), ProfileId = null, // No profile
            Email = "client@test.com", FirstName = "Test", LastName = "Client",
            Role = UserRole.Client, IsActive = true
        };
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);

        // Act
        await vm.BookSelectedSlotCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Client profile not found.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task BookCommand_ApiReturnsNull_SetsSlotUnavailableError()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SelectedSlot = CreateSlots().First();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(),
            Email = "client@test.com", FirstName = "Test", LastName = "Client",
            Role = UserRole.Client, IsActive = true
        };
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        _apiServiceMock.Setup(a => a.CreateAppointmentAsync(It.IsAny<CreateAppointmentDto>()))
            .ReturnsAsync((AppointmentDto?)null);

        // Act
        await vm.BookSelectedSlotCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("slot may no longer be available", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }
}
#endif
