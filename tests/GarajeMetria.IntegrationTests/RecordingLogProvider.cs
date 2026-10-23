using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.IntegrationTests;

internal sealed class RecordingLogProvider : ILoggerProvider
{
    public ConcurrentQueue<Entry> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
    public void Dispose() { }

    internal sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new(category, logLevel, formatter(state, exception), exception,
                state is IEnumerable<KeyValuePair<string, object?>> properties ? properties.ToDictionary() : new Dictionary<string, object?>()));
    }
}
