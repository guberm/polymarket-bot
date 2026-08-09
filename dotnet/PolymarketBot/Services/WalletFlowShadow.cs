using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed record WalletFlowReference(
    int WindowMinutes,
    int TradeCount,
    int WalletCount,
    double GrossVolumeUsd,
    double YesDirectionVolumeUsd,
    double NoDirectionVolumeUsd,
    double NetYesFlowUsd,
    double FlowImbalance,
    double TopWalletShare,
    int LargeTradeCount,
    double LargeTradeShare,
    long ObservedAt);

public sealed class WalletFlowShadow
{
    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<WalletFlowShadow> _log;
    private readonly Func<DateTimeOffset> _now;

    public WalletFlowShadow(BotConfig config, HttpClient http, ILogger<WalletFlowShadow> log,
        Func<DateTimeOffset>? now = null)
    {
        _config = config;
        _http = http;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<WalletFlowReference?> LookupAsync(MarketInfo market, CancellationToken ct = default)
    {
        var now = _now().ToUnixTimeSeconds();
        var windowMinutes = Math.Max(1, _config.WalletFlowWindowMinutes);
        var start = now - windowMinutes * 60L;
        var limit = Math.Clamp(_config.WalletFlowTradesLimit, 1, 10_000);
        var url = $"{_config.WalletFlowApiHost.TrimEnd('/')}/trades" +
                  $"?market={Uri.EscapeDataString(market.ConditionId)}&start={start}&end={now}" +
                  $"&limit={limit}&takerOnly=true";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = Aggregate(document.RootElement, market.ConditionId, start, now, windowMinutes);
            _log.LogInformation("Wallet-flow shadow: {Trades} trades, ${Volume:F2} volume, imbalance={Imbalance:+0.00;-0.00;0.00}",
                result.TradeCount, result.GrossVolumeUsd, result.FlowImbalance);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Wallet-flow shadow lookup failed: {Message}", ex.Message);
            return null;
        }
    }

    private WalletFlowReference Aggregate(JsonElement root, string conditionId, long start, long end, int windowMinutes)
    {
        var yesVolume = 0.0;
        var noVolume = 0.0;
        var largeVolume = 0.0;
        var tradeCount = 0;
        var largeCount = 0;
        var walletVolume = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var threshold = Math.Max(0, _config.WalletFlowLargeTradeUsd);

        if (root.ValueKind == JsonValueKind.Array)
        foreach (var trade in root.EnumerateArray())
        {
            if (!Text(trade, "conditionId").Equals(conditionId, StringComparison.OrdinalIgnoreCase)) continue;
            var timestamp = Number(trade, "timestamp");
            var size = Number(trade, "size");
            var price = Number(trade, "price");
            if (timestamp < start || timestamp > end || size <= 0 || price <= 0) continue;
            var notional = size * price;
            if (!double.IsFinite(notional) || notional <= 0) continue;

            var side = Text(trade, "side").ToUpperInvariant();
            var outcome = Text(trade, "outcome").ToUpperInvariant();
            var yesDirection = outcome == "YES" && side == "BUY" || outcome == "NO" && side == "SELL";
            var noDirection = outcome == "NO" && side == "BUY" || outcome == "YES" && side == "SELL";
            if (!yesDirection && !noDirection) continue;

            tradeCount++;
            if (yesDirection) yesVolume += notional;
            else noVolume += notional;
            var wallet = Text(trade, "proxyWallet").Trim();
            if (wallet.Length > 0)
                walletVolume[wallet] = walletVolume.GetValueOrDefault(wallet) + notional;
            if (notional >= threshold)
            {
                largeCount++;
                largeVolume += notional;
            }
        }

        var gross = yesVolume + noVolume;
        return new WalletFlowReference(
            windowMinutes,
            tradeCount,
            walletVolume.Count,
            gross,
            yesVolume,
            noVolume,
            yesVolume - noVolume,
            gross > 0 ? (yesVolume - noVolume) / gross : 0,
            gross > 0 ? walletVolume.Values.DefaultIfEmpty().Max() / gross : 0,
            largeCount,
            gross > 0 ? largeVolume / gross : 0,
            end);
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double Number(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }
}
