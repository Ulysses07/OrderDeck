using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class ShipmentSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1_716_000_000L;
    }

    private static readonly Guid TestLicenseId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffffaaaabbbb");
    private const string TestLicenseKey = "SHIPMENT-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    private sealed record Fixture(
        ShipmentSyncService Svc,
        AppSettings Settings,
        SettingsStore Store,
        InMemorySqlite Db);

    private static Fixture Build(Func<HttpRequestMessage, HttpResponseMessage> responder,
        SyncStatusTracker? tracker = null)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var shipments = new ShipmentRepository(db);

        var settingsPath = Path.Combine(Path.GetTempPath(), $"shipsync-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());

        var licenseProvider = new FakeLicenseProvider { CurrentLicenseKey = TestLicenseKey };

        var svc = new ShipmentSyncService(
            api, shipments, store, settings, licenseProvider,
            new FakeClock(), NullLogger<ShipmentSyncService>.Instance, tracker);

        return new Fixture(svc, settings, store, db);
    }

    // ── R3-01: imleç sunucunun teslim sırasından, yeniden sıralama YOK ────────

    [Fact]
    public async Task Pull_imlec_sunucunun_teslim_ettigi_son_satirdan_okunur()
    {
        // node baytları SQL sırasını belirler: ...0001 < ...0002 (SQL),
        // ama .NET sırasında 00000000-... < ffffffff-...
        var sqlSmall = Guid.Parse("ffffffff-ffff-ffff-ffff-000000000001"); // SQL: küçük, .NET: büyük
        var sqlBig   = Guid.Parse("00000000-0000-0000-0000-000000000002"); // SQL: büyük, .NET: küçük

        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/shipments/since"))
            {
                // Sunucunun teslim sırası (SQL uniqueidentifier): sqlSmall, sqlBig.
                // Aynı UpdatedAt — sayfa sınırı tam bu eşitlik kümesinde kesildi.
                var json = $@"[
                    {{ ""id"": ""{sqlSmall}"", ""customerId"": ""c1"", ""status"": ""held"",
                       ""cumulativeAmount"": 100, ""createdAt"": ""2026-05-11T10:00:00Z"",
                       ""heldAt"": null, ""shippedAt"": null, ""updatedAt"": ""2026-05-11T10:30:00Z"" }},
                    {{ ""id"": ""{sqlBig}"", ""customerId"": ""c2"", ""status"": ""held"",
                       ""cumulativeAmount"": 200, ""createdAt"": ""2026-05-11T10:00:00Z"",
                       ""heldAt"": null, ""shippedAt"": null, ""updatedAt"": ""2026-05-11T10:30:00Z"" }}
                ]";
                return FakeHttpMessageHandler.Json(200, json);
            }
            return FakeHttpMessageHandler.Json(200, "[]");
        });
        using var _d = fx.Db;

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Pulled.Should().Be(2);
        fx.Settings.LastShipmentReverseSyncId.Should().Be(sqlBig,
            "imleç sunucunun teslim ettiği SON satır olmalı — .NET Guid sırasıyla yeniden seçilirse " +
            "sunucu sayfa sınırının gerisine düşer ve aynı satırlar tekrar iner");
        fx.Store.Load().LastShipmentReverseSyncId.Should().Be(sqlBig, "diske de aynı imleç yazılmalı");
    }

    // ── durum satırı: gönderim ilerlemesi (D2 incelemesi I-3) ──────────

    [Fact]
    public async Task Gonderim_ilerlemesi_basarili_gonderimde_ve_bos_kuyrukta_yazilir_hatada_yazilmaz()
    {
        var tracker = new SyncStatusTracker();
        var fail = true;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/shipments/sync"))
                return fail ? FakeHttpMessageHandler.Json(500, "{}") : FakeHttpMessageHandler.Json(200, "[]");
            if (path.Contains("/shipments/since")) return FakeHttpMessageHandler.Json(200, "[]");
            return FakeHttpMessageHandler.Empty(404);
        }, tracker);
        using var _d = fx.Db;
        DateTimeOffset? PushOk() => tracker.Snapshot().PushOkAt![ShipmentSyncService.PushStatusName];
        new ShipmentRepository(fx.Db).Insert(new Shipment(Guid.NewGuid().ToString(), Guid.NewGuid().ToString("N"),
            ShipmentStatus.Pending, 1_716_000_000L, null, null, 250m));

        await fx.Svc.SyncOnceAsync();
        await fx.Svc.SyncOnceAsync();
        PushOk().Should().BeNull("gönderim her turda düştü");

        fail = false;
        await fx.Svc.SyncOnceAsync();
        var sent = PushOk();
        sent.Should().NotBeNull();

        await fx.Svc.SyncOnceAsync();
        PushOk().Should().BeOnOrAfter(sent!.Value, "gönderecek bir şey olmayan tur da sağlıklı");
    }
}
