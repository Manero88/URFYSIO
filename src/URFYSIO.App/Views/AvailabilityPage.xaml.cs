using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class AvailabilityPage : ContentPage
{
    private readonly AvailabilityViewModel _vm;

    public AvailabilityPage(AvailabilityViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadSlotsCommand.ExecuteAsync(null);
    }
}
