"""Bot configuration.

Priority (highest wins):
  1. Environment variables
  2. config.json  (project root, or path in CONFIG_FILE env var)
  3. Code defaults

config.json location: project root (polymarket_bot/config.json)
"""

import json
import os
from dataclasses import dataclass
from pathlib import Path


def _load_json() -> dict:
    """Load config.json. Returns empty dict if not found."""
    path = os.environ.get("CONFIG_FILE") or str(Path(__file__).parent.parent / "polymarket_bot_config.json")
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {}
    except Exception as e:
        import logging
        logging.getLogger("bot.config").warning(f"Could not load config.json: {e}")
        return {}


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
    min_trade_usd: float = 0.5

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

    # Endpoints / contracts (required — set via config.json or env vars)
    anthropic_api_host: str = ""
    gamma_api_host: str = ""
    clob_host: str = ""
    exchange_address: str = ""
    neg_risk_exchange_address: str = ""

    # Email notifications
    email_enabled: bool = False
    email_smtp_host: str = ""
    email_smtp_port: int = 587
    email_use_tls: bool = True
    email_user: str = ""
    email_password: str = ""
    email_to: str = ""

    # Persistence (shared between Python and .NET)
    data_dir: str = "../data"

    @classmethod
    def from_env(cls) -> "BotConfig":
        """Build config: env var > config.json > code default."""
        j = _load_json()

        def get(key: str, default):
            """Return env var (if set) → JSON value → code default."""
            env_val = os.environ.get(key.upper())
            if env_val is not None:
                # Coerce env string to the type of the default
                if isinstance(default, bool):
                    return env_val.lower() == "true"
                if isinstance(default, int):
                    return int(env_val)
                if isinstance(default, float):
                    return float(env_val)
                return env_val
            # JSON value is already typed
            if key in j:
                return j[key]
            return default

        return cls(
            live_trading=get("live_trading", False),
            scan_interval_minutes=get("scan_interval_minutes", 10),
            min_liquidity=get("min_liquidity", 5000.0),
            min_volume_24hr=get("min_volume_24hr", 1000.0),
            min_time_to_resolution_hours=get("min_time_to_resolution_hours", 24.0),
            min_market_price=get("min_market_price", 0.10),
            markets_per_cycle=get("markets_per_cycle", 30),
            claude_model=get("claude_model", "claude-sonnet-4-20250514"),
            ensemble_size=get("ensemble_size", 5),
            ensemble_temperature=get("ensemble_temperature", 0.7),
            kelly_fraction=get("kelly_fraction", 0.25),
            min_edge=get("min_edge", 0.08),
            min_trade_usd=get("min_trade_usd", 0.5),
            enable_position_review=get("enable_position_review", True),
            position_stop_loss_pct=get("position_stop_loss_pct", 0.30),
            take_profit_price=get("take_profit_price", 0.95),
            exit_edge_buffer=get("exit_edge_buffer", 0.05),
            review_reestimate_threshold_pct=get("review_reestimate_threshold_pct", 0.10),
            review_ensemble_size=get("review_ensemble_size", 3),
            max_position_pct=get("max_position_pct", 0.15),
            max_total_exposure_pct=get("max_total_exposure_pct", 1.00),
            max_category_exposure_pct=get("max_category_exposure_pct", 0.80),
            daily_stop_loss_pct=get("daily_stop_loss_pct", 0.20),
            max_drawdown_pct=get("max_drawdown_pct", 0.50),
            max_concurrent_positions=get("max_concurrent_positions", 20),
            initial_bankroll=get("initial_bankroll", 10000.0),
            anthropic_api_key=get("anthropic_api_key", ""),
            polymarket_private_key=get("polymarket_private_key", ""),
            polymarket_funder_address=get("polymarket_funder_address", ""),
            polymarket_chain_id=get("polymarket_chain_id", 137),
            polymarket_signature_type=get("polymarket_signature_type", 0),
            polymarket_api_key=get("polymarket_api_key", ""),
            polymarket_api_secret=get("polymarket_api_secret", ""),
            polymarket_api_passphrase=get("polymarket_api_passphrase", ""),
            anthropic_api_host=get("anthropic_api_host", ""),
            gamma_api_host=get("gamma_api_host", ""),
            clob_host=get("clob_host", ""),
            exchange_address=get("exchange_address", ""),
            neg_risk_exchange_address=get("neg_risk_exchange_address", ""),
            email_enabled=get("email_enabled", False),
            email_smtp_host=get("email_smtp_host", ""),
            email_smtp_port=get("email_smtp_port", 587),
            email_use_tls=get("email_use_tls", True),
            email_user=get("email_user", ""),
            email_password=get("email_password", ""),
            email_to=get("email_to", ""),
            data_dir=get("data_dir", "../data"),
        )
