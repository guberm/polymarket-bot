# Polymarket Trading Bot

Autonomous trading agent for [Polymarket](https://polymarket.com) prediction markets. Scans hundreds of binary markets, estimates fair probabilities using a Claude ensemble, finds mispricing, and executes trades with Kelly criterion sizing.

Available in **Python** and **.NET 8** — both implementations share the same logic, config, and data formats.

**The agent pays for its own inference.** Claude API costs are deducted from the bankroll each cycle. If the total portfolio value (bankroll + open positions) drops below $1, the agent halts.

## How It Works

```text
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
  8. Execute trade (paper or live via CLOB GTC limit orders, +2 ticks aggression for immediate fills)
  9. Deduct API costs from bankroll
  10. Save state and repeat
```

## Quick Start

### 1. Create your config file

Copy the template and fill in your API keys:

```bash
git clone https://github.com/guberm/polymarket-bot.git
cd polymarket-bot
cp polymarket_bot_config.json.example polymarket_bot_config.json  # or create manually
# Edit polymarket_bot_config.json with your keys
```

Minimum required fields for paper trading:

```json
{
  "anthropic_api_key": "sk-ant-...",
  "anthropic_api_host": "https://api.anthropic.com",
  "gamma_api_host": "https://gamma-api.polymarket.com",
  "clob_host": "https://clob.polymarket.com"
}
```

### 2. Run

**Python:**

```bash
cd python
pip install -r requirements.txt
python main.py           # paper trading
python main.py --verbose # debug logging
python main.py --console # human-readable console output
```

**.NET:**

```bash
cd dotnet/PolymarketBot
dotnet run               # paper trading
dotnet run -- --verbose  # debug logging
dotnet run -- --console  # human-readable console output
```

**Windows (.bat):**

```text
run-bot.bat   ← double-click, reads polymarket_bot_config.json automatically
```

## Live Trading

> **Warning:** Live trading uses real money. Start with paper trading to validate signals.

Set these fields in `polymarket_bot_config.json`:

```json
{
  "live_trading": true,
  "polymarket_private_key": "0x...",
  "polymarket_funder_address": "0x...",
  "exchange_address": "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E",
  "neg_risk_exchange_address": "0xC5d563A36AE78145C45a50134d48A1215220f80a"
}
```

Requires a funded Polymarket wallet on Polygon (chain ID 137). For Gnosis Safe wallets, set `"polymarket_signature_type": 1`.

### Auto-claim (optional, .NET only)

When a position resolves WON, the bot can automatically submit the on-chain `CTF.redeemPositions` transaction so USDC returns to your wallet without manual intervention:

```json
{
  "auto_claim": true,
  "ctf_address":    "0x4D97DCd97eC945f40cF65F87097ACe5EA0476045",
  "usdc_address":   "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174",
  "polygon_rpc_url": "https://polygon-rpc.com"
}
```

`auto_claim` defaults to `true` but does nothing unless `ctf_address` and `usdc_address` are set.

## CLI Arguments

Risk parameters can be overridden at startup (override config file):

```bash
# Python
python main.py --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20

# .NET
dotnet run -- --max-position-pct 0.15 --max-total-exposure-pct 0.90 --daily-stop-loss-pct 0.20
```

Available CLI args: `--max-position-pct`, `--max-total-exposure-pct`, `--max-category-exposure-pct`, `--daily-stop-loss-pct`, `--max-drawdown-pct`, `--max-concurrent-positions`, `--verbose`.

## Configuration

All settings live in **`polymarket_bot_config.json`** at the project root. The file is not tracked by git (it contains secrets).

Config priority (highest wins): **CLI arg → env var → config file → code default**

| Key | Default | Description |
|-----|---------|-------------|
| `live_trading` | `false` | Set `true` for real orders |
| `initial_bankroll` | `10000` | Starting capital in USD |
| `anthropic_api_key` | — | Required. Claude API key |
| `anthropic_api_host` | — | Anthropic API base URL |
| `claude_model` | `claude-sonnet-4-20250514` | Model for estimation |
| `min_edge` | `0.08` | Minimum mispricing to trade (8%) |
| `scan_interval_minutes` | `10` | Time between scan cycles |
| `markets_per_cycle` | `30` | Max markets to evaluate per cycle |
| `ensemble_size` | `5` | Claude calls per market estimate |
| `ensemble_temperature` | `0.7` | Temperature for ensemble diversity |
| `kelly_fraction` | `0.25` | Fractional Kelly (0.25 = quarter Kelly) |
| `max_position_pct` | `0.15` | Max 15% of portfolio per position |
| `max_total_exposure_pct` | `1.00` | Max 100% of portfolio in open positions |
| `max_category_exposure_pct` | `0.80` | Max 80% per category |
| `daily_stop_loss_pct` | `0.20` | Halt if daily loss exceeds 20% |
| `max_drawdown_pct` | `0.50` | Halt if drawdown exceeds 50% |
| `max_concurrent_positions` | `20` | Max open positions |
| `min_trade_usd` | `0.5` | Minimum trade size in USD |
| `enable_position_review` | `true` | Review positions for exits each cycle |
| `position_stop_loss_pct` | `0.30` | Sell if position drops > 30% |
| `take_profit_price` | `0.95` | Sell if price reaches 0.95+ |
| `exit_edge_buffer` | `0.05` | Buffer before edge-gone exit triggers |
| `polymarket_private_key` | — | Wallet private key (live trading) |
| `polymarket_funder_address` | — | Funder address (live trading) |
| `polymarket_signature_type` | `0` | Signature type (0=EOA, 1=GNOSIS_SAFE) |
| `gamma_api_host` | — | Gamma API base URL (market discovery) |
| `clob_host` | — | CLOB API base URL (price quotes, orders) |
| `exchange_address` | — | CTF Exchange contract address |
| `neg_risk_exchange_address` | — | Neg Risk CTF Exchange contract address |
| `email_enabled` | `false` | Send email notifications |
| `email_smtp_host` | — | SMTP server (e.g. `smtp.gmail.com`) |
| `email_smtp_port` | `587` | SMTP port |
| `email_use_tls` | `true` | Use STARTTLS; set `false` for SSL on port 465 |
| `email_user` | — | SMTP login / sender address |
| `email_password` | — | SMTP password (use app password for Gmail) |
| `email_to` | — | Recipient address |
| `auto_claim` | `true` | Auto-submit on-chain redemption when a position resolves WON (.NET only) |
| `polygon_rpc_url` | `https://polygon-rpc.com` | Polygon JSON-RPC endpoint for auto-claim |
| `ctf_address` | — | CTF contract address (required for auto-claim) |
| `usdc_address` | — | USDC contract address (required for auto-claim) |

All keys can also be set as environment variables (uppercase, underscores). For example, `anthropic_api_key` → `ANTHROPIC_API_KEY`. Env vars take priority over the config file.

## Email Notifications

Set `"email_enabled": true` in `polymarket_bot_config.json` to receive emails on every state change:

| Event | Example subject |
|-------|----------------|
| Bot started | `Started: LIVE mode` |
| Daily reset | `Daily reset — portfolio $37.50` |
| Position opened | `BUY YES $5.00 — Will X happen?` |
| Position closed | `SELL (stop_loss) -32.1% — Will X happen?` |
| Market resolved | `Resolved (WON) PnL=+$5.00 — ...` |
| Agent halted | `HALTED: Portfolio value < $1` |
| Cycle error | `Error in cycle 42` |
| Bot stopped | `Stopped — portfolio $35.20, PnL -$1.80` |

For Gmail, create an [App Password](https://myaccount.google.com/apppasswords) instead of using your regular password.

## Project Structure

```text
polymarket_bot_config.json     ← Your config (not tracked by git — contains secrets)

python/                        ← Python implementation
  main.py                        Orchestration loop
  config.py                      BotConfig (reads config.json then env vars)
  notifier.py                    Email notifications (smtplib)
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
  BotConfig.cs                   Config (reads config.json then env vars)
  Models/                        Domain models (including ExitSignal, TopupCandidate)
  Services/
    MarketScanner.cs             Gamma API integration, market filtering, batch price fetch
    Estimator.cs                 Claude ensemble via Anthropic REST API
    Portfolio.cs                 Kelly sizing, risk management, position review, API cost tracking
    Notifier.cs                  Email notifications (System.Net.Mail)
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

```text
f* = (b × p - q) / b
```

Where:
- `f*` = fraction of bankroll to wager
- `b` = net odds (payout per $1 risked). For a market priced at `m`, `b = (1 - m) / m`
- `p` = estimated true probability (from Claude ensemble)
- `q` = 1 - p (probability of losing)

**Example:** A market is trading at $0.40 (implied 40% chance). Claude estimates the true probability is 55%.

```text
b = (1 - 0.40) / 0.40 = 1.5        (risk $1 to win $1.50)
f* = (1.5 × 0.55 - 0.45) / 1.5     = 0.25    (full Kelly says bet 25%)
```

**Why fractional Kelly?** Full Kelly is mathematically optimal but extremely volatile — a small estimation error can lead to massive drawdowns. The bot defaults to **quarter Kelly** (`kelly_fraction: 0.25`), betting 25% of what full Kelly recommends. This sacrifices ~25% of the theoretical growth rate in exchange for ~75% less variance. The resulting bet in the example above:

```text
Actual bet = 0.25 × 25% = 6.25% of bankroll
```

This is then capped by `max_position_pct` (default 15%) and must pass all risk checks before execution.

### Position Review & Exits

Each cycle, before scanning for new trades, the bot reviews all open positions:

- **Stop-loss** — sell if price dropped > 30% from entry
- **Take-profit** — sell if price reached 0.95+ (near certain resolution)
- **Edge-gone** — sell if the market price moved past the original fair estimate (edge evaporated)
- **Penny filter** — skip positions priced below $0.01 (can't create valid CLOB sell orders at sub-cent prices)
- **Top-up-and-sell** — tiny positions (<5 tokens) that trigger an exit are rescued by buying 5 more tokens to reach the CLOB minimum, then selling all

Buy orders are placed at midpoint + 2 tick sizes to cross the spread and fill immediately as taker orders. Sell orders use the midpoint price. GTC orders poll for fill for up to 15 seconds, then cancel if unfilled. The top-up-and-sell algorithm handles positions that are too small to sell directly: it buys 5 tokens (CLOB minimum order size), then immediately sells the full position. If the buy fills but the sell doesn't, the position becomes sellable on the next cycle.

### Risk Management

Five layers of protection:

1. **Per-position cap** — max 15% of portfolio on any single market
2. **Per-category cap** — max 80% exposure in politics, sports, crypto, etc.
3. **Total exposure cap** — max 100% of portfolio in open positions
4. **Daily stop-loss** — halt trading if daily losses exceed 20%
5. **Max drawdown** — halt if drawdown from peak exceeds 50%

Daily stop-loss and drawdown are calculated against **portfolio value** (bankroll + open position value), not just bankroll alone. This prevents false halts when capital is deployed in positions.

### Agent Survival

API costs (Claude inference) are deducted from the bankroll every cycle. The agent must generate enough edge to cover its own operating costs.

To avoid spending the last USDC on API calls when no trades are possible, the estimation loop stops early if `bankroll < $0.30`. The bot also skips the Gamma API scan entirely if the bankroll is too low to fund the smallest possible position — saving ~15s per cycle. The scan threshold is `max(min_trade_usd, max_position_pct × bankroll)`, so the threshold scales with free cash rather than total portfolio value (avoiding false blocks when most capital is locked in open positions). The bot continues running (monitoring positions, waiting for exits) and resumes full operation once USDC returns from resolved/exited positions.

The agent truly halts only when total portfolio value (`bankroll + open position value`) drops below $1. This prevents false halts when capital is deployed in positions but free USDC is temporarily low. A stale halt flag from a previous session is automatically cleared on restart if the portfolio is still healthy.

## Disclaimer

This is experimental software. Prediction market trading carries risk. The bot can and will lose money. Past performance of paper trading does not predict live results. Do not trade with money you cannot afford to lose.

## License

MIT
