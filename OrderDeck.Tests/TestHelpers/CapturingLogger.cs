using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Günlüğe yazılanları toplayan logger: seviye, biçimlenmiş ileti ve istisnanın tam metni. Uyarı sayısı ve
/// "günlükte kişisel veri yok" denetimleri için (ileti + istisna birlikte taranır).
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, string? Exception)
    {
        public string Full => Exception is null ? Message : Message + Environment.NewLine + Exception;
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IReadOnlyList<Entry> Warnings => Entries.Where(e => e.Level >= LogLevel.Warning).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception?.ToString()));
}
