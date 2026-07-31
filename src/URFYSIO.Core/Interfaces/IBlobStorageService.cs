namespace URFYSIO.Core.Interfaces;

/// <summary>
/// Stores treatment-plan photo attachments in Azure Blob Storage.
///
/// The container is PRIVATE, so nothing here ever hands out a durable public link. What
/// is persisted against a comment is the opaque blob name; a short-lived SAS URL is minted
/// on each read. That keeps a leaked or shoulder-surfed URL from becoming permanent access
/// to a patient's photo — which matters because these are medical-context images.
///
/// Every method is expected to throw on failure; callers decide whether a photo problem
/// should fail the whole request (upload) or degrade gracefully (read/delete).
/// </summary>
public interface IBlobStorageService
{
    /// <summary>
    /// Uploads a photo and returns the generated blob name to persist. The stream is read
    /// from its current position; the caller keeps ownership of it.
    /// </summary>
    Task<string> UploadPhotoAsync(Stream photoStream, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a read-only SAS URL for the blob, valid for about an hour. Generated fresh on
    /// every read rather than stored, so access expires on its own.
    /// </summary>
    Task<string> GetPhotoSasUrlAsync(string blobName, CancellationToken cancellationToken = default);

    /// <summary>Deletes the blob. Succeeds silently when it is already gone.</summary>
    Task DeletePhotoAsync(string blobName, CancellationToken cancellationToken = default);
}
