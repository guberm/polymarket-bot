import unittest
from datetime import date

from models import MarketInfo
from weather_estimator import WeatherEstimator, parse_weather_market, probability_for_range


class WeatherEstimatorTests(unittest.TestCase):
    def market(self, question: str) -> MarketInfo:
        return MarketInfo(
            condition_id="weather-1",
            question=question,
            slug="weather",
            outcome_yes_price=.25,
            outcome_no_price=.75,
            token_id_yes="yes",
            token_id_no="no",
            liquidity=1000,
            volume=1000,
            volume_24hr=1000,
            best_bid=.24,
            best_ask=.26,
            spread=.02,
            end_date="2026-08-23T23:59:00Z",
            category="weather",
            event_title="Highest temperature in NYC on August 23?",
            description=(
                "This market resolves to the highest temperature recorded by NOAA at "
                "LaGuardia Airport. https://www.weather.gov/wrh/timeseries?site=klga"
            ),
        )

    def test_parses_bracket_and_station(self):
        parsed = parse_weather_market(
            self.market("Will the highest temperature in NYC be between 80-81°F on August 23?")
        )
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed.station, "KLGA")
        self.assertEqual(parsed.metric, "high")
        self.assertEqual(parsed.lower_f, 80)
        self.assertEqual(parsed.upper_f, 81)
        self.assertEqual(parsed.target_date.isoformat(), "2026-08-23")

    def test_parses_upper_tail(self):
        parsed = parse_weather_market(
            self.market("Will the highest temperature in NYC be 92°F or higher on August 23?")
        )
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed.lower_f, 92)
        self.assertIsNone(parsed.upper_f)

    def test_probability_uses_whole_degree_resolution_bins(self):
        self.assertAlmostEqual(
            probability_for_range([79.4, 79.6, 80.4, 81.4, 81.6], 80, 81),
            3 / 5,
        )
        self.assertAlmostEqual(probability_for_range([72.4, 73.4, 73.6], None, 73), 2 / 3)
        self.assertAlmostEqual(probability_for_range([91.4, 91.6, 92.4], 92, None), 2 / 3)

    def test_requests_local_timezone(self):
        today = date.today()
        daily = {}
        for index, value in enumerate([79.4, 79.6, 80.1, 80.4, 81.1, 81.4, 81.6, 82.0, 82.4, 78.0], 1):
            daily[f"temperature_2m_max_member{index:02d}"] = [value]

        captured = {}

        class Response:
            def raise_for_status(self):
                pass

            def json(self):
                return {"daily": daily}

        def get(*args, **kwargs):
            captured.update(kwargs["params"])
            return Response()

        market = self.market(f"Will the highest temperature in NYC be between 80-81°F on {today:%B} {today.day}?")
        market.end_date = f"{today.isoformat()}T23:59:00Z"
        result = WeatherEstimator(get).estimate(market)
        self.assertIsNotNone(result)
        self.assertEqual(captured.get("timezone"), "auto")


if __name__ == "__main__":
    unittest.main()
