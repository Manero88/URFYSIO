#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.ViewModels;

public class AppointmentsViewModelTests
{
    private readonly Mock<IApiService> _apiServiceMock = new();
    private readonly Mock<IAuthService> _authServiceMock = new();

    private AppointmentsViewModel CreateViewModel() => new(_apiServiceMock.Object, _authServiceMock.Object);

    private static UserDto CreateClientUser(Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = profileId,
        Email = "client@test.com",
        FirstName = "Test",
        LastName = "Client",
        Role = UserRole.Client,
        IsActive = true
    };

    private static List<AppointmentDto> CreateMixedAppointments()
    {
        var upcoming1 = new AppointmentDto
        {
            Id = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            Status = AppointmentStatus.Scheduled,
            ClientName = "Client A",
            PhysiotherapistName = "Physio A"
        };
        var upcoming2 = new AppointmentDto
        {
            Id = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(2),
            EndTime = DateTime.UtcNow.AddDays(2).AddMinutes(30),
            Status = AppointmentStatus.Scheduled,
            ClientName = "Client B",
            PhysiotherapistName = "Physio B"
        };
        var past1 = new AppointmentDto
        {
            Id = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(-1).AddMinutes(30),
            Status = AppointmentStatus.Completed,
            ClientName = "Client C",
            PhysiotherapistName = "Physio C"
        };
        var cancelled = new AppointmentDto
        {
            Id = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            Status = AppointmentStatus.Cancelled, // future but cancelled → goes to past
            ClientName = "Client D",
            PhysiotherapistName = "Physio D"
        };
        return [upcoming1, upcoming2, past1, cancelled];
    }

    [Fact]
    public async Task LoadAppointments_ClientRole_PopulatesCollections()
    {
        // Arrange
        var profileId = Guid.NewGuid();
        var vm = CreateViewModel();
        var user = CreateClientUser(profileId);
        var appointments = CreateMixedAppointments();

        _authServiceMock.Setup(a => a.CurrentRole).Returns("Client");
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        _apiServiceMock.Setup(a => a.GetAppointmentsByClientAsync(profileId)).ReturnsAsync(appointments);

        // Act
        await vm.LoadAppointmentsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, vm.UpcomingAppointments.Count);
        Assert.Equal(2, vm.PastAppointments.Count);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadAppointments_SplitsCorrectly_UpcomingVsPast()
    {
        // Arrange
        var profileId = Guid.NewGuid();
        var vm = CreateViewModel();
        var user = CreateClientUser(profileId);
        var appointments = CreateMixedAppointments();

        _authServiceMock.Setup(a => a.CurrentRole).Returns("Client");
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        _apiServiceMock.Setup(a => a.GetAppointmentsByClientAsync(profileId)).ReturnsAsync(appointments);

        // Act
        await vm.LoadAppointmentsCommand.ExecuteAsync(null);

        // Assert — upcoming should only be future + scheduled
        Assert.All(vm.UpcomingAppointments, a =>
        {
            Assert.True(a.StartTime > DateTime.UtcNow);
            Assert.Equal(AppointmentStatus.Scheduled, a.Status);
        });

        // Past should contain completed and cancelled items
        Assert.Contains(vm.PastAppointments, a => a.Status == AppointmentStatus.Completed);
        Assert.Contains(vm.PastAppointments, a => a.Status == AppointmentStatus.Cancelled);
    }

    [Fact]
    public async Task LoadAppointments_OnError_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        _authServiceMock.Setup(a => a.CurrentRole).Returns("Client");
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ThrowsAsync(new Exception("API down"));

        // Act
        await vm.LoadAppointmentsCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("API down", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.UpcomingAppointments);
        Assert.Empty(vm.PastAppointments);
    }

    [Fact]
    public void ShowPastCommand_SetsShowHistoryTrue()
    {
        // Arrange
        var vm = CreateViewModel();
        Assert.False(vm.ShowHistory);

        // Act
        vm.ShowPastCommand.Execute(null);

        // Assert
        Assert.True(vm.ShowHistory);
    }

    [Fact]
    public void ShowUpcomingCommand_SetsShowHistoryFalse()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ShowPastCommand.Execute(null); // first set to true
        Assert.True(vm.ShowHistory);

        // Act
        vm.ShowUpcomingCommand.Execute(null);

        // Assert
        Assert.False(vm.ShowHistory);
    }

    [Fact]
    public async Task LoadAppointments_AdminRole_CallsGetAllAppointments()
    {
        // Arrange
        var vm = CreateViewModel();
        var user = new UserDto
        {
            Id = Guid.NewGuid(), Email = "admin@test.com",
            FirstName = "Admin", LastName = "User",
            Role = UserRole.Admin, IsActive = true
        };

        _authServiceMock.Setup(a => a.CurrentRole).Returns("Admin");
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(user);
        _apiServiceMock.Setup(a => a.GetAllAppointmentsAsync()).ReturnsAsync([]);

        // Act
        await vm.LoadAppointmentsCommand.ExecuteAsync(null);

        // Assert
        _apiServiceMock.Verify(a => a.GetAllAppointmentsAsync(), Times.Once);
        _apiServiceMock.Verify(a => a.GetAppointmentsByClientAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoadAppointments_NullUser_ReturnsEarly()
    {
        // Arrange
        var vm = CreateViewModel();
        _apiServiceMock.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync((UserDto?)null);

        // Act
        await vm.LoadAppointmentsCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(vm.UpcomingAppointments);
        Assert.Empty(vm.PastAppointments);
        Assert.False(vm.IsBusy);
    }
}
#endif
