using PolymarketBot.Models;

namespace PolymarketBot.Services;

public sealed class PaperTrader : ITrader
{
    public Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default)
    {
        var market = signal.Market;
        var price = signal.MarketPrice;
        var sizeUsd = signal.PositionSizeUsd;
        var shares = price > 0 ? sizeUsd / price : 0.0;
        var tokenId = signal.Side == Side.YES ? market.TokenIdYes : market.TokenIdNo;

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
        };
        portfolio.OpenPosition(position);

        var trade = new Trade
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
            IsPaper = true,
            Rationale = signal.Estimate.ReasoningSummary,
            EdgeAtEntry = signal.Edge,
            KellyAtEntry = signal.KellyFraction,
        };

        return Task.FromResult<Trade?>(trade);
    }
}
