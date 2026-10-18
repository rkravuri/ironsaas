using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Files.Contracts;

namespace Iron.Files.Services;

/// <summary>Azure Blob Storage through the Azure SDK, against Azurite on the laptop (ADR-0006). One container, the tenant's.</summary>
public sealed class BlobObjectStorage : IObjectStorage
{
    private readonly BlobContainerClient _container;

    public BlobObjectStorage(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Object storage needs Storage:ConnectionString and Storage:Container, or Storage:ContainerSasUrl. A unit reads them from its secret.");
        }

        _container = !string.IsNullOrWhiteSpace(options.ContainerSasUrl)
            ? new BlobContainerClient(new Uri(options.ContainerSasUrl))
            : new BlobContainerClient(options.ConnectionString, options.Container);
    }

    public string ContainerName => _container.Name;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        var blob = _container.GetBlobClient(key);
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var response = await _container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var details = response.Value.Details;
            return new StoredObject(response.Value.Content, details.ContentType ?? "application/octet-stream", details.ContentLength);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var response = await _container.DeleteBlobIfExistsAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    public async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _container.ExistsAsync(cancellationToken).ConfigureAwait(false);
            return exists.Value ? null : $"The blob container '{_container.Name}' does not exist.";
        }
        catch (RequestFailedException exception)
        {
            return $"Object storage does not answer: {exception.Status} {exception.ErrorCode}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or AggregateException)
        {
            return $"Object storage does not answer: {exception.Message}";
        }
    }
}
