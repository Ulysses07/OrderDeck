using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Günlük satırlarını düzey + biçimlenmiş metin olarak toplar; banka testleri ortak kullanır (yarış ve bağdaştırıcı
/// testleri: hangi uyarının, hangi kimlikle yazıldığı).</summary>
internal sealed class LogRecorder<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
}
