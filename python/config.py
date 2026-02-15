"""Bot configuration loaded from environment variables with sensible defaults."""

import os
from dataclasses import dataclass


@dataclass
class BotConfig:
    # Mode
    live_trading: bool = False

    # Scan
    scan_interval_minutes: int = 10
    min_liquidity: float = 5000.0
    min_volume_24hr: float = 1000.0
    min_time_to_resolution_hours: float = 24.0
    markets_per_cycle: int = 30  # Cap on markets to evaluate per cycle

    # Estimation
    claude_model: str = "claude-sonnet-4-20250514"
    ensemble_size: int = 5
    ensemble_temperature: float = 0.7
    max_estimate_tokens: int = 1024

    # Sizing
    kelly_fraction: float = 0.25
    min_edge: float = 0.08  # 8 percentage points
    min_trade_usd: float = 1.0

    # Risk
    max_position_pct: float = 0.06  # Max 6% of bankroll per position
    max_total_exposure_pct: float = 0.50
    max_category_exposure_pct: float = 0.20
    daily_stop_loss_pct: float = 0.03
    max_drawdown_pct: float = 0.15
    max_concurrent_positions: int = 20

    # Capital
    initial_bankroll: float = 10000.0

    # API keys
    anthropic_api_key: str = ""
    polymarket_private_key: str = ""
    polymarket_funder_address: str = ""
    polymarket_chain_id: int = 137
    polymarket_signature_type: int = 0

    # Persistence
    data_dir: str = "data"

    @classmethod
    def from_env(cls) -> "BotConfig":
        """Build config from environment variables."""
        return cls(
            live_trading=os.getenv("LIVE_TRADING", "false").lower() == "true",
            scan_interval_minutes=int(os.getenv("SCAN_INTERVAL_MINUTES", "10")),
            min_liquidity=float(os.getenv("MIN_LIQUIDITY", "5000")),
            min_volume_24hr=float(os.getenv("MIN_VOLUME_24HR", "1000")),
            min_time_to_resolution_hours=float(os.getenv("MIN_TIME_TO_RESOLUTION_HOURS", "24")),
            markets_per_cycle=int(os.getenv("MARKETS_PER_CYCLE", "30")),
            claude_model=os.getenv("CLAUDE_MODEL", "claude-sonnet-4-20250514"),
            ensemble_size=int(os.getenv("ENSEMBLE_SIZE", "5")),
            ensemble_temperature=float(os.getenv("ENSEMBLE_TEMPERATURE", "0.7")),
            kelly_fraction=float(os.getenv("KELLY_FRACTION", "0.25")),
            min_edge=float(os.getenv("MIN_EDGE", "0.05")),
            max_position_pct=float(os.getenv("MAX_POSITION_PCT", "0.05")),
            max_total_exposure_pct=float(os.getenv("MAX_TOTAL_EXPOSURE_PCT", "0.50")),
            max_category_exposure_pct=float(os.getenv("MAX_CATEGORY_EXPOSURE_PCT", "0.20")),
            daily_stop_loss_pct=float(os.getenv("DAILY_STOP_LOSS_PCT", "0.03")),
            max_drawdown_pct=float(os.getenv("MAX_DRAWDOWN_PCT", "0.15")),
            max_concurrent_positions=int(os.getenv("MAX_CONCURRENT_POSITIONS", "20")),
            initial_bankroll=float(os.getenv("INITIAL_BANKROLL", "10000")),
            anthropic_api_key=os.getenv("ANTHROPIC_API_KEY", ""),
            polymarket_private_key=os.getenv("POLYMARKET_PRIVATE_KEY", ""),
            polymarket_funder_address=os.getenv("POLYMARKET_FUNDER_ADDRESS", ""),
            polymarket_chain_id=int(os.getenv("POLYMARKET_CHAIN_ID", "137")),
            polymarket_signature_type=int(os.getenv("POLYMARKET_SIGNATURE_TYPE", "0")),
            data_dir=os.getenv("DATA_DIR", "data"),
        )
