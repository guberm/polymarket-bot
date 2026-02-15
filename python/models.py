"""Shared data models for the Polymarket trading bot."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional
import time


class Side(Enum):
    YES = "YES"
    NO = "NO"


class TradeAction(Enum):
    BUY = "BUY"
    SELL = "SELL"


@dataclass
class MarketInfo:
    """A single binary market from the Gamma API."""
    condition_id: str
    question: str
    slug: str
    outcome_yes_price: float
    outcome_no_price: float
    token_id_yes: str
    token_id_no: str
    liquidity: float
    volume: float
    volume_24hr: float
    best_bid: float
    best_ask: float
    spread: float
    end_date: str  # ISO 8601
    category: str
    event_title: str
    description: str


@dataclass
class Estimate:
    """Result of Claude ensemble probability estimation."""
    market_condition_id: str
    question: str
    fair_probability: float  # Trimmed mean of ensemble
    raw_estimates: list[float]
    confidence: float  # Std dev (lower = more confident)
    reasoning_summary: str
    timestamp: float = field(default_factory=time.time)
    input_tokens_used: int = 0
    output_tokens_used: int = 0


@dataclass
class Signal:
    """A trading signal after comparing estimate to market price."""
    market: MarketInfo
    estimate: Estimate
    side: Side
    edge: float
    market_price: float  # Price we'd pay for the chosen side
    kelly_fraction: float  # Raw Kelly fraction
    position_size_usd: float
    expected_value: float


@dataclass
class Position:
    """An open position in the portfolio."""
    condition_id: str
    question: str
    side: Side
    token_id: str
    entry_price: float
    size_usd: float  # Cost basis
    shares: float
    current_price: float
    unrealized_pnl: float
    category: str
    opened_at: float = field(default_factory=time.time)
    order_id: Optional[str] = None


@dataclass
class Trade:
    """A completed trade record."""
    trade_id: str  # UUID
    condition_id: str
    question: str
    side: Side
    action: TradeAction
    price: float
    size_usd: float
    shares: float
    timestamp: float
    order_id: Optional[str] = None
    is_paper: bool = True
    rationale: str = ""
    edge_at_entry: float = 0.0
    kelly_at_entry: float = 0.0


@dataclass
class PortfolioSnapshot:
    """Complete portfolio state for persistence."""
    bankroll: float
    initial_bankroll: float
    positions: list[Position]
    high_water_mark: float
    daily_start_value: float
    total_realized_pnl: float
    total_trades: int
    is_halted: bool
    last_updated: float = field(default_factory=time.time)
