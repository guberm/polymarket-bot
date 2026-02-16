using Microsoft.Extensions.Logging;
using PolymarketBot;
using PolymarketBot.Services;

// ── Parse args ──────────────────────────────────────────────────

var verbose = args.Contains("--verbose") || args.Contains("-v");
var console_ = args.Contains("--console") || args.Contains("-c");

// ── Config ──────────────────────────────────────────────────────

var config = BotConfig.FromEnv();

// CLI args override env vars
static double? ParseDoubleArg(string[] a, string name)
{
    var idx = Array.IndexOf(a, name);
    return idx >= 0 && idx + 1 < a.Length && double.TryParse(a[idx + 1], out var v) ? v : null;
}

static int? ParseIntArg(string[] a, string name)
{
    var idx = Array.IndexOf(a, name);
    return idx >= 0 && idx + 1 < a.Length && int.TryParse(a[idx + 1], out var v) ? v : null;
}

if (ParseDoubleArg(args, "--max-position-pct") is { } maxPosPct)
    config.MaxPositionPct = maxPosPct;
if (ParseDoubleArg(args, "--max-total-exposure-pct") is { } maxExpPct)
    config.MaxTotalExposurePct = maxExpPct;
if (ParseDoubleArg(args, "--max-category-exposure-pct") is { } maxCatPct)
    config.MaxCategoryExposurePct = maxCatPct;
if (ParseDoubleArg(args, "--daily-stop-loss-pct") is { } dailySl)
    config.DailyStopLossPct = dailySl;
if (ParseDoubleArg(args, "--max-drawdown-pct") is { } maxDd)
    config.MaxDrawdownPct = maxDd;
if (ParseIntArg(args, "--max-concurrent-positions") is { } maxPos)
    config.MaxConcurrentPositions = maxPos;

static string Ts() => DateTime.Now.ToString("HH:mm:ss");

// Helper: Console.Write only if --console flag is set
void Con(string msg) { if (console_) Console.WriteLine($"[{Ts()}] {msg}"); }

// ── Logging ─────────────────────────────────────────────────────

Directory.CreateDirectory(config.DataDir);

// JSON file logger (matches Python's JsonFormatter → data/bot.log)
using var fileLogStream = new StreamWriter(
    Path.Combine(config.DataDir, "bot.log"), append: true) { AutoFlush = true };

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddSimpleConsole(opts =>
    {
        opts.TimestampFormat = "[HH:mm:ss] ";
        opts.SingleLine = true;
    });
    builder.AddProvider(new JsonFileLoggerProvider(fileLogStream));
});

var log = loggerFactory.CreateLogger("bot.main");

var mode = config.LiveTrading ? "LIVE" : "PAPER";
log.LogInformation(new string('=', 60));
log.LogInformation("Polymarket Bot (.NET)");
log.LogInformation("Mode: {Mode} | Bankroll: ${Bankroll:F2}", mode, config.InitialBankroll);
log.LogInformation("Min edge: {MinEdge:P0} | Max position: {MaxPos:P0}", config.MinEdge, config.MaxPositionPct);
log.LogInformation("Scan interval: {Interval} min | Markets/cycle: {Markets}",
    config.ScanIntervalMinutes, config.MarketsPerCycle);
log.LogInformation("Ensemble: {Size}x {Model}", config.EnsembleSize, config.ClaudeModel);
log.LogInformation(new string('=', 60));

if (console_)
{
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine($"  POLYMARKET BOT (.NET) — {mode} MODE");
    Console.WriteLine($"  Bankroll: ${config.InitialBankroll:F2} | Min edge: {config.MinEdge:P0}");
    Console.WriteLine($"  Risk: {config.MaxPositionPct:P0}/pos, {config.MaxTotalExposurePct:P0}/total, {config.DailyStopLossPct:P0}/daily-SL");
    Console.WriteLine($"  Scan: every {config.ScanIntervalMinutes}min, {config.MarketsPerCycle} markets/cycle");
    Console.WriteLine($"{new string('=', 60)}\n");
}

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
    Con($"RESUME: ${portfolio.Bankroll:F2} bankroll, {portfolio.Positions.Count} positions, ${portfolio.TotalExposure():F2} exposure");
}
else
{
    log.LogInformation("Starting fresh");
    Con($"START: fresh portfolio, ${portfolio.Bankroll:F2} bankroll");
    // Persist initial state immediately so portfolio.json exists from the start
    PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
}

// ── Services ────────────────────────────────────────────────────

using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

var scanner = new MarketScanner(config, httpClient, loggerFactory.CreateLogger<MarketScanner>());
var estimator = new Estimator(config, httpClient, loggerFactory.CreateLogger<Estimator>());

// ── Graceful shutdown ───────────────────────────────────────────

using var cts = new CancellationTokenSource();

ITrader trader;
if (config.LiveTrading)
{
    if (string.IsNullOrEmpty(config.PolymarketPrivateKey) && string.IsNullOrEmpty(config.PolymarketApiKey))
    {
        log.LogError("POLYMARKET_PRIVATE_KEY or POLYMARKET_API_KEY required for live trading");
        return 1;
    }
    var clobClient = new ClobApiClient(config, httpClient, loggerFactory.CreateLogger<ClobApiClient>());
    await clobClient.InitializeAsync(cts.Token);
    Con("CLOB API credentials initialized");
    var liveTrader = new LiveTrader(clobClient, loggerFactory.CreateLogger<LiveTrader>());
    trader = liveTrader;

    // Sync bankroll from actual on-chain balance
    var initBal = await clobClient.GetBalanceAsync(cts.Token);
    if (initBal is not null)
    {
        portfolio.SyncBalance(initBal.Value);
        log.LogInformation("Initial USDC balance: ${Balance:F2}", initBal.Value);
        Con($"BALANCE: ${initBal.Value:F2} (on-chain)");
    }
}
else
{
    trader = new PaperTrader();
}
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    log.LogInformation("Shutdown requested...");
    Con("SHUTDOWN requested (Ctrl+C)");
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
        Con("HALTED: portfolio risk limit reached, stopping bot");
        break;
    }

    // Daily reset
    var today = DateTimeOffset.UtcNow.Date;
    if (today != lastDailyReset)
    {
        portfolio.ResetDaily();
        lastDailyReset = today;
        log.LogInformation("New day — daily start value reset to ${Bankroll:F2}", portfolio.Bankroll);
        Con($"NEW DAY: daily PnL reset, start=${portfolio.Bankroll:F2}");
    }

    log.LogInformation("--- Cycle {Cycle} ---", cycle);
    {
        var pvLog = portfolio.Bankroll + portfolio.TotalExposure();
        log.LogInformation(
            "Portfolio: ${Value:F2} (bankroll=${Bankroll:F2} + exposure=${Exposure:F2}) | {Positions} positions",
            pvLog, portfolio.Bankroll, portfolio.TotalExposure(), portfolio.Positions.Count);
    }

    if (console_)
    {
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        Console.WriteLine($"\n{new string('\u2500', 60)}");
        Console.WriteLine($"[{Ts()}] CYCLE {cycle}");
        Console.WriteLine($"  Portfolio: ${pv:F2} (bankroll=${portfolio.Bankroll:F2} + exposure=${portfolio.TotalExposure():F2})");
        Console.WriteLine($"  Positions: {portfolio.Positions.Count} | API cost: ${portfolio.TotalApiCost:F4}");
        Console.WriteLine(new string('\u2500', 60));
    }

    // ── Position review phase ─────────────────────────────────
    if (config.EnablePositionReview && portfolio.Positions.Count > 0)
    {
        log.LogInformation("Reviewing {Count} open positions...", portfolio.Positions.Count);
        Con($"REVIEW: checking {portfolio.Positions.Count} positions...");

        var tokenIds = portfolio.Positions.Select(p => p.TokenId).ToList();
        var prices = await scanner.GetMarketPricesAsync(tokenIds, cts.Token);
        portfolio.UpdatePositionPrices(prices);

        var pennyCount = portfolio.Positions.Count(p => p.CurrentPrice < 0.01);
        var exitSignals = portfolio.GenerateExitSignals();
        var exitsThisCycle = 0;

        if (pennyCount > 0)
        {
            log.LogInformation("  Skipping {Count} penny positions (price < $0.01, unsellable)", pennyCount);
            Con($"  SKIP: {pennyCount} penny positions (price < $0.01)");
        }

        if (exitSignals.Count > 0)
        {
            log.LogInformation("  Found {Count} exit signals", exitSignals.Count);
            Con($"  Found {exitSignals.Count} exit signal(s)");
        }
        else
        {
            log.LogInformation("  No exit signals — all positions OK");
            Con("  All positions OK, no exits needed");
        }

        foreach (var es in exitSignals)
        {
            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            log.LogInformation(
                "  EXIT {Reason}: {Question} entry={Entry:F4} -> {Current:F4} (PnL={Pnl:+0.0%;-0.0%})",
                es.ExitReason, Truncate(es.Position.Question, 50), es.Position.EntryPrice, es.CurrentPrice, es.PnlPct);
            if (console_)
            {
                Con($"  EXIT ({es.ExitReason}): {Truncate(es.Position.Question, 50)}...");
                Con($"    {es.Position.EntryPrice:F4} -> {es.CurrentPrice:F4} PnL={es.PnlPct:+0.0%;-0.0%}");
            }

            var sellTrade = await trader.ExecuteSellAsync(es, portfolio, cts.Token);
            if (sellTrade is not null)
            {
                PersistenceService.AppendTrade(sellTrade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                exitsThisCycle++;
                Con($"    SOLD OK");
            }
            else
            {
                Con($"    SELL FAILED (min 5 tokens or order not filled)");
            }
        }

        Con($"REVIEW: {exitsThisCycle} exits, bankroll=${portfolio.Bankroll:F2}, {portfolio.Positions.Count} positions remaining");
    }

    try
    {
        log.LogInformation("Scanning markets...");
        Con("SCAN: fetching markets...");
        var markets = await scanner.ScanAsync(cts.Token);
        var eligible = markets.Take(config.MarketsPerCycle).ToList();
        var tradesThisCycle = 0;

        Con($"SCAN: {markets.Count} total, evaluating top {eligible.Count}");

        // Pre-check: skip estimation entirely if exposure is at the limit
        // Use portfolio value (bankroll + exposure) as base, not just bankroll
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        var exposureRoom = config.MaxTotalExposurePct * pv - portfolio.TotalExposure();
        var minRealisticPosition = config.MaxPositionPct * pv * 0.5;
        // Also can't trade more than available cash
        exposureRoom = Math.Min(exposureRoom, portfolio.Bankroll);
        var atCapacity = exposureRoom < minRealisticPosition;
        if (atCapacity)
        {
            log.LogInformation(
                "Exposure near limit: room=${Room:F2} < min realistic position=${MinPos:F2} — skipping estimation to save API costs",
                exposureRoom, minRealisticPosition);
            Con($"EXPOSURE FULL: room=${exposureRoom:F2} < ${minRealisticPosition:F2}, skipping evaluations");
        }

        for (var i = 0; i < eligible.Count; i++)
        {
            var market = eligible[i];
            var idx = $"[{i + 1,2}/{eligible.Count}]";

            if (cts.Token.IsCancellationRequested || portfolio.IsHalted)
                break;

            if (portfolio.HasPosition(market.ConditionId))
            {
                log.LogInformation("  {Idx} SKIP (already held): {Question}", idx, Truncate(market.Question, 60));
                Con($"  {idx} SKIP (held): {Truncate(market.Question, 55)}");
                continue;
            }

            // Skip estimation entirely if at exposure limit (saves API costs)
            if (atCapacity)
                continue;

            // Estimate fair value
            log.LogInformation("  {Idx} Evaluating: {Question}...", idx, Truncate(market.Question, 60));
            Con($"  {idx} EVAL: {Truncate(market.Question, 55)}...");
            var estimate = await estimator.EstimateAsync(market, cts.Token);
            if (estimate is null)
            {
                log.LogInformation("  {Idx} SKIP (estimation failed)", idx);
                Con($"  {idx} -> FAILED");
                continue;
            }

            // Agent pays for inference
            portfolio.RecordApiCost(estimate.InputTokensUsed, estimate.OutputTokensUsed);

            if (portfolio.Bankroll <= 0)
            {
                log.LogWarning("Bankroll depleted by API costs — agent is dead");
                Con("DEAD: bankroll depleted by API costs");
                portfolio.IsHalted = true;
                break;
            }

            // Generate signal
            var signal = portfolio.GenerateSignal(market, estimate);
            if (signal is null)
            {
                var yesEdge = estimate.FairProbability - market.OutcomeYesPrice;
                var noEdge = (1.0 - estimate.FairProbability) - market.OutcomeNoPrice;
                var bestEdge = Math.Max(yesEdge, noEdge);
                log.LogInformation(
                    "  {Idx} SKIP (no edge): fair={Fair:P1} vs market={Market:P1} (edge={Edge:+0.0%;-0.0%}, need>{Min:P0})",
                    idx, estimate.FairProbability, market.OutcomeYesPrice, bestEdge, config.MinEdge);
                Con($"  {idx} -> {estimate.FairProbability:P0} (edge={bestEdge:+0.0%;-0.0%}) SKIP");
                continue;
            }

            // Risk check
            if (!portfolio.CheckRisk(signal))
            {
                log.LogInformation(
                    "  {Idx} SKIP (risk limit): {Side} {Question} ${Size:F2}",
                    idx, signal.Side, Truncate(market.Question, 40), signal.PositionSizeUsd);
                Con($"  {idx} -> {estimate.FairProbability:P0} RISK BLOCKED");
                continue;
            }

            // Execute
            log.LogInformation(
                "  {Idx} >>> BUYING {Side} {Question} ${Size:F2} @ {Price:F3}",
                idx, signal.Side, Truncate(market.Question, 50), signal.PositionSizeUsd, signal.MarketPrice);
            if (console_)
            {
                Con($"  {idx} -> {estimate.FairProbability:P0} edge={signal.Edge:P1}");
                Con($"  {idx} >>> BUY {signal.Side} ${signal.PositionSizeUsd:F2} @ {signal.MarketPrice:F3}...");
            }

            var trade = await trader.ExecuteAsync(signal, portfolio, cts.Token);
            if (trade is not null)
            {
                // Log on-chain USDC balance after trade (diagnostic only;
                // internal bookkeeping is authoritative when positions are open)
                if (trader is LiveTrader lt)
                {
                    var bal = await lt.GetBalanceAsync(cts.Token);
                    if (bal is not null)
                    {
                        log.LogInformation("On-chain USDC after trade: ${Balance:F2}", bal.Value);
                        Con($"  USDC balance: ${bal.Value:F2}");
                    }
                }

                PersistenceService.AppendTrade(trade, config.DataDir);
                PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
                tradesThisCycle++;

                log.LogInformation(
                    "  {Idx} TRADE OK: {Side} {Question} ${Size:F2} @ {Price:F3} (edge={Edge:P1}, EV=${EV:F2})",
                    idx, trade.Side, Truncate(market.Question, 50), trade.SizeUsd, trade.Price,
                    signal.Edge, signal.ExpectedValue);

                Con($"  {idx} TRADE OK (EV=${signal.ExpectedValue:F2})");
            }
            else
            {
                log.LogWarning("  {Idx} TRADE FAILED: order execution error", idx);
                Con($"  {idx} TRADE FAILED");
            }
        }

        // Cycle summary
        log.LogInformation(
            "Cycle {Cycle}: {Trades} trades | Bankroll: ${Bankroll:F2} | Positions: {Positions} | " +
            "Exposure: ${Exposure:F2} | API cost: ${ApiCost:F4} | Realized PnL: ${PnL:+0.00;-0.00}",
            cycle, tradesThisCycle, portfolio.Bankroll, portfolio.Positions.Count,
            portfolio.TotalExposure(), portfolio.TotalApiCost, portfolio.TotalRealizedPnl);

        if (console_)
        {
            var pvSummary = portfolio.Bankroll + portfolio.TotalExposure();
            Console.WriteLine($"\n[{Ts()}] SUMMARY: {tradesThisCycle} trades this cycle");
            Console.WriteLine($"  Portfolio: ${pvSummary:F2} | Bankroll: ${portfolio.Bankroll:F2} | Exposure: ${portfolio.TotalExposure():F2}");
            Console.WriteLine($"  Positions: {portfolio.Positions.Count} | API cost: ${portfolio.TotalApiCost:F4} | PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}");
        }

        PersistenceService.SaveSnapshot(portfolio.Snapshot(), config.DataDir);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Cycle {Cycle} error", cycle);
        Con($"ERROR: {ex.Message}");
    }

    // Sleep in 1-second ticks for responsive shutdown
    if (!cts.Token.IsCancellationRequested)
    {
        log.LogInformation("Next scan in {Interval} min", config.ScanIntervalMinutes);
        Con($"WAIT: sleeping {config.ScanIntervalMinutes} min...");
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

if (console_)
{
    var pv = portfolio.Bankroll + portfolio.TotalExposure();
    Console.WriteLine($"\n{new string('=', 60)}");
    Console.WriteLine($"[{Ts()}] BOT STOPPED");
    Console.WriteLine($"  Final portfolio: ${pv:F2} | Bankroll: ${portfolio.Bankroll:F2}");
    Console.WriteLine($"  Total trades: {portfolio.TotalTrades} | API cost: ${portfolio.TotalApiCost:F4}");
    Console.WriteLine($"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}");
    Console.WriteLine(new string('=', 60));
}

return 0;

static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
