using PolymarketBot.Models;

namespace PolymarketBot.Services;

public interface ITrader
{
    Task<Trade?> ExecuteAsync(Signal signal, Portfolio portfolio, CancellationToken ct = default);
}
