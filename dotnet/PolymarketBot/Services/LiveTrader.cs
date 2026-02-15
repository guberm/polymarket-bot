using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

/// <summary>
/// Live execution via Polymarket CLOB API with proper EIP-712 + HMAC auth.
/// </summary>
public sealed class LiveTrader : ITrader
{
    private readonly ClobApiClient _clob;
    private readonly ILogger<LiveTrader> _log;

    public LiveTrader(ClobApiClient clob, ILogger<LiveTrader> log)
    {
        _clob = clob;
        _log = log;
        _log.LogInformation("Live CLOB trader initialized");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _clob.InitializeAsync(ct);
    }

    public async Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.MarketPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;

        string? orderId;
        try
        {
            orderId = await _clob.PostMarketBuyOrderAsync(tokenId, sizeUsd, price, ct);
            if (orderId is null)
            {
                _log.LogWarning("CLOB order returned null (see errors above)");
                return null;
            }
            _log.LogInformation("CLOB order placed: {OrderId}", orderId);
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB order exception: {Error}", ex.Message);
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
