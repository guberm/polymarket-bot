"""Claude ensemble probability estimation for prediction markets."""

import json
import logging
import statistics
import time
from typing import Optional

import anthropic

from config import BotConfig
from models import MarketInfo, Estimate

log = logging.getLogger("bot.estimator")

SYSTEM_PROMPT = """You are a calibrated probability estimator for prediction markets.
Given a market question, estimate the TRUE probability that the outcome resolves YES.

Rules:
- Output ONLY valid JSON: {"probability": 0.XX, "reasoning": "one sentence"}
- probability must be between 0.02 and 0.98
- Be well-calibrated: events you rate at 70% should happen ~70% of the time
- Use base rates, current knowledge, and logical reasoning
- Do NOT anchor on the current market price — estimate independently
- If deeply uncertain, use base rates or lean toward 0.50
- Keep reasoning under 50 words"""


def _build_user_prompt(market: MarketInfo) -> str:
    desc = market.description[:500] if market.description else "N/A"
    return (
        f"Market: {market.question}\n"
        f"Event: {market.event_title}\n"
        f"Description: {desc}\n"
        f"Category: {market.category}\n"
        f"Resolution date: {market.end_date or 'Unknown'}\n\n"
        f"Estimate the probability this resolves YES. Output JSON only."
    )


class Estimator:
    def __init__(self, config: BotConfig):
        kwargs = {"api_key": config.anthropic_api_key}
        if config.anthropic_api_host:
            kwargs["base_url"] = config.anthropic_api_host
        self.client = anthropic.Anthropic(**kwargs)
        self.config = config

    def estimate(self, market: MarketInfo) -> Optional[Estimate]:
        """Run ensemble estimation: N independent Claude calls, trimmed mean."""
        raw_estimates: list[float] = []
        total_input = 0
        total_output = 0
        first_reasoning = ""

        for _ in range(self.config.ensemble_size):
            result = self._single_call(market)
            if result is None:
                continue
            prob, reasoning, in_tok, out_tok = result
            raw_estimates.append(prob)
            if not first_reasoning:
                first_reasoning = reasoning
            total_input += in_tok
            total_output += out_tok

        if len(raw_estimates) < 2:
            log.warning(f"Only {len(raw_estimates)} valid estimates for: {market.question[:60]}")
            if not raw_estimates:
                return None

        # Trimmed mean: drop highest and lowest if we have enough samples
        if len(raw_estimates) >= 4:
            sorted_est = sorted(raw_estimates)
            trimmed = sorted_est[1:-1]
        else:
            trimmed = raw_estimates

        fair_prob = statistics.mean(trimmed)
        confidence = statistics.stdev(raw_estimates) if len(raw_estimates) > 1 else 1.0

        log.info(
            f"Estimate: {market.question[:50]}... -> {fair_prob:.2%} "
            f"(n={len(raw_estimates)}, std={confidence:.3f})"
        )

        return Estimate(
            market_condition_id=market.condition_id,
            question=market.question,
            fair_probability=fair_prob,
            raw_estimates=raw_estimates,
            confidence=confidence,
            reasoning_summary=first_reasoning,
            input_tokens_used=total_input,
            output_tokens_used=total_output,
        )

    def _single_call(self, market: MarketInfo):
        """Single Claude call. Returns (prob, reasoning, input_tokens, output_tokens) or None."""
        try:
            response = self.client.messages.create(
                model=self.config.claude_model,
                max_tokens=self.config.max_estimate_tokens,
                temperature=self.config.ensemble_temperature,
                system=SYSTEM_PROMPT,
                messages=[{"role": "user", "content": _build_user_prompt(market)}],
            )

            text = response.content[0].text.strip()
            in_tok = response.usage.input_tokens
            out_tok = response.usage.output_tokens

            # Handle markdown code blocks in response
            if text.startswith("```"):
                lines = text.split("\n")
                # Drop first and last ``` lines
                lines = [l for l in lines if not l.strip().startswith("```")]
                text = "\n".join(lines)

            data = json.loads(text)
            prob = float(data["probability"])
            reasoning = data.get("reasoning", "")

            prob = max(0.02, min(0.98, prob))
            return prob, reasoning, in_tok, out_tok

        except (json.JSONDecodeError, KeyError, IndexError, ValueError) as e:
            log.debug(f"Failed to parse estimate response: {e}")
            return None
        except anthropic.RateLimitError:
            log.warning("Anthropic rate limit — waiting 5s")
            time.sleep(5)
            return None
        except anthropic.APIError as e:
            log.error(f"Anthropic API error: {e}")
            return None
