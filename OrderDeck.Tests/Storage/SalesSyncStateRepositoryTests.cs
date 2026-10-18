using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Yerel satış durumu (spec 2026-10-13 §2, §3.2, §8.4, §8.5): görülen kip kuralı, sunucu durumunun saklanması ve mod
/// On açılışının yerel yazıcıları (W2-10): 1. adımın kuyruk kalanı, 2. adımın kilit altındaki yeniden denetimi ve tek
/// işlemdeki yazımı (<see cref="SalesSyncStateRepository.TryEnableMode"/>), 4. adımın ufuktaki türetmesi
/// (<see cref="SalesSyncStateRepository.CompleteFirstCatchUp"/>).
/// </summary>
public sealed class SalesSyncStateRepositoryTests : IDisposable
{
    private const string Cursor = "sales-changes-in";
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";
    private const long T0 = 1_760_000_000L;

    private readonly InMemorySqlite _db = new();
    private readonly SalesSyncStateRepository _repo;

    public SalesSyncStateRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _repo = new SalesSyncStateRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private T Scalar<T>(string sql, object? param = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    private string Customer(decimal totalAmount = 0m)
    {
        var id = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 1, 1, false,
            null, null, 0, totalAmount, null, null, null));
        return id;
    }

    /// <summary>Bu bilgisayarın yayını; <paramref name="endedAt"/> boşsa canlı.</summary>
    private string Session(long? endedAt, bool synced = true, long? releasedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(id, "Yayın", T0, endedAt, ["tiktok"], null,
            SyncedAt: synced ? T0 + 10 : null));
        if (releasedAt is { } r) Exec("UPDATE StreamSession SET ReleasedAt = @r WHERE Id = @id", new { r, id });
        return id;
    }

    private string Label(string sessionId, string customerId, bool synced = true, long? printedAt = T0 + 120,
        decimal price = 40m)
    {
        var id = Guid.NewGuid().ToString("N");
        new LabelRepository(_db).Insert(new Label(id, sessionId, customerId, "tiktok", "alici", "yorum", "B2", price,
            T0 + 60, printedAt, SyncedAt: synced ? T0 + 200 : null));
        return id;
    }

    private string Shipment(string customerId, bool synced)
    {
        var id = Guid.NewGuid().ToString("N");
        new ShipmentRepository(_db).Insert(new Shipment(id, customerId, ShipmentStatus.Pending, T0, null, null, 40m,
            SyncedAt: synced ? T0 + 10 : null));
        return id;
    }

    private void Payment(bool synced)
    {
        new PaymentRepository(_db).Insert(new Payment(Guid.NewGuid().ToString("N"), $"odeyen-{Guid.NewGuid():N}", 40m,
            T0, $"ref-{Guid.NewGuid():N}", null, PaymentStatus.Pending, T0, T0, synced ? T0 + 10 : null, null, null, null));
    }

    private (string Authority, long? SyncedAt, long Revision) SessionRow(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<(string, long?, long)>(
            "SELECT Authority, SyncedAt, Revision FROM StreamSession WHERE Id = @id", new { id });
    }

    private SalesModeEnableResult Enable(long? modeSeenAt = T0 + 5_000, long now = T0 + 9_000) =>
        _repo.TryEnableMode(modeSeenAt, Cursor, Lisans, now);

    [Fact]
    public void Gorulen_kip_yalniz_mod_0_ve_bekleyen_komut_yokken_0()
    {
        _repo.GetMode().Should().Be(0, "049 tohumu: mod Off");
        _repo.HasPendingSalesCommands().Should().BeFalse();
        _repo.GetSeenMode().Should().Be(0);

        Exec("INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt) VALUES ('k1', 'cancel', '{}', 1)");
        _repo.HasPendingSalesCommands().Should().BeTrue();
        _repo.GetSeenMode().Should().Be(1, "Off turu bekleyen komut çevrilene kadar bitmiş sayılmaz (§8.5)");

        Exec("DELETE FROM PendingSalesCommand");
        _repo.GetSeenMode().Should().Be(0);

        Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        _repo.GetMode().Should().Be(1);
        _repo.GetSeenMode().Should().Be(1);
    }

    [Fact]
    public void Sunucu_durumu_saklanir_ayniysa_yazilmaz_kip_alanlarina_dokunulmaz()
    {
        Exec("UPDATE SalesSyncState SET ModeSeenAt = 77, FirstCatchUpAt = 88 WHERE Id = 1");

        _repo.SaveServerState(3, 1500m, 89.90m, scrubOrderText: true).Should().BeTrue();
        var row = _repo.Get();
        row.ShippingVersion.Should().Be(3);
        row.FreeShippingThreshold.Should().Be(1500m);
        row.ShippingFee.Should().Be(89.90m);
        row.ScrubOrderText.Should().BeTrue();

        _repo.SaveServerState(3, 1500m, 89.90m, scrubOrderText: true).Should().BeFalse("aynı değerler yazma kilidi almaz");

        // Ayar hiç yazılmamış lisans: üçü de boş.
        _repo.SaveServerState(null, null, null, scrubOrderText: false).Should().BeTrue();
        row = _repo.Get();
        row.ShippingVersion.Should().BeNull();
        row.FreeShippingThreshold.Should().BeNull();
        row.ShippingFee.Should().BeNull();
        row.ScrubOrderText.Should().BeFalse();

        row.Mode.Should().Be(0);
        row.ModeSeenAt.Should().Be(77);
        row.FirstCatchUpAt.Should().Be(88);
    }

    // ── mod On açılışı, 1. adım: kuyruk kalanı (§8.4-1) ────────────────────

    [Fact]
    public void Acilis_kuyruk_kalani_her_giden_kuyrugu_sayar_canli_yayini_ve_tavandan_sonraki_musteriyi_saymaz()
    {
        var customer = Customer();
        var ended = Session(endedAt: T0 + 3600, synced: false);
        Label(ended, customer, synced: false);
        Label(ended, customer, synced: false);
        var live = Session(endedAt: null, synced: false);
        Label(live, customer, synced: false);              // canlı yayının etiketi iki kipte aynı yargılanır
        Shipment(customer, synced: false);
        Payment(synced: false);
        Payment(synced: true);
        Exec("INSERT INTO PendingPaymentRecord (RecordId, Kind, PayloadJson, CreatedAt) VALUES ('r1', 'record', '{}', 1)");
        var ceiling = _repo.MaxCustomerSyncSeq();
        ceiling.Should().BeGreaterThan(0);
        Customer();                                        // tavandan sonra doğan müşteri (yayında sohbet) sayılmaz

        var backlog = _repo.GetOpeningBacklog(customerWatermark: 0, customerCeiling: ceiling);

        backlog.Should().Be(new SalesOpeningBacklog(Customers: 1, EndedSessions: 1, EndedSessionLabels: 2, Shipments: 1,
            Payments: 1, PaymentRecords: 1));
        backlog.IsEmpty.Should().BeFalse();
        _repo.GetOpeningBacklog(customerWatermark: ceiling, customerCeiling: ceiling).Customers.Should().Be(0,
            "imleç tavanı geçtiyse müşteri kuyruğu boşaldı");
    }

    // ── 2. adım: kilit altında yeniden denetim (§8.4-2) ───────────────────

    [Fact]
    public void Acilis_gonderilmemis_kargo_dosyasi_varsa_hicbir_sey_yazmaz()
    {
        var customer = Customer();
        var own = Session(endedAt: T0 + 3600, releasedAt: T0 + 3700);
        Exec("INSERT INTO SyncCursor (Name, LicenseKey, Seq) VALUES (@Cursor, @Lisans, 77)", new { Cursor, Lisans });
        Shipment(customer, synced: false);                 // 1. adım gönderirken kaydedilen dekontun kargo yazımı

        var result = Enable();

        result.Status.Should().Be(SalesModeEnableStatus.Backlog);
        result.PendingShipments.Should().Be(1);
        _repo.GetMode().Should().Be(0, "yerel mod 0 kalır; sonraki turda 1. adımdan devam");
        _repo.Get().ModeSeenAt.Should().BeNull();
        SessionRow(own).Authority.Should().Be(StreamSessionAuthority.Local);
        Scalar<long>("SELECT Seq FROM SyncCursor WHERE Name = @Cursor", new { Cursor }).Should().Be(77);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(0);
    }

    [Fact]
    public void Acilis_bitmis_yayinin_gonderilmemis_etiketi_varsa_yazmaz_canli_yayinin_etiketi_engellemez()
    {
        var customer = Customer();
        var ended = Session(endedAt: T0 + 3600);
        var pending = Label(ended, customer, synced: false);

        Enable().Should().Be(new SalesModeEnableResult(SalesModeEnableStatus.Backlog, 0, 1, 0));
        _repo.GetMode().Should().Be(0);

        new LabelRepository(_db).MarkSynced(pending, T0 + 300, new LabelRepository(_db).GetById(pending)!.Revision);
        var live = Session(endedAt: null);
        Label(live, customer, synced: false);              // canlı yayının etiketi denetime girmez

        Enable().Status.Should().Be(SalesModeEnableStatus.Enabled);
        _repo.GetMode().Should().Be(1);
    }

    [Fact]
    public void Acilis_kipi_ve_gorulen_ani_yazar_devri_bilinen_bitmis_yayinlari_sunucuya_verir_imleci_sifirlar()
    {
        var released = Session(endedAt: T0 + 3600, releasedAt: T0 + 3700);
        var unreleased = Session(endedAt: T0 + 7200);                          // devri yerelde bilinmiyor
        var live = Session(endedAt: null, releasedAt: T0 + 4000);              // force-end: yayın açık, yerel kalır
        var before = SessionRow(released);
        Exec("INSERT INTO SyncCursor (Name, LicenseKey, Seq) VALUES (@Cursor, @Lisans, 999)", new { Cursor, Lisans });
        Exec("UPDATE SalesSyncState SET FirstCatchUpAt = 123 WHERE Id = 1");   // önceki mod On dönemi

        var result = Enable(modeSeenAt: T0 + 5_000);

        result.Should().Be(new SalesModeEnableResult(SalesModeEnableStatus.Enabled, 0, 0, 1));
        var state = _repo.Get();
        state.Mode.Should().Be(1);
        state.ModeSeenAt.Should().Be(T0 + 5_000);
        state.FirstCatchUpAt.Should().BeNull("ilk yetişme bu açılış için baştan (§8.5 Off → On)");
        SessionRow(released).Should().Be((StreamSessionAuthority.Server, before.SyncedAt, before.Revision),
            "devri bilinen kendi bitmiş yayını hemen 'server'; kuyruğa girmez (SyncedAt ve Revision değişmez)");
        SessionRow(unreleased).Authority.Should().Be(StreamSessionAuthority.Local, "ilk yetişmeye kadar düzenlemeye kapalı");
        SessionRow(live).Authority.Should().Be(StreamSessionAuthority.Local, "canlı yayın yerel kalır");
        Scalar<long>("SELECT Seq FROM SyncCursor WHERE Name = @Cursor AND LicenseKey = @Lisans", new { Cursor, Lisans })
            .Should().Be(0, "akış 0'dan (§8.4-3)");
        Scalar<string>("SELECT Kind FROM SalesConflictLog").Should().Be(SalesConflictLog.ModeOpened);

        // Açılış bir kez: mod zaten 1 ise hiçbir şey yazmaz.
        Exec("UPDATE SyncCursor SET Seq = 55 WHERE Name = @Cursor", new { Cursor });
        Enable(modeSeenAt: T0 + 6_000).Status.Should().Be(SalesModeEnableStatus.AlreadyOn);
        _repo.Get().ModeSeenAt.Should().Be(T0 + 5_000);
        Scalar<long>("SELECT Seq FROM SyncCursor WHERE Name = @Cursor", new { Cursor }).Should().Be(55);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1);
    }

    [Fact]
    public void Yeni_mod_On_donemi_yerel_kip_1_iken_imleci_ve_ilk_yetismeyi_bastan_alir_ayni_donemde_yazmaz()
    {
        Enable(modeSeenAt: T0 + 5_000).Status.Should().Be(SalesModeEnableStatus.Enabled);
        Exec("UPDATE SalesSyncState SET FirstCatchUpAt = 123 WHERE Id = 1");
        Exec("UPDATE SyncCursor SET Seq = 77 WHERE Name = @Cursor", new { Cursor });

        _repo.TryReopenMode(T0 + 5_000, Cursor, Lisans, T0 + 9_500).Should().BeNull("dönem aynı");
        _repo.TryReopenMode(T0 + 4_000, Cursor, Lisans, T0 + 9_500).Should().BeNull("eski dönem");
        Scalar<long>("SELECT Seq FROM SyncCursor WHERE Name = @Cursor", new { Cursor }).Should().Be(77);
        _repo.Get().FirstCatchUpAt.Should().Be(123);

        _repo.TryReopenMode(T0 + 8_000, Cursor, Lisans, T0 + 9_500).Should().Be(0);

        var state = _repo.Get();
        state.Mode.Should().Be(1);
        state.ModeSeenAt.Should().Be(T0 + 8_000);
        state.FirstCatchUpAt.Should().BeNull("yeni dönemin ilk yetişmesi baştan");
        Scalar<long>("SELECT Seq FROM SyncCursor WHERE Name = @Cursor", new { Cursor }).Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @k", new { k = SalesConflictLog.ModeOpened })
            .Should().Be(2);

        Exec("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");
        _repo.TryReopenMode(T0 + 9_000, Cursor, Lisans, T0 + 9_500).Should().BeNull("yerel kip 0: olağan açılış yolu");
    }

    [Fact]
    public void Mod_1_yazildiktan_sonra_yerel_kargo_yazimi_ABORT_eder_gonderilmemis_dosya_dogamaz()
    {
        // §10.3: 2. adımdan sonra SyncedAt IS NULL yerel dosya oluşamaz (kilit altındaki denetim + ABORT).
        var customer = Customer();
        var session = Session(endedAt: T0 + 3600, releasedAt: T0 + 3700);
        var label = Label(session, customer);
        var existing = Shipment(customer, synced: true);
        var shipments = new ShipmentService(new ShipmentRepository(_db), new LabelRepository(_db), () => new AppSettings(),
            () => T0 + 10_000);

        Enable().Status.Should().Be(SalesModeEnableStatus.Enabled);

        var other = Customer();
        FluentActions.Invoking(() => shipments.GetOrCreateOpenShipment(other))
            .Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*");
        FluentActions.Invoking(() => shipments.ApplyDecision(existing, ShipmentDecision.Hold))
            .Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*");
        FluentActions.Invoking(() => new ShipmentRepository(_db).AttachLabel(existing, label))
            .Should().Throw<SqliteException>();
        Scalar<long>("SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL").Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM Shipment").Should().Be(1);
    }

    // ── 4. adım: ufukta türetme (§8.4-4, §6.1) ────────────────────────────

    [Fact]
    public void Ilk_yetisme_ufukta_toplamlari_turetir_FirstCatchUpAt_yazar_acilistan_beri_ustune_yazimi_sayar()
    {
        var customer = Customer();
        var session = Session(endedAt: T0 + 3600, releasedAt: T0 + 3700);
        Label(session, customer, price: 40m);
        Label(session, customer, price: 60m);
        Exec("UPDATE Customer SET TotalAmount = 999, TotalLabelsPrinted = 9 WHERE Id = @customer", new { customer });
        _repo.CompleteFirstCatchUp(T0 + 9_500).Should().BeNull("mod Off'ta ilk yetişme yok");

        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind) VALUES (1, 'eski', 'catchup-overwrite')");  // önceki dönem
        Enable().Status.Should().Be(SalesModeEnableStatus.Enabled);
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind) VALUES (2, 'a', 'catchup-overwrite')");
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind) VALUES (3, 'b', 'catchup-overwrite')");
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind) VALUES (4, 'c', 'push-ignored')");

        var done = _repo.CompleteFirstCatchUp(T0 + 10_000);

        done.Should().NotBeNull();
        done!.CatchUpOverwrites.Should().Be(2, "yalnız bu açılıştan beri düşen catchup-overwrite izleri");
        done.Aggregates.ChangedRows.Should().Be(1);
        _repo.Get().FirstCatchUpAt.Should().Be(T0 + 10_000);
        Scalar<double>("SELECT TotalAmount FROM Customer WHERE Id = @customer", new { customer }).Should().Be(100d);
        Scalar<long>("SELECT TotalLabelsPrinted FROM Customer WHERE Id = @customer", new { customer }).Should().Be(2);

        _repo.CompleteFirstCatchUp(T0 + 11_000).Should().BeNull("bir kez");
        _repo.Get().FirstCatchUpAt.Should().Be(T0 + 10_000);
    }
}
