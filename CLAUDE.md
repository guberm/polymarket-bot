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

.NET 8 required. NuGet packages: `Microsoft.Extensions.Logging`, `Nethereum.Signer` (EIP-712/ECDSA for live trading).

**All config via env vars** — see `BotConfig.from_env()` in `python/config.py` or `BotConfig.FromEnv()` in `dotnet/PolymarketBot/BotConfig.cs`. Key ones:
- `ANTHROPIC_API_KEY` (required)
- `POLYMARKET_PRIVATE_KEY`, `POLYMARKET_FUNDER_ADDRESS` (live trading)
- `POLYMARKET_SIGNATURE_TYPE` (0=EOA, 1=GNOSIS_SAFE; default: 0)
- `LIVE_TRADING=true` (default: false/paper)
- `INITIAL_BANKROLL` (default: 10000)
- `MIN_EDGE` (default: 0.08 = 8%)
- `SCAN_INTERVAL_MINUTES` (default: 10)
- `ENABLE_POSITION_REVIEW` (default: true) — review & exit positions each cycle
- `POSITION_STOP_LOSS_PCT` (default: 0.30), `TAKE_PROFIT_PRICE` (default: 0.95), `EXIT_EDGE_BUFFER` (default: 0.05)
- `ANTHROPIC_API_HOST`, `GAMMA_API_HOST`, `CLOB_HOST` (API base URLs, required)
- `EXCHANGE_ADDRESS`, `NEG_RISK_EXCHANGE_ADDRESS` (contract addresses, required for live trading)

No test suite or linter configured.

## Architecture

### Python (`python/`)

```
python/
  main.py            – Orchestration loop: scan → estimate → signal → risk check → execute → save
  config.py          – BotConfig dataclass, all params from env vars
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

```
dotnet/PolymarketBot/
  Program.cs               – Async orchestration loop with CancellationToken
  BotConfig.cs             – Config from env vars (same vars as Python)
  Models/                  – Enums, MarketInfo, Estimate, Signal, Position, Trade, TopupCandidate, PortfolioSnapshot
  Services/
    MarketScanner.cs       – Gamma API with HttpClient, pagination, filtering, batch price fetch
    Estimator.cs           – Claude ensemble via Anthropic REST API (HttpClient)
    Portfolio.cs           – Kelly sizing, 5-layer risk, position review & exit signals, API cost tracking
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
- **CLI args** override env vars for risk params: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`
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
