"""Main orchestration loop for the Polymarket trading bot.

Usage:
    python main.py              # paper trading (default)
    python main.py --verbose    # debug logging
    python main.py --console    # human-readable CLI prints for each step
    LIVE_TRADING=true python main.py   # real orders (requires funded wallet)
"""

import argparse
import logging
import signal
import sys
import time
from datetime import datetime, timezone

from config import BotConfig
from logger_setup import setup_logging
from market_scanner import MarketScanner
from estimator import Estimator
from portfolio import Portfolio
from trader import PaperTrader, LiveTrader
from persistence import load_snapshot, save_snapshot, append_trade

log = logging.getLogger("bot.main")


def ts():
    """Current timestamp for console prints."""
    return datetime.now().strftime("%H:%M:%S")


def main():
    parser = argparse.ArgumentParser(description="Polymarket trading bot")
    parser.add_argument("--verbose", "-v", action="store_true", help="Debug logging")
    parser.add_argument("--console", "-c", action="store_true", help="Human-readable CLI prints for each step")
    parser.add_argument("--max-position-pct", type=float, help="Max %% of bankroll per position (e.g. 0.15)")
    parser.add_argument("--max-total-exposure-pct", type=float, help="Max %% of bankroll in open positions (e.g. 0.90)")
    parser.add_argument("--max-category-exposure-pct", type=float, help="Max %% per category (e.g. 0.50)")
    parser.add_argument("--daily-stop-loss-pct", type=float, help="Halt if daily loss exceeds this %% (e.g. 0.20)")
    parser.add_argument("--max-drawdown-pct", type=float, help="Halt if drawdown exceeds this %% (e.g. 0.50)")
    parser.add_argument("--max-concurrent-positions", type=int, help="Max open positions (e.g. 20)")
    args = parser.parse_args()

    config = BotConfig.from_env()
    con = args.console  # shorthand for console flag

    # CLI args override env vars
    if args.max_position_pct is not None:
        config.max_position_pct = args.max_position_pct
    if args.max_total_exposure_pct is not None:
        config.max_total_exposure_pct = args.max_total_exposure_pct
    if args.max_category_exposure_pct is not None:
        config.max_category_exposure_pct = args.max_category_exposure_pct
    if args.daily_stop_loss_pct is not None:
        config.daily_stop_loss_pct = args.daily_stop_loss_pct
    if args.max_drawdown_pct is not None:
        config.max_drawdown_pct = args.max_drawdown_pct
    if args.max_concurrent_positions is not None:
        config.max_concurrent_positions = args.max_concurrent_positions
    setup_logging(config.data_dir, verbose=args.verbose)

    mode = "LIVE" if config.live_trading else "PAPER"
    log.info("=" * 60)
    log.info("Polymarket Bot")
    log.info(f"Mode: {mode} | Bankroll: ${config.initial_bankroll:.2f}")
    log.info(f"Min edge: {config.min_edge:.0%} | Max position: {config.max_position_pct:.0%}")
    log.info(f"Scan interval: {config.scan_interval_minutes} min | Markets/cycle: {config.markets_per_cycle}")
    log.info(f"Ensemble: {config.ensemble_size}x {config.claude_model}")
    log.info("=" * 60)

    if con:
        print(f"\n{'='*60}")
        print(f"  POLYMARKET BOT — {mode} MODE")
        print(f"  Bankroll: ${config.initial_bankroll:.2f} | Min edge: {config.min_edge:.0%}")
        print(f"  Risk: {config.max_position_pct:.0%}/pos, {config.max_total_exposure_pct:.0%}/total, {config.daily_stop_loss_pct:.0%}/daily-SL")
        print(f"  Scan: every {config.scan_interval_minutes}min, {config.markets_per_cycle} markets/cycle")
        print(f"{'='*60}\n")

    if not config.anthropic_api_key:
        log.error("ANTHROPIC_API_KEY not set")
        sys.exit(1)

    # Load saved state or start fresh
    snapshot = load_snapshot(config.data_dir)
    portfolio = Portfolio(config, snapshot)
    if snapshot:
        log.info(f"Resumed from saved state: ${portfolio.bankroll:.2f} bankroll, {len(portfolio.positions)} positions")
        if con:
            print(f"[{ts()}] RESUME: ${portfolio.bankroll:.2f} bankroll, {len(portfolio.positions)} positions, ${portfolio.total_exposure():.2f} exposure")
    else:
        log.info("Starting fresh")
        if con:
            print(f"[{ts()}] START: fresh portfolio, ${portfolio.bankroll:.2f} bankroll")

    scanner = MarketScanner(config)
    estimator = Estimator(config)

    if config.live_trading:
        if not config.polymarket_private_key and not config.polymarket_api_key:
            log.error("POLYMARKET_PRIVATE_KEY or POLYMARKET_API_KEY required for live trading")
            sys.exit(1)
        trader = LiveTrader(config)
    else:
        trader = PaperTrader()

    # Graceful shutdown on Ctrl+C
    running = True

    def handle_shutdown(sig, frame):
        nonlocal running
        log.info("Shutdown requested...")
        if con:
            print(f"\n[{ts()}] SHUTDOWN requested (Ctrl+C)")
        running = False

    signal.signal(signal.SIGINT, handle_shutdown)

    last_daily_reset = datetime.now(timezone.utc).date()
    cycle = 0

    while running:
        cycle += 1

        if portfolio.is_halted:
            log.warning("Portfolio halted — stopping")
            if con:
                print(f"[{ts()}] HALTED: portfolio risk limit reached, stopping bot")
            break

        # Daily reset check
        today = datetime.now(timezone.utc).date()
        if today != last_daily_reset:
            portfolio.reset_daily()
            last_daily_reset = today
            log.info(f"New day — daily start value reset to ${portfolio.bankroll:.2f}")
            if con:
                print(f"[{ts()}] NEW DAY: daily PnL reset, start=${portfolio.bankroll:.2f}")

        log.info(f"--- Cycle {cycle} ---")
        pv = portfolio.bankroll + portfolio.total_exposure()
        log.info(
            f"Portfolio: ${pv:.2f} "
            f"(bankroll=${portfolio.bankroll:.2f} + exposure=${portfolio.total_exposure():.2f}) | "
            f"{len(portfolio.positions)} positions"
        )

        if con:
            pv = portfolio.bankroll + portfolio.total_exposure()
            print(f"\n{'─'*60}")
            print(f"[{ts()}] CYCLE {cycle}")
            print(f"  Portfolio: ${pv:.2f} (bankroll=${portfolio.bankroll:.2f} + exposure=${portfolio.total_exposure():.2f})")
            print(f"  Positions: {len(portfolio.positions)} | API cost: ${portfolio.total_api_cost:.4f}")
            print(f"{'─'*60}")

        try:
            log.info("Scanning markets...")
            if con:
                print(f"[{ts()}] SCAN: fetching markets...")
            markets = scanner.scan()
            eligible = markets[:config.markets_per_cycle]
            trades_this_cycle = 0

            if con:
                print(f"[{ts()}] SCAN: {len(markets)} total, evaluating top {len(eligible)}")

            for i, market in enumerate(eligible, 1):
                if not running or portfolio.is_halted:
                    break

                # Skip markets we already hold
                if portfolio.has_position(market.condition_id):
                    log.info(f"  [{i}/{len(eligible)}] SKIP (already held): {market.question[:60]}")
                    if con:
                        print(f"[{ts()}]   [{i:>2}/{len(eligible)}] SKIP (held): {market.question[:55]}")
                    continue

                # Estimate fair value
                log.info(f"  [{i}/{len(eligible)}] Evaluating: {market.question[:60]}...")
                if con:
                    print(f"[{ts()}]   [{i:>2}/{len(eligible)}] EVAL: {market.question[:55]}...", end="", flush=True)
                estimate = estimator.estimate(market)
                if estimate is None:
                    log.info(f"  [{i}/{len(eligible)}] SKIP (estimation failed)")
                    if con:
                        print(" -> FAILED")
                    continue

                # Agent pays for its own inference
                portfolio.record_api_cost(estimate.input_tokens_used, estimate.output_tokens_used)

                if portfolio.bankroll <= 0:
                    log.warning("Bankroll depleted by API costs — agent is dead")
                    if con:
                        print(f"\n[{ts()}] DEAD: bankroll depleted by API costs")
                    portfolio.is_halted = True
                    break

                # Generate signal
                signal_obj = portfolio.generate_signal(market, estimate)
                if signal_obj is None:
                    yes_edge = estimate.fair_probability - market.outcome_yes_price
                    no_edge = (1.0 - estimate.fair_probability) - market.outcome_no_price
                    best_edge = max(yes_edge, no_edge)
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (no edge): "
                        f"fair={estimate.fair_probability:.1%} vs market={market.outcome_yes_price:.1%} "
                        f"(edge={best_edge:+.1%}, need>{config.min_edge:.0%})"
                    )
                    if con:
                        print(f" -> {estimate.fair_probability:.0%} (edge={best_edge:+.1%}) SKIP")
                    continue

                # Risk check
                if not portfolio.check_risk(signal_obj):
                    log.info(
                        f"  [{i}/{len(eligible)}] SKIP (risk limit): "
                        f"{signal_obj.side.value} {market.question[:40]}... "
                        f"${signal_obj.position_size_usd:.2f}"
                    )
                    if con:
                        print(f" -> {estimate.fair_probability:.0%} RISK BLOCKED")
                    continue

                # Execute
                log.info(
                    f"  [{i}/{len(eligible)}] >>> BUYING {signal_obj.side.value} "
                    f"{market.question[:50]}... ${signal_obj.position_size_usd:.2f} @ {signal_obj.market_price:.3f}"
                )
                if con:
                    print(f" -> {estimate.fair_probability:.0%} edge={signal_obj.edge:.1%}")
                    print(f"[{ts()}]   [{i:>2}/{len(eligible)}] >>> BUY {signal_obj.side.value} ${signal_obj.position_size_usd:.2f} @ {signal_obj.market_price:.3f}...", end="", flush=True)

                trade = trader.execute(signal_obj, portfolio)
                if trade:
                    append_trade(trade, config.data_dir)
                    save_snapshot(portfolio.snapshot(), config.data_dir)
                    trades_this_cycle += 1

                    log.info(
                        f"  [{i}/{len(eligible)}] TRADE OK: {trade.side.value} {market.question[:50]}... "
                        f"${trade.size_usd:.2f} @ {trade.price:.3f} "
                        f"(edge={signal_obj.edge:.1%}, EV=${signal_obj.expected_value:.2f})"
                    )
                    if con:
                        print(f" OK (EV=${signal_obj.expected_value:.2f})")
                else:
                    log.warning(f"  [{i}/{len(eligible)}] TRADE FAILED: order execution error")
                    if con:
                        print(f" FAILED")

            # Cycle summary
            log.info(
                f"Cycle {cycle}: {trades_this_cycle} trades | "
                f"Bankroll: ${portfolio.bankroll:.2f} | "
                f"Positions: {len(portfolio.positions)} | "
                f"Exposure: ${portfolio.total_exposure():.2f} | "
                f"API cost: ${portfolio.total_api_cost:.4f} | "
                f"Realized PnL: ${portfolio.total_realized_pnl:+.2f}"
            )

            if con:
                pv = portfolio.bankroll + portfolio.total_exposure()
                print(f"\n[{ts()}] SUMMARY: {trades_this_cycle} trades this cycle")
                print(f"  Portfolio: ${pv:.2f} | Bankroll: ${portfolio.bankroll:.2f} | Exposure: ${portfolio.total_exposure():.2f}")
                print(f"  Positions: {len(portfolio.positions)} | API cost: ${portfolio.total_api_cost:.4f} | PnL: ${portfolio.total_realized_pnl:+.2f}")

            save_snapshot(portfolio.snapshot(), config.data_dir)

        except Exception as e:
            log.error(f"Cycle {cycle} error: {e}", exc_info=True)
            if con:
                print(f"\n[{ts()}] ERROR: {e}")

        # Sleep in 1-second ticks for responsive shutdown
        if running:
            log.info(f"Next scan in {config.scan_interval_minutes} min")
            if con:
                print(f"[{ts()}] WAIT: sleeping {config.scan_interval_minutes} min...")
            for _ in range(config.scan_interval_minutes * 60):
                if not running:
                    break
                time.sleep(1)

    # Final save
    save_snapshot(portfolio.snapshot(), config.data_dir)
    log.info(
        f"Bot stopped | Final bankroll: ${portfolio.bankroll:.2f} | "
        f"Total trades: {portfolio.total_trades} | "
        f"Total API cost: ${portfolio.total_api_cost:.4f} | "
        f"Realized PnL: ${portfolio.total_realized_pnl:+.2f}"
    )
    if con:
        pv = portfolio.bankroll + portfolio.total_exposure()
        print(f"\n{'='*60}")
        print(f"[{ts()}] BOT STOPPED")
        print(f"  Final portfolio: ${pv:.2f} | Bankroll: ${portfolio.bankroll:.2f}")
        print(f"  Total trades: {portfolio.total_trades} | API cost: ${portfolio.total_api_cost:.4f}")
        print(f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}")
        print(f"{'='*60}")


if __name__ == "__main__":
    main()
