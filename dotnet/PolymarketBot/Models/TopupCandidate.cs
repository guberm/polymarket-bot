namespace PolymarketBot.Models;

/// <summary>Tiny position (&lt;5 tokens) that wants to exit but needs a top-up BUY first.</summary>
public sealed class TopupCandidate
{
    public required Position Position { get; init; }
    public required string ExitReason { get; init; }
    public double TokensToBuy { get; init; }    // 5.0 (CLOB minimum for BUY order)
    public double TopupCost { get; init; }       // TokensToBuy * CurrentPrice
    public double RecoveryValue { get; init; }   // Position.Shares * CurrentPrice (stuck capital to free)
}
