using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using URFYSIO.Core.Exceptions;
using URFYSIO.Core.Interfaces;
using URFYSIO.Shared.Photos;

namespace URFYSIO.Infrastructure.Services;

/// <summary>
/// Azure Blob Storage implementation for treatment-plan photo attachments.
///
/// Initialisation is LAZY on purpose. The obvious alternative — resolving the connection
/// string in the constructor and throwing when it is absent — would take the entire API
/// down at startup on any environment where the storage setting hasn't been configured,
/// since this service is resolved per request. Photos are one optional feature; losing
/// appointments and treatment plans because of them would be a bad trade. Instead the
/// client is built on first use and a missing setting surfaces as a clear error on the
/// photo path only.
///
/// Images are re-encoded before upload (see <see cref="ProcessImage"/>): a 10 MB phone
/// photo becomes a couple hundred KB, and because the pixels are re-encoded rather than
/// copied, EXIF metadata — including GPS coordinates, which phone cameras attach by
/// default — does not reach storage. That matters for photos taken in a medical context.
/// </summary>
public class BlobStorageService : IBlobStorageService
{
    public const string ContainerName = "treatment-photos";
    private const string ConnectionStringKey = "Storage:ConnectionString";

    /// <summary>SAS lifetime. Long enough to load a thread of photos, short enough that a leaked URL dies quickly.</summary>
    private static readonly TimeSpan SasLifetime = TimeSpan.FromHours(1);

    /// <summary>Backdated SAS start, so a client whose clock runs slightly fast doesn't get a 403.</summary>
    private static readonly TimeSpan SasClockSkew = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly ILogger<BlobStorageService> _logger;

    // Created once per process. BlobContainerClient is thread-safe and intended to be reused.
    private static BlobContainerClient? _containerClient;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> UploadPhotoAsync(
        Stream photoStream, string contentType, CancellationToken cancellationToken = default)
    {
        var container = await GetContainerAsync(cancellationToken);

        // Re-encode before upload. This also proves the bytes really are an image:
        // Content-Type is supplied by the client and cannot be trusted on its own.
        var (data, resolvedContentType) = ProcessImage(photoStream, contentType);

        var blobName = $"{Guid.NewGuid():N}{PhotoRules.ExtensionFor(resolvedContentType)}";
        var blob = container.GetBlobClient(blobName);

        using var upload = new MemoryStream(data, writable: false);
        await blob.UploadAsync(
            upload,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = resolvedContentType }
            },
            cancellationToken);

        _logger.LogInformation(
            "Uploaded treatment photo {BlobName} ({Bytes} bytes, {ContentType}).",
            blobName, data.Length, resolvedContentType);

        return blobName;
    }

    public async Task<string> GetPhotoSasUrlAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobName))
            throw new ArgumentException("Blob name is required.", nameof(blobName));

        var container = await GetContainerAsync(cancellationToken);
        var blob = container.GetBlobClient(blobName);

        // Requires the client to hold the account key, which it does when built from a
        // connection string. If this is ever switched to managed identity, this needs to
        // become a user-delegation SAS instead.
        if (!blob.CanGenerateSasUri)
            throw new InvalidOperationException(
                "Cannot generate a photo access URL: the storage client has no shared key. " +
                "Check that Storage:ConnectionString includes an AccountKey.");

        var builder = new BlobSasBuilder
        {
            BlobContainerName = ContainerName,
            BlobName = blobName,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.Subtract(SasClockSkew),
            ExpiresOn = DateTimeOffset.UtcNow.Add(SasLifetime)
        };
        builder.SetPermissions(BlobSasPermissions.Read);

        return blob.GenerateSasUri(builder).ToString();
    }

    public async Task DeletePhotoAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobName)) return;

        var container = await GetContainerAsync(cancellationToken);
        await container.GetBlobClient(blobName)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

        _logger.LogInformation("Deleted treatment photo {BlobName}.", blobName);
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerClient is not null) return _containerClient;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_containerClient is not null) return _containerClient;

            var connectionString = _configuration[ConnectionStringKey];
            if (string.IsNullOrWhiteSpace(connectionString)
                || connectionString.Contains("REPLACE_IN_AZURE_APP_SETTINGS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Photo storage is not configured: '{ConnectionStringKey}' is missing. " +
                    "Set Storage__ConnectionString in Azure App Settings (or user secrets locally).");
            }

            var client = new BlobContainerClient(connectionString, ContainerName);

            // Idempotent, and only ever runs once per process. Explicitly PublicAccessType.None
            // so a container created by this path can never be world-readable.
            await client.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

            _containerClient = client;
            return _containerClient;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Decodes, downscales to <see cref="PhotoRules.MaxDimension"/> on the longest edge, and
    /// re-encodes. Returns the bytes to store plus the content type they were encoded as.
    /// Throws a validation <see cref="DomainException"/> when the bytes aren't a decodable
    /// image, so a mislabelled or corrupt upload becomes a clear 400 rather than a 500.
    /// </summary>
    internal static (byte[] Data, string ContentType) ProcessImage(Stream input, string contentType)
    {
        var normalised = PhotoRules.Normalise(contentType);
        var isPng = normalised == PhotoRules.PngContentType;

        using var bitmap = DecodeOriented(input)
            ?? throw DomainException.Validation(
                "That file could not be read as an image. Please choose a JPEG or PNG photo.");

        var longestEdge = Math.Max(bitmap.Width, bitmap.Height);
        using var scaled = longestEdge <= PhotoRules.MaxDimension
            ? null
            : ResizeTo(bitmap, PhotoRules.MaxDimension / (double)longestEdge);

        var source = scaled ?? bitmap;

        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(
            isPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
            PhotoRules.JpegQuality);

        if (encoded is null)
            throw DomainException.Validation("That photo could not be processed. Please try a different image.");

        return (encoded.ToArray(), isPng ? PhotoRules.PngContentType : PhotoRules.JpegContentType);
    }

    private static SKBitmap ResizeTo(SKBitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, new SKRect(0, 0, width, height));
        }
        return target;
    }

    /// <summary>
    /// Decodes and applies the EXIF orientation. Phone cameras record rotation as metadata
    /// rather than rotating the pixels, so decoding naively leaves portrait photos on their
    /// side — and since we re-encode (dropping the metadata), the viewer gets no chance to
    /// correct it afterwards.
    /// </summary>
    private static SKBitmap? DecodeOriented(Stream input)
    {
        if (input.CanSeek) input.Position = 0;

        // Buffer once: SKCodec needs to read the stream, and we may need a second pass.
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        buffer.Position = 0;

        using var codec = SKCodec.Create(buffer);
        if (codec is null) return null;

        buffer.Position = 0;
        var bitmap = SKBitmap.Decode(buffer);
        if (bitmap is null) return null;

        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft) return bitmap;

        try
        {
            return ApplyOrigin(bitmap, origin);
        }
        catch
        {
            // Orientation is cosmetic — never fail an upload over it.
            return bitmap;
        }
    }

    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        // Quarter-turn origins swap the output's width and height.
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;

        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);

        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1).PostConcat(SKMatrix.CreateTranslation(width, 0)),
            SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, width / 2f, height / 2f),
            SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1).PostConcat(SKMatrix.CreateTranslation(0, height)),
            SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateScale(-1, 1))
                                                                        .PostConcat(SKMatrix.CreateTranslation(width, 0)),
            SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(width, 0)),
            SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateScale(-1, 1))
                                                                             .PostConcat(SKMatrix.CreateTranslation(width, height))
                                                                             .PostConcat(SKMatrix.CreateScale(1, 1)),
            SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, height)),
            _ => SKMatrix.Identity
        };

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        source.Dispose();
        return target;
    }
}
