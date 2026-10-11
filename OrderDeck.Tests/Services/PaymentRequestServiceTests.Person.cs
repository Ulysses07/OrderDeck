using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Ödeme isteği KİŞİ düzeyinde (tasarım 1, 2026-10-10) ─────────────────────
//
// İş eskiden kartın birincil satırının Id'siyle aranıyordu; birincil satır değişince (telefon
// eklendi, arama sırası, birleştirme) aynı kişi ve kapsam için ikinci iş açılıyor, bakiye ikinci kez
// düşüyordu. Artık mantıksal kimlik (kişinin güncel üye kümesi, kapsam); işin CustomerId'si sahip
// satırdır ve sunucuya giden her çağrı onunla yapılır. Gerçek SQLite dosyası (FileDb): üye kümesi
// ödeme işinin yazma işleminin İÇİNDE Customer tablosundan yeniden okunur.
public partial class PaymentRequestServiceTests
{
    /// <summary>Kişinin bir üyesi (GUID Id'li Customer satırı).</summary>
    private static Customer Uye(
        SyncedDb db, string username, bool phone = true, bool recipientPays = false, string? id = null)
    {
        var c = MakeCustomer(phone ? TestPhone.NewE164() : null, recipientPays, id ?? Guid.NewGuid().ToString("N"))
            with { Username = username };
        db.Customers.Insert(c);
        return c;
    }

    private static void Kisi(SyncedDb db, params Customer[] members)
        => db.Customers.MergeIntoGroup(members.Select(m => m.Id).ToList());

    private (PaymentRequestService Sut, WhatsAppStubHandler Handler) KisiSut(SyncedDb db)
        => MakeCloudSut(_store, _launcher, jobs: db.Jobs, customers: db.Customers, busy: db.Busy);

    private static PaymentJob[] Isler(SyncedDb db, string? scopeKey = null)
    {
        using var conn = db.Factory.Open();
        return Dapper.SqlMapper.Query<string>(conn,
                "SELECT Id FROM PaymentJob WHERE @scopeKey IS NULL OR ScopeKey = @scopeKey", new { scopeKey })
            .Select(id => db.Jobs.Get(id)!).ToArray();
    }

    /// <summary>Kaydı Customer satırına bağlı açık miras iş (033→034 ya da taşıma): anahtar diskte,
    /// sonuç bilinmiyor.</summary>
    private static (PaymentJob Job, Guid Key) MirasIs(SyncedDb db, string customerId, decimal total = 250m)
    {
        var key = Guid.NewGuid();
        var job = db.Jobs.FindOrCreate(customerId, $"legacy:{key:N}:{Guid.NewGuid():N}", total);
        db.Jobs.BeginApply(job.Id, key).Should().BeTrue();
        return (job, key);
    }

    private static Guid G(Customer c) => Guid.ParseExact(c.Id, "N");

    private static string ScopeJsonAt(Guid tx, decimal applied, decimal total, DateTimeOffset at) => $$"""
        {"transactionId":"{{tx}}",
         "appliedAmount":{{applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "productTotal":{{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
         "createdAt":"{{at:O}}"}
        """;

    [Fact] // Repro A: birincil satır kayınca ikinci tıklama aynı işin tekrar paylaşımıdır
    public async Task Birincil_kayinca_ikinci_tiklama_ayni_isi_bulur_ikinci_dusum_yok()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a", phone: false);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            // Kartın birincili telefonlu b: iş açılır, düşülür, teslim edilir.
            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            // Arama sonrası kartın birincili a oldu: aynı kişi, aynı iş — önce sorulur, cevapsız hiçbir şey olmaz.
            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.AlreadyRequested);
            _launcher.LaunchedUrls.Should().ContainSingle();

            (await EvetAsync(sut, a, 250m, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.AppliedBalanceBodies.Should().ContainSingle("aynı kişi × kapsam: tek düşüm");
            Isler(db, "session:s1").Should().ContainSingle("birincil kayması ikinci iş açmaz");
            _launcher.LaunchedUrls.Should().HaveCount(2).And.OnlyContain(u => u.Contains("150%2C00"),
                "iki mesaj aynı tutar: 250 − 100");
        }
        finally { Drop(db); }
    }

    [Fact] // Sunucuya giden çağrılar işin SAHİP satırıyla — tıklanan satırla değil
    public async Task Tiklanan_satir_degil_isin_sahip_satiri_sunucuya_gider()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a", phone: false);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            var outcome = await sut.RequestPaymentAsync(a, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.Opened);
            outcome.OwnerId.Should().Be(b.Id, "W2 beklentisi sahip satırına yazılır");
            outcome.Demoted.Should().BeEmpty();
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(b.Id, "sahip: telefonlu üye (belirlenimli kural)");
            handler.PreviewCustomerIds.Should().OnlyContain(id => id == G(b));
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(b));
            handler.ScopeQueries.Should().Contain(q => q.Contains(G(a).ToString("D")))
                .And.Contain(q => q.Contains(G(b).ToString("D")),
                    "kişi düzeyinde R4-03: taze işte kişinin her GUID'li üyesi sorulur");
        }
        finally { Drop(db); }
    }

    // Sahip kararı 4 + yargıçların zorunlu düzeltmesi: ayrı kartken iki iş düşülmüş ve teslim edilmiş;
    // birleşince birleştirme SESSİZ olamaz. Bir kez sorulur; ret → hiçbir şey değişmez. Onayda kaybeden
    // kendi sahip satırıyla tekrar oynatılıp geri alınır, kalan iş kişi toplamına revize edilir; kişiye
    // tek düzeltilmiş mesaj gider.
    [Fact]
    public async Task Birlesen_kisinin_parali_iki_isi_sorulur_onayda_kaybeden_iade_edilir_kalan_revize_edilir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var c = Uye(db, "kisi.c");
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 50m;
            handler.CapAppliedToRequest = true;

            (await sut.OpenWhatsAppAsync(a, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(c, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var keys = Isler(db, "session:s1").Should().HaveCount(2).And.Subject
                .ToDictionary(j => j.CustomerId, j => j.ApplyKey!.Value);
            Kisi(db, a, c);
            var mesajlar = _launcher.LaunchedUrls.Count;
            var applySayisi = handler.AppliedBalanceBodies.Count;

            // Ret: indirme, geri alma, ağ çağrısı ve mesaj YOK.
            var hayir = new FakeDialogService();
            var red = await PaymentResultPresenter.RequestConfirmedAsync(hayir, t =>
                sut.RequestPaymentAsync(a, _ => 300m, T, "session:s1", t));
            red.Result.Should().Be(PaymentRequestResult.MergeRequired);
            hayir.Confirmations.Should().ContainSingle().Which.Message.Should()
                .MatchRegex(@"bu yayın için 2 istek gitmiş \((50 ₺, 150 ₺|150 ₺, 50 ₺)\)", "istenen netler: 100 − 50, 200 − 50")
                .And.Contain("Fazladan düşülen bakiye iade edilir");
            Isler(db, "session:s1").Should().HaveCount(2);
            handler.ReverseCalls.Should().BeEmpty();
            handler.AppliedBalanceBodies.Should().HaveCount(applySayisi);
            _launcher.LaunchedUrls.Should().HaveCount(mesajlar);

            // Onay.
            var evet = new FakeDialogService { ConfirmResult = _ => true };
            var outcome = await PaymentResultPresenter.RequestConfirmedAsync(evet, t =>
                sut.RequestPaymentAsync(a, _ => 300m, T, "session:s1", t));

            outcome.Result.Should().Be(PaymentRequestResult.Opened);
            evet.Confirmations.Should().ContainSingle("birleştirme bir kez sorulur — ayrıca tekrar gönderme sorulmaz");
            var kalan = Isler(db, "session:s1").Should().ContainSingle().Subject;
            var kaybeden = Isler(db).Single(j => j.Id != kalan.Id);
            kalan.ProductTotal.Should().Be(300m, "kalan iş kişi toplamına revize edildi");
            kalan.State.Should().Be(PaymentJobState.Applied);
            kaybeden.ScopeKey.Should().StartWith("legacy:");
            kaybeden.PriorScopeKey.Should().Be("session:s1", "W2 kaybedenin beklentisini bununla iptal eder");
            kaybeden.ClosedAt.Should().NotBeNull("geri alındı ve kapandı");
            outcome.OwnerId.Should().Be(kalan.CustomerId);
            outcome.Demoted.Should().ContainSingle().Which.Should().Be(
                new DemotedJob(kaybeden.Id, kaybeden.CustomerId, "session:s1", KeptOpen: true));

            // Kaybeden KENDİ sahip satırıyla tekrar oynatıldı (başka satırın Id'si içerik çakışması olurdu).
            var yeniApplyler = handler.AppliedBalanceBodies.Skip(applySayisi).ToList();
            var replay = yeniApplyler.Single(body => AppliedKey(body) == keys[kaybeden.CustomerId]);
            WhatsAppStubHandler.WpfCustomerIdOf(replay).Should().Be(Guid.ParseExact(kaybeden.CustomerId, "N"));
            handler.ReverseCalls.Should().BeEquivalentTo(new[] { keys[a.Id], keys[c.Id] },
                "kaybedenin düşümü iade edildi; kalanın eski düşümü revizyonla geri alındı");

            _launcher.LaunchedUrls.Should().HaveCount(mesajlar + 1, "kişiye tek düzeltilmiş mesaj");
            _launcher.LaunchedUrls[^1].Should().Contain("250%2C00", "300 − 50");
        }
        finally { Drop(db); }
    }

    [Fact] // Başka üyenin miras işi benimsenmez: kendi Id'siyle tekrar oynatılır ve geri alınır
    public async Task Baska_uyenin_miras_isi_kendi_sahibiyle_oynatilir_benimsenmez_geri_alinir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a", phone: false);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (miras, k) = MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.AppliedBalanceBodies.Should().HaveCount(2);
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(k);
            handler.AppliedCustomerIds[0].Should().Be(G(a), "miras iş KENDİ sahip satırıyla oynatılır");
            handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(k,
                "taze iş başka satıra ait (b); a'nın düşümü benimsenmez, iade edilir");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(b.Id);
            job.ApplyKey.Should().NotBe(k);
            job.State.Should().Be(PaymentJobState.Applied);
            handler.AppliedCustomerIds[1].Should().Be(G(b));
            db.Jobs.Get(miras.Id)!.ClosedAt.Should().NotBeNull();
        }
        finally { Drop(db); }
    }

    [Fact] // Çevrimdışı: kişinin BAŞKA üyesinde açık miras iş varsa mesaj gitmez
    public async Task Cevrimdisi_baska_uyede_acik_miras_varsa_BalanceUncertain()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a", phone: false);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.ThrowTimeoutOnLicenses = true;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.BalanceUncertain);

            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // GUID olmayan satırın miras işi kapatılır, akışı bloklamaz (sunucu o Id'ye düşüm yazamaz)
    public async Task GUID_olmayan_uyenin_miras_isi_kapatilir_akis_surer()
    {
        var db = FileDb();
        try
        {
            var x = Uye(db, "kisi.x", phone: false, id: "yerel-" + Guid.NewGuid().ToString("N"));
            var g = Uye(db, "kisi.g");
            Kisi(db, x, g);
            var (miras, k) = MirasIs(db, x.Id);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(g, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            db.Jobs.Get(miras.Id)!.ClosedAt.Should().NotBeNull("uyarı günlüğüyle kapatıldı — kişi kalıcı bloklanmaz");
            handler.AppliedBalanceBodies.Select(AppliedKey).Should().NotContain(k);
            handler.ReverseCalls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // GUID'siz tıklanan satırın GUID'li üyesi varsa bakiye akışı o sahiple koşar
    public async Task GUIDsiz_satirin_GUIDli_uyesi_varsa_bakiye_akisi_o_sahiple_kosar()
    {
        var db = FileDb();
        try
        {
            var x = Uye(db, "kisi.x", id: "yerel-" + Guid.NewGuid().ToString("N"));
            var g = Uye(db, "kisi.g", phone: false);
            Kisi(db, x, g);
            var yalniz = Uye(db, "kisi.y", id: "yerel-" + Guid.NewGuid().ToString("N"));
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(x, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(g.Id);
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(g));
            _launcher.LaunchedUrls[^1].Should().Contain("150%2C00");

            // GUID'li üyesi olmayan kişi: bugünkü gibi iş açılmaz, mesaj düşümsüz gider.
            (await sut.OpenWhatsAppAsync(yalniz, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Isler(db, "session:s1").Should().ContainSingle();
            _launcher.LaunchedUrls[^1].Should().Contain("250%2C00");
        }
        finally { Drop(db); }
    }

    [Fact] // Sahip kararı 1: alıcı ödemeli üyelerden HERHANGİ birinde açıksa kişi alıcı ödemelidir
    public async Task Alici_odemeli_kisinin_herhangi_bir_uyesinde_aciksa_kargo_eklenmez()
    {
        var db = FileDb();
        try
        {
            var settings = new AppSettings();
            settings.Shipping.FreeShippingThreshold = 10_000m;
            settings.Shipping.ShippingFee = 50m;
            _store.Save(settings);
            var a = Uye(db, "kisi.a", phone: false, recipientPays: true);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var tek = Uye(db, "kisi.tek");
            var (sut, _) = KisiSut(db);

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            _launcher.LaunchedUrls[^1].Should().Contain("250%2C00", "kiralanan b'de bayrak kapalı ama kişi alıcı ödemeli");

            (await sut.OpenWhatsAppAsync(tek, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            _launcher.LaunchedUrls[^1].Should().Contain("300%2C00", "karşılaştırma: bayraksız kişiye kargo eklenir");
        }
        finally { Drop(db); }
    }

    [Fact] // U13 kişi düzeyinde: akış sürerken tıklanmayan üye de taşınmaz; akış bitince taşınır
    public async Task Akis_surerken_tiklanmayan_uye_tasinmaz_akis_bitince_tasinir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var bAsil = Uye(db, "Kisi.B");                         // b'nin başka bilgisayardaki yazımı
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            RekeyResult? akisSirasinda = null;
            handler.OnPreviewAsync = () =>
            {
                akisSirasinda = db.Sync.RekeyToLocal(b.Id, bAsil.Id, pushedThroughSeq: long.MaxValue, nowUnix: SyncNow);
                return Task.CompletedTask;
            };

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            akisSirasinda.Should().Be(RekeyResult.Busy, "kişinin bütün üyeleri kiralı (tıklanmayan b dahil)");
            Leased(db, b.Id).Should().BeFalse("akış bitince bütün kiralar bırakılır");
            db.Sync.RekeyToLocal(b.Id, bAsil.Id, pushedThroughSeq: long.MaxValue, nowUnix: SyncNow)
                .Should().Be(RekeyResult.Rekeyed);
        }
        finally { Drop(db); }
    }

    // Kişi kilidi: iki pencereden (rapor + müşteriler) aynı kişinin FARKLI üyelerine aynı anda
    // tıklandı. İkinci akış kilitte bekler (para adımına varamaz); birinci teslim edince ikinci aynı
    // işi bulur ve tekrar göndermeyi sorar. Gerçek SQLite dosyası: tek iş.
    [Fact]
    public async Task Iki_pencereden_farkli_uyelerle_es_zamanli_tiklama_sira_bekler_tek_is()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var onizlemeler = 0;
            var birinciOnizlemede = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var birinciSerbest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnPreviewAsync = async () =>
            {
                if (Interlocked.Increment(ref onizlemeler) != 1) return;
                birinciOnizlemede.TrySetResult();
                await birinciSerbest.Task;
            };

            var t1 = Task.Run(() => sut.OpenWhatsAppAsync(a, 250m, T, "session:s1"));
            await birinciOnizlemede.Task;
            var t2 = Task.Run(() => sut.OpenWhatsAppAsync(b, 250m, T, "session:s1"));

            (await Task.WhenAny(t2, Task.Delay(300))).Should().NotBeSameAs(t2, "ikinci akış kişi kilidinde bekler");
            Volatile.Read(ref onizlemeler).Should().Be(1, "ikinci akış para adımına varamadı");

            birinciSerbest.TrySetResult();
            (await t1).Should().Be(PaymentRequestResult.Opened);
            (await t2).Should().Be(PaymentRequestResult.AlreadyRequested,
                "ikinci akış birincinin teslim ettiği işi bulur — tekrar göndermeden önce sorulur");

            handler.AppliedBalanceBodies.Should().ContainSingle();
            Isler(db).Should().ContainSingle("kişi × kapsam tek iş");
            _launcher.LaunchedUrls.Should().ContainSingle();
        }
        finally { Drop(db); }
    }

    [Fact] // Kişi kilidinin beklemesi sınırlı: öbür pencerenin akışı takılırsa "başka ödeme akışı sürüyor"
    public async Task Kisi_kilidi_beklemesi_dolarsa_PaymentInProgress_hicbir_sey_gonderilmez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            sut.PersonGateTimeout = TimeSpan.FromMilliseconds(100);
            handler.PreviewBalance = 100m;
            var birinciOnizlemede = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var birinciSerbest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnPreviewAsync = async () =>
            {
                birinciOnizlemede.TrySetResult();
                await birinciSerbest.Task;
            };

            var t1 = Task.Run(() => sut.OpenWhatsAppAsync(a, 250m, T, "session:s1"));
            await birinciOnizlemede.Task;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.PaymentInProgress);
            _launcher.LaunchedUrls.Should().BeEmpty();
            Leased(db, b.Id).Should().BeTrue("yalnız birinci akışın kirası kaldı");

            birinciSerbest.TrySetResult();
            (await t1).Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().ContainSingle();
            Leased(db, b.Id).Should().BeFalse("ikinci akışın kiraları da bırakıldı");
        }
        finally { Drop(db); }
    }

    [Fact] // 046 yeniden numaralama yalnız GroupId değerini değiştirir: iş CustomerId'ye bağlı, aynı iş bulunur
    public async Task Grup_kimligi_yeniden_numaralaninca_ayni_is_bulunur()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            using (var conn = db.Factory.Open())
                Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET GroupId = @g WHERE Id IN @ids",
                    new { g = "grp-" + Guid.NewGuid().ToString("N"), ids = new[] { a.Id, b.Id } });

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.AlreadyRequested);
            (await EvetAsync(sut, b, 250m, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Isler(db).Should().ContainSingle();
            handler.AppliedBalanceBodies.Should().ContainSingle();
        }
        finally { Drop(db); }
    }

    // D-1 (yargıç aşısı): üye kümesi akışın okumasıyla ödeme işinin yazma işlemi arasında değişirse
    // işlemin içindeki yeniden okuma bunu görür, hiçbir şey yazılmaz; akış kişiyi yeniden okuyup
    // (yeni üyeyi kiralayarak) tekrar dener. Kabul edilen "milisaniyelik pencere" yerine.
    [Fact]
    public async Task Uye_kumesi_akis_icinde_degisirse_kisi_yeniden_okunur_tek_is()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            var c = Uye(db, "kisi.c");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var okunanUyeler = new List<int>();

            var outcome = await sut.RequestPaymentAsync(a, person =>
            {
                okunanUyeler.Add(person.MemberIds.Count);
                // Tutar okunduktan hemen sonra öbür pencerede elle birleştirme (kiraya takılmaz).
                if (okunanUyeler.Count == 1) db.Customers.MergeIntoGroup(new[] { a.Id, c.Id });
                return 250m;
            }, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.Opened);
            okunanUyeler.Should().Equal(new[] { 2, 3 }, "değişen küme yazma işleminde yakalandı, kişi yeniden okundu");
            Isler(db).Should().ContainSingle();
            handler.AppliedBalanceBodies.Should().ContainSingle();
        }
        finally { Drop(db); }
    }

    // Kişi düzeyinde R4-03 (yargıç aşısı): taze iş açılırken sunucuda bu kapsamın düşümü kişinin BAŞKA
    // bir üyesinde olabilir (eski sahip, eski sürüm bilgisayar, yedekten dönen veritabanı). İz tek
    // üyedeyse iş o üyeye geçer ve iz benimsenir — ikinci düşüm yok.
    [Fact]
    public async Task Kapsam_izi_baska_uyedeyse_is_o_uyeye_gecer_benimsenir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a", phone: false);
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var tx = Guid.NewGuid();
            handler.ScopeResponseFor = id => id == G(a) ? ScopeJsonAt(tx, 40m, 250m, DateTimeOffset.UtcNow) : null;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.AppliedBalanceBodies.Should().BeEmpty("sunucu bu satışı a'da zaten düşmüş");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(a.Id, "düşüm a'nın asıl kaydında — anahtar onunla tanınır");
            job.ApplyKey.Should().Be(tx);
            _launcher.LaunchedUrls[^1].Should().Contain("210%2C00", "250 − 40");
        }
        finally { Drop(db); }
    }

    [Fact] // İz iki üyede (aynı kişiye iki satırdan düşülmüş): sorulur; onayda fazlası iade edilir
    public async Task Kapsam_izi_iki_uyedeyse_sorulur_onayda_yenisi_iade_edilir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var b = Uye(db, "kisi.b");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var (txA, txB) = (Guid.NewGuid(), Guid.NewGuid());
            var t0 = DateTimeOffset.UtcNow.AddHours(-2);
            handler.ScopeResponseFor = id =>
                id == G(a) ? ScopeJsonAt(txA, 40m, 250m, t0)
                : id == G(b) ? ScopeJsonAt(txB, 40m, 250m, t0.AddHours(1))
                : null;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.MergeRequired);
            handler.ReverseCalls.Should().BeEmpty("onaydan önce geri alma yok");
            _launcher.LaunchedUrls.Should().BeEmpty();

            (await EvetAsync(sut, a, 250m, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(txB, "en eski düşüm kalır, fazlası iade");
            handler.AppliedBalanceBodies.Should().BeEmpty();
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(a.Id);
            job.ApplyKey.Should().Be(txA);
        }
        finally { Drop(db); }
    }
}
