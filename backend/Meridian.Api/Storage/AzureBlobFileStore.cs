using System.Security.Cryptography;
using Azure.Core;
using Azure.Storage.Blobs;

namespace Meridian.Api.Storage;

// Uploaded files in Azure Blob Storage (step 11). Same contract as LocalFileStore.
// Two ways to connect:
//   account URL + credential (Entra ID, no secret: DefaultAzureCredential = Managed Identity in Azure,
//   `az login` on a developer machine) - the way to run in production
//   connection string with an account key - Azurite in tests, or accounts that allow keys
public class AzureBlobFileStore : IFileStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobFileStore(Uri accountUrl, TokenCredential credential, string containerName)
        : this(new BlobServiceClient(accountUrl, credential).GetBlobContainerClient(containerName)) { }

    public AzureBlobFileStore(string connectionString, string containerName)
        : this(new BlobContainerClient(connectionString, containerName)) { }

    private AzureBlobFileStore(BlobContainerClient container)
    {
        _container = container;
        _container.CreateIfNotExists();   // at startup: wrong access fails here, not on the first upload
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken ct)
    {
        // our own blob name: never trust the uploaded name for a path
        var name = $"{DateTime.UtcNow:yyyy-MM-dd}/{Guid.NewGuid():N}.csv";

        // uploads arrive as a seekable stream (ASP.NET buffers form files), so: hash, rewind, upload
        if (!content.CanSeek)
            throw new InvalidOperationException("AzureBlobFileStore needs a seekable stream.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
        content.Position = 0;
        await _container.GetBlobClient(name).UploadAsync(content, overwrite: false, ct);

        return new StoredFile(name, hash, content.Length);
    }

    // streamed: a 100 MB file is read in chunks, never loaded whole
    public Stream OpenRead(string storagePath) => _container.GetBlobClient(storagePath).OpenRead();
}
