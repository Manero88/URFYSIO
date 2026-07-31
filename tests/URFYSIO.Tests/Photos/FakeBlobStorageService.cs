using URFYSIO.Core.Interfaces;
using URFYSIO.Infrastructure.Services;
using URFYSIO.Shared.Photos;

namespace URFYSIO.Tests.Photos;

/// <summary>
/// In-memory stand-in for <see cref="IBlobStorageService"/>. The Azure SDK's
/// BlobContainerClient is sealed-ish and non-virtual, so the realistic way to test
/// everything built on top of storage is a double at the interface — which is also where
/// the production seam already is.
///
/// Records every call so tests can assert not just the happy path but the cleanup
/// behaviour: orphaned blobs deleted when an insert fails, photos erased on GDPR delete.
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    private readonly Dictionary<string, byte[]> _blobs = new();

    public List<string> Uploaded { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<string> SasRequested { get; } = [];

    /// <summary>When set, upload throws this — simulating a storage outage.</summary>
    public Exception? UploadException { get; set; }

    /// <summary>When set, SAS generation throws this — simulating an expired key or outage.</summary>
    public Exception? SasException { get; set; }

    /// <summary>When set, delete throws this — used to prove cleanup failures don't break the caller.</summary>
    public Exception? DeleteException { get; set; }

    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public Task<string> UploadPhotoAsync(
        Stream photoStream, string contentType, CancellationToken cancellationToken = default)
    {
        if (UploadException is not null) throw UploadException;

        // Run the real processing step rather than just copying bytes. Without this the
        // double would silently accept input the production service rejects — notably a
        // non-image mislabelled as image/jpeg, which only fails at decode time. A double
        // that accepts more than the real thing hides exactly the bugs worth catching.
        var (data, resolvedContentType) = BlobStorageService.ProcessImage(photoStream, contentType);

        // Mirrors the real naming scheme so assertions about extensions stay meaningful.
        var blobName = $"{Guid.NewGuid():N}{PhotoRules.ExtensionFor(resolvedContentType)}";
        _blobs[blobName] = data;
        Uploaded.Add(blobName);

        return Task.FromResult(blobName);
    }

    public Task<string> GetPhotoSasUrlAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (SasException is not null) throw SasException;

        SasRequested.Add(blobName);
        return Task.FromResult(
            $"https://urfysiostorage.blob.core.windows.net/treatment-photos/{blobName}?sig=fake-signature");
    }

    public Task DeletePhotoAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (DeleteException is not null) throw DeleteException;

        Deleted.Add(blobName);
        _blobs.Remove(blobName);
        return Task.CompletedTask;
    }
}
