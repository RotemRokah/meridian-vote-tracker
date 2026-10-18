using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Meridian.Api.Imports;
using Meridian.Api.Pdf;
using Meridian.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Tests.Integration;

// Document Intelligence replaced by a fake: each call takes the next scripted answer (layout JSON or an error)
public class FakeAnalyzer : IDocumentAnalyzer
{
    public ConcurrentQueue<Func<string>> Answers { get; } = new();
    public ConcurrentQueue<string> CorrelationIds { get; } = new();
    public bool IsConfigured => true;

    public Task<string> AnalyzeLayoutAsync(Stream pdf, string correlationId, CancellationToken ct)
    {
        CorrelationIds.Enqueue(correlationId);
        return Task.FromResult(Answers.TryDequeue(out var next) ? next() : throw new DocumentAnalysisException("no scripted answer"));
    }
}

public class PdfApiFactory : ApiFactory
{
    public FakeAnalyzer Analyzer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IDocumentAnalyzer>(Analyzer));
    }
}

[CollectionDefinition(Name)]
public class PdfCollection : ICollectionFixture<PdfApiFactory>
{
    public const string Name = "pdf";
}

// PDF-01, PDF-02, PDF-06: upload, background read, failure shown, retry
[Collection(PdfCollection.Name)]
public class PdfExtractionTests
{
    private readonly PdfApiFactory _factory;
    private readonly HttpClient _dana;

    public PdfExtractionTests(PdfApiFactory factory)
    {
        _factory = factory;
        _dana = factory.ClientFor("dana");
    }

    public static string Layout(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "pdf", name));
    private static readonly byte[] FakePdf = "%PDF-1.4 fake sheet"u8.ToArray();

    public static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] content, string name = "MA_2011_042.pdf", string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/pdf-extractions")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } },
        };
        if (correlationId != null) request.Headers.Add("X-Correlation-ID", correlationId);
        return await client.SendAsync(request);
    }

    public static async Task<PdfExtractionDetailsDto> WaitWhileBusy(HttpClient client, long id)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var details = (await client.GetFromJsonAsync<PdfExtractionDetailsDto>($"/api/pdf-extractions/{id}", ApiFactory.Json))!;
            if (details.Extraction.Status is not (PdfExtractionStatus.Queued or PdfExtractionStatus.Running) || DateTime.UtcNow > deadline)
                return details;
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Uploaded_pdf_is_read_in_the_background_with_the_request_correlation_id()
    {
        _factory.Analyzer.Answers.Enqueue(() => Layout("MA_2011_042.layout.json"));
        var response = await Upload(_dana, FakePdf, correlationId: "pdf-corr-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<PdfExtractionDto>(ApiFactory.Json))!;

        var details = await WaitWhileBusy(_dana, queued.Id);

        Assert.Equal(PdfExtractionStatus.ReadyForReview, details.Extraction.Status);
        Assert.Equal(148, details.Sheet!.Rows.Count);
        Assert.Equal("MA/2011/042", details.Sheet.AssemblyVoteId);
        Assert.Empty(details.BlockingProblems);
        Assert.Contains("pdf-corr-1", _factory.Analyzer.CorrelationIds);   // passed on to the AI call
    }

    [Fact]
    public async Task AI_failure_is_shown_and_retry_reads_it_again()
    {
        _factory.Analyzer.Answers.Enqueue(() => throw new DocumentAnalysisException("Document Intelligence did not answer within 2 minutes."));
        var queued = (await (await Upload(_dana, FakePdf)).Content.ReadFromJsonAsync<PdfExtractionDto>(ApiFactory.Json))!;

        var failed = await WaitWhileBusy(_dana, queued.Id);
        Assert.Equal(PdfExtractionStatus.Failed, failed.Extraction.Status);
        Assert.Equal("Document Intelligence did not answer within 2 minutes.", failed.Extraction.Error);

        _factory.Analyzer.Answers.Enqueue(() => Layout("MA_2011_042.layout.json"));
        Assert.Equal(HttpStatusCode.Accepted, (await _dana.PostAsync($"/api/pdf-extractions/{queued.Id}/retry", null)).StatusCode);
        var retried = await WaitWhileBusy(_dana, queued.Id);
        Assert.Equal((PdfExtractionStatus.ReadyForReview, 2, (string?)null), (retried.Extraction.Status, retried.Extraction.Attempts, retried.Extraction.Error));

        // only a failed one can be retried
        Assert.Equal(HttpStatusCode.Conflict, (await _dana.PostAsync($"/api/pdf-extractions/{queued.Id}/retry", null)).StatusCode);
    }

    private async Task<long> ReadyForReview(string layout)
    {
        _factory.Analyzer.Answers.Enqueue(() => Layout(layout));
        var queued = (await (await Upload(_dana, FakePdf)).Content.ReadFromJsonAsync<PdfExtractionDto>(ApiFactory.Json))!;
        Assert.Equal(PdfExtractionStatus.ReadyForReview, (await WaitWhileBusy(_dana, queued.Id)).Extraction.Status);
        return queued.Id;
    }

    private async Task<ImportDetailsDto> ApproveAndImport(long id)
    {
        var response = await _dana.PostAsync($"/api/pdf-extractions/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = (await response.Content.ReadFromJsonAsync<PdfExtractionDto>(ApiFactory.Json))!;
        Assert.Equal((PdfExtractionStatus.Approved, "dana"), (approved.Status, approved.DecidedBy));
        return await ApiFactory.WaitForImport(_dana, approved.ImportId!.Value);
    }

    // PDF-05: approval goes through the CSV ingestion rules, with the PDF as the source file
    [Fact]
    public async Task Approved_sheet_is_ingested_like_a_csv_and_a_second_copy_changes_nothing()
    {
        var first = await ApproveAndImport(await ReadyForReview("MA_2011_042.layout.json"));
        Assert.Equal((ImportStatus.Completed, "MA_2011_042.pdf"), (first.Import.Status, first.Import.FileName));
        Assert.Equal(148, first.Summary!.Counts.Values.Sum());
        Assert.True(first.Summary.Counts[RowClassification.New] > 100);
        Assert.Equal(0, first.Summary.Counts[RowClassification.Conflict]);

        var again = await ApproveAndImport(await ReadyForReview("MA_2011_042.layout.json"));
        Assert.Equal(0, again.Summary!.Counts[RowClassification.New]);
        Assert.Equal(first.Summary.Counts[RowClassification.New], again.Summary.Counts[RowClassification.Unchanged]);
    }

    [Fact]
    public async Task Sheet_with_an_unreadable_ballot_cannot_be_approved_and_can_be_rejected()
    {
        var id = await ReadyForReview("MA_2009_117.layout.json");   // HUN and IND have two ticks

        var refused = await _dana.PostAsync($"/api/pdf-extractions/{id}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains("ballot of HUN", problem.Errors["sheet"][0]);

        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor("omer").PostAsync($"/api/pdf-extractions/{id}/reject", null)).StatusCode);
        var second = await _dana.PostAsync($"/api/pdf-extractions/{id}/reject", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("rejected by omer", (await second.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_is_400_on_the_file_field()
    {
        var response = await Upload(_dana, "assembly_vote_id,session"u8.ToArray(), "votes.csv");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains("file", problem.Errors.Keys);
    }
}

// Without Document Intelligence settings the feature is off, and the rest of the system runs as usual
[Collection(IntegrationCollection.Name)]
public class PdfFeatureOffTests
{
    private readonly HttpClient _dana;
    public PdfFeatureOffTests(ApiFactory factory) => _dana = factory.ClientFor("dana");

    [Fact]
    public async Task Without_configuration_the_feature_is_off_and_upload_is_503()
    {
        Assert.False((await _dana.GetFromJsonAsync<PdfFeatureDto>("/api/pdf-extractions/feature", ApiFactory.Json))!.Enabled);
        var response = await PdfExtractionTests.Upload(_dana, "%PDF-1.4"u8.ToArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
