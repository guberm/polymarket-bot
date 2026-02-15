# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Polymarket trading bot that uses Claude ensemble estimation to find and trade mispriced binary prediction markets. Every cycle it scans markets, estimates fair probabilities via multiple Claude calls (trimmed mean), finds mispricing > 8%, sizes positions with fractional Kelly criterion, and executes. The agent pays for its own API inference from its bankroll — if bankroll hits $0, it stops.

## Running

```bash
pip install -r requirements.txt

# Paper trading (default)
ANTHROPIC_API_KEY=sk-... python main.py

# Verbose mode
ANTHROPIC_API_KEY=sk-... python main.py --verbose

# Live trading (requires funded Polymarket wallet)
ANTHROPIC_API_KEY=sk-... POLYMARKET_PRIVATE_KEY=0x... LIVE_TRADING=true python main.py
```

Python 3.10+ required. Dependencies: `requests`, `anthropic`, `py-clob-client` (live trading only).

**All config via env vars** — see `BotConfig.from_env()` in `config.py` for the full list. Key ones:
- `ANTHROPIC_API_KEY` (required)
- `POLYMARKET_PRIVATE_KEY`, `POLYMARKET_FUNDER_ADDRESS` (live trading)
- `LIVE_TRADING=true` (default: false/paper)
- `INITIAL_BANKROLL` (default: 10000)
- `MIN_EDGE` (default: 0.08 = 8%)
- `SCAN_INTERVAL_MINUTES` (default: 10)

No test suite or linter configured.

## Architecture

```
main.py            – Orchestration loop: scan → estimate → signal → risk check → execute → save
config.py          – BotConfig dataclass, all params from env vars
models.py          – Domain dataclasses: MarketInfo, Estimate, Signal, Position, Trade, PortfolioSnapshot
market_scanner.py  – MarketScanner: Gamma API pagination, market parsing/filtering, CLOB price quotes
estimator.py       – Estimator: N independent Claude calls per market, trimmed mean, JSON parsing
portfolio.py       – Portfolio: bankroll, positions, Kelly sizing, risk limits, API cost tracking
trader.py          – PaperTrader (simulated) + LiveTrader (py-clob-client market orders via CLOB)
persistence.py     – JSON save/load for PortfolioSnapshot + JSONL append for trade log
logger_setup.py    – Dual logging: colored console + JSON lines file (data/bot.log)
data/              – Portfolio state (portfolio.json), trade log (trades.jsonl), bot.log
```

**Data flow per cycle:**
1. `MarketScanner.scan()` → list of `MarketInfo` (filtered by liquidity, volume, time-to-resolution)
2. `Estimator.estimate()` → `Estimate` per market (ensemble of N Claude calls, trimmed mean)
3. `Portfolio.generate_signal()` → `Signal` when edge > `min_edge` (8% default)
4. `Portfolio.check_risk()` → validates all risk limits before execution
5. `PaperTrader/LiveTrader.execute()` → `Trade` record + `Position` opened
6. `persistence` → save snapshot + append trade to log

**External APIs:**
- Gamma API (`gamma-api.polymarket.com/events`) — market discovery with pagination
- CLOB API (`clob.polymarket.com`) — price quotes + live order execution via py-clob-client
- Anthropic API — Claude ensemble estimation (agent pays from bankroll)

## Key Design Decisions

- **Binary markets only** — filters out non-binary outcomes in `_parse_market`
- **Ensemble estimation** — N independent Claude calls at temperature 0.7, trimmed mean reduces variance
- **Estimator prompt** asks Claude to output `{"probability": 0.XX, "reasoning": "..."}` — does NOT show current market price to avoid anchoring
- **Keyword-based categorization** (`CATEGORY_KEYWORDS` in `market_scanner.py`) — used for per-category exposure limits
- **Gamma API returns JSON-encoded strings** inside JSON for `outcomes`, `outcomePrices`, and `clobTokenIds` — parsing handles both string and list forms
- **Risk is layered:** per-position (6%), per-category (20%), total exposure (50%), daily stop-loss (3%), max drawdown (15%)
- **Agent pays for inference** — API token costs are deducted from bankroll each cycle
- **Atomic persistence** — portfolio.json written via tmp+rename to avoid corruption on crash
- **Polygon chain** (chain ID 137) for Polymarket settlement
- **Live trading** uses FOK (Fill or Kill) market orders via py-clob-client
