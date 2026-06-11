using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Availability;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// Reschedule flow: load the existing appointment by id, then show the slots that
/// belong to THE SAME PHYSIOTHERAPIST (you can't reschedule onto a different
/// physio's calendar — that would be a new booking, not a reschedule). Works for
/// every role:
///
///   • Client:  reschedules their own appointment.
///   • Physio:  reschedules an appointment they own (e.g. on behalf of a client
///              who phoned in).
///   • Admin:   reschedules any appointment.
///
/// The previous version of this VM only called <c>GetAppointmentsByClientAsync</c>
/// using the logged-in user's profile id, so a physio or admin always saw
/// "Appointment not found" and the slot list was filtered to nothing because it
/// was loading global slots without a physio filter. We now go through
/// <c>GetAppointmentByIdAsync</c> (server-side authorisation already checks the
/// caller is allowed to see this appointment) and pin the slot list to the
/// appointment's own physiotherapist.
/// </summary>
[QueryProperty(nameof(AppointmentId), "appointmentId")]
public partial class RescheduleAppointmentViewModel : BaseViewModel
{
    private readonly IApiService _apiService;

    public ObservableCollection<AvailabilitySlotDto> AvailableSlots { get; } = [];

    [ObservableProperty] private AvailabilitySlotDto? _selectedSlot;
    [ObservableProperty] private AppointmentDto? _currentAppointment;
    [ObservableProperty] private string _appointmentId = string.Empty;

    public RescheduleAppointmentViewModel(IApiService apiService)
    {
        _apiService = apiService;
        Title = "Reschedule Appointment";
    }

    partial void OnAppointmentIdChanged(string value)
    {
        if (Guid.TryParse(value, out _))
            LoadDataCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (!SetBusy()) return;
        try
        {
            ErrorMessage = null;
            SuccessMessage = null;
            CurrentAppointment = null;
            AvailableSlots.Clear();

            if (!Guid.TryParse(AppointmentId, out var id))
            {
                SetError("Invalid appointment id.");
                return;
            }

            // Fetch by id — the API endpoint authorises the caller server-side, so
            // we don't need to special-case roles here.
            var appointment = await _apiService.GetAppointmentByIdAsync(id);
            if (appointment is null)
            {
                SetError("Appointment not found.");
                return;
            }
            CurrentAppointment = appointment;

            // Slots must come from the SAME physio. Also exclude the slot the
            // appointment already occupies (rescheduling onto the same slot is a no-op
            // and the API would reject it anyway), and any slots in the past.
            var slots = await _apiService.GetAvailableSlotsAsync(appointment.PhysiotherapistProfileId);
            var now = DateTime.UtcNow;
            foreach (var s in slots
                         .Where(s => s.Id != appointment.AvailabilitySlotId)
                         .Where(s => s.StartTime > now)
                         .OrderBy(s => s.StartTime))
            {
                AvailableSlots.Add(s);
            }

            if (AvailableSlots.Count == 0)
                SetError("No other available time slots for this physiotherapist.");
        }
        catch (Exception ex)
        {
            SetError($"Failed to load data: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task RescheduleAsync()
    {
        if (SelectedSlot is null)
        {
            SetError("Please select a new time slot.");
            return;
        }

        if (CurrentAppointment?.AvailabilitySlotId == SelectedSlot.Id)
        {
            SetError("Please select a different slot than the current one.");
            return;
        }

        if (!Guid.TryParse(AppointmentId, out var id)) return;

        if (!SetBusy()) return;
        try
        {
            var result = await _apiService.RescheduleAppointmentAsync(id, SelectedSlot.Id);
            if (result is not null)
            {
                await Shell.Current.DisplayAlertAsync("Success", "Appointment rescheduled!", "OK");
                await Shell.Current.GoToAsync("..");
            }
            else
            {
                SetError("Failed to reschedule. The slot may no longer be available.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to reschedule: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }
}
