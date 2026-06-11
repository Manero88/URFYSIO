using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.TreatmentPlans;

namespace URFYSIO.App.ViewModels;

public partial class DashboardViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly IAuthService _authService;

    [ObservableProperty] private string _welcomeMessage = string.Empty;
    [ObservableProperty] private string _roleBadge = string.Empty;

    public ObservableCollection<AppointmentDto> UpcomingAppointments { get; } = [];

    // --- Client-only: most recent active treatment plan ---
    // Only set when the dashboard is loading for a Client user. Physio and admin
    // dashboards skip this entirely. We expose a separate ObservableCollection of
    // first-3 entries (rather than binding directly to ActivePlan.Entries) so the
    // dashboard "preview" stays cheap to render regardless of how many entries the
    // plan has — and so the empty-state binding (HasActivePlan) is a clean bool.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePlan))]
    [NotifyPropertyChangedFor(nameof(ShowNoPlanMessage))]
    private TreatmentPlanDto? _activePlan;

    [ObservableProperty] private string _activePlanTitle = string.Empty;
    public ObservableCollection<TreatmentPlanEntryDto> ActivePlanEntries { get; } = [];

    // Drives the dashboard's "Current Treatment Plan" section visibility. Only the
    // Client role sees this card; for physios/admin the whole region is hidden via
    // IsClient. ShowNoPlanMessage is true ONLY when the client section is visible
    // AND there is no active plan, so the "no plan yet" copy doesn't briefly flash
    // for non-clients.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoPlanMessage))]
    private bool _isClient;

    public bool HasActivePlan => ActivePlan is not null;
    public bool ShowNoPlanMessage => IsClient && ActivePlan is null;

    public DashboardViewModel(IApiService apiService, IAuthService authService)
    {
        _apiService = apiService;
        _authService = authService;
        Title = "Dashboard";
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (!SetBusy()) return;
        try
        {
            // Always fetch the CURRENT user from the API — the SecureStorage-cached role
            // set at login time may be stale if an admin changed this user's role since.
            // Using the DB role (via /api/users/me) is the only way to show the right
            // badge when the Auth0 JWT still carries the old role.
            var user = await _apiService.GetCurrentUserAsync();
            if (user is null)
            {
                ErrorMessage = "Could not load your profile. Please log in again.";
                return;
            }

            // Sync the AuthService cache too, so other screens that read CurrentRole
            // (tab routing, permission gates) see the fresh value without a re-login.
            await _authService.RefreshFromApiAsync();

            var displayName = !string.IsNullOrWhiteSpace(user.FullName) && user.FullName.Trim().Length > 0
                ? user.FullName
                : (_authService.CurrentUserName ?? "User");

            WelcomeMessage = $"Welcome, {displayName}!";
            RoleBadge = user.Role.ToString();
            IsClient = user.Role == Shared.Enums.UserRole.Client;

            UpcomingAppointments.Clear();
            ActivePlan = null;
            ActivePlanTitle = string.Empty;
            ActivePlanEntries.Clear();

            List<AppointmentDto> appointments;

            if (user.Role == Shared.Enums.UserRole.Client)
            {
                appointments = user.ProfileId.HasValue
                    ? await _apiService.GetAppointmentsByClientAsync(user.ProfileId.Value)
                    : [];

                // Load treatment plans only for clients — the dashboard card is
                // client-specific. We pull all plans then filter to "active" client-side
                // because the existing API doesn't accept a status filter and a
                // dedicated endpoint would be a one-line case for a single screen.
                if (user.ProfileId.HasValue)
                {
                    var plans = await _apiService.GetTreatmentPlansByClientAsync(user.ProfileId.Value);
                    var active = plans
                        .Where(p => !p.IsCompleted)
                        .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                        .FirstOrDefault();
                    if (active is not null)
                    {
                        ActivePlan = active;
                        ActivePlanTitle = active.Title;
                        foreach (var e in active.Entries.OrderBy(e => e.OrderIndex).Take(3))
                            ActivePlanEntries.Add(e);
                    }
                }
            }
            else if (user.Role == Shared.Enums.UserRole.Physiotherapist)
            {
                appointments = user.ProfileId.HasValue
                    ? await _apiService.GetAppointmentsByPhysioAsync(user.ProfileId.Value)
                    : [];
            }
            else
            {
                appointments = await _apiService.GetAllAppointmentsAsync();
            }

            // "Upcoming" means strictly in the future. The API endpoints return ALL
            // appointments for a user (client/physio/admin) — they don't filter by
            // date — so a Scheduled appointment whose StartTime is yesterday would
            // otherwise leak into this list. Filter client-side here AND order
            // ascending so the nearest future appointment is on top.
            var now = DateTime.UtcNow;
            foreach (var apt in appointments
                         .Where(a => a.Status == Shared.Enums.AppointmentStatus.Scheduled)
                         .Where(a => a.StartTime >= now)
                         .OrderBy(a => a.StartTime)
                         .Take(5))
            {
                UpcomingAppointments.Add(apt);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load dashboard: {ex.Message}";
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ViewFullPlanAsync()
    {
        // The Plans tab is the same destination whether they came here via the
        // dashboard card or the bottom-tab — Shell tab routing handles the rest.
        await Shell.Current.GoToAsync("//client/treatmentplans");
    }
}
