using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            PendingRegistrations = pending.Count;
            foreach (var r in pending) PendingRequests.Add(r);
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

        // Second confirmation: require the admin to type the user's full name. This is
        // the type-to-confirm pattern — it makes an accidental destructive click on the
        // wrong row almost impossible.
        var typed = await Shell.Current.DisplayPromptAsync(
            "Confirm Deletion",
            $"To confirm, type the user's full name exactly:\n\n{user.FullName}",
            accept: "Delete Permanently",
            cancel: "Cancel",
            placeholder: user.FullName);

        if (typed is null) return; // cancelled
        if (!string.Equals(typed.Trim(), user.FullName, StringComparison.Ordinal))
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
}
