using Meridian.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Meridian.Tests.Integration;

// Starts a real PostgreSQL 16 in Docker, then the API in memory against it.
// The API applies migrations and seeds on startup, exactly like in docker compose.
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _ = Services; // start the app now: runs migrations + seed once for all tests
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);

    // A fresh DbContext per use, so a failed SaveChanges doesn't leak into the next test
    public MeridianDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<MeridianDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

// All integration test classes share one container (starting PostgreSQL takes a few seconds)
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "integration";
}
