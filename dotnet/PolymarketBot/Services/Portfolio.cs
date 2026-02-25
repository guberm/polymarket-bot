using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class Portfolio
{
    private const double InputCostPerMTok = 3.0;
    private const double OutputCostPerMTok = 15.0;

    private readonly BotConfig _config;
    private readonly ILogger<Portfolio> _log;

    public double Bankroll { get; private set; }
    public double InitialBankroll { get; private set; }
    public List<Position> Positions { get; private set; }
    public double HighWaterMark { get; private set; }
    public double DailyStartValue { get; private set; }
    public double TotalRealizedPnl { get; private set; }
    public int TotalTrades { get; private set; }
    public bool IsHalted { get; set; }
    public double TotalApiCost { get; private set; }

    public Portfolio(BotConfig config, ILogger<Portfolio> log, PortfolioSnapshot? snapshot = null)
    {
        _config = config;
        _log = log;

        if (snapshot is not null)
        {
            Bankroll = snapshot.Bankroll;
            InitialBankroll = snapshot.InitialBankroll;
            Positions = new List<Position>(snapshot.Positions);
            HighWaterMark = snapshot.HighWaterMark;
            DailyStartValue = snapshot.DailyStartValue;
            TotalRealizedPnl = snapshot.TotalRealizedPnl;
            TotalTrades = snapshot.TotalTrades;
            IsHalted = snapshot.IsHalted;
        }
        else
        {
            Bankroll = config.InitialBankroll;
            InitialBankroll = config.InitialBankroll;
            Positions = [];
            HighWaterMark = config.InitialBankroll;
            DailyStartValue = config.InitialBankroll;
            TotalRealizedPnl = 0;
            TotalTrades = 0;
            IsHalted = false;
        }

        TotalApiCost = 0;
    }

    public PortfolioSnapshot Snapshot() => new()
    {
        Bankroll = Bankroll,
        InitialBankroll = InitialBankroll,
        Positions = new List<Position>(Positions),
        HighWaterMark = HighWaterMark,
        DailyStartValue = DailyStartValue,
        TotalRealizedPnl = TotalRealizedPnl,
        TotalTrades = TotalTrades,
        IsHalted = IsHalted,
    };

    public double TotalExposure() => Positions.Sum(p => p.SizeUsd);

    public double CategoryExposure(string category)
        => Positions.Where(p => p.Category == category).Sum(p => p.SizeUsd);

    public bool HasPosition(string conditionId)
        => Positions.Any(p => p.ConditionId == conditionId);

    // -- Signal generation --

    public Signal? GenerateSignal(MarketInfo market, Estimate estimate)
    {
        var fair = estimate.FairProbability;
        var yesEdge = fair - market.OutcomeYesPrice;
        var noEdge = (1.0 - fair) - market.OutcomeNoPrice;

        Side side;
        double edge, marketPrice;

        if (yesEdge > noEdge && yesEdge > _config.MinEdge)
        {
            side = Side.YES;
            edge = yesEdge;
            marketPrice = market.OutcomeYesPrice;
        }
        else if (noEdge > _config.MinEdge)
        {
            side = Side.NO;
            edge = noEdge;
            marketPrice = market.OutcomeNoPrice;
        }
        else
        {
            return null;
        }

        if (marketPrice <= 0 || marketPrice >= 1) return null;

        // Kelly criterion: f* = (b*p - q) / b
        var b = (1.0 / marketPrice) - 1.0;
        var p = side == Side.YES ? fair : 1.0 - fair;
        var q = 1.0 - p;
        var kellyRaw = b > 0 ? (b * p - q) / b : 0.0;
        kellyRaw = Math.Max(0.0, kellyRaw);

        // Fractional Kelly + position cap (use portfolio value, not just cash)
        var kelly = kellyRaw * _config.KellyFraction;
        var portfolioVal = Bankroll + TotalExposure();
        var sizeUsd = kelly * portfolioVal;
        sizeUsd = Math.Min(sizeUsd, portfolioVal * _config.MaxPositionPct);
        sizeUsd = Math.Min(sizeUsd, Bankroll); // never exceed available cash

        if (sizeUsd < _config.MinTradeUsd) return null;

        // CLOB minimum order size is 5 tokens → minimum USD = 5 * price
        var minClobUsd = 5.0 * marketPrice;
        if (sizeUsd < minClobUsd)
        {
            _log.LogInformation("Position ${Size:F2} below CLOB minimum ${Min:F2} (5 tokens @ {Price:F3})",
                sizeUsd, minClobUsd, marketPrice);
            return null;
        }

        return new Signal
        {
            Market = market,
            Estimate = estimate,
            Side = side,
            Edge = edge,
            MarketPrice = marketPrice,
            KellyFraction = kelly,
            PositionSizeUsd = Math.Round(sizeUsd, 2),
            ExpectedValue = Math.Round(sizeUsd * edge, 4),
        };
    }

    // -- Risk checks --

    public bool CheckRisk(Signal signal)
    {
        if (HasPosition(signal.Market.ConditionId))
        {
            _log.LogInformation("Risk BLOCK: already positioned in {Question}", Truncate(signal.Market.Question, 40));
            return false;
        }

        if (Positions.Count >= _config.MaxConcurrentPositions)
        {
            _log.LogInformation("Risk BLOCK: max positions ({Max}) reached", _config.MaxConcurrentPositions);
            return false;
        }

        var pv = Bankroll + TotalExposure();
        var newExposure = TotalExposure() + signal.PositionSizeUsd;
        var maxAllowed = pv * _config.MaxTotalExposurePct;
        if (newExposure > maxAllowed)
        {
            _log.LogInformation("Risk BLOCK: total exposure ${New:F2} > limit ${Limit:F2}", newExposure, maxAllowed);
            return false;
        }

        var catExp = CategoryExposure(signal.Market.Category) + signal.PositionSizeUsd;
        var catLimit = pv * _config.MaxCategoryExposurePct;
        if (catExp > catLimit)
        {
            _log.LogInformation("Risk BLOCK: '{Category}' exposure ${Exp:F2} > limit ${Limit:F2}",
                signal.Market.Category, catExp, catLimit);
            return false;
        }

        // Daily stop loss (include open position value — deployed capital isn't lost)
        var portfolioValue = Bankroll + TotalExposure();
        var dailyPnl = portfolioValue - DailyStartValue;
        if (dailyPnl < 0 && Math.Abs(dailyPnl) > DailyStartValue * _config.DailyStopLossPct)
        {
            _log.LogWarning("HALT: Daily stop loss triggered (PnL=${Pnl:+0.00;-0.00}, limit={Limit:P0})",
                dailyPnl, _config.DailyStopLossPct);
            IsHalted = true;
            return false;
        }

        // Max drawdown from high water mark
        if (HighWaterMark > 0)
        {
            var drawdown = (HighWaterMark - portfolioValue) / HighWaterMark;
            if (drawdown > _config.MaxDrawdownPct)
            {
                _log.LogWarning("HALT: Max drawdown {Drawdown:P1} exceeded (limit={Limit:P0})",
                    drawdown, _config.MaxDrawdownPct);
                IsHalted = true;
                return false;
            }
        }

        // Agent death — only when total portfolio value (free cash + open positions)
        // drops below $1. Negative bankroll from API costs while holding positions
        // is normal: positions will eventually resolve and return USDC.
        if (Bankroll + TotalExposure() < 1.0)
        {
            _log.LogWarning("HALT: Portfolio value < $1 — agent is dead");
            IsHalted = true;
            return false;
        }

        return true;
    }

    // -- Position management --

    public void OpenPosition(Position position)
    {
        Bankroll -= position.SizeUsd;
        TotalTrades++;
        Positions.Add(position);
        _log.LogInformation("Opened {Side} on {Question} ${Size:F2} @ {Price:F3}",
            position.Side, Truncate(position.Question, 40), position.SizeUsd, position.EntryPrice);
    }

    public double ClosePosition(string conditionId, double exitPrice)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return 0.0;

        var pnl = pos.Shares * (exitPrice - pos.EntryPrice);
        Bankroll += pos.SizeUsd + pnl;
        TotalRealizedPnl += pnl;
        Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
        HighWaterMark = Math.Max(HighWaterMark, Bankroll);

        _log.LogInformation("Closed {Question} PnL: ${Pnl:+0.00;-0.00}", Truncate(pos.Question, 40), pnl);
        return pnl;
    }

    public double ResolvePosition(string conditionId, bool won)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return 0.0;

        var payout = won ? pos.Shares : 0.0;
        var pnl = payout - pos.SizeUsd;
        Bankroll += payout;
        TotalRealizedPnl += pnl;
        TotalTrades++;
        Positions = Positions.Where(p => p.ConditionId != conditionId).ToList();
        HighWaterMark = Math.Max(HighWaterMark, Bankroll);

        var result = won ? "WON" : "LOST";
        _log.LogInformation("Resolved ({Result}): {Question} payout=${Payout:F2}, PnL=${Pnl:+0.00;-0.00}",
            result, Truncate(pos.Question, 40), payout, pnl);
        return pnl;
    }

    // -- Position review --

    public void UpdatePositionPrices(Dictionary<string, double> prices)
    {
        foreach (var pos in Positions)
        {
            if (prices.TryGetValue(pos.TokenId, out var price))
            {
                pos.CurrentPrice = price;
                pos.UnrealizedPnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
            }
        }
    }

    public List<ExitSignal> GenerateExitSignals()
    {
        var signals = new List<ExitSignal>();
        foreach (var pos in Positions)
        {
            // Skip unsellable positions: penny prices or below CLOB minimum (5 tokens)
            if (pos.CurrentPrice < 0.01)
            {
                _log.LogDebug("Skip review: {Question} (price {Price:F4} < $0.01)",
                    Truncate(pos.Question, 40), pos.CurrentPrice);
                continue;
            }
            if (pos.Shares < 5.0)
            {
                _log.LogDebug("Skip review: {Question} ({Shares:F2} tokens < 5 minimum)",
                    Truncate(pos.Question, 40), pos.Shares);
                continue;
            }

            var pnl = pos.Shares * (pos.CurrentPrice - pos.EntryPrice);
            var pnlPct = pos.EntryPrice > 0 ? (pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice : 0.0;

            // Stop-loss
            if (pnlPct < -_config.PositionStopLossPct)
            {
                signals.Add(new ExitSignal { Position = pos, ExitReason = "stop_loss", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                continue;
            }

            // Take-profit
            if (pos.CurrentPrice >= _config.TakeProfitPrice)
            {
                signals.Add(new ExitSignal { Position = pos, ExitReason = "take_profit", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                continue;
            }

            // Edge-gone
            if (pos.FairEstimateAtEntry > 0)
            {
                var fairForSide = pos.Side == Side.YES ? pos.FairEstimateAtEntry : 1.0 - pos.FairEstimateAtEntry;
                if (pos.CurrentPrice > fairForSide + _config.ExitEdgeBuffer)
                {
                    signals.Add(new ExitSignal { Position = pos, ExitReason = "edge_gone", CurrentPrice = pos.CurrentPrice, UnrealizedPnl = pnl, PnlPct = pnlPct });
                    continue;
                }
            }
        }
        return signals;
    }

    public List<TopupCandidate> GenerateTopupCandidates()
    {
        var candidates = new List<TopupCandidate>();
        foreach (var pos in Positions)
        {
            if (pos.CurrentPrice < 0.01) continue;  // penny = unsellable even with top-up
            if (pos.Shares >= 5.0) continue;         // can sell normally
            if (pos.EntryPrice <= 0) continue;

            var pnlPct = (pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice;

            // Check same exit conditions as GenerateExitSignals
            string? exitReason = null;
            if (pnlPct < -_config.PositionStopLossPct)
                exitReason = "stop_loss";
            else if (pos.CurrentPrice >= _config.TakeProfitPrice)
                exitReason = "take_profit";
            else if (pos.FairEstimateAtEntry > 0)
            {
                var fairForSide = pos.Side == Side.YES ? pos.FairEstimateAtEntry : 1.0 - pos.FairEstimateAtEntry;
                if (pos.CurrentPrice > fairForSide + _config.ExitEdgeBuffer)
                    exitReason = "edge_gone";
            }

            if (exitReason is null) continue;

            var topupCost = 5.0 * pos.CurrentPrice;
            var recoveryValue = pos.Shares * pos.CurrentPrice;

            candidates.Add(new TopupCandidate
            {
                Position = pos,
                ExitReason = exitReason,
                TokensToBuy = 5.0,
                TopupCost = topupCost,
                RecoveryValue = recoveryValue,
            });
        }
        return candidates;
    }

    public void AddToPosition(string conditionId, double additionalShares, double additionalCost)
    {
        var pos = Positions.FirstOrDefault(p => p.ConditionId == conditionId);
        if (pos is null) return;
        pos.Shares += additionalShares;
        pos.SizeUsd += additionalCost;
        Bankroll -= additionalCost;
        _log.LogInformation(
            "Top-up: +{Shares:F2} tokens (${Cost:F2}) -> {Question} now {Total:F2} tokens",
            additionalShares, additionalCost, Truncate(pos.Question, 40), pos.Shares);
    }

    public List<Position> GetReviewCandidates()
    {
        var candidates = new List<Position>();
        foreach (var pos in Positions)
        {
            if (pos.EntryPrice <= 0) continue;
            var priceMove = Math.Abs(pos.CurrentPrice - pos.EntryPrice) / pos.EntryPrice;
            if (priceMove >= _config.ReviewReestimateThresholdPct)
                candidates.Add(pos);
        }
        candidates.Sort((a, b) => b.SizeUsd.CompareTo(a.SizeUsd));
        return candidates;
    }

    // -- Balance sync --

    /// <summary>
    /// Sync bankroll from actual on-chain USDC balance.
    /// On-chain USDC is always the free cash (bankroll) — conditional tokens
    /// are held separately in the CLOB and not reflected in USDC balance.
    /// Always syncs both up and down so the bot has an accurate view of
    /// spendable funds (handles resolved-position payouts, deposits, fees, etc.)
    /// </summary>
    public void SyncBalance(double actualUsdcBalance)
    {
        var prevBankroll = Bankroll;
        var diff = actualUsdcBalance - prevBankroll;
        if (Math.Abs(diff) <= 0.001)
            return;

        Bankroll = actualUsdcBalance;
        if (diff > 0)
            _log.LogInformation(
                "Balance sync (upward): ${Old:F2} -> ${New:F2} (+${Diff:F2}, {Count} positions open)",
                prevBankroll, Bankroll, diff, Positions.Count);
        else
            _log.LogWarning(
                "Balance sync (downward): ${Old:F2} -> ${New:F2} (${Diff:F2}, {Count} positions open)",
                prevBankroll, Bankroll, diff, Positions.Count);

        HighWaterMark = Math.Max(HighWaterMark, Bankroll + TotalExposure());
    }

    // -- Cost tracking --

    public void RecordApiCost(int inputTokens, int outputTokens)
    {
        var cost = (inputTokens * InputCostPerMTok / 1_000_000.0) +
                   (outputTokens * OutputCostPerMTok / 1_000_000.0);
        TotalApiCost += cost;
        Bankroll -= cost;
    }

    public void ResetDaily()
    {
        DailyStartValue = Bankroll;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
