using System.Net;
using System.Net.Http.Json;
using System.Text;
using Meridian.Api.Imports;
using Meridian.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Meridian.Tests.Domain.TestData;

namespace Meridian.Tests.Integration;

// HTTP behaviour of the import endpoints, and the 7.2 original file through the full pipeline
[Collection(IntegrationCollection.Name)]
public class ImportEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _dana;

    public ImportEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
    }

    // the shared test DB has other votes: give each test its own vote ids
    private static string Prefix() => $"T{Guid.NewGuid():N}"[..9];

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

    private static async Task<ValidationProblemDetails> ReadValidationProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }

    [Fact]
    public async Task Upload_without_a_user_is_401()
    {
        var response = await ApiFactory.Upload(_factory.CreateClient(), "a.csv", Csv(Header));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_without_a_file_is_400_on_the_file_field()
    {
        var form = new MultipartFormDataContent { { new StringContent("hello"), "note" } };
        var response = await _dana.PostAsync("/api/imports", form);

        var problem = await ReadValidationProblem(response);
        Assert.Contains("file", problem.Errors.Keys);
    }

    [Fact]
    public async Task Malformed_form_body_is_400_not_500()
    {
        // an empty multipart body can't be parsed as a form: the client's fault
        var response = await _dana.PostAsync("/api/imports", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Wrong_header_is_rejected_at_upload_and_no_import_is_created()
    {
        await using var db = _factory.NewDbContext();
        var before = await db.Imports.CountAsync();

        var response = await ApiFactory.Upload(_dana, "wrong.csv", Csv("vote,country,ballot", "MA/1,BRA,Y"));

        var problem = await ReadValidationProblem(response);
        Assert.Contains("Header must be", problem.Errors["file"].Single());
        Assert.Equal(before, await db.Imports.CountAsync());
    }

    [Fact]
    public async Task Upload_returns_202_immediately_with_a_location()
    {
        var response = await ApiFactory.Upload(_dana, "one.csv", Csv(Header, Line($"{Prefix()}/1", "BRA", "Y")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<ImportDto>(ApiFactory.Json))!;
        Assert.Equal($"/api/imports/{queued.Id}", response.Headers.Location!.ToString());
        Assert.Equal("dana", queued.UploadedBy);
        Assert.Equal("one.csv", queued.FileName);
    }

    [Fact]
    public async Task Correlation_id_of_the_upload_is_stored_on_the_import()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/imports")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(Csv(Header, Line($"{Prefix()}/1", "BRA", "Y"))), "file", "c.csv" } },
        };
        request.Headers.Add("X-Correlation-ID", "upload-corr-1");
        var response = await _dana.SendAsync(request);
        var queued = (await response.Content.ReadFromJsonAsync<ImportDto>(ApiFactory.Json))!;

        await using var db = _factory.NewDbContext();
        Assert.Equal("upload-corr-1", (await db.Imports.SingleAsync(i => i.Id == queued.Id)).CorrelationId);

        // the worker's log lines carry the same id: traceable from the request to the background job
        await ApiFactory.WaitForImport(_dana, queued.Id);
        Assert.Contains(_factory.Logs.Lines, l => l.Contains("CorrelationId:upload-corr-1") && l.Contains($"Import {queued.Id} completed"));
    }

    [Fact]
    public async Task Worked_example_original_file_through_the_pipeline()
    {
        // spec 7.2, original file
        var p = Prefix();
        var details = await _factory.ImportAndWait(_dana, "original.csv", Csv(
            Header,
            Line($"{p}/101", "BRA", "Y", title: "Title 101"),   // 2 new
            Line($"{p}/101", "IND", "Y", title: "Title 101"),   // 3 new
            Line($"{p}/102", "JPN", "Y", title: "Title 102"),   // 4 new
            Line($"{p}/102", "JPN", "Y", title: "Title 102"),   // 5 duplicate
            Line($"{p}/103", "FRA", "Y", title: "Title 103"),   // 6 contradiction
            Line($"{p}/103", "FRA", "N", title: "Title 103"),   // 7 contradiction
            Line($"{p}/104", "FRN", "Y", title: "Title 104"),   // 8 invalid: unknown code
            Line($"{p}/104", "NZL", "Y", title: "Title 104"))); // 9 invalid: not a member

        Assert.Equal(ImportStatus.Completed, details.Import.Status);
        Assert.Equal(8, details.Import.TotalRows);
        Assert.Equal(8, details.Import.ProcessedRows);
        var counts = details.Summary!.Counts;
        Assert.Equal(3, counts[RowClassification.New]);
        Assert.Equal(1, counts[RowClassification.Duplicate]);
        Assert.Equal(2, counts[RowClassification.Contradiction]);
        Assert.Equal(2, counts[RowClassification.Invalid]);

        // ING-14: rejected rows with line number, reason and original value
        var invalid = (await _dana.GetFromJsonAsync<PagedResult<ImportRowDto>>(
            $"/api/imports/{details.Import.Id}/rows?classification=Invalid", ApiFactory.Json))!;
        Assert.Equal(2, invalid.Total);
        Assert.Equal(new[] { 8, 9 }, invalid.Items.Select(r => r.LineNo));
        Assert.Contains("value: 'FRN'", invalid.Items[0].Reason);
        Assert.Contains("FRN", invalid.Items[0].RawLine);
        Assert.Contains("NZL was not a member", invalid.Items[1].Reason);

        // per-vote outcome in the report
        var votes = details.Summary.Votes.ToDictionary(v => v.AssemblyVoteId, v => v.Outcome);
        Assert.Equal(VoteDetailsOutcome.Created, votes[$"{p}/101"]);
        Assert.Equal(VoteDetailsOutcome.NotCreated, votes[$"{p}/103"]);

        // audit: the new votes and their ballots are on record, by the uploader
        await using var db = _factory.NewDbContext();
        var events = await db.AuditEvents.Where(a => a.ImportId == details.Import.Id).Select(a => a.EventType).ToListAsync();
        Assert.Equal(2, events.Count(e => e == "VoteCreated"));
        Assert.Equal(2, events.Count(e => e == "BallotsAdded"));
    }

    [Fact]
    public async Task Import_list_shows_the_upload()
    {
        var details = await _factory.ImportAndWait(_dana, "listed.csv", Csv(Header, Line($"{Prefix()}/1", "BRA", "Y")));

        var list = (await _dana.GetFromJsonAsync<List<ImportDto>>("/api/imports", ApiFactory.Json))!;
        Assert.Contains(list, i => i.Id == details.Import.Id && i.Status == ImportStatus.Completed);
    }

    [Fact]
    public async Task Rows_page_size_is_validated()
    {
        var details = await _factory.ImportAndWait(_dana, "p.csv", Csv(Header, Line($"{Prefix()}/1", "BRA", "Y")));

        var response = await _dana.GetAsync($"/api/imports/{details.Import.Id}/rows?pageSize=5000");

        var problem = await ReadValidationProblem(response);
        Assert.Contains("pageSize", problem.Errors.Keys);
    }

    [Fact]
    public async Task Unknown_import_is_404()
    {
        var response = await _dana.GetAsync("/api/imports/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
