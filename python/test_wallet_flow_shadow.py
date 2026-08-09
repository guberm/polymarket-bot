import json
import unittest
from unittest.mock import Mock, patch

from config import BotConfig
from models import MarketInfo
from wallet_flow_shadow import WalletFlowShadow


def market() -> MarketInfo:
    return MarketInfo(
        condition_id="0x" + "a" * 64,
        question="Test?",
        slug="test",
        outcome_yes_price=.5,
        outcome_no_price=.5,
        token_id_yes="yes",
        token_id_no="no",
        liquidity=10_000,
        volume=20_000,
        volume_24hr=2_000,
        best_bid=.49,
        best_ask=.51,
        spread=.02,
        end_date="2030-01-01T00:00:00Z",
        category="test",
        event_title="Test",
        description="",
    )


class WalletFlowShadowTests(unittest.TestCase):
    @patch("wallet_flow_shadow.requests.get")
    def test_aggregates_public_trades_without_exposing_wallets(self, get: Mock):
        condition_id = market().condition_id
        response = Mock()
        response.raise_for_status.return_value = None
        response.json.return_value = [
            {"conditionId": condition_id, "proxyWallet": "wallet-a", "side": "BUY", "outcome": "YES", "size": 10, "price": .6, "timestamp": 900},
            {"conditionId": condition_id, "proxyWallet": "wallet-a", "side": "BUY", "outcome": "NO", "size": 20, "price": .4, "timestamp": 901},
            {"conditionId": condition_id, "proxyWallet": "wallet-b", "side": "SELL", "outcome": "YES", "size": 5, "price": .5, "timestamp": 902},
            {"conditionId": condition_id, "proxyWallet": "wallet-c", "side": "SELL", "outcome": "NO", "size": 4, "price": .25, "timestamp": 903},
            {"conditionId": condition_id, "proxyWallet": "old-wallet", "side": "BUY", "outcome": "YES", "size": 100, "price": .5, "timestamp": 399},
            {"conditionId": "0x" + "b" * 64, "proxyWallet": "other-wallet", "side": "BUY", "outcome": "YES", "size": 100, "price": .5, "timestamp": 904},
        ]
        get.return_value = response
        config = BotConfig(
            wallet_flow_window_minutes=10,
            wallet_flow_trades_limit=123,
            wallet_flow_large_trade_usd=5,
        )

        result = WalletFlowShadow(config, now_fn=lambda: 1000).lookup(market())

        self.assertEqual(get.call_args.kwargs["params"], {
            "market": condition_id,
            "start": 400,
            "end": 1000,
            "limit": 123,
            "takerOnly": "true",
        })
        self.assertEqual(result["trade_count"], 4)
        self.assertEqual(result["wallet_count"], 3)
        self.assertAlmostEqual(result["gross_volume_usd"], 17.5)
        self.assertAlmostEqual(result["yes_direction_volume_usd"], 7)
        self.assertAlmostEqual(result["no_direction_volume_usd"], 10.5)
        self.assertAlmostEqual(result["net_yes_flow_usd"], -3.5)
        self.assertAlmostEqual(result["flow_imbalance"], -.2)
        self.assertAlmostEqual(result["top_wallet_share"], .8)
        self.assertEqual(result["large_trade_count"], 2)
        self.assertAlmostEqual(result["large_trade_share"], .8)
        serialized = json.dumps(result)
        self.assertNotIn("wallet-a", serialized)
        self.assertNotIn("proxyWallet", serialized)

    @patch("wallet_flow_shadow.requests.get", side_effect=RuntimeError("offline"))
    def test_network_failure_is_fail_open(self, _get: Mock):
        result = WalletFlowShadow(BotConfig(), now_fn=lambda: 1000).lookup(market())
        self.assertIsNone(result)


if __name__ == "__main__":
    unittest.main()
