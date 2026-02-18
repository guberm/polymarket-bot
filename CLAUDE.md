# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Polymarket trading bot that uses Claude ensemble estimation to find and trade mispriced binary prediction markets. Every cycle it scans markets, estimates fair probabilities via multiple Claude calls (trimmed mean), finds mispricing > 8%, sizes positions with fractional Kelly criterion, and executes. The agent pays for its own API inference from its bankroll — if bankroll hits $0, it stops.

Two implementations: **Python** (`python/`) and **.NET 8** (`dotnet/PolymarketBot/`). Both share the same logic, config, and data formats.

## Running

### Config file (primary)

All settings live in **`polymarket_bot_config.json`** at the project root (not tracked by git — contains secrets). Copy from the example or create manually:

```json
{
  "anthropic_api_key": "sk-ant-...",
  "anthropic_api_host": "https://api.anthropic.com",
  "gamma_api_host": "https://gamma-api.polymarket.com",
  "clob_host": "https://clob.polymarket.com",
  "live_trading": false,
  "initial_bankroll": 10000
}
```

Config priority (highest wins): **CLI arg → env var → polymarket_bot_config.json → code default**

### Python

```bash
cd python
pip install -r requirements.txt
python main.py           # paper trading
python main.py --verbose # debug logging
python main.py --console # human-readable console output
```

Python 3.10+ required. Dependencies: `requests`, `anthropic`, `py-clob-client` (live trading only).

### .NET

```bash
cd dotnet/PolymarketBot
dotnet run               # paper trading
dotnet run -- --verbose  # debug logging
dotnet run -- --console  # human-readable console output
```

.NET 8 required. NuGet packages: `Microsoft.Extensions.Logging`, `Nethereum.Signer` (EIP-712/ECDSA for live trading).

### Windows

Double-click `run-bot.bat` — reads `polymarket_bot_config.json` automatically.

### CLI risk overrides

```bash
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Available: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`, `--console`.

No test suite or linter configured.

## Architecture

### Python (`python/`)

```text
python/
  main.py            – Orchestration loop: scan → estimate → signal → risk check → execute → save
  config.py          – BotConfig dataclass; reads polymarket_bot_config.json then env vars
  notifier.py        – Email notifications (smtplib); all events: trade, sell, resolved, halted, etc.
  models.py          – Domain dataclasses: MarketInfo, Estimate, Signal, Position, Trade, ExitSignal, TopupCandidate, PortfolioSnapshot
  market_scanner.py  – MarketScanner: Gamma API pagination, market parsing/filtering, CLOB price quotes, batch price fetch
  estimator.py       – Estimator: N independent Claude calls per market, trimmed mean, JSON parsing
  portfolio.py       – Portfolio: bankroll, positions, Kelly sizing, risk limits, position review & exit signals, API cost tracking
  trader.py          – PaperTrader (simulated) + LiveTrader (py-clob-client GTC orders, buy & sell via CLOB)
  persistence.py     – JSON save/load for PortfolioSnapshot + JSONL append for trade log
  logger_setup.py    – Dual logging: colored console + JSON lines file (data/bot.log)
  requirements.txt   – Python dependencies
```

### .NET (`dotnet/PolymarketBot/`)

```text
dotnet/PolymarketBot/
  Program.cs               – Async orchestration loop with CancellationToken
  BotConfig.cs             – Config from polymarket_bot_config.json then env vars (same keys as Python)
  Models/                  – Enums, MarketInfo, Estimate, Signal, Position, Trade, TopupCandidate, PortfolioSnapshot
  Services/
    MarketScanner.cs       – Gamma API with HttpClient, pagination, filtering, batch price fetch
    Estimator.cs           – Claude ensemble via Anthropic REST API (HttpClient)
    Portfolio.cs           – Kelly sizing, 5-layer risk, position review & exit signals, API cost tracking
    Notifier.cs            – Email notifications (System.Net.Mail); mirrors python/notifier.py
    ClobApiClient.cs       – CLOB API auth (EIP-712 signing, HMAC, API key derivation), buy & sell orders
    ITrader.cs             – Trader interface (ExecuteAsync + ExecuteSellAsync + ExecuteTopupAndSellAsync)
    PaperTrader.cs         – Simulated execution (buy & sell)
    LiveTrader.cs          – Live CLOB API execution with GTC polling (buy & sell)
    PersistenceService.cs  – Atomic JSON save + JSONL trade log (System.Text.Json)
    JsonFileLoggerProvider.cs – JSON lines file logger (matches Python's JsonFormatter)
```

**Data flow per cycle (both implementations):**

1. **Balance sync** — fetch on-chain USDC every cycle, sync bankroll in both directions (up = resolved positions returned USDC; down = fees/failed orders)
2. **Position review** — fetch midpoint prices, check exit rules (stop-loss/take-profit/edge-gone), execute SELLs, top-up-and-sell tiny positions
3. `MarketScanner.Scan()` → list of `MarketInfo` (filtered by liquidity, volume, time-to-resolution)
4. `Estimator.Estimate()` → `Estimate` per market (ensemble of N Claude calls, trimmed mean)
5. `Portfolio.GenerateSignal()` → `Signal` when edge > `min_edge` (8% default)
6. `Portfolio.CheckRisk()` → validates all risk limits before execution
7. `PaperTrader/LiveTrader.Execute()` → `Trade` record + `Position` opened
8. `Persistence` → save snapshot + append trade to log

**External APIs:**
- Gamma API (`gamma-api.polymarket.com/events`) — market discovery with pagination
- CLOB API (`clob.polymarket.com`) — price quotes + live order execution
- Anthropic API — Claude ensemble estimation (agent pays from bankroll)

## Key Design Decisions

- **Binary markets only** — filters out non-binary outcomes in market parsing
- **Ensemble estimation** — N independent Claude calls at temperature 0.7, trimmed mean reduces variance
- **Estimator prompt** asks Claude to output `{"probability": 0.XX, "reasoning": "..."}` — does NOT show current market price to avoid anchoring
- **Keyword-based categorization** (`CATEGORY_KEYWORDS` in scanner) — used for per-category exposure limits
- **Gamma API returns JSON-encoded strings** inside JSON for `outcomes`, `outcomePrices`, and `clobTokenIds` — parsing handles both string and list forms
- **Risk is layered:** per-position (15%), per-category (50%), total exposure (90%), daily stop-loss (20%), max drawdown (50%)
- **Portfolio value** for stop-loss/drawdown = bankroll + total open position value (deployed capital isn't a loss)
- **Config file** `polymarket_bot_config.json` at project root (not tracked by git). Loaded by both Python (`Path(__file__).parent.parent / "polymarket_bot_config.json"`) and .NET (`../../polymarket_bot_config.json` relative to CWD). `CONFIG_FILE` env var overrides path. Priority: CLI arg → env var → config file → code default
- **Email notifications** — `Notifier` class (python/notifier.py, dotnet/.../Notifier.cs) sends emails on: started, trade, sell, topup+sell, resolved, halted, daily_reset, error, stopped. Errors silently swallowed — email failure never crashes bot. Use STARTTLS (port 587) or SMTP_SSL (port 465) based on `email_use_tls`
- **CLI args** override env vars/config for risk params: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`
- **Agent pays for inference** — API token costs are deducted from bankroll each cycle
- **Atomic persistence** — portfolio.json written via tmp+rename to avoid corruption on crash
- **Polygon chain** (chain ID 137) for Polymarket settlement
- **Live trading** uses GTC (Good-Till-Cancelled) limit orders. BUY price = midpoint + 2 tick sizes (crosses the spread for immediate taker fills). Poll 5×3s = 15s for MATCHED status, cancel if unfilled
- **Position review** each cycle: stop-loss (>30% drop), take-profit (price≥0.95), edge-gone (market past fair estimate). Penny positions (<$0.01) skipped — unsellable on CLOB
- **Top-up-and-sell** for tiny positions (<5 tokens) with exit signals: BUY 5 tokens (CLOB minimum) then SELL all. If BUY fills but SELL doesn't, position becomes sellable next cycle. Skipped if bankroll < topup cost
- **SELL orders** use Side=1, makerAmount=tokens, takerAmount=USDC (reversed from BUY). Minimum 5 tokens enforced
- **Agent survival**: estimation loop stops when bankroll < $0.30 (API reserve guard) — bot keeps running for position review. Agent truly "dead" only when `bankroll + TotalExposure() < $1`. `IsHalted` is auto-cleared on restart if portfolio value is healthy (transient low-USDC halts don't persist)
- **.NET version** uses direct HttpClient calls to Anthropic REST API (no SDK dependency)
- **.NET CLOB auth** implements EIP-712 signing (ClobAuth struct for L1, Order struct for orders) + HMAC-SHA256 for L2, using Nethereum.Signer for Keccak/ECDSA
- **No hardcoded URLs or contract addresses** — all endpoints/contracts come from env vars
