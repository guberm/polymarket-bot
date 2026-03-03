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
    Estimator.cs           – Claude ensemble via Anthropic REST API (HttpClient); 429/529 retry with 10/20/40s backoff
    Portfolio.cs           – Kelly sizing, 5-layer risk, position review & exit signals, API cost tracking
    Notifier.cs            – Email notifications (System.Net.Mail); mirrors python/notifier.py
    ClobApiClient.cs       – CLOB API auth (EIP-712 signing, HMAC, API key derivation), buy & sell orders, auto-claim (CTF.redeemPositions on Polygon)
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
- **Risk is layered:** per-position (15%), per-category (80%), total exposure (100%), daily stop-loss (20%), max drawdown (50%)
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
- **Scan skip threshold** = `max(MinTradeUsd, MaxPositionPct × bankroll)` — based on free cash only, not portfolio value, to avoid false blocks when most capital is locked in positions. Default `MinTradeUsd = $0.50` (≈ CLOB minimum: 5 tokens)
- **.NET version** uses direct HttpClient calls to Anthropic REST API (no SDK dependency)
- **.NET CLOB auth** implements EIP-712 signing (ClobAuth struct for L1, Order struct for orders) + HMAC-SHA256 for L2, using Nethereum.Signer for Keccak/ECDSA
- **No hardcoded URLs or contract addresses** — all endpoints/contracts come from env vars
- **Auto-claim** (.NET only) — when a WON position is detected in the position review loop, `ClobApiClient.RedeemWinningPositionAsync()` submits a raw EIP-155 transaction to Polygon calling `CTF.redeemPositions(collateral, parentCollectionId, conditionId, indexSets)`. Calldata is ABI-encoded manually (196 bytes). Signing reuses existing `EthECKey` + Keccak — no new NuGet packages. Requires `ctf_address`, `usdc_address`, `polygon_rpc_url` in config. Controlled by `auto_claim` (default `true`)
- **Anthropic 429/529 retry** — `Estimator.SingleCallAsync` retries up to 3 times on rate-limit (429) or overload (529) errors, with exponential backoff 10s → 20s → 40s. After max retries returns null (market skipped)
- **SKIP log clarity** — `Program.cs` distinguishes two null-signal reasons: "SKIP (bankroll < min)" when edge IS sufficient but position size is below CLOB minimum (5 tokens), vs "SKIP (no edge)" when edge is genuinely below threshold. Console shows "TOO SMALL: need $X, have $Y" in the former case

## Dashboard (`dashboard/`)

Electron desktop app for real-time bot monitoring.

### Running

```bash
# Windows: double-click run-dashboard.bat
# Or:
cd dashboard && npm install && npm start
```

### Files

```text
dashboard/
  main.js       Main process: IPC handlers, file watchers, bot process management
  preload.js    Context bridge (exposes safe API to renderer)
  renderer.js   All UI logic: stats, tables, charts, log, config, resize handles
  index.html    UI shell: stats row, positions table, trade table, charts, log, modals
  styles.css    Dark theme CSS
  package.json  electron ^33.0.0 devDependency
```

### Key Patterns

- **Bot spawn**: Use `shell: false` when running a direct `.exe` path (avoids Windows CMD splitting paths at spaces). Use `shell: true` for `python` / `dotnet run` (PATH-based commands).
- **Log isolation**: `logClearedAt = Date.now()` at dashboard load → hides all pre-existing log entries. `confirmStart()` resets `logClearedAt = 0` + `logs = []` for a fresh session view.
- **Log file rotation**: Old `bot.log` renamed to `bot-TIMESTAMP.log` before each new bot start (in `main.js` start-bot handler).
- **Timestamp normalization**: `parseTs(ts)` helper strips extra fractional-second digits before `new Date()` — handles .NET's 7-decimal `ToString("o")` format reliably.
- **Charts**: Initialized once with `animation: false`; updated with `chart.update('none')` — prevents jumping/flickering on 8-second refresh.
- **FileShare fix (.NET)**: `bot.log` must be opened with `FileShare.ReadWrite` in `Program.cs` so both the bot and dashboard can access it simultaneously.
- **Stale exe**: After source changes to .NET, rebuild with `dotnet build -c Debug` from `dotnet/PolymarketBot/`. The dashboard prefers the compiled exe over `dotnet run` to avoid recompile locking.
- **File watcher**: `setupFileWatcher()` uses 300ms debounce per file + `name === null` fallback (Windows sometimes omits filename in `fs.watch` callback).
- **`t` variable bug**: `refresh()` must destructure as `[p, tr, l]` not `[p, t, l]` — `t` is the global translation function; shadowing it causes TypeError on every refresh call.
- **i18n system**: `TRANS = { ru: {}, en: {} }` + `t(key, ...args)` helper. `applyLang()` updates `[data-i18n]` elements via first text node (not `innerHTML`) to preserve child elements (tip icons, sort-ind spans). `currentLang` stored in `localStorage.lang`.
- **Tooltips**: `.tip-icon` spans with `data-tip-key`. `applyTips()` sets `data-tip` from `TRANS[lang].tips`. `initTooltips()` creates one `.tooltip-popup` div in `<body>` using `position: fixed` + `getBoundingClientRect()` — avoids `overflow: hidden` clipping that breaks CSS `::after` tooltips.
- **Theme**: `body.light` CSS class toggles light theme. `initTheme()` reads/writes `localStorage.theme`.

### IPC Channels

`read-portfolio`, `read-trades`, `read-logs`, `read-config`, `write-config`, `get-data-dir`, `set-data-dir`, `browse-data-dir`, `bot-status`, `start-bot`, `stop-bot`, `save-file`, `open-logs-dir`

Push events (main → renderer): `file-changed`, `bot-output`, `bot-stopped`
