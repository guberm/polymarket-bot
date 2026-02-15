namespace PolymarketBot.Models;

public sealed class MarketInfo
{
    public required string ConditionId { get; init; }
    public required string Question { get; init; }
    public required string Slug { get; init; }
    public double OutcomeYesPrice { get; init; }
    public double OutcomeNoPrice { get; init; }
    public required string TokenIdYes { get; init; }
    public required string TokenIdNo { get; init; }
    public double Liquidity { get; init; }
    public double Volume { get; init; }
    public double Volume24Hr { get; init; }
    public double BestBid { get; init; }
    public double BestAsk { get; init; }
    public double Spread { get; init; }
    public string EndDate { get; init; } = "";
    public string Category { get; init; } = "other";
    public string EventTitle { get; init; } = "";
    public string Description { get; init; } = "";
}
