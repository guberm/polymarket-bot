# Bot Memory

Running notes between Claude Code sessions. Not a changelog — just current state, known issues, and context that's useful to restore quickly.

---

## Current State (as of 2026-02-25)

- **Mode:** LIVE on Polygon (chain ID 137)
- **Wallet:** see `polymarket_bot_config.json` (`polymarket_funder_address`)
- **Bankroll:** ~$0.30 free USDC + ~$21.58 locked in 6 open positions
- **Portfolio value:** ~$21.88
- **Active implementation:** .NET (run via `run-bot.bat` or `dotnet run -- --console`)

### Open Positions (~6 remaining)

Some are **penny positions** (price < $0.01) — effectively worthless, unsellable on CLOB, waiting for resolution.

Most recent trade: BUY YES "Will US or Israel strike Iran by Feb 28, 2026?" — $1.23 at $0.12/share (10.21 shares), 23.5% edge, filled on-chain.

Bot is currently idle: `SCAN SKIP: bankroll $0.30 < min $0.50`. Waiting for a position to resolve or more USDC deposited before resuming.

---

## Recent Fixes (2026-02-25 session)

### 1. Scan threshold inflation fix

**Problem:** Bot was blocked from scanning with `EXPOSURE FULL: room=$1.52 < $1.64`. The formula used portfolio value (`MaxPositionPct × pv × 0.5`) which inflated the threshold when most capital was locked in positions.

**Fix:** Changed `Program.cs` threshold to `Math.Max(config.MinTradeUsd, config.MaxPositionPct * portfolio.Bankroll)` — based on free cash only.

**Files:** `dotnet/PolymarketBot/Program.cs`

---

### 2. Auto-claim: on-chain CTF.redeemPositions

**Problem:** After a market resolves (WON), the user had to manually click "Claim" on polymarket.com. USDC wouldn't return to the wallet until claimed.

**Implementation:**
- `BotConfig.cs`: added `AutoClaim` (default `true`), `PolygonRpcUrl`, `CtfAddress`, `UsdcAddress`
- `ClobApiClient.cs`: added `RedeemWinningPositionAsync()` — submits a raw EIP-155 transaction to Polygon calling `CTF.redeemPositions(collateral, parentCollectionId, conditionId, indexSets)`
  - ABI-encodes calldata manually (196 bytes)
  - Signs with existing `EthECKey` + `Keccak` (no new NuGet dependencies)
  - Broadcasts via JSON-RPC 2.0 (`eth_sendRawTransaction`) to `polygon_rpc_url`
- `Program.cs`: calls `RedeemWinningPositionAsync` in the Tier 0 position review loop when a WON position is detected

**Config required** (`polymarket_bot_config.json`):
```json
"auto_claim": true,
"ctf_address":    "0x4D97DCd97eC945f40cF65F87097ACe5EA0476045",
"usdc_address":   "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174",
"polygon_rpc_url": "https://polygon-rpc.com"
```

**Files:** `dotnet/PolymarketBot/BotConfig.cs`, `dotnet/PolymarketBot/Services/ClobApiClient.cs`, `dotnet/PolymarketBot/Program.cs`

---

### 3. Misleading "SKIP (no edge)" log message fixed

**Problem:** `Portfolio.GenerateSignal()` returns null for two different reasons — edge below threshold AND position size below CLOB minimum (5 tokens). Both showed as "SKIP (no edge)" in the log even when edge was +48%.

**Fix:**
- `Program.cs`: after `GenerateSignal() == null`, checks `bestEdge > config.MinEdge` separately:
  - If edge IS sufficient but size is too small → logs "SKIP (bankroll $X < min $Y)" and console "TOO SMALL: need $X, have $Y"
  - If edge is genuinely below threshold → logs "SKIP (no edge)"
- `Portfolio.cs`: upgraded CLOB minimum log from `LogDebug` → `LogInformation` so it appears in normal runs

**Files:** `dotnet/PolymarketBot/Program.cs`, `dotnet/PolymarketBot/Services/Portfolio.cs`

---

### 4. Anthropic 529 retry with exponential backoff

**Problem:** Anthropic 529 (overloaded) errors hit `EnsureSuccessStatusCode()` in the estimator, threw `HttpRequestException`, returned null immediately with no retry — causing entire market estimations to fail silently.

**Fix:** Added retry loop in `Estimator.cs` (up to 3 attempts) for both 429 and 529:
- Backoff: 10s → 20s → 40s
- Warning log per attempt: "Anthropic 529 (attempt 1/3) — retrying in 10s"
- After max retries: logs error and returns null

**Files:** `dotnet/PolymarketBot/Services/Estimator.cs`

---

## Known Issues / Watchlist

- **Penny positions**: priced < $0.01, will never recover. They'll either resolve (returning some fraction) or expire worthless. No action needed — penny filter skips them correctly.
- **Low free bankroll**: Bot won't open new positions until existing ones resolve/exit and USDC returns (or user deposits more).
- **Auto-claim not yet tested in production**: Implemented and built successfully; will fire next time a WON position is detected. Requires `ctf_address` + `usdc_address` in config.
- **Python implementation not updated**: The fixes above are .NET only. The Python version still has the old scan threshold logic and no auto-claim.

---

## Architecture Reminders

- Config priority: CLI arg → env var → `polymarket_bot_config.json` → code default
- `polymarket_bot_config.json` is gitignored (contains private key + API keys)
- Kelly sizing caps at `min(KellyFraction × Kelly%, MaxPositionPct × portfolioValue)`, then checked against `bankroll` (can't spend what you don't have)
- Balance sync happens every cycle: on-chain USDC is fetched and bankroll is adjusted both up (resolved positions) and down (fees/failed orders)
- `IsHalted` is cleared on restart if `bankroll + TotalExposure() > $1` — transient halts don't stick across restarts
- Scan skip threshold = `max(MinTradeUsd, MaxPositionPct × bankroll)` — free cash only, not portfolio value

---

## Config Defaults (code-level)

| Setting | Default |
|---------|---------|
| `min_trade_usd` | `0.5` |
| `kelly_fraction` | `0.25` |
| `min_edge` | `8%` |
| `max_position_pct` | `15%` |
| `max_total_exposure_pct` | `100%` |
| `max_category_exposure_pct` | `80%` |
| `daily_stop_loss_pct` | `20%` |
| `max_drawdown_pct` | `50%` |
| `position_stop_loss_pct` | `30%` |
| `take_profit_price` | `0.95` |
| `ensemble_size` | `5` |
| `scan_interval_minutes` | `10` |
| `auto_claim` | `true` |
| `polygon_rpc_url` | `https://polygon-rpc.com` |
