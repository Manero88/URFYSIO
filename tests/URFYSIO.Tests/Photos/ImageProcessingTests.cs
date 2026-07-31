using SkiaSharp;
using URFYSIO.Core.Exceptions;
using URFYSIO.Infrastructure.Services;
using URFYSIO.Shared.Photos;

namespace URFYSIO.Tests.Photos;

/// <summary>
/// Server-side image handling before upload. The point of this step is that a phone photo
/// is several megabytes and several thousand pixels wide, and nothing in the app displays
/// it at anywhere near that size — storing the original would cost storage and bandwidth
/// for no visible benefit.
/// </summary>
public class ImageProcessingTests
{
    /// <summary>Builds a real encoded image of the given size so the decoder has genuine input.</summary>
    private static byte[] MakeImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            // Some contrast, so the encoder can't collapse it to a trivial image.
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(new SKRect(0, 0, width / 2f, height / 2f), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    private static (int Width, int Height) DimensionsOf(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return (bitmap.Width, bitmap.Height);
    }

    [Fact]
    public void ProcessImage_DownscalesAnOversizedPhoto()
    {
        var original = MakeImage(4000, 3000);

        using var input = new MemoryStream(original);
        var (data, contentType) = BlobStorageService.ProcessImage(input, "image/jpeg");

        var (width, height) = DimensionsOf(data);
        Assert.Equal(PhotoRules.MaxDimension, Math.Max(width, height));
        Assert.Equal("image/jpeg", contentType);

        // The whole point: the stored bytes are a fraction of the original.
        Assert.True(data.Length < original.Length,
            $"Expected the processed image ({data.Length} bytes) to be smaller than the original ({original.Length}).");
    }

    [Fact]
    public void ProcessImage_PreservesAspectRatio()
    {
        using var input = new MemoryStream(MakeImage(4000, 2000));
        var (data, _) = BlobStorageService.ProcessImage(input, "image/jpeg");

        var (width, height) = DimensionsOf(data);
        Assert.Equal(PhotoRules.MaxDimension, width);
        Assert.Equal(PhotoRules.MaxDimension / 2, height);
    }

    [Fact]
    public void ProcessImage_BoundsTheLongEdgeForPortraitToo()
    {
        using var input = new MemoryStream(MakeImage(2000, 4000));
        var (data, _) = BlobStorageService.ProcessImage(input, "image/jpeg");

        var (width, height) = DimensionsOf(data);
        Assert.Equal(PhotoRules.MaxDimension, height);
        Assert.True(width <= PhotoRules.MaxDimension);
    }

    [Fact]
    public void ProcessImage_DoesNotUpscaleASmallImage()
    {
        using var input = new MemoryStream(MakeImage(400, 300));
        var (data, _) = BlobStorageService.ProcessImage(input, "image/jpeg");

        var (width, height) = DimensionsOf(data);
        Assert.Equal(400, width);
        Assert.Equal(300, height);
    }

    [Fact]
    public void ProcessImage_LeavesAnImageExactlyAtTheLimitAlone()
    {
        using var input = new MemoryStream(MakeImage(PhotoRules.MaxDimension, 900));
        var (data, _) = BlobStorageService.ProcessImage(input, "image/jpeg");

        var (width, height) = DimensionsOf(data);
        Assert.Equal(PhotoRules.MaxDimension, width);
        Assert.Equal(900, height);
    }

    [Fact]
    public void ProcessImage_KeepsPngAsPng()
    {
        using var input = new MemoryStream(MakeImage(2400, 1200, SKEncodedImageFormat.Png));
        var (data, contentType) = BlobStorageService.ProcessImage(input, "image/png");

        Assert.Equal("image/png", contentType);
        Assert.Equal(PhotoRules.MaxDimension, DimensionsOf(data).Width);
    }

    [Fact]
    public void ProcessImage_RejectsBytesThatAreNotAnImage()
    {
        // Content-Type is client-supplied, so "image/jpeg" on arbitrary bytes has to fail
        // as a clean validation error rather than a 500 from deep inside the decoder.
        using var input = new MemoryStream("this is definitely not a jpeg"u8.ToArray());

        var ex = Assert.Throws<DomainException>(() => BlobStorageService.ProcessImage(input, "image/jpeg"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("could not be read as an image", ex.Message);
    }

    [Fact]
    public void ProcessImage_RejectsAnEmptyStream()
    {
        using var input = new MemoryStream();
        Assert.Throws<DomainException>(() => BlobStorageService.ProcessImage(input, "image/jpeg"));
    }

    [Fact]
    public void ProcessImage_ReadsFromTheStartEvenIfTheStreamWasAlreadyConsumed()
    {
        // IFormFile streams can arrive mid-position; rewinding is the service's job.
        var bytes = MakeImage(2000, 1000);
        using var input = new MemoryStream(bytes);
        input.Position = input.Length;

        var (data, _) = BlobStorageService.ProcessImage(input, "image/jpeg");

        Assert.Equal(PhotoRules.MaxDimension, DimensionsOf(data).Width);
    }
}
