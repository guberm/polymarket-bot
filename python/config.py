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
    min_market_price: float = 0.10  # Skip markets priced below 10% (no FOK liquidity)
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
    max_position_pct: float = 0.15  # Max 15% of bankroll per position
    max_total_exposure_pct: float = 1.00  # Max 100% of bankroll in open positions
    max_category_exposure_pct: float = 0.80
    daily_stop_loss_pct: float = 0.20
    max_drawdown_pct: float = 0.50
    max_concurrent_positions: int = 20

    # Position review / exit
    enable_position_review: bool = True
    position_stop_loss_pct: float = 0.30       # Exit if position drops 30% from entry
    take_profit_price: float = 0.95            # Exit if price reaches 0.95 (near certain)
    exit_edge_buffer: float = 0.05             # Buffer above fair estimate before edge-gone exit
    review_reestimate_threshold_pct: float = 0.10  # Re-estimate if price moved >10%
    review_ensemble_size: int = 3              # Smaller ensemble for re-estimation

    # Capital
    initial_bankroll: float = 10000.0

    # API keys
    anthropic_api_key: str = ""
    polymarket_private_key: str = ""
    polymarket_funder_address: str = ""
    polymarket_chain_id: int = 137
    polymarket_signature_type: int = 0

    # CLOB API credentials (pre-generated, avoids deriving from private key)
    polymarket_api_key: str = ""
    polymarket_api_secret: str = ""
    polymarket_api_passphrase: str = ""

    # Endpoints / contracts (required — set via env vars)
    anthropic_api_host: str = ""
    gamma_api_host: str = ""
    clob_host: str = ""
    exchange_address: str = ""
    neg_risk_exchange_address: str = ""

    # Persistence (shared between Python and .NET)
    data_dir: str = "../data"

    @classmethod
    def from_env(cls) -> "BotConfig":
        """Build config from environment variables."""
        return cls(
            live_trading=os.getenv("LIVE_TRADING", "false").lower() == "true",
            scan_interval_minutes=int(os.getenv("SCAN_INTERVAL_MINUTES", "10")),
            min_liquidity=float(os.getenv("MIN_LIQUIDITY", "5000")),
            min_volume_24hr=float(os.getenv("MIN_VOLUME_24HR", "1000")),
            min_time_to_resolution_hours=float(os.getenv("MIN_TIME_TO_RESOLUTION_HOURS", "24")),
            min_market_price=float(os.getenv("MIN_MARKET_PRICE", "0.10")),
            markets_per_cycle=int(os.getenv("MARKETS_PER_CYCLE", "30")),
            claude_model=os.getenv("CLAUDE_MODEL", "claude-sonnet-4-20250514"),
            ensemble_size=int(os.getenv("ENSEMBLE_SIZE", "5")),
            ensemble_temperature=float(os.getenv("ENSEMBLE_TEMPERATURE", "0.7")),
            kelly_fraction=float(os.getenv("KELLY_FRACTION", "0.25")),
            min_edge=float(os.getenv("MIN_EDGE", "0.08")),
            max_position_pct=float(os.getenv("MAX_POSITION_PCT", "0.15")),
            max_total_exposure_pct=float(os.getenv("MAX_TOTAL_EXPOSURE_PCT", "1.00")),
            max_category_exposure_pct=float(os.getenv("MAX_CATEGORY_EXPOSURE_PCT", "0.80")),
            daily_stop_loss_pct=float(os.getenv("DAILY_STOP_LOSS_PCT", "0.20")),
            max_drawdown_pct=float(os.getenv("MAX_DRAWDOWN_PCT", "0.50")),
            max_concurrent_positions=int(os.getenv("MAX_CONCURRENT_POSITIONS", "20")),
            initial_bankroll=float(os.getenv("INITIAL_BANKROLL", "10000")),
            anthropic_api_key=os.getenv("ANTHROPIC_API_KEY", ""),
            polymarket_private_key=os.getenv("POLYMARKET_PRIVATE_KEY", ""),
            polymarket_funder_address=os.getenv("POLYMARKET_FUNDER_ADDRESS", ""),
            polymarket_chain_id=int(os.getenv("POLYMARKET_CHAIN_ID", "137")),
            polymarket_signature_type=int(os.getenv("POLYMARKET_SIGNATURE_TYPE", "0")),
            polymarket_api_key=os.getenv("POLYMARKET_API_KEY", ""),
            polymarket_api_secret=os.getenv("POLYMARKET_API_SECRET", ""),
            polymarket_api_passphrase=os.getenv("POLYMARKET_API_PASSPHRASE", ""),
            min_trade_usd=float(os.getenv("MIN_TRADE_USD", "1.0")),
            enable_position_review=os.getenv("ENABLE_POSITION_REVIEW", "true").lower() == "true",
            position_stop_loss_pct=float(os.getenv("POSITION_STOP_LOSS_PCT", "0.30")),
            take_profit_price=float(os.getenv("TAKE_PROFIT_PRICE", "0.95")),
            exit_edge_buffer=float(os.getenv("EXIT_EDGE_BUFFER", "0.05")),
            review_reestimate_threshold_pct=float(os.getenv("REVIEW_REESTIMATE_THRESHOLD_PCT", "0.10")),
            review_ensemble_size=int(os.getenv("REVIEW_ENSEMBLE_SIZE", "3")),
            anthropic_api_host=os.getenv("ANTHROPIC_API_HOST", ""),
            gamma_api_host=os.getenv("GAMMA_API_HOST", ""),
            clob_host=os.getenv("CLOB_HOST", ""),
            exchange_address=os.getenv("EXCHANGE_ADDRESS", ""),
            neg_risk_exchange_address=os.getenv("NEG_RISK_EXCHANGE_ADDRESS", ""),
            data_dir=os.getenv("DATA_DIR", "../data"),
        )
