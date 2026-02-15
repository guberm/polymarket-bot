using Microsoft.Extensions.Logging;
using PolymarketBot;
using PolymarketBot.Services;

// ── Parse args ──────────────────────────────────────────────────

var verbose = args.Contains("--verbose") || args.Contains("-v");

// ── Config ──────────────────────────────────────────────────────

var config = BotConfig.FromEnv();

// ── Logging ─────────────────────────────────────────────────────

Directory.CreateDirectory(config.DataDir);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddSimpleConsole(opts =>
    {
        opts.TimestampFormat = "[HH:mm:ss] ";
        opts.SingleLine = true;
    });

    // JSON file logging
    var logPath = Path.Combine(config.DataDir, "bot.log");
    builder.AddJsonConsole(); // structured logs go to console; file appender below
});

var log = loggerFactory.CreateLogger("bot.main");

// Also write structured JSON to file
var fileLogStream = new StreamWriter(
    Path.Combine(config.DataDir, "bot.log"), append: true) { AutoFlush = true };

var mode = config.LiveTrading ? "LIVE" : "PAPER";
log.LogInformation(new string('=', 60));
log.LogInformation("Polymarket Bot (.NET)");
log.LogInformation("Mode: {Mode} | Bankroll: ${Bankroll:F2}", mode, config.InitialBankroll);
log.LogInformation("Min edge: {MinEdge:P0} | Max position: {MaxPos:P0}", config.MinEdge, config.MaxPositionPct);
log.LogInformation("Scan interval: {Interval} min | Markets/cycle: {Markets}",
    config.ScanIntervalMinutes, config.MarketsPerCycle);
log.LogInformation("Ensemble: {Size}x {Model}", config.EnsembleSize, config.ClaudeModel);
log.LogInformation(new string('=', 60));

if (string.IsNullOrEmpty(config.AnthropicApiKey))
{
    log.LogError("ANTHROPIC_API_KEY not set");
    return 1;
}

// ── Load state ──────────────────────────────────────────────────

var snapshot = PersistenceService.LoadSnapshot(config.DataDir);
var portfolio = new Portfolio(config, loggerFactory.CreateLogger<Portfolio>(), snapshot);

if (snapshot is not null)
{
    log.LogInformation("Resumed from saved state: ${Bankroll:F2} bankroll, {Positions} positions",
        portfolio.Bankroll, portfolio.Positions.Count);
}
else
{
    log.LogInformation("Starting fresh");
}

// ── Services ────────────────────────────────────────────────────

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

var scanner = new MarketScanner(config, httpClient, loggerFactory.CreateLogger<MarketScanner>());
var estimator = new Estimator(config, httpClient, loggerFactory.CreateLogger<Estimator>());

ITrader trader;
if (config.LiveTrading)
{
    if (string.IsNullOrEmpty(config.PolymarketPrivateKey))
    {
        log.LogError("POLYMARKET_PRIVATE_KEY not set for live trading");
        return 1;
    }
    trader = new LiveTrader(config, httpClient, loggerFactory.CreateLogger<LiveTrader>());
}
else
{
    trader = new PaperTrader();
}

// ── Graceful shutdown ───────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    log.LogInformation("Shutdown requested...");
    cts.Cancel();
};

var lastDailyReset = DateTimeOffset.UtcNow.Date;
var cycle = 0;

// ── Main loop ───────────────────────────────────────────────────

while (!cts.Token.IsCancellationRequested)
{
    cycle++;

    if (portfolio.IsHalted)
    {
        log.LogWarning("Portfolio halted — stopping");
        break;
    }

    // Daily reset
    var today = DateTimeOffset.UtcNow.Date;
    if (today != lastDailyReset)
    {
        portfolio.ResetDaily();
        lastDailyReset = today;
        log.LogInformation("New day — daily start value reset to ${Bankroll:F2}", portfolio.Bankroll);
    }

    log.LogInformation("--- Cycle {Cycle} ---", cycle);

    try
    {
        var markets = await scanner.ScanAsync(cts.Token);
        var tradesThisCycle = 0;

        foreach (var market in markets.Take(config.MarketsPerCycle))
        {
            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            if (portfolio.HasPosition(market.ConditionId))
                continue;

            // Estimate fair value
            var estimate = await estimator.EstimateAsync(market, cts.Token);
            if (estimate is null) continue;

            // Agent pays for inference
            portfolio.RecordApiCost(estimate.InputTokensUsed, estimate.OutputTokensUsed);

            if (portfolio.Bankroll <= 0)
            {
                log.LogWarning("Bankroll depleted by API costs — agent is dead");
                portfolio.IsHalted = true;
                break;
            }

            // Generate signal
            var signal = portfolio.GenerateSignal(market, estimate);
            if (signal is null) continue;

            // Risk check
            if (!portfolio.CheckRisk(signal)) continue;

            // Execute
            var trade = await trader.ExecuteAsync(signal, portfolio, cts.Token);
            if (trade is not null)
            {
                PersistenceService.AppendTrade(trade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                tradesThisCycle++;

                log.LogInformation(
                    "TRADE: {Side} {Question} ${Size:F2} @ {Price:F3} (edge={Edge:P1}, EV=${EV:F2})",
                    trade.Side, Truncate(market.Question, 50), trade.SizeUsd, trade.Price,
                    signal.Edge, signal.ExpectedValue);
            }
        }

        // Cycle summary
        log.LogInformation(
            "Cycle {Cycle}: {Trades} trades | Bankroll: ${Bankroll:F2} | Positions: {Positions} | " +
            "Exposure: ${Exposure:F2} | API cost: ${ApiCost:F4} | Realized PnL: ${PnL:+0.00;-0.00}",
            cycle, tradesThisCycle, portfolio.Bankroll, portfolio.Positions.Count,
            portfolio.TotalExposure(), portfolio.TotalApiCost, portfolio.TotalRealizedPnl);

        PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Cycle {Cycle} error", cycle);
    }

    // Sleep in 1-second ticks for responsive shutdown
    if (!cts.Token.IsCancellationRequested)
    {
        log.LogInformation("Next scan in {Interval} min", config.ScanIntervalMinutes);
        for (var i = 0; i < config.ScanIntervalMinutes * 60; i++)
        {
            if (cts.Token.IsCancellationRequested) break;
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
    }
}

// ── Final save ──────────────────────────────────────────────────

PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
log.LogInformation(
    "Bot stopped | Final bankroll: ${Bankroll:F2} | Total trades: {Trades} | " +
    "Total API cost: ${ApiCost:F4} | Realized PnL: ${PnL:+0.00;-0.00}",
    portfolio.Bankroll, portfolio.TotalTrades, portfolio.TotalApiCost, portfolio.TotalRealizedPnl);

fileLogStream.Dispose();
return 0;

static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
