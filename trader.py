"""Trade execution — paper trading and live Polymarket CLOB orders."""

import logging
import time
from typing import Optional
from uuid import uuid4

from config import BotConfig
from models import Signal, Trade, Position, Side, TradeAction
from portfolio import Portfolio

log = logging.getLogger("bot.trader")


class PaperTrader:
    """Simulated execution — deducts from bankroll, tracks positions, no real orders."""

    def execute(self, signal: Signal, portfolio: Portfolio) -> Optional[Trade]:
        market = signal.market
        price = signal.market_price
        size_usd = signal.position_size_usd
        shares = size_usd / price if price > 0 else 0.0
        token_id = market.token_id_yes if signal.side == Side.YES else market.token_id_no

        position = Position(
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            token_id=token_id,
            entry_price=price,
            size_usd=size_usd,
            shares=shares,
            current_price=price,
            unrealized_pnl=0.0,
            category=market.category,
        )
        portfolio.open_position(position)

        return Trade(
            trade_id=str(uuid4()),
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            action=TradeAction.BUY,
            price=price,
            size_usd=size_usd,
            shares=shares,
            timestamp=time.time(),
            is_paper=True,
            rationale=signal.estimate.reasoning_summary,
            edge_at_entry=signal.edge,
            kelly_at_entry=signal.kelly_fraction,
        )


class LiveTrader:
    """Real execution via Polymarket CLOB API using py-clob-client."""

    def __init__(self, config: BotConfig):
        try:
            from py_clob_client.client import ClobClient
        except ImportError:
            raise RuntimeError(
                "Live trading requires py-clob-client: pip install py-clob-client"
            )

        self.client = ClobClient(
            "https://clob.polymarket.com",
            key=config.polymarket_private_key,
            chain_id=config.polymarket_chain_id,
            signature_type=config.polymarket_signature_type,
            funder=config.polymarket_funder_address or None,
        )
        self.client.set_api_creds(self.client.create_or_derive_api_creds())
        log.info("Live CLOB client initialized")

    def execute(self, signal: Signal, portfolio: Portfolio) -> Optional[Trade]:
        from py_clob_client.clob_types import MarketOrderArgs, OrderType
        from py_clob_client.order_builder.constants import BUY

        market = signal.market
        price = signal.market_price
        size_usd = signal.position_size_usd
        token_id = market.token_id_yes if signal.side == Side.YES else market.token_id_no

        try:
            order_args = MarketOrderArgs(
                token_id=token_id,
                amount=size_usd,
                side=BUY,
                order_type=OrderType.FOK,
            )
            signed_order = self.client.create_market_order(order_args)
            resp = self.client.post_order(signed_order, OrderType.FOK)

            order_id = resp.get("orderID") or resp.get("id") or str(uuid4())
            log.info(f"CLOB order placed: {order_id}")

        except Exception as e:
            log.error(f"CLOB order failed: {e}")
            return None

        shares = size_usd / price if price > 0 else 0.0

        position = Position(
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            token_id=token_id,
            entry_price=price,
            size_usd=size_usd,
            shares=shares,
            current_price=price,
            unrealized_pnl=0.0,
            category=market.category,
            order_id=order_id,
        )
        portfolio.open_position(position)

        return Trade(
            trade_id=str(uuid4()),
            condition_id=market.condition_id,
            question=market.question,
            side=signal.side,
            action=TradeAction.BUY,
            price=price,
            size_usd=size_usd,
            shares=shares,
            timestamp=time.time(),
            order_id=order_id,
            is_paper=False,
            rationale=signal.estimate.reasoning_summary,
            edge_at_entry=signal.edge,
            kelly_at_entry=signal.kelly_fraction,
        )
