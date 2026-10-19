using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Kernel.Files.Contracts;

namespace Iron.Kernel.Files.Services;

/// <summary>Azure Blob Storage through the Azure SDK, against Azurite on the laptop (ADR-0006). One container, the tenant's.</summary>
public sealed class BlobObjectStorage : IObjectStorage
{
    private readonly BlobContainerClient _container;
    private readonly bool _signed;

    public BlobObjectStorage(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Object storage needs Storage:ConnectionString and Storage:Container, or Storage:ContainerSasUrl. A unit reads them from its secret.");
        }

        _signed = !string.IsNullOrWhiteSpace(options.ContainerSasUrl);
        _container = _signed
            ? new BlobContainerClient(new Uri(options.ContainerSasUrl!))
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
        if (_signed)
        {
            // A container signature cannot create containers; the fleet did that with the account key (ADR-0018).
            var problem = await CheckAsync(cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                throw new InvalidOperationException($"The unit's container is not usable: {problem}");
            }

            return;
        }

        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // One page of one blob: works with the account key and with a container signature (reading container
            // properties does not under a signature), and a missing container answers 404.
            await foreach (var page in _container.GetBlobsAsync(cancellationToken: cancellationToken).AsPages(pageSizeHint: 1).ConfigureAwait(false))
            {
                break;
            }

            return null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return $"The blob container '{_container.Name}' does not exist.";
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
