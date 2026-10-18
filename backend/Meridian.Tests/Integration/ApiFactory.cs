using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Meridian.Tests.Integration;

// Starts a real PostgreSQL 16 in Docker, then the API in memory against it.
// The API applies migrations and seeds on startup, and runs the import worker, exactly like in docker compose.
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // same PostgreSQL settings as docker-compose.yml, so volume tests measure what reviewers run
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", "max_wal_size=4GB", "-c", "shared_buffers=512MB")
        .Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "meridian-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString => _postgres.GetConnectionString();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public virtual async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _ = Services; // start the app now: runs migrations + seed once for all tests
    }

    public LogCapture Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Storage:Path", _storage);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    // A fresh DbContext per use, so a failed SaveChanges doesn't leak into the next test
    public MeridianDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<MeridianDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public HttpClient ClientFor(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    // Upload a CSV and wait until the worker is done with it
    public async Task<ImportDetailsDto> ImportAndWait(HttpClient client, string fileName, byte[] content, TimeSpan? timeout = null)
    {
        var response = await Upload(client, fileName, content);
        response.EnsureSuccessStatusCode();
        var queued = (await response.Content.ReadFromJsonAsync<ImportDto>(Json))!;
        return await WaitForImport(client, queued.Id, timeout);
    }

    public static Task<HttpResponseMessage> Upload(HttpClient client, string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", fileName } };
        return client.PostAsync("/api/imports", form);
    }

    public static async Task<ImportDetailsDto> WaitForImport(HttpClient client, long id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (true)
        {
            var details = (await client.GetFromJsonAsync<ImportDetailsDto>($"/api/imports/{id}", Json))!;
            if (details.Import.Status is ImportStatus.Completed or ImportStatus.Failed)
                return details;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Import {id} still {details.Import.Status} at the deadline");
            await Task.Delay(200);
        }
    }

    public new virtual async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}

// All integration test classes share one container (starting PostgreSQL takes a few seconds)
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "integration";
}

// The real-file test needs an empty database of its own: it asserts exact counts
[CollectionDefinition(Name)]
public class RealFilesCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "real-files";
}
