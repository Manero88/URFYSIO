using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Availability;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.ViewModels;

public partial class AvailabilityViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly IAuthService _authService;

    // --- Admin: manage availability ON BEHALF OF a physiotherapist ---
    // A physio manages their own calendar; an admin first picks WHOSE calendar to
    // manage. All loads/creates below resolve the "target" profile id accordingly.
    // The API enforces the same rule server-side (admin: any; physio: own only).
    public ObservableCollection<UserDto> Physiotherapists { get; } = [];

    [ObservableProperty] private bool _isAdmin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManagementUi))]
    private UserDto? _selectedPhysiotherapist;

    // The slot form + lists only make sense once we know whose calendar this is:
    // always for a physio, after picking one for an admin.
    public bool ShowManagementUi => !IsAdmin || SelectedPhysiotherapist?.ProfileId is not null;

    partial void OnIsAdminChanged(bool value) => OnPropertyChanged(nameof(ShowManagementUi));

    partial void OnSelectedPhysiotherapistChanged(UserDto? value)
    {
        if (!IsAdmin) return;
        // Abandon any in-progress edit — it belonged to the previous physio's slot.
        CancelEdit();
        LoadSlotsCommand.Execute(null);
    }

    // Slots are split into two buckets for the UI:
    //   • UpcomingSlots — future slots, fully editable.
    //   • PastSlots — slots whose window has passed. The API already drops past
    //     UNBOOKED slots (see AvailabilityService), so this only ever contains past
    //     booked slots — i.e. appointment history. Rendered read-only.
    public ObservableCollection<AvailabilitySlotDto> UpcomingSlots { get; } = [];
    public ObservableCollection<AvailabilitySlotDto> PastSlots { get; } = [];

    public bool HasPastSlots => PastSlots.Count > 0;

    // Slot duration is fixed at 30 minutes. The physio only picks a start time;
    // the end time is automatically computed and shown as a read-only label.
    private static readonly TimeSpan SlotDuration = TimeSpan.FromMinutes(30);

    // Start-time picker options. Replaces the old per-minute TimePicker, which was
    // unusable. Work hours 08:00–20:00, half-hour granularity — which also guarantees
    // the 30-minute slot rule without extra validation.
    public IReadOnlyList<string> HourOptions { get; } =
        Enumerable.Range(8, 13).Select(h => h.ToString("D2")).ToList(); // "08".."20"
    public IReadOnlyList<string> MinuteOptions { get; } = ["00", "30"];

    [ObservableProperty] private DateTime _newSlotDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private string _selectedHour = "09";
    [ObservableProperty] private string _selectedMinute = "00";
    [ObservableProperty] private TimeSpan _newSlotEndTime = new(9, 30, 0);
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private AvailabilitySlotDto? _editingSlot;

    partial void OnSelectedHourChanged(string value) => RecomputeEndTime();
    partial void OnSelectedMinuteChanged(string value) => RecomputeEndTime();

    private void RecomputeEndTime() => NewSlotEndTime = GetStartTimeSpan() + SlotDuration;

    // Null-safe parse of the two dropdown values into a start TimeSpan.
    private TimeSpan GetStartTimeSpan()
    {
        var h = int.TryParse(SelectedHour, out var hh) ? hh : 9;
        var m = int.TryParse(SelectedMinute, out var mm) ? mm : 0;
        return new TimeSpan(h, m, 0);
    }

    public AvailabilityViewModel(IApiService apiService, IAuthService authService)
    {
        _apiService = apiService;
        _authService = authService;
        Title = "Availability";
        PastSlots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPastSlots));
    }

    private bool ValidateSlotTimes()
    {
        NewSlotEndTime = GetStartTimeSpan() + SlotDuration;

        var startTime = NewSlotDate.Date + GetStartTimeSpan();

        if (startTime <= DateTime.Now)
        {
            SetError("Slot must be in the future.");
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task LoadSlotsAsync()
    {
        if (!SetBusy()) return;
        try
        {
            await LoadSlotsCoreAsync();
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

    // Does the actual work without touching IsBusy, so callers that are already inside a
    // SetBusy/ClearBusy block (AddSlotAsync, SaveEditAsync, DeleteSlotAsync) can refresh
    // the slot list after their operation.
    private async Task LoadSlotsCoreAsync()
    {
        UpcomingSlots.Clear();
        PastSlots.Clear();

        var user = await _apiService.GetCurrentUserAsync();
        if (user is null) return;

        IsAdmin = user.Role == Shared.Enums.UserRole.Admin;

        // Admin needs the physio picker populated before anything can load.
        if (IsAdmin && Physiotherapists.Count == 0)
        {
            foreach (var p in await _apiService.GetPhysiotherapistsAsync())
                Physiotherapists.Add(p);
        }

        // Whose calendar? Physio → their own; admin → the picked physio's.
        var targetProfileId = IsAdmin ? SelectedPhysiotherapist?.ProfileId : user.ProfileId;
        if (!targetProfileId.HasValue) return; // admin hasn't picked yet

        var list = await _apiService.GetPhysioSlotsAsync(targetProfileId.Value);
        var now = DateTime.UtcNow;
        foreach (var s in list)
        {
            if (s.EndTime > now)
                UpcomingSlots.Add(s);
            else
                PastSlots.Add(s);
        }
    }

    [RelayCommand]
    private async Task AddSlotAsync()
    {
        if (!ValidateSlotTimes()) return;
        if (!SetBusy()) return;
        try
        {
            var user = await _apiService.GetCurrentUserAsync();
            if (user is null) { SetError("Could not load current user."); return; }

            // Resolve whose calendar the new slot belongs to. Physios create on their
            // own; admins create on behalf of the selected physiotherapist. The API
            // enforces the same ownership rule server-side.
            Guid? targetProfileId;
            if (user.Role == Shared.Enums.UserRole.Physiotherapist)
            {
                targetProfileId = user.ProfileId;
                if (!targetProfileId.HasValue)
                {
                    SetError("Physiotherapist profile not found. Please sign out and in again, or contact an admin.");
                    return;
                }
            }
            else if (user.Role == Shared.Enums.UserRole.Admin)
            {
                targetProfileId = SelectedPhysiotherapist?.ProfileId;
                if (!targetProfileId.HasValue)
                {
                    SetError("Select a physiotherapist first.");
                    return;
                }
            }
            else
            {
                SetError($"Only physiotherapists or admins can create slots (your role is {user.Role}).");
                return;
            }

            var dto = new CreateAvailabilitySlotDto
            {
                PhysiotherapistProfileId = targetProfileId.Value,
                StartTime = NewSlotDate.Date + GetStartTimeSpan(),
                EndTime = NewSlotDate.Date + NewSlotEndTime
            };

            Debug.WriteLine($"[AvailabilityViewModel] POST api/availability " +
                            $"PhysiotherapistProfileId={dto.PhysiotherapistProfileId} " +
                            $"Start={dto.StartTime:o} End={dto.EndTime:o} " +
                            $"(user.Id={user.Id}, user.Role={user.Role})");

            var (slot, error) = await _apiService.CreateAvailabilitySlotAsync(dto);
            if (slot is not null)
            {
                await LoadSlotsCoreAsync();
                SetSuccess("Availability slot created.");
            }
            else
            {
                SetError(string.IsNullOrWhiteSpace(error)
                    ? "Failed to create slot. Check for overlapping times."
                    : error);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to create slot: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void EditSlot(AvailabilitySlotDto slot)
    {
        if (slot.IsBooked)
        {
            SetError("Cannot edit a booked slot.");
            return;
        }

        EditingSlot = slot;
        IsEditing = true;
        NewSlotDate = slot.StartTime.Date;
        // Snap the slot's start time onto the dropdown options. Slots created through
        // this app are always on a half-hour boundary, but snap defensively so a
        // legacy slot with an odd minute doesn't leave the picker blank.
        SelectedHour = Math.Clamp(slot.StartTime.Hour, 8, 20).ToString("D2");
        SelectedMinute = slot.StartTime.Minute >= 30 ? "30" : "00";
        RecomputeEndTime();
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (EditingSlot is null) return;
        if (!ValidateSlotTimes()) return;
        if (!SetBusy()) return;
        try
        {
            var dto = new UpdateAvailabilitySlotDto
            {
                StartTime = NewSlotDate.Date + GetStartTimeSpan(),
                EndTime = NewSlotDate.Date + NewSlotEndTime
            };

            var result = await _apiService.UpdateAvailabilitySlotAsync(EditingSlot.Id, dto);
            if (result is not null)
            {
                CancelEdit();
                await LoadSlotsCoreAsync();
                SetSuccess("Slot updated successfully.");
            }
            else
            {
                SetError("Failed to update slot. Check for overlapping times.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to update slot: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditingSlot = null;
        NewSlotDate = DateTime.Today.AddDays(1);
        SelectedHour = "09";
        SelectedMinute = "00";
        RecomputeEndTime();
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task DeleteSlotAsync(AvailabilitySlotDto slot)
    {
        if (slot.IsBooked)
        {
            await Shell.Current.DisplayAlertAsync("Error", "Cannot delete a booked slot.", "OK");
            return;
        }

        var confirm = await Shell.Current.DisplayAlertAsync("Delete", "Delete this availability slot?", "Yes", "No");
        if (!confirm) return;

        try
        {
            var success = await _apiService.DeleteAvailabilitySlotAsync(slot.Id);
            if (success)
            {
                await LoadSlotsCoreAsync();
                SetSuccess("Slot deleted.");
            }
            else
            {
                SetError("Failed to delete slot.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to delete slot: {ex.Message}");
        }
    }
}
