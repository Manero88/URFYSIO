using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Registration;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// "Register here" flow. This does NOT create an Auth0 account or log anyone in — it
/// submits a <see cref="CreateRegistrationRequestDto"/> to the public
/// <c>POST /api/registration</c> endpoint, which persists a RegistrationRequest with
/// Status=Pending. An admin then sees it under Pending Registrations and approves it,
/// at which point the actual user account is created. The previous implementation
/// went straight through Auth0 signup and never created a RegistrationRequest, so
/// nothing ever appeared in the admin list — that was the bug.
/// </summary>
public partial class RegistrationViewModel : BaseViewModel
{
    private readonly IApiService _apiService;

    [ObservableProperty] private bool _isSubmitted;

    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phoneNumber = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    public RegistrationViewModel(IApiService apiService)
    {
        _apiService = apiService;
        Title = "Register";
    }

    [RelayCommand]
    private async Task SignUpAsync()
    {
        if (!SetBusy()) return;
        try
        {
            // Client-side validation — the API also validates, but catching it here
            // gives instant feedback without a round-trip.
            if (string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName))
            {
                SetError("First and last name are required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Email) || !IsValidEmail(Email))
            {
                SetError("Please enter a valid email address.");
                return;
            }

            var dto = new CreateRegistrationRequestDto
            {
                FirstName = FirstName.Trim(),
                LastName = LastName.Trim(),
                Email = Email.Trim(),
                PhoneNumber = string.IsNullOrWhiteSpace(PhoneNumber) ? null : PhoneNumber.Trim(),
                Message = string.IsNullOrWhiteSpace(Message) ? null : Message.Trim()
            };

            var success = await _apiService.SubmitRegistrationAsync(dto);
            if (success)
            {
                IsSubmitted = true;
                ErrorMessage = null;
            }
            else
            {
                SetError("Could not submit your registration. Please try again later.");
            }
        }
        catch (Exception ex)
        {
            SetError($"Registration failed: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task BackToLoginAsync()
    {
        await Shell.Current.GoToAsync("//login");
    }

    private static bool IsValidEmail(string email) =>
        Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
