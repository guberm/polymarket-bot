"""Read-only aggregate Polymarket wallet-flow telemetry for offline research."""

from __future__ import annotations

import logging
import math
import time
from collections import defaultdict
from typing import Callable, Optional

import requests

from config import BotConfig
from models import MarketInfo

log = logging.getLogger("bot.wallet_flow_shadow")


class WalletFlowShadow:
    def __init__(self, config: BotConfig, now_fn: Callable[[], float] = time.time):
        self.config = config
        self._now_fn = now_fn

    def lookup(self, market: MarketInfo) -> Optional[dict]:
        """Fetch public trades and return anonymous market-level aggregates."""
        now = int(self._now_fn())
        window_minutes = max(1, self.config.wallet_flow_window_minutes)
        start = now - window_minutes * 60
        try:
            response = requests.get(
                f"{self.config.wallet_flow_api_host.rstrip('/')}/trades",
                params={
                    "market": market.condition_id,
                    "start": start,
                    "end": now,
                    "limit": max(1, min(self.config.wallet_flow_trades_limit, 10_000)),
                    "takerOnly": "true",
                },
                timeout=20,
            )
            response.raise_for_status()
            payload = response.json()
            trades = payload if isinstance(payload, list) else []
            result = self._aggregate(trades, market.condition_id, start, now, window_minutes)
            log.info(
                "Wallet-flow shadow: %d trades, $%.2f volume, imbalance=%+.2f",
                result["trade_count"], result["gross_volume_usd"], result["flow_imbalance"],
            )
            return result
        except Exception as exc:
            log.warning("Wallet-flow shadow lookup failed: %s", exc)
            return None

    def _aggregate(
        self, trades: list[dict], condition_id: str, start: int, end: int, window_minutes: int
    ) -> dict:
        yes_volume = 0.0
        no_volume = 0.0
        large_volume = 0.0
        trade_count = 0
        large_count = 0
        wallet_volume: dict[str, float] = defaultdict(float)
        threshold = max(0.0, self.config.wallet_flow_large_trade_usd)

        for trade in trades:
            if str(trade.get("conditionId", "")).lower() != condition_id.lower():
                continue
            timestamp = _number(trade.get("timestamp"))
            size = _number(trade.get("size"))
            price = _number(trade.get("price"))
            if timestamp < start or timestamp > end or size <= 0 or price <= 0:
                continue
            notional = size * price
            if not math.isfinite(notional) or notional <= 0:
                continue
            side = str(trade.get("side", "")).upper()
            outcome = str(trade.get("outcome", "")).upper()
            yes_direction = (outcome == "YES" and side == "BUY") or (outcome == "NO" and side == "SELL")
            no_direction = (outcome == "NO" and side == "BUY") or (outcome == "YES" and side == "SELL")
            if not yes_direction and not no_direction:
                continue

            trade_count += 1
            if yes_direction:
                yes_volume += notional
            else:
                no_volume += notional
            wallet = str(trade.get("proxyWallet", "")).strip()
            if wallet:
                wallet_volume[wallet] += notional
            if notional >= threshold:
                large_count += 1
                large_volume += notional

        gross = yes_volume + no_volume
        return {
            "window_minutes": window_minutes,
            "trade_count": trade_count,
            "wallet_count": len(wallet_volume),
            "gross_volume_usd": gross,
            "yes_direction_volume_usd": yes_volume,
            "no_direction_volume_usd": no_volume,
            "net_yes_flow_usd": yes_volume - no_volume,
            "flow_imbalance": (yes_volume - no_volume) / gross if gross else 0.0,
            "top_wallet_share": max(wallet_volume.values(), default=0.0) / gross if gross else 0.0,
            "large_trade_count": large_count,
            "large_trade_share": large_volume / gross if gross else 0.0,
            "observed_at": end,
        }


def _number(value) -> float:
    try:
        number = float(value or 0)
        return number if math.isfinite(number) else 0.0
    except (TypeError, ValueError):
        return 0.0
