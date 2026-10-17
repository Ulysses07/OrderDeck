using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
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
/// W2-08: mod On'da servis hiç istek göndermez; mod Off'ta v2 yanıtının satırı yerel aynaya yazılır (F05 koşuluyla).
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

    // ── W2-08 (spec 2026-10-13 §7.1): mod On'da sıfır HTTP; mod Off'ta v2 yanıtı yerel aynaya ────────────

    /// <summary>Yerel kipi okuyan servis (uygulamadaki DI kaydı gibi).</summary>
    private ShipmentSyncService ModeAware()
    {
        var store = new SettingsStore(_settingsPath);
        return new ShipmentSyncService(Api(), _shipments, store, store.Load(), _license, _envelope, new Clock(),
            NullLogger<ShipmentSyncService>.Instance, salesState: new SalesSyncStateRepository(_db));
    }

    private void SetMode(int mode)
    {
        using var c = _db.Open();
        Dapper.SqlMapper.Execute(c, "UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
    }

    [Fact]
    public async Task Mod_On_kargo_servisi_hic_istek_gondermez_ne_gonderim_ne_ters_cekme()
    {
        AddShipment();
        SetMode(1);
        var svc = ModeAware();

        var result = await svc.SyncOnceAsync();

        result.Should().Be(default(ShipmentSyncService.SyncResult));
        _server.PushCalls.Should().BeEmpty("mod On'da dosya sunucunun; gönderim yok (§7.1)");
        _server.ShipmentsSinceRequestCount.Should().Be(0, "/since çekmesi de kalkar");
        _server.LicenseRequestCount.Should().Be(0, "lisans çözümü dahil hiç HTTP yok");
        _server.SalesRequestCount.Should().Be(0);
        _shipments.GetUnsynced().Should().ContainSingle("kuyruk olduğu gibi kalır; mod On'da yerel kargo yazımı zaten yok");

        // Kıyas: aynı servis mod Off'ta bugünkü gibi gönderir ve ters çeker.
        SetMode(0);
        (await svc.SyncOnceAsync()).Pushed.Should().Be(1);
        _server.ShipmentsSinceRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Mod_Off_sunucuda_kargolanmis_dosya_yerel_aynada_Shipped_olur_sonraki_dekont_yeni_dosya_acar()
    {
        // PC-A kapalıyken başka bilgisayar dosyayı kargolamış; yerel ayna hâlâ Held (bayat karar) ve gönderilmeyi
        // bekliyor. Sunucu Shipped'i tek yönlü tutar; v2 yanıtı sunucunun satırını döner (§7.1).
        var customers = new CustomerRepository(_db);
        var customer = new Customer(Guid.NewGuid().ToString("N"), "instagram", "@alici_ornek", null, null, 100, 200,
            false, null, null, 0, 0m, null, null, null);
        customers.Insert(customer);
        var shipmentId = Guid.NewGuid().ToString("N");
        _shipments.Insert(new Shipment(shipmentId, customer.Id, ShipmentStatus.Held, 1_760_000_000L, 1_760_000_050L,
            null, 300m));
        var shippedAt = DateTimeOffset.FromUnixTimeSeconds(1_760_000_500L);
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(Guid.Parse(shipmentId), customer.Id, "shipped", 300m,
            DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L), DateTimeOffset.FromUnixTimeSeconds(1_760_000_050L),
            shippedAt), origin: ReplicaId);

        var result = await ModeAware().SyncOnceAsync();

        result.Pushed.Should().Be(1);
        _server.Shipment(Guid.Parse(shipmentId))!.Row.Status.Should().Be("shipped", "Shipped tek yönlü");
        var local = _shipments.GetById(shipmentId)!;
        local.Status.Should().Be(ShipmentStatus.Shipped, "v2 yanıtındaki sunucu satırı yerel aynaya yazılır");
        local.ShippedAt.Should().Be(shippedAt.ToUnixTimeSeconds());
        local.ServerVersion.Should().Be(_server.Shipment(Guid.Parse(shipmentId))!.Version);
        local.Revision.Should().Be(0, "kilit altında yazıldı: kuyruğa alma tetikleyicisi susar");
        _shipments.GetUnsynced().Should().BeEmpty("yankı yok: sunucunun satırı yeniden gönderilmez");

        // Sonraki dekont: kargolanmış dosyaya değil yeni dosyaya bağlanır.
        var sessions = new SessionRepository(_db);
        var labels = new LabelRepository(_db);
        var sessionId = Guid.NewGuid().ToString("N");
        sessions.Insert(new StreamSession(sessionId, null, 1_760_001_000L, null, ["instagram"], null));
        var labelId = Guid.NewGuid().ToString("N");
        labels.Insert(new Label(labelId, sessionId, customer.Id, "instagram", "@alici_ornek", "ürün", null, 120m,
            1_760_001_100L, null));
        var settings = new AppSettings();
        var vm = new OrderDeck.App.ViewModels.DekontEkleViewModel(new PaymentRepository(_db), customers, sessions,
            new PaymentMatcherService(labels, () => settings), labels,
            new ShipmentService(_shipments, labels, () => settings, () => 1_760_002_000L),
            new OrderDeck.PdfParsing.PdfDekontParser(), settings, new Clock(),
            NullLogger<OrderDeck.App.ViewModels.DekontEkleViewModel>.Instance)
        {
            PayerName = "Gönderen", AmountText = "120", ReferansNo = $"ref-{Guid.NewGuid():N}",
            PaidAt = DateTime.Today, CustomerPlatform = "instagram", CustomerUsername = "@alici_ornek",
        };

        vm.TrySave().Kind.Should().Be(OrderDeck.App.ViewModels.DekontEkleViewModel.SaveResultKind.Saved);

        var attached = labels.GetById(labelId)!.ShipmentId;
        attached.Should().NotBeNull().And.NotBe(shipmentId, "kargolanmış dosyaya etiket bağlanmaz");
        _shipments.GetById(attached!)!.Status.Should().Be(ShipmentStatus.Pending);
        _shipments.GetById(shipmentId)!.CumulativeAmount.Should().Be(300m);
    }

    [Fact]
    public async Task Mod_Off_ucusta_yerelde_degisen_dosyaya_sunucunun_satiri_yazilmaz()
    {
        var shipmentId = AddShipment(300m);
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(Guid.Parse(shipmentId), "musteri", "shipped", 300m,
            DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L), null, DateTimeOffset.FromUnixTimeSeconds(1_760_000_500L)),
            origin: ReplicaId);
        var changed = false;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.ShipmentsSync || changed) return;
            changed = true;
            var row = _shipments.GetById(shipmentId)!;
            _shipments.Update(row with { CumulativeAmount = 450m });           // operatör uçuşta tutarı değiştirdi
        };

        await ModeAware().SyncOnceAsync();

        var local = _shipments.GetById(shipmentId)!;
        local.Status.Should().Be(ShipmentStatus.Held, "F05: gönderilen Revision değişti — sunucunun satırı yazılmaz");
        local.CumulativeAmount.Should().Be(450m);
        local.SyncedAt.Should().BeNull("yeni değer sonraki turda gider, sunucu yeniden yanıtlar");
    }

    [Fact]
    public async Task Geri_yuklemeden_sonra_hello_state_ve_kip_adimi_olana_kadar_kargo_gonderilmez()
    {
        new ReplicaIdentityRepository(_db).SetPendingPreviousReplicaId(Guid.NewGuid());
        AddShipment();

        await _svc.SyncOnceAsync();

        _server.Calls(FakeSalesServer.ShipmentsSync).Should().BeEmpty();
        _shipments.GetUnsynced().Should().ContainSingle();

        _envelope.MarkHelloSucceeded(_server.Lisans, ReplicaId);
        await _svc.SyncOnceAsync();
        _server.Calls(FakeSalesServer.ShipmentsSync).Should().BeEmpty("W2-10 (§8.7): hello yetmez — state ve kip adımı da");

        _envelope.MarkStateSucceeded(_server.Lisans, ReplicaId);
        _envelope.MarkModeRefreshed(_server.Lisans, ReplicaId);
        await _svc.SyncOnceAsync();
        _shipments.GetUnsynced().Should().BeEmpty();
    }
}
