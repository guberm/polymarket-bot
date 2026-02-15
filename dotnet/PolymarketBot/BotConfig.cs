namespace PolymarketBot;

public sealed class BotConfig
{
    // Mode
    public bool LiveTrading { get; init; }

    // Scan
    public int ScanIntervalMinutes { get; init; } = 10;
    public double MinLiquidity { get; init; } = 5000.0;
    public double MinVolume24Hr { get; init; } = 1000.0;
    public double MinTimeToResolutionHours { get; init; } = 24.0;
    public int MarketsPerCycle { get; init; } = 30;

    // Estimation
    public string ClaudeModel { get; init; } = "claude-sonnet-4-20250514";
    public int EnsembleSize { get; init; } = 5;
    public double EnsembleTemperature { get; init; } = 0.7;
    public int MaxEstimateTokens { get; init; } = 1024;

    // Sizing
    public double KellyFraction { get; init; } = 0.25;
    public double MinEdge { get; init; } = 0.08;
    public double MinTradeUsd { get; init; } = 1.0;

    // Risk
    public double MaxPositionPct { get; set; } = 0.15;
    public double MaxTotalExposurePct { get; set; } = 0.90;
    public double MaxCategoryExposurePct { get; set; } = 0.50;
    public double DailyStopLossPct { get; set; } = 0.20;
    public double MaxDrawdownPct { get; set; } = 0.50;
    public int MaxConcurrentPositions { get; set; } = 20;

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

    // Endpoints / contracts (required — set via env vars)
    public string AnthropicApiHost { get; init; } = "";
    public string GammaApiHost { get; init; } = "";
    public string ClobHost { get; init; } = "";
    public string ExchangeAddress { get; init; } = "";
    public string NegRiskExchangeAddress { get; init; } = "";

    // Persistence
    public string DataDir { get; init; } = "data";

    public static BotConfig FromEnv()
    {
        return new BotConfig
        {
            LiveTrading = Env("LIVE_TRADING", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ScanIntervalMinutes = int.Parse(Env("SCAN_INTERVAL_MINUTES", "10")),
            MinLiquidity = double.Parse(Env("MIN_LIQUIDITY", "5000")),
            MinVolume24Hr = double.Parse(Env("MIN_VOLUME_24HR", "1000")),
            MinTimeToResolutionHours = double.Parse(Env("MIN_TIME_TO_RESOLUTION_HOURS", "24")),
            MarketsPerCycle = int.Parse(Env("MARKETS_PER_CYCLE", "30")),
            ClaudeModel = Env("CLAUDE_MODEL", "claude-sonnet-4-20250514"),
            EnsembleSize = int.Parse(Env("ENSEMBLE_SIZE", "5")),
            EnsembleTemperature = double.Parse(Env("ENSEMBLE_TEMPERATURE", "0.7")),
            KellyFraction = double.Parse(Env("KELLY_FRACTION", "0.25")),
            MinEdge = double.Parse(Env("MIN_EDGE", "0.08")),
            MinTradeUsd = double.Parse(Env("MIN_TRADE_USD", "1.0")),
            MaxPositionPct = double.Parse(Env("MAX_POSITION_PCT", "0.15")),
            MaxTotalExposurePct = double.Parse(Env("MAX_TOTAL_EXPOSURE_PCT", "0.90")),
            MaxCategoryExposurePct = double.Parse(Env("MAX_CATEGORY_EXPOSURE_PCT", "0.50")),
            DailyStopLossPct = double.Parse(Env("DAILY_STOP_LOSS_PCT", "0.20")),
            MaxDrawdownPct = double.Parse(Env("MAX_DRAWDOWN_PCT", "0.50")),
            MaxConcurrentPositions = int.Parse(Env("MAX_CONCURRENT_POSITIONS", "20")),
            InitialBankroll = double.Parse(Env("INITIAL_BANKROLL", "10000")),
            AnthropicApiKey = Env("ANTHROPIC_API_KEY", ""),
            PolymarketPrivateKey = Env("POLYMARKET_PRIVATE_KEY", ""),
            PolymarketFunderAddress = Env("POLYMARKET_FUNDER_ADDRESS", ""),
            PolymarketChainId = int.Parse(Env("POLYMARKET_CHAIN_ID", "137")),
            PolymarketSignatureType = int.Parse(Env("POLYMARKET_SIGNATURE_TYPE", "0")),
            PolymarketApiKey = Env("POLYMARKET_API_KEY", ""),
            PolymarketApiSecret = Env("POLYMARKET_API_SECRET", ""),
            PolymarketApiPassphrase = Env("POLYMARKET_API_PASSPHRASE", ""),
            AnthropicApiHost = Env("ANTHROPIC_API_HOST", ""),
            GammaApiHost = Env("GAMMA_API_HOST", ""),
            ClobHost = Env("CLOB_HOST", ""),
            ExchangeAddress = Env("EXCHANGE_ADDRESS", ""),
            NegRiskExchangeAddress = Env("NEG_RISK_EXCHANGE_ADDRESS", ""),
            DataDir = Env("DATA_DIR", "data"),
        };
    }

    private static string Env(string key, string defaultValue)
        => Environment.GetEnvironmentVariable(key) ?? defaultValue;
}
