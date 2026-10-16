using System.Net;
using System.Net.Http.Json;
using System.Text;
using Meridian.Api.Conflicts;
using Meridian.Api.Imports;
using Meridian.Api.Votes;
using Microsoft.AspNetCore.Mvc;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// HIS-02, HIS-04: the vote screen and its timeline
[Collection(IntegrationCollection.Name)]
public class VoteHistoryTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _dana;
    private readonly HttpClient _omer;

    public VoteHistoryTests(ApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
        _omer = factory.ClientFor("omer");
    }

    private static string Prefix() => $"H{Guid.NewGuid():N}"[..9];

    private Task<ImportDetailsDto> Upload(HttpClient client, string fileName, params string[] lines) =>
        _factory.ImportAndWait(client, fileName, Encoding.UTF8.GetBytes(string.Join("\n", lines.Prepend(Header)) + "\n"));

    private async Task<PagedResult<VoteListItemDto>> ListVotes(string query) =>
        (await _dana.GetFromJsonAsync<PagedResult<VoteListItemDto>>($"/api/votes?{query}", ApiFactory.Json))!;

    private async Task<long> VoteId(string assemblyVoteId) =>
        (await ListVotes($"search={Uri.EscapeDataString(assemblyVoteId)}")).Items.Single().Id;

    private async Task<List<TimelineEntryDto>> Timeline(long voteId, string? country = null) =>
        (await _dana.GetFromJsonAsync<List<TimelineEntryDto>>(
            $"/api/votes/{voteId}/timeline{(country == null ? "" : "?country=" + country)}", ApiFactory.Json))!;

    [Fact]
    public async Task Worked_example_vote_screen_and_timeline()
    {
        // spec 7.2 up to Dana's decision
        var vote = $"{Prefix()}/101";
        var original = await Upload(_dana, "original.csv",
            Line(vote, "BRA", "Y", title: "Title 101"), Line(vote, "IND", "Y", title: "Title 101"));
        var revised = await Upload(_omer, "revised.csv",
            Line(vote, "BRA", "A", title: "Title 101 revised"), Line(vote, "IND", "Y", title: "Title 101 revised"));
        var conflicts = (await _dana.GetFromJsonAsync<List<ConflictDto>>(
            $"/api/conflicts?vote={Uri.EscapeDataString(vote)}", ApiFactory.Json))!;
        var bra = conflicts.Single(c => c.CountryCode == "BRA");
        (await _dana.PostAsJsonAsync($"/api/conflicts/{bra.Id}/decision", new DecisionRequest("Approve", "checked"))).EnsureSuccessStatusCode();

        var id = await VoteId(vote);

        // vote screen: current values, their source, and the "changed by decision" marker (HIS-02)
        var details = (await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{id}", ApiFactory.Json))!;
        Assert.Equal(("Title 101", "original.csv"), (details.Title, details.DetailsSource.FileName));
        var braBallot = details.Ballots.Single(b => b.CountryCode == "BRA");
        Assert.Equal(("Brazil", "A", "revised.csv", true, "dana"),
            (braBallot.CountryName, braBallot.Ballot, braBallot.Source.FileName, braBallot.ChangedByDecision, braBallot.ApprovedBy));
        var indBallot = details.Ballots.Single(b => b.CountryCode == "IND");
        Assert.Equal(("Y", "original.csv", false), (indBallot.Ballot, indBallot.Source.FileName, indBallot.ChangedByDecision));
        Assert.Equal(1, details.BallotCounts["Y"]);
        Assert.Equal(1, details.BallotCounts["A"]);
        Assert.Null(details.Position);
        var open = Assert.Single(details.OpenConflicts);   // title conflict still pending
        Assert.Equal(Meridian.Domain.ConflictField.Title, open.Field);

        // full timeline (HIS-04): files that touched the vote, and what changed, in order
        var timeline = await Timeline(id);
        Assert.Equal(
            new[] { "FileProcessed", "VoteCreated", "BallotsAdded", "FileProcessed", "ConflictOpened", "ConflictOpened", "ConflictApproved" },
            timeline.Select(t => t.EventType));
        Assert.Equal(("original.csv", "2 New"), (timeline[0].Source!.FileName, timeline[0].Detail));
        Assert.Equal(("revised.csv", "omer", "1 Unchanged, 1 Conflict"), (timeline[3].Source!.FileName, timeline[3].Actor, timeline[3].Detail));
        var approved = timeline[^1];
        Assert.Equal(("Ballot: Y", "Ballot: A", "dana", "revised.csv", "checked"),
            (approved.OldValue, approved.NewValue, approved.Actor, approved.Source!.FileName, approved.Comment));

        // "why did BRA's ballot change, from which file, who approved?" in one call
        var braStory = await Timeline(id, "bra");
        Assert.Equal(new[] { "FileRow", "FileRow", "ConflictOpened", "ConflictApproved" }, braStory.Select(t => t.EventType));
        Assert.Equal(("Y", "original.csv", "line 2: New - New vote"), (braStory[0].NewValue, braStory[0].Source!.FileName, braStory[0].Detail));
        Assert.Equal(("A", "revised.csv", bra.Id), (braStory[1].NewValue, braStory[1].Source!.FileName, braStory[1].ConflictId));
        Assert.StartsWith("line 2: Conflict - Approved Y, file has A", braStory[1].Detail);
        Assert.Equal(("dana", "checked"), (braStory[3].Actor, braStory[3].Comment));
        Assert.All(braStory, t => Assert.True(t.CountryCode is "BRA" or null));
    }

    [Fact]
    public async Task Vote_list_filters_sorts_and_pages()
    {
        var p = Prefix();
        await Upload(_dana, "list.csv",
            Line($"{p}/1", "BRA", "Y", session: "70", date: "2015-03-01"),
            Line($"{p}/1", "IND", "N", session: "70", date: "2015-03-01"),
            Line($"{p}/2", "BRA", "Y", session: "71", date: "2016-05-01"),
            Line($"{p}/3", "BRA", "Y", session: "71", date: "2016-06-01"));
        await Upload(_dana, "change.csv", Line($"{p}/3", "BRA", "N", session: "71", date: "2016-06-01"));

        var all = await ListVotes($"search={p}");
        Assert.Equal(3, all.Total);
        Assert.Equal(new[] { $"{p}/3", $"{p}/2", $"{p}/1" }, all.Items.Select(v => v.AssemblyVoteId));   // newest first
        Assert.Equal((2, 0), (all.Items[2].BallotCount, all.Items[2].PendingConflicts));
        Assert.Equal(1, all.Items[0].PendingConflicts);

        Assert.Equal(2, (await ListVotes($"search={p}&session=71")).Total);
        Assert.Equal(new[] { $"{p}/2" }, (await ListVotes($"search={p}&from=2016-01-01&to=2016-05-31")).Items.Select(v => v.AssemblyVoteId));

        var page2 = await ListVotes($"search={p}&pageSize=2&page=2");
        Assert.Equal(3, page2.Total);
        Assert.Equal(new[] { $"{p}/1" }, page2.Items.Select(v => v.AssemblyVoteId));
    }

    [Theory]
    [InlineData("from=2020-01-02&to=2020-01-01", "from")]
    [InlineData("pageSize=501", "pageSize")]
    [InlineData("page=0", "page")]
    public async Task Invalid_list_query_is_400_on_the_field(string query, string field)
    {
        var response = await _dana.GetAsync($"/api/votes?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains(field, problem.Errors.Keys);
    }

    [Fact]
    public async Task Unknown_vote_is_404_and_no_user_is_401()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _dana.GetAsync("/api/votes/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _dana.GetAsync("/api/votes/999999/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/votes")).StatusCode);
    }
}
