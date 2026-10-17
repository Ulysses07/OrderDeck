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

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Kargo gönderiminin v2 zarfı (spec 2026-10-13 §4.0, §7.1; W1-06): istek kopya kimliği, protokol 2 ve görülen kip
/// 0 taşır, sunucu yeni dosyanın <c>OriginReplicaId</c>'sini bu kopya yazar; eski sunucunun v1 yanıtı da okunur.
/// Sunucu <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class ShipmentSyncV2Tests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => 1_760_000_000L;
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-shipv2-{Guid.NewGuid():N}.json");
    private readonly ShipmentRepository _shipments;
    private readonly License _license;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ShipmentSyncService _svc;

    public ShipmentSyncV2Tests()
    {
        new MigrationRunner(_db).Run();
        _shipments = new ShipmentRepository(_db);
        _license = new License(_server.Lisans);
        _envelope = TestSalesEnvelope.For(_db, _license);
        var store = new SettingsStore(_settingsPath);
        _svc = new ShipmentSyncService(Api(), _shipments, store, store.Load(), _license, _envelope, new Clock(),
            NullLogger<ShipmentSyncService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        File.Delete(_settingsPath);
    }

    private LicenseApiClient Api() => new(
        new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
        new LicenseTokenStore());

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    private string AddShipment(decimal amount = 300m)
    {
        var id = Guid.NewGuid().ToString("N");
        _shipments.Insert(new Shipment(id, Guid.NewGuid().ToString("N"), ShipmentStatus.Held, 1_760_000_000L,
            1_760_000_050L, null, amount));
        return id;
    }

    [Fact]
    public async Task Kargo_gonderimi_v2_zarfini_tasir_sunucu_dosyanin_kaynak_kopyasini_yazar()
    {
        var id = AddShipment();

        var result = await _svc.SyncOnceAsync();

        result.Pushed.Should().Be(1);
        _server.Faults.Should().BeEmpty();
        var root = _server.Calls(FakeSalesServer.ShipmentsSync).Single().Json;
        root.GetProperty("replicaId").GetGuid().Should().Be(ReplicaId);
        root.GetProperty("protocol").GetInt32().Should().Be(2);
        root.GetProperty("seenMode").GetInt32().Should().Be(0);
        _server.Shipment(Guid.Parse(id))!.OriginReplicaId.Should().Be(ReplicaId,
            "mod Off'ta dosyanın tek yazıcısı bu kopya (§3.1)");
        _shipments.GetUnsynced().Should().BeEmpty("yanıt okunur, bugünkü gibi onaylanır");
    }

    [Fact]
    public async Task Eski_sunucunun_v1_yaniti_okunur_dosya_onaylanir()
    {
        _server.ShipmentsResponse = FakeSalesServer.PushResponseKind.V1List;
        AddShipment();

        var result = await _svc.SyncOnceAsync();

        result.Pushed.Should().Be(1);
        _shipments.GetUnsynced().Should().BeEmpty("v1 yanıtı çözümlenemeseydi gönderim hata sayılır, onay yazılmazdı");
    }

    [Fact]
    public async Task Istemci_v1_ve_v2_kargo_yanitini_ayni_tiple_okur()
    {
        var id = AddShipment(410m);
        var api = Api();
        var item = new OrderDeck.Licensing.Api.Models.SyncShipmentItem(Guid.Parse(id), "musteri", "held", 410m,
            DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L), null, null);

        _server.ShipmentsResponse = FakeSalesServer.PushResponseKind.V1List;
        var v1 = await api.SyncShipmentsAsync(_server.LicenseId, new([item]));
        _server.ShipmentsResponse = FakeSalesServer.PushResponseKind.Auto;
        var v2 = await api.SyncShipmentsAsync(_server.LicenseId, new([item], ReplicaId, 2, 0));

        v1.Should().ContainSingle().Which.Should().Match<OrderDeck.Licensing.Api.Models.SalesShipmentItem>(s =>
            s.Id == Guid.Parse(id) && s.Status == "held" && s.CumulativeAmount == 410m && s.OriginReplicaId == null);
        v2.Should().ContainSingle().Which.Should().Match<OrderDeck.Licensing.Api.Models.SalesShipmentItem>(s =>
            s.Id == Guid.Parse(id) && s.OriginReplicaId == ReplicaId && s.Version == 1);
    }

    [Fact]
    public async Task Geri_yuklemeden_sonra_hello_gidene_kadar_kargo_gonderilmez()
    {
        new ReplicaIdentityRepository(_db).SetPendingPreviousReplicaId(Guid.NewGuid());
        AddShipment();

        await _svc.SyncOnceAsync();

        _server.Calls(FakeSalesServer.ShipmentsSync).Should().BeEmpty();
        _shipments.GetUnsynced().Should().ContainSingle();

        _envelope.MarkHelloSucceeded(_server.Lisans, ReplicaId);
        await _svc.SyncOnceAsync();
        _shipments.GetUnsynced().Should().BeEmpty();
    }
}
