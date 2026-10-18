using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Mod Off'ta her teslimden sonra kirasız ödeme kaydı (spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)", M17;
// W3-06 b) ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Mod Off (bugün her lisans) mesaj akışı ve soruları bugünküyle birebir; yalnız gönderim çıkışlarında kuyruğa bir kayıt
// yazılır: Sent/Opened/SendPending → teslim (SendPending yerelde teslim yazar), Cloud API'den sonra açılamayan wa.me →
// belirsiz, düz LaunchFailed → yok. İşli kişide kayıt yalnız işin satırından türer: ödeme geçmişiyle aynı Id ve içerik.
// Kira ucu (payment-requests/begin) hiç çağrılmaz; ağa yalnız bugünkü bakiye ve gönderim istekleri çıkar.
public partial class PaymentRequestServiceTests
{
    private sealed class OffClock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private sealed class NoLicense : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => null;
    }

    private sealed record OffBed(
        PaymentRequestService Sut, WhatsAppStubHandler Handler, PendingPaymentRecordRepository Pending,
        PaymentOffRecordWriter Writer);

    /// <summary>Gerçek SQLite (FileDb) üstünde, kayıt yazıcısı bağlı servis. Gönderici hiç gönderim yapmaz: testler
    /// kuyruğa bakar.</summary>
    private OffBed OffSut(SyncedDb db, ICurrentLicenseProvider? license = null)
    {
        license ??= new FixedLicenseProvider();
        var handler = new WhatsAppStubHandler();
        var pending = new PendingPaymentRecordRepository(db.Factory);
        var clock = new OffClock();
        var senderApi = new LicenseApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://stub") },
            new LicenseTokenStore());
        var sender = new PaymentRecordSender(senderApi, pending, TestSalesEnvelope.For(db.Factory, license), license,
            clock, NullLogger<PaymentRecordSender>.Instance);
        var writer = new PaymentOffRecordWriter(sender, new SalesSyncStateRepository(db.Factory),
            new LabelRepository(db.Factory), license, clock, NullLogger<PaymentOffRecordWriter>.Instance);
        var sut = SutOn(handler, _store, _launcher, license, db.Jobs, db.Customers, db.Busy, offRecords: writer);
        return new OffBed(sut, handler, pending, writer);
    }

    private static string Oturum(SyncedDb db)
    {
        var id = Guid.NewGuid().ToString("N");
        new SessionRepository(db.Factory).Insert(new StreamSession(id, "Örnek yayın", 1_000, 5_000,
            new[] { "tiktok" }, null));
        return id;
    }

    private static string Etiket(SyncedDb db, string sessionId, string customerId, decimal price, long? printedAt = 2_000)
    {
        var id = Guid.NewGuid().ToString("N");
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn,
            @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price,
                                 AddedAt, PrintedAt)
              VALUES (@id, @sessionId, @customerId, 'twitch', 'alice', 'Alice', 'alıyorum', 'A1', @price, 1500, @printedAt)",
            new { id, sessionId, customerId, price, printedAt });
        return id;
    }

    private static IReadOnlyList<PaymentRequestBackfillItem> Kayitlar(OffBed bed) =>
        bed.Pending.List().Select(row =>
        {
            row.Kind.Should().Be(PendingPaymentRecordKinds.Record);
            return PendingPaymentRecordCodec.DecodeRecord(row);
        }).ToList();

    private static void KiraUcuYok(OffBed bed) =>
        bed.Handler.RequestPaths.Should().NotContain(p => p.Contains("payment-requests"),
            "mod Off'ta begin/finish yok; kayıt yalnız kuyruğa yazılır");

    private static void ModOn(SyncedDb db)
    {
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn, "UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
    }

    [Theory]
    [InlineData(false, PaymentRequestResult.Opened)]
    [InlineData(true, PaymentRequestResult.Sent)]
    public async Task Mod_Offta_teslimden_sonra_bir_delivered_kayit_kuyrukta_Id_istekten_turer(
        bool cloud, PaymentRequestResult expected)
    {
        var db = FileDb();
        try
        {
            if (cloud) EnableCloudApi();
            var owner = Uye(db, "ornek_sahip");
            var session = Oturum(db);
            var printed = Etiket(db, session, owner.Id, 250m);
            Etiket(db, session, owner.Id, 99m, printedAt: null);                     // basılmamış: katkı yok
            var bed = OffSut(db);
            bed.Handler.PreviewBalance = 100m;
            var scope = $"session:{session}";

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, scope)).Should().Be(expected);

            var job = Isler(db, scope).Should().ContainSingle().Subject;
            var record = Kayitlar(bed).Should().ContainSingle().Subject;
            record.RecordId.Should().Be(PaymentRecordIds.Delivered(job.Id, 1),
                "işli kişide ödeme geçmişiyle aynı türetme: iş ve teslim sayısı");
            record.State.Should().Be(PaymentRequestStates.Delivered);
            record.OwnerId.Should().Be(owner.Id);
            record.MemberIds.Should().Equal(owner.Id);
            record.ScopeKey.Should().Be(scope);
            record.OrderIds.Should().Equal(Guid.ParseExact(printed, "N"));
            record.NetAmount.Should().Be(150m, "mesajdaki net: 250 − 100 bakiye");
            record.AppliedAmount.Should().Be(100m);
            record.FullyFromBalance.Should().BeFalse();
            KiraUcuYok(bed);
            bed.Handler.AppliedBalanceBodies.Should().ContainSingle("bakiye akışı bugünkü gibi");
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Isli_kiside_kaydin_siparisleri_odeme_gecmisiyle_ayni_Covers_kuraliyla_istekten_sonra_basilan_girmez()
    {
        // Aynı Id ödeme geçmişinde de üretilir (PaymentHistoryRecords.FromJob): iki yazıcı aynı kümeyi taşımalı, yoksa
        // sunucu content-conflict verir; isteğin anından sonra basılan etiket "istendi" sayılmaz.
        var db = FileDb();
        try
        {
            var owner = Uye(db, "ornek_sahip");
            var session = Oturum(db);
            var before = Etiket(db, session, owner.Id, 250m);
            Etiket(db, session, owner.Id, 80m, printedAt: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
            var bed = OffSut(db);
            var scope = $"session:{session}";

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, scope)).Should().Be(PaymentRequestResult.Opened);

            var job = Isler(db, scope).Should().ContainSingle().Subject;
            Kayitlar(bed).Should().ContainSingle().Which.OrderIds.Should().Equal(new[] { Guid.ParseExact(before, "N") },
                "Covers: PrintedAt ≤ PromptAt (job={0})", job.Id);
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Mod_Offta_tamami_bakiyeden_odenen_istek_fullyFromBalance()
    {
        var db = FileDb();
        try
        {
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);
            bed.Handler.PreviewBalance = 300m;
            bed.Handler.CapAppliedToRequest = true;

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.Opened);

            var record = Kayitlar(bed).Should().ContainSingle().Subject;
            record.NetAmount.Should().Be(0m);
            record.AppliedAmount.Should().Be(250m);
            record.FullyFromBalance.Should().BeTrue();
            record.OrderIds.Should().BeEmpty("kişinin basılmış etiketi yok");
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Mod_Offta_SendPending_kaydi_isin_teslimiyle_delivered_odeme_gecmisiyle_ayni_Id_ve_icerik()
    {
        // W3 son incelemesi: in_progress'te (SendPending) iş teslimini yazar (MarkDelivered: teslim = deneme = 1; #506).
        // Mod Off kaydı onu Unconfirmed(J,1,1) Id'li "unconfirmed" kayıt diye yazıyor, ödeme geçmişi aynı işi sonradan
        // okuyunca (HasUnconfirmedSend = false) Delivered(J,1) Id'li "delivered" kayıt kuruyordu: Id'ler farklı, sunucu
        // ikisini de applied alır, tek mesaja kalıcı iki öncül olurdu. Kayıt yalnız işin satırından türer.
        var db = FileDb();
        try
        {
            EnableCloudApi();
            var owner = Uye(db, "ornek_sahip");
            var session = Oturum(db);
            var printed = Etiket(db, session, owner.Id, 250m);
            var bed = OffSut(db);
            bed.Handler.SendResponseJson =
                """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""";
            var scope = $"session:{session}";

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, scope)).Should().Be(PaymentRequestResult.SendPending);

            var job = Isler(db, scope).Should().ContainSingle().Subject;
            job.Deliveries.Should().Be(1, "ön koşul: SendPending yerelde teslim yazar (#506)");
            job.HasUnconfirmedSend.Should().BeFalse();
            var record = Kayitlar(bed).Should().ContainSingle().Subject;
            record.RecordId.Should().Be(PaymentRecordIds.Delivered(job.Id, 1));
            record.State.Should().Be(PaymentRequestStates.Delivered, "işin satırı teslim diyor; kayıt onun aynası");
            record.Should().BeEquivalentTo(
                PaymentHistoryRecords.FromJob(job, [owner.Id], [Guid.ParseExact(printed, "N")]),
                o => o.ComparingRecordsByMembers(),
                "ödeme geçmişi aynı işten aynı kaydı kurar: sunucu yinelemeyi unchanged sayar");
            record.NetAmount.Should().Be(250m);
            _launcher.LaunchedUrls.Should().BeEmpty("bugünkü davranış: wa.me açılmaz");
            KiraUcuYok(bed);
        }
        finally { Drop(db); }
    }

    /// <summary>
    /// İşli kişide kaydı gönderim çıkışının bayrağı değil işin satırı belirler: ödeme geçmişi işi bayraksız okur, iki yazıcı
    /// her durumda aynı kaydı (Id ve içerik) kurmalı (<see cref="PaymentHistoryRecords.FromJob"/>).
    /// </summary>
    [Theory]
    [InlineData(1, 1, true)]     // SendPending: sonuç bilinmiyor, ama iş teslimini yazdı
    [InlineData(1, 1, false)]    // teslim
    [InlineData(0, 1, true)]     // gerçek belirsiz deneme (Cloud API'den sonra açılamayan wa.me)
    [InlineData(1, 2, false)]    // teslim yazılamadı (hata yutuldu): iş sonucu bilinmeyen gönderim diyor
    [InlineData(2, 2, false)]    // ardışık ikinci teslim
    [InlineData(2, 3, true)]     // iki teslimden sonra belirsiz deneme
    public void Isli_kiside_mod_Off_kaydi_bayraktan_bagimsiz_isin_satirindan_odeme_gecmisiyle_ayni(
        int deliveries, int attempts, bool unconfirmed)
    {
        var db = FileDb();
        try
        {
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);
            var job = new PaymentJob(Guid.NewGuid().ToString("N"), owner.Id, "cumulative", 250m, 0, null, null,
                PaymentJobState.NoBalance, 1_000, 1_100, null,
                DeliveredAt: deliveries > 0 ? 1_000 : null, DeliveredNet: deliveries > 0 ? 250m : null,
                Deliveries: deliveries, SendAttempts: attempts, AttemptNet: 240m, AttemptAt: 1_100);

            bed.Writer.Record(db.Customers.GetPaymentPerson(owner.Id), job, "cumulative", unconfirmed, 250m)
                .Should().NotBeNull();

            var record = Kayitlar(bed).Should().ContainSingle().Subject;
            record.Should().BeEquivalentTo(PaymentHistoryRecords.FromJob(job, [owner.Id], []),
                o => o.ComparingRecordsByMembers());
            record.RecordId.Should().Be(attempts > deliveries
                ? PaymentRecordIds.Unconfirmed(job.Id, deliveries, attempts)
                : PaymentRecordIds.Delivered(job.Id, deliveries));
            record.State.Should().Be(attempts > deliveries ? PaymentRequestStates.Unconfirmed : PaymentRequestStates.Delivered);
        }
        finally { Drop(db); }
    }

    [Theory]
    [InlineData("network")]
    [InlineData("server_error")]
    public async Task Mod_Offta_Cloud_APIden_sonra_acilamayan_wame_unconfirmed_kayit(string errorCode)
    {
        var db = FileDb();
        try
        {
            EnableCloudApi();
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);
            bed.Handler.SendResponseJson =
                $$"""{"ok":false,"errorCode":"{{errorCode}}","errorMessage":"x","messageId":null}""";
            _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.LaunchFailed);

            var job = Isler(db, "cumulative").Should().ContainSingle().Subject;
            job.HasUnconfirmedSend.Should().BeTrue();
            var record = Kayitlar(bed).Should().ContainSingle().Subject;
            record.State.Should().Be(PaymentRequestStates.Unconfirmed);
            record.RecordId.Should().Be(PaymentRecordIds.Unconfirmed(job.Id, 0, 1));
            record.NetAmount.Should().Be(250m);
            KiraUcuYok(bed);
        }
        finally { Drop(db); }
    }

    [Theory]
    [InlineData(null)]               // Cloud API kapalı: wa.me açılamadı, mesaj kesin gitmedi
    [InlineData("window_closed")]    // sunucu Graph'a çıkmadan reddetti: yine kesin gitmedi
    public async Task Mod_Offta_duz_LaunchFailed_kayit_yazmaz(string? cloudCode)
    {
        var db = FileDb();
        try
        {
            if (cloudCode is not null) EnableCloudApi();
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);
            if (cloudCode is not null)
                bed.Handler.SendResponseJson =
                    $$"""{"ok":false,"errorCode":"{{cloudCode}}","errorMessage":"x","messageId":null}""";
            _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.LaunchFailed);

            bed.Pending.Count().Should().Be(0);
            Isler(db, "cumulative").Should().ContainSingle().Which.HasUnconfirmedSend.Should().BeFalse("deneme geri alındı");
            KiraUcuYok(bed);
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Lisans_anahtari_yoksa_kayit_yazilmaz()
    {
        var db = FileDb();
        try
        {
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db, new NoLicense());

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.Opened);

            bed.Pending.Count().Should().Be(0);
            bed.Handler.RequestPaths.Should().BeEmpty("lisanssız bugünkü gibi: ağ yok");
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task GUIDsiz_kiside_her_teslim_rastgele_Idli_kayit_sahip_belirlenimli()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "ornek_a", id: $"tiktok:ornek_a_{Guid.NewGuid():N}");
            var b = Uye(db, "ornek_b", phone: false, id: $"instagram:ornek_b_{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var session = Oturum(db);
            var la = Etiket(db, session, a.Id, 120m);
            var lb = Etiket(db, session, b.Id, 80m);
            var bed = OffSut(db);
            var scope = $"session:{session}";
            var person = db.Customers.GetPaymentPerson(a.Id);
            person.OwnerId.Should().BeNull("GUID'li üye yok: iş açılmaz");

            (await bed.Sut.OpenWhatsAppAsync(b, 200m, T, scope)).Should().Be(PaymentRequestResult.Opened);
            (await bed.Sut.OpenWhatsAppAsync(a, 200m, T, scope)).Should().Be(PaymentRequestResult.Opened,
                "işsiz kişide soru yok — bugünkü gibi");

            Isler(db).Should().BeEmpty();
            var records = Kayitlar(bed);
            records.Should().HaveCount(2);
            records.Select(r => r.RecordId).Distinct().Should().HaveCount(2, "işsiz kişide her gönderim yeni rastgele Id");
            records.Should().OnlyContain(r =>
                r.OwnerId == person.Members[0].Id
                && r.MemberIds.SequenceEqual(new[] { person.Members[0].Id, person.Members[1].Id })
                && r.ScopeKey == scope
                && r.State == PaymentRequestStates.Delivered
                && r.NetAmount == 200m && r.AppliedAmount == null && !r.FullyFromBalance);
            person.Members[0].Id.Should().Be(a.Id, "sahip kuralı: telefonlu üye önce (tıklanan satırdan bağımsız)");
            records[0].OrderIds.Should().BeEquivalentTo(new[] { Guid.ParseExact(la, "N"), Guid.ParseExact(lb, "N") });
            KiraUcuYok(bed);
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Giriste_mod_Off_secen_akis_kip_arada_1_olsa_da_kaydini_yazar()
    {
        // Kirasız kaydın kararı akışın GİRİŞİNDEKİ kipe göre: istek mod Off yolundan girdi (kira yok), bakiye uçlarında
        // beklerken açılış turu yerel kipi 1 yaptı. Teslimden sonra kip yeniden okunsaydı isteğin ne kirası ne kaydı
        // olurdu; ödeme geçmişi o ana dek tamamlandığından onu bir daha hiçbir yol göndermezdi.
        var db = FileDb();
        try
        {
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);
            bed.Handler.PreviewBalance = 100m;
            bed.Handler.OnApplyAsync = _ =>
            {
                ModOn(db);                                                    // açılış turunun 2. adımı
                return Task.CompletedTask;
            };

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.Opened);

            new SalesSyncStateRepository(db.Factory).GetMode().Should().Be(1, "ön koşul: kip akış sürerken değişti");
            var job = Isler(db, "cumulative").Should().ContainSingle().Subject;
            var record = Kayitlar(bed).Should().ContainSingle("giriş mod Off'u seçti: teslimin kaydı yazılır").Subject;
            record.RecordId.Should().Be(PaymentRecordIds.Delivered(job.Id, 1));
            record.NetAmount.Should().Be(150m);
            KiraUcuYok(bed);
        }
        finally { Drop(db); }
    }

    [Fact]
    public async Task Yerel_kip_On_iken_bu_yol_kayit_yazmaz()
    {
        // Mod On'un kaydı kiradır (begin/finish, W3-07); bu yol yalnız Mode = 0'da yazar.
        var db = FileDb();
        try
        {
            ModOn(db);
            var owner = Uye(db, "ornek_sahip");
            var bed = OffSut(db);

            (await bed.Sut.OpenWhatsAppAsync(owner, 250m, T, "cumulative")).Should().Be(PaymentRequestResult.Opened);

            bed.Pending.Count().Should().Be(0);
        }
        finally { Drop(db); }
    }
}
