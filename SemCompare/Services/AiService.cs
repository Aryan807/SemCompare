using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SemCompare.Models;

namespace SemCompare.Services;

/// <summary>
/// Calls the Google Gemini API to generate:
///   1. A plain-English summary of an entire diff run
///   2. An impact explanation + migration advice for each breaking change
/// </summary>
public class AiService
{
    private readonly HttpClient _http;
    private readonly ILogger<AiService> _logger;
    private static readonly Queue<DateTimeOffset> _requestTimestamps = new();
    private static readonly SemaphoreSlim _rateGate = new(1, 1);
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private static int _maxRequestsPerMinute = 5;
    private static readonly string[] ApiVersions = ["v1beta", "v1"];
    private static readonly string[] CandidateModels =
    [
        "gemini-2.5-flash-lite"
    ];
    private const int MaxTokens = 1024;

    public AiService(HttpClient http, ILogger<AiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public static void ConfigureRateLimit(int maxRequestsPerMinute)
    {
        if (maxRequestsPerMinute <= 0)
            return;

        _maxRequestsPerMinute = maxRequestsPerMinute;
    }

    /// <summary>
    /// Sends all method and field changes to Gemini and returns a plain-English
    /// paragraph summarising what changed in this commit range.
    /// Returns null if the API key is not configured or the call fails.
    /// </summary>
    public async Task<string?> SummarizeRunAsync(
        List<DiffResult>      methodResults,
        List<FieldDiffResult> fieldResults)
    {
        var changes = BuildChangeSummaryText(methodResults, fieldResults);
        if (string.IsNullOrWhiteSpace(changes)) return "No meaningful changes detected.";

        var prompt = $"""
            You are a senior software engineer reviewing a C# code diff.
            Below is a structured list of semantic changes detected between two commits.
            Write a concise 2-3 sentence plain-English summary of what changed and what it likely means.
            Do not use bullet points. Do not repeat the raw data — interpret it.

            Changes:
            {changes}
            """;

        return await CallGeminiAsync(prompt);
    }

    /// <summary>
    /// For a single breaking change, asks Gemini to explain the impact on callers
    /// and suggest a migration path. Returns null if the call fails.
    /// </summary>
    public async Task<string?> ExplainBreakingChangeAsync(BreakingChange bc)
    {
        var paramInfo = "";
        if (bc.ParamDiffs?.Count > 0)
        {
            var lines = bc.ParamDiffs.Select(p => p.Kind switch
            {
                "Added"       => $"  - Parameter '{p.ParamName}' ({p.NewType}) was added",
                "Removed"     => $"  - Parameter '{p.ParamName}' ({p.OldType}) was removed",
                "TypeChanged" => $"  - Parameter '{p.ParamName}' changed from {p.OldType} to {p.NewType}",
                _             => $"  - Parameter '{p.ParamName}' changed"
            });
            paramInfo = "\nParameter-level changes:\n" + string.Join("\n", lines);
        }

        var prompt = $"""
            You are a senior software engineer reviewing a breaking change in a C# codebase.
            Explain in 2-3 sentences:
            1. Why this change breaks existing callers
            2. What developers need to do to fix their code

            Breaking change:
            Member kind: {bc.MemberKind}
            Class: {bc.ClassName}
            Member: {bc.MemberName}
            Change type: {bc.Kind}
            Before: {bc.BeforeSig ?? "did not exist"}
            After:  {bc.AfterSig ?? "removed"}
            {paramInfo}

            Be specific and practical. No bullet points.
            """;

        return await CallGeminiAsync(prompt);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> CallGeminiAsync(string userPrompt)
    {
        try
        {
            string? lastFailure = null;

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = userPrompt }
                        }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = MaxTokens
                }
            };

            var bodyJson = JsonSerializer.Serialize(requestBody);

            foreach (var apiVersion in ApiVersions)
            {
                foreach (var model in CandidateModels)
                {
                    await WaitForRateLimitSlotAsync();

                    var apiUrl = BuildApiUrl(apiVersion, model);
                    using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                    {
                        Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                    };

                    using var response = await _http.SendAsync(request);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                        continue;

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        lastFailure = $"{(int)response.StatusCode} {response.StatusCode}: {errorBody}";

                        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode >= 500)
                        {
                            continue;
                        }

                        _logger.LogWarning("Gemini request failed: {Failure}", lastFailure);
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                        candidates.GetArrayLength() == 0)
                    {
                        lastFailure = "Gemini response had no candidates.";
                        return null;
                    }

                    return candidates[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();
                }
            }

            if (!string.IsNullOrWhiteSpace(lastFailure))
                _logger.LogWarning("Gemini unavailable after trying model/version fallbacks. Last failure: {Failure}", lastFailure);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini request failed with exception.");
            return null;
        }
    }

    private static string BuildApiUrl(string apiVersion, string model) =>
        $"https://generativelanguage.googleapis.com/{apiVersion}/models/{model}:generateContent";

    private static async Task WaitForRateLimitSlotAsync()
    {
        while (true)
        {
            TimeSpan wait;

            await _rateGate.WaitAsync();
            try
            {
                var now = DateTimeOffset.UtcNow;

                while (_requestTimestamps.Count > 0 &&
                       now - _requestTimestamps.Peek() >= RateWindow)
                {
                    _requestTimestamps.Dequeue();
                }

                if (_requestTimestamps.Count < _maxRequestsPerMinute)
                {
                    _requestTimestamps.Enqueue(now);
                    return;
                }

                var oldest = _requestTimestamps.Peek();
                wait = RateWindow - (now - oldest);
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
            }
            finally
            {
                _rateGate.Release();
            }

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }
    }

    private static string BuildChangeSummaryText(
        List<DiffResult>      methods,
        List<FieldDiffResult> fields)
    {
        var sb = new StringBuilder();

        foreach (var r in methods.Where(r => r.Kind != ChangeKind.Unchanged))
            sb.AppendLine($"[Method] {r.Kind}: {r.ClassName}.{r.Before?.Display ?? r.After?.Display}");

        foreach (var f in fields.Where(f => f.Kind != ChangeKind.Unchanged))
            sb.AppendLine($"[Field] {f.Kind}: {f.ClassName}.{f.Before?.Display ?? f.After?.Display}");

        return sb.ToString();
    }
}
