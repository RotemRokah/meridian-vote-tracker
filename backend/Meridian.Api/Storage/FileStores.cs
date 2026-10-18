using Azure.Identity;

namespace Meridian.Api.Storage;

// Picks where uploaded files go, from config:
//   Storage:Provider = Local (default)  -> local disk (Storage:Path)
//   Storage:Provider = AzureBlob        -> Azure Blob Storage, container Storage:AzureBlob:Container, connected by
//       Storage:AzureBlob:AccountUrl        Entra ID via DefaultAzureCredential, no secret (preferred), or
//       Storage:AzureBlob:ConnectionString  account key (Azurite, or accounts that allow keys)
// A wrong or incomplete setting stops the API at startup with a clear message.
public static class FileStores
{
    public static IFileStore Create(IServiceProvider services)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var provider = config["Storage:Provider"];

        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
            return ActivatorUtilities.CreateInstance<LocalFileStore>(services);

        if (provider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            var container = config["Storage:AzureBlob:Container"] ?? "imports";
            var accountUrl = config["Storage:AzureBlob:AccountUrl"];
            if (!string.IsNullOrWhiteSpace(accountUrl))
                return new AzureBlobFileStore(new Uri(accountUrl), new DefaultAzureCredential(), container);

            var connectionString = config["Storage:AzureBlob:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(connectionString))
                return new AzureBlobFileStore(connectionString, container);

            throw new InvalidOperationException(
                "Storage:Provider is AzureBlob but neither Storage:AzureBlob:AccountUrl (Entra ID) " +
                "nor Storage:AzureBlob:ConnectionString is set (AZURE_STORAGE_ACCOUNT_URL or AZURE_STORAGE_CONNECTION_STRING in .env).");
        }

        throw new InvalidOperationException($"Unknown Storage:Provider '{provider}'. Use Local or AzureBlob.");
    }
}
