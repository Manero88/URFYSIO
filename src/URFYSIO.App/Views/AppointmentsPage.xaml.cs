using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class AppointmentsPage : ContentPage
{
    private readonly AppointmentsViewModel _vm;

    public AppointmentsPage(AppointmentsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // RefreshOnAppear resets the "Show History" toggle + error/success banners before
        // reloading, so navigating back to this tab doesn't show stale state from a prior
        // visit (e.g. you cancelled an appointment last time, came back, and the error
        // from that flow was still on screen).
        await _vm.RefreshOnAppearCommand.ExecuteAsync(null);
    }
}
