using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PolymarketBot.Services;

/// <summary>
/// Minimal ILoggerProvider that writes JSON lines to a file, matching Python's JsonFormatter.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public JsonFileLoggerProvider(StreamWriter writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(categoryName, _writer);

    public void Dispose() { }
}

internal sealed class JsonFileLogger : ILogger
{
    private readonly string _category;
    private readonly StreamWriter _writer;

    public JsonFileLogger(string category, StreamWriter writer)
    {
        _category = category;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            level = logLevel.ToString().ToUpperInvariant(),
            logger = _category,
            message = formatter(state, exception),
        };

        var json = JsonSerializer.Serialize(entry);
        lock (_writer)
        {
            _writer.WriteLine(json);
        }
    }
}
