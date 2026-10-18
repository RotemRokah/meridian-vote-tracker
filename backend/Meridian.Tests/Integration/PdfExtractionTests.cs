using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
