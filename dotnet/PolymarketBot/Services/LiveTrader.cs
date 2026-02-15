using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

/// <summary>
/// Live execution via Polymarket CLOB API.
/// Uses direct HTTP calls (no py-clob-client equivalent for .NET).
/// </summary>
public sealed class LiveTrader : ITrader
{
    private readonly BotConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<LiveTrader> _log;

    public LiveTrader(BotConfig config, HttpClient http, ILogger<LiveTrader> log)
    {
        _config = config;
        _http = http;
        _log = log;
        _log.LogInformation("Live CLOB trader initialized");
    }

    public async Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.MarketPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;

        string orderId;
        try
        {
            // POST market order to CLOB
            var orderPayload = new
            {
                token_id = tokenId,
                amount = sizeUsd,
                side = "BUY",
                type = "FOK",
            };

            var json = JsonSerializer.Serialize(orderPayload);
            var request = new HttpRequestMessage(HttpMethod.Post, "https://clob.polymarket.com/order")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            // Authentication headers would be set up via the private key signing flow.
            // Full CLOB auth requires EIP-712 signing which needs a dedicated crypto library.
            // For a production implementation, integrate Nethereum for wallet signing.

            var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();
            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(respJson);

            orderId = doc.RootElement.TryGetProperty("orderID", out var oid) ? oid.GetString() ?? ""
                     : doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? ""
                     : Guid.NewGuid().ToString();

            _log.LogInformation("CLOB order placed: {OrderId}", orderId);
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB order failed: {Error}", ex.Message);
            return null;
        }

        var shares = price > 0 ? sizeUsd / price : 0.0;

        var position = new Position
        {
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            TokenId = tokenId,
            EntryPrice = price,
            SizeUsd = sizeUsd,
            Shares = shares,
            CurrentPrice = price,
            UnrealizedPnl = 0.0,
            Category = market.Category,
            OrderId = orderId,
        };
        portfolio.OpenPosition(position);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            Action = TradeAction.BUY,
            Price = price,
            SizeUsd = sizeUsd,
            Shares = shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = orderId,
            IsPaper = false,
            Rationale = signal.Estimate.ReasoningSummary,
            EdgeAtEntry = signal.Edge,
            KellyAtEntry = signal.KellyFraction,
        };
    }
}
