using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meridian.Api.Conflicts;
using Meridian.Api.Imports;
using Meridian.Api.Snapshots;
using Meridian.Api.Votes;
using Microsoft.AspNetCore.Mvc;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// SNP-01..05, POS-04, and the snapshot part of spec 7.2
[Collection(IntegrationCollection.Name)]
public class SnapshotTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _dana;   // Analyst
    private readonly HttpClient _lena;   // Admin

    public SnapshotTests(ApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
        _lena = factory.ClientFor("lena");
    }

    // Own year per test: quarters (one published each) and date filters are shared across the test DB
    private readonly int _year = Random.Shared.Next(2100, 2900);
    private readonly string _p = $"S{Guid.NewGuid():N}"[..9];
    private string Date(int month = 2) => $"{_year}-{month:00}-10";
    private string Vote(int n) => $"{_p}/{n}";

    private Task<ImportDetailsDto> Upload(params string[] lines) =>
        _factory.ImportAndWait(_dana, "votes.csv", Encoding.UTF8.GetBytes(string.Join("\n", lines.Prepend(Header)) + "\n"));

    private async Task<VoteDetailsDto> VoteDetails(string assemblyVoteId)
    {
        var list = (await _dana.GetFromJsonAsync<PagedResult<VoteListItemDto>>(
            $"/api/votes?search={Uri.EscapeDataString(assemblyVoteId)}", ApiFactory.Json))!;
        return (await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{list.Items.Single().Id}", ApiFactory.Json))!;
    }

    private async Task SetPosition(string assemblyVoteId, string stance, int weight)
    {
        var vote = await VoteDetails(assemblyVoteId);
        var response = await _lena.PutAsJsonAsync($"/api/votes/{vote.Id}/position",
            new PositionRequest(stance, weight, vote.Position?.Version));
        response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> Post(CreateSnapshotRequest request) => _dana.PostAsJsonAsync("/api/snapshots", request);

    private async Task<SnapshotDetailsDto> Get(long id) =>
        (await _dana.GetFromJsonAsync<SnapshotDetailsDto>($"/api/snapshots/{id}", ApiFactory.Json))!;

    private async Task<SnapshotDetailsDto> CreateAndWait(CreateSnapshotRequest request)
    {
        var response = await Post(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);   // SNP-02: not blocked
        var queued = (await response.Content.ReadFromJsonAsync<SnapshotDto>(ApiFactory.Json))!;

        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var details = await Get(queued.Id);
            if (details.Snapshot.Status is Meridian.Domain.SnapshotStatus.Completed or Meridian.Domain.SnapshotStatus.Failed)
                return details;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Snapshot {queued.Id} still {details.Snapshot.Status}");
            await Task.Delay(200);
        }
    }

    private CreateSnapshotRequest ByList(string name, params string[] votes) => new(name, null, null, null, votes.ToList());

    private static async Task<ValidationProblemDetails> BadRequest(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }

    [Fact]
    public async Task Scores_follow_the_agreed_formula()
    {
        // V1 For w4, V2 For w2, V3 Against w1 (the table we agreed on)
        string[] Ballots(string country, string b1, string b2, string b3) => new[]
        {
            Line(Vote(1), country, b1, date: Date()), Line(Vote(2), country, b2, date: Date()), Line(Vote(3), country, b3, date: Date()),
        };
        await Upload(new[]
        {
            Ballots("FRA", "A", "X", "N"), Ballots("IND", "Y", "Y", "Y"), Ballots("BRA", "Y", "N", "N"),
            Ballots("DEU", "N", "N", "Y"), Ballots("JPN", "A", "A", "X"),
        }.SelectMany(x => x).ToArray());
        await SetPosition(Vote(1), "For", 4);
        await SetPosition(Vote(2), "For", 2);
        await SetPosition(Vote(3), "Against", 1);

        var snapshot = await CreateAndWait(ByList("formula", Vote(1), Vote(2), Vote(3)));

        Assert.Equal(Meridian.Domain.SnapshotStatus.Completed, snapshot.Snapshot.Status);
        Assert.Equal(3, snapshot.Snapshot.VoteCount);
        var expected = new[]
        {
            // code, score, rank, matched, counted, countedVotes, abstain, absent
            ("FRA", (decimal?)100.00m, (int?)1, 1, 1, 1, 1, 1),   // only V3 counts: N on Against
            ("IND", 85.71m, 2, 6, 7, 3, 0, 0),
            ("BRA", 71.43m, 3, 5, 7, 3, 0, 0),
            ("DEU", 0.00m, 4, 0, 7, 3, 0, 0),
            ("JPN", null, null, 0, 0, 0, 2, 1),                    // no Y/N: N/A, unranked
        };
        Assert.Equal(expected, snapshot.Scores.Select(s =>
            (s.CountryCode, s.Score, s.Rank, s.MatchedWeight, s.CountedWeight, s.CountedVotes, s.AbstainCount, s.AbsentCount)));
        Assert.Equal("Brazil", snapshot.Scores.Single(s => s.CountryCode == "BRA").CountryName);

        // SNP-03: BRA vote by vote
        var bra = (await _dana.GetFromJsonAsync<CountryBreakdownDto>(
            $"/api/snapshots/{snapshot.Snapshot.Id}/countries/bra", ApiFactory.Json))!;
        Assert.Equal(new[]
        {
            (Vote(1), "Y", Meridian.Domain.Stance.For, 4, true, true, 4, 4),
            (Vote(2), "N", Meridian.Domain.Stance.For, 2, true, false, 0, 2),
            (Vote(3), "N", Meridian.Domain.Stance.Against, 1, true, true, 1, 1),
        }.OrderBy(x => x.Item1), bra.Votes.Select(v =>
            (v.AssemblyVoteId, v.Ballot, v.Stance, v.Weight, v.Counted, v.Matched, v.MatchedWeight, v.CountedWeight)).OrderBy(x => x.Item1));

        // 7.4: exactly which data it was based on
        var inputs = (await _dana.GetFromJsonAsync<PagedResult<SnapshotInputDto>>(
            $"/api/snapshots/{snapshot.Snapshot.Id}/inputs", ApiFactory.Json))!;
        Assert.Equal(15, inputs.Total);
    }

    [Fact]
    public async Task Spec_example_and_ties()
    {
        // spec: Y on a weight-4 For vote + N on a weight-2 For vote -> 66.7
        await Upload(
            Line(Vote(1), "BRA", "Y", date: Date()), Line(Vote(2), "BRA", "N", date: Date()),
            Line(Vote(1), "IND", "Y", date: Date()), Line(Vote(2), "IND", "N", date: Date()),
            Line(Vote(1), "FRA", "Y", date: Date()), Line(Vote(2), "FRA", "Y", date: Date()),
            Line(Vote(1), "DEU", "N", date: Date()), Line(Vote(2), "DEU", "N", date: Date()));
        await SetPosition(Vote(1), "For", 4);
        await SetPosition(Vote(2), "For", 2);

        var snapshot = await CreateAndWait(ByList("ties", Vote(1), Vote(2)));

        Assert.Equal(new[] { ("FRA", 100.00m, 1), ("BRA", 66.67m, 2), ("IND", 66.67m, 2), ("DEU", 0.00m, 4) },
            snapshot.Scores.Select(s => (s.CountryCode, s.Score!.Value, s.Rank!.Value)));
    }

    [Fact]
    public async Task Worked_example_published_snapshot_never_changes()
    {
        // spec 7.2: snapshot with BRA = Y, published; Dana approves A; published still Y, a new one uses A
        await Upload(Line(Vote(101), "BRA", "Y", date: Date()), Line(Vote(101), "IND", "Y", date: Date()));
        await Upload(Line(Vote(101), "BRA", "A", date: Date()), Line(Vote(101), "IND", "Y", date: Date()));
        await SetPosition(Vote(101), "For", 1);

        var s1 = await CreateAndWait(ByList("before decision", Vote(101)));
        Assert.Equal(100.00m, s1.Scores.Single(s => s.CountryCode == "BRA").Score);
        Assert.Equal(new[] { $"{Vote(101)} BRA Ballot: Y -> A" }, s1.Rules.PendingConflictsAtCreation);

        var published = await _lena.PostAsJsonAsync($"/api/snapshots/{s1.Snapshot.Id}/publish", new PublishRequest(null));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var pub = (await published.Content.ReadFromJsonAsync<SnapshotDto>(ApiFactory.Json))!;
        Assert.Equal((true, $"{_year}-Q1", "lena"), (pub.IsPublished, pub.Quarter, pub.PublishedBy));   // quarter derived

        // after the snapshot: a conflict decision and a weight change
        var conflict = (await _dana.GetFromJsonAsync<List<ConflictDto>>(
            $"/api/conflicts?vote={Uri.EscapeDataString(Vote(101))}", ApiFactory.Json))!.Single();
        (await _dana.PostAsJsonAsync($"/api/conflicts/{conflict.Id}/decision", new DecisionRequest("Approve", null))).EnsureSuccessStatusCode();
        await SetPosition(Vote(101), "For", 5);

        // SNP-04: the published snapshot is identical, down to its inputs
        var s1Again = await Get(s1.Snapshot.Id);
        Assert.Equal(s1.Scores, s1Again.Scores);
        var braInS1 = (await _dana.GetFromJsonAsync<CountryBreakdownDto>(
            $"/api/snapshots/{s1.Snapshot.Id}/countries/BRA", ApiFactory.Json))!.Votes.Single();
        Assert.Equal(("Y", 1), (braInS1.Ballot, braInS1.Weight));

        // a new snapshot uses the new values: BRA = A (not counted) -> N/A
        var s2 = await CreateAndWait(ByList("after decision", Vote(101)));
        var braInS2 = s2.Scores.Single(s => s.CountryCode == "BRA");
        Assert.Equal(((decimal?)null, (int?)null, 1), (braInS2.Score, braInS2.Rank, braInS2.AbstainCount));
        Assert.Empty(s2.Rules.PendingConflictsAtCreation);

        // SNP-05: one published per quarter; explicit unpublish, both audited
        var second = await _lena.PostAsJsonAsync($"/api/snapshots/{s2.Snapshot.Id}/publish", new PublishRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains($"Snapshot #{s1.Snapshot.Id}", problem.GetProperty("detail").GetString());

        (await _lena.PostAsync($"/api/snapshots/{s1.Snapshot.Id}/unpublish", null)).EnsureSuccessStatusCode();
        (await _lena.PostAsJsonAsync($"/api/snapshots/{s2.Snapshot.Id}/publish", new PublishRequest(null))).EnsureSuccessStatusCode();

        var s1Final = await Get(s1.Snapshot.Id);
        Assert.False(s1Final.Snapshot.IsPublished);
        Assert.Equal(s1.Scores, s1Final.Scores);   // unpublished, numbers unchanged
        Assert.Equal(new[] { "SnapshotPublished", "SnapshotUnpublished" }, s1Final.History.Select(h => h.EventType));
        Assert.All(s1Final.History, h => Assert.Equal("lena", h.Actor));
    }

    [Fact]
    public async Task Vote_without_position_blocks_creation_unless_explicitly_excluded()
    {
        await Upload(Line(Vote(1), "BRA", "Y", date: Date()), Line(Vote(2), "BRA", "N", date: Date()));
        await SetPosition(Vote(1), "For", 3);   // Vote(2) has no position

        // POS-04: blocked, with the list
        var blocked = await Post(ByList("blocked", Vote(1), Vote(2)));
        var problem = await BadRequest(blocked);
        Assert.Contains("1 selected votes have no position", problem.Errors["excludeVotesWithoutPosition"].Single());
        Assert.Equal(new[] { Vote(2) }, problem.Extensions["votesWithoutPosition"] is JsonElement list
            ? list.EnumerateArray().Select(e => e.GetString()) : null);

        // explicit exclusion: allowed for an Analyst, recorded in the rules
        var snapshot = await CreateAndWait(ByList("excluded", Vote(1), Vote(2)) with { ExcludeVotesWithoutPosition = true });
        Assert.Equal(new[] { Vote(2) }, snapshot.Rules.ExcludedVotesWithoutPosition);
        Assert.Equal(1, snapshot.Snapshot.VoteCount);
        Assert.Equal(100.00m, snapshot.Scores.Single(s => s.CountryCode == "BRA").Score);   // only Vote(1)

        // nothing left after excluding
        Assert.Contains("selection", (await BadRequest(await Post(ByList("none", Vote(2)) with { ExcludeVotesWithoutPosition = true }))).Errors.Keys);
    }

    [Fact]
    public async Task Selection_is_the_union_of_filter_and_list()
    {
        await Upload(
            Line(Vote(1), "BRA", "Y", date: Date(1)), Line(Vote(2), "BRA", "Y", date: Date(3)), Line(Vote(3), "BRA", "Y", date: Date(6)));
        foreach (var v in new[] { 1, 2, 3 }) await SetPosition(Vote(v), "For", 1);

        var byRange = await CreateAndWait(new CreateSnapshotRequest("range", new DateOnly(_year, 1, 1), new DateOnly(_year, 3, 31), null, null));
        Assert.Equal(2, byRange.Snapshot.VoteCount);

        var union = await CreateAndWait(new CreateSnapshotRequest("union", new DateOnly(_year, 1, 1), new DateOnly(_year, 3, 31), null, new() { Vote(3) }));
        Assert.Equal(3, union.Snapshot.VoteCount);
        Assert.Equal(new[] { Vote(3) }, union.Filter.VoteIds);
    }

    [Theory]
    [InlineData(null, null, null, null, "name")]
    [InlineData("x", null, null, null, "selection")]
    [InlineData("x", "2100-02-01", "2100-01-01", null, "from")]
    [InlineData("x", null, null, "NO/SUCH/VOTE", "voteIds")]
    [InlineData("x", "1900-01-01", "1900-12-31", null, "selection")]   // nothing in that range
    public async Task Invalid_snapshot_request_is_400_on_the_field(string? name, string? from, string? to, string? vote, string field)
    {
        var request = new CreateSnapshotRequest(name,
            from == null ? null : DateOnly.Parse(from), to == null ? null : DateOnly.Parse(to), null,
            vote == null ? null : new() { vote });

        Assert.Contains(field, (await BadRequest(await Post(request))).Errors.Keys);
    }

    [Fact]
    public async Task Publish_rules()
    {
        await Upload(Line(Vote(1), "BRA", "Y", date: Date(3)), Line(Vote(2), "BRA", "Y", date: Date(4)));
        await SetPosition(Vote(1), "For", 1);
        await SetPosition(Vote(2), "For", 1);
        var span = await CreateAndWait(ByList("two quarters", Vote(1), Vote(2)));
        var id = span.Snapshot.Id;

        // Analyst can't publish (403, enforced in the API)
        Assert.Equal(HttpStatusCode.Forbidden, (await _dana.PostAsJsonAsync($"/api/snapshots/{id}/publish", new PublishRequest(null))).StatusCode);

        // votes in two quarters: the quarter must be chosen
        var noQuarter = await BadRequest(await _lena.PostAsJsonAsync($"/api/snapshots/{id}/publish", new PublishRequest(null)));
        Assert.Contains($"{_year}-Q1 to {_year}-Q2", noQuarter.Errors["quarter"].Single());
        Assert.Contains("quarter", (await BadRequest(await _lena.PostAsJsonAsync($"/api/snapshots/{id}/publish", new PublishRequest($"{_year}-Q5")))).Errors.Keys);

        // explicit quarter works; publishing twice or unpublishing twice is a 409
        (await _lena.PostAsJsonAsync($"/api/snapshots/{id}/publish", new PublishRequest($"{_year}-Q2"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await _lena.PostAsJsonAsync($"/api/snapshots/{id}/publish", new PublishRequest($"{_year}-Q2"))).StatusCode);
        (await _lena.PostAsync($"/api/snapshots/{id}/unpublish", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await _lena.PostAsync($"/api/snapshots/{id}/unpublish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _dana.PostAsync($"/api/snapshots/{id}/unpublish", null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_snapshot_is_404_and_no_user_is_401()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _dana.GetAsync("/api/snapshots/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _dana.GetAsync("/api/snapshots/999999/countries/BRA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _lena.PostAsJsonAsync("/api/snapshots/999999/publish", new PublishRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/snapshots")).StatusCode);
    }
}
