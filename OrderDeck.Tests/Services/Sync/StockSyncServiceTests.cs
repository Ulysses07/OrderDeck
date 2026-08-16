using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public class StockSyncServiceTests
{
    // FakeLicenseProvider / RecordingLogger kalıpları
    // CatalogSyncServiceTests ve ShopperRegistrationIngestServiceTests içinde
    // private nested class olarak tanımlı; burada da aynı şekilde tanımlıyoruz.

    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }

        public FakeLicenseProvider(string? key) => CurrentLicenseKey = key;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static StockSyncService Build(
        InMemorySqlite db,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        StockBalanceProvider provider,
        string? licenseKey = "LDK-TEST")
    {
        var http = new HttpClient(new FakeHttpMessageHandler(responder))
        { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return new StockSyncService(
            api, new StockBalanceRepository(db), provider,
            new FakeLicenseProvider(licenseKey),
            new RecordingLogger<StockSyncService>());
    }

    private const string LicenseId = "44444444-4444-4444-4444-444444444444";

    private static int _pageCursor;

    private static HttpResponseMessage Route(
        HttpRequestMessage req, params string[] stockPages)
    {
        var path = req.RequestUri!.PathAndQuery;
        if (path.Contains("/me/licenses"))
            return FakeHttpMessageHandler.Json(200,
                $$"""[{"id":"{{LicenseId}}","licenseKey":"LDK-TEST"}]""");

        // Sayfa sırası çağrı sırasına göre; testler tek veya iki sayfa veriyor.
        var index = Math.Min(_pageCursor++, stockPages.Length - 1);
        return FakeHttpMessageHandler.Json(200, stockPages[index]);
    }

    [Fact]
    public async Task Writes_balances_and_advances_cursor()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));

        const string page = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":5}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":false}
            """;
        var svc = Build(db, req => Route(req, page), provider);

        var written = await svc.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(1);
        repo.GetForProduct("11111111111111111111111111111111")
            .Should().ContainSingle().Which.Quantity.Should().Be(5);
        repo.GetCursor().Id.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public async Task Follows_hasMore_across_pages()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));

        const string first = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":5}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":true}
            """;
        const string second = """
            {"balances":[{"productId":"22222222-2222-2222-2222-222222222222",
                          "productVariantId":null,"quantity":9}],
             "cursorCreatedAt":"2026-08-15T10:01:00+00:00",
             "cursorId":"66666666-6666-6666-6666-666666666666",
             "hasMore":false}
            """;
        var svc = Build(db, req => Route(req, first, second), provider);

        var written = await svc.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(2);
        repo.GetForProduct("22222222222222222222222222222222").Should().ContainSingle();
        repo.GetCursor().Id.Should().Be(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    }

    [Fact]
    public async Task Raises_BalancesChanged_only_when_something_was_written()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var fired = 0;
        provider.BalancesChanged += (_, __) => fired++;

        const string empty = """
            {"balances":[],"cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333","hasMore":false}
            """;
        var svc = Build(db, req => Route(req, empty), provider);

        await svc.SyncOnceAsync(CancellationToken.None);

        fired.Should().Be(0);
    }

    [Fact]
    public async Task Does_nothing_without_a_license_key()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var svc = Build(db, _ => throw new InvalidOperationException("çağrılmamalıydı"),
            provider, licenseKey: null);

        (await svc.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Swallows_network_failures()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var svc = Build(db, req => req.RequestUri!.PathAndQuery.Contains("/me/licenses")
            ? FakeHttpMessageHandler.Json(200, $$"""[{"id":"{{LicenseId}}","licenseKey":"LDK-TEST"}]""")
            : throw new HttpRequestException("ağ yok"), provider);

        // Yayın sırasında ağ kopması normaldir; senkron sessizce 0 döner ve
        // imleç yerinde kalır — bir sonraki tur kaldığı yerden devam eder.
        (await svc.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
        repo.GetCursor().Id.Should().Be(Guid.Empty);
    }
}
