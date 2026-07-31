namespace URFYSIO.App.Services;

/// <summary>
/// A photo the user chose, held in memory as bytes rather than as a stream over the
/// picker's temp file. Two reasons: the preview thumbnail and the upload both need to read
/// it (a stream would be consumed by the first), and a failed upload must be retryable
/// without asking the user to pick the photo again.
/// </summary>
public sealed record PickedPhoto(string FileName, string ContentType, byte[] Data)
{
    public Stream OpenStream() => new MemoryStream(Data, writable: false);
}

/// <summary>
/// Outcome of a pick/capture. The distinction matters for what the UI shows:
/// <list type="bullet">
///   <item>Photo set — proceed.</item>
///   <item>Photo null, Error null — the user simply cancelled; say nothing.</item>
///   <item>Photo null, Error set — permission denied or a real failure; show the message.</item>
/// </list>
/// </summary>
public sealed record PhotoPickResult(PickedPhoto? Photo, string? Error)
{
    public static PhotoPickResult Cancelled() => new(null, null);
    public static PhotoPickResult Failed(string error) => new(null, error);
    public static PhotoPickResult Success(PickedPhoto photo) => new(photo, null);
}

/// <summary>
/// Wraps MAUI's MediaPicker plus the runtime permission prompts. Exists as an interface so
/// comment view-models can be tested without a camera, a gallery, or a platform at all.
/// </summary>
public interface IPhotoPickerService
{
    /// <summary>False on devices/emulators with no camera — the UI hides "Take photo" instead of offering a dead option.</summary>
    bool IsCaptureSupported { get; }

    /// <summary>Takes a photo with the camera, requesting the camera permission first.</summary>
    Task<PhotoPickResult> CapturePhotoAsync();

    /// <summary>Picks an existing photo from the gallery.</summary>
    Task<PhotoPickResult> PickPhotoAsync();
}
