using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// Booking has two distinct flows depending on who's logged in:
///
///   • Client:  pick a physio → see that physio's slots → book for yourself.
///   • Physio/Admin: pick a client (searchable) → see YOUR OWN slots → book for that client.
///
/// The physio flow exists because reception staff / the treating physio themselves need to
/// create appointments on behalf of clients (phone bookings, walk-ins). They don't book with
/// a *different* physio — the slots shown are always the logged-in user's own availability.
/// Admins don't have a PhysiotherapistProfile so the flow short-circuits with a clear message
/// if an admin tries to book (admins should promote a physio, not book through the admin seat).
/// </summary>
public partial class BookAppointmentViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly IAuthService _authService;

    // --- Client flow: pick a physio to book with ---
    public ObservableCollection<UserDto> Physiotherapists { get; } = [];
    public ObservableCollection<AvailabilitySlotDto> AvailableSlots { get; } = [];

    [ObservableProperty] private UserDto? _selectedPhysiotherapist;
    [ObservableProperty] private AvailabilitySlotDto? _selectedSlot;

    // --- Physio/Admin flow: pick a client to book for ---
    // ClientList holds the full list from the API; FilteredClients is the search-narrowed
    // subset bound to the ClientPickerView. The split keeps the filter cheap — we don't
    // re-fetch from the API when the user types.
    public ObservableCollection<UserDto> ClientList { get; } = [];
    public ObservableCollection<UserDto> FilteredClients { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientSelected))]
    private UserDto? _selectedClient;

    [ObservableProperty] private string _clientSearchText = string.Empty;

    public bool IsClientSelected => SelectedClient is not null;

    // Who's logged in — drives which UI section is visible.
    [ObservableProperty] private bool _isClient;
    [ObservableProperty] private bool _isPhysioOrAdmin;

    public BookAppointmentViewModel(IApiService apiService, IAuthService authService)
    {
        _apiService = apiService;
        _authService = authService;
        Title = "Book Appointment";
    }

    // -------- Shared init --------

    /// <summary>
    /// Entry point for the page's OnAppearing. Figures out the role from the DB (not the
    /// cached JWT role — that may be stale) and loads the right list (physios vs clients).
    /// </summary>
    [RelayCommand]
    private async Task LoadInitialDataAsync()
    {
        if (!SetBusy()) return;
        try
        {
            // Reset everything so navigating back to this page doesn't show a stale selection.
            Physiotherapists.Clear();
            AvailableSlots.Clear();
            ClientList.Clear();
            FilteredClients.Clear();
            SelectedPhysiotherapist = null;
            SelectedSlot = null;
            SelectedClient = null;
            ClientSearchText = string.Empty;

            var me = await _apiService.GetCurrentUserAsync();
            if (me is null)
            {
                SetError("Could not load your profile. Please log in again.");
                return;
            }

            IsClient = me.Role == Shared.Enums.UserRole.Client;
            IsPhysioOrAdmin = me.Role is Shared.Enums.UserRole.Physiotherapist
                                      or Shared.Enums.UserRole.Admin;

            if (IsPhysioOrAdmin)
            {
                // Admins can't own slots (no PhysiotherapistProfile) — tell them up front
                // rather than letting them pick a client and fail at the book step.
                if (me.Role == Shared.Enums.UserRole.Admin || !me.ProfileId.HasValue)
                {
                    SetError("Admins cannot book appointments — ask the physiotherapist to book via their own login.");
                    return;
                }

                var clients = await _apiService.GetClientsAsync();
                foreach (var c in clients)
                    ClientList.Add(c);
                ApplyClientFilter();

                // Pre-load the logged-in physio's own slots so the "for:" search is the
                // only thing still to do — the slot list is already there.
                var slots = await _apiService.GetAvailableSlotsAsync(me.ProfileId.Value);
                foreach (var s in slots.Where(s => s.StartTime > DateTime.UtcNow))
                    AvailableSlots.Add(s);
            }
            else // Client flow
            {
                var physios = await _apiService.GetPhysiotherapistsAsync();
                foreach (var p in physios)
                    Physiotherapists.Add(p);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to load booking data: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // -------- Client flow --------

    partial void OnSelectedPhysiotherapistChanged(UserDto? value)
    {
        AvailableSlots.Clear();
        SelectedSlot = null;
        ErrorMessage = null;
        SuccessMessage = null;

        if (value?.ProfileId is not null)
            _ = LoadSlotsForPhysioAsync(value.ProfileId.Value);
    }

    private async Task LoadSlotsForPhysioAsync(Guid physioProfileId)
    {
        if (!SetBusy()) return;
        try
        {
            var slots = await _apiService.GetAvailableSlotsAsync(physioProfileId);
            foreach (var s in slots.Where(s => s.StartTime > DateTime.UtcNow))
                AvailableSlots.Add(s);
        }
        catch (Exception ex)
        {
            SetError($"Failed to load slots: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // -------- Physio/Admin flow: client picker (searchable) --------

    partial void OnClientSearchTextChanged(string value) => ApplyClientFilter();

    private void ApplyClientFilter()
    {
        FilteredClients.Clear();
        IEnumerable<UserDto> source = ClientList;
        var q = ClientSearchText?.Trim();
        if (!string.IsNullOrEmpty(q))
        {
            source = source.Where(c =>
                c.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        // Cap at 50 to keep the list scannable even on fresh load.
        foreach (var c in source.OrderBy(c => c.LastName).ThenBy(c => c.FirstName).Take(50))
            FilteredClients.Add(c);
    }

    [RelayCommand]
    private void SelectClient(UserDto client)
    {
        SelectedClient = client;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private void ClearClientSelection()
    {
        SelectedClient = null;
        ClientSearchText = string.Empty;
        ApplyClientFilter();
    }

    // -------- Book --------

    [RelayCommand]
    private async Task BookSelectedSlotAsync()
    {
        if (SelectedSlot is null)
        {
            SetError("Please select a time slot.");
            return;
        }

        if (SelectedSlot.StartTime <= DateTime.UtcNow)
        {
            SetError("This slot is in the past. Please select a future time slot.");
            return;
        }

        if (IsPhysioOrAdmin)
        {
            if (SelectedClient is null)
            {
                SetError("Please select a client first.");
                return;
            }
            if (!SelectedClient.ProfileId.HasValue)
            {
                SetError("The selected client is missing a client profile. Ask an admin to run the profile backfill.");
                return;
            }
        }
        else // Client flow
        {
            if (SelectedPhysiotherapist is null)
            {
                SetError("Please select a physiotherapist first.");
                return;
            }
        }

        if (!SetBusy()) return;
        try
        {
            var me = await _apiService.GetCurrentUserAsync();
            if (me is null) { SetError("Not logged in."); return; }

            Guid clientProfileId;
            if (IsPhysioOrAdmin)
            {
                clientProfileId = SelectedClient!.ProfileId!.Value;
            }
            else
            {
                if (!me.ProfileId.HasValue) { SetError("Client profile not found."); return; }
                clientProfileId = me.ProfileId.Value;
            }

            var dto = new CreateAppointmentDto
            {
                ClientProfileId = clientProfileId,
                PhysiotherapistProfileId = SelectedSlot.PhysiotherapistProfileId,
                AvailabilitySlotId = SelectedSlot.Id
            };

            var result = await _apiService.CreateAppointmentAsync(dto);
            if (result is not null)
            {
                var forWhom = IsPhysioOrAdmin ? $" for {SelectedClient!.FullName}" : "";
                await Shell.Current.DisplayAlertAsync("Success", $"Appointment booked{forWhom}!", "OK");
                await Shell.Current.GoToAsync("..");
            }
            else
            {
                SetError("Failed to book appointment. The slot may no longer be available.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to book appointment: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ConfirmBookingAsync(AvailabilitySlotDto slot)
    {
        SelectedSlot = slot;
        await BookSelectedSlotAsync();
    }
}
