using System.Globalization;
using Auth0.OidcClient;
using Plugin.LocalNotification;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.App.Views;

namespace URFYSIO.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // App-wide culture: Dutch (nl-NL) with DD/MM/YYYY dates and 24-hour HH:mm
        // times. We start from nl-NL (24-hour clock, European conventions) and pin
        // the date separator to "/" and the patterns explicitly — nl-NL defaults to
        // "-" as the date separator, but the design calls for slashes. Pinning these
        // also makes DatePicker/TimePicker render in the same format, and makes XAML
        // StringFormat tokens like {0:dd/MM/yyyy} resolve the "/" to a literal slash.
        var culture = new CultureInfo("nl-NL");
        culture.DateTimeFormat.DateSeparator = "/";
        culture.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
        culture.DateTimeFormat.ShortTimePattern = "HH:mm";
        culture.DateTimeFormat.LongTimePattern = "HH:mm:ss";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseLocalNotification()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // --- Auth0 OIDC Client ---
        builder.Services.AddSingleton(new Auth0Client(new Auth0ClientOptions
        {
            Domain = "dev-n4gkajag3xbup52c.us.auth0.com",
            ClientId = "KM0R5l4jcCMaDIX9LcAxMOASD5VU4Cfm",
            RedirectUri = "myapp://callback",
            PostLogoutRedirectUri = "myapp://callback",
            Scope = "openid profile email"
        }));

        // --- HTTP Client with auth handler ---
        builder.Services.AddTransient<AuthTokenHandler>();

        builder.Services.AddHttpClient<IApiService, ApiService>(client =>
        {
#if DEBUG
            // Local development
            string baseUrl = DeviceInfo.Platform == DevicePlatform.Android
                ? "http://10.0.2.2:5068"
                : "http://localhost:5068";
#else
            // Production (Azure)
            string baseUrl = "https://urfysio-api-crfnashkcphjeaah.westeurope-01.azurewebsites.net";
#endif
            client.BaseAddress = new Uri(baseUrl);
        }).AddHttpMessageHandler<AuthTokenHandler>();

        // --- Services ---
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IAppointmentReminderService, AppointmentReminderService>();
        // Camera / gallery access for photo attachments on treatment-plan comments.
        builder.Services.AddSingleton<IPhotoPickerService, PhotoPickerService>();

        // --- ViewModels ---
        builder.Services.AddSingleton<AppShellViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<AppointmentsViewModel>();
        builder.Services.AddTransient<BookAppointmentViewModel>();
        builder.Services.AddTransient<AvailabilityViewModel>();
        builder.Services.AddTransient<TreatmentPlanViewModel>();
        builder.Services.AddTransient<AdminDashboardViewModel>();
        builder.Services.AddTransient<RegistrationViewModel>();
        builder.Services.AddTransient<RescheduleAppointmentViewModel>();
        builder.Services.AddTransient<ProfileViewModel>();

        // --- Pages ---
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<AppointmentsPage>();
        builder.Services.AddTransient<BookAppointmentPage>();
        builder.Services.AddTransient<AvailabilityPage>();
        builder.Services.AddTransient<TreatmentPlanPage>();
        builder.Services.AddTransient<AdminDashboardPage>();
        builder.Services.AddTransient<RegistrationPage>();
        builder.Services.AddTransient<RescheduleAppointmentPage>();
        builder.Services.AddTransient<ProfilePage>();

        return builder.Build();
    }
}
