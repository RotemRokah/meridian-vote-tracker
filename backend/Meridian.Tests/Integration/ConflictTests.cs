using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meridian.Api.Conflicts;
using Meridian.Api.Imports;
using Meridian.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// CON-01..06, and the decision part of spec 7.2
[Collection(IntegrationCollection.Name)]
public class ConflictTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _dana;
    private readonly HttpClient _omer;

    public ConflictTests(ApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
        _omer = factory.ClientFor("omer");
    }

    // the shared test DB has other votes: give each test its own vote ids
    private static string Prefix() => $"C{Guid.NewGuid():N}"[..9];

    private Task<ImportDetailsDto> Upload(HttpClient client, string fileName, params string[] lines) =>
        _factory.ImportAndWait(client, fileName, Encoding.UTF8.GetBytes(string.Join("\n", lines.Prepend(Header)) + "\n"));

    private async Task<List<ConflictDto>> Conflicts(string vote, ConflictStatus status = ConflictStatus.Pending) =>
        (await _dana.GetFromJsonAsync<List<ConflictDto>>(
            $"/api/conflicts?vote={Uri.EscapeDataString(vote)}&status={status}", ApiFactory.Json))!;

    private static Task<HttpResponseMessage> Decide(HttpClient client, long id, string? decision, string? comment = null) =>
        client.PostAsJsonAsync($"/api/conflicts/{id}/decision", new DecisionRequest(decision, comment));

    private static async Task<ConflictDto> Ok(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConflictDto>(ApiFactory.Json))!;
    }

    private static async Task<JsonElement> Problem409(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Worked_example_decision()
    {
        // spec 7.2: original by Dana, revised by Omer
        var vote = $"{Prefix()}/101";
        var original = await Upload(_dana, "original.csv",
            Line(vote, "BRA", "Y", title: "Title 101"), Line(vote, "IND", "Y", title: "Title 101"));
        var revised = await Upload(_omer, "revised.csv",
            Line(vote, "BRA", "A", title: "Title 101 revised"), Line(vote, "IND", "Y", title: "Title 101 revised"));

        var pending = await Conflicts(vote);
        Assert.Equal(new[] { ConflictField.Ballot, ConflictField.Title }, pending.Select(c => c.Field).Order());
        var bra = pending.Single(c => c.Field == ConflictField.Ballot);

        // CON-02: both values side by side, each with its file, uploader, time
        Assert.Equal(("BRA", "Y", "A", "Y"), (bra.CountryCode, bra.ApprovedValue, bra.ProposedValue, bra.CurrentApprovedValue));
        Assert.Equal(new ValueSourceDto(original.Import.Id, "original.csv", "dana", original.Import.UploadedAt), bra.ApprovedSource);
        Assert.Equal(new ValueSourceDto(revised.Import.Id, "revised.csv", "omer", revised.Import.UploadedAt), bra.ProposedSource);

        // Dana approves BRA = A
        var decided = await Ok(await Decide(_dana, bra.Id, "Approve", "Confirmed with the secretariat"));
        Assert.Equal((ConflictStatus.Approved, "dana", "A"), (decided.Status, decided.DecidedBy, decided.CurrentApprovedValue));
        Assert.Equal("Confirmed with the secretariat", decided.Comment);

        await using (var db = _factory.NewDbContext())
        {
            var ballot = await db.CountryVotes.SingleAsync(cv => cv.CountryCode == "BRA" && db.Votes.Any(v => v.Id == cv.VoteId && v.AssemblyVoteId == vote));
            Assert.Equal(("A", revised.Import.Id, "dana"), (ballot.Ballot, ballot.SourceImportId, ballot.ApprovedBy));

            // the old Y stays in the history, with the source file and the approver (HIS-02, HIS-03)
            var audit = await db.AuditEvents.SingleAsync(a => a.ConflictId == bra.Id && a.EventType == "ConflictApproved");
            Assert.Equal(("Ballot: Y", "Ballot: A", revised.Import.Id, "dana"), (audit.OldValue, audit.NewValue, audit.ImportId, audit.Actor));
        }

        // Omer decides the same conflict afterwards: clear 409 with the current state, nothing changes (CON-05)
        var problem = await Problem409(await Decide(_omer, bra.Id, "Reject"));
        Assert.Equal("Conflict already decided", problem.GetProperty("title").GetString());
        Assert.Contains("dana approved this conflict", problem.GetProperty("detail").GetString());
        Assert.Equal("Approved", problem.GetProperty("current").GetProperty("status").GetString());
        Assert.Equal("A", problem.GetProperty("current").GetProperty("currentApprovedValue").GetString());

        // CON-06: decided conflicts stay viewable with the decision; pending list no longer has it
        Assert.DoesNotContain(await Conflicts(vote), c => c.Id == bra.Id);
        var approved = Assert.Single(await Conflicts(vote, ConflictStatus.Approved));
        Assert.Equal((bra.Id, "dana", "Confirmed with the secretariat"), (approved.Id, approved.DecidedBy, approved.Comment));

        // re-upload of the revised file: 0 new, 0 new conflicts; BRA=A now matches approved
        var again = await Upload(_omer, "revised.csv",
            Line(vote, "BRA", "A", title: "Title 101 revised"), Line(vote, "IND", "Y", title: "Title 101 revised"));
        Assert.Equal(0, again.Summary!.Counts[RowClassification.New]);
        Assert.Equal(0, again.Summary.NewConflicts);
        Assert.Equal(2, again.Summary.Counts[RowClassification.Unchanged]);
        Assert.Equal(1, again.Summary.LinkedConflicts);   // the title conflict is still pending

        await using (var db = _factory.NewDbContext())
            Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.ConflictId == bra.Id && a.EventType.StartsWith("Conflict") && a.EventType != "ConflictOpened"));
    }

    [Fact]
    public async Task Two_analysts_deciding_at_the_same_moment_exactly_one_lands()
    {
        // 10 conflicts, and for each one Dana (approve) and Omer (reject) fire at the same time
        var p = Prefix();
        var votes = Enumerable.Range(1, 10).Select(i => $"{p}/{i}").ToList();
        await Upload(_dana, "before.csv", votes.Select(v => Line(v, "BRA", "Y")).ToArray());
        await Upload(_dana, "after.csv", votes.Select(v => Line(v, "BRA", "A")).ToArray());
        var conflictIds = new List<long>();
        foreach (var v in votes) conflictIds.Add((await Conflicts(v)).Single().Id);

        var race = conflictIds.Select(async id =>
        {
            var both = await Task.WhenAll(Decide(_dana, id, "Approve"), Decide(_omer, id, "Reject"));
            return (Id: id, Dana: both[0].StatusCode, Omer: both[1].StatusCode);
        });
        var results = await Task.WhenAll(race);

        await using var db = _factory.NewDbContext();
        foreach (var r in results)
        {
            // exactly one 200 and one 409
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }.Order(), new[] { r.Dana, r.Omer }.Order());

            // the DB agrees with the winner, and holds one decision event
            var conflict = await db.Conflicts.SingleAsync(c => c.Id == r.Id);
            var ballot = await db.CountryVotes.SingleAsync(cv => cv.VoteId == conflict.VoteId && cv.CountryCode == "BRA");
            if (r.Dana == HttpStatusCode.OK)
                Assert.Equal((ConflictStatus.Approved, "dana", "A", "dana"), (conflict.Status, conflict.DecidedBy, ballot.Ballot, ballot.ApprovedBy));
            else
                Assert.Equal((ConflictStatus.Rejected, "omer", "Y", (string?)null), (conflict.Status, conflict.DecidedBy, ballot.Ballot, ballot.ApprovedBy));
            Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.ConflictId == r.Id && a.EventType != "ConflictOpened"));
        }
    }

    [Fact]
    public async Task Reject_keeps_approved_and_the_same_proposal_later_links_to_the_rejection()
    {
        var vote = $"{Prefix()}/1";
        await Upload(_dana, "before.csv", Line(vote, "BRA", "Y"));
        await Upload(_dana, "after.csv", Line(vote, "BRA", "A"));
        var conflict = (await Conflicts(vote)).Single();

        var rejected = await Ok(await Decide(_omer, conflict.Id, "reject", "Typo in the source file"));
        Assert.Equal((ConflictStatus.Rejected, "Y"), (rejected.Status, rejected.CurrentApprovedValue));

        var again = await Upload(_dana, "after-again.csv", Line(vote, "BRA", "A"));
        Assert.Equal((0, 1), (again.Summary!.NewConflicts, again.Summary.LinkedConflicts));
        var rows = (await _dana.GetFromJsonAsync<PagedResult<ImportRowDto>>(
            $"/api/imports/{again.Import.Id}/rows?classification=Conflict", ApiFactory.Json))!;
        var row = Assert.Single(rows.Items);
        Assert.Equal(conflict.Id, row.ConflictId);
        Assert.Contains("already rejected", row.Reason);
        Assert.Contains("by omer", row.Reason);
    }

    [Fact]
    public async Task Approving_against_a_changed_approved_value_is_409_and_changes_nothing()
    {
        // two different proposals for the same ballot: approving one makes the other stale
        var vote = $"{Prefix()}/1";
        await Upload(_dana, "y.csv", Line(vote, "BRA", "Y"));
        await Upload(_dana, "a.csv", Line(vote, "BRA", "A"));
        await Upload(_dana, "n.csv", Line(vote, "BRA", "N"));
        var pending = await Conflicts(vote);
        var toA = pending.Single(c => c.ProposedValue == "A");
        var toN = pending.Single(c => c.ProposedValue == "N");

        await Ok(await Decide(_dana, toA.Id, "Approve"));

        var problem = await Problem409(await Decide(_omer, toN.Id, "Approve"));
        Assert.Equal("Approved value has changed", problem.GetProperty("title").GetString());
        Assert.Contains("now 'A', not 'Y'", problem.GetProperty("detail").GetString());

        // rolled back: still pending, approved still A; rejecting it is allowed
        var stillPending = (await Conflicts(vote)).Single();
        Assert.Equal(toN.Id, stillPending.Id);
        Assert.Equal("A", stillPending.CurrentApprovedValue);
        await Ok(await Decide(_omer, toN.Id, "Reject"));
    }

    [Fact]
    public async Task Approving_vote_detail_conflicts_changes_the_vote()
    {
        var vote = $"{Prefix()}/1";
        await Upload(_dana, "v1.csv", Line(vote, "BRA", "Y", title: "Old title", date: "2024-02-05"));
        var revised = await Upload(_dana, "v2.csv", Line(vote, "BRA", "Y", title: "New title", date: "2024-02-06"));
        var pending = await Conflicts(vote);

        foreach (var c in pending)
            await Ok(await Decide(_dana, c.Id, "Approve"));

        await using var db = _factory.NewDbContext();
        var v = await db.Votes.SingleAsync(x => x.AssemblyVoteId == vote);
        Assert.Equal(("New title", new DateOnly(2024, 2, 6), revised.Import.Id), (v.Title, v.VoteDate, v.SourceImportId));
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.VoteId == v.Id && a.EventType == "ConflictApproved"));
    }

    [Theory]
    [InlineData(null, "decision")]
    [InlineData("Maybe", "decision")]
    public async Task Invalid_decision_is_400_on_the_field(string? decision, string field)
    {
        var vote = $"{Prefix()}/1";
        await Upload(_dana, "y.csv", Line(vote, "BRA", "Y"));
        await Upload(_dana, "a.csv", Line(vote, "BRA", "A"));
        var conflict = (await Conflicts(vote)).Single();

        var response = await Decide(_dana, conflict.Id, decision);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains(field, problem.Errors.Keys);
        Assert.Equal(ConflictStatus.Pending, (await Conflicts(vote)).Single().Status);   // nothing decided
    }

    [Fact]
    public async Task Too_long_comment_is_400_on_the_field()
    {
        var response = await Decide(_dana, 1, "Approve", new string('x', ConflictEndpoints.MaxCommentLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains("comment", problem.Errors.Keys);
    }

    [Fact]
    public async Task Unknown_conflict_is_404_and_no_user_is_401()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(_dana, 999999, "Approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _dana.GetAsync("/api/conflicts/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/conflicts")).StatusCode);
    }
}
