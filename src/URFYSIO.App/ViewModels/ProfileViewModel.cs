using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.Users;

namespace URFYSIO.App.ViewModels;

public partial class ProfileViewModel : BaseViewModel
{
    private readonly IApiService _apiService;

    public List<string> GenderOptions { get; } = new() { "Male", "Female", "Rather not tell" };

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string? _phoneNumber;
    [ObservableProperty] private DateTime? _dateOfBirth;
    [ObservableProperty] private string? _gender;
    [ObservableProperty] private string? _street;
    [ObservableProperty] private string? _houseNumber;
    [ObservableProperty] private string? _postalCode;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _authProvider = string.Empty;
    [ObservableProperty] private string _memberSince = string.Empty;
    [ObservableProperty] private bool _isEditing;

    // DatePicker needs a non-null DateTime, so we use a wrapper
    [ObservableProperty] private DateTime _datePickerDate = DateTime.Today;
    [ObservableProperty] private bool _hasDateOfBirth;

    // Password change fields
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _isEmailPasswordUser;
    [ObservableProperty] private string? _passwordErrorMessage;
    [ObservableProperty] private string? _passwordSuccessMessage;

    public ProfileViewModel(IApiService apiService)
    {
        _apiService = apiService;
        Title = "Profile";
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        if (!SetBusy()) return;
        try
        {
            var profile = await _apiService.GetMyProfileAsync();
            if (profile is null)
            {
                SetError("Could not load profile.");
                return;
            }

            Email = profile.Email;
            FirstName = profile.FirstName;
            LastName = profile.LastName;
            PhoneNumber = profile.PhoneNumber;
            DateOfBirth = profile.DateOfBirth;
            Gender = profile.Gender;
            Street = profile.Street;
            HouseNumber = profile.HouseNumber;
            PostalCode = profile.PostalCode;
            City = profile.City;
            Role = profile.Role.ToString();
            AuthProvider = profile.AuthProvider;
            MemberSince = profile.CreatedAt.ToString("dd MMM yyyy");
            IsEmailPasswordUser = string.Equals(profile.AuthProvider, "email", StringComparison.OrdinalIgnoreCase);

            HasDateOfBirth = profile.DateOfBirth.HasValue;
            DatePickerDate = profile.DateOfBirth ?? DateTime.Today;

            IsEditing = false;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load profile: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private void StartEditing()
    {
        IsEditing = true;
        ErrorMessage = null;
        SuccessMessage = null;
    }

    [RelayCommand]
    private async Task CancelEditingAsync()
    {
        IsEditing = false;
        await LoadProfileAsync();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(FirstName))
        {
            SetError("First name is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(LastName))
        {
            SetError("Last name is required.");
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var dto = new UpdateUserProfileDto
            {
                FirstName = FirstName.Trim(),
                LastName = LastName.Trim(),
                PhoneNumber = string.IsNullOrWhiteSpace(PhoneNumber) ? null : PhoneNumber.Trim(),
                DateOfBirth = HasDateOfBirth ? DatePickerDate : null,
                Gender = string.IsNullOrWhiteSpace(Gender) ? null : Gender.Trim(),
                Street = string.IsNullOrWhiteSpace(Street) ? null : Street.Trim(),
                HouseNumber = string.IsNullOrWhiteSpace(HouseNumber) ? null : HouseNumber.Trim(),
                PostalCode = string.IsNullOrWhiteSpace(PostalCode) ? null : PostalCode.Trim(),
                City = string.IsNullOrWhiteSpace(City) ? null : City.Trim()
            };

            var result = await _apiService.UpdateMyProfileAsync(dto);
            if (result is null)
            {
                SetError("Failed to save profile.");
                return;
            }

            // Update local state from response
            FirstName = result.FirstName;
            LastName = result.LastName;
            PhoneNumber = result.PhoneNumber;
            DateOfBirth = result.DateOfBirth;
            Gender = result.Gender;
            Street = result.Street;
            HouseNumber = result.HouseNumber;
            PostalCode = result.PostalCode;
            City = result.City;

            HasDateOfBirth = result.DateOfBirth.HasValue;
            DatePickerDate = result.DateOfBirth ?? DateTime.Today;

            IsEditing = false;
            SetSuccess("Profile updated successfully.");
        }
        catch (Exception ex)
        {
            SetError($"Failed to save profile: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        PasswordErrorMessage = null;
        PasswordSuccessMessage = null;

        if (!IsEmailPasswordUser)
        {
            PasswordErrorMessage = "Password change is only available for email/password accounts.";
            return;
        }

        if (string.IsNullOrEmpty(NewPassword) || NewPassword.Length < 8)
        {
            PasswordErrorMessage = "Password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            PasswordErrorMessage = "Passwords do not match.";
            return;
        }

        if (!SetBusy()) return;
        try
        {
            var dto = new ChangePasswordDto
            {
                NewPassword = NewPassword,
                ConfirmPassword = ConfirmPassword
            };

            var (success, message) = await _apiService.ChangeMyPasswordAsync(dto);
            if (success)
            {
                PasswordSuccessMessage = message;
                NewPassword = string.Empty;
                ConfirmPassword = string.Empty;
            }
            else
            {
                PasswordErrorMessage = message;
            }
        }
        catch (Exception ex)
        {
            PasswordErrorMessage = $"Failed to change password: {ex.Message}";
        }
        finally
        {
            ClearBusy();
        }
    }
}
