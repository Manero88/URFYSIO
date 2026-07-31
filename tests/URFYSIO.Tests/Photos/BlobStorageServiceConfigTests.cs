using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using URFYSIO.Infrastructure.Services;

namespace URFYSIO.Tests.Photos;

/// <summary>
/// Configuration behaviour of the real service. The important guarantee is that a missing
/// storage setting is a photo-feature problem and nothing more — resolving the connection
/// string in the constructor would instead take the whole API down at startup, since the
/// service is resolved per request.
/// </summary>
public class BlobStorageServiceConfigTests
{
    private static BlobStorageService Create(string? connectionString)
    {
        var settings = new Dictionary<string, string?>();
        if (connectionString is not null) settings["Storage:ConnectionString"] = connectionString;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new BlobStorageService(configuration, Mock.Of<ILogger<BlobStorageService>>());
    }

    [Fact]
    public void Constructing_WithNoConnectionString_DoesNotThrow()
    {
        // The whole point of lazy initialisation: constructing this must never be what
        // breaks an environment where photos simply aren't configured.
        var exception = Record.Exception(() => Create(null));
        Assert.Null(exception);
    }

    [Fact]
    public async Task UploadPhotoAsync_WithNoConnectionString_ExplainsWhatToSet()
    {
        var service = Create(null);
        using var stream = new MemoryStream([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadPhotoAsync(stream, "image/jpeg"));

        // Names the exact setting, so the failure is self-diagnosing in a log.
        Assert.Contains("Storage:ConnectionString", ex.Message);
        Assert.Contains("Storage__ConnectionString", ex.Message);
    }

    [Fact]
    public async Task UploadPhotoAsync_WithThePlaceholderValue_IsTreatedAsUnconfigured()
    {
        // appsettings.json ships this placeholder for values that belong in App Settings;
        // it would otherwise fail deep inside the Azure SDK with an opaque parse error.
        var service = Create("REPLACE_IN_AZURE_APP_SETTINGS");
        using var stream = new MemoryStream([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UploadPhotoAsync(stream, "image/jpeg"));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task GetPhotoSasUrlAsync_RejectsAnEmptyBlobName()
    {
        var service = Create(null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPhotoSasUrlAsync(""));
    }

    [Fact]
    public async Task DeletePhotoAsync_IgnoresAnEmptyBlobName()
    {
        // Callers iterate over possibly-empty blob names during cleanup; a blank one is a
        // no-op rather than a reason to fail an erasure.
        var service = Create(null);
        var exception = await Record.ExceptionAsync(() => service.DeletePhotoAsync(""));
        Assert.Null(exception);
    }
}
