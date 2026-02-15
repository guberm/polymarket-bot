"""Portfolio management, risk checks, and Kelly criterion position sizing."""

import logging
from typing import Optional

from config import BotConfig
from models import MarketInfo, Estimate, Signal, Side, Position, PortfolioSnapshot

log = logging.getLogger("bot.portfolio")

# Approximate Claude API pricing (per million tokens)
INPUT_COST_PER_MTOK = 3.0
OUTPUT_COST_PER_MTOK = 15.0


class Portfolio:
    def __init__(self, config: BotConfig, snapshot: Optional[PortfolioSnapshot] = None):
        self.config = config
        if snapshot:
            self.bankroll = snapshot.bankroll
            self.initial_bankroll = snapshot.initial_bankroll
            self.positions = list(snapshot.positions)
            self.high_water_mark = snapshot.high_water_mark
            self.daily_start_value = snapshot.daily_start_value
            self.total_realized_pnl = snapshot.total_realized_pnl
            self.total_trades = snapshot.total_trades
            self.is_halted = snapshot.is_halted
        else:
            self.bankroll = config.initial_bankroll
            self.initial_bankroll = config.initial_bankroll
            self.positions = []
            self.high_water_mark = config.initial_bankroll
            self.daily_start_value = config.initial_bankroll
            self.total_realized_pnl = 0.0
            self.total_trades = 0
            self.is_halted = False

        self.total_api_cost = 0.0

    def snapshot(self) -> PortfolioSnapshot:
        return PortfolioSnapshot(
            bankroll=self.bankroll,
            initial_bankroll=self.initial_bankroll,
            positions=list(self.positions),
            high_water_mark=self.high_water_mark,
            daily_start_value=self.daily_start_value,
            total_realized_pnl=self.total_realized_pnl,
            total_trades=self.total_trades,
            is_halted=self.is_halted,
        )

    def total_exposure(self) -> float:
        return sum(p.size_usd for p in self.positions)

    def category_exposure(self, category: str) -> float:
        return sum(p.size_usd for p in self.positions if p.category == category)

    def has_position(self, condition_id: str) -> bool:
        return any(p.condition_id == condition_id for p in self.positions)

    # ── Signal generation ─────────────────────────────────────────────

    def generate_signal(self, market: MarketInfo, estimate: Estimate) -> Optional[Signal]:
        """Compare estimate to market price, return Signal if edge exceeds threshold."""
        fair = estimate.fair_probability

        yes_edge = fair - market.outcome_yes_price
        no_edge = (1.0 - fair) - market.outcome_no_price

        if yes_edge > no_edge and yes_edge > self.config.min_edge:
            side = Side.YES
            edge = yes_edge
            market_price = market.outcome_yes_price
        elif no_edge > self.config.min_edge:
            side = Side.NO
            edge = no_edge
            market_price = market.outcome_no_price
        else:
            return None

        if market_price <= 0 or market_price >= 1:
            return None

        # Kelly criterion: f* = (b*p - q) / b
        b = (1.0 / market_price) - 1.0  # decimal odds
        p = fair if side == Side.YES else (1.0 - fair)
        q = 1.0 - p
        kelly_raw = (b * p - q) / b if b > 0 else 0.0
        kelly_raw = max(0.0, kelly_raw)

        # Fractional Kelly + position cap
        kelly = kelly_raw * self.config.kelly_fraction
        size_usd = kelly * self.bankroll
        size_usd = min(size_usd, self.bankroll * self.config.max_position_pct)

        if size_usd < self.config.min_trade_usd:
            return None

        return Signal(
            market=market,
            estimate=estimate,
            side=side,
            edge=edge,
            market_price=market_price,
            kelly_fraction=kelly,
            position_size_usd=round(size_usd, 2),
            expected_value=round(size_usd * edge, 4),
        )

    # ── Risk checks ───────────────────────────────────────────────────

    def check_risk(self, signal: Signal) -> bool:
        """Return True if the trade passes all risk limits."""
        if self.has_position(signal.market.condition_id):
            log.info(f"Risk BLOCK: already positioned in {signal.market.question[:40]}")
            return False

        if len(self.positions) >= self.config.max_concurrent_positions:
            log.info(f"Risk BLOCK: max positions ({self.config.max_concurrent_positions}) reached")
            return False

        new_exposure = self.total_exposure() + signal.position_size_usd
        max_allowed = self.bankroll * self.config.max_total_exposure_pct
        if new_exposure > max_allowed:
            log.info(f"Risk BLOCK: total exposure ${new_exposure:.2f} > limit ${max_allowed:.2f}")
            return False

        cat_exp = self.category_exposure(signal.market.category) + signal.position_size_usd
        cat_limit = self.bankroll * self.config.max_category_exposure_pct
        if cat_exp > cat_limit:
            log.info(f"Risk BLOCK: '{signal.market.category}' exposure ${cat_exp:.2f} > limit ${cat_limit:.2f}")
            return False

        # Daily stop loss (include open position value — deployed capital isn't lost)
        portfolio_value = self.bankroll + self.total_exposure()
        daily_pnl = portfolio_value - self.daily_start_value
        if daily_pnl < 0 and abs(daily_pnl) > self.daily_start_value * self.config.daily_stop_loss_pct:
            log.warning(f"HALT: Daily stop loss triggered (PnL=${daily_pnl:+.2f}, limit={self.config.daily_stop_loss_pct:.0%})")
            self.is_halted = True
            return False

        # Max drawdown from high water mark
        if self.high_water_mark > 0:
            drawdown = (self.high_water_mark - portfolio_value) / self.high_water_mark
            if drawdown > self.config.max_drawdown_pct:
                log.warning(f"HALT: Max drawdown {drawdown:.1%} exceeded (limit={self.config.max_drawdown_pct:.0%})")
                self.is_halted = True
                return False

        # Agent death condition
        if self.bankroll <= 0:
            log.warning("HALT: Bankroll depleted — agent is dead")
            self.is_halted = True
            return False

        return True

    # ── Position management ───────────────────────────────────────────

    def open_position(self, position: Position) -> None:
        """Deduct cost from bankroll and track position."""
        self.bankroll -= position.size_usd
        self.total_trades += 1
        self.positions.append(position)
        log.info(
            f"Opened {position.side.value} on {position.question[:40]}... "
            f"${position.size_usd:.2f} @ {position.entry_price:.3f}"
        )

    def close_position(self, condition_id: str, exit_price: float) -> float:
        """Close position, return cost basis + PnL to bankroll. Returns realized PnL."""
        pos = next((p for p in self.positions if p.condition_id == condition_id), None)
        if pos is None:
            return 0.0

        pnl = pos.shares * (exit_price - pos.entry_price)
        self.bankroll += pos.size_usd + pnl
        self.total_realized_pnl += pnl
        self.positions = [p for p in self.positions if p.condition_id != condition_id]
        self.high_water_mark = max(self.high_water_mark, self.bankroll)

        log.info(f"Closed {pos.question[:40]}... PnL: ${pnl:+.2f}")
        return pnl

    # ── Cost tracking ─────────────────────────────────────────────────

    def record_api_cost(self, input_tokens: int, output_tokens: int) -> None:
        """Deduct inference cost from bankroll. The agent pays for its own thinking."""
        cost = (input_tokens * INPUT_COST_PER_MTOK / 1_000_000) + \
               (output_tokens * OUTPUT_COST_PER_MTOK / 1_000_000)
        self.total_api_cost += cost
        self.bankroll -= cost

    def reset_daily(self) -> None:
        """Reset daily tracking. Call at the start of each new trading day."""
        self.daily_start_value = self.bankroll
