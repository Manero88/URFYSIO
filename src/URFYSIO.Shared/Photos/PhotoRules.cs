namespace URFYSIO.Shared.Photos;

/// <summary>
/// Upload rules for treatment-plan photo attachments, kept as pure functions so both the
/// API (which rejects bad uploads) and the tests can use them without touching Azure.
///
/// Only JPEG and PNG are accepted. That is deliberately narrow: the container is served
/// back to clients via SAS URLs and rendered directly in the app, so allowing arbitrary
/// types (SVG in particular, which can carry script) would turn an image field into a
/// content-injection surface.
/// </summary>
public static class PhotoRules
{
    /// <summary>Largest upload accepted, before server-side downscaling.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Longest edge kept when re-encoding. Phone photos are far larger than any view needs.</summary>
    public const int MaxDimension = 1600;

    /// <summary>JPEG quality used when re-encoding a downscaled image.</summary>
    public const int JpegQuality = 85;

    public const string JpegContentType = "image/jpeg";
    public const string PngContentType = "image/png";

    /// <summary>
    /// Accepted content types. "image/jpg" is not a real MIME type but some Android
    /// pickers send it, so it is tolerated and normalised to image/jpeg.
    /// </summary>
    public static bool IsAllowedContentType(string? contentType) =>
        Normalise(contentType) is JpegContentType or PngContentType;

    /// <summary>
    /// Validates an upload. Returns null when acceptable, otherwise a message written for
    /// the end user â€” it is surfaced verbatim in the app.
    /// </summary>
    public static string? Validate(string? contentType, long lengthBytes)
    {
        if (lengthBytes <= 0)
            return "The selected photo appears to be empty. Please choose another image.";

        if (!IsAllowedContentType(contentType))
            return "Only JPEG and PNG images can be attached. Please choose a different photo.";

        if (lengthBytes > MaxBytes)
            return $"That photo is too large ({FormatSize(lengthBytes)}). " +
                   $"The maximum size is {FormatSize(MaxBytes)}.";

        return null;
    }

    /// <summary>File extension for a validated content type; used to build the blob name.</summary>
    public static string ExtensionFor(string? contentType) =>
        Normalise(contentType) == PngContentType ? ".png" : ".jpg";

    /// <summary>
    /// Lowercases and strips any parameters ("image/jpeg; charset=..."), then maps the
    /// non-standard image/jpg onto image/jpeg.
    /// </summary>
    public static string Normalise(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var value = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return value == "image/jpg" ? JpegContentType : value;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (double)(1024 * 1024):0.#} MB"
            : $"{bytes / 1024d:0} KB";
}
