using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.App.ViewModels;

public partial class AppointmentsViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly IAuthService _authService;
    private readonly IAppointmentReminderService _reminderService;

    // Preference key: the appointment-reminder rationale is shown to a client once,
    // before the first OS notification-permission prompt.
    private const string ReminderRationaleShownKey = "reminder_rationale_shown";

    public ObservableCollection<AppointmentDto> UpcomingAppointments { get; } = [];
    public ObservableCollection<AppointmentDto> PastAppointments { get; } = [];

    [ObservableProperty] private bool _showHistory;

    // Drives the "contact your physiotherapist" notice — clients see it, physios/admins
    // don't (they're the ones who'd be contacted). Set from the DTO role on every load
    // so a stale JWT role can't surface the wrong UI for an admin-flipped account.
    [ObservableProperty] private bool _isClient;

    // --- Admin filter (FEATURE: admin oversees all appointments) ---
    // Admins see every appointment by default ("All") and can narrow to a single
    // physiotherapist's or a single client's appointments. The pickers reuse the
    // existing per-physio / per-client API endpoints, which already allow Admin.
    public const string FilterAll = "All";
    public const string FilterByPhysio = "By Physiotherapist";
    public const string FilterByClient = "By Client";

    public IReadOnlyList<string> FilterModes { get; } = [FilterAll, FilterByPhysio, FilterByClient];

    public ObservableCollection<UserDto> FilterPhysios { get; } = [];
    public ObservableCollection<UserDto> FilterClients { get; } = [];

    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _selectedFilterMode = FilterAll;
    [ObservableProperty] private UserDto? _filterPhysio;
    [ObservableProperty] private UserDto? _filterClient;

    public bool ShowPhysioFilter => IsAdmin && SelectedFilterMode == FilterByPhysio;
    public bool ShowClientFilter => IsAdmin && SelectedFilterMode == FilterByClient;

    partial void OnIsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPhysioFilter));
        OnPropertyChanged(nameof(ShowClientFilter));
    }

    partial void OnSelectedFilterModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPhysioFilter));
        OnPropertyChanged(nameof(ShowClientFilter));
        if (IsAdmin) LoadAppointmentsCommand.Execute(null);
    }

    partial void OnFilterPhysioChanged(UserDto? value)
    {
        if (IsAdmin) LoadAppointmentsCommand.Execute(null);
    }

    partial void OnFilterClientChanged(UserDto? value)
    {
        if (IsAdmin) LoadAppointmentsCommand.Execute(null);
    }

    public AppointmentsViewModel(IApiService apiService, IAuthService authService,
        IAppointmentReminderService reminderService)
    {
        _apiService = apiService;
        _authService = authService;
        _reminderService = reminderService;
        Title = "Appointments";

        // Seed IsClient from the login-time cached role IMMEDIATELY so the
        // "contact your physiotherapist" notice is part of the page's very first
        // layout pass for clients. Previously IsClient started false and only
        // flipped true after the /api/users/me round-trip — and if that call
        // failed (cold Azure SQL, transient network), it never flipped at all,
        // so clients never saw the notice. The API load below still refines this
        // from DB truth (handles admin-changed roles), but the cached value gives
        // a correct default for the overwhelmingly common case.
        IsClient = string.Equals(_authService.CurrentRole, "Client", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task LoadAppointmentsAsync()
    {
        if (!SetBusy()) return;
        try
        {
            await LoadAppointmentsCoreAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load appointments: {ex.Message}";
        }
        finally
        {
            ClearBusy();
        }
    }

    // Does the actual work without managing IsBusy. Callable from RelayCommand wrappers
    // AND from other flows (CancelAppointmentAsync) that are already inside a busy block,
    // without silently early-returning because IsBusy is already true.
    private async Task LoadAppointmentsCoreAsync()
    {
        UpcomingAppointments.Clear();
        PastAppointments.Clear();

        var user = await _apiService.GetCurrentUserAsync();
        if (user is null) return;

        IsClient = user.Role == UserRole.Client;
        IsAdmin = user.Role == UserRole.Admin;

        // Use the DTO role (DB truth), not _authService.CurrentRole (login-time cache).
        List<AppointmentDto> list;
        if (user.Role == UserRole.Client)
        {
            list = user.ProfileId.HasValue ? await _apiService.GetAppointmentsByClientAsync(user.ProfileId.Value) : [];
        }
        else if (user.Role == UserRole.Physiotherapist)
        {
            list = user.ProfileId.HasValue ? await _apiService.GetAppointmentsByPhysioAsync(user.ProfileId.Value) : [];
        }
        else // Admin — apply the management filter
        {
            // Lazy-load the picker lists once; they only change when users are added,
            // and a pull-to-refresh re-enters here anyway if the admin wants fresh data.
            if (FilterPhysios.Count == 0)
            {
                foreach (var p in await _apiService.GetPhysiotherapistsAsync())
                    FilterPhysios.Add(p);
            }
            if (FilterClients.Count == 0)
            {
                foreach (var c in await _apiService.GetClientsAsync())
                    FilterClients.Add(c);
            }

            if (SelectedFilterMode == FilterByPhysio)
                list = FilterPhysio?.ProfileId is Guid pid ? await _apiService.GetAppointmentsByPhysioAsync(pid) : [];
            else if (SelectedFilterMode == FilterByClient)
                list = FilterClient?.ProfileId is Guid cid ? await _apiService.GetAppointmentsByClientAsync(cid) : [];
            else
                list = await _apiService.GetAllAppointmentsAsync();
        }

        var now = DateTime.UtcNow;
        foreach (var a in list)
        {
            if (a.StartTime > now && a.Status == AppointmentStatus.Scheduled)
                UpcomingAppointments.Add(a);
            else
                PastAppointments.Add(a);
        }

        // User Story 8: (re)schedule local reminders for the client's own upcoming
        // appointments. Only clients get reminders; physios/admins viewing others'
        // calendars must not. Best-effort and self-reconciling (cancels stale ones).
        if (IsClient)
            await TryScheduleRemindersAsync();
    }

    // Requests notification permission (once, behind a one-time rationale) and hands the
    // current upcoming set to the reminder service to schedule. Fully guarded: a denied
    // permission or any scheduling failure leaves the appointments screen working normally.
    private async Task TryScheduleRemindersAsync()
    {
        try
        {
            if (!Preferences.Get(ReminderRationaleShownKey, false))
            {
                await Shell.Current.DisplayAlertAsync(
                    "Appointment reminders",
                    "URFYSIO can remind you 24 hours before each physiotherapy appointment " +
                    "so you don't miss a session. You'll be asked to allow notifications next.",
                    "OK");
                Preferences.Set(ReminderRationaleShownKey, true);
            }

            if (!await _reminderService.EnsurePermissionAsync())
                return; // denied — app keeps working, just no reminders

            await _reminderService.SyncRemindersAsync(UpcomingAppointments);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Reminders] schedule failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called by <c>AppointmentsPage.OnAppearing</c>. Resets tab filter + error/success
    /// state so that navigating back to this page never shows stale UI, then reloads.
    /// </summary>
    [RelayCommand]
    private async Task RefreshOnAppearAsync()
    {
        ShowHistory = false;
        ErrorMessage = null;
        SuccessMessage = null;
        await LoadAppointmentsAsync();
    }

    [RelayCommand]
    private void ShowUpcoming()
    {
        ShowHistory = false;
    }

    [RelayCommand]
    private void ShowPast()
    {
        ShowHistory = true;
    }

    [RelayCommand]
    private async Task CancelAppointmentAsync(AppointmentDto appointment)
    {
        bool confirm = await Shell.Current.DisplayAlertAsync("Cancel", "Cancel this appointment?", "Yes", "No");
        if (!confirm) return;

        try
        {
            var success = await _apiService.CancelAppointmentAsync(appointment.Id);
            if (success)
            {
                // Drop this appointment's reminder right away; the reload below also
                // re-syncs the full set (rescheduled/removed ones fall out automatically).
                await _reminderService.CancelReminderAsync(appointment.Id);
                await LoadAppointmentsCoreAsync();
                SetSuccess("Appointment cancelled.");
            }
            else
            {
                SetError("Failed to cancel appointment.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to cancel appointment: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RescheduleAppointmentAsync(AppointmentDto appointment)
    {
        await Shell.Current.GoToAsync($"reschedule?appointmentId={appointment.Id}");
    }

    [RelayCommand]
    private async Task BookAppointmentAsync()
    {
        await Shell.Current.GoToAsync("book");
    }
}
