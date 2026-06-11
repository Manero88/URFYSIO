using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class RescheduleAppointmentPage : ContentPage
{
    public RescheduleAppointmentPage(RescheduleAppointmentViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
