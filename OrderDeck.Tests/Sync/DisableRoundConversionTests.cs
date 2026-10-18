using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// <c>disable</c> turunun yerel adımları (spec 2026-10-13 §8.5-2/-3, §5.5 "Mod Off'a dönüşte", §6.2; W2-11) —
/// <see cref="SalesModeOffRound"/>: 2. adımdan sonra lisans geneli kargo ayarı yerel ayara (disk ve canlı örnek) yazılır;
/// bekleyen iptal <see cref="LabelService.Cancel"/>'a, bekleyen baskı onayı aynı basış kimliği ve sırayla
/// <see cref="LabelService.MarkPrintedAndRecord(IReadOnlyList{string}, string, IReadOnlyList{int})"/>'a çevrilir —
/// kuyruğa girer, basılmış kâğıt yeniden teklif edilmez, iptal edilmiş etiketin basılmış kâğıdı "imha et" olur; her
/// satır kendi yazımından sonra düşer; yazım düşerse kuyrukta kalır ve <see cref="SalesModeOffRound.Resume"/> yeniden
/// dener; görülen kip ancak kuyruk boşalınca 0.
/// </summary>
public sealed class DisableRoundConversionTests : IDisposable
{
    private const long T0 = 1_790_980_000L;
    private const long ModeChangedAt = 1_790_995_000L;
    private const string Reason = "musteri-vazgecti";

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private readonly InMemorySqlite _db = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-disable-conv-{Guid.NewGuid():N}.json");
    private readonly SettingsStore _store;
    private readonly AppSettings _live;
    private readonly SalesModeOffRound _round;
    private readonly Guid _replica;
    private readonly string _customer;
    private readonly string _session;

    public DisableRoundConversionTests()
    {
        new MigrationRunner(_db).Run();
        _store = new SettingsStore(_settingsPath);
        _store.Update(s => s.Shipping = new ShippingSettings { FreeShippingThreshold = 500m, ShippingFee = 50m });
        _live = _store.Load();
        _round = NewRound();
        _replica = new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

        _customer = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(_customer, "tiktok", "alici_" + _customer[..8], "Takma Ad", null,
            1, 1, false, null, null, 0, 0m, null, null, null));
        _session = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(_session, "Kendi yayını", T0, T0 + 3600, ["tiktok"], null,
            SyncedAt: T0 + 3700));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _settingsPath, _settingsPath + ".bak", _settingsPath + ".tmp" })
            try { File.Delete(f); } catch { /* en iyi çaba */ }
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    /// <summary>Turun yerel adımları; aynı veritabanı ve ayar örneğiyle yenisi "yeniden açılış"tır.</summary>
    private SalesModeOffRound NewRound()
    {
        var clock = new Clock();
        var labels = new LabelRepository(_db);
        var labelService = new LabelService(labels,
            new CustomerService(new CustomerRepository(_db), new SessionRepository(_db), labels, clock), _db, clock);
        return new SalesModeOffRound(new SalesFeedRepository(_db), new SalesSyncStateRepository(_db),
            new PendingSalesCommandRepository(_db), labelService, labels, new SalesConflictLogRepository(_db), _store,
            _live, clock, NullLogger<SalesModeOffRound>.Instance);
    }

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

    /// <summary>Devredilmiş kendi yayınından kalan, basılmamış kuyruk etiketi (onaylı).</summary>
    private string Residual()
    {
        var id = Guid.NewGuid().ToString("N");
        new LabelRepository(_db).Insert(new Label(id, _session, _customer, "tiktok", "alici", "yorum", "A1", 40m,
            T0 + 60, PrintedAt: null, DisplayName: "Takma Ad", SyncedAt: T0 + 200));
        return id;
    }

    /// <summary>Başka bilgisayarın devredilmiş yayınındaki basılmamış etiket (ör. <c>add-backup</c> ile eklenmiş yedek):
    /// akıştan inmiş gibi <c>Origin = 'remote'</c>, <c>Authority = 'server'</c>.</summary>
    private string RemoteLabel()
    {
        var session = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(session, "Başka bilgisayarın yayını", T0, T0 + 3600, ["tiktok"],
            null, SyncedAt: T0 + 3700));
        var id = Guid.NewGuid().ToString("N");
        new LabelRepository(_db).Insert(new Label(id, session, _customer, "tiktok", "alici", "yorum", "A1", 70m,
            T0 + 90, PrintedAt: null, DisplayName: "Takma Ad", SyncedAt: T0 + 200));
        Exec("UPDATE StreamSession SET Origin = 'remote', Authority = 'server', ReleasedAt = @r WHERE Id = @s",
            new { r = T0 + 3650, s = session });
        return id;
    }

    /// <summary>Mod On'da devri görülmüş yayın: yerel kip 1, yayın sunucu otoritesinde.</summary>
    private void ModeOnReleased()
    {
        Exec("UPDATE SalesSyncState SET Mode = 1, ModeSeenAt = 10, FirstCatchUpAt = 20 WHERE Id = 1");
        Exec("UPDATE StreamSession SET ReleasedAt = @r, Authority = 'server' WHERE Id = @s", new { r = T0 + 3650, s = _session });
    }

    private string EnqueueCancel(params string[] labelIds)
    {
        var intent = new SalesCommandIntent(Guid.NewGuid(), SalesCommandKinds.Cancel,
            new SalesCancelPayload(labelIds.Select(id => Guid.ParseExact(id, "N")).ToList(), Reason).ToWireJson(), []);
        new PendingSalesCommandRepository(_db).Enqueue(intent.CommandId.ToString("N"), intent.Kind,
            PendingSalesCommandCodec.Encode(intent), labelIds, T0 + 10).Should().BeTrue("ön koşul: yerel kip 1");
        return intent.CommandId.ToString("N");
    }

    private string EnqueuePrint(string batchId, params (string Id, int Seq)[] items)
    {
        var intent = new SalesCommandIntent(Guid.NewGuid(), SalesCommandKinds.MarkPrinted,
            new SalesMarkPrintedPayload(batchId,
                items.Select(i => new SalesMarkPrintedItem(Guid.ParseExact(i.Id, "N"), i.Seq)).ToList()).ToWireJson(), []);
        new PendingSalesCommandRepository(_db).Enqueue(intent.CommandId.ToString("N"), intent.Kind,
            PendingSalesCommandCodec.Encode(intent), items.Select(i => i.Id).ToList(), T0 + 20).Should().BeTrue();
        return intent.CommandId.ToString("N");
    }

    private (long? PrintedAt, string? Batch, long? Seq, long? CancelledAt, string? Reason, long? SyncedAt) Row(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<(long?, string?, long?, long?, string?, long?)>(
            "SELECT PrintedAt, PrintBatchId, PrintSeq, CancelledAt, CancelReason, SyncedAt FROM Label WHERE Id = @id",
            new { id });
    }

    private int PendingCount() => Scalar<int>("SELECT COUNT(*) FROM PendingSalesCommand");

    private int SeenMode() => new SalesSyncStateRepository(_db).GetSeenMode();

    // ── 3. adım ───────────────────────────────────────────────────────────

    [Fact]
    public void Bekleyen_iptal_ve_baski_onayi_yerel_yazima_cevrilir_kuyruga_girer_kagit_yeniden_teklif_edilmez()
    {
        var cancel = Residual();
        var printed = Residual();
        var cancelledElsewhere = Residual();
        ModeOnReleased();
        EnqueueCancel(cancel);
        var batch = LabelService.NewPrintBatchId();
        EnqueuePrint(batch, (printed, 2), (cancelledElsewhere, 1));
        // Son akış: başka bilgisayar bu arada kâğıdı basılmış etiketi iptal etmişti (akış satırı kilit altında yazar).
        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute("UPDATE Label SET CancelledAt = @at, CancelReason = 'stok-yok' WHERE Id = @id",
                new { at = T0 + 4000, id = cancelledElsewhere });
            scope.Commit();
        }
        SeenMode().Should().Be(1);

        _round.ApplyLocal(ModeChangedAt, _replica).Busy.Should().BeFalse();
        SeenMode().Should().Be(1, "2. adımdan sonra kuyruk dolu: Off turu bitmedi (§2)");
        var result = _round.ConvertPendingCommands();

        result.Should().BeEquivalentTo(new { Converted = 2, Dropped = 0, Remaining = 0 });
        var c = Row(cancel);
        c.CancelledAt.Should().NotBeNull();
        c.Reason.Should().Be(Reason, "aynı neden");
        c.SyncedAt.Should().BeNull("olağan yerel yazım: kuyruğa girer, push'la gider");
        var p = Row(printed);
        p.PrintedAt.Should().NotBeNull();
        p.Batch.Should().Be(batch, "aynı basış kimliği");
        p.Seq.Should().Be(2, "kâğıttaki aynı sıra");
        p.SyncedAt.Should().BeNull();
        Row(cancelledElsewhere).PrintedAt.Should().BeNull("iptal edilmiş satır basılmış sayılmaz (R7-04)");
        result.Settled.DiscardLabelIds.Should().Equal([cancelledElsewhere],
            "basılmış kâğıdın etiketi başka bilgisayarda iptal edilmişti: kâğıdı imha et (§5.5)");
        result.Settled.ChangedLabelIds.Should().BeEquivalentTo([cancel, printed, cancelledElsewhere]);
        PendingCount().Should().Be(0);
        SeenMode().Should().Be(0, "yerel kip 0 ∧ kuyruk boş: tur bitti");
        new LabelRepository(_db).GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "iptal edilen ve basılan kalıntı yeniden teklif edilmez");
        new SessionRepository(_db).GetById(_session)!.Authority.Should().Be(StreamSessionAuthority.Local);
    }

    [Fact]
    public void Uzak_baski_onayi_yerel_yazima_cevrilemez_sayili_uyari_ve_izle_duser_kendi_kalintisi_cevrilir()
    {
        var own = Residual();
        var remote = RemoteLabel();
        var other = RemoteLabel();
        ModeOnReleased();
        var batch = LabelService.NewPrintBatchId();
        // Kâğıt: kendi kalıntısı ve başka bilgisayarın yayınındaki iki satır (Y11: ikisi de bekleyen komut, ayrı parça).
        EnqueuePrint(batch, (own, 1));
        var remoteCommand = EnqueuePrint(batch, (remote, 2), (other, 3));

        _round.ApplyLocal(ModeChangedAt, _replica).Busy.Should().BeFalse();
        Scalar<int>("SELECT COUNT(*) FROM Label WHERE Id IN (@remote, @other)", new { remote, other }).Should().Be(0,
            "ön koşul: 2. adım uzak satırları sildi");
        var act = () => _round.ConvertPendingCommands();
        var result = act.Should().NotThrow("uzak satıra yerel yazım denenmez, ABORT yok").Subject;

        result.Should().BeEquivalentTo(new { Converted = 1, Dropped = 1, Remaining = 0 });
        Row(own).PrintedAt.Should().NotBeNull("kendi kalıntısı aynı basışla yerelde işaretlenir");
        Row(own).Batch.Should().Be(batch);
        result.Settled.Warnings.Should().Equal(SalesCommandMessages.PrintUnrecordedModeOff(2));
        result.Settled.DiscardLabelIds.Should().BeEmpty();
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @k AND Reason LIKE @r",
                new { k = SalesConflictLog.PendingCommandRejected, r = $"%command={remoteCommand}%code=remote-mode-off%" })
            .Should().Be(1, "sessiz kayıp yok: iz");
        PendingCount().Should().Be(0);
        SeenMode().Should().Be(0, "kuyruk boşaldı: tur bitti");
        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(1, "uzak satır yerelde yeniden doğmaz");
    }

    [Fact]
    public void Ayri_kagitlarin_uzak_baski_onaylari_tek_toplam_uyari_ve_komut_basina_iz_olur()
    {
        var first = RemoteLabel();
        var second = RemoteLabel();
        var third = RemoteLabel();
        ModeOnReleased();
        // İki ayrı kâğıt: başka bilgisayarın satırları için iki ayrı bekleyen komut (1 ve 2 etiket).
        EnqueuePrint(LabelService.NewPrintBatchId(), (first, 1));
        EnqueuePrint(LabelService.NewPrintBatchId(), (second, 1), (third, 2));

        _round.ApplyLocal(ModeChangedAt, _replica).Busy.Should().BeFalse();
        var result = _round.ConvertPendingCommands();

        result.Should().BeEquivalentTo(new { Converted = 0, Dropped = 2, Remaining = 0 });
        result.Settled.Warnings.Should().Equal(new[] { SalesCommandMessages.PrintUnrecordedModeOff(3) },
            "şerit aynı metni bir kez gösterir: komut başına uyarı sayıyı eksik gösterirdi, uyarı turun sonunda toplanır");
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @k AND Reason LIKE @r",
                new { k = SalesConflictLog.PendingCommandRejected, r = "%code=remote-mode-off%" })
            .Should().Be(2, "iz komut başına");
        PendingCount().Should().Be(0);
    }

    [Fact]
    public void Acilista_kalmis_uzak_baski_onayi_Resumeda_da_sayili_uyariyla_duser()
    {
        var remote = RemoteLabel();
        ModeOnReleased();
        EnqueuePrint(LabelService.NewPrintBatchId(), (remote, 1));
        // 2. adım işlendi, 3. adımdan önce süreç kapandı.
        new SalesFeedRepository(_db).ApplyModeOff(ModeChangedAt, _replica).Busy.Should().BeFalse();

        var result = NewRound().Resume();

        result.Should().BeEquivalentTo(new { Converted = 0, Dropped = 1, Remaining = 0 });
        result.Settled.Warnings.Should().Equal(SalesCommandMessages.PrintUnrecordedModeOff(1));
        PendingCount().Should().Be(0);
        SeenMode().Should().Be(0);
    }

    [Fact]
    public void Yerel_kip_1_iken_cevirmez_komutla_gonderilir()
    {
        var label = Residual();
        ModeOnReleased();
        EnqueueCancel(label);

        _round.ConvertPendingCommands().Should().Be(SalesPendingConversion.None);
        _round.Resume().Should().Be(SalesPendingConversion.None);

        PendingCount().Should().Be(1);
        Row(label).CancelledAt.Should().BeNull();
    }

    [Fact]
    public void Okunamayan_kayit_iz_ve_uyariyla_duser_ortusu_kalkar()
    {
        var label = Residual();
        ModeOnReleased();
        var id = Guid.NewGuid().ToString("N");
        new PendingSalesCommandRepository(_db).Enqueue(id, PendingSalesCommandKinds.Cancel, "{\"payload\":{}}",
            [label], T0).Should().BeTrue();
        _round.ApplyLocal(ModeChangedAt, _replica);

        var result = _round.ConvertPendingCommands();

        result.Should().BeEquivalentTo(new { Converted = 0, Dropped = 1, Remaining = 0 });
        result.Settled.Warnings.Should().Equal(SalesCommandMessages.PendingNotConverted(PendingSalesCommandKinds.Cancel, 1));
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @k AND Reason LIKE '%unreadable%'",
            new { k = SalesConflictLog.PendingCommandRejected }).Should().Be(1);
        PendingCount().Should().Be(0);
        SeenMode().Should().Be(0);
    }

    [Fact]
    public void Yerel_yazim_duserse_satir_ve_arkasindakiler_kalir_Resume_yeniden_dener()
    {
        var cancel = Residual();
        var printed = Residual();
        ModeOnReleased();
        EnqueueCancel(cancel);
        EnqueuePrint(LabelService.NewPrintBatchId(), (printed, 1));
        _round.ApplyLocal(ModeChangedAt, _replica);
        Exec("CREATE TRIGGER TestFailCancel BEFORE UPDATE OF CancelledAt ON Label BEGIN SELECT RAISE(ABORT, 'test'); END");

        var failed = _round.ConvertPendingCommands();

        failed.Should().BeEquivalentTo(new { Converted = 0, Dropped = 0, Remaining = 2 }, "sıra korunur: ilk yazım düştü");
        PendingCount().Should().Be(2);
        SeenMode().Should().Be(1, "tur bitmedi");
        Row(printed).PrintedAt.Should().BeNull();

        Exec("DROP TRIGGER TestFailCancel");
        var resumed = _round.Resume();

        resumed.Should().BeEquivalentTo(new { Converted = 2, Remaining = 0 });
        Row(cancel).CancelledAt.Should().NotBeNull();
        Row(printed).PrintedAt.Should().NotBeNull();
        SeenMode().Should().Be(0);
        _round.ConvertPendingCommands().Should().Be(SalesPendingConversion.None, "ikinci çeviri yok");
    }

    // ── kargo ayarı ───────────────────────────────────────────────────────

    [Fact]
    public void Ikinci_adimdan_sonra_lisans_kargo_ayari_diske_ve_canli_ornege_yazilir()
    {
        new SalesSyncStateRepository(_db).SaveServerState(3, 750m, 60m, scrubOrderText: false);
        ModeOnReleased();

        _round.ApplyLocal(ModeChangedAt, _replica);

        _live.Shipping.FreeShippingThreshold.Should().Be(750m);
        _live.Shipping.ShippingFee.Should().Be(60m);
        var disk = new SettingsStore(_settingsPath).Load().Shipping;
        disk.FreeShippingThreshold.Should().Be(750m);
        disk.ShippingFee.Should().Be(60m);
    }

    [Fact]
    public void Ikinci_adim_islenip_kopya_yazilmadan_surec_kapanirsa_acilista_kargo_ayari_bir_kez_yazilir()
    {
        new SalesSyncStateRepository(_db).SaveServerState(3, 750m, 60m, scrubOrderText: false);
        ModeOnReleased();
        // 2. adımın işlemi işlendi (yerel kip 0, ModeSeenAt yazıldı: tur bir daha koşmaz), kopyadan önce süreç kapandı.
        new SalesFeedRepository(_db).ApplyModeOff(ModeChangedAt, _replica).Busy.Should().BeFalse();
        _live.Shipping.FreeShippingThreshold.Should().Be(500m, "ön koşul: kopya yazılmadı");

        NewRound().Resume();                                              // yeniden açılış (AppHost) ya da tur başı

        _live.Shipping.FreeShippingThreshold.Should().Be(750m, "işaret kalıcı: kopya açılışta tamamlanır");
        _live.Shipping.ShippingFee.Should().Be(60m);
        new SettingsStore(_settingsPath).Load().Shipping.ShippingFee.Should().Be(60m);

        // Off'ta operatör yerel ayarı sonradan değiştirir: kopya bir kez yapıldı, yeniden ezilmez.
        _store.Update(s => s.Shipping = new ShippingSettings { FreeShippingThreshold = 900m, ShippingFee = 40m });
        _live.Shipping = new ShippingSettings { FreeShippingThreshold = 900m, ShippingFee = 40m };
        NewRound().Resume();
        _live.Shipping.FreeShippingThreshold.Should().Be(900m);
        new SettingsStore(_settingsPath).Load().Shipping.ShippingFee.Should().Be(40m);
    }

    [Fact]
    public void Lisans_ayarinda_bos_esik_ve_ucret_kargoyu_kapatir()
    {
        new SalesSyncStateRepository(_db).SaveServerState(4, null, null, scrubOrderText: false);

        _round.ApplyLocal(ModeChangedAt, _replica);

        _live.Shipping.IsEnabled.Should().BeFalse();
        _live.Shipping.FreeShippingThreshold.Should().BeNull();
        new SettingsStore(_settingsPath).Load().Shipping.ShippingFee.Should().BeNull();
    }

    [Fact]
    public void Lisans_ayari_hic_yazilmamissa_yerel_ayar_korunur()
    {
        new SalesSyncStateRepository(_db).SaveServerState(null, null, null, scrubOrderText: false);

        _round.ApplyLocal(ModeChangedAt, _replica);

        _live.Shipping.FreeShippingThreshold.Should().Be(500m);
        new SettingsStore(_settingsPath).Load().Shipping.ShippingFee.Should().Be(50m);
    }
}
