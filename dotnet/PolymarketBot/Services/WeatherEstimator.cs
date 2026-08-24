using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed record WeatherEstimateResult(double Probability, int MemberCount, int MatchingMembers, string Station, string Reasoning);

public sealed class WeatherEstimator(HttpClient http)
{
    public const string ProviderName = "weather_gfs";
    public const string ModelName = "gfs_seamless";
    private const string Endpoint = "https://ensemble-api.open-meteo.com/v1/ensemble";
    private static readonly Dictionary<string, (double Lat, double Lon)> Stations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KLGA"] = (40.7769, -73.8740), ["KNYC"] = (40.7794, -73.9692),
        ["KORD"] = (41.9742, -87.9073), ["KMIA"] = (25.7959, -80.2870),
        ["KLAX"] = (33.9425, -118.4081), ["KDEN"] = (39.8617, -104.6731),
    };
    private readonly Dictionary<string, (DateTimeOffset At, Dictionary<string, double[]> Data)> _cache = [];
    private sealed record Spec(string Station, double Lat, double Lon, DateOnly Date, string Metric, double? Lower, double? Upper);

    public static double ProbabilityForRange(IEnumerable<double> members, double? lower, double? upper)
    {
        var values = members.ToList();
        if (values.Count == 0) return 0;
        var low = lower.HasValue ? lower.Value - .5 : double.NegativeInfinity;
        var high = upper.HasValue ? upper.Value + .5 : double.PositiveInfinity;
        return (double)values.Count(value => value >= low && value < high) / values.Count;
    }

    private static Spec? Parse(MarketInfo market)
    {
        var text = $"{market.Question} {market.EventTitle} {market.Slug}";
        if (!market.Category.Equals("weather", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("temperature", StringComparison.OrdinalIgnoreCase)) return null;
        var metric = Regex.IsMatch(text, @"\b(highest|high temperature|daily high)\b", RegexOptions.IgnoreCase)
            ? "high" : Regex.IsMatch(text, @"\b(lowest|low temperature|daily low)\b", RegexOptions.IgnoreCase) ? "low" : null;
        if (metric is null) return null;
        var stationMatch = Regex.Match(market.Description ?? "", @"(?:[?&]site=|\bstation\s+)([a-z0-9]{4})\b", RegexOptions.IgnoreCase);
        var station = stationMatch.Success ? stationMatch.Groups[1].Value.ToUpperInvariant() : CityStation(text);
        if (station is null || !Stations.TryGetValue(station, out var coords)) return null;
        var range = TemperatureRange(text);
        var targetDate = TargetDate(text, market.EndDate);
        return range is null || targetDate is null ? null
            : new Spec(station, coords.Lat, coords.Lon, targetDate.Value, metric, range.Value.Lower, range.Value.Upper);
    }

    private static string? CityStation(string text)
    {
        if (Regex.IsMatch(text, @"\b(new york city|new york|nyc)\b", RegexOptions.IgnoreCase)) return "KLGA";
        if (Regex.IsMatch(text, @"\bchicago\b", RegexOptions.IgnoreCase)) return "KORD";
        if (Regex.IsMatch(text, @"\bmiami\b", RegexOptions.IgnoreCase)) return "KMIA";
        if (Regex.IsMatch(text, @"\blos angeles\b", RegexOptions.IgnoreCase)) return "KLAX";
        if (Regex.IsMatch(text, @"\bdenver\b", RegexOptions.IgnoreCase)) return "KDEN";
        return null;
    }

    private static (double? Lower, double? Upper)? TemperatureRange(string text)
    {
        var bracket = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*(?:-|–|—|to)\s*(-?\d+(?:\.\d+)?)\s*°?\s*f\b", RegexOptions.IgnoreCase);
        if (bracket.Success)
        {
            var a = double.Parse(bracket.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = double.Parse(bracket.Groups[2].Value, CultureInfo.InvariantCulture);
            return (Math.Min(a, b), Math.Max(a, b));
        }
        var tail = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*°?\s*f\s*(?:or\s+)?(below|lower|less|under|higher|above|more|over)\b", RegexOptions.IgnoreCase);
        if (!tail.Success) return null;
        var threshold = double.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture);
        var direction = tail.Groups[2].Value.ToLowerInvariant();
        return direction is "below" or "lower" or "less" or "under" ? (null, threshold) : (threshold, null);
    }

    private static DateOnly? TargetDate(string text, string endDate)
    {
        var year = DateTimeOffset.TryParse(endDate, out var end) ? end.Year : DateTime.UtcNow.Year;
        var match = Regex.Match(text,
            @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var month = DateTime.ParseExact(match.Groups[1].Value, "MMMM", CultureInfo.InvariantCulture).Month;
        return new DateOnly(year, month, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    public async Task<WeatherEstimateResult?> EstimateAsync(MarketInfo market, CancellationToken ct = default)
    {
        var spec = Parse(market);
        if (spec is null) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (spec.Date < today || spec.Date > today.AddDays(16)) return null;
        var forecast = await ForecastAsync(spec, ct);
        if (forecast is null) return null;
        var prefix = spec.Metric == "high" ? "temperature_2m_max" : "temperature_2m_min";
        var members = forecast.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .SelectMany(item => item.Value.Take(1)).Where(double.IsFinite).ToList();
        if (members.Count < 10) return null;
        var raw = ProbabilityForRange(members, spec.Lower, spec.Upper);
        var probability = Math.Clamp(raw, .05, .95);
        var matching = (int)Math.Round(raw * members.Count);
        return new WeatherEstimateResult(probability, members.Count, matching, spec.Station,
            $"GFS ensemble {spec.Station}: {matching}/{members.Count} members match");
    }

    private async Task<Dictionary<string, double[]>?> ForecastAsync(Spec spec, CancellationToken ct)
    {
        var key = $"{spec.Lat:F4}:{spec.Lon:F4}:{spec.Date:yyyy-MM-dd}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(15))
            return cached.Data;
        var url = FormattableString.Invariant(
            $"{Endpoint}?latitude={spec.Lat}&longitude={spec.Lon}&daily=temperature_2m_max,temperature_2m_min&temperature_unit=fahrenheit&timezone=auto&start_date={spec.Date:yyyy-MM-dd}&end_date={spec.Date:yyyy-MM-dd}&models={ModelName}");
        try
        {
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object)
                return null;
            var data = new Dictionary<string, double[]>();
            foreach (var item in daily.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.Array)
                    data[item.Name] = item.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetDouble()).ToArray();
            _cache[key] = (DateTimeOffset.UtcNow, data);
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
