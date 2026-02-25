using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class Estimator
{
    private const string SystemPrompt =
        "You are a calibrated probability estimator for prediction markets.\n" +
        "Given a market question, estimate the TRUE probability that the outcome resolves YES.\n" +
        "\n" +
        "Rules:\n" +
        "- Output ONLY valid JSON: {\"probability\": 0.XX, \"reasoning\": \"one sentence\"}\n" +
        "- probability must be between 0.02 and 0.98\n" +
        "- Be well-calibrated: events you rate at 70% should happen ~70% of the time\n" +
        "- Use base rates, current knowledge, and logical reasoning\n" +
        "- Do NOT anchor on the current market price — estimate independently\n" +
        "- If deeply uncertain, use base rates or lean toward 0.50\n" +
        "- Keep reasoning under 50 words";

    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<Estimator> _log;

    public Estimator(BotConfig config, HttpClient http, ILogger<Estimator> log)
    {
        _config = config;
        _http = http;
        _log = log;
    }

    public async Task<Estimate?> EstimateAsync(MarketInfo market, CancellationToken ct = default)
    {
        var rawEstimates = new List<double>();
        var totalInput = 0;
        var totalOutput = 0;
        var firstReasoning = "";

        for (var i = 0; i < _config.EnsembleSize; i++)
        {
            var result = await SingleCallAsync(market, ct);
            if (result is null) continue;

            rawEstimates.Add(result.Value.Probability);
            if (string.IsNullOrEmpty(firstReasoning))
                firstReasoning = result.Value.Reasoning;
            totalInput += result.Value.InputTokens;
            totalOutput += result.Value.OutputTokens;
        }

        if (rawEstimates.Count < 2)
        {
            _log.LogWarning("Only {Count} valid estimates for: {Question}",
                rawEstimates.Count, Truncate(market.Question, 60));
            if (rawEstimates.Count == 0) return null;
        }

        // Trimmed mean: drop highest and lowest if enough samples
        List<double> trimmed;
        if (rawEstimates.Count >= 4)
        {
            var sorted = rawEstimates.OrderBy(x => x).ToList();
            trimmed = sorted.Skip(1).Take(sorted.Count - 2).ToList();
        }
        else
        {
            trimmed = rawEstimates;
        }

        var fairProb = trimmed.Average();
        var confidence = rawEstimates.Count > 1 ? StdDev(rawEstimates) : 1.0;

        _log.LogInformation("Estimate: {Question} -> {Prob:P2} (n={Count}, std={Std:F3})",
            Truncate(market.Question, 50), fairProb, rawEstimates.Count, confidence);

        return new Estimate
        {
            MarketConditionId = market.ConditionId,
            Question = market.Question,
            FairProbability = fairProb,
            RawEstimates = rawEstimates,
            Confidence = confidence,
            ReasoningSummary = firstReasoning,
            InputTokensUsed = totalInput,
            OutputTokensUsed = totalOutput,
        };
    }

    private async Task<CallResult?> SingleCallAsync(MarketInfo market, CancellationToken ct)
    {
        // Retry delays for transient API overload (429 / 529): 10s, 20s, 40s
        int[] backoffMs = { 10_000, 20_000, 40_000 };

        for (var attempt = 0; attempt <= backoffMs.Length; attempt++)
        {
            try
            {
                var userPrompt = BuildUserPrompt(market);

                var requestBody = new
                {
                    model = _config.ClaudeModel,
                    max_tokens = _config.MaxEstimateTokens,
                    temperature = _config.EnsembleTemperature,
                    system = SystemPrompt,
                    messages = new[]
                    {
                        new { role = "user", content = userPrompt }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.AnthropicApiHost}/v1/messages")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("x-api-key", _config.AnthropicApiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                var resp = await _http.SendAsync(request, ct);
                var status = (int)resp.StatusCode;

                // 429 = rate-limited, 529 = API overloaded — both are transient
                if (status == 429 || status == 529)
                {
                    if (attempt < backoffMs.Length)
                    {
                        var delay = backoffMs[attempt];
                        _log.LogWarning("Anthropic {Status} (attempt {A}/{Max}) — retrying in {Sec}s",
                            status, attempt + 1, backoffMs.Length, delay / 1000);
                        await Task.Delay(delay, ct);
                        continue;
                    }
                    _log.LogError("Anthropic {Status}: giving up after {Max} retries for {Question}",
                        status, backoffMs.Length, Truncate(market.Question, 40));
                    return null;
                }

                resp.EnsureSuccessStatusCode();

                var responseJson = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(responseJson);

                var text = doc.RootElement
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString()?.Trim() ?? "";

                var usage = doc.RootElement.GetProperty("usage");
                var inputTokens = usage.GetProperty("input_tokens").GetInt32();
                var outputTokens = usage.GetProperty("output_tokens").GetInt32();

                // Handle markdown code blocks
                if (text.StartsWith("```"))
                {
                    var lines = text.Split('\n')
                        .Where(l => !l.TrimStart().StartsWith("```"))
                        .ToArray();
                    text = string.Join('\n', lines);
                }

                var parsed = JsonDocument.Parse(text);
                var prob = parsed.RootElement.GetProperty("probability").GetDouble();
                var reasoning = "";
                if (parsed.RootElement.TryGetProperty("reasoning", out var r))
                    reasoning = r.GetString() ?? "";

                prob = Math.Clamp(prob, 0.02, 0.98);

                return new CallResult(prob, reasoning, inputTokens, outputTokens);
            }
            catch (JsonException ex)
            {
                _log.LogDebug("Failed to parse estimate response: {Error}", ex.Message);
                return null;
            }
            catch (HttpRequestException ex)
            {
                _log.LogError("Anthropic API error: {Error}", ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("Estimate call failed: {Error}", ex.Message);
                return null;
            }
        }

        return null;
    }

    private static string BuildUserPrompt(MarketInfo market)
    {
        var desc = market.Description.Length > 500
            ? market.Description[..500]
            : market.Description;
        if (string.IsNullOrEmpty(desc)) desc = "N/A";

        return $"Market: {market.Question}\n" +
            $"Event: {market.EventTitle}\n" +
            $"Description: {desc}\n" +
            $"Category: {market.Category}\n" +
            $"Resolution date: {(string.IsNullOrEmpty(market.EndDate) ? "Unknown" : market.EndDate)}\n" +
            "\n" +
            "Estimate the probability this resolves YES. Output JSON only.";
    }

    private static double StdDev(List<double> values)
    {
        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";

    private readonly record struct CallResult(double Probability, string Reasoning, int InputTokens, int OutputTokens);
}
