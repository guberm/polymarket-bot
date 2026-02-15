# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Polymarket trading bot that uses Claude ensemble estimation to find and trade mispriced binary prediction markets. Every cycle it scans markets, estimates fair probabilities via multiple Claude calls (trimmed mean), finds mispricing > 8%, sizes positions with fractional Kelly criterion, and executes. The agent pays for its own API inference from its bankroll — if bankroll hits $0, it stops.

Two implementations: **Python** (`python/`) and **.NET 8** (`dotnet/PolymarketBot/`). Both share the same logic, config env vars, and data formats.

## Running

### Python

```bash
cd python
pip install -r requirements.txt

# Paper trading (default)
ANTHROPIC_API_KEY=sk-... python main.py

# Verbose mode
ANTHROPIC_API_KEY=sk-... python main.py --verbose

# Live trading (requires funded Polymarket wallet)
ANTHROPIC_API_KEY=sk-... POLYMARKET_PRIVATE_KEY=0x... POLYMARKET_FUNDER_ADDRESS=0x... POLYMARKET_SIGNATURE_TYPE=1 LIVE_TRADING=true python main.py

# CLI risk overrides
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Python 3.10+ required. Dependencies: `requests`, `anthropic`, `py-clob-client` (live trading only).

### .NET

```bash
cd dotnet/PolymarketBot

# Paper trading (default)
ANTHROPIC_API_KEY=sk-... dotnet run

# Verbose mode
ANTHROPIC_API_KEY=sk-... dotnet run -- --verbose

# Live trading
ANTHROPIC_API_KEY=sk-... POLYMARKET_PRIVATE_KEY=0x... POLYMARKET_FUNDER_ADDRESS=0x... POLYMARKET_SIGNATURE_TYPE=1 LIVE_TRADING=true dotnet run

# CLI risk overrides
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

.NET 8 required. No external NuGet packages beyond `Microsoft.Extensions.Logging`.

**All config via env vars** — see `BotConfig.from_env()` in `python/config.py` or `BotConfig.FromEnv()` in `dotnet/PolymarketBot/BotConfig.cs`. Key ones:
- `ANTHROPIC_API_KEY` (required)
- `POLYMARKET_PRIVATE_KEY`, `POLYMARKET_FUNDER_ADDRESS` (live trading)
- `POLYMARKET_SIGNATURE_TYPE` (0=EOA, 1=GNOSIS_SAFE; default: 0)
- `LIVE_TRADING=true` (default: false/paper)
- `INITIAL_BANKROLL` (default: 10000)
- `MIN_EDGE` (default: 0.05 = 5%)
- `SCAN_INTERVAL_MINUTES` (default: 10)

No test suite or linter configured.

## Architecture

### Python (`python/`)

```
python/
  main.py            – Orchestration loop: scan → estimate → signal → risk check → execute → save
  config.py          – BotConfig dataclass, all params from env vars
  models.py          – Domain dataclasses: MarketInfo, Estimate, Signal, Position, Trade, PortfolioSnapshot
  market_scanner.py  – MarketScanner: Gamma API pagination, market parsing/filtering, CLOB price quotes
  estimator.py       – Estimator: N independent Claude calls per market, trimmed mean, JSON parsing
  portfolio.py       – Portfolio: bankroll, positions, Kelly sizing, risk limits, API cost tracking
  trader.py          – PaperTrader (simulated) + LiveTrader (py-clob-client market orders via CLOB)
  persistence.py     – JSON save/load for PortfolioSnapshot + JSONL append for trade log
  logger_setup.py    – Dual logging: colored console + JSON lines file (data/bot.log)
  requirements.txt   – Python dependencies
```

### .NET (`dotnet/PolymarketBot/`)

```
dotnet/PolymarketBot/
  Program.cs               – Async orchestration loop with CancellationToken
  BotConfig.cs             – Config from env vars (same vars as Python)
  Models/                  – Enums, MarketInfo, Estimate, Signal, Position, Trade, PortfolioSnapshot
  Services/
    MarketScanner.cs       – Gamma API with HttpClient, pagination, filtering
    Estimator.cs           – Claude ensemble via Anthropic REST API (HttpClient)
    Portfolio.cs           – Kelly sizing, 5-layer risk, API cost tracking
    ITrader.cs             – Trader interface
    PaperTrader.cs         – Simulated execution
    LiveTrader.cs          – CLOB API execution
    PersistenceService.cs  – Atomic JSON save + JSONL trade log (System.Text.Json)
```

**Data flow per cycle (both implementations):**
1. `MarketScanner.Scan()` → list of `MarketInfo` (filtered by liquidity, volume, time-to-resolution)
2. `Estimator.Estimate()` → `Estimate` per market (ensemble of N Claude calls, trimmed mean)
3. `Portfolio.GenerateSignal()` → `Signal` when edge > `min_edge` (8% default)
4. `Portfolio.CheckRisk()` → validates all risk limits before execution
5. `PaperTrader/LiveTrader.Execute()` → `Trade` record + `Position` opened
6. `Persistence` → save snapshot + append trade to log

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
- **CLI args** override env vars for risk params: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`
- **Agent pays for inference** — API token costs are deducted from bankroll each cycle
- **Atomic persistence** — portfolio.json written via tmp+rename to avoid corruption on crash
- **Polygon chain** (chain ID 137) for Polymarket settlement
- **Live trading** uses FOK (Fill or Kill) market orders
- **.NET version** uses direct HttpClient calls to Anthropic REST API (no SDK dependency)
