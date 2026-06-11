using Microsoft.Extensions.DependencyInjection;
using URFYSIO.App.ViewModels;
using URFYSIO.App.Views;

namespace URFYSIO.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        var services = IPlatformApplication.Current?.Services;
        if (services is not null)
            BindingContext = services.GetRequiredService<AppShellViewModel>();

        // Register detail routes for navigation
        Routing.RegisterRoute("book", typeof(BookAppointmentPage));
        Routing.RegisterRoute("reschedule", typeof(RescheduleAppointmentPage));
    }
}
