using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;

namespace URFYSIO.App.ViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly IAuthService _authService;
    private readonly IApiService _apiService;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    public LoginViewModel(IAuthService authService, IApiService apiService)
    {
        _authService = authService;
        _apiService = apiService;
        Title = "Inloggen";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (!SetBusy()) return;
        try
        {
            var (success, error) = await _authService.LoginWithPasswordAsync(Email, Password);
            if (!success)
            {
                SetError(error ?? "Login failed.");
                return;
            }

            Password = string.Empty;
            if (!await EnsureAccountActiveAsync()) return;
            await NavigateByRoleAsync();
        }
        catch (Exception ex)
        {
            SetError($"Login failed: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        if (!SetBusy()) return;
        try
        {
            var success = await _authService.LoginWithGoogleAsync();
            if (!success)
            {
                SetError("Google login was cancelled or failed.");
                return;
            }

            if (!await EnsureAccountActiveAsync()) return;
            await NavigateByRoleAsync();
        }
        catch (Exception ex)
        {
            SetError($"Google login failed: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    private async Task LoginWithMicrosoftAsync()
    {
        if (!SetBusy()) return;
        try
        {
            var success = await _authService.LoginWithMicrosoftAsync();
            if (!success)
            {
                SetError("Microsoft login was cancelled or failed.");
                return;
            }

            if (!await EnsureAccountActiveAsync()) return;
            await NavigateByRoleAsync();
        }
        catch (Exception ex)
        {
            SetError($"Microsoft login failed: {ex.Message}");
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>
    /// After a successful Auth0 login, confirm the local account is active. Brand-new
    /// accounts (auto-created by Auth0UserSyncMiddleware on first login) start inactive
    /// and must be approved by an admin. If inactive, sign the user straight back out
    /// and show a pending-approval message. Returns false when the caller should stop
    /// (account inactive or profile unreadable); true to continue navigating.
    /// </summary>
    private async Task<bool> EnsureAccountActiveAsync()
    {
        var me = await _apiService.GetCurrentUserAsync();
        if (me is null)
        {
            // Couldn't read the profile — fail safe by logging out rather than
            // dropping the user into the app in an unknown state.
            await _authService.LogoutAsync();
            SetError("Could not verify your account. Please try again.");
            return false;
        }

        if (!me.IsActive)
        {
            await _authService.LogoutAsync();
            SetError("Your account is pending admin approval. Please wait for activation.");
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task GoToRegisterAsync()
    {
        await Shell.Current.GoToAsync("//register");
    }

    private async Task NavigateByRoleAsync()
    {
        var route = _authService.CurrentRole switch
        {
            "Admin" => "//admin",
            "Physiotherapist" => "//physio",
            "Client" => "//client",
            _ => "//client"
        };
        await Shell.Current.GoToAsync(route);
    }
}
