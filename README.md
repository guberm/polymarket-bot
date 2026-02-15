# Polymarket Trading Bot

Autonomous trading agent for [Polymarket](https://polymarket.com) prediction markets. Scans hundreds of binary markets, estimates fair probabilities using a Claude ensemble, finds mispricing, and executes trades with Kelly criterion sizing.

**The agent pays for its own inference.** Claude API costs are deducted from the bankroll each cycle. If the bankroll hits $0, the agent dies.

## How It Works

```
Every 10 minutes:
  1. Scan all active Polymarket markets (Gamma API)
  2. Filter by liquidity, volume, and time to resolution
  3. Estimate fair probability for each market (N independent Claude calls → trimmed mean)
  4. Find mispricing > 8% between estimate and market price
  5. Size position using fractional Kelly criterion (max 6% of bankroll)
  6. Check risk limits (per-position, per-category, total exposure, drawdown)
  7. Execute trade (paper or live)
  8. Deduct API costs from bankroll
  9. Save state and repeat
```

## Quick Start

```bash
# Clone
git clone https://github.com/guberm/polymarket-bot.git
cd polymarket-bot

# Install dependencies
pip install -r requirements.txt

# Run in paper trading mode (no real money)
ANTHROPIC_API_KEY=sk-... python main.py

# Verbose logging
ANTHROPIC_API_KEY=sk-... python main.py --verbose
```

## Live Trading

> **Warning:** Live trading uses real money. Start with paper trading to validate signals.

```bash
ANTHROPIC_API_KEY=sk-... \
POLYMARKET_PRIVATE_KEY=0x... \
POLYMARKET_FUNDER_ADDRESS=0x... \
LIVE_TRADING=true \
python main.py
```

Requires a funded Polymarket wallet on Polygon (chain ID 137) and `py-clob-client`.

## Configuration

All parameters are set via environment variables. Defaults are tuned for conservative trading.

| Variable | Default | Description |
|----------|---------|-------------|
| `ANTHROPIC_API_KEY` | — | Required. Claude API key |
| `LIVE_TRADING` | `false` | Set `true` for real orders |
| `INITIAL_BANKROLL` | `10000` | Starting capital in USD |
| `MIN_EDGE` | `0.08` | Minimum mispricing to trade (8%) |
| `SCAN_INTERVAL_MINUTES` | `10` | Time between scan cycles |
| `MARKETS_PER_CYCLE` | `30` | Max markets to evaluate per cycle |
| `ENSEMBLE_SIZE` | `5` | Claude calls per market estimate |
| `ENSEMBLE_TEMPERATURE` | `0.7` | Temperature for ensemble diversity |
| `CLAUDE_MODEL` | `claude-sonnet-4-20250514` | Model for estimation |
| `KELLY_FRACTION` | `0.25` | Fractional Kelly (0.25 = quarter Kelly) |
| `MAX_POSITION_PCT` | `0.06` | Max 6% of bankroll per position |
| `MAX_TOTAL_EXPOSURE_PCT` | `0.50` | Max 50% of bankroll in open positions |
| `MAX_CATEGORY_EXPOSURE_PCT` | `0.20` | Max 20% per category |
| `DAILY_STOP_LOSS_PCT` | `0.03` | Halt if daily loss exceeds 3% |
| `MAX_DRAWDOWN_PCT` | `0.15` | Halt if drawdown exceeds 15% |
| `MAX_CONCURRENT_POSITIONS` | `20` | Max open positions |
| `POLYMARKET_PRIVATE_KEY` | — | Wallet private key (live trading) |
| `POLYMARKET_FUNDER_ADDRESS` | — | Funder address (live trading) |

## Architecture

```
main.py            → Orchestration loop
config.py          → BotConfig dataclass (all env vars)
models.py          → Domain models (MarketInfo, Estimate, Signal, Position, Trade)
market_scanner.py  → Gamma API integration, market filtering
estimator.py       → Claude ensemble estimation (trimmed mean)
portfolio.py       → Kelly sizing, risk management, API cost tracking
trader.py          → PaperTrader + LiveTrader (py-clob-client)
persistence.py     → JSON state + JSONL trade log
logger_setup.py    → Colored console + JSON file logging
data/              → Runtime state (portfolio.json, trades.jsonl, bot.log)
```

### Estimation

The estimator makes N independent Claude calls per market at temperature 0.7. Each call returns a probability estimate. The trimmed mean (dropping highest and lowest when N≥4) becomes the fair value. The current market price is deliberately **not shown** to Claude to prevent anchoring.

### Risk Management

Five layers of protection:

1. **Per-position cap** — max 6% of bankroll on any single market
2. **Per-category cap** — max 20% exposure in politics, sports, crypto, etc.
3. **Total exposure cap** — max 50% of bankroll in open positions
4. **Daily stop-loss** — halt trading if daily losses exceed 3%
5. **Max drawdown** — halt if drawdown from peak exceeds 15%

### Agent Survival

API costs (Claude inference) are deducted from the bankroll every cycle. The agent must generate enough edge to cover its own operating costs. If the bankroll reaches $0 — from trading losses or API bills — `is_halted` flips to `true` and the bot stops.

## Data & Logs

- `data/portfolio.json` — Current portfolio state (atomically written)
- `data/trades.jsonl` — Append-only trade history
- `data/bot.log` — Structured JSON logs

## Disclaimer

This is experimental software. Prediction market trading carries risk. The bot can and will lose money. Past performance of paper trading does not predict live results. Do not trade with money you cannot afford to lose.

## License

MIT
