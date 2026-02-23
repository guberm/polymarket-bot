# Bot Memory

Running notes between Claude Code sessions. Not a changelog — just current state, known issues, and context that's useful to restore quickly.

---

## Current State (as of 2026-02-22)

- **Mode:** LIVE on Polygon (chain ID 137)
- **Wallet:** `0x6e1e4f95258f110d2498e3a2a0c9fed3375bd2d9`
- **Bankroll:** ~$0.86 free USDC + ~$23 locked in 7 open positions
- **Portfolio value:** ~$24
- **Cycle count:** ~474+ (running continuously)
- **Active implementation:** .NET (run via `run-bot.bat` or `dotnet run -- --console`)

### Open Positions (~7 remaining)

4 of the 7 are **penny positions** (price < $0.01) — effectively worthless, unsellable on CLOB, waiting for resolution. The other 3 have real value and will be reviewed for exits each cycle.

The Bitcoin/$75k February position was stop-loss sold in cycle ~474 after a 504 timeout on the prior cycle's attempt.

---

## Recent Fixes (this session)

### Scan threshold inflation fix (2026-02-22)

**Problem:** With $0.86 free bankroll and $25 in locked positions, scan was blocked at `min ~$1.94`. The formula was `MaxPositionPct × (bankroll + exposure) × 0.5`, which inflates the threshold when most capital is locked.

**Fix:** Changed formula to `max(MinTradeUsd, MaxPositionPct × bankroll)` — based on free cash only.

Also lowered `min_trade_usd` default from `$1.00` to `$0.50` everywhere:
- `python/config.py` (dataclass default + config loader default)
- `dotnet/PolymarketBot/BotConfig.cs` (config loader default)
- `polymarket_bot_config.json` (line 32)

**Files changed:** `python/main.py`, `python/config.py`, `dotnet/PolymarketBot/Program.cs`, `dotnet/PolymarketBot/BotConfig.cs`, `polymarket_bot_config.json`

**Result:** With $0.86 bankroll, threshold is now `max($0.50, 0.15 × $0.86) = $0.50`. Bot scans normally. Scan ran, found 507 markets. Estimation skipped (correct — no room for new positions).

---

## Known Issues / Watchlist

- **4 penny positions**: priced < $0.01, will never recover. They'll either resolve (returning some fraction) or expire worthless. No action needed.
- **CLOB 504 timeouts on SELL**: Happens intermittently. No retry logic — the next cycle will re-attempt. The failed Bitcoin sell eventually succeeded on retry.
- **Low free bankroll**: Bot won't open new positions until existing ones resolve/exit and USDC returns. Secondary "exposure near limit" check (`MaxPositionPct × portfolioValue × 0.5`) correctly blocks estimation when room is < minimum position size.

---

## Architecture Reminders

- Config priority: CLI arg → env var → `polymarket_bot_config.json` → code default
- `polymarket_bot_config.json` is gitignored (contains private key + API keys)
- Kelly sizing caps at `min(KellyFraction × Kelly%, MaxPositionPct × portfolioValue)`, then checked against `bankroll` (can't spend what you don't have)
- Balance sync happens every cycle: on-chain USDC is fetched and bankroll is adjusted both up (resolved positions) and down (fees/failed orders)
- `IsHalted` is cleared on restart if `bankroll + TotalExposure() > $1` — transient halts don't stick across restarts

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
