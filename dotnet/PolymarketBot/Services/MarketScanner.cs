using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class MarketScanner
{
    private static readonly Dictionary<string, string[]> CategoryKeywords = new()
    {
        ["politics"] = ["president", "election", "congress", "senate", "governor", "vote", "party",
                        "democrat", "republican", "trump", "biden", "political", "inaugur"],
        ["sports"] = ["nfl", "nba", "mlb", "nhl", "soccer", "football", "basketball", "baseball",
                      "tennis", "ufc", "fight", "championship", "super bowl", "world series",
                      "premier league", "match", "game", "serie a", "ncaa"],
        ["crypto"] = ["bitcoin", "btc", "ethereum", "eth", "crypto", "solana", "sol", "token",
                      "defi", "blockchain", "coin", "memecoin"],
        ["weather"] = ["weather", "temperature", "hurricane", "storm", "rainfall", "snow", "climate"],
        ["entertainment"] = ["oscar", "grammy", "emmy", "movie", "film", "tv", "show", "album",
                             "music", "celebrity", "award"],
        ["finance"] = ["fed", "interest rate", "inflation", "gdp", "stock", "market", "s&p",
                       "nasdaq", "dow", "recession", "unemployment"],
    };

    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<MarketScanner> _log;
    private const string BaseUrl = "https://gamma-api.polymarket.com";

    public MarketScanner(BotConfig config, HttpClient http, ILogger<MarketScanner> log)
    {
        _config = config;
        _http = http;
        _log = log;
    }

    public async Task<List<MarketInfo>> ScanAsync(CancellationToken ct = default)
    {
        var rawEvents = await FetchAllEventsAsync(ct);
        var markets = new List<MarketInfo>();

        foreach (var evt in rawEvents)
        {
            var eventTitle = evt.GetPropertyOrDefault("title", "");
            var eventSlug = evt.GetPropertyOrDefault("slug", "");
            var description = evt.GetPropertyOrDefault("description", "");
            var category = Categorize(eventTitle, eventSlug);

            if (evt.TryGetProperty("markets", out var marketsEl) && marketsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var mkt in marketsEl.EnumerateArray())
                {
                    var parsed = ParseMarket(mkt, eventTitle, description, category);
                    if (parsed is not null)
                        markets.Add(parsed);
                }
            }
        }

        markets.Sort((a, b) => b.Volume24Hr.CompareTo(a.Volume24Hr));
        _log.LogInformation("Scan complete: {EventCount} events, {MarketCount} eligible markets",
            rawEvents.Count, markets.Count);
        return markets;
    }

    private async Task<List<JsonElement>> FetchAllEventsAsync(CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var offset = 0;
        const int limit = 100;

        while (true)
        {
            var page = await FetchEventsPageAsync(offset, limit, ct);
            if (page.Count == 0) break;
            all.AddRange(page);
            if (page.Count < limit) break;
            offset += limit;
        }

        return all;
    }

    private async Task<List<JsonElement>> FetchEventsPageAsync(int offset, int limit, CancellationToken ct)
    {
        var url = $"{BaseUrl}/events?active=true&closed=false&limit={limit}&offset={offset}";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await _http.GetAsync(url, ct);

                if ((int)resp.StatusCode == 429)
                {
                    var wait = (int)Math.Pow(2, attempt);
                    _log.LogWarning("Rate limited on /events, retrying in {Wait}s", wait);
                    await Task.Delay(wait * 1000, ct);
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.EnumerateArray().ToList();

                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt < 2)
                {
                    var wait = (int)Math.Pow(2, attempt);
                    _log.LogWarning("Error fetching events (attempt {Attempt}): {Error}, retrying in {Wait}s",
                        attempt + 1, ex.Message, wait);
                    await Task.Delay(wait * 1000, ct);
                }
                else
                {
                    _log.LogError("Failed to fetch events after 3 attempts: {Error}", ex.Message);
                    return [];
                }
            }
        }

        return [];
    }

    private MarketInfo? ParseMarket(JsonElement mkt, string eventTitle, string description, string category)
    {
        try
        {
            if (!mkt.GetPropertyOrDefault("active", false) || mkt.GetPropertyOrDefault("closed", false))
                return null;

            var outcomes = ParseJsonStringOrArray(mkt, "outcomes");
            if (outcomes.Count != 2) return null;

            var prices = ParseJsonStringOrArray(mkt, "outcomePrices");
            if (prices.Count != 2) return null;

            var yesPrice = double.Parse(prices[0]);
            var noPrice = double.Parse(prices[1]);

            var tokens = ParseJsonStringOrArray(mkt, "clobTokenIds");
            if (tokens.Count != 2) return null;

            var liquidity = mkt.GetDoubleOrDefault("liquidity");
            var volume = mkt.GetDoubleOrDefault("volume");
            var volume24Hr = mkt.GetDoubleOrDefault("volume24hr");

            if (liquidity < _config.MinLiquidity) return null;
            if (volume24Hr < _config.MinVolume24Hr) return null;

            var endDateStr = mkt.GetPropertyOrDefault("endDate", "");
            if (!string.IsNullOrEmpty(endDateStr))
            {
                if (DateTimeOffset.TryParse(endDateStr, out var endDate))
                {
                    var hoursLeft = (endDate - DateTimeOffset.UtcNow).TotalHours;
                    if (hoursLeft < _config.MinTimeToResolutionHours)
                        return null;
                }
            }

            var bestBid = mkt.GetDoubleOrDefault("bestBid");
            var bestAsk = mkt.GetDoubleOrDefault("bestAsk");
            var spread = bestAsk > bestBid ? bestAsk - bestBid : 0.0;

            return new MarketInfo
            {
                ConditionId = mkt.GetPropertyOrDefault("conditionId", ""),
                Question = mkt.GetPropertyOrDefault("question", eventTitle),
                Slug = mkt.GetPropertyOrDefault("slug", ""),
                OutcomeYesPrice = yesPrice,
                OutcomeNoPrice = noPrice,
                TokenIdYes = tokens[0],
                TokenIdNo = tokens[1],
                Liquidity = liquidity,
                Volume = volume,
                Volume24Hr = volume24Hr,
                BestBid = bestBid,
                BestAsk = bestAsk,
                Spread = spread,
                EndDate = endDateStr,
                Category = category,
                EventTitle = eventTitle,
                Description = mkt.GetPropertyOrDefault("description", description),
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug("Failed to parse market: {Error}", ex.Message);
            return null;
        }
    }

    private static List<string> ParseJsonStringOrArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
            return [];

        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString() ?? "[]";
            var arr = JsonDocument.Parse(str).RootElement;
            if (arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.GetString() ?? e.GetRawText().Trim('"')).ToList();
        }

        return [];
    }

    private static string Categorize(string title, string slug)
    {
        var text = $"{title} {slug}".ToLowerInvariant();
        foreach (var (category, keywords) in CategoryKeywords)
        {
            if (keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                return category;
        }
        return "other";
    }

    public async Task<double?> GetMarketPriceAsync(string tokenId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://clob.polymarket.com/midpoint?token_id={tokenId}";
            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mid", out var mid))
                return double.Parse(mid.GetString() ?? "0");
            return null;
        }
        catch
        {
            return null;
        }
    }
}

internal static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement el, string name, string defaultValue)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? defaultValue;
        return defaultValue;
    }

    public static bool GetPropertyOrDefault(this JsonElement el, string name, bool defaultValue)
    {
        if (el.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    public static double GetDoubleOrDefault(this JsonElement el, string name, double defaultValue = 0.0)
    {
        if (!el.TryGetProperty(name, out var prop)) return defaultValue;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble();
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var val)) return val;
        if (prop.ValueKind == JsonValueKind.Null) return defaultValue;
        return defaultValue;
    }
}
