using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Models;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Appointments;
using URFYSIO.Shared.DTOs.Registration;
using URFYSIO.Shared.DTOs.Users;
using URFYSIO.Shared.Enums;

namespace URFYSIO.App.ViewModels;

public partial class AdminDashboardViewModel : BaseViewModel
{
    private readonly IApiService _apiService;

    [ObservableProperty] private int _totalUsers;
    [ObservableProperty] private int _totalAppointments;
    [ObservableProperty] private int _pendingRegistrations;

    private Guid? _currentUserId;

    public ObservableCollection<UserDto> Users { get; } = [];
    public ObservableCollection<RegistrationRequestDto> PendingRequests { get; } = [];

    /// <summary>
    /// Unified approval queue: pending registration-form submissions AND inactive users
    /// (self-service SSO sign-ups, which the middleware creates with IsActive=false).
    /// Before this existed only the former was listed, so anyone who signed in with
    /// Google was invisible to the admin and permanently stuck on "pending approval".
    /// </summary>
    public ObservableCollection<PendingApprovalItem> PendingApprovals { get; } = [];

    public AdminDashboardViewModel(IApiService apiService)
    {
        _apiService = apiService;
        Title = "Admin Dashboard";
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (!SetBusy()) return;
        try
        {
            Users.Clear();
            PendingRequests.Clear();
            PendingApprovals.Clear();

            var me = await _apiService.GetCurrentUserAsync();
            _currentUserId = me?.Id;

            var users = await _apiService.GetUsersAsync();
            TotalUsers = users.Count;
            foreach (var u in users) Users.Add(u);

            var appointments = await _apiService.GetAllAppointmentsAsync();
            TotalAppointments = appointments.Count;

            // Ask the server for Pending only (the GetAll endpoint supports ?status=).
            // Diagnosis note (BUG: "registrations don't appear"): rows WERE saved with
            // Status=Pending and the filter matched — the list looked empty because the
            // old code swallowed load failures (Azure SQL auto-pause) into an empty
            // list. GetRegistrationRequestsAsync now throws on failure, which lands in
            // this method's catch and shows the admin a real error banner.
            var pending = await _apiService.GetRegistrationRequestsAsync(RegistrationStatus.Pending);
            foreach (var r in pending) PendingRequests.Add(r);

            // Merge both kinds of "waiting for an admin" into one queue. Inactive users
            // come from the list already fetched above — no extra endpoint needed. The
            // admin's own account is excluded so a deactivated admin can't see itself
            // queued for approval.
            var inactiveUsers = users
                .Where(u => !u.IsActive && u.Id != _currentUserId)
                .ToList();

            foreach (var item in pending.Select(PendingApprovalItem.FromRegistration)
                         .Concat(inactiveUsers.Select(PendingApprovalItem.FromUser))
                         .OrderByDescending(i => i.RequestedAt))
            {
                PendingApprovals.Add(item);
            }

            // Drives the "Requests" stat tile — count everything awaiting action, not
            // just form submissions.
            PendingRegistrations = PendingApprovals.Count;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load admin data: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ApproveRegistrationAsync(RegistrationRequestDto request)
    {
        try
        {
            var (success, message) = await _apiService.ApproveRegistrationAsync(request.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess($"{request.FirstName} {request.LastName} approved — a password-setup email has been sent.");
            }
            else
            {
                // Surface the API's reason (Auth0 conflict, missing scope, etc.).
                SetError(string.IsNullOrWhiteSpace(message)
                    ? "Failed to approve registration."
                    : message);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to approve registration: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RejectRegistrationAsync(RegistrationRequestDto request)
    {
        var confirm = await Shell.Current.DisplayAlertAsync("Reject", "Reject this registration?", "Yes", "No");
        if (!confirm) return;

        try
        {
            var success = await _apiService.RejectRegistrationAsync(request.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess("Registration rejected.");
            }
            else
            {
                SetError("Failed to reject registration.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to reject registration: {ex.Message}");
        }
    }

    /// <summary>
    /// Single entry point for the unified pending list — dispatches to the registration
    /// approval flow or to plain activation depending on what the row represents.
    /// </summary>
    [RelayCommand]
    private async Task ApprovePendingAsync(PendingApprovalItem item)
    {
        if (item is null) return;

        if (item.IsRegistration)
        {
            await ApproveRegistrationAsync(item.Registration!);
            return;
        }

        await ActivateUserAsync(item.User!);
    }

    /// <summary>
    /// Rejecting a form submission just marks it rejected (no account exists yet).
    /// Rejecting an SSO sign-up must DELETE the account: the person already has a working
    /// Auth0 login, so leaving the row inactive would let them sign in again and re-create
    /// the same pending entry forever.
    /// </summary>
    [RelayCommand]
    private async Task RejectPendingAsync(PendingApprovalItem item)
    {
        if (item is null) return;

        if (item.IsRegistration)
        {
            await RejectRegistrationAsync(item.Registration!);
            return;
        }

        var user = item.User!;
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Reject sign-up",
            $"Reject {item.DisplayName} ({item.Email})?\n\n" +
            "Their account and login will be permanently deleted. They signed in with " +
            $"{item.SourceLabel}, so leaving it would let them sign in again and reappear here.",
            "Reject and delete", "Cancel");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var (success, message) = await _apiService.DeleteUserPermanentlyAsync(user.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess($"{item.DisplayName} was rejected and their account deleted.");
            }
            else
            {
                SetError(string.IsNullOrWhiteSpace(message) ? "Failed to reject sign-up." : message);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to reject sign-up: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ActivateUserAsync(UserDto user)
    {
        if (user is null) return;

        if (user.IsActive)
        {
            SetError("User is already active.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var (success, message) = await _apiService.ActivateUserAsync(user.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess($"{user.FullName} has been activated and can now sign in.");
            }
            else
            {
                SetError(string.IsNullOrWhiteSpace(message) ? "Failed to activate user." : message);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to activate user: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ChangeRoleAsync(UserDto user)
    {
        if (user.Id == _currentUserId)
        {
            SetError("You cannot change your own role.");
            return;
        }

        var roles = new[] { "Admin", "Physiotherapist", "Client" };
        var selected = await Shell.Current.DisplayActionSheetAsync(
            $"Change role for {user.FullName}", "Cancel", null, roles);

        if (selected is null or "Cancel") return;

        if (!Enum.TryParse<UserRole>(selected, out var newRole)) return;
        if (newRole == user.Role) return;

        if (!SetBusy()) return;
        try
        {
            var dto = new UpdateUserDto
            {
                FirstName = user.FirstName,
                LastName = user.LastName,
                PhoneNumber = user.PhoneNumber,
                Role = newRole,
                IsActive = user.IsActive
            };

            var result = await _apiService.UpdateUserAsync(user.Id, dto);
            if (result is not null)
            {
                await LoadDataAsync();
                SetSuccess($"Role changed to {selected} for {user.FullName}.");
            }
            else
            {
                SetError("Failed to change role.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to change role: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync(UserDto user)
    {
        if (!user.IsEmailPasswordUser)
        {
            SetError("Password reset is only available for email/password accounts.");
            return;
        }

        var confirm = await Shell.Current.DisplayAlertAsync(
            "Send Password Reset",
            $"Send a password reset email to {user.Email}?",
            "Send", "Cancel");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var (success, message) = await _apiService.AdminResetPasswordAsync(user.Id);
            if (success)
                SetSuccess(message);
            else
                SetError(message);
        }
        catch (Exception ex)
        {
            SetError($"Failed to send password reset: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task DeactivateUserAsync(UserDto user)
    {
        if (user.Id == _currentUserId)
        {
            SetError("You cannot deactivate your own account.");
            return;
        }

        if (!user.IsActive)
        {
            SetError("User is already inactive.");
            return;
        }

        var confirm = await Shell.Current.DisplayAlertAsync(
            "Deactivate User",
            $"Are you sure you want to deactivate {user.FullName}?",
            "Deactivate", "Cancel");
        if (!confirm) return;

        if (!SetBusy()) return;
        try
        {
            var success = await _apiService.DeactivateUserAsync(user.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess($"{user.FullName} has been deactivated.");
            }
            else
            {
                SetError("Failed to deactivate user.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to deactivate user: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync(UserDto user)
    {
        // Self-delete guard (the API also rejects this, but fail fast in the UI).
        if (user.Id == _currentUserId)
        {
            SetError("You cannot delete your own account.");
            return;
        }

        // First confirmation: spell out exactly what will be erased.
        var confirm = await Shell.Current.DisplayAlertAsync(
            "Permanently Delete User",
            $"This will PERMANENTLY delete {user.FullName} and all their data " +
            "(appointments, treatment plans, comments, and their login account). " +
            "This cannot be undone. Continue?",
            "Continue", "Cancel");
        if (!confirm) return;

        // Second confirmation: require the admin to type the user's name. This is the
        // type-to-confirm pattern — it makes an accidental destructive click on the wrong
        // row almost impossible.
        //
        // The label falls back to the email when there's no usable name: an SSO account
        // Auth0 gave us nothing for would otherwise ask the admin to "type the full name"
        // and show them nothing to type.
        var confirmLabel = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;

        var typed = await Shell.Current.DisplayPromptAsync(
            "Confirm Deletion",
            $"To confirm, type the user's name exactly:\n\n{confirmLabel}",
            accept: "Delete Permanently",
            cancel: "Cancel",
            placeholder: confirmLabel);

        if (typed is null) return; // cancelled

        // Compare leniently: case-insensitive, with internal runs of whitespace collapsed.
        // The strict ordinal comparison this replaces made some accounts undeletable —
        // a user with an empty LastName produced a FullName with a trailing space, which
        // the admin's (trimmed) input could never equal no matter what they typed.
        if (!NamesMatch(typed, confirmLabel))
        {
            SetError("The name you typed didn't match. Deletion cancelled.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var (success, message) = await _apiService.DeleteUserPermanentlyAsync(user.Id);
            if (success)
            {
                await LoadDataAsync();
                SetSuccess($"{user.FullName} and all their data have been permanently deleted.");
            }
            else
            {
                // Surface the API's reason (e.g. physio has upcoming appointments).
                SetError(string.IsNullOrWhiteSpace(message) ? "Failed to delete user." : message);
            }
        }
        catch (Exception ex)
        {
            SetError($"Failed to delete user: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>
    /// Type-to-confirm comparison: trims, collapses internal whitespace, and ignores case.
    /// Still requires the admin to type the right name — it only removes the ways a match
    /// could fail for reasons that have nothing to do with intent.
    /// </summary>
    internal static bool NamesMatch(string? typed, string? expected)
    {
        static string Normalise(string? s) =>
            string.Join(' ', (s ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var a = Normalise(typed);
        var b = Normalise(expected);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
