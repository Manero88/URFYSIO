#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Models;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.Tests.ViewModels;

/// <summary>
/// The admin's approval queue has to show BOTH registration-form submissions and inactive
/// SSO sign-ups. Only the former was ever listed, which left anyone who signed in with
/// Google invisible to the admin and permanently stuck on "pending admin approval".
/// </summary>
public class AdminDashboardViewModelTests
{
    private readonly Mock<IApiService> _api = new();

    private static readonly Guid AdminId = Guid.NewGuid();

    private AdminDashboardViewModel CreateViewModel() => new(_api.Object);

    private static UserDto AdminUser() => new()
    {
        Id = AdminId, Email = "admin@urfysio.nl", FirstName = "Ad", LastName = "Min",
        Role = UserRole.Admin, IsActive = true, AuthProvider = "email"
    };

    private static UserDto InactiveGoogleUser(string first = "Karin", string last = "Jansen") => new()
    {
        Id = Guid.NewGuid(), Email = "karin@gmail.com", FirstName = first, LastName = last,
        Role = UserRole.Client, IsActive = false, AuthProvider = "google",
        CreatedAt = DateTime.UtcNow.AddHours(-1)
    };

    private static RegistrationRequestDto PendingRegistration() => new()
    {
        Id = Guid.NewGuid(), FirstName = "Piet", LastName = "de Vries",
        Email = "piet@example.com", Message = "Graag een afspraak",
        Status = RegistrationStatus.Pending, CreatedAt = DateTime.UtcNow.AddHours(-2)
    };

    private void SetupApi(List<UserDto> users, List<RegistrationRequestDto> registrations)
    {
        _api.Setup(a => a.GetCurrentUserAsync()).ReturnsAsync(AdminUser());
        _api.Setup(a => a.GetUsersAsync()).ReturnsAsync(users);
        _api.Setup(a => a.GetAllAppointmentsAsync()).ReturnsAsync([]);
        _api.Setup(a => a.GetRegistrationRequestsAsync(It.IsAny<RegistrationStatus?>()))
            .ReturnsAsync(registrations);
    }

    [Fact]
    public async Task LoadData_ListsBothRegistrationsAndInactiveSsoUsers()
    {
        var inactive = InactiveGoogleUser();
        var registration = PendingRegistration();
        SetupApi([AdminUser(), inactive], [registration]);

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.PendingApprovals.Count);
        Assert.Contains(vm.PendingApprovals, i => i.IsRegistration && i.Email == registration.Email);
        Assert.Contains(vm.PendingApprovals, i => !i.IsRegistration && i.Email == inactive.Email);
    }

    [Fact]
    public async Task LoadData_ShowsHowEachPersonSignedUp()
    {
        SetupApi([AdminUser(), InactiveGoogleUser()], [PendingRegistration()]);

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);

        Assert.Contains(vm.PendingApprovals, i => i.SourceLabel == "Registration form");
        Assert.Contains(vm.PendingApprovals, i => i.SourceLabel == "Google");
    }

    [Fact]
    public async Task LoadData_ActiveUsersAreNotQueuedForApproval()
    {
        var active = new UserDto
        {
            Id = Guid.NewGuid(), Email = "active@example.com", FirstName = "A", LastName = "B",
            Role = UserRole.Client, IsActive = true, AuthProvider = "google"
        };
        SetupApi([AdminUser(), active], []);

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);

        Assert.Empty(vm.PendingApprovals);
    }

    [Fact]
    public async Task LoadData_ExcludesTheAdminsOwnAccount()
    {
        // A deactivated admin should never see themselves queued for their own approval.
        var meButInactive = AdminUser();
        meButInactive.IsActive = false;
        SetupApi([meButInactive], []);

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.PendingApprovals, i => i.User?.Id == AdminId);
    }

    [Fact]
    public async Task LoadData_PendingCountCoversBothKinds()
    {
        SetupApi([AdminUser(), InactiveGoogleUser()], [PendingRegistration()]);

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.PendingRegistrations);
    }

    [Fact]
    public async Task ApprovePending_OnSsoUser_ActivatesRatherThanApprovingARegistration()
    {
        var inactive = InactiveGoogleUser();
        SetupApi([AdminUser(), inactive], []);
        _api.Setup(a => a.ActivateUserAsync(inactive.Id)).ReturnsAsync((true, "User activated."));

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);
        await vm.ApprovePendingCommand.ExecuteAsync(vm.PendingApprovals.Single());

        _api.Verify(a => a.ActivateUserAsync(inactive.Id), Times.Once);
        _api.Verify(a => a.ApproveRegistrationAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ApprovePending_OnRegistration_UsesTheRegistrationApprovalFlow()
    {
        var registration = PendingRegistration();
        SetupApi([AdminUser()], [registration]);
        _api.Setup(a => a.ApproveRegistrationAsync(registration.Id)).ReturnsAsync((true, "Approved"));

        var vm = CreateViewModel();
        await vm.LoadDataCommand.ExecuteAsync(null);
        await vm.ApprovePendingCommand.ExecuteAsync(vm.PendingApprovals.Single());

        _api.Verify(a => a.ApproveRegistrationAsync(registration.Id), Times.Once);
        _api.Verify(a => a.ActivateUserAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ActivateUser_AlreadyActive_ReportsAnErrorAndDoesNotCallTheApi()
    {
        var active = new UserDto
        {
            Id = Guid.NewGuid(), Email = "a@b.com", FirstName = "A", LastName = "B",
            Role = UserRole.Client, IsActive = true, AuthProvider = "google"
        };

        var vm = CreateViewModel();
        await vm.ActivateUserCommand.ExecuteAsync(active);

        Assert.NotNull(vm.ErrorMessage);
        _api.Verify(a => a.ActivateUserAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ---- PendingApprovalItem projection ----

    [Fact]
    public void PendingApprovalItem_FromUser_FallsBackToEmailWhenNameIsBlank()
    {
        var nameless = InactiveGoogleUser(string.Empty, string.Empty);
        var item = PendingApprovalItem.FromUser(nameless);

        // Better to show the email than render an unidentifiable blank row.
        Assert.Equal(nameless.Email, item.DisplayName);
    }

    [Fact]
    public void PendingApprovalItem_ApproveLabel_DistinguishesTheTwoActions()
    {
        Assert.Equal("Approve", PendingApprovalItem.FromRegistration(PendingRegistration()).ApproveLabel);
        Assert.Equal("Activate", PendingApprovalItem.FromUser(InactiveGoogleUser()).ApproveLabel);
    }

    // ---- Type-to-confirm delete matching (FIX 4) ----

    [Theory]
    [InlineData("Karin Jansen", "Karin Jansen", true)]
    [InlineData("karin jansen", "Karin Jansen", true)]   // case shouldn't block a delete
    [InlineData("  Karin   Jansen  ", "Karin Jansen", true)] // stray whitespace shouldn't either
    [InlineData("Karin", "Karin", true)]                  // single-name account (was undeletable)
    [InlineData("Karin", "Karin Jansen", false)]          // still has to be the right name
    [InlineData("", "Karin Jansen", false)]
    [InlineData("   ", "Karin Jansen", false)]
    public void NamesMatch_AcceptsOnlyTheRightNameButToleratesFormatting(
        string typed, string expected, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, AdminDashboardViewModel.NamesMatch(typed, expected));
    }
}
#endif
