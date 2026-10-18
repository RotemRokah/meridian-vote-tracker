using System.Net.Http.Headers;
using System.Text.Json;

namespace Meridian.Api.Pdf;

// Reads a PDF with Azure AI Document Intelligence (layout model). Tests use a fake: no network.
public interface IDocumentAnalyzer
{
    bool IsConfigured { get; }

    // Returns the analyzeResult JSON. Throws DocumentAnalysisException with a message fit for the user.
    Task<string> AnalyzeLayoutAsync(Stream pdf, string correlationId, CancellationToken ct);
}

public class DocumentAnalysisException : Exception
{
    public DocumentAnalysisException(string message, Exception? inner = null) : base(message, inner) { }
}

// REST call, no SDK: one POST, then poll the operation until it succeeds, fails or times out (PDF-06).
public class AzureDocumentAnalyzer : IDocumentAnalyzer
{
    private const string ApiVersion = "2024-11-30";

    private readonly HttpClient _http;
    private readonly string? _endpoint;
    private readonly string? _key;
    private readonly TimeSpan _timeout;   // a job never hangs forever (PDF-06)
    private readonly ILogger<AzureDocumentAnalyzer> _logger;

    public AzureDocumentAnalyzer(HttpClient http, IConfiguration config, ILogger<AzureDocumentAnalyzer> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(60);   // per request
        _endpoint = config["DocumentIntelligence:Endpoint"]?.TrimEnd('/');
        _key = config["DocumentIntelligence:Key"];
        _timeout = TimeSpan.FromSeconds(config.GetValue("DocumentIntelligence:TimeoutSeconds", 120));
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_endpoint) && !string.IsNullOrWhiteSpace(_key);

    public async Task<string> AnalyzeLayoutAsync(Stream pdf, string correlationId, CancellationToken ct)
    {
        if (!IsConfigured) throw new DocumentAnalysisException("Document Intelligence is not configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var post = new HttpRequestMessage(HttpMethod.Post,
                $"{_endpoint}/documentintelligence/documentModels/prebuilt-layout:analyze?api-version={ApiVersion}")
            {
                Content = new StreamContent(pdf) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } },
            };
            AddHeaders(post, correlationId);
            using var accepted = await _http.SendAsync(post, timeout.Token);
            if (!accepted.IsSuccessStatusCode)
                throw new DocumentAnalysisException($"Document Intelligence refused the file: {(int)accepted.StatusCode} {await Short(accepted)}");
            var operation = accepted.Headers.GetValues("Operation-Location").First();
            _logger.LogInformation("Document Intelligence analysis started, correlation {CorrelationId}", correlationId);

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                var poll = new HttpRequestMessage(HttpMethod.Get, operation);
                AddHeaders(poll, correlationId);
                using var response = await _http.SendAsync(poll, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new DocumentAnalysisException($"Document Intelligence error: {(int)response.StatusCode} {Trim(body)}");
                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.GetProperty("status").GetString();
                if (status == "succeeded") return doc.RootElement.GetProperty("analyzeResult").GetRawText();
                if (status == "failed")
                    throw new DocumentAnalysisException($"Document Intelligence could not read the file: {Trim(body)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DocumentAnalysisException($"Document Intelligence did not answer within {_timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new DocumentAnalysisException($"Document Intelligence could not be reached: {ex.Message}", ex);
        }
    }

    // the correlation id goes with the AI call too (Document Intelligence logs x-ms-client-request-id)
    private void AddHeaders(HttpRequestMessage request, string correlationId)
    {
        request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        request.Headers.Add("x-ms-client-request-id", correlationId);
    }

    private static async Task<string> Short(HttpResponseMessage r) => Trim(await r.Content.ReadAsStringAsync());
    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
