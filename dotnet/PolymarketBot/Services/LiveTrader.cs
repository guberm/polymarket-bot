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

        // GTC orders may not fill immediately — poll status before tracking as position.
        // With +2-tick aggression the order should cross the spread and fill quickly.
        bool matched = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(3000, ct);
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
            _log.LogWarning("GTC order not filled after 15s, cancelling: {OrderId}", result.OrderId);
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

        // Poll for fill — same cadence as BUY (5 × 3s = 15s)
        bool matched = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(3000, ct);
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
            _log.LogWarning("SELL order not filled after 15s, cancelling: {OrderId}", result.OrderId);
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

    public async Task<Trade?> ExecuteTopupAndSellAsync(TopupCandidate candidate, Portfolio portfolio, CancellationToken ct = default)
    {
        var pos = candidate.Position;
        var price = pos.CurrentPrice;
        var buyUsd = candidate.TopupCost;

        // Step 1: BUY 5 tokens to top up position
        _log.LogInformation("TOPUP BUY: {Question} 5 tokens @ {Price:F4} (${Cost:F2})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], price, buyUsd);

        ClobApiClient.OrderResult? buyResult;
        try
        {
            buyResult = await _clob.PostMarketBuyOrderAsync(pos.TokenId, buyUsd, price, ct);
            if (buyResult is null)
            {
                _log.LogWarning("TOPUP BUY order returned null");
                return null;
            }
            _log.LogInformation("TOPUP BUY GTC order submitted: {OrderId}", buyResult.OrderId);
        }
        catch (Exception ex)
        {
            _log.LogError("TOPUP BUY order exception: {Error}", ex.Message);
            return null;
        }

        // Poll for BUY fill
        bool buyMatched = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(2000, ct);
            var status = await _clob.GetOrderStatusAsync(buyResult.OrderId, ct);
            _log.LogDebug("TOPUP BUY poll {Attempt}: status={Status}", attempt + 1, status);
            if (status == "MATCHED")
            {
                buyMatched = true;
                _log.LogInformation("TOPUP BUY MATCHED: {OrderId}", buyResult.OrderId);
                break;
            }
            if (status is "CANCELLED" or "DELAYED") break;
        }

        if (!buyMatched)
        {
            _log.LogWarning("TOPUP BUY not filled after 6s, cancelling: {OrderId}", buyResult.OrderId);
            await _clob.CancelOrderAsync(buyResult.OrderId, ct);
            return null;
        }

        // BUY filled — update position in portfolio
        portfolio.AddToPosition(pos.ConditionId, 5.0, buyUsd);

        // Step 2: SELL all tokens (now >= 5)
        var totalShares = pos.Shares;  // already updated by AddToPosition
        _log.LogInformation("TOPUP SELL: {Shares:F2} tokens @ {Price:F4}", totalShares, price);

        ClobApiClient.OrderResult? sellResult;
        try
        {
            sellResult = await _clob.PostMarketSellOrderAsync(pos.TokenId, totalShares, price, ct);
            if (sellResult is null)
            {
                _log.LogWarning("TOPUP SELL order returned null (position now has {Shares:F2} tokens)", totalShares);
                return null;
            }
            _log.LogInformation("TOPUP SELL GTC order submitted: {OrderId}", sellResult.OrderId);
        }
        catch (Exception ex)
        {
            _log.LogError("TOPUP SELL order exception (position now has {Shares:F2} tokens): {Error}", totalShares, ex.Message);
            return null;
        }

        // Poll for SELL fill — 5 × 3s = 15s
        bool sellMatched = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(3000, ct);
            var status = await _clob.GetOrderStatusAsync(sellResult.OrderId, ct);
            _log.LogDebug("TOPUP SELL poll {Attempt}: status={Status}", attempt + 1, status);
            if (status == "MATCHED")
            {
                sellMatched = true;
                _log.LogInformation("TOPUP SELL MATCHED: {OrderId}", sellResult.OrderId);
                break;
            }
            if (status is "CANCELLED" or "DELAYED") break;
        }

        if (!sellMatched)
        {
            _log.LogWarning(
                "TOPUP SELL not filled after 15s, cancelling: {OrderId} (position now sellable with {Shares:F2} tokens)",
                sellResult.OrderId, totalShares);
            await _clob.CancelOrderAsync(sellResult.OrderId, ct);
            return null;
        }

        // Both orders filled — close position
        var pnl = portfolio.ClosePosition(pos.ConditionId, price);
        _log.LogInformation("TOPUP+SELL complete: {Question} PnL=${Pnl:+0.00;-0.00} ({Reason})",
            pos.Question[..Math.Min(pos.Question.Length, 40)], pnl, candidate.ExitReason);

        return new Trade
        {
            TradeId = Guid.NewGuid().ToString(),
            ConditionId = pos.ConditionId,
            Question = pos.Question,
            Side = pos.Side,
            Action = TradeAction.SELL,
            Price = price,
            SizeUsd = pos.SizeUsd,
            Shares = totalShares,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            OrderId = sellResult.OrderId,
            IsPaper = false,
            Rationale = $"Topup+Exit: {candidate.ExitReason}",
            ExitReason = candidate.ExitReason,
        };
    }
}
