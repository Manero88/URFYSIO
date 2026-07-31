using Microsoft.Extensions.Logging;
using URFYSIO.Shared.Photos;

namespace URFYSIO.App.Services;

/// <summary>
/// MediaPicker-backed implementation. All the platform awkwardness lives here: runtime
/// permissions, cancellation-vs-failure, and the fact that pickers report content types
/// inconsistently across Android versions.
/// </summary>
public class PhotoPickerService : IPhotoPickerService
{
    private readonly ILogger<PhotoPickerService> _logger;

    public PhotoPickerService(ILogger<PhotoPickerService> logger) => _logger = logger;

    public bool IsCaptureSupported
    {
        get
        {
            try { return MediaPicker.Default.IsCaptureSupported; }
            catch { return false; }
        }
    }

    public async Task<PhotoPickResult> CapturePhotoAsync()
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported)
                return PhotoPickResult.Failed("This device doesn't have a camera available.");

            var permission = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.Camera>();

            if (permission != PermissionStatus.Granted)
            {
                // Not an error state for the app — the user can still comment with text.
                return PhotoPickResult.Failed(
                    "Camera permission was not granted. You can still post a comment without a photo, " +
                    "or choose an existing photo from your gallery.");
            }

            var file = await MediaPicker.Default.CapturePhotoAsync();
            return file is null ? PhotoPickResult.Cancelled() : await ReadAsync(file);
        }
        catch (PermissionException ex)
        {
            _logger.LogWarning(ex, "Camera permission denied.");
            return PhotoPickResult.Failed(
                "Camera permission was denied. You can enable it in your device settings, " +
                "or post a comment without a photo.");
        }
        catch (FeatureNotSupportedException)
        {
            return PhotoPickResult.Failed("Taking photos isn't supported on this device.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CapturePhotoAsync failed.");
            return PhotoPickResult.Failed($"Could not take a photo: {ex.Message}");
        }
    }

    public async Task<PhotoPickResult> PickPhotoAsync()
    {
        try
        {
            // No explicit permission request here: the system gallery picker runs
            // out-of-process and returns only what the user chose, so modern Android
            // grants access to that single item without a storage permission.
            var file = await MediaPicker.Default.PickPhotoAsync();
            return file is null ? PhotoPickResult.Cancelled() : await ReadAsync(file);
        }
        catch (PermissionException ex)
        {
            _logger.LogWarning(ex, "Photo library permission denied.");
            return PhotoPickResult.Failed(
                "Permission to access your photos was denied. You can enable it in your device settings, " +
                "or post a comment without a photo.");
        }
        catch (FeatureNotSupportedException)
        {
            return PhotoPickResult.Failed("Choosing photos isn't supported on this device.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PickPhotoAsync failed.");
            return PhotoPickResult.Failed($"Could not open your photos: {ex.Message}");
        }
    }

    private async Task<PhotoPickResult> ReadAsync(FileResult file)
    {
        try
        {
            await using var source = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            var data = buffer.ToArray();

            var contentType = ResolveContentType(file);

            // Check the size here as well as server-side: failing before a multi-megabyte
            // upload over mobile data is much kinder than failing after it.
            var error = PhotoRules.Validate(contentType, data.LongLength);
            if (error is not null) return PhotoPickResult.Failed(error);

            return PhotoPickResult.Success(new PickedPhoto(file.FileName ?? "photo.jpg", contentType, data));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the picked photo failed.");
            return PhotoPickResult.Failed($"Could not read the selected photo: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a usable content type. FileResult.ContentType is unreliable across Android
    /// versions (frequently null, or a generic octet-stream), so the file extension is the
    /// fallback — and a JPEG guess is right for essentially every camera capture.
    /// </summary>
    private static string ResolveContentType(FileResult file)
    {
        if (PhotoRules.IsAllowedContentType(file.ContentType))
            return PhotoRules.Normalise(file.ContentType);

        var extension = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        return extension == ".png" ? PhotoRules.PngContentType : PhotoRules.JpegContentType;
    }
}
