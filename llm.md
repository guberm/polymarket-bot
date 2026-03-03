# Polymarket Bot — LLM Code Reference

Comprehensive description of the codebase for future Claude sessions.

---

## Project Summary

Autonomous trading bot for Polymarket (binary prediction markets). Each cycle it:

1. Reviews open positions (exit if stop-loss / take-profit / edge-gone / resolved)
2. Scans Gamma API for active markets
3. Estimates fair probability via ensemble of N Claude API calls (trimmed mean)
4. Sizes a position using fractional Kelly criterion
5. Checks 5-layer risk limits
6. Executes the trade (paper or live CLOB)
7. Persists state and sends email notification

The bot pays for its own Claude API inference from its bankroll. Two identical implementations: **Python** (`python/`) and **.NET 8** (`dotnet/PolymarketBot/`). Both read the same `polymarket_bot_config.json` and write to the same `data/` directory.

---

## Config (`python/config.py` / `dotnet/BotConfig.cs`)

Single `BotConfig` dataclass. Load priority: **CLI arg → env var → `polymarket_bot_config.json` → code default**.

Key fields and defaults:

- `live_trading: bool = False` — paper vs real orders
- `scan_interval_minutes: int = 10` — sleep between cycles
- `min_liquidity: float = 5000` — Gamma API filter
- `min_volume_24hr: float = 1000` — Gamma API filter
- `min_time_to_resolution_hours: float = 24` — skip markets resolving soon
- `min_market_price: float = 0.10` — skip extreme prices (no CLOB liquidity)
- `markets_per_cycle: int = 30` — cap on markets evaluated per cycle
- `claude_model: str = "claude-sonnet-4-20250514"` — model for estimation
- `ensemble_size: int = 5` — number of independent Claude calls per market
- `ensemble_temperature: float = 0.7`
- `kelly_fraction: float = 0.25` — fractional Kelly (25% of full Kelly)
- `min_edge: float = 0.08` — 8pp minimum edge to trade
- `min_trade_usd: float = 0.5` — minimum position size in dollars
- `max_position_pct: float = 0.15` — per-position cap (15% of portfolio value)
- `max_total_exposure_pct: float = 1.00` — total exposure cap (100%)
- `max_category_exposure_pct: float = 0.80` — per-category cap (80%)
- `daily_stop_loss_pct: float = 0.20` — halt if daily loss > 20%
- `max_drawdown_pct: float = 0.50` — halt if drawdown from HWM > 50%
- `max_concurrent_positions: int = 20`
- `position_stop_loss_pct: float = 0.30` — individual position exit if down 30%
- `take_profit_price: float = 0.95` — exit if token price ≥ 0.95
- `exit_edge_buffer: float = 0.05` — exit if price > fair_estimate + 5pp
- `initial_bankroll: float = 10000` — starting cash
- `data_dir: str = "../data"` — where to write portfolio.json, trades.jsonl, bot.log
- `auto_claim: bool = True` — (.NET only) auto-claim won positions on-chain

**Config file location**: project root `polymarket_bot_config.json`. Not tracked by git (contains secrets). Path can be overridden via `CONFIG_FILE` env var.

---

## Models (`python/models.py` / `dotnet/Models/`)

All domain types. Both implementations share the same field names.

### MarketInfo

Parsed from Gamma API. Key fields: `condition_id`, `question`, `outcome_yes_price`, `outcome_no_price`, `token_id_yes`, `token_id_no`, `liquidity`, `volume_24hr`, `best_bid`, `best_ask`, `spread`, `end_date`, `category`, `event_title`, `description`.

### Estimate

Output of Claude ensemble. Key fields: `fair_probability` (trimmed mean), `raw_estimates` (list), `confidence` (std dev), `reasoning_summary` (first call's reasoning), `input_tokens_used`, `output_tokens_used`.

### Signal

Generated when edge exceeds threshold. Key fields: `market`, `estimate`, `side` (YES/NO), `edge`, `market_price`, `kelly_fraction`, `position_size_usd`, `expected_value`.

### Position

Open position in portfolio. Key fields: `condition_id`, `question`, `side`, `token_id`, `entry_price`, `size_usd` (cost basis), `shares`, `current_price`, `unrealized_pnl`, `category`, `fair_estimate_at_entry` (original Claude estimate — used for edge-gone exit logic), `order_id`.

### Trade

Completed trade record (BUY or SELL). Key fields: `trade_id`, `condition_id`, `side`, `action` (BUY/SELL), `price`, `size_usd`, `shares`, `is_paper`, `edge_at_entry`, `kelly_at_entry`, `exit_reason`.

### ExitSignal

Signals a position should be closed. Fields: `position`, `exit_reason` (`stop_loss`/`take_profit`/`edge_gone`), `current_price`, `unrealized_pnl`, `pnl_pct`.

### TopupCandidate

Tiny position (<5 tokens) that wants to exit but can't (CLOB minimum is 5 tokens). Fields: `position`, `exit_reason`, `tokens_to_buy=5.0`, `topup_cost` (5 × current_price), `recovery_value` (current shares × current_price).

### PortfolioSnapshot

Persisted state. Fields: `bankroll`, `initial_bankroll`, `positions`, `high_water_mark`, `daily_start_value`, `total_realized_pnl`, `total_trades`, `is_halted`.

---

## Main Loop (`python/main.py` / `dotnet/Program.cs`)

Runs indefinitely, one cycle per `scan_interval_minutes`.

### Cycle structure

**1. Halt check** — if `is_halted`, send notification and break.

**2. Daily reset** — if UTC date changed since last cycle, call `portfolio.reset_daily()` (resets `daily_start_value` to current bankroll).

**3. Balance sync** — live trading only: fetch actual on-chain USDC balance from CLOB API and call `portfolio.sync_balance(balance)`. This corrects any drift from fees, partial fills, or resolved positions returning USDC.

**4. Position review** (if `enable_position_review` and positions exist):

- Fetch current midpoint prices for all held tokens via CLOB API
- Update `current_price` and `unrealized_pnl` on all positions

  **Tier 0 — Resolved markets**: For positions where price is missing or < $0.01, query CLOB `/markets/{condition_id}` to check if closed. If resolved, call `portfolio.resolve_position(condition_id, won)`. Won: payout = shares × $1. Lost: payout = $0. In .NET: also submits auto-claim on-chain tx via `ClobApiClient.RedeemWinningPositionAsync`.

  **Tier 1 — Rule-based exits**: `portfolio.generate_exit_signals()` checks each position (skips price < $0.01 or shares < 5): stop-loss (PnL% < -30%), take-profit (price ≥ 0.95), edge-gone (price > fair_estimate + buffer). For each ExitSignal, calls `trader.execute_sell()`. After live sell, re-syncs bankroll from on-chain USDC.

  **Tier 1.5 — Topup-and-sell**: `portfolio.generate_topup_candidates()` finds tiny (<5 token) positions that meet exit conditions. For each, calls `trader.execute_topup_and_sell()` which buys 5 tokens then sells everything. Skipped if bankroll < topup cost.

**5. Scan skip guard** — if `bankroll < max(max_position_pct × bankroll, min_trade_usd)`, skip the scan entirely (saves ~15s API call).

**6. Market scan** — `scanner.scan()` fetches all active events from Gamma API (paginated), parses markets, filters by liquidity/volume/price/time, sorts by 24h volume descending. Returns up to `markets_per_cycle` results.

**7. Exposure capacity check** — pre-check if `exposure_room < min_realistic_position`. If so, skip all estimations (saves API costs).

**8. Market evaluation loop** — for each market:

- Skip if already holding this `condition_id`
- Skip if `atCapacity` (exposure full)
- Skip if `bankroll < $0.30` (API reserve guard — bot keeps running for position review)
- Skip if `bankroll < 5 × min(yes_price, no_price)` — can't afford the CLOB minimum for either side; no point calling Claude (saves API costs when bankroll is low)
- Call `estimator.estimate(market)` → N Claude calls, trimmed mean
- Call `portfolio.record_api_cost()` → deduct inference cost from bankroll
- If portfolio value < $1 total, set `is_halted = True` (agent dead)
- Call `portfolio.generate_signal(market, estimate)` → Signal if edge > min_edge
- If signal is None, log why (no edge vs too small for CLOB minimum)
- Call `portfolio.check_risk(signal)` → validate all risk limits
- Call `trader.execute(signal, portfolio)` → Trade record
- After live trade, re-sync bankroll from on-chain USDC
- Persist trade + snapshot

**9. Cycle summary** — log stats, save final snapshot.

**10. Sleep** — tick-by-tick sleep (1s ticks) for responsive Ctrl+C shutdown.

---

## MarketScanner (`python/market_scanner.py` / `dotnet/Services/MarketScanner.cs`)

### scan() / ScanAsync()

Paginates through `{gamma_api_host}/events?active=true&closed=false&limit=100&offset=N`. Each event contains `markets[]`. Filters each market:

- `active=true` and `closed=false`
- Exactly 2 binary outcomes
- `liquidity >= min_liquidity`
- `volume_24hr >= min_volume_24hr`
- Price in tradeable range: at least one side between [min_market_price, 1-min_market_price]
- Time to resolution > `min_time_to_resolution_hours`

Gamma API quirk: `outcomes`, `outcomePrices`, and `clobTokenIds` fields may be JSON-encoded strings inside JSON (e.g., `"[\"YES\", \"NO\"]"`) or actual arrays — both forms are handled.

Category classification uses `CATEGORY_KEYWORDS` dict with keyword matching against event title + slug. Categories: politics, geopolitics, sports, crypto, tech, social_media, weather, entertainment, finance, other.

Markets sorted by `volume_24hr` descending.

### check_market_resolution(condition_id) / CheckMarketResolutionAsync

Queries `{clob_host}/markets/{condition_id}`. Returns `{"winning_side": "YES"|"NO"}` if `closed=true` and a token has `winner=true`. Returns None if still active or can't determine.

### get_market_prices(token_ids) / GetMarketPricesAsync

Fetches midpoint price for each token from `{clob_host}/midpoint?token_id=...`. Returns dict of `token_id → price`.

---

## Estimator (`python/estimator.py` / `dotnet/Services/Estimator.cs`)

### Ensemble approach

Calls Claude `ensemble_size` (default 5) times independently for each market. Uses `temperature=0.7` for diversity. Drops highest and lowest if ≥ 4 samples (trimmed mean). Confidence = standard deviation of all samples.

### System prompt

Asks for calibrated probability estimate. Rules: output only JSON `{"probability": 0.XX, "reasoning": "..."}`, probability clamped to [0.02, 0.98], do NOT anchor on market price, keep reasoning under 50 words.

### User prompt

Includes: market question, event title, description (truncated to 500 chars), category, resolution date.

### Error handling

- Python: catches `anthropic.RateLimitError` → sleep 5s, returns None for that call
- .NET: retries on HTTP 429 or 529 with exponential backoff 10s → 20s → 40s, up to 3 retries
- JSON parse failures return None for that call
- If fewer than 2 valid estimates collected, warning logged; if 0, returns None (market skipped)

### Token cost tracking

Sums input/output tokens across all calls, returns in `Estimate`. Main loop deducts cost from bankroll via `portfolio.record_api_cost()`.

---

## Portfolio (`python/portfolio.py` / `dotnet/Services/Portfolio.cs`)

All financial math and state management.

### Kelly criterion (generate_signal)

```text
b = (1/market_price) - 1     # decimal odds
p = fair_probability          # for YES side
q = 1 - p
kelly_raw = (b*p - q) / b
kelly = kelly_raw * kelly_fraction   # fractional Kelly (25%)
size_usd = kelly * portfolio_value   # use total portfolio value, not just cash
size_usd = min(size_usd, portfolio_value * max_position_pct)  # position cap
size_usd = min(size_usd, bankroll)   # never exceed available cash
```

Returns None if: edge < min_edge for both sides, market_price ≤ 0 or ≥ 1, size_usd < min_trade_usd, size_usd < 5 × market_price (CLOB minimum of 5 tokens).

### Risk checks (check_risk)

In order:

1. Already holding this market → block
2. `len(positions) >= max_concurrent_positions` → block
3. `total_exposure + new_size > portfolio_value × max_total_exposure_pct` → block
4. `category_exposure + new_size > portfolio_value × max_category_exposure_pct` → block
5. `portfolio_value - daily_start_value < -daily_start_value × daily_stop_loss_pct` → halt
6. `(high_water_mark - portfolio_value) / high_water_mark > max_drawdown_pct` → halt
7. `bankroll + total_exposure < $1` → halt (agent dead)

### Position management

- `open_position()`: deducts `size_usd` from bankroll, appends to positions list
- `close_position(condition_id, exit_price)`: removes position, returns `size_usd + pnl` to bankroll. PnL = `shares × (exit_price - entry_price)`. Updates high water mark.
- `resolve_position(condition_id, won)`: won → payout = shares (each share = $1 at resolution). lost → payout = 0.
- `add_to_position()`: for topup-and-sell; adds shares and cost to existing position.

### Exit signal generation

Iterates all positions, skips `current_price < $0.01` (penny) or `shares < 5` (tiny). Checks in order: stop-loss → take-profit → edge-gone. Returns list of ExitSignal.

### Balance sync

`sync_balance(actual_usdc_balance)`: always updates bankroll to actual on-chain USDC (up or down). Ignores changes < $0.001. Updates high water mark.

### API cost tracking

`record_api_cost(input_tokens, output_tokens)`: deducts `(input × $3/MTok + output × $15/MTok)` from bankroll. Accumulates in `total_api_cost`.

### Persistence

`snapshot()` creates a `PortfolioSnapshot` with all state. `total_api_cost` is NOT persisted (resets each run).

---

## Trader (`python/trader.py` / `dotnet/Services/LiveTrader.cs`, `PaperTrader.cs`)

### PaperTrader

- `execute(signal, portfolio)`: creates Position at `signal.market_price`, `size_usd = signal.position_size_usd`, `shares = size_usd / price`. Calls `portfolio.open_position()`. Returns Trade.
- `execute_sell(exit_signal, portfolio)`: calls `portfolio.close_position()`. Returns Trade.
- `execute_topup_and_sell(candidate, portfolio)`: calls `portfolio.add_to_position(5 tokens)` then simulate sell.

### LiveTrader

Uses py-clob-client (Python) or direct CLOB API calls (C# ClobApiClient).

**BUY flow** (`execute` / `ExecuteAsync`):

1. Set price = `signal.market_price + 0.02` (2 ticks aggression to cross spread, capped at 0.99)
2. Create GTC limit order via CLOB
3. Poll for MATCHED status: 5 attempts × 3s each = 15s max
4. If not matched → cancel order, return None
5. If matched → parse actual fill amounts from response, open position with actual price/shares

**SELL flow** (`execute_sell` / `ExecuteSellAsync`):

1. Fetch actual on-chain conditional token balance (via `update_balance_allowance` then `get_balance_allowance`)
2. If on-chain balance < portfolio recorded shares → use actual balance (partial fill correction)
3. Skip if price < $0.01 or shares < 5
4. Submit SELL GTC order (side=SELL, amount=tokens)
5. Poll 3 attempts × 2s = 6s max
6. If matched → call `portfolio.close_position()`, return Trade

**TopupAndSell flow**:

1. BUY 5 tokens (same GTC polling as BUY)
2. Update position via `portfolio.add_to_position(5, cost)`
3. Fetch actual on-chain balance to get true total
4. SELL all tokens (same GTC polling as SELL)
5. If SELL fails → position now has 5+ tokens, will be sellable next cycle

### CLOB authentication (.NET ClobApiClient)

Implements EIP-712 signing from scratch using Nethereum. Two auth levels:

- **L1 (CLOB auth)**: Signs `ClobAuth` struct with domain `ClobAuthDomain` — used to derive API keys
- **L2 (Order signing)**: Signs `Order` struct with domain `Polymarket CTF Exchange` (includes verifyingContract = exchange address) — used for each order
- **HMAC**: L2 requests also include HMAC-SHA256 signature of timestamp+method+path+body

**Auto-claim** (.NET only): When a WON position is detected, submits raw EIP-155 tx to Polygon calling `CTF.redeemPositions`. Calldata is 196-byte ABI-encoded manually. Requires `ctf_address`, `usdc_address`, `polygon_rpc_url` in config. Controlled by `auto_claim` config flag (default true).

---

## Persistence (`python/persistence.py` / `dotnet/Services/PersistenceService.cs`)

**Portfolio state**: `data/portfolio.json` — written atomically via tmp+rename (prevents corruption on crash). Contains full PortfolioSnapshot. Loaded on startup.

**Trade log**: `data/trades.jsonl` — append-only JSONL, one Trade per line. Never read by the bot (for analysis only).

**Logging**: `data/bot.log` — append-only JSON lines. Each line: `{timestamp, level, logger, message}`. Console output uses ANSI colors.

---

## Notifier (`python/notifier.py` / `dotnet/Services/Notifier.cs`)

Sends email notifications. All methods silently swallow errors — notification failure never crashes bot.

Events: `started`, `trade` (BUY), `sell`, `topup_sell`, `resolved` (WON/LOST), `halted`, `daily_reset`, `error`, `stopped`.

SMTP: STARTTLS (port 587) if `email_use_tls=true`, SMTP_SSL (port 465) if false.

---

## Key Invariants and Edge Cases

**Portfolio value**: always `bankroll + total_exposure()`. The bankroll is free USDC; total_exposure is cost basis of open positions. Both are required to assess true financial state.

**CLOB minimum**: 5 tokens per order. Minimum BUY in USD = `5 × price`. Positions that accidentally end up with <5 tokens (partial fill, tiny edge) are unsellable and handled by TopupAndSell.

**Penny positions**: price < $0.01 — completely unsellable (not even via topup). Skipped in all review checks. Usually indicates resolved losing positions still lingering in CLOB.

**Bankroll can go negative**: When all capital is locked in open positions and API costs continue, bankroll goes negative. This is OK as long as `bankroll + total_exposure ≥ $1`. Bot keeps running for position review; stops new estimation when `bankroll < $0.30`.

**Stale is_halted**: On restart, if `is_halted=True` but `bankroll + total_exposure ≥ $1`, the flag is cleared. Prevents transient low-USDC halt (while positions are held) from persisting across restarts.

**Scan skip threshold**: `max(min_trade_usd, max_position_pct × bankroll)` — uses only free cash (bankroll), not portfolio value. Prevents scan when we can't actually afford a trade even if portfolio looks healthy on paper.

**Edge-gone logic**: When we bought YES at 0.40 because Claude estimated 0.60 fair value. If the market later moves to 0.65 (above our fair estimate + buffer), we were wrong — exit. The `fair_estimate_at_entry` field stores the original estimate per position.

**Balance sync**: Every cycle in live mode, and after each BUY/SELL, the bankroll is synced from actual on-chain USDC. This corrects for: gas fees, partial fills, resolved positions returning funds, manual deposits.

**GTC orders**: All live orders are GTC (Good-Till-Cancelled) limit orders. BUY adds +0.02 aggression to cross spread and fill as taker. SELL orders don't add aggression (sells at current price). Poll for MATCHED status, cancel if unfilled.

---

## Data Flow Diagram

```text
main.py (main loop)
├── scanner.scan()
│   └── Gamma API /events (paginated)
│       └── parse + filter → list[MarketInfo]
│
├── scanner.get_market_prices(token_ids)
│   └── CLOB API /midpoint (per token)
│
├── scanner.check_market_resolution(condition_id)
│   └── CLOB API /markets/{condition_id}
│
├── estimator.estimate(market)
│   └── N × Anthropic API /messages
│       └── trimmed mean → Estimate
│
├── portfolio.generate_signal(market, estimate)
│   └── Kelly criterion → Signal
│
├── portfolio.check_risk(signal)
│   └── 5-layer risk check → bool
│
├── trader.execute(signal, portfolio)  [or execute_sell / execute_topup_and_sell]
│   ├── PaperTrader: in-memory simulation
│   └── LiveTrader: CLOB API /order (GTC) + poll /order/{id}
│
├── persistence.save_snapshot(portfolio.snapshot())
│   └── data/portfolio.json (atomic tmp+rename)
│
└── persistence.append_trade(trade)
    └── data/trades.jsonl (append)
```

---

## File Index

### Python

| File | Purpose |
| --- | --- |
| [python/main.py](python/main.py) | Main orchestration loop |
| [python/config.py](python/config.py) | BotConfig dataclass + config loading |
| [python/models.py](python/models.py) | All domain dataclasses and enums |
| [python/market_scanner.py](python/market_scanner.py) | Gamma API + CLOB price/resolution queries |
| [python/estimator.py](python/estimator.py) | Claude ensemble probability estimation |
| [python/portfolio.py](python/portfolio.py) | Kelly sizing, risk checks, position management |
| [python/trader.py](python/trader.py) | PaperTrader + LiveTrader (py-clob-client) |
| [python/persistence.py](python/persistence.py) | JSON save/load for portfolio + JSONL trades |
| [python/notifier.py](python/notifier.py) | Email notifications |
| [python/logger_setup.py](python/logger_setup.py) | Dual logging: colored console + JSON file |

### .NET

| File | Purpose |
| --- | --- |
| [dotnet/PolymarketBot/Program.cs](dotnet/PolymarketBot/Program.cs) | Async main loop (mirrors python/main.py) |
| [dotnet/PolymarketBot/BotConfig.cs](dotnet/PolymarketBot/BotConfig.cs) | Config (mirrors python/config.py) |
| [dotnet/PolymarketBot/Services/MarketScanner.cs](dotnet/PolymarketBot/Services/MarketScanner.cs) | Market discovery (mirrors market_scanner.py) |
| [dotnet/PolymarketBot/Services/Estimator.cs](dotnet/PolymarketBot/Services/Estimator.cs) | Claude estimation via raw HttpClient |
| [dotnet/PolymarketBot/Services/Portfolio.cs](dotnet/PolymarketBot/Services/Portfolio.cs) | Portfolio math (mirrors portfolio.py) |
| [dotnet/PolymarketBot/Services/LiveTrader.cs](dotnet/PolymarketBot/Services/LiveTrader.cs) | Live trading via ClobApiClient |
| [dotnet/PolymarketBot/Services/PaperTrader.cs](dotnet/PolymarketBot/Services/PaperTrader.cs) | Paper trading simulation |
| [dotnet/PolymarketBot/Services/ClobApiClient.cs](dotnet/PolymarketBot/Services/ClobApiClient.cs) | EIP-712 + HMAC CLOB auth + auto-claim |
| [dotnet/PolymarketBot/Services/PersistenceService.cs](dotnet/PolymarketBot/Services/PersistenceService.cs) | Atomic JSON portfolio + JSONL trades |
| [dotnet/PolymarketBot/Services/Notifier.cs](dotnet/PolymarketBot/Services/Notifier.cs) | Email notifications |
| [dotnet/PolymarketBot/Services/JsonFileLoggerProvider.cs](dotnet/PolymarketBot/Services/JsonFileLoggerProvider.cs) | JSON line file logger |
| [dotnet/PolymarketBot/Models/](dotnet/PolymarketBot/Models/) | C# model classes (mirrors python/models.py) |

---

## Common Gotchas When Making Changes

1. **Both implementations must stay in sync** — any logic change in Python should be mirrored in .NET and vice versa.

2. **Gamma API JSON quirk** — `outcomes`, `outcomePrices`, `clobTokenIds` fields can be either JSON-encoded strings or actual lists. Always handle both.

3. **CLOB order amounts**: BUY `amount` = USD; SELL `amount` = tokens. This asymmetry is by design (CLOB convention).

4. **Portfolio value vs bankroll**: Risk limits use `bankroll + total_exposure()` as portfolio value. Bankroll alone is free USDC. Never confuse the two.

5. **The agent is NOT dead when bankroll goes negative** — it's only dead when `bankroll + total_exposure < $1`. Negative bankroll with open positions is normal (API costs while waiting for resolution).

6. **`fair_estimate_at_entry`** is stored per position at trade time. It's used for edge-gone exits. Set to 0 for old/legacy positions, which disables edge-gone checks for them.

7. **CLOB minimum = 5 tokens** — enforced on both BUY and SELL. Positions under 5 tokens use the TopupAndSell pathway.

8. **Scan skip uses bankroll (free cash), not portfolio value** — so the check correctly reflects ability to fund a new trade.

9. **.NET Estimator uses raw HttpClient** to Anthropic REST API (no SDK). Python uses the `anthropic` SDK. Both hit the same endpoint with the same request format.

10. **Auto-claim (.NET only)**: When `auto_claim=true` and a WON position is detected, an on-chain EIP-155 tx is submitted to Polygon calling `CTF.redeemPositions`. Failure is non-blocking — the USDC returns to the wallet eventually when claimed manually, and balance sync will pick it up.

---

## Dashboard (`dashboard/`)

Electron desktop app for real-time bot monitoring. Runs alongside (or instead of) the CLI. Does not modify any bot data — read-only except for `write-config`.

### Architecture

Standard Electron three-process model:

- **main.js** — Node.js main process. File I/O, child process management, IPC handlers, `fs.watch` file watchers.
- **preload.js** — Context bridge. Exposes `window.api` to renderer using `contextBridge.exposeInMainWorld`. No raw Node APIs leak to the renderer.
- **renderer.js** — Browser-context UI logic. All display logic: stats, charts, tables, log, modals, resize.
- **index.html** / **styles.css** — UI shell and dark theme.

### Running

```bash
cd dashboard
npm install      # first time only
npm start        # or run-dashboard.bat from project root
```

### Main Process (main.js)

Key functions:

- `findDataDir()` — looks for `../data` relative to `dashboard/`, falls back to `userData/data`
- `getBotRoot()` — `path.dirname(dataDir)` — project root
- `getConfigPath()` — `botRoot/polymarket_bot_config.json`
- `setupFileWatcher()` — watches `data/` dir for changes to `portfolio.json`, `trades.jsonl`, `bot.log`; includes **300ms debounce** per file and handles `name === null` (Windows sometimes omits filename); sends `file-changed` IPC event to renderer
- `readLines(file, n)` — reads last N lines of a JSONL file; non-JSON lines get `timestamp: new Date().toISOString()`

**IPC handlers**: `get-data-dir`, `set-data-dir`, `browse-data-dir`, `read-portfolio`, `read-trades`, `read-logs`, `read-config`, `write-config`, `bot-status`, `start-bot`, `stop-bot`, `save-file`, `open-logs-dir`

**start-bot handler**:

1. Rotates `bot.log` → `bot-TIMESTAMP.log` (isolates sessions)
2. Checks for pre-compiled exe: `bin/Release/net8.0/PolymarketBot.exe` then `bin/Debug/net8.0/PolymarketBot.exe`
3. If exe found: `spawn(exePath, extraArgs, { shell: false })` — `shell: false` critical for paths with spaces
4. If no exe (first run): `spawn('dotnet', ['run', ...], { shell: true })`
5. Python: `spawn('python', ['main.py', ...], { shell: true })`
6. Forwards stdout → `bot-output` IPC (level: INFO), stderr → `bot-output` (level: WARNING)
7. On exit: `bot-stopped` IPC with exit code

### Renderer (renderer.js)

**State variables**:

- `portfolio`, `trades`, `logs`, `extraLogLines` — data from IPC reads
- `logClearedAt = Date.now()` — initialized to load time so pre-existing log entries are hidden; reset to 0 on bot start
- `pnlChart`, `catChart` — Chart.js instances, created once
- `posSort`, `tradesSort = { col, dir }` — sort state for each table
- `hiddenCategories = new Set()` — category filter state
- `catColorCache` — stable category → color mapping
- `currentLang` — `localStorage.getItem('lang') || 'ru'`

**Key functions**:

- `init()` — bootstraps: get data dir, calls `initLang()`, `initTooltips()`, then `initCharts()`, first refresh, start 8s interval, wire IPC listeners
- `refresh()` — reads portfolio + trades + logs in parallel, renders all sections; **bug fix**: destructures result as `[p, tr, l]` (was `[p, t, l]` which shadowed the global `t()` translation function, causing TypeError on every call)
- `initCharts()` — creates Chart.js instances with `animation: false` and `responsive: true`
- `renderPnlChart()` / `renderCatChart()` — update data in-place, call `chart.update('none')` (no flicker)
- `renderCategoryFilters()` — renders filter pills; click toggles `hiddenCategories`; pill has colored dot
- `renderPositions()` — filters hidden categories, sorts by `posSort`, renders table rows
- `renderTrades()` — sorts by `tradesSort` (default: newest-first by timestamp), limit 500 rows
- `renderLog()` — filters `logs` by `logClearedAt` using `parseTs()`; replaces container HTML
- `appendLogLine()` — appends a single line DOM node (for live bot-output events)
- `initSortHeaders()` — attaches click handlers to `.th-sort` headers in both `#positions-table` and `#trades-table`
- `setupResize()` — drag handles for horizontal split (left/right cols) and vertical split (right-upper/log)
- `parseTs(ts)` — normalizes timestamps before `new Date()`: strips extra fractional-second digits (handles .NET's 7-decimal `ToString("o")`)
- `exportLog()` — filters both `logs` and `extraLogLines` by `logClearedAt`, sorts, exports via `save-file` IPC
- `confirmStart()` — saves mode/flags to localStorage, resets log state (`logClearedAt=0, logs=[], extraLogLines=[]`), starts bot
- `applyLang()` — updates all `[data-i18n]` elements; preserves child elements (tip icons, sort-ind spans) by updating first text node only instead of `innerHTML`; also calls `applyTips()`
- `applyTips()` — sets `data-tip` attribute on all `.tip-icon[data-tip-key]` elements from current language's `tips` dict
- `initLang()` — reads `localStorage.lang`, wires RU/EN toggle button, calls `applyLang()` + re-renders on switch
- `initTheme()` — reads `localStorage.theme`, toggles `body.light` class, wires 🌙/☀ button
- `initTooltips()` — creates a single `.tooltip-popup` div appended to `<body>`; uses `position: fixed` + `getBoundingClientRect()` for positioning; event delegation via `mouseover`/`mouseout` on `.tip-icon[data-tip]` elements. Avoids `overflow: hidden` clipping that breaks CSS `::after` pseudo-element tooltips.
- `openConfig()` / `saveConfig()` — reads/writes `polymarket_bot_config.json` via IPC; renders form from `CONFIG_SCHEMA`; language-aware: uses `s.ru`/`f.ru` from `CONFIG_SCHEMA` when `currentLang === 'ru'`
- `fmtTime` — uses `ru-RU` locale when `currentLang === 'ru'`, `en-US` otherwise

**i18n System**:

- `TRANS = { ru: {...}, en: {...} }` — all UI strings in both languages. Values can be plain strings or functions for parameterized strings (e.g., `subInitial: v => \`нач. \${v}\``).
- `t(key, ...args)` — returns `TRANS[currentLang][key]`, falls back to `en`; calls it if a function.
- `TRANS[lang].tips` — sub-object with tooltip texts for all 13 sections/stats.
- `data-i18n="key"` attributes on HTML elements — updated by `applyLang()`.
- `data-tip-key="key"` on `.tip-icon` spans — `data-tip` attribute set by `applyTips()`.
- Language persists in `localStorage.lang`; theme persists in `localStorage.theme`.
- `applyLang()` uses text-node update (not `innerHTML`) to preserve child elements.

**Log isolation pattern**:

```javascript
// On dashboard load:
let logClearedAt = Date.now()  // hides all pre-existing entries

// On bot start (confirmStart):
logClearedAt = 0; logs = []; extraLogLines = []; container.innerHTML = ''

// On clear button:
logClearedAt = Date.now(); extraLogLines = []; container.innerHTML = ''

// In renderLog / exportLog / onBotOutput:
parseTs(l.timestamp) > logClearedAt  // only show post-clear entries
```

### Dashboard File Index

| File | Purpose |
| --- | --- |
| [dashboard/main.js](dashboard/main.js) | Main process: IPC, file watch, bot spawn, log rotation |
| [dashboard/preload.js](dashboard/preload.js) | Context bridge — `window.api` |
| [dashboard/renderer.js](dashboard/renderer.js) | All UI logic |
| [dashboard/index.html](dashboard/index.html) | UI shell |
| [dashboard/styles.css](dashboard/styles.css) | Dark/light themes, tooltip styles (CSS variables, grid layout) |
| [dashboard/package.json](dashboard/package.json) | `electron ^33.0.0` devDependency |
| [run-dashboard.bat](run-dashboard.bat) | Windows launcher — uses cached Electron binary if installed |

### Dashboard Known Issues / Fixed Bugs

- **Path with spaces**: Bot root `my repos/polymarket_bot` contains a space. `shell: true` + exe path breaks on Windows CMD. Fixed by using `shell: false` for direct exe execution.
- **FileShare**: .NET `StreamWriter` default opens with `FileShare.None`, blocking dashboard reads. Fixed in `Program.cs:77` with `new FileStream(..., FileShare.ReadWrite)`.
- **Stale exe**: After .NET source changes, must `dotnet build -c Debug` from `dotnet/PolymarketBot/` before restarting. Dashboard runs the compiled exe directly; rebuilding requires killing the running process first.
- **7-decimal timestamps**: .NET `DateTime.ToString("o")` produces 7 fractional-second digits. Handled by `parseTs()` normalization in renderer.
- **`t` variable shadowing**: `refresh()` destructured `[p, t, l]` where `t` shadowed the global `t()` translation function, causing TypeError on every call (timestamp display never updated). Fixed by renaming to `[p, tr, l]`.
- **File watcher on Windows**: `fs.watch` sometimes delivers `name = null` (filename not provided). Added `name === null` fallback + 300ms debounce per file to prevent event flooding.
- **CSS tooltip clipping**: CSS `::after` pseudo-element tooltips are clipped by ancestor `overflow: hidden`. Fixed by using a JS-positioned `<div class="tooltip-popup">` appended to `<body>` with `position: fixed`.
