using Microsoft.UI.Xaml;

namespace URFYSIO.App.WinUI;

public partial class App : MauiWinUIApplication
{
	public App()
	{
		if (Auth0.OidcClient.Platforms.Windows.Activator.Default.CheckRedirectionActivation())
			return;

		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		base.OnLaunched(args);
	}
}
