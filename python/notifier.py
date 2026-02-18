"""Email notifications for bot state changes.

Configure via env vars:
    EMAIL_ENABLED=true
    EMAIL_SMTP_HOST=smtp.gmail.com
    EMAIL_SMTP_PORT=587
    EMAIL_USE_TLS=true         (STARTTLS; set false for SMTP_SSL on port 465)
    EMAIL_USER=mybot@gmail.com
    EMAIL_PASSWORD=app_password
    EMAIL_TO=me@example.com
"""

import logging
import smtplib
import ssl
from datetime import datetime
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText

log = logging.getLogger("bot.notifier")


class Notifier:
    """Sends email notifications for bot state changes.

    All send methods silently swallow errors — a notification failure must
    never crash the bot.
    """

    def __init__(self, config):
        self._config = config

    @property
    def enabled(self) -> bool:
        return (
            self._config.email_enabled
            and bool(self._config.email_smtp_host)
            and bool(self._config.email_to)
        )

    def send(self, subject: str, body: str) -> None:
        """Send a plain-text email. No-op if notifications are disabled."""
        if not self.enabled:
            return
        cfg = self._config
        try:
            msg = MIMEMultipart("alternative")
            msg["Subject"] = f"[Polymarket Bot] {subject}"
            msg["From"] = cfg.email_user or f"polymarket-bot@{cfg.email_smtp_host}"
            msg["To"] = cfg.email_to
            msg.attach(MIMEText(body, "plain"))

            context = ssl.create_default_context()
            if cfg.email_use_tls:
                # STARTTLS (port 587)
                with smtplib.SMTP(cfg.email_smtp_host, cfg.email_smtp_port) as smtp:
                    smtp.ehlo()
                    smtp.starttls(context=context)
                    smtp.ehlo()
                    if cfg.email_user and cfg.email_password:
                        smtp.login(cfg.email_user, cfg.email_password)
                    smtp.sendmail(msg["From"], [cfg.email_to], msg.as_string())
            else:
                # SMTP_SSL (port 465)
                with smtplib.SMTP_SSL(cfg.email_smtp_host, cfg.email_smtp_port, context=context) as smtp:
                    if cfg.email_user and cfg.email_password:
                        smtp.login(cfg.email_user, cfg.email_password)
                    smtp.sendmail(msg["From"], [cfg.email_to], msg.as_string())

            log.debug(f"Email sent: {subject}")
        except Exception as e:
            log.warning(f"Email notification failed: {e}")

    # ── Convenience helpers ────────────────────────────────────────────────

    def notify_started(self, mode: str, bankroll: float, positions: int) -> None:
        self.send(
            f"Started: {mode} mode",
            f"Bot started.\n\n"
            f"Mode: {mode}\n"
            f"Bankroll: ${bankroll:.2f}\n"
            f"Open positions: {positions}\n"
            f"Time: {_now()}",
        )

    def notify_trade(self, trade, signal, portfolio) -> None:
        self.send(
            f"BUY {trade.side.value} ${trade.size_usd:.2f} — {trade.question[:60]}",
            f"New position opened.\n\n"
            f"Market: {trade.question}\n"
            f"Side: {trade.side.value}\n"
            f"Price: {trade.price:.4f}\n"
            f"Size: ${trade.size_usd:.2f}\n"
            f"Shares: {trade.shares:.2f}\n"
            f"Edge: {signal.edge:.1%}\n"
            f"Expected value: ${signal.expected_value:.2f}\n"
            f"\nPortfolio after:\n"
            f"  Bankroll: ${portfolio.bankroll:.2f}\n"
            f"  Exposure: ${portfolio.total_exposure():.2f}\n"
            f"  Positions: {len(portfolio.positions)}\n"
            f"Time: {_now()}",
        )

    def notify_sell(self, trade, exit_reason: str, pnl_pct: float, portfolio) -> None:
        sign = "+" if pnl_pct >= 0 else ""
        self.send(
            f"SELL ({exit_reason}) {sign}{pnl_pct:.1%} — {trade.question[:60]}",
            f"Position closed.\n\n"
            f"Market: {trade.question}\n"
            f"Exit reason: {exit_reason}\n"
            f"Exit price: {trade.price:.4f}\n"
            f"PnL: {sign}{pnl_pct:.1%}\n"
            f"Recovered: ${trade.size_usd:.2f}\n"
            f"\nPortfolio after:\n"
            f"  Bankroll: ${portfolio.bankroll:.2f}\n"
            f"  Exposure: ${portfolio.total_exposure():.2f}\n"
            f"  Positions: {len(portfolio.positions)}\n"
            f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )

    def notify_topup_sell(self, trade, tc, portfolio) -> None:
        self.send(
            f"TOPUP+SELL ({tc.exit_reason}) recovered ${tc.recovery_value:.2f} — {tc.position.question[:55]}",
            f"Tiny position rescued via top-up-and-sell.\n\n"
            f"Market: {tc.position.question}\n"
            f"Exit reason: {tc.exit_reason}\n"
            f"Tokens bought: {tc.tokens_to_buy:.0f} (top-up)\n"
            f"Total tokens sold: {tc.position.shares + tc.tokens_to_buy:.2f}\n"
            f"Top-up cost: ${tc.topup_cost:.2f}\n"
            f"Recovered: ${tc.recovery_value:.2f}\n"
            f"\nPortfolio after:\n"
            f"  Bankroll: ${portfolio.bankroll:.2f}\n"
            f"  Exposure: ${portfolio.total_exposure():.2f}\n"
            f"  Positions: {len(portfolio.positions)}\n"
            f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )

    def notify_resolved(self, position, won: bool, pnl: float, portfolio) -> None:
        result = "WON" if won else "LOST"
        self.send(
            f"Resolved ({result}) PnL={pnl:+.2f} — {position.question[:60]}",
            f"Market resolved.\n\n"
            f"Market: {position.question}\n"
            f"Result: {result}\n"
            f"PnL: ${pnl:+.2f}\n"
            f"Shares: {position.shares:.2f}\n"
            f"\nPortfolio after:\n"
            f"  Bankroll: ${portfolio.bankroll:.2f}\n"
            f"  Exposure: ${portfolio.total_exposure():.2f}\n"
            f"  Positions: {len(portfolio.positions)}\n"
            f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )

    def notify_halted(self, reason: str, portfolio) -> None:
        pv = portfolio.bankroll + portfolio.total_exposure()
        self.send(
            f"HALTED: {reason}",
            f"Bot halted.\n\n"
            f"Reason: {reason}\n"
            f"\nPortfolio:\n"
            f"  Value: ${pv:.2f}\n"
            f"  Bankroll: ${portfolio.bankroll:.2f}\n"
            f"  Exposure: ${portfolio.total_exposure():.2f}\n"
            f"  Positions: {len(portfolio.positions)}\n"
            f"  Realized PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )

    def notify_daily_reset(self, portfolio) -> None:
        pv = portfolio.bankroll + portfolio.total_exposure()
        self.send(
            f"Daily reset — portfolio ${pv:.2f}",
            f"New trading day started.\n\n"
            f"Portfolio value: ${pv:.2f}\n"
            f"Bankroll: ${portfolio.bankroll:.2f}\n"
            f"Exposure: ${portfolio.total_exposure():.2f}\n"
            f"Open positions: {len(portfolio.positions)}\n"
            f"Cumulative PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )

    def notify_error(self, cycle: int, error: Exception) -> None:
        self.send(
            f"Error in cycle {cycle}",
            f"An error occurred in cycle {cycle}.\n\n"
            f"Error: {error}\n"
            f"Time: {_now()}",
        )

    def notify_stopped(self, portfolio) -> None:
        pv = portfolio.bankroll + portfolio.total_exposure()
        self.send(
            f"Stopped — portfolio ${pv:.2f}, PnL {portfolio.total_realized_pnl:+.2f}",
            f"Bot stopped.\n\n"
            f"Final portfolio value: ${pv:.2f}\n"
            f"Bankroll: ${portfolio.bankroll:.2f}\n"
            f"Exposure: ${portfolio.total_exposure():.2f}\n"
            f"Open positions: {len(portfolio.positions)}\n"
            f"Total trades: {portfolio.total_trades}\n"
            f"Total API cost: ${portfolio.total_api_cost:.4f}\n"
            f"Realized PnL: ${portfolio.total_realized_pnl:+.2f}\n"
            f"Time: {_now()}",
        )


def _now() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")
