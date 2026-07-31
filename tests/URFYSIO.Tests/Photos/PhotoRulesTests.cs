using URFYSIO.Shared.Photos;

namespace URFYSIO.Tests.Photos;

/// <summary>
/// Upload validation for comment photo attachments. These rules run on both sides — the
/// app checks before spending a mobile upload, the API checks because a client's
/// Content-Type header is not trustworthy.
/// </summary>
public class PhotoRulesTests
{
    // ---- Accepted types ----

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("IMAGE/JPEG")]                 // casing varies by picker
    [InlineData("image/jpg")]                  // not a real MIME type, but Android sends it
    [InlineData("image/jpeg; charset=binary")] // parameters must not defeat the match
    public void IsAllowedContentType_AcceptsJpegAndPng(string contentType)
    {
        Assert.True(PhotoRules.IsAllowedContentType(contentType));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/svg+xml")] // can carry script — must never be renderable from our container
    [InlineData("application/pdf")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowedContentType_RejectsEverythingElse(string? contentType)
    {
        Assert.False(PhotoRules.IsAllowedContentType(contentType));
    }

    // ---- Validate ----

    [Fact]
    public void Validate_AcceptsAReasonableJpeg()
    {
        Assert.Null(PhotoRules.Validate("image/jpeg", 250 * 1024));
    }

    [Fact]
    public void Validate_RejectsNonImageWithAUsableMessage()
    {
        var error = PhotoRules.Validate("application/pdf", 1024);

        Assert.NotNull(error);
        // The message is shown verbatim to the user, so it must say what IS accepted.
        Assert.Contains("JPEG", error);
        Assert.Contains("PNG", error);
    }

    [Fact]
    public void Validate_RejectsOversizedUpload()
    {
        var error = PhotoRules.Validate("image/jpeg", PhotoRules.MaxBytes + 1);

        Assert.NotNull(error);
        Assert.Contains("too large", error, StringComparison.OrdinalIgnoreCase);
        // Both the actual and the permitted size, so the user knows how far over they are.
        Assert.Contains("5 MB", error);
    }

    [Fact]
    public void Validate_AcceptsExactlyTheLimit()
    {
        Assert.Null(PhotoRules.Validate("image/jpeg", PhotoRules.MaxBytes));
    }

    [Fact]
    public void Validate_RejectsEmptyFile()
    {
        var error = PhotoRules.Validate("image/jpeg", 0);

        Assert.NotNull(error);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ChecksTypeBeforeSize()
    {
        // A 10 MB PDF is both wrong-type and oversized; the type is the more useful
        // complaint because shrinking it would not help.
        var error = PhotoRules.Validate("application/pdf", PhotoRules.MaxBytes * 2);

        Assert.NotNull(error);
        Assert.Contains("JPEG", error);
    }

    // ---- Extension / normalisation ----

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/jpg", ".jpg")]
    [InlineData(null, ".jpg")] // default rather than throwing
    public void ExtensionFor_MapsContentTypeToExtension(string? contentType, string expected)
    {
        Assert.Equal(expected, PhotoRules.ExtensionFor(contentType));
    }

    [Fact]
    public void Normalise_MapsJpgOntoJpeg()
    {
        Assert.Equal("image/jpeg", PhotoRules.Normalise("IMAGE/JPG"));
    }

    [Fact]
    public void Normalise_StripsParametersAndWhitespace()
    {
        Assert.Equal("image/png", PhotoRules.Normalise("  image/png ; charset=binary "));
    }
}
