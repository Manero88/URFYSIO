namespace URFYSIO.App.Views;

/// <summary>
/// Full-screen viewer for a comment's photo attachment, pushed modally when a thumbnail is
/// tapped. Written in C# rather than XAML because it is a single image on a black
/// background — a XAML file and code-behind pair would be more ceremony than content.
///
/// The photo is loaded from a short-lived SAS URL, so two failure modes are expected and
/// handled rather than left to crash: the URL may already have expired, and the device may
/// be offline. Both land on a readable message instead of an empty black screen.
/// </summary>
public class PhotoViewerPage : ContentPage
{
    public PhotoViewerPage(string photoUrl)
    {
        Title = "Photo";
        BackgroundColor = Colors.Black;

        var image = new Image
        {
            Source = ImageSource.FromUri(new Uri(photoUrl)),
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };

        var spinner = new ActivityIndicator
        {
            IsRunning = true,
            IsVisible = true,
            Color = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        var failureLabel = new Label
        {
            Text = "This photo could not be loaded.\nThe link may have expired — go back and reopen the comments.",
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(24),
            IsVisible = false
        };

        // Image exposes loading state via IsLoading; mirror it onto the spinner and reveal
        // the failure text if loading finished without producing anything to show.
        image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Image.IsLoading)) return;

            spinner.IsRunning = image.IsLoading;
            spinner.IsVisible = image.IsLoading;

            if (!image.IsLoading)
            {
                var loaded = image.Width > 0 && image.Height > 0;
                failureLabel.IsVisible = !loaded;
            }
        };

        var closeButton = new Button
        {
            Text = "Close",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 24)
        };
        closeButton.Clicked += async (_, _) => await CloseAsync();

        // Tapping the image also dismisses — the usual gesture for a full-screen photo.
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CloseAsync();
        image.GestureRecognizers.Add(tap);

        Content = new Grid
        {
            Children = { image, spinner, failureLabel, closeButton }
        };
    }

    private async Task CloseAsync()
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch
        {
            // Already dismissed (double-tap racing the close button) — nothing to do.
        }
    }
}
