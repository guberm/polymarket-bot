using PolymarketBot.Models;

namespace PolymarketBot.Services;

public interface ITrader
{
    Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default);
    Task<Trade?> ExecuteSellAsync(ExitSignal exitSignal, Portfolio portfolio, CancellationToken ct = default);
    Task<Trade?> ExecuteTopupAndSellAsync(TopupCandidate candidate, Portfolio portfolio, CancellationToken ct = default);
}
