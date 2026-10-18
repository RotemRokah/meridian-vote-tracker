using System.Net;
using Meridian.Api.Pdf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Tests.Integration;

// PDF-06 and the correlation id, on the real client; the HTTP side is a fake handler, no network
public class DocumentAnalyzerTests
{
    // POST is accepted, then every poll says "running": the service never finishes
    private class NeverFinishes : HttpMessageHandler
    {
        public List<string> ClientRequestIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ClientRequestIds.Add(request.Headers.GetValues("x-ms-client-request-id").Single());
            if (request.Method == HttpMethod.Post)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Add("Operation-Location", "https://di.test/operations/1");
                return Task.FromResult(accepted);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"running\"}") });
        }
    }

    private static AzureDocumentAnalyzer Analyzer(HttpMessageHandler handler, int timeoutSeconds) =>
        new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DocumentIntelligence:Endpoint"] = "https://di.test/",
            ["DocumentIntelligence:Key"] = "test-key",
            ["DocumentIntelligence:TimeoutSeconds"] = timeoutSeconds.ToString(),
        }).Build(), NullLogger<AzureDocumentAnalyzer>.Instance);

    [Fact]
    public async Task A_service_that_never_finishes_fails_with_a_clear_message_instead_of_hanging()
    {
        var handler = new NeverFinishes();
        var started = DateTime.UtcNow;

        var error = await Assert.ThrowsAsync<DocumentAnalysisException>(() =>
            Analyzer(handler, 3).AnalyzeLayoutAsync(new MemoryStream("%PDF-1.4"u8.ToArray()), "corr-di-1", CancellationToken.None));

        Assert.Equal("Document Intelligence did not answer within 3 seconds.", error.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.All(handler.ClientRequestIds, id => Assert.Equal("corr-di-1", id));   // correlation id on every AI call
        Assert.True(handler.ClientRequestIds.Count >= 2);                               // the POST and at least one poll
    }

    [Fact]
    public async Task Without_settings_it_is_not_configured_and_refuses_to_run()
    {
        var analyzer = new AzureDocumentAnalyzer(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<AzureDocumentAnalyzer>.Instance);
        Assert.False(analyzer.IsConfigured);
        await Assert.ThrowsAsync<DocumentAnalysisException>(() =>
            analyzer.AnalyzeLayoutAsync(new MemoryStream(), "x", CancellationToken.None));
    }
}
