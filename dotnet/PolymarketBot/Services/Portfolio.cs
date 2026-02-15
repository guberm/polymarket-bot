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

        // Fractional Kelly + position cap
        var kelly = kellyRaw * _config.KellyFraction;
        var sizeUsd = kelly * Bankroll;
        sizeUsd = Math.Min(sizeUsd, Bankroll * _config.MaxPositionPct);

        if (sizeUsd < _config.MinTradeUsd) return null;

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
            _log.LogDebug("Already positioned in {Question}", Truncate(signal.Market.Question, 40));
            return false;
        }

        if (Positions.Count >= _config.MaxConcurrentPositions)
        {
            _log.LogDebug("Max concurrent positions reached");
            return false;
        }

        var newExposure = TotalExposure() + signal.PositionSizeUsd;
        if (newExposure > Bankroll * _config.MaxTotalExposurePct)
        {
            _log.LogDebug("Total exposure limit exceeded");
            return false;
        }

        var catExp = CategoryExposure(signal.Market.Category) + signal.PositionSizeUsd;
        if (catExp > Bankroll * _config.MaxCategoryExposurePct)
        {
            _log.LogDebug("Category '{Category}' exposure limit exceeded", signal.Market.Category);
            return false;
        }

        // Daily stop loss
        var dailyPnl = Bankroll - DailyStartValue;
        if (dailyPnl < 0 && Math.Abs(dailyPnl) > DailyStartValue * _config.DailyStopLossPct)
        {
            _log.LogWarning("Daily stop loss triggered");
            IsHalted = true;
            return false;
        }

        // Max drawdown
        if (HighWaterMark > 0)
        {
            var drawdown = (HighWaterMark - Bankroll) / HighWaterMark;
            if (drawdown > _config.MaxDrawdownPct)
            {
                _log.LogWarning("Max drawdown {Drawdown:P1} exceeded", drawdown);
                IsHalted = true;
                return false;
            }
        }

        // Agent death
        if (Bankroll <= 0)
        {
            _log.LogWarning("Bankroll depleted — agent is dead");
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
