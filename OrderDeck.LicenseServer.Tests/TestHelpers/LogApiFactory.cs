using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary><see cref="ApiFactory"/> + uygulamanın günlük satırlarının toplayıcısı (<see cref="LogRecorderProvider"/>):
/// uçların, servislerin ve <c>LicenseDbContext</c>'in (bağlamın <c>ILoggerFactory</c>'si uygulamanınkidir) yazdıkları.</summary>
public sealed class LogApiFactory : ApiFactory
{
    public LogRecorderProvider Log { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Log));
    }
}

/// <summary>Information ve üstü günlük satırlarını kategori, düzey ve biçimlenmiş metinle toplar (iş parçacığı
/// güvenli). Tek bir <c>ILogger&lt;T&gt;</c>'yi elle veren testler için <see cref="LogRecorder{T}"/>.</summary>
public sealed class LogRecorderProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>Yalnız metinler, yazılış sırasıyla.</summary>
    public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToArray();

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class Recorder(LogRecorderProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner._entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>Toplanan bir günlük satırı.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message);
