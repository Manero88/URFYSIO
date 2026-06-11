using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class BookAppointmentPage : ContentPage
{
    private readonly BookAppointmentViewModel _vm;

    public BookAppointmentPage(BookAppointmentViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // The VM now decides per-role what to load (physios for clients, clients +
        // own slots for physios, short-circuit for admins), so the page just kicks
        // off the shared init.
        await _vm.LoadInitialDataCommand.ExecuteAsync(null);
    }
}
