using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// Treatment plans viewmodel — two audiences and two lifecycle states:
///
///   • Client:  read-only flat list of their own plans. Can comment on entries.
///   • Physio/Admin: grouped by client. Physios can create, edit, mark complete,
///                   reopen. Admins can view oversight but not create.
///
/// Active vs History: a single boolean (<see cref="IsHistoryTab"/>) flips the
/// page between two views over the same loaded data. We keep the underlying
/// <see cref="_allPlans"/> in memory and rebuild the user-visible collections
/// (flat or grouped) any time the tab toggles or a mutation reloads the data,
/// so tab switches are local — no network round-trip needed to flip between
/// Active and History.
///
/// Client picker for the "Create New Plan" card uses the shared
/// <c>ClientPickerView</c> contract (ClientList/FilteredClients/ClientSearchText/
/// SelectedClient/IsClientSelected + SelectClient/ClearClientSelection commands).
/// </summary>
public partial class TreatmentPlanViewModel : BaseViewModel
{
    private readonly IApiService _apiService;
    private readonly IAuthService _authService;

    // Source of truth — all plans returned by the API for the current user. We
    // never mutate this collection from XAML; it feeds the visible collections
    // below depending on IsHistoryTab.
    private readonly List<TreatmentPlanWithEntriesViewModel> _allPlans = [];

    // Flat list for the client view. Filtered to either active or completed plans
    // depending on IsHistoryTab.
    public ObservableCollection<TreatmentPlanWithEntriesViewModel> Plans { get; } = [];

    // Grouped view for physio/admin — one header per client, plans nested
    // underneath. Same filtering rule as Plans.
    public ObservableCollection<ClientTreatmentPlansGroup> ClientGroups { get; } = [];

    // --- Client picker (physio/admin only) — matches the ClientPickerView contract ---
    public ObservableCollection<UserDto> ClientList { get; } = [];
    public ObservableCollection<UserDto> FilteredClients { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientSelected))]
    private UserDto? _selectedClient;

    [ObservableProperty] private string _clientSearchText = string.Empty;

    public bool IsClientSelected => SelectedClient is not null;

    [ObservableProperty] private TreatmentPlanDto? _selectedPlan;

    // --- Create plan state ---
    [ObservableProperty] private string _newPlanTitle = string.Empty;
    [ObservableProperty] private string? _newPlanDescription;

    // --- Plan editing state ---
    [ObservableProperty] private bool _isEditingPlan;
    [ObservableProperty] private TreatmentPlanDto? _editingPlan;
    [ObservableProperty] private string _editPlanTitle = string.Empty;
    [ObservableProperty] private string? _editPlanDescription;

    // --- Entry editing state ---
    [ObservableProperty] private bool _isEditingEntry;
    [ObservableProperty] private TreatmentPlanEntryDto? _editingEntry;
    [ObservableProperty] private Guid _editingEntryPlanId;
    [ObservableProperty] private string _editEntryTitle = string.Empty;
    [ObservableProperty] private string? _editEntryDescription;
    [ObservableProperty] private int _editEntryOrderIndex;
    [ObservableProperty] private bool _editEntryIsCompleted;

    // --- New entry state ---
    // Separate from the edit-entry state because a) the forms are visually distinct
    // (no "completed" checkbox, no order field — order defaults to the next slot at
    // the bottom of the list) and b) we want to keep an in-progress edit visible
    // even if the user opens the add-entry form by mistake.
    [ObservableProperty] private bool _isAddingEntry;
    [ObservableProperty] private Guid _addingEntryPlanId;
    [ObservableProperty] private string _addingEntryPlanTitle = string.Empty;
    [ObservableProperty] private string _newEntryTitle = string.Empty;
    [ObservableProperty] private string? _newEntryDescription;

    // Role-driven visibility flags. We recompute these from the DTO (DB truth) inside
    // LoadPlansAsync, so a stale JWT-cached role can't show the wrong UI. The grouped
    // list and the create form have *different* visibility rules — admins see the
    // grouped list (oversight) but cannot create plans themselves because they don't
    // own a PhysiotherapistProfile (their plans would have a null FK), so we expose
    // CanCreatePlans separately rather than reusing IsPhysioOrAdmin for both.
    [ObservableProperty] private bool _isClient;
    [ObservableProperty] private bool _isPhysioOrAdmin;
    [ObservableProperty] private bool _canCreatePlans;

    // --- Admin oversight filter (FEATURE: admin views all plans) ---
    // An admin has no plans of their own; they pick a physiotherapist or a client
    // and see that person's plans (read-only — edit/complete stay physio-only via
    // CanCreatePlans). Reuses the existing per-physio / per-client API endpoints.
    public const string PlanFilterByPhysio = "By Physiotherapist";
    public const string PlanFilterByClient = "By Client";

    public IReadOnlyList<string> PlanFilterModes { get; } = [PlanFilterByPhysio, PlanFilterByClient];

    public ObservableCollection<UserDto> FilterPhysios { get; } = [];

    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _selectedPlanFilterMode = PlanFilterByPhysio;
    [ObservableProperty] private UserDto? _filterPhysio;
    [ObservableProperty] private UserDto? _filterClient;

    public bool ShowPhysioFilter => IsAdmin && SelectedPlanFilterMode == PlanFilterByPhysio;
    public bool ShowClientFilter => IsAdmin && SelectedPlanFilterMode == PlanFilterByClient;

    partial void OnIsAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPhysioFilter));
        OnPropertyChanged(nameof(ShowClientFilter));
    }

    partial void OnSelectedPlanFilterModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowPhysioFilter));
        OnPropertyChanged(nameof(ShowClientFilter));
        if (IsAdmin) LoadPlansCommand.Execute(null);
    }

    partial void OnFilterPhysioChanged(UserDto? value)
    {
        if (IsAdmin) LoadPlansCommand.Execute(null);
    }

    partial void OnFilterClientChanged(UserDto? value)
    {
        if (IsAdmin) LoadPlansCommand.Execute(null);
    }

    // Tab state. Default to Active; the History tab is opt-in and the toggle
    // commands at the top of the page (ShowActive / ShowHistory) flip this.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActiveTab))]
    [NotifyPropertyChangedFor(nameof(HasNoGroupedPlans))]
    private bool _isHistoryTab;

    public bool IsActiveTab => !IsHistoryTab;

    // "No clients have treatment plans yet" only makes sense for physio/admin; the
    // client gets a different empty state. Computed rather than stored so it tracks
    // ClientGroups.Count automatically.
    public bool HasNoGroupedPlans => IsPhysioOrAdmin && ClientGroups.Count == 0;

    public TreatmentPlanViewModel(IApiService apiService, IAuthService authService)
    {
        _apiService = apiService;
        _authService = authService;
        Title = "Treatment Plans";
        ClientGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoGroupedPlans));
    }

    partial void OnIsPhysioOrAdminChanged(bool value) => OnPropertyChanged(nameof(HasNoGroupedPlans));

    // Switching tabs only re-projects the already-loaded plans — no network call.
    partial void OnIsHistoryTabChanged(bool value)
    {
        RebuildVisibleCollections();
        // Cancel any in-flight edits — editing a plan in Active then switching to
        // History (where edit isn't allowed) and back would otherwise leave a
        // half-completed form open.
        CancelPlanEdit();
        CancelEntryEdit();
        CancelAddEntry();
    }

    [RelayCommand] private void ShowActive() => IsHistoryTab = false;
    [RelayCommand] private void ShowHistory() => IsHistoryTab = true;

    [RelayCommand]
    private async Task LoadPlansAsync()
    {
        if (!SetBusy()) return;
        try
        {
            await LoadPlansCoreAsync();
            if (IsPhysioOrAdmin)
                await LoadClientsAsync(); // doubles as the admin "By Client" filter source

            // Admin filter: physiotherapist picker source, loaded once.
            if (IsAdmin && FilterPhysios.Count == 0)
            {
                foreach (var p in await _apiService.GetPhysiotherapistsAsync())
                    FilterPhysios.Add(p);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to load treatment plans: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // Core reload without IsBusy juggling — callable from flows (CreatePlan, edit,
    // delete, complete, reopen) that are already inside a busy block and would
    // otherwise hit the silent-early-return in LoadPlansAsync's SetBusy guard.
    private async Task LoadPlansCoreAsync()
    {
        _allPlans.Clear();
        Plans.Clear();
        ClientGroups.Clear();

        var user = await _apiService.GetCurrentUserAsync();
        if (user is null) return;

        // Use the DTO role (DB truth), not the cached login-time role.
        IsClient = user.Role == UserRole.Client;
        IsAdmin = user.Role == UserRole.Admin;
        IsPhysioOrAdmin = user.Role is UserRole.Physiotherapist or UserRole.Admin;
        CanCreatePlans = user.Role == UserRole.Physiotherapist;

        List<TreatmentPlanDto> list;
        if (user.Role == UserRole.Client)
        {
            list = user.ProfileId.HasValue ? await _apiService.GetTreatmentPlansByClientAsync(user.ProfileId.Value) : [];
        }
        else if (user.Role == UserRole.Physiotherapist)
        {
            list = user.ProfileId.HasValue ? await _apiService.GetTreatmentPlansByPhysioAsync(user.ProfileId.Value) : [];
        }
        else
        {
            // Admin oversight: no plans of their own — load the selected physio's or
            // client's plans. Nothing selected yet → empty list with the filter UI
            // prompting a choice.
            if (SelectedPlanFilterMode == PlanFilterByPhysio && FilterPhysio?.ProfileId is Guid pid)
                list = await _apiService.GetTreatmentPlansByPhysioAsync(pid);
            else if (SelectedPlanFilterMode == PlanFilterByClient && FilterClient?.ProfileId is Guid cid)
                list = await _apiService.GetTreatmentPlansByClientAsync(cid);
            else
                list = [];
        }

        foreach (var p in list)
            _allPlans.Add(new TreatmentPlanWithEntriesViewModel(p, _apiService));

        RebuildVisibleCollections();
    }

    /// <summary>
    /// Re-projects <see cref="_allPlans"/> into the user-visible collections based
    /// on the current tab and role. Called any time the data set or tab changes.
    /// History is sorted by CompletedAt desc (most-recently-finished first),
    /// Active by UpdatedAt-or-CreatedAt desc.
    /// </summary>
    private void RebuildVisibleCollections()
    {
        Plans.Clear();
        var filtered = _allPlans.Where(p => p.IsCompleted == IsHistoryTab);
        filtered = IsHistoryTab
            ? filtered.OrderByDescending(p => p.CompletedAt ?? p.UpdatedAt ?? p.CreatedAt)
            : filtered.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt);

        var materialised = filtered.ToList();
        foreach (var p in materialised)
            Plans.Add(p);

        // Rebuild grouped view from the same filtered set, preserving the existing
        // expanded/collapsed state per client.
        var previousExpanded = ClientGroups
            .Where(g => g.IsExpanded)
            .Select(g => g.ClientProfileId)
            .ToHashSet();

        ClientGroups.Clear();
        var grouped = materialised
            .GroupBy(p => new { p.ClientProfileId, p.ClientName })
            .OrderBy(g => g.Key.ClientName);
        foreach (var g in grouped)
        {
            ClientGroups.Add(new ClientTreatmentPlansGroup(
                g.Key.ClientProfileId,
                g.Key.ClientName,
                g,
                isExpanded: previousExpanded.Contains(g.Key.ClientProfileId)));
        }

        OnPropertyChanged(nameof(HasNoGroupedPlans));
    }

    [RelayCommand]
    private void ToggleGroupExpanded(ClientTreatmentPlansGroup group)
    {
        if (group is null) return;
        group.IsExpanded = !group.IsExpanded;
    }

    private async Task LoadClientsAsync()
    {
        try
        {
            var previouslySelectedId = SelectedClient?.Id;
            ClientList.Clear();
            var clients = await _apiService.GetClientsAsync();
            foreach (var c in clients)
                ClientList.Add(c);

            ApplyClientFilter();

            // Preserve the selection across reloads if the same client is still there.
            if (previouslySelectedId.HasValue)
                SelectedClient = ClientList.FirstOrDefault(c => c.Id == previouslySelectedId.Value);
        }
        catch (Exception ex)
        {
            SetError($"Failed to load clients: {ex.Message}");
        }
    }

    // --- Searchable client picker (physio/admin) ---

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

    // ===== Create Plan =====

    [RelayCommand]
    private async Task CreatePlanAsync()
    {
        if (!IsPhysioOrAdmin) { SetError("Only physiotherapists can create plans."); return; }

        if (SelectedClient is null)
        {
            SetError("Please select a client first.");
            return;
        }
        if (SelectedClient.ProfileId is null)
        {
            SetError("The selected client is missing a client profile. Ask an admin to run the profile backfill.");
            return;
        }
        if (string.IsNullOrWhiteSpace(NewPlanTitle))
        {
            SetError("Title is required.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var me = await _apiService.GetCurrentUserAsync();
            if (me is null) { SetError("Could not load current user."); return; }

            // Admins don't have a PhysiotherapistProfile themselves, so they can't own
            // a plan directly. Short-circuit with a clear message rather than sending a
            // null FK and getting a generic failure back.
            if (!me.ProfileId.HasValue || me.Role != UserRole.Physiotherapist)
            {
                SetError("Only physiotherapists can own a treatment plan. Admins should ask a physio to create it.");
                return;
            }

            var dto = new CreateTreatmentPlanDto
            {
                ClientProfileId = SelectedClient.ProfileId.Value,
                PhysiotherapistProfileId = me.ProfileId.Value,
                Title = NewPlanTitle.Trim(),
                Description = NewPlanDescription
            };

            var (plan, error) = await _apiService.CreateTreatmentPlanAsync(dto);
            if (plan is not null)
            {
                var clientName = SelectedClient.FullName;
                // Fully reset the create-plan form so it collapses back to the
                // client-picker state. Clearing SelectedClient flips IsClientSelected
                // to false, which hides the title/description/submit section in XAML.
                NewPlanTitle = string.Empty;
                NewPlanDescription = null;
                SelectedClient = null;
                ClientSearchText = string.Empty;
                ApplyClientFilter();
                await LoadPlansCoreAsync();
                SetSuccess($"Plan created for {clientName}.");
            }
            else
            {
                SetError(string.IsNullOrWhiteSpace(error)
                    ? "Failed to create treatment plan."
                    : error);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to create plan: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // ===== Plan Edit/Delete =====

    [RelayCommand]
    private void EditPlan(TreatmentPlanWithEntriesViewModel plan)
    {
        if (plan is null) return;
        EditingPlan = plan.Dto;
        EditPlanTitle = plan.Title;
        EditPlanDescription = plan.Description;
        IsEditingPlan = true;
        IsEditingEntry = false;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task SavePlanEditAsync()
    {
        if (EditingPlan is null) return;

        if (string.IsNullOrWhiteSpace(EditPlanTitle))
        {
            SetError("Title is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditPlanDescription))
        {
            SetError("Description is required.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var dto = new UpdateTreatmentPlanDto
            {
                Title = EditPlanTitle,
                Description = EditPlanDescription
            };
            var result = await _apiService.UpdateTreatmentPlanAsync(EditingPlan.Id, dto);
            if (result is not null)
            {
                CancelPlanEdit();
                await LoadPlansCoreAsync();
                SetSuccess("Treatment plan updated.");
            }
            else
            {
                SetError("Failed to update treatment plan.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to update plan: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void CancelPlanEdit()
    {
        IsEditingPlan = false;
        EditingPlan = null;
        EditPlanTitle = string.Empty;
        EditPlanDescription = null;
        // Don't clear error/success here — they may have been set by the operation
        // that triggered the cancel (e.g. a successful save).
    }

    [RelayCommand]
    private async Task DeletePlanAsync(TreatmentPlanWithEntriesViewModel plan)
    {
        if (plan is null) return;
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Delete Plan", $"Are you sure you want to delete \"{plan.Title}\"?", "Yes", "No");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var success = await _apiService.DeleteTreatmentPlanAsync(plan.Id);
            if (success)
            {
                await LoadPlansCoreAsync();
                SetSuccess("Treatment plan deleted.");
            }
            else
            {
                SetError("Failed to delete treatment plan.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to delete plan: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // ===== Plan Completion =====

    [RelayCommand]
    private async Task MarkPlanCompletedAsync(TreatmentPlanWithEntriesViewModel plan)
    {
        if (plan is null) return;
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Complete Plan",
            "Mark this treatment plan as completed?",
            "Yes", "No");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var result = await _apiService.CompleteTreatmentPlanAsync(plan.Id);
            if (result is not null)
            {
                await LoadPlansCoreAsync();
                SetSuccess($"Plan \"{plan.Title}\" marked as completed.");
            }
            else
            {
                SetError("Failed to complete plan.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to complete plan: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ReopenPlanAsync(TreatmentPlanWithEntriesViewModel plan)
    {
        if (plan is null) return;
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Reopen Plan",
            $"Move \"{plan.Title}\" back to active?",
            "Yes", "No");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var result = await _apiService.ReopenTreatmentPlanAsync(plan.Id);
            if (result is not null)
            {
                await LoadPlansCoreAsync();
                SetSuccess($"Plan \"{plan.Title}\" reopened.");
            }
            else
            {
                SetError("Failed to reopen plan.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to reopen plan: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    // ===== Entry Edit/Delete =====

    [RelayCommand]
    private void EditEntry(TreatmentPlanEntryViewModel entry)
    {
        if (entry is null) return;
        EditingEntry = entry.Dto;
        EditingEntryPlanId = entry.TreatmentPlanId;
        EditEntryTitle = entry.Title;
        EditEntryDescription = entry.Description;
        EditEntryOrderIndex = entry.OrderIndex;
        EditEntryIsCompleted = entry.IsCompleted;
        IsEditingEntry = true;
        IsEditingPlan = false;
        IsAddingEntry = false;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task SaveEntryEditAsync()
    {
        if (EditingEntry is null) return;

        if (string.IsNullOrWhiteSpace(EditEntryTitle))
        {
            SetError("Entry title is required.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var dto = new UpdateTreatmentPlanEntryDto
            {
                Title = EditEntryTitle,
                Description = EditEntryDescription,
                OrderIndex = EditEntryOrderIndex,
                IsCompleted = EditEntryIsCompleted
            };
            var result = await _apiService.UpdateTreatmentPlanEntryAsync(EditingEntry.Id, dto);
            if (result is not null)
            {
                CancelEntryEdit();
                await LoadPlansCoreAsync();
                SetSuccess("Entry updated.");
            }
            else
            {
                SetError("Failed to update entry.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to update entry: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void CancelEntryEdit()
    {
        IsEditingEntry = false;
        EditingEntry = null;
        EditEntryTitle = string.Empty;
        EditEntryDescription = null;
        EditEntryOrderIndex = 0;
        EditEntryIsCompleted = false;
    }

    // ===== Add Entry =====

    [RelayCommand]
    private void StartAddEntry(TreatmentPlanWithEntriesViewModel plan)
    {
        if (plan is null) return;
        // Refuse to open the form on a completed plan — the API would reject it on
        // save and the user would have just wasted typing.
        if (plan.IsCompleted) return;

        AddingEntryPlanId = plan.Id;
        AddingEntryPlanTitle = plan.Title;
        NewEntryTitle = string.Empty;
        NewEntryDescription = null;
        IsAddingEntry = true;
        // Close any other open inline forms — only one form at a time keeps the UI
        // unambiguous about what Save applies to.
        IsEditingEntry = false;
        IsEditingPlan = false;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task SaveNewEntryAsync()
    {
        if (!IsAddingEntry) return;
        if (string.IsNullOrWhiteSpace(NewEntryTitle))
        {
            SetError("Entry title is required.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            // Append at the bottom of the existing list — the OrderIndex is just
            // (last index + 1). The physio can drag later via the order field on
            // the edit form if they want a different position.
            var plan = _allPlans.FirstOrDefault(p => p.Id == AddingEntryPlanId);
            var nextOrderIndex = (plan?.Entries.Count ?? 0) > 0
                ? plan!.Entries.Max(e => e.OrderIndex) + 1
                : 0;

            var dto = new CreateTreatmentPlanEntryDto
            {
                Title = NewEntryTitle.Trim(),
                Description = string.IsNullOrWhiteSpace(NewEntryDescription) ? null : NewEntryDescription,
                OrderIndex = nextOrderIndex
            };
            var result = await _apiService.AddTreatmentPlanEntryAsync(AddingEntryPlanId, dto);
            if (result is not null)
            {
                CancelAddEntry();
                await LoadPlansCoreAsync();
                SetSuccess("Entry added.");
            }
            else
            {
                SetError("Failed to add entry.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to add entry: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void CancelAddEntry()
    {
        IsAddingEntry = false;
        AddingEntryPlanId = Guid.Empty;
        AddingEntryPlanTitle = string.Empty;
        NewEntryTitle = string.Empty;
        NewEntryDescription = null;
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(TreatmentPlanEntryViewModel entry)
    {
        if (entry is null) return;
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Delete Entry", $"Are you sure you want to delete \"{entry.Title}\"?", "Yes", "No");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var success = await _apiService.DeleteTreatmentPlanEntryAsync(entry.Id);
            if (success)
            {
                await LoadPlansCoreAsync();
                SetSuccess("Entry deleted.");
            }
            else
            {
                SetError("Failed to delete entry.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to delete entry: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }
}
