using System.Net.Http.Json;
using System.Text;
using Meridian.Api.Data;
using Meridian.Api.Imports;
using Meridian.Api.Snapshots;
using Meridian.Api.Votes;
using Meridian.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// What the workers do with a job that was Running when the API stopped (restart, crash):
// an import is marked Failed (re-upload is safe), a snapshot goes back to the queue (one transaction, nothing saved).
[Collection(IntegrationCollection.Name)]
public class WorkerRestartTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _dana;

    public WorkerRestartTests(ApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
    }

    // A second API on the same database: its workers run their startup step, like after a restart
    private class RestartedApi : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        public RestartedApi(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", _connectionString);
            builder.UseSetting("Storage:Path", Path.Combine(Path.GetTempPath(), "meridian-tests", Guid.NewGuid().ToString("N")));
        }
    }

    private static async Task<T> WaitFor<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Import_cut_off_by_a_restart_is_marked_Failed_with_a_clear_message()
    {
        long id;
        await using (var db = _factory.NewDbContext())
        {
            var cutOff = new Import
            {
                FileName = "cut-off.csv", FileSha256 = new string('0', 64), StoragePath = "missing",
                UploadedBy = "dana", UploadedAt = DateTime.UtcNow, Status = ImportStatus.Running,
                Stage = ImportStage.Classifying, StartedAt = DateTime.UtcNow,
            };
            db.Imports.Add(cutOff);
            await db.SaveChangesAsync();
            id = cutOff.Id;
        }

        await using var restarted = new RestartedApi(_factory.ConnectionString);
        _ = restarted.Services;   // starts the workers

        var import = await WaitFor(async () =>
        {
            await using var db = _factory.NewDbContext();
            return await db.Imports.AsNoTracking().SingleAsync(i => i.Id == id);
        }, i => i.Status != ImportStatus.Running);

        Assert.Equal(ImportStatus.Failed, import.Status);
        Assert.Contains("Interrupted by an API restart", import.Error);
        Assert.NotNull(import.CompletedAt);
    }

    [Fact]
    public async Task Snapshot_cut_off_by_a_restart_is_queued_again_and_completes()
    {
        // a real snapshot gives valid filter and rules to copy
        var p = $"R{Guid.NewGuid():N}"[..9];
        await _factory.ImportAndWait(_dana, "r.csv", Encoding.UTF8.GetBytes($"{Header}\n{Line($"{p}/1", "BRA", "Y")}\n"));
        var vote = (await _dana.GetFromJsonAsync<PagedResult<VoteListItemDto>>($"/api/votes?search={p}", ApiFactory.Json))!.Items.Single();
        (await _factory.ClientFor("lena").PutAsJsonAsync($"/api/votes/{vote.Id}/position", new PositionRequest("For", 2, null))).EnsureSuccessStatusCode();
        var created = (await (await _dana.PostAsJsonAsync("/api/snapshots",
            new CreateSnapshotRequest("source", null, null, null, new() { $"{p}/1" }))).Content.ReadFromJsonAsync<SnapshotDto>(ApiFactory.Json))!;

        long id;
        await using (var db = _factory.NewDbContext())
        {
            var source = await WaitFor(() => db.Snapshots.AsNoTracking().SingleAsync(s => s.Id == created.Id),
                s => s.Status == SnapshotStatus.Completed);
            var cutOff = new Snapshot
            {
                Name = "cut-off", Filter = source.Filter, Rules = source.Rules, Status = SnapshotStatus.Running,
                CreatedBy = "dana", CreatedAt = DateTime.UtcNow,
            };
            db.Snapshots.Add(cutOff);
            await db.SaveChangesAsync();
            id = cutOff.Id;
        }

        await using var restarted = new RestartedApi(_factory.ConnectionString);
        _ = restarted.Services;

        var snapshot = await WaitFor(async () =>
        {
            await using var db = _factory.NewDbContext();
            return await db.Snapshots.AsNoTracking().SingleAsync(s => s.Id == id);
        }, s => s.Status is SnapshotStatus.Completed or SnapshotStatus.Failed);

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        await using (var db = _factory.NewDbContext())
            Assert.Equal(1, await db.SnapshotScores.CountAsync(s => s.SnapshotId == id && s.CountryCode == "BRA"));
    }
}
