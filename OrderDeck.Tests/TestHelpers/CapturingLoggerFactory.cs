using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Kurduğu bütün logger'lara yazılanı TEK listede, yazım sırasıyla toplar: kategori, seviye, biçimlenmiş ileti ve
/// istisnanın tam metni. <see cref="CapturingLogger{T}"/> tek kategoriyi dinler; birkaç sınıfın (hosted service,
/// poller, çözümleyici, kapı) günlüğünü birlikte okuyan ve satırların sırasına bakan testler için bu fabrika
/// gerekir. Bütün seviyeler açıktır; eşzamanlı yazım güvenlidir.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    /// <param name="Category">Satırı yazan logger'ın kategorisi (<c>CreateLogger&lt;T&gt;</c> için tipin tam adı).</param>
    /// <param name="Level">Satırın seviyesi.</param>
    /// <param name="Message">Biçimlenmiş ileti.</param>
    /// <param name="Exception">İstisnanın <c>ToString</c> metni; istisna yoksa null.</param>
    public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception)
    {
        /// <summary>İleti + istisna metni: "günlükte şu yok" denetimleri ikisini birlikte tarar.</summary>
        public string Full => Exception is null ? Message : Message + Environment.NewLine + Exception;
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>Şimdiye dek yazılan satırlar, yazım sırasıyla (anlık kopya).</summary>
    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
        // Sağlayıcı yok: her satır doğrudan listeye yazılır.
    }

    public void Dispose()
    {
        // Bırakılacak kaynak yok.
    }

    private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception?.ToString()));
    }
}
