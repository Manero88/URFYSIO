using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;

namespace URFYSIO.App.ViewModels;

public partial class AppShellViewModel : ObservableObject
{
    private readonly IAuthService _authService;

    // Exposed so ContentPage toolbar items can bind via {x:Static vm:AppShellViewModel.Instance}.
    // Populated when AppShell resolves this singleton during construction.
    public static AppShellViewModel Instance { get; private set; } = null!;

    public AppShellViewModel(IAuthService authService)
    {
        _authService = authService;
        Instance = this;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await _authService.LogoutAsync();
        }
        catch
        {
            // Swallow logout errors — still navigate away so the user isn't stuck.
        }
        await Shell.Current.GoToAsync("//login");
    }
}
