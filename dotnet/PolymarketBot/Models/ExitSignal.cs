namespace PolymarketBot.Models;

/// <summary>Signal to close an existing position.</summary>
public sealed class ExitSignal
{
    public required Position Position { get; init; }
    public required string ExitReason { get; init; } // "stop_loss", "take_profit", "edge_gone", "reestimate_exit"
    public double CurrentPrice { get; init; }
    public double UnrealizedPnl { get; init; }
    public double PnlPct { get; init; } // PnL as fraction of entry price
}
