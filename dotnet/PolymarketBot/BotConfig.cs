using System.Text.Json;

namespace PolymarketBot;

/// <summary>
/// Bot configuration.
/// Priority (highest wins):
///   1. Environment variables
///   2. polymarket_bot_config.json  (project root, or path in CONFIG_FILE env var)
///   3. Code defaults
/// polymarket_bot_config.json location: polymarket_bot/polymarket_bot_config.json (../../polymarket_bot_config.json relative to dotnet/PolymarketBot/)
/// </summary>
public sealed class BotConfig
{
    // Mode
    public bool LiveTrading { get; init; }

    // Scan
    public int ScanIntervalMinutes { get; init; } = 10;
    public double MinLiquidity { get; init; } = 5000.0;
    public double MinVolume24Hr { get; init; } = 1000.0;
    public double MinTimeToResolutionHours { get; init; } = 24.0;
    public double MinMarketPrice { get; init; } = 0.10;
    public int MarketsPerCycle { get; init; } = 30;

    // Estimation
    public string ClaudeModel { get; init; } = "claude-sonnet-4-20250514";
    public int EnsembleSize { get; init; } = 5;
    public double EnsembleTemperature { get; init; } = 0.7;
    public int MaxEstimateTokens { get; init; } = 1024;

    // Sizing
    public double KellyFraction { get; init; } = 0.25;
    public double MinEdge { get; init; } = 0.08;
    public double MinTradeUsd { get; init; } = 0.5;

    // Risk
    public double MaxPositionPct { get; set; } = 0.15;
    public double MaxTotalExposurePct { get; set; } = 1.00;
    public double MaxCategoryExposurePct { get; set; } = 0.80;
    public double DailyStopLossPct { get; set; } = 0.20;
    public double MaxDrawdownPct { get; set; } = 0.50;
    public int MaxConcurrentPositions { get; set; } = 20;

    // Position review / exit
    public bool EnablePositionReview { get; init; } = true;
    public double PositionStopLossPct { get; init; } = 0.30;
    public double TakeProfitPrice { get; init; } = 0.95;
    public double ExitEdgeBuffer { get; init; } = 0.05;
    public double ReviewReestimateThresholdPct { get; init; } = 0.10;
    public int ReviewEnsembleSize { get; init; } = 3;

    // Capital
    public double InitialBankroll { get; init; } = 10000.0;

    // API keys
    public string AnthropicApiKey { get; init; } = "";
    public string PolymarketPrivateKey { get; init; } = "";
    public string PolymarketFunderAddress { get; init; } = "";
    public int PolymarketChainId { get; init; } = 137;
    public int PolymarketSignatureType { get; init; } = 0;

    // CLOB API credentials (pre-generated)
    public string PolymarketApiKey { get; init; } = "";
    public string PolymarketApiSecret { get; init; } = "";
    public string PolymarketApiPassphrase { get; init; } = "";

    // Endpoints / contracts (required — set via polymarket_bot_config.json or env vars)
    public string AnthropicApiHost { get; init; } = "";
    public string GammaApiHost { get; init; } = "";
    public string ClobHost { get; init; } = "";
    public string ExchangeAddress { get; init; } = "";
    public string NegRiskExchangeAddress { get; init; } = "";

    // Email notifications
    public bool EmailEnabled { get; init; }
    public string EmailSmtpHost { get; init; } = "";
    public int EmailSmtpPort { get; init; } = 587;
    public bool EmailUseTls { get; init; } = true;
    public string EmailUser { get; init; } = "";
    public string EmailPassword { get; init; } = "";
    public string EmailTo { get; init; } = "";

    // Persistence (shared between Python and .NET)
    public string DataDir { get; init; } = "../../data";

    public static BotConfig FromEnv()
    {
        var j = LoadJsonConfig();

        // Priority: env var > polymarket_bot_config.json > default
        string Cfg(string jsonKey, string envKey, string def)
        {
            var ev = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(ev)) return ev;
            if (j.TryGetValue(jsonKey, out var jv)) return jv;
            return def;
        }

        return new BotConfig
        {
            LiveTrading = Cfg("live_trading", "LIVE_TRADING", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ScanIntervalMinutes = int.Parse(Cfg("scan_interval_minutes", "SCAN_INTERVAL_MINUTES", "10")),
            MinLiquidity = double.Parse(Cfg("min_liquidity", "MIN_LIQUIDITY", "5000")),
            MinVolume24Hr = double.Parse(Cfg("min_volume_24hr", "MIN_VOLUME_24HR", "1000")),
            MinTimeToResolutionHours = double.Parse(Cfg("min_time_to_resolution_hours", "MIN_TIME_TO_RESOLUTION_HOURS", "24")),
            MinMarketPrice = double.Parse(Cfg("min_market_price", "MIN_MARKET_PRICE", "0.10")),
            MarketsPerCycle = int.Parse(Cfg("markets_per_cycle", "MARKETS_PER_CYCLE", "30")),
            ClaudeModel = Cfg("claude_model", "CLAUDE_MODEL", "claude-sonnet-4-20250514"),
            EnsembleSize = int.Parse(Cfg("ensemble_size", "ENSEMBLE_SIZE", "5")),
            EnsembleTemperature = double.Parse(Cfg("ensemble_temperature", "ENSEMBLE_TEMPERATURE", "0.7")),
            KellyFraction = double.Parse(Cfg("kelly_fraction", "KELLY_FRACTION", "0.25")),
            MinEdge = double.Parse(Cfg("min_edge", "MIN_EDGE", "0.08")),
            MinTradeUsd = double.Parse(Cfg("min_trade_usd", "MIN_TRADE_USD", "0.5")),
            EnablePositionReview = Cfg("enable_position_review", "ENABLE_POSITION_REVIEW", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            PositionStopLossPct = double.Parse(Cfg("position_stop_loss_pct", "POSITION_STOP_LOSS_PCT", "0.30")),
            TakeProfitPrice = double.Parse(Cfg("take_profit_price", "TAKE_PROFIT_PRICE", "0.95")),
            ExitEdgeBuffer = double.Parse(Cfg("exit_edge_buffer", "EXIT_EDGE_BUFFER", "0.05")),
            ReviewReestimateThresholdPct = double.Parse(Cfg("review_reestimate_threshold_pct", "REVIEW_REESTIMATE_THRESHOLD_PCT", "0.10")),
            ReviewEnsembleSize = int.Parse(Cfg("review_ensemble_size", "REVIEW_ENSEMBLE_SIZE", "3")),
            MaxPositionPct = double.Parse(Cfg("max_position_pct", "MAX_POSITION_PCT", "0.15")),
            MaxTotalExposurePct = double.Parse(Cfg("max_total_exposure_pct", "MAX_TOTAL_EXPOSURE_PCT", "1.00")),
            MaxCategoryExposurePct = double.Parse(Cfg("max_category_exposure_pct", "MAX_CATEGORY_EXPOSURE_PCT", "0.80")),
            DailyStopLossPct = double.Parse(Cfg("daily_stop_loss_pct", "DAILY_STOP_LOSS_PCT", "0.20")),
            MaxDrawdownPct = double.Parse(Cfg("max_drawdown_pct", "MAX_DRAWDOWN_PCT", "0.50")),
            MaxConcurrentPositions = int.Parse(Cfg("max_concurrent_positions", "MAX_CONCURRENT_POSITIONS", "20")),
            InitialBankroll = double.Parse(Cfg("initial_bankroll", "INITIAL_BANKROLL", "10000")),
            AnthropicApiKey = Cfg("anthropic_api_key", "ANTHROPIC_API_KEY", ""),
            PolymarketPrivateKey = Cfg("polymarket_private_key", "POLYMARKET_PRIVATE_KEY", ""),
            PolymarketFunderAddress = Cfg("polymarket_funder_address", "POLYMARKET_FUNDER_ADDRESS", ""),
            PolymarketChainId = int.Parse(Cfg("polymarket_chain_id", "POLYMARKET_CHAIN_ID", "137")),
            PolymarketSignatureType = int.Parse(Cfg("polymarket_signature_type", "POLYMARKET_SIGNATURE_TYPE", "0")),
            PolymarketApiKey = Cfg("polymarket_api_key", "POLYMARKET_API_KEY", ""),
            PolymarketApiSecret = Cfg("polymarket_api_secret", "POLYMARKET_API_SECRET", ""),
            PolymarketApiPassphrase = Cfg("polymarket_api_passphrase", "POLYMARKET_API_PASSPHRASE", ""),
            AnthropicApiHost = Cfg("anthropic_api_host", "ANTHROPIC_API_HOST", ""),
            GammaApiHost = Cfg("gamma_api_host", "GAMMA_API_HOST", ""),
            ClobHost = Cfg("clob_host", "CLOB_HOST", ""),
            ExchangeAddress = Cfg("exchange_address", "EXCHANGE_ADDRESS", ""),
            NegRiskExchangeAddress = Cfg("neg_risk_exchange_address", "NEG_RISK_EXCHANGE_ADDRESS", ""),
            EmailEnabled = Cfg("email_enabled", "EMAIL_ENABLED", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            EmailSmtpHost = Cfg("email_smtp_host", "EMAIL_SMTP_HOST", ""),
            EmailSmtpPort = int.Parse(Cfg("email_smtp_port", "EMAIL_SMTP_PORT", "587")),
            EmailUseTls = Cfg("email_use_tls", "EMAIL_USE_TLS", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            EmailUser = Cfg("email_user", "EMAIL_USER", ""),
            EmailPassword = Cfg("email_password", "EMAIL_PASSWORD", ""),
            EmailTo = Cfg("email_to", "EMAIL_TO", ""),
            DataDir = Cfg("data_dir", "DATA_DIR", "../../data"),
        };
    }

    /// <summary>
    /// Load polymarket_bot_config.json, returning all values as strings (matching env var behaviour).
    /// Looks for CONFIG_FILE env var first, then ../../polymarket_bot_config.json relative to CWD.
    /// </summary>
    private static Dictionary<string, string> LoadJsonConfig()
    {
        var configFile = Environment.GetEnvironmentVariable("CONFIG_FILE");
        if (string.IsNullOrEmpty(configFile))
            configFile = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "polymarket_bot_config.json");

        if (!File.Exists(configFile))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(File.ReadAllText(configFile));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip comment keys
                if (prop.Name.StartsWith("_")) continue;
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText(),
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
