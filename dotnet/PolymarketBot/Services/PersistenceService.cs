using System.Text.Json;
using System.Text.Json.Serialization;
using PolymarketBot.Models;

namespace PolymarketBot.Services;

public static class PersistenceService
{
    private const string PortfolioFile = "portfolio.json";
    private const string TradesFile = "trades.jsonl";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly JsonSerializerOptions JsonLineOpts = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void SaveSnapshot(PortfolioSnapshot snapshot, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, PortfolioFile);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static PortfolioSnapshot? LoadSnapshot(string dataDir)
    {
        var path = Path.Combine(dataDir, PortfolioFile);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PortfolioSnapshot>(json, JsonOpts);
    }

    public static void AppendTrade(Trade trade, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, TradesFile);
        var line = JsonSerializer.Serialize(trade, JsonLineOpts);
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
