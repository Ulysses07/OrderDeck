using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Satış senkronunun kopya turu (spec 2026-10-13 §4.5, §8.2-1, §8.7): hello'nun gövdesi ve sıklığı, her turda
/// kalp atışı olan state, sunucu durumunun saklanması, mod 1'e karşı yerel kipin 0 kalması, hatalarda sessiz
/// yeniden deneme, geri yüklemeden sonraki önceki kimlik ve gönderim kapısı, eksik/bozuk kimlik, canlı yolun ağı
/// beklememesi ve günlükte kişisel veri olmaması. Sunucu <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SalesSyncStateServiceTests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Hardware(string id) : IHardwareIdProvider
    {
        public string GetHardwareId() => id;
        public string? GetLegacyHardwareId() => null;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Bir bilgisayarın kopya yığını: yerel veritabanı, zarf ve tur servisi.</summary>
    private sealed class Pc
    {
        public IDbConnectionFactory Db { get; }
        public License License { get; }
        public AppSettings Settings { get; } = new();
        public string Fingerprint { get; }
        public ReplicaIdentityRepository Identity { get; }
        public SalesSyncStateRepository State { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesSyncStateService Service { get; }
        public CapturingLogger<SalesSyncStateService> Log { get; } = new();
        public CapturingLogger<SalesSyncEnvelope> EnvelopeLog { get; } = new();

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string? fingerprint = null)
        {
            Db = db;
            new MigrationRunner(db).Run();
            License = new License(server.Lisans);
            Fingerprint = fingerprint ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            Identity = new ReplicaIdentityRepository(db);
            State = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(Identity, State, new Hardware(Fingerprint), License, EnvelopeLog);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            Service = new SalesSyncStateService(api, Envelope, Identity, State, License, () => Settings, Log);
        }

        public Guid ReplicaId => Identity.GetReplicaId()!.Value;

        public Task<SalesStateTourOutcome> TourAsync() => Service.TourOnceAsync(CancellationToken.None);

        public void Exec(string sql, object? args = null)
        {
            using var c = Db.Open();
            c.Execute(sql, args);
        }
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _memory = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orderdeck-sales-state-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* en iyi çaba */ }
    }

    private Pc NewPc(string? fingerprint = null) => new(_server, _memory, fingerprint);

    // ── hello ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hello_kopyayi_derlemeyi_ve_yerel_kargo_ayarini_tasir_onceki_kimlik_yoksa_alan_yok()
    {
        var pc = NewPc();
        pc.Settings.Shipping.FreeShippingThreshold = 1500m;
        pc.Settings.Shipping.ShippingFee = 89.90m;

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);

        var hello = _server.Hellos.Should().ContainSingle().Subject;
        hello.LicenseId.Should().Be(_server.LicenseId);
        var json = hello.Json;
        json.GetProperty("replicaId").GetGuid().Should().Be(pc.ReplicaId);
        json.GetProperty("protocol").GetInt32().Should().Be(2);
        json.GetProperty("featureLevel").GetInt32().Should().Be(1, "W1 derlemesi; 3 yalnız W3 ile (§8.1-4)");
        json.GetProperty("seenMode").GetInt32().Should().Be(0);
        json.GetProperty("machineName").GetString().Should().Be(
            Environment.MachineName.Length > 128 ? Environment.MachineName[..128] : Environment.MachineName);
        json.GetProperty("hardwareFingerprint").GetString().Should().Be(pc.Fingerprint);
        var shipping = json.GetProperty("reportedShipping");
        shipping.GetProperty("threshold").GetDecimal().Should().Be(1500m);
        shipping.GetProperty("fee").GetDecimal().Should().Be(89.90m);
        json.TryGetProperty("previousReplicaId", out _).Should().BeFalse("bekleyen önceki kimlik yokken alan yazılmaz");
        json.TryGetProperty("backfillCompleted", out _).Should().BeFalse();
        json.TryGetProperty("paymentHistoryCompleted", out _).Should().BeFalse();

        var row = _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!;
        row.FeatureLevel.Should().Be(1);
        row.ReportedThreshold.Should().Be(1500m);
        row.ReportedFee.Should().Be(89.90m);
        row.PreviousReplicaId.Should().BeNull();
        _server.Faults.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1500", null)]
    [InlineData(null, "89.90")]
    public async Task Kargo_ozelligi_kapaliyken_bildirilen_alanlar_bos(string? threshold, string? fee)
    {
        var pc = NewPc();
        pc.Settings.Shipping.FreeShippingThreshold = threshold is null ? null : decimal.Parse(threshold, CultureInfo.InvariantCulture);
        pc.Settings.Shipping.ShippingFee = fee is null ? null : decimal.Parse(fee, CultureInfo.InvariantCulture);

        await pc.TourAsync();

        var shipping = _server.Hellos.Single().Json.GetProperty("reportedShipping");
        shipping.GetProperty("threshold").ValueKind.Should().Be(JsonValueKind.Null);
        shipping.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
        var row = _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!;
        row.ReportsShipping.Should().BeTrue("kapalı özellik de bir bildirimdir (§6.2 yan yana gösterim)");
        row.ReportedThreshold.Should().BeNull();
        row.ReportedFee.Should().BeNull();
    }

    [Fact]
    public async Task Uzun_parmak_izi_128_karaktere_kisalir()
    {
        var pc = NewPc(fingerprint: string.Concat(Enumerable.Repeat(Guid.NewGuid().ToString("N"), 7)));

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);

        var json = _server.Hellos.Single().Json;
        json.GetProperty("hardwareFingerprint").GetString().Should().Be(pc.Fingerprint[..128]);
        json.GetProperty("machineName").GetString()!.Length.Should().BeLessThanOrEqualTo(128);
        _server.Faults.Should().BeEmpty("sunucu 128'i aşan alanı 400 ile reddeder");
    }

    [Fact]
    public async Task Hello_surecte_lisans_basina_bir_kez_lisans_degisince_yeniden()
    {
        var pc = NewPc();

        for (var i = 0; i < 3; i++) (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().ContainSingle();
        _server.States.Should().HaveCount(3);

        pc.License.CurrentLicenseKey = _server.SecondLisans;
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(2);
        _server.Hellos[1].LicenseId.Should().Be(_server.SecondLicenseId);
        _server.States[^1].LicenseId.Should().Be(_server.SecondLicenseId);
        _server.ReplicaRow(_server.SecondLicenseId, pc.ReplicaId).Should().NotBeNull("kopya satırı lisans başına (§3.1)");

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(2);

        pc.License.CurrentLicenseKey = _server.Lisans;
        await pc.TourAsync();
        _server.Hellos.Should().HaveCount(3);
        _server.Hellos[2].LicenseId.Should().Be(_server.LicenseId);
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Yerel_kargo_ayari_degisince_sonraki_tur_hello_yu_yeni_degerle_yeniden_gonderir()
    {
        // §6.2 "Geçiş", §8.1-6: sales-sync status her PC'nin yerel değerini yan yana gösterir; süreç içinde değişen
        // ayar yeniden açılışa kadar sunucuda eski kalmamalı.
        var pc = NewPc();
        pc.Settings.Shipping.FreeShippingThreshold = 500m;
        pc.Settings.Shipping.ShippingFee = 60m;

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().ContainSingle("değişmeyen ayar ek hello göndermez");

        pc.Settings.Shipping.ShippingFee = 75m;
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);

        _server.Hellos.Should().HaveCount(2);
        var shipping = _server.Hellos[1].Json.GetProperty("reportedShipping");
        shipping.GetProperty("threshold").GetDecimal().Should().Be(500m);
        shipping.GetProperty("fee").GetDecimal().Should().Be(75m);
        var row = _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!;
        row.ReportedThreshold.Should().Be(500m);
        row.ReportedFee.Should().Be(75m);

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(2, "yeni değer bildirildi; değişmedikçe hello tekrarlanmaz");

        // Ölçüt son BAŞARILI hello'dur: düşen bildirim sonraki turda yeniden denenir.
        pc.Settings.Shipping.FreeShippingThreshold = 750m;
        _server.EnqueueStatus(FakeSalesServer.Hello, 500);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Failed);
        _server.Hellos.Should().HaveCount(3);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(4);
        _server.Hellos[3].Json.GetProperty("reportedShipping").GetProperty("threshold").GetDecimal().Should().Be(750m);
        _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!.ReportedThreshold.Should().Be(750m);

        // Özelliğin kapatılması da bir değişikliktir: iki alan boş bildirilir.
        pc.Settings.Shipping.ShippingFee = null;
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(5);
        var off = _server.Hellos[4].Json.GetProperty("reportedShipping");
        off.GetProperty("threshold").ValueKind.Should().Be(JsonValueKind.Null);
        off.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(5);
        _server.Faults.Should().BeEmpty();
    }

    // ── state ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task State_her_turda_kimlik_ve_gorulen_kiple_kargo_ve_kvkk_ayari_saklanir()
    {
        var pc = NewPc();
        _server.Shipping = new FakeSalesServer.ShippingBody(4, 2000m, 99.5m);
        _server.ScrubOrderTextOnPurge = true;
        _server.ForceEndSilenceMinutes = 20;
        _server.MinClientVersion = "0.9.11";
        _server.LiveSessions.Add(new FakeSalesServer.LiveSessionBody(
            Guid.NewGuid(), null, DateTimeOffset.UtcNow.AddHours(-1), null, null, DateTimeOffset.UtcNow, "live", null));

        await pc.TourAsync();
        await pc.TourAsync();

        _server.States.Should().HaveCount(2).And.OnlyContain(s =>
            Guid.Parse(s.ReplicaId!) == pc.ReplicaId && s.SeenMode == "0" && s.LicenseId == _server.LicenseId);
        var state = pc.State.Get();
        state.ShippingVersion.Should().Be(4);
        state.FreeShippingThreshold.Should().Be(2000m);
        state.ShippingFee.Should().Be(99.5m);
        state.ScrubOrderText.Should().BeTrue();
        state.Mode.Should().Be(0);

        var snapshot = pc.Service.LastState!;
        snapshot.LicenseKey.Should().Be(_server.Lisans);
        snapshot.Mode.Should().Be(0);
        snapshot.MinClientVersion.Should().Be("0.9.11");
        snapshot.ForceEndSilenceMinutes.Should().Be(20);
        snapshot.LiveSessions.Should().ContainSingle().Which.State.Should().Be("live");

        var row = _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!;
        row.Requests.Should().Be(3, "hello + iki kalp atışı");
        row.SeenMode.Should().Be(0);

        // Lisans geneli ayar kaldırılırsa (satır yok) önbellek de boşalır.
        _server.Shipping = null;
        _server.ScrubOrderTextOnPurge = false;
        await pc.TourAsync();
        state = pc.State.Get();
        state.ShippingVersion.Should().BeNull();
        state.FreeShippingThreshold.Should().BeNull();
        state.ShippingFee.Should().BeNull();
        state.ScrubOrderText.Should().BeFalse();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Sunucu_mod_1_bildirse_de_yerel_kip_0_kalir_ve_tek_uyari_duser()
    {
        var pc = NewPc();
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++) await pc.TourAsync();

        pc.State.GetMode().Should().Be(0, "kip geçişi W2'nin (§8.4); W1 Mode'u hiç yazmaz");
        pc.State.Get().ModeSeenAt.Should().BeNull();
        pc.State.Get().FirstCatchUpAt.Should().BeNull();
        pc.Log.Warnings.Where(w => w.Message.Contains("mod 1", StringComparison.Ordinal)).Should().ContainSingle();
        _server.States.Should().OnlyContain(s => s.SeenMode == "0");
        _server.Hellos.Single().Json.GetProperty("seenMode").GetInt32().Should().Be(0);
        pc.Service.LastState!.Mode.Should().Be(1, "anlık görüntü sunucunun söylediğini saklar (W2'nin girdisi)");
    }

    [Fact]
    public async Task Bekleyen_satis_komutu_varken_istekler_gorulen_kip_1_tasir()
    {
        var pc = NewPc();
        pc.Exec("INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt) VALUES ('k1', 'mark-printed', '{}', 1)");

        await pc.TourAsync();

        pc.Envelope.SeenMode.Should().Be(1);
        _server.Hellos.Single().Json.GetProperty("seenMode").GetInt32().Should().Be(1);
        _server.States.Single().SeenMode.Should().Be("1");
    }

    // ── hatalar ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Hata_ve_zaman_asimi_firlatmaz_sonraki_turda_yeniden_denenir()
    {
        var pc = NewPc();
        _server.EnqueueStatus(FakeSalesServer.Hello, 500);
        _server.EnqueueTimeout(FakeSalesServer.State);

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Failed);
        _server.Hellos.Should().HaveCount(1);
        _server.States.Should().HaveCount(1, "hello düşse de kalp atışı gider (Y7: susmuş sayılmamalı)");

        _server.EnqueueTimeout(FakeSalesServer.Hello);
        _server.EnqueueStatus(FakeSalesServer.State, 500);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Failed);
        _server.Hellos.Should().HaveCount(2, "başarısız hello sonraki turda yeniden denenir");

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(3);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(3, "başarılı hello bu süreçte tekrarlanmaz");
        _server.States.Should().HaveCount(4);

        pc.Log.Warnings.Where(w => w.Message.Contains("sales/hello gönderilemedi", StringComparison.Ordinal))
            .Should().ContainSingle("art arda hatalar seri başına bir uyarı yazar, arası Debug");
        _server.Faults.Should().BeEmpty();
    }

    // ── geri yüklemeden sonra önceki kimlik (§8.7) ────────────────────────

    [Fact]
    public async Task Onceki_kimlik_hello_ile_gider_204_siler_500_tutar_o_ana_dek_gonderim_kapali()
    {
        var pc = NewPc();
        var previous = Guid.NewGuid();
        pc.Identity.SetPendingPreviousReplicaId(previous);
        pc.Envelope.IsReadyForPush.Should().BeFalse("geri yüklenmiş kopya hello'dan önce gönderim yapmaz");

        _server.EnqueueStatus(FakeSalesServer.Hello, 500);
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Failed);
        _server.Hellos.Single().Json.GetProperty("previousReplicaId").GetGuid().Should().Be(previous);
        pc.Identity.GetPendingPreviousReplicaId().Should().Be(previous, "500: önceki kimlik bekler");
        pc.Envelope.IsReadyForPush.Should().BeFalse();

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Should().HaveCount(2);
        _server.Hellos[1].Json.GetProperty("previousReplicaId").GetGuid().Should().Be(previous);
        pc.Identity.GetPendingPreviousReplicaId().Should().BeNull("2xx önceki kimliği siler");
        pc.Envelope.IsReadyForPush.Should().BeTrue();
        _server.ReplicaRow(_server.LicenseId, pc.ReplicaId)!.PreviousReplicaId.Should().Be(previous);

        await pc.TourAsync();
        _server.Hellos.Should().HaveCount(2);
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Hello_basarili_ama_silinemeyen_onceki_kimlik_bu_lisans_ve_kopya_icin_gonderimi_acar()
    {
        var pc = NewPc();
        var previous = Guid.NewGuid();
        pc.Identity.SetPendingPreviousReplicaId(previous);
        // Sunucu 2xx döndü ama değer silinmeden yeniden yazıldı (silme başarısız gibi): bekleyen kalır.
        await pc.TourAsync();
        pc.Identity.SetPendingPreviousReplicaId(previous);

        pc.Envelope.IsReadyForPush.Should().BeTrue("bu (lisans, kopya) için hello başarıyla gitti");
        pc.License.CurrentLicenseKey = _server.SecondLisans;
        pc.Envelope.IsReadyForPush.Should().BeFalse("başka lisansta hello henüz gitmedi");
        pc.License.CurrentLicenseKey = null;
        pc.Envelope.IsReadyForPush.Should().BeFalse();
    }

    [Fact]
    public async Task Geri_yukleme_kimligi_cozulemeyen_surecte_hicbir_v2_istegi_gitmez_gonderim_v1e_de_dusmez()
    {
        // Açılışta geri yükleme işareti okunamadı ya da uygulanamadı (AppHost): veritabanı geri yüklenmiş ama sunucuya
        // öyle bildirilemiyor. Bu süreçte hello gitseydi sunucu kopyayı özgün sanırdı (§8.7, M20); sonraki açılış yeniden
        // dener.
        var pc = NewPc();
        pc.Envelope.HoldUntilRestart();
        pc.Envelope.HoldUntilRestart();

        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.RestoreUnresolved);
        (await pc.Service.ReportBackfillCompletedAsync(pc.License.CurrentLicenseKey!, _server.LicenseId, pc.ReplicaId,
            CancellationToken.None)).Should().BeFalse();
        pc.Envelope.IsReadyForPush.Should().BeFalse();
        pc.Envelope.ForPush().Should().BeNull("tur atlanır");
        pc.Exec("UPDATE _meta SET ReplicaId = NULL WHERE Id = 1");
        pc.Envelope.ForPush().Should().BeNull("kimlik bozuk olsa da zarfsız (v1) gövdeye düşmez");

        _server.SalesRequestCount.Should().Be(0);
        pc.EnvelopeLog.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains("önceki kopya kimliği"))
            .Should().ContainSingle("süreç başına bir kez");
    }

    // ── kimlik eksik/bozuk ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("bozuk-kimlik")]
    public async Task Eksik_ya_da_bozuk_kimlik_v2_istegi_gondermez_bir_kez_uyarir(string? raw)
    {
        var pc = NewPc();
        pc.Exec("UPDATE _meta SET ReplicaId = @raw WHERE Id = 1", new { raw });

        for (var i = 0; i < 3; i++)
            (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.NoReplica);
        pc.Envelope.IsReadyForPush.Should().BeFalse();

        _server.SalesRequestCount.Should().Be(0);
        pc.EnvelopeLog.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("_meta.ReplicaId");

        // Değer düzelince tur kendiliğinden işler.
        var fixedId = Guid.NewGuid();
        pc.Exec("UPDATE _meta SET ReplicaId = @id WHERE Id = 1", new { id = fixedId.ToString("N") });
        (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);
        _server.Hellos.Single().Json.GetProperty("replicaId").GetGuid().Should().Be(fixedId);
    }

    // ── canlı yol ağı beklemez ────────────────────────────────────────────

    [Theory]
    [InlineData(FakeSalesServer.Hello)]
    [InlineData(FakeSalesServer.State)]
    public async Task Yanit_vermeyen_sunucu_canli_yolu_bekletmez(string hangingEndpoint)
    {
        // Üretimdeki kilit davranışı için dosya veritabanı (gerçek fabrika, WAL, 10 sn yazma bekleme bütçesi).
        Directory.CreateDirectory(_dir);
        var db = new SqliteConnectionFactory(Path.Combine(_dir, "orderdeck.db"));
        var pc = new Pc(_server, db);
        if (hangingEndpoint == FakeSalesServer.State)
            (await pc.TourAsync()).Should().Be(SalesStateTourOutcome.Ok);   // hello geçsin, sonraki tur state'te asılı

        var clock = new Clock();
        var sessions = new SessionRepository(db);
        var labelRepo = new LabelRepository(db);
        var labels = new LabelService(labelRepo,
            new CustomerService(new CustomerRepository(db), sessions, labelRepo, clock), db, clock);

        _server.HangEndpoint = hangingEndpoint;
        using var cts = new CancellationTokenSource();
        var tour = Task.Run(() => pc.Service.TourOnceAsync(cts.Token));
        try
        {
            await _server.RequestHanging.WaitAsync(Patience);

            var sessionId = Guid.NewGuid().ToString("N");
            var watch = Stopwatch.StartNew();
            sessions.Insert(new StreamSession(sessionId, null, clock.UnixNow(), null, new[] { "tiktok" }, null));
            var label = labels.Add(sessionId,
                new ChatMessage(Guid.NewGuid().ToString("N"), "tiktok", null, "u" + Guid.NewGuid().ToString("N")[..8],
                    null, null, "A1", clock.UnixNow(), Array.Empty<string>()),
                10m, "A1");
            labels.MarkPrintedAndRecord(new[] { label.Id });
            sessions.End(sessionId, clock.UnixNow());
            watch.Stop();

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "tur ağı beklerken SQLite kilidi tutmaz; canlı yol yazma bütçesini (10 sn) beklemez");
            tour.IsCompleted.Should().BeFalse("sunucu hâlâ yanıt vermiyor");
            labelRepo.GetById(label.Id)!.PrintedAt.Should().NotBeNull();
            sessions.GetById(sessionId)!.EndedAt.Should().NotBeNull();
        }
        finally
        {
            cts.Cancel();                                                 // asılı tur testten sonra kalmasın
        }
        await FluentActions.Awaiting(() => tour).Should().ThrowAsync<OperationCanceledException>();
    }

    // ── günlük ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gunlukte_musteri_verisi_lisans_anahtari_ve_parmak_izi_yok()
    {
        var pc = NewPc();
        var clock = new Clock();
        var sessions = new SessionRepository(_memory);
        sessions.Insert(new StreamSession("s1", null, clock.UnixNow(), null, new[] { "tiktok" }, null));
        var labelRepo = new LabelRepository(_memory);
        var customers = new CustomerRepository(_memory);
        var labels = new LabelService(labelRepo, new CustomerService(customers, sessions, labelRepo, clock), _memory, clock);
        var username = "alici" + Guid.NewGuid().ToString("N")[..10];
        var displayName = "Görünen " + Guid.NewGuid().ToString("N")[..10];
        var message = "yorum " + Guid.NewGuid().ToString("N");
        var phone = TestPhone.NewE164();
        var label = labels.Add("s1",
            new ChatMessage(Guid.NewGuid().ToString("N"), "tiktok", null, username, displayName, null, message,
                clock.UnixNow(), Array.Empty<string>()),
            10m, "A1");
        pc.Exec("UPDATE Customer SET Phone = @phone WHERE Id = @id", new { phone, id = label.CustomerId });

        // Başarılı, başarısız, zaman aşımı, mod 1 ve geri yükleme yolları.
        pc.Identity.SetPendingPreviousReplicaId(Guid.NewGuid());
        _server.Mode = 1;
        _server.EnqueueStatus(FakeSalesServer.Hello, 500);
        _server.EnqueueTimeout(FakeSalesServer.State);
        for (var i = 0; i < 3; i++) await pc.TourAsync();
        pc.Exec("UPDATE _meta SET ReplicaId = 'bozuk' WHERE Id = 1");
        await pc.TourAsync();

        var entries = pc.Log.Entries.Concat(pc.EnvelopeLog.Entries.Select(e =>
            new CapturingLogger<SalesSyncStateService>.Entry(e.Level, e.Message, e.Exception))).ToList();
        entries.Should().NotBeEmpty();
        foreach (var secret in new[] { username, displayName, message, phone, _server.Lisans, pc.Fingerprint })
            entries.Should().NotContain(e => e.Full.Contains(secret, StringComparison.OrdinalIgnoreCase),
                "günlük yalnız kimlik ve sayı taşır");
        entries.Should().NotContain(e => e.Message.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "makine adı günlüğe yazılmaz (sunucudaki kuralla aynı)");
    }

    // Arka plan işi (açılışta hemen tur, sonra 30 sn): W2-03'ten beri kalp atışını satış akışı turu sürer —
    // SalesChangesPullServiceTests.Arka_plan_isi_acilista_hemen_tur_atar_sonra_ritimle_surer_tek_state_surucusu.

    /// <summary>Üst sınır, bekleme süresi değil: koşul sağlanınca hemen döner.</summary>
    private static readonly TimeSpan Patience = TestPatience.Upper;
}
