# Polymarket Trading Bot

Autonomous trading agent for [Polymarket](https://polymarket.com) prediction markets. Scans hundreds of binary markets, estimates fair probabilities using a Claude ensemble, finds mispricing, and executes trades with Kelly criterion sizing.

Available in **Python** and **.NET 8** — both implementations share the same logic, config, and data formats.

**The agent pays for its own inference.** Claude API costs are deducted from the bankroll each cycle. If the bankroll hits $0, the agent dies.

## How It Works

```
Every 10 minutes:
  1. Scan all active Polymarket markets (Gamma API)
  2. Review existing positions — fetch current prices, check exit rules
     - Stop-loss: sell if position dropped > 30%
     - Take-profit: sell if price reached 0.95+
     - Edge-gone: sell if market moved past our original fair estimate
     - Skip penny positions (price < $0.01, unsellable on CLOB)
     - Top-up tiny positions (<5 tokens) that need exit: buy 5 more, then sell all
  3. Filter new markets by liquidity, volume, and time to resolution
  4. Estimate fair probability for each market (N independent Claude calls → trimmed mean)
  5. Find mispricing > 8% between estimate and market price
  6. Size position using fractional Kelly criterion (max 15% of bankroll)
  7. Check risk limits (per-position, per-category, total exposure, drawdown)
  8. Execute trade (paper or live via CLOB GTC orders)
  9. Deduct API costs from bankroll
  10. Save state and repeat
```

## Quick Start

### Python

```bash
git clone https://github.com/guberm/polymarket-bot.git
cd polymarket-bot/python

pip install -r requirements.txt

# Paper trading (default)
ANTHROPIC_API_KEY=sk-... python main.py

# Verbose logging
ANTHROPIC_API_KEY=sk-... python main.py --verbose
```

### .NET

```bash
git clone https://github.com/guberm/polymarket-bot.git
cd polymarket-bot/dotnet/PolymarketBot

# Paper trading (default)
ANTHROPIC_API_KEY=sk-... dotnet run

# Verbose logging
ANTHROPIC_API_KEY=sk-... dotnet run -- --verbose
```

## Live Trading

> **Warning:** Live trading uses real money. Start with paper trading to validate signals.

### Python
```bash
cd python
ANTHROPIC_API_KEY=sk-... \
ANTHROPIC_API_HOST=https://api.anthropic.com \
POLYMARKET_PRIVATE_KEY=0x... \
POLYMARKET_FUNDER_ADDRESS=0x... \
GAMMA_API_HOST=https://gamma-api.polymarket.com \
CLOB_HOST=https://clob.polymarket.com \
EXCHANGE_ADDRESS=0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E \
NEG_RISK_EXCHANGE_ADDRESS=0xC5d563A36AE78145C45a50134d48A1215220f80a \
LIVE_TRADING=true \
python main.py
```

### .NET
```bash
cd dotnet/PolymarketBot
ANTHROPIC_API_KEY=sk-... \
ANTHROPIC_API_HOST=https://api.anthropic.com \
POLYMARKET_PRIVATE_KEY=0x... \
POLYMARKET_FUNDER_ADDRESS=0x... \
GAMMA_API_HOST=https://gamma-api.polymarket.com \
CLOB_HOST=https://clob.polymarket.com \
EXCHANGE_ADDRESS=0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E \
NEG_RISK_EXCHANGE_ADDRESS=0xC5d563A36AE78145C45a50134d48A1215220f80a \
LIVE_TRADING=true \
dotnet run
```

Requires a funded Polymarket wallet on Polygon (chain ID 137). For Gnosis Safe wallets, set `POLYMARKET_SIGNATURE_TYPE=1`.

## CLI Arguments

Risk parameters can also be passed as command-line arguments (overriding env vars):

```bash
# Python
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20

# .NET
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Available CLI args: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`.

## Configuration

All parameters are set via environment variables. Both implementations use the same env vars.

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
| `MAX_POSITION_PCT` | `0.15` | Max 15% of bankroll per position |
| `MAX_TOTAL_EXPOSURE_PCT` | `0.90` | Max 90% of bankroll in open positions |
| `MAX_CATEGORY_EXPOSURE_PCT` | `0.50` | Max 50% per category |
| `DAILY_STOP_LOSS_PCT` | `0.20` | Halt if daily loss exceeds 20% |
| `MAX_DRAWDOWN_PCT` | `0.50` | Halt if drawdown exceeds 50% |
| `MAX_CONCURRENT_POSITIONS` | `20` | Max open positions |
| `MIN_TRADE_USD` | `1.0` | Minimum trade size in USD |
| `ENABLE_POSITION_REVIEW` | `true` | Review positions for exits each cycle |
| `POSITION_STOP_LOSS_PCT` | `0.30` | Sell if position drops > 30% |
| `TAKE_PROFIT_PRICE` | `0.95` | Sell if price reaches 0.95+ |
| `EXIT_EDGE_BUFFER` | `0.05` | Buffer before edge-gone exit triggers |
| `POLYMARKET_PRIVATE_KEY` | — | Wallet private key (live trading) |
| `POLYMARKET_FUNDER_ADDRESS` | — | Funder address (live trading) |
| `POLYMARKET_SIGNATURE_TYPE` | `0` | Signature type (0=EOA, 1=GNOSIS_SAFE) |
| `ANTHROPIC_API_HOST` | — | Anthropic API base URL |
| `GAMMA_API_HOST` | — | Gamma API base URL (market discovery) |
| `CLOB_HOST` | — | CLOB API base URL (price quotes, orders) |
| `EXCHANGE_ADDRESS` | — | CTF Exchange contract address |
| `NEG_RISK_EXCHANGE_ADDRESS` | — | Neg Risk CTF Exchange contract address |

## Project Structure

```
python/                        ← Python implementation
  main.py                        Orchestration loop
  config.py                      BotConfig dataclass (all env vars)
  models.py                      Domain models (MarketInfo, Estimate, Signal, Position, Trade, ExitSignal, TopupCandidate)
  market_scanner.py              Gamma API integration, market filtering, batch price fetch
  estimator.py                   Claude ensemble estimation (trimmed mean)
  portfolio.py                   Kelly sizing, risk management, position review, API cost tracking
  trader.py                      PaperTrader + LiveTrader (buy & sell via py-clob-client)
  persistence.py                 JSON state + JSONL trade log
  logger_setup.py                Colored console + JSON file logging
  requirements.txt               Python dependencies

dotnet/PolymarketBot/          ← .NET 8 implementation
  Program.cs                     Orchestration loop (async)
  BotConfig.cs                   Config from env vars
  Models/                        Domain models (including ExitSignal, TopupCandidate)
  Services/
    MarketScanner.cs             Gamma API integration, market filtering, batch price fetch
    Estimator.cs                 Claude ensemble via Anthropic REST API
    Portfolio.cs                 Kelly sizing, risk management, position review, API cost tracking
    ClobApiClient.cs             CLOB API auth (EIP-712 + HMAC), order signing/posting (buy & sell)
    LiveTrader.cs                Live execution via CLOB API (buy & sell with GTC polling)
    PaperTrader.cs               Simulated execution
    PersistenceService.cs        Atomic JSON save + JSONL trade log
    JsonFileLoggerProvider.cs    JSON lines file logger
  PolymarketBot.csproj           Project file

data/                          ← Runtime state (both implementations)
  portfolio.json                 Current portfolio state (atomically written)
  trades.jsonl                   Append-only trade history
  bot.log                        Structured JSON logs
```

### Estimation

The estimator makes N independent Claude calls per market at temperature 0.7. Each call returns a probability estimate. The trimmed mean (dropping highest and lowest when N≥4) becomes the fair value. The current market price is deliberately **not shown** to Claude to prevent anchoring.

### Kelly Criterion Sizing

The bot uses the [Kelly criterion](https://en.wikipedia.org/wiki/Kelly_criterion) to determine optimal position sizes. The Kelly formula maximizes long-run growth rate by betting more when the edge is larger and the odds are better.

**Formula:**

```
f* = (b × p - q) / b
```

Where:
- `f*` = fraction of bankroll to wager
- `b` = net odds (payout per $1 risked). For a market priced at `m`, `b = (1 - m) / m`
- `p` = estimated true probability (from Claude ensemble)
- `q` = 1 - p (probability of losing)

**Example:** A market is trading at $0.40 (implied 40% chance). Claude estimates the true probability is 55%.

```
b = (1 - 0.40) / 0.40 = 1.5        (risk $1 to win $1.50)
f* = (1.5 × 0.55 - 0.45) / 1.5     = 0.25    (full Kelly says bet 25%)
```

**Why fractional Kelly?** Full Kelly is mathematically optimal but extremely volatile — a small estimation error can lead to massive drawdowns. The bot defaults to **quarter Kelly** (`KELLY_FRACTION=0.25`), betting 25% of what full Kelly recommends. This sacrifices ~25% of the theoretical growth rate in exchange for ~75% less variance. The resulting bet in the example above:

```
Actual bet = 0.25 × 25% = 6.25% of bankroll
```

This is then capped by `MAX_POSITION_PCT` (default 15%) and must pass all risk checks before execution.

### Position Review & Exits

Each cycle, before scanning for new trades, the bot reviews all open positions:

- **Stop-loss** — sell if price dropped > 30% from entry
- **Take-profit** — sell if price reached 0.95+ (near certain resolution)
- **Edge-gone** — sell if the market price moved past the original fair estimate (edge evaporated)
- **Penny filter** — skip positions priced below $0.01 (can't create valid CLOB sell orders at sub-cent prices)
- **Top-up-and-sell** — tiny positions (<5 tokens) that trigger an exit are rescued by buying 5 more tokens to reach the CLOB minimum, then selling all

Sell orders use GTC (Good-Till-Cancelled) with a 6-second fill timeout. The top-up-and-sell algorithm handles positions that are too small to sell directly: it buys 5 tokens (CLOB minimum order size), then immediately sells the full position. If the buy fills but the sell doesn't, the position becomes sellable on the next cycle.

### Risk Management

Five layers of protection:

1. **Per-position cap** — max 15% of bankroll on any single market
2. **Per-category cap** — max 50% exposure in politics, sports, crypto, etc.
3. **Total exposure cap** — max 90% of bankroll in open positions
4. **Daily stop-loss** — halt trading if daily losses exceed 20%
5. **Max drawdown** — halt if drawdown from peak exceeds 50%

Daily stop-loss and drawdown are calculated against **portfolio value** (bankroll + open position value), not just bankroll alone. This prevents false halts when capital is deployed in positions.

### Agent Survival

API costs (Claude inference) are deducted from the bankroll every cycle. The agent must generate enough edge to cover its own operating costs. If the bankroll reaches $0 — from trading losses or API bills — `is_halted` flips to `true` and the bot stops.

## Disclaimer

This is experimental software. Prediction market trading carries risk. The bot can and will lose money. Past performance of paper trading does not predict live results. Do not trade with money you cannot afford to lose.

## License

MIT
