using System.Text.Json;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class PaymentSyncServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private static readonly Guid TestLicenseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string TestLicenseKey = "LDK-TEST-FIXTURE";

    // R9-D02: imleç artık Payment satırlarıyla aynı SQLite dosyasındaki
    // SyncCursor tablosunda ("payment-decision-in", lisans anahtarına bağlı).
    private const string PullCursorName = "payment-decision-in";

    private static (PaymentSyncService svc, PaymentRepository repo, SyncCursorRepository cursors,
            StubLicenseProvider licenseProvider, List<(HttpMethod Method, string Path, string? Body)> requests) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new PaymentRepository(db);
        var cursors = new SyncCursorRepository(db);

        var requests = new List<(HttpMethod, string, string?)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((req.Method, req.RequestUri!.PathAndQuery, body));
            return responder(req);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());

        var licenseProvider = new StubLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var svc = new PaymentSyncService(api, repo, cursors, licenseProvider,
            new FakeClock(), NullLogger<PaymentSyncService>.Instance);
        return (svc, repo, cursors, licenseProvider, requests);
    }

    private static Payment NewLocalPayment(string id = "p1", string? refNo = null) => new(
        Id: id,
        PayerName: "Ahmet Y",
        Amount: 100m,
        PaidAt: 1714521600L,
        ReferansNo: refNo ?? $"REF-{id}",
        PdfHash: null,
        Status: PaymentStatus.Pending,
        CreatedAt: 1714521600L,
        UpdatedAt: 1714521600L,
        SyncedAt: null,
        ApprovedAt: null,
        RejectedAt: null,
        RejectReason: null);

    private static HttpResponseMessage JsonResp(int code, string body) =>
        FakeHttpMessageHandler.Json(code, body);

    private static string LicensesJson() =>
        $@"[{{ ""id"": ""{TestLicenseId}"", ""licenseKey"": ""{TestLicenseKey}"",
            ""skuCode"": ""STD"", ""expiresAt"": ""2030-01-01T00:00:00Z"" }}]";

    [Fact]
    public async Task SyncOnceAsync_skips_when_no_license_key()
    {
        var (svc, _, _, _, requests) = Build(_ => JsonResp(200, "[]"), seedLicense: false);

        var result = await svc.SyncOnceAsync();

        result.Pushed.Should().Be(0);
        result.Pulled.Should().Be(0);
        requests.Should().BeEmpty("no HTTP calls when no license");
    }

    [Fact]
    public async Task SyncOnceAsync_resolves_LicenseId_and_pushes_unsynced()
    {
        var (svc, repo, _, _, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            if (path.Contains("/payments/sync"))
                return JsonResp(200, "[]");
            if (path.Contains("/payments/since"))
                return JsonResp(200, "[]");
            return JsonResp(404, "{}");
        });

        var paymentId = Guid.NewGuid();
        repo.Insert(NewLocalPayment(paymentId.ToString()));

        var result = await svc.SyncOnceAsync();

        result.Pushed.Should().Be(1);
        result.Pulled.Should().Be(0);

        // Resolved LicenseId, then pushed, then pulled
        requests.Should().HaveCountGreaterThanOrEqualTo(3);
        requests.Should().Contain(r => r.Path.StartsWith("/api/v1/me/licenses"));
        requests.Should().Contain(r => r.Path == $"/api/v1/licenses/{TestLicenseId}/payments/sync");

        // Payment now marked synced
        repo.FindById(paymentId.ToString())!.SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOnceAsync_caches_LicenseId_across_runs()
    {
        var (svc, repo, _, _, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            return JsonResp(200, "[]");
        });

        // First run resolves LicenseId
        await svc.SyncOnceAsync();
        var firstRunMeCalls = requests.Count(r => r.Path.StartsWith("/api/v1/me/licenses"));

        // Insert a payment so second run pushes
        repo.Insert(NewLocalPayment(Guid.NewGuid().ToString()));
        await svc.SyncOnceAsync();

        var secondRunMeCalls = requests.Count(r => r.Path.StartsWith("/api/v1/me/licenses"));
        secondRunMeCalls.Should().Be(firstRunMeCalls, "LicenseId cached, no re-resolve");
    }

    [Fact]
    public async Task SyncOnceAsync_applies_server_echo_to_local()
    {
        var paymentId = Guid.NewGuid();
        var (svc, repo, _, _, _) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            if (path.Contains("/payments/sync"))
            {
                // Echo back with approved status (race: mobile approved between pushes)
                var echoJson = $@"[{{
                    ""id"": ""{paymentId}"",
                    ""status"": ""approved"",
                    ""approvedAt"": ""2026-05-11T10:00:00Z"",
                    ""rejectedAt"": null,
                    ""rejectReason"": null,
                    ""updatedAt"": ""2026-05-11T10:00:00Z""
                }}]";
                return JsonResp(200, echoJson);
            }
            return JsonResp(200, "[]");
        });

        repo.Insert(NewLocalPayment(paymentId.ToString()));
        await svc.SyncOnceAsync();

        var stored = repo.FindById(paymentId.ToString())!;
        stored.Status.Should().Be(PaymentStatus.Approved, "echo applied status");
        stored.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOnceAsync_pulls_reverse_sync_updates_and_advances_cursor()
    {
        var paymentId = Guid.NewGuid();
        var (svc, repo, cursors, _, _) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            if (path.Contains("/payments/since"))
            {
                var sinceJson = $@"[{{
                    ""id"": ""{paymentId}"",
                    ""status"": ""rejected"",
                    ""approvedAt"": null,
                    ""rejectedAt"": ""2026-05-11T10:30:00Z"",
                    ""rejectReason"": ""tutar uyusmuyor"",
                    ""updatedAt"": ""2026-05-11T10:30:00Z""
                }}]";
                return JsonResp(200, sinceJson);
            }
            return JsonResp(200, "[]");
        });

        // Already-synced payment in local — pull will update it
        repo.Insert(NewLocalPayment(paymentId.ToString()));
        repo.MarkSynced(paymentId.ToString(), 1714000000L);

        var result = await svc.SyncOnceAsync();

        result.Pulled.Should().Be(1);
        var stored = repo.FindById(paymentId.ToString())!;
        stored.Status.Should().Be(PaymentStatus.Rejected);
        stored.RejectReason.Should().Be("tutar uyusmuyor");
        // R9-D02: imleç veriyle aynı SQLite dosyasındaki SyncCursor satırında.
        var row = cursors.Get(PullCursorName, TestLicenseKey);
        row.Should().NotBeNull();
        row!.UpdatedAt.Should().Be(new DateTimeOffset(2026, 5, 11, 10, 30, 0, TimeSpan.Zero));
        row.LastId.Should().Be(paymentId);
    }

    /// <summary>N08: Sunucu (UpdatedAt, Id) çiftini SQL Server'ın
    /// uniqueidentifier sırasıyla sayfalıyor; .NET Guid.CompareTo FARKLI bir
    /// sıra üretir (SQL son 6 bayttan başlar). Sözleşme sunucu tarafında
    /// açıkça yazılı: "WPF bir sonraki imleci sayfanın SON satırından okur".
    /// İstemci yeniden sıralayıp .NET-sırasına göre son satırı seçerse imleç
    /// sunucunun sayfa sınırının gerisinde kalır → satırlar tekrar iner.</summary>
    [Fact]
    public async Task Pull_cursor_sunucunun_teslim_ettigi_son_satirdan_okunur()
    {
        // node baytları SQL sırasını belirler: ...0001 < ...0002 (SQL),
        // ama .NET sırasında 00000000-... < ffffffff-...
        var sqlSmall = Guid.Parse("ffffffff-ffff-ffff-ffff-000000000001"); // SQL: küçük, .NET: büyük
        var sqlBig   = Guid.Parse("00000000-0000-0000-0000-000000000002"); // SQL: büyük, .NET: küçük

        var (svc, repo, cursors, _, _) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            if (path.Contains("/payments/since"))
            {
                // Sunucunun teslim sırası (SQL uniqueidentifier): sqlSmall, sqlBig.
                // Aynı UpdatedAt — sayfa sınırı tam bu eşitlik kümesinde kesildi.
                var json = $@"[
                    {{ ""id"": ""{sqlSmall}"", ""status"": ""approved"",
                       ""approvedAt"": ""2026-05-11T10:30:00Z"", ""rejectedAt"": null,
                       ""rejectReason"": null, ""updatedAt"": ""2026-05-11T10:30:00Z"" }},
                    {{ ""id"": ""{sqlBig}"", ""status"": ""approved"",
                       ""approvedAt"": ""2026-05-11T10:30:00Z"", ""rejectedAt"": null,
                       ""rejectReason"": null, ""updatedAt"": ""2026-05-11T10:30:00Z"" }}
                ]";
                return JsonResp(200, json);
            }
            return JsonResp(200, "[]");
        });

        repo.Insert(NewLocalPayment(sqlSmall.ToString()));
        repo.MarkSynced(sqlSmall.ToString(), 1714000000L);
        repo.Insert(NewLocalPayment(sqlBig.ToString(), refNo: "REF-2"));
        repo.MarkSynced(sqlBig.ToString(), 1714000000L);

        var result = await svc.SyncOnceAsync();

        result.Pulled.Should().Be(2);
        cursors.Get(PullCursorName, TestLicenseKey)!.LastId.Should().Be(sqlBig,
            "imleç sunucunun teslim ettiği SON satır olmalı — .NET Guid sırasıyla yeniden seçilirse " +
            "sunucu sayfa sınırının gerisine düşer ve aynı satırlar tekrar iner");
    }

    [Fact]
    public async Task SyncOnceAsync_uses_since_cursor_in_pull_request()
    {
        var initial = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var cursorId = Guid.Parse("00000000-0000-0000-0000-0000000000cc");
        var (svc, repo, cursors, _, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            return JsonResp(200, "[]");
        });

        cursors.Upsert(PullCursorName, TestLicenseKey, updatedAt: initial, lastId: cursorId);
        await svc.SyncOnceAsync();

        var pullCall = requests.FirstOrDefault(r => r.Path.Contains("/payments/since"));
        pullCall.Path.Should().NotBeNull();
        pullCall.Path.Should().Contain("since=2026-05-01", "cursor passed")
            .And.Contain("sinceId=00000000-0000-0000-0000-0000000000cc");
    }

    /// <summary>R9-D02 geri yükleme sözleşmesi: SyncCursor satırı yoksa
    /// (taze kurulum VEYA yedekten dönen DB'de imleç hiç ilerlememiş) pull
    /// baştan başlar — since=MinValue. Eski settings.json imleci TOHUM OLMAZ:
    /// settings yedeğin dışında yaşadığı için ileri kalmış değeri, mobilde
    /// onaylanmış kararın bir daha hiç inmemesine yol açıyordu.</summary>
    [Fact]
    public async Task Pull_imlec_satiri_yoksa_bastan_ceker()
    {
        var (svc, _, _, _, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200, LicensesJson());
            return JsonResp(200, "[]");
        });

        await svc.SyncOnceAsync();

        var pullCall = requests.First(r => r.Path.Contains("/payments/since"));
        pullCall.Path.Should().Contain("since=0001-01-01",
            "satır yokken baştan çekim — ApplyServerStatus idempotent, atlama riski sıfır");
    }

    /// <summary>R9-D02: imleç lisans anahtarına bağlı — anahtar değişince
    /// A'nın ilerlemesi B adına okunmaz, B kendi (boş) satırıyla baştan başlar.</summary>
    [Fact]
    public async Task Pull_imleci_lisans_anahtarina_bagli()
    {
        var (svc, _, cursors, licenseProvider, requests) = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return JsonResp(200,
                    $@"[{{ ""id"": ""{TestLicenseId}"", ""licenseKey"": ""{TestLicenseKey}"",
                        ""skuCode"": ""STD"", ""expiresAt"": ""2030-01-01T00:00:00Z"" }},
                       {{ ""id"": ""22222222-2222-2222-2222-222222222222"", ""licenseKey"": ""LDK-OTHER"",
                        ""skuCode"": ""STD"", ""expiresAt"": ""2030-01-01T00:00:00Z"" }}]");
            return JsonResp(200, "[]");
        });

        // A lisansının imleci ilerlemiş olsun.
        cursors.Upsert(PullCursorName, TestLicenseKey,
            updatedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), lastId: Guid.NewGuid());

        // B lisansına geçiş.
        licenseProvider.CurrentLicenseKey = "LDK-OTHER";
        await svc.SyncOnceAsync();

        var pullCall = requests.First(r => r.Path.Contains("/payments/since"));
        pullCall.Path.Should().Contain("since=0001-01-01",
            "B lisansı A'nın imlecini devralmamalı");
    }

    // N04 testi ("çağrı sırasında yazılan ayarı ezmez") SİLİNDİ: servis artık
    // settings.json'a hiç dokunmuyor (R9-D02) — korunacak davranış kalmadı.

    [Fact]
    public async Task SyncOnceAsync_gracefully_handles_5xx_failure()
    {
        var (svc, repo, _, _, _) = Build(_ => JsonResp(500, "{}"));
        repo.Insert(NewLocalPayment(Guid.NewGuid().ToString()));

        // Should not throw — LicenseId resolve fails, sync skipped
        var result = await svc.SyncOnceAsync();
        result.Pushed.Should().Be(0);
        result.Pulled.Should().Be(0);
    }
}
