"""Deterministic GFS fair-value source for supported temperature markets."""

from __future__ import annotations

import math
import re
import time
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from typing import Callable, Optional

import requests

from models import MarketInfo

OPEN_METEO_ENSEMBLE_URL = "https://ensemble-api.open-meteo.com/v1/ensemble"
PROVIDER_NAME = "weather_gfs"
MODEL_NAME = "gfs_seamless"

STATIONS = {
    "KLGA": (40.7769, -73.8740),
    "KNYC": (40.7794, -73.9692),
    "KORD": (41.9742, -87.9073),
    "KMIA": (25.7959, -80.2870),
    "KLAX": (33.9425, -118.4081),
    "KDEN": (39.8617, -104.6731),
}

CITY_STATIONS = (
    (r"\bnew york city\b|\bnew york\b|\bnyc\b", "KLGA"),
    (r"\bchicago\b", "KORD"),
    (r"\bmiami\b", "KMIA"),
    (r"\blos angeles\b", "KLAX"),
    (r"\bdenver\b", "KDEN"),
)

MONTHS = {
    "january": 1, "jan": 1, "february": 2, "feb": 2, "march": 3, "mar": 3,
    "april": 4, "apr": 4, "may": 5, "june": 6, "jun": 6, "july": 7, "jul": 7,
    "august": 8, "aug": 8, "september": 9, "sep": 9, "october": 10, "oct": 10,
    "november": 11, "nov": 11, "december": 12, "dec": 12,
}


@dataclass(frozen=True)
class WeatherMarketSpec:
    station: str
    latitude: float
    longitude: float
    target_date: date
    metric: str
    lower_f: Optional[float]
    upper_f: Optional[float]


@dataclass(frozen=True)
class WeatherEstimate:
    probability: float
    member_count: int
    matching_members: int
    station: str
    reasoning: str


def _fallback_year(market: MarketInfo) -> int:
    if market.end_date:
        try:
            return datetime.fromisoformat(market.end_date.replace("Z", "+00:00")).year
        except ValueError:
            pass
    return date.today().year


def _extract_date(text: str, year: int) -> Optional[date]:
    month_names = "|".join(sorted(MONTHS, key=len, reverse=True))
    match = re.search(rf"\b({month_names})\s+(\d{{1,2}})(?:\s*,?\s*(\d{{4}}))?\b", text, re.I)
    if match:
        try:
            return date(int(match.group(3) or year), MONTHS[match.group(1).lower()], int(match.group(2)))
        except ValueError:
            return None
    return None


def _station_for_market(market: MarketInfo, text: str) -> Optional[str]:
    explicit = re.search(r"(?:[?&]site=|\bstation\s+)([a-z0-9]{4})\b", market.description or "", re.I)
    if explicit:
        station = explicit.group(1).upper()
        return station if station in STATIONS else None
    for pattern, station in CITY_STATIONS:
        if re.search(pattern, text, re.I):
            return station
    return None


def _temperature_range(text: str) -> tuple[Optional[float], Optional[float]] | None:
    number = r"(-?\d+(?:\.\d+)?)"
    bracket = re.search(rf"{number}\s*(?:-|–|—|to)\s*{number}\s*°?\s*f\b", text, re.I)
    if bracket:
        lower, upper = float(bracket.group(1)), float(bracket.group(2))
        return min(lower, upper), max(lower, upper)
    tail = re.search(rf"{number}\s*°?\s*f\s*(?:or\s+)?(below|lower|less|under|higher|above|more|over)\b", text, re.I)
    if tail:
        threshold = float(tail.group(1))
        direction = tail.group(2).lower()
        return (None, threshold) if direction in {"below", "lower", "less", "under"} else (threshold, None)
    return None


def parse_weather_market(market: MarketInfo) -> Optional[WeatherMarketSpec]:
    text = f"{market.question} {market.event_title} {market.slug}".lower()
    if market.category.lower() != "weather" and "temperature" not in text:
        return None
    if re.search(r"\b(highest|high temperature|daily high)\b", text):
        metric = "high"
    elif re.search(r"\b(lowest|low temperature|daily low)\b", text):
        metric = "low"
    else:
        return None
    bounds = _temperature_range(text)
    station = _station_for_market(market, text)
    target_date = _extract_date(text, _fallback_year(market))
    if bounds is None or station is None or target_date is None:
        return None
    latitude, longitude = STATIONS[station]
    return WeatherMarketSpec(station, latitude, longitude, target_date, metric, bounds[0], bounds[1])


def probability_for_range(members: list[float], lower_f: Optional[float], upper_f: Optional[float]) -> float:
    if not members:
        return 0.0
    lower_edge = -math.inf if lower_f is None else lower_f - 0.5
    upper_edge = math.inf if upper_f is None else upper_f + 0.5
    return sum(lower_edge <= value < upper_edge for value in members) / len(members)


class WeatherEstimator:
    def __init__(self, get: Callable = requests.get):
        self._get = get
        self._cache: dict[tuple[float, float, str], tuple[float, dict]] = {}

    def estimate(self, market: MarketInfo) -> Optional[WeatherEstimate]:
        spec = parse_weather_market(market)
        if spec is None:
            return None
        today = date.today()
        if spec.target_date < today or spec.target_date > today + timedelta(days=16):
            return None
        data = self._forecast(spec)
        if data is None:
            return None
        prefix = "temperature_2m_max" if spec.metric == "high" else "temperature_2m_min"
        daily = data.get("daily", {})
        members: list[float] = []
        for key, values in daily.items():
            if not key.startswith(prefix) or not isinstance(values, list) or not values:
                continue
            try:
                value = float(values[0])
            except (TypeError, ValueError):
                continue
            if math.isfinite(value):
                members.append(value)
        if len(members) < 10:
            return None
        raw_probability = probability_for_range(members, spec.lower_f, spec.upper_f)
        probability = min(0.95, max(0.05, raw_probability))
        matching = round(raw_probability * len(members))
        return WeatherEstimate(probability, len(members), matching, spec.station, "GFS ensemble")

    def _forecast(self, spec: WeatherMarketSpec) -> Optional[dict]:
        cache_key = (spec.latitude, spec.longitude, spec.target_date.isoformat())
        cached = self._cache.get(cache_key)
        if cached and time.monotonic() - cached[0] < 900:
            return cached[1]
        try:
            response = self._get(
                OPEN_METEO_ENSEMBLE_URL,
                params={"latitude": spec.latitude, "longitude": spec.longitude,
                        "daily": "temperature_2m_max,temperature_2m_min",
                        "temperature_unit": "fahrenheit", "timezone": "auto",
                        "start_date": spec.target_date.isoformat(),
                        "end_date": spec.target_date.isoformat(), "models": MODEL_NAME},
                timeout=15,
            )
            response.raise_for_status()
            data = response.json()
        except (requests.RequestException, ValueError, TypeError):
            return None
        self._cache[cache_key] = (time.monotonic(), data)
        return data
