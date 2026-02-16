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

    /// <summary>
    /// Fetch actual USDC balance from CLOB API.
    /// </summary>
    public async Task<double?> GetBalanceAsync(CancellationToken ct = default)
    {
        return await _clob.GetBalanceAsync(ct);
    }

    public async Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.MarketPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;

        ClobApiClient.OrderResult? result;
        try
        {
            result = await _clob.PostMarketBuyOrderAsync(tokenId, sizeUsd, price, ct);
            if (result is null)
            {
                _log.LogWarning("CLOB order returned null (see errors above)");
                return null;
            }
            _log.LogInformation("CLOB GTC order submitted: {OrderId}", result.OrderId);
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB order exception: {Error}", ex.Message);
            return null;
        }

        // GTC orders may not fill immediately — poll status before tracking as position
        bool matched = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(2000, ct);
            var status = await _clob.GetOrderStatusAsync(result.OrderId, ct);
            if (status == "MATCHED")
            {
                matched = true;
                _log.LogInformation("GTC order MATCHED: {OrderId}", result.OrderId);
                break;
            }
            _log.LogDebug("GTC order poll {Attempt}: status={Status}", attempt + 1, status);
            if (status is "CANCELLED" or "DELAYED") break; // no point retrying
        }

        if (!matched)
        {
            _log.LogWarning("GTC order not filled after 6s, cancelling: {OrderId}", result.OrderId);
            await _clob.CancelOrderAsync(result.OrderId, ct);
            return null;
        }

        // Use actual fill amounts from CLOB (may differ from requested due to price improvement)
        var actualCost = result.ActualCostUsd;
        var actualShares = result.ActualShares;
        var actualPrice = actualShares > 0 ? actualCost / actualShares : price;

        _log.LogInformation("Fill: requested ${Req:F2}, actual ${Act:F2} ({Shares:F2} shares @ {Price:F4})",
            sizeUsd, actualCost, actualShares, actualPrice);

        var position = new Position
        {
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            TokenId = tokenId,
            EntryPrice = actualPrice,
            SizeUsd = actualCost,
            Shares = actualShares,
            CurrentPrice = actualPrice,
            UnrealizedPnl = 0.0,
            Category = market.Category,
            OrderId = result.OrderId,
            FairEstimateAtEntry = signal.Estimate.FairProbability,
        };
        portfolio.OpenPosition(position);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = market.ConditionId,
            Question = market.Question,
            Side = signal.Side,
            Action = TradeAction.BUY,
            Price = actualPrice,
            SizeUsd = actualCost,
            Shares = actualShares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = result.OrderId,
            IsPaper = false,
            Rationale = signal.Estimate.ReasoningSummary,
            EdgeAtEntry = signal.Edge,
            KellyAtEntry = signal.KellyFraction,
        };
    }

    public async Task<Trade?> ExecuteSellAsync(ExitSignal exitSignal, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = exitSignal.Position;
        var price = exitSignal.CurrentPrice;

        if (pos.Shares < 5.0)
        {
            _log.LogWarning("SKIP SELL (below CLOB minimum 5 tokens): {Question} {Shares:F2} shares",
                pos.Question[..Math.Min(pos.Question.Length, 40)], pos.Shares);
            return null;
        }

        ClobApiClient.OrderResult? result;
        try
        {
            result = await _clob.PostMarketSellOrderAsync(pos.TokenId, pos.Shares, price, ct);
            if (result is null)
            {
                _log.LogWarning("CLOB SELL order returned null");
                return null;
            }
            _log.LogInformation("CLOB SELL GTC order submitted: {OrderId}", result.OrderId);
        }
        catch (Exception ex)
        {
            _log.LogError("CLOB SELL order exception: {Error}", ex.Message);
            return null;
        }

        // Poll for fill (same pattern as BUY)
        bool matched = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(2000, ct);
            var status = await _clob.GetOrderStatusAsync(result.OrderId, ct);
            if (status == "MATCHED")
            {
                matched = true;
                _log.LogInformation("SELL GTC order MATCHED: {OrderId}", result.OrderId);
                break;
            }
            _log.LogDebug("SELL GTC poll {Attempt}: status={Status}", attempt + 1, status);
            if (status is "CANCELLED" or "DELAYED") break;
        }

        if (!matched)
        {
            _log.LogWarning("SELL order not filled after 6s, cancelling: {OrderId}", result.OrderId);
            await _clob.CancelOrderAsync(result.OrderId, ct);
            return null;
        }

        // Close position in portfolio (returns capital + PnL to bankroll)
        var pnl = portfolio.ClosePosition(pos.ConditionId, price);

        _log.LogInformation("SOLD: {Question} PnL=${Pnl:+0.00;-0.00} ({Reason})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], pnl, exitSignal.ExitReason);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = pos.ConditionId,
            Question = pos.Question,
            Side = pos.Side,
            Action = TradeAction.SELL,
            Price = price,
            SizeUsd = pos.SizeUsd,
            Shares = pos.Shares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = result.OrderId,
            IsPaper = false,
            Rationale = $"Exit: {exitSignal.ExitReason}",
            ExitReason = exitSignal.ExitReason,
        };
    }
}
