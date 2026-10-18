using System.Security.Cryptography;
using Azure.Identity;
using Azure.Storage.Blobs;
using Meridian.Api.Storage;
using Xunit.Abstractions;

namespace Meridian.Tests.Integration;

// One-time check against a real Azure Storage account through Entra ID (no key, no connection string).
// Opt-in: runs only when MERIDIAN_AZURE_BLOB_ACCOUNT_URL is set (e.g. https://<account>.blob.core.windows.net)
// and the machine is signed in (`az login`); otherwise Skipped. Uses the production class with
// DefaultAzureCredential - the same path Managed Identity takes when the API runs in Azure.
// The uploaded blob is left in place, so it can be seen in the portal (container "imports").
public class AzureBlobLiveTests
{
    private readonly ITestOutputHelper _out;

    public AzureBlobLiveTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [SkippableFact]
    public async Task Uploads_a_file_to_the_real_account_with_Entra_ID_and_reads_it_back()
    {
        var accountUrl = Environment.GetEnvironmentVariable("MERIDIAN_AZURE_BLOB_ACCOUNT_URL");
        Skip.If(string.IsNullOrWhiteSpace(accountUrl), "MERIDIAN_AZURE_BLOB_ACCOUNT_URL not set: live Azure check skipped.");

        // a developer machine has no Managed Identity, and its failed probe stops DefaultAzureCredential's chain
        // before it reaches `az login`. Skip just that source here; the API itself keeps the full chain (MI in Azure).
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true });

        var file = Path.Combine(AppContext.BaseDirectory, "TestData", "assembly_export_2024_q1_rev2.csv");
        var bytes = await File.ReadAllBytesAsync(file);

        var store = new AzureBlobFileStore(new Uri(accountUrl!), credential, "imports");
        StoredFile stored;
        await using (var content = new MemoryStream(bytes))
            stored = await store.SaveAsync(content, CancellationToken.None);
        _out.WriteLine($"uploaded to container 'imports' as {stored.StoragePath} ({stored.Size} bytes)");

        // read back through the same store, as the import worker would
        await using var read = store.OpenRead(stored.StoragePath);
        var back = new MemoryStream();
        await read.CopyToAsync(back);
        Assert.Equal(bytes, back.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), stored.Sha256);

        // and it is really there, seen by a separate client
        var blob = new BlobServiceClient(new Uri(accountUrl!), credential)
            .GetBlobContainerClient("imports").GetBlobClient(stored.StoragePath);
        Assert.True(await blob.ExistsAsync());
    }
}
