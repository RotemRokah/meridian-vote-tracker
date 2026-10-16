using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meridian.Api.Imports;
using Meridian.Api.Votes;
using Meridian.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// POS-01..03, HIS-03
[Collection(IntegrationCollection.Name)]
public class PositionTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _lena;   // Admin
    private readonly HttpClient _dana;   // Analyst

    public PositionTests(ApiFactory factory)
    {
        _factory = factory;
        _lena = factory.ClientFor("lena");
        _dana = factory.ClientFor("dana");
    }

    // a fresh vote per test
    private async Task<long> NewVote()
    {
        var vote = $"P{Guid.NewGuid():N}"[..9] + "/1";
        await _factory.ImportAndWait(_dana, "vote.csv", Encoding.UTF8.GetBytes($"{Header}\n{Line(vote, "BRA", "Y")}\n"));
        var list = (await _dana.GetFromJsonAsync<PagedResult<VoteListItemDto>>(
            $"/api/votes?search={Uri.EscapeDataString(vote)}", ApiFactory.Json))!;
        return list.Items.Single().Id;
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, long voteId, string? stance, int? weight, uint? version) =>
        client.PutAsJsonAsync($"/api/votes/{voteId}/position", new PositionRequest(stance, weight, version));

    private static async Task<PositionDto> Ok(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PositionDto>(ApiFactory.Json))!;
    }

    private async Task<PositionDto?> CurrentPosition(long voteId) =>
        (await _dana.GetFromJsonAsync<VoteDetailsDto>($"/api/votes/{voteId}", ApiFactory.Json))!.Position;

    private async Task<int> PositionEvents(long voteId)
    {
        await using var db = _factory.NewDbContext();
        return await db.AuditEvents.CountAsync(a => a.VoteId == voteId && a.EventType == "PositionSet");
    }

    [Fact]
    public async Task Admin_sets_and_changes_the_position_and_every_change_is_on_record()
    {
        var voteId = await NewVote();

        var first = await Ok(await Put(_lena, voteId, "For", 3, null));
        Assert.Equal((Stance.For, 3, "lena"), (first.Stance, first.Weight, first.UpdatedBy));

        var second = await Ok(await Put(_lena, voteId, "against", 5, first.Version));
        Assert.Equal((Stance.Against, 5), (second.Stance, second.Weight));
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(second, await CurrentPosition(voteId));

        // "who changed the weight, and what was it before?" (HIS-03) is in the vote timeline
        var timeline = (await _dana.GetFromJsonAsync<List<TimelineEntryDto>>($"/api/votes/{voteId}/timeline", ApiFactory.Json))!;
        var changes = timeline.Where(t => t.EventType == "PositionSet").ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal(((string?)null, "For, weight 3", "lena"), (changes[0].OldValue, changes[0].NewValue, changes[0].Actor));
        Assert.Equal(("For, weight 3", "Against, weight 5", "lena"), (changes[1].OldValue, changes[1].NewValue, changes[1].Actor));

        // the vote list shows it too
        var item = (await _dana.GetFromJsonAsync<PagedResult<VoteListItemDto>>("/api/votes?pageSize=500", ApiFactory.Json))!
            .Items.SingleOrDefault(v => v.Id == voteId);
        if (item != null) Assert.Equal((Stance.Against, 5), (item.Stance!.Value, item.Weight!.Value));

        // sending the same values again changes nothing and logs nothing
        await Ok(await Put(_lena, voteId, "Against", 5, second.Version));
        Assert.Equal(2, await PositionEvents(voteId));
    }

    [Theory]
    [InlineData("dana")]
    [InlineData("omer")]
    public async Task Analyst_gets_403_and_nothing_changes(string analyst)
    {
        // the 403 test moved here from step 2: enforced in the API, not only by hiding a button
        var voteId = await NewVote();

        var response = await Put(_factory.ClientFor(analyst), voteId, "For", 3, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(403, problem.Status);
        Assert.Null(await CurrentPosition(voteId));
        Assert.Equal(0, await PositionEvents(voteId));
    }

    [Theory]
    [InlineData("Maybe", 3, "stance")]
    [InlineData("For", 7, "weight")]   // the spec's example
    [InlineData("For", 0, "weight")]
    [InlineData("For", null, "weight")]
    [InlineData(null, null, "stance,weight")]
    public async Task Invalid_position_is_400_with_errors_next_to_the_fields(string? stance, int? weight, string fields)
    {
        var voteId = await NewVote();

        var response = await Put(_lena, voteId, stance, weight, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal(fields.Split(','), problem.Errors.Keys.Order());
        Assert.Null(await CurrentPosition(voteId));
    }

    [Fact]
    public async Task Stale_version_is_409_with_the_current_position_and_changes_nothing()
    {
        var voteId = await NewVote();
        var v1 = await Ok(await Put(_lena, voteId, "For", 3, null));
        var v2 = await Ok(await Put(_lena, voteId, "Against", 4, v1.Version));

        // a second screen still holds v1
        var response = await Put(_lena, voteId, "For", 2, v1.Version);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Position was changed by someone else", problem.GetProperty("title").GetString());
        Assert.Contains("lena set it to Against, weight 4", problem.GetProperty("detail").GetString());
        Assert.Equal(v2.Version, problem.GetProperty("current").GetProperty("version").GetUInt32());
        Assert.Equal(v2, await CurrentPosition(voteId));

        // no version while a position exists is also stale
        Assert.Equal(HttpStatusCode.Conflict, (await Put(_lena, voteId, "For", 2, null)).StatusCode);
    }

    [Fact]
    public async Task Row_version_blocks_a_save_based_on_an_old_read()
    {
        // The exact window the endpoint relies on xmin for: two requests both read version v1 and
        // both pass the version check; the first saves; the second must not overwrite it.
        // Deterministic: two contexts, no timing luck.
        var voteId = await NewVote();
        await Ok(await Put(_lena, voteId, "For", 3, null));

        await using var first = _factory.NewDbContext();
        await using var second = _factory.NewDbContext();
        var a = await first.VotePositions.SingleAsync(p => p.VoteId == voteId);
        var b = await second.VotePositions.SingleAsync(p => p.VoteId == voteId);
        Assert.Equal(a.Version, b.Version);

        a.Weight = 2;
        await first.SaveChangesAsync();

        b.Weight = 4;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(2, (await CurrentPosition(voteId))!.Weight);
    }

    [Fact]
    public async Task Two_simultaneous_changes_with_the_same_version_exactly_one_lands()
    {
        // End to end over HTTP. Usually the second request is stopped by the version check;
        // if both slip past it, xmin stops the second save (see the test above). Either way: 409.
        var voteId = await NewVote();
        var v1 = await Ok(await Put(_lena, voteId, "For", 3, null));

        var both = await Task.WhenAll(
            Put(_lena, voteId, "Against", 5, v1.Version),
            Put(_factory.ClientFor("lena"), voteId, "For", 1, v1.Version));

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }.Order(), both.Select(r => r.StatusCode).Order());
        var winner = await Ok(both.Single(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(winner, await CurrentPosition(voteId));
        Assert.Equal(2, await PositionEvents(voteId));   // the first set + the winner
    }

    [Fact]
    public async Task Unknown_vote_is_404_and_no_user_is_401()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Put(_lena, 999999, "For", 3, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(_factory.CreateClient(), 1, "For", 3, null)).StatusCode);
    }
}
