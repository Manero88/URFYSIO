using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class LoginPage : ContentPage
{
    public LoginPage(LoginViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    /// <summary>
    /// Wired up by <c>EmailEntry.Completed</c> in XAML. Tabbing the focus to the
    /// password field is what lets the user fill the form without ever reaching for
    /// the mouse — Enter on email → password gets focus → Enter again → login fires
    /// (the Login Entry has <c>ReturnCommand="{Binding LoginCommand}"</c>, so the
    /// chain finishes there without code-behind).
    ///
    /// We don't need a corresponding handler for the password field because its
    /// <c>ReturnCommand</c> is the LoginCommand itself.
    /// </summary>
    private void OnEmailEntryCompleted(object? sender, EventArgs e)
    {
        PasswordEntry.Focus();
    }
}
