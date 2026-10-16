using System.Security.Cryptography;

namespace Meridian.Api.Storage;

public record StoredFile(string StoragePath, string Sha256, long Size);

// Where uploaded files live. Local disk now; Azure Blob can implement the same interface (step 11).
public interface IFileStore
{
    Task<StoredFile> SaveAsync(Stream content, CancellationToken ct);
    Stream OpenRead(string storagePath);
}

public class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(IConfiguration config, IWebHostEnvironment env)
    {
        // Storage:Path, relative to the app folder unless absolute (docker: /app/uploads volume)
        _root = Path.GetFullPath(Path.Combine(env.ContentRootPath, config["Storage:Path"] ?? "uploads"));
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken ct)
    {
        // our own file name: never trust the uploaded name for a path
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy-MM-dd"), $"{Guid.NewGuid():N}.csv");
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await using (var file = File.Create(full))
            await content.CopyToAsync(file, ct);

        // hash of the stored bytes: lets us spot the same file uploaded twice
        await using var stored = File.OpenRead(full);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stored, ct)).ToLowerInvariant();
        return new StoredFile(relative.Replace('\\', '/'), hash, stored.Length);
    }

    public Stream OpenRead(string storagePath) => File.OpenRead(Path.Combine(_root, storagePath));
}
