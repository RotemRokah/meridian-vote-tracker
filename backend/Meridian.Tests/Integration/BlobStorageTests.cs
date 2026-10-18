using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Meridian.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.Azurite;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// The API with Storage:Provider = AzureBlob, against Azurite (Microsoft's Azure Storage emulator) in Docker:
// no Azure account needed, so it runs on any machine.
public class BlobApiFactory : ApiFactory
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.34.0")
        .WithCommand("--skipApiVersionCheck")   // let the newest SDK talk to the emulator
        .Build();

    public string BlobConnectionString => _azurite.GetConnectionString();

    public override async Task InitializeAsync()
    {
        await _azurite.StartAsync();   // before the API starts: it creates the container at startup
        await base.InitializeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:Provider", "AzureBlob");
        builder.UseSetting("Storage:AzureBlob:ConnectionString", BlobConnectionString);
        builder.UseSetting("Storage:AzureBlob:Container", "imports");
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _azurite.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class BlobCollection : ICollectionFixture<BlobApiFactory>
{
    public const string Name = "blob";
}

[Collection(BlobCollection.Name)]
public class BlobStorageTests
{
    private readonly BlobApiFactory _factory;

    public BlobStorageTests(BlobApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Uploaded_file_is_stored_in_blob_storage_and_the_import_reads_it_from_there()
    {
        var csv = Encoding.UTF8.GetBytes(string.Join("\n", Header,
            Line("BLOB/1", "BRA", "Y"), Line("BLOB/1", "IND", "N"), Line("BLOB/1", "FRN", "Y")) + "\n");

        var details = await _factory.ImportAndWait(_factory.ClientFor("dana"), "blob.csv", csv);

        // the worker read the file back from blob storage and processed it
        Assert.Equal(ImportStatus.Completed, details.Import.Status);
        Assert.Equal(2, details.Summary!.Counts[RowClassification.New]);
        Assert.Equal(1, details.Summary.Counts[RowClassification.Invalid]);

        // the blob holds exactly the uploaded bytes, and the stored hash matches them
        await using var db = _factory.NewDbContext();
        var import = await db.Imports.SingleAsync(i => i.Id == details.Import.Id);
        var blob = new BlobContainerClient(_factory.BlobConnectionString, "imports").GetBlobClient(import.StoragePath);
        Assert.True(await blob.ExistsAsync());
        Assert.Equal(csv, (await blob.DownloadContentAsync()).Value.Content.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(csv)).ToLowerInvariant(), import.FileSha256);
    }
}

// Wrong storage settings stop the API at startup, with a message that says what to fix
public class StorageConfigurationTests
{
    private static Exception StartWith(params (string Key, string Value)[] settings)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", "Host=not-used-the-api-stops-before-the-database");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });
        return Record.Exception(() => factory.CreateClient())!;
    }

    [Fact]
    public void AzureBlob_without_an_account_url_or_connection_string_stops_startup()
    {
        var ex = StartWith(("Storage:Provider", "AzureBlob"));
        Assert.Contains("neither Storage:AzureBlob:AccountUrl (Entra ID) nor Storage:AzureBlob:ConnectionString is set", ex.ToString());
    }

    [Fact]
    public void Unknown_storage_provider_stops_startup()
    {
        var ex = StartWith(("Storage:Provider", "S3"));
        Assert.Contains("Unknown Storage:Provider 'S3'", ex.ToString());
    }
}
