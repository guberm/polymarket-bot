using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

/// <summary>
/// Sends email notifications for bot state changes.
/// All methods silently swallow errors — a notification failure must never crash the bot.
/// Configure via env vars: EMAIL_ENABLED, EMAIL_SMTP_HOST, EMAIL_SMTP_PORT,
/// EMAIL_USE_TLS, EMAIL_USER, EMAIL_PASSWORD, EMAIL_TO.
/// </summary>
public sealed class Notifier
{
    private readonly BotConfig _config;
    private readonly ILogger<Notifier> _logger;

    public Notifier(BotConfig config, ILogger<Notifier> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool Enabled =>
        _config.EmailEnabled &&
        !string.IsNullOrEmpty(_config.EmailSmtpHost) &&
        !string.IsNullOrEmpty(_config.EmailTo);

    public void Send(string subject, string body)
    {
        if (!Enabled) return;
        try
        {
#pragma warning disable SYSLIB0021 // SmtpClient is deprecated but acceptable for simple SMTP
            using var client = new SmtpClient(_config.EmailSmtpHost, _config.EmailSmtpPort)
            {
                EnableSsl = _config.EmailUseTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
            };
#pragma warning restore SYSLIB0021
            if (!string.IsNullOrEmpty(_config.EmailUser))
                client.Credentials = new NetworkCredential(_config.EmailUser, _config.EmailPassword);

            var from = string.IsNullOrEmpty(_config.EmailUser)
                ? $"polymarket-bot@{_config.EmailSmtpHost}"
                : _config.EmailUser;

            using var msg = new MailMessage(from, _config.EmailTo)
            {
                Subject = $"[Polymarket Bot] {subject}",
                Body = body,
                IsBodyHtml = false,
            };
            client.Send(msg);
            _logger.LogDebug("Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Email notification failed: {Error}", ex.Message);
        }
    }

    // ── Convenience helpers ──────────────────────────────────────────────

    public void NotifyStarted(string mode, double bankroll, int positions) =>
        Send($"Started: {mode} mode",
            $"Bot started.\n\n" +
            $"Mode: {mode}\n" +
            $"Bankroll: ${bankroll:F2}\n" +
            $"Open positions: {positions}\n" +
            $"Time: {Now()}");

    public void NotifyTrade(Trade trade, Signal signal, Portfolio portfolio) =>
        Send($"BUY {trade.Side} ${trade.SizeUsd:F2} — {Truncate(trade.Question, 60)}",
            $"New position opened.\n\n" +
            $"Market: {trade.Question}\n" +
            $"Side: {trade.Side}\n" +
            $"Price: {trade.Price:F4}\n" +
            $"Size: ${trade.SizeUsd:F2}\n" +
            $"Shares: {trade.Shares:F2}\n" +
            $"Edge: {signal.Edge:P1}\n" +
            $"Expected value: ${signal.ExpectedValue:F2}\n" +
            $"\nPortfolio after:\n" +
            $"  Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"  Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"  Positions: {portfolio.Positions.Count}\n" +
            $"Time: {Now()}");

    public void NotifySell(Trade trade, string exitReason, double pnlPct, Portfolio portfolio)
    {
        var sign = pnlPct >= 0 ? "+" : "";
        Send($"SELL ({exitReason}) {sign}{pnlPct:P1} — {Truncate(trade.Question, 60)}",
            $"Position closed.\n\n" +
            $"Market: {trade.Question}\n" +
            $"Exit reason: {exitReason}\n" +
            $"Exit price: {trade.Price:F4}\n" +
            $"PnL: {sign}{pnlPct:P1}\n" +
            $"Recovered: ${trade.SizeUsd:F2}\n" +
            $"\nPortfolio after:\n" +
            $"  Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"  Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"  Positions: {portfolio.Positions.Count}\n" +
            $"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");
    }

    public void NotifyTopupSell(Trade trade, TopupCandidate tc, Portfolio portfolio) =>
        Send($"TOPUP+SELL ({tc.ExitReason}) recovered ${tc.RecoveryValue:F2} — {Truncate(tc.Position.Question, 55)}",
            $"Tiny position rescued via top-up-and-sell.\n\n" +
            $"Market: {tc.Position.Question}\n" +
            $"Exit reason: {tc.ExitReason}\n" +
            $"Tokens bought: {tc.TokensToBuy:F0} (top-up)\n" +
            $"Total tokens sold: {tc.Position.Shares + tc.TokensToBuy:F2}\n" +
            $"Top-up cost: ${tc.TopupCost:F2}\n" +
            $"Recovered: ${tc.RecoveryValue:F2}\n" +
            $"\nPortfolio after:\n" +
            $"  Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"  Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"  Positions: {portfolio.Positions.Count}\n" +
            $"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");

    public void NotifyResolved(Position pos, bool won, double pnl, Portfolio portfolio) =>
        Send($"Resolved ({(won ? "WON" : "LOST")}) PnL={pnl:+0.00;-0.00} — {Truncate(pos.Question, 60)}",
            $"Market resolved.\n\n" +
            $"Market: {pos.Question}\n" +
            $"Result: {(won ? "WON" : "LOST")}\n" +
            $"PnL: ${pnl:+0.00;-0.00}\n" +
            $"Shares: {pos.Shares:F2}\n" +
            $"\nPortfolio after:\n" +
            $"  Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"  Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"  Positions: {portfolio.Positions.Count}\n" +
            $"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");

    public void NotifyHalted(string reason, Portfolio portfolio)
    {
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        Send($"HALTED: {reason}",
            $"Bot halted.\n\n" +
            $"Reason: {reason}\n" +
            $"\nPortfolio:\n" +
            $"  Value: ${pv:F2}\n" +
            $"  Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"  Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"  Positions: {portfolio.Positions.Count}\n" +
            $"  Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");
    }

    public void NotifyDailyReset(Portfolio portfolio)
    {
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        Send($"Daily reset — portfolio ${pv:F2}",
            $"New trading day started.\n\n" +
            $"Portfolio value: ${pv:F2}\n" +
            $"Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"Open positions: {portfolio.Positions.Count}\n" +
            $"Cumulative PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");
    }

    public void NotifyBuyFail(MarketInfo market, Signal signal, string reason) =>
        Send($"BUY FAILED {signal.Side} ${signal.PositionSizeUsd:F2} — {Truncate(market.Question, 60)}",
            $"BUY order failed.\n\n" +
            $"Market: {market.Question}\n" +
            $"Side: {signal.Side}\n" +
            $"Attempted price: {signal.MarketPrice:F4}\n" +
            $"Attempted size: ${signal.PositionSizeUsd:F2}\n" +
            $"Edge: {signal.Edge:P1}\n" +
            $"Reason: {reason}\n" +
            $"Time: {Now()}");

    public void NotifySellFail(Position position, string exitReason, string failReason) =>
        Send($"SELL FAILED ({exitReason}) — {Truncate(position.Question, 60)}",
            $"SELL order failed.\n\n" +
            $"Market: {position.Question}\n" +
            $"Exit reason: {exitReason}\n" +
            $"Attempted price: {position.CurrentPrice:F4}\n" +
            $"Shares: {position.Shares:F2}\n" +
            $"Reason: {failReason}\n" +
            $"Time: {Now()}");

    public void NotifyTopupSellFail(TopupCandidate tc, string failReason) =>
        Send($"TOPUP+SELL FAILED ({tc.ExitReason}) — {Truncate(tc.Position.Question, 55)}",
            $"Top-up-and-sell failed.\n\n" +
            $"Market: {tc.Position.Question}\n" +
            $"Exit reason: {tc.ExitReason}\n" +
            $"Current tokens: {tc.Position.Shares:F2}\n" +
            $"Top-up cost: ${tc.TopupCost:F2}\n" +
            $"Reason: {failReason}\n" +
            $"Time: {Now()}");

    public void NotifyError(int cycle, Exception ex) =>
        Send($"Error in cycle {cycle}",
            $"An error occurred in cycle {cycle}.\n\n" +
            $"Error: {ex.Message}\n" +
            $"Time: {Now()}");

    public void NotifyStopped(Portfolio portfolio)
    {
        var pv = portfolio.Bankroll + portfolio.TotalExposure();
        Send($"Stopped — portfolio ${pv:F2}, PnL {portfolio.TotalRealizedPnl:+0.00;-0.00}",
            $"Bot stopped.\n\n" +
            $"Final portfolio value: ${pv:F2}\n" +
            $"Bankroll: ${portfolio.Bankroll:F2}\n" +
            $"Exposure: ${portfolio.TotalExposure():F2}\n" +
            $"Open positions: {portfolio.Positions.Count}\n" +
            $"Total trades: {portfolio.TotalTrades}\n" +
            $"Total API cost: ${portfolio.TotalApiCost:F4}\n" +
            $"Realized PnL: ${portfolio.TotalRealizedPnl:+0.00;-0.00}\n" +
            $"Time: {Now()}");
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
