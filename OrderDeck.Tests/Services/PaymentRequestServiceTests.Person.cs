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
    //
    // Bakiye SATIR BAŞINA (sunucu gibi): a'da 50, c'de 50. Ayrı kartken iki istek kendi ürünleri içindi
    // (50 + 150 = 200 istendi, iki bakiye de kullanıldı). Birleştirme kaybedenin düşümünü KENDİ satırına
    // iade eder; kalan işin revizyonu onu kullanamaz → 300 − 50 = 250 istenir, 50 a'da kalır. Bu, kabul
    // edilmiş tasarımın sonucu (kişi düzeyinde bakiye sunucu kararı); soru metni bunu "fazladan düşülen"
    // diye gizlemez, iadenin başka hesaba döndüğünü söyler — kararı operatör verir.
    [Fact]
    public async Task Birlesen_kisinin_parali_iki_isi_sorulur_onayda_kaybeden_iade_edilir_kalan_revize_edilir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var c = Uye(db, "kisi.c");
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(a)] = 50m, [G(c)] = 50m };

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
                .MatchRegex(@"bu yayın için 2 istek gitmiş \((50,00 TL, 150,00 TL|150,00 TL, 50,00 TL)\)",
                    "istenen netler: 100 − 50, 200 − 50")
                .And.Contain("Önceki düşümler iade edilir")
                .And.Contain("iade o hesaba döner, bu istekte kullanılmaz", "düşümler farklı satırlarda")
                .And.NotContain("Fazladan", "ayrı ürünler için yapılmış düşüm fazla değildir");
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
            _launcher.LaunchedUrls[^1].Should().Contain("250%2C00",
                "300 − 50: kalan iş yalnız kendi satırının bakiyesini kullanabilir");
            handler.RowBalances[Guid.ParseExact(kaybeden.CustomerId, "N")].Should().Be(50m,
                "kaybedenin iadesi kendi satırında kalır (soruda söylenen)");
            handler.RowBalances[Guid.ParseExact(kalan.CustomerId, "N")].Should().Be(0m);
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

            var soru = await sut.RequestPaymentAsync(a, _ => 250m, T, "session:s1");
            soru.Result.Should().Be(PaymentRequestResult.MergeRequired);
            // İnceleme turu 2: sunucu teslim bilgisini bilmez — izler "gitmiş istek" sayılmaz.
            soru.Prompt!.Requests.Should().BeEmpty("sunucudaki düşümün müşteriye gidip gitmediği bilinmiyor");
            soru.Prompt.RemoteDeductions.Should().Equal(40m, 40m);
            handler.ReverseCalls.Should().BeEmpty("onaydan önce geri alma yok");
            _launcher.LaunchedUrls.Should().BeEmpty();

            var dialogs = new FakeDialogService();
            (await EvetAsync(sut, a, 250m, "session:s1", dialogs)).Should().Be(PaymentRequestResult.Opened);
            dialogs.Confirmations.Should().ContainSingle().Which.Message.Should()
                .StartWith("Sunucuda bu kişinin bu yayın için 2 ayrı bakiye düşümü bulundu (40,00 TL, 40,00 TL)")
                .And.NotContain("istek gitmiş");

            handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(txB, "en eski düşüm kalır, fazlası iade");
            handler.AppliedBalanceBodies.Should().BeEmpty();
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(a.Id);
            job.ApplyKey.Should().Be(txA);
        }
        finally { Drop(db); }
    }

    // ── İnceleme turu 1 ──────────────────────────────────────────────────────

    // Eşzamanlılık: çekmeceler modal değil — aynı kişiye iki girişten (arama çekmecesi, rapor) aynı
    // "tekrar gönderilsin mi?" sorusu açılabilir, ikisi de aynı belirteci taşır. Birine "Evet" denip
    // tekrar gönderildikten sonra öbürünün "Evet"i ESKİ duruma verilmiş onaydır: yeniden sorulur, soru
    // son gönderimin saatini gösterir, mesaj gitmez. Eskiden tekrar paylaşım işin parmak izini
    // değiştirmiyordu (finansal yazım yok, Close ilk ClosedAt'ı koruyordu) → üçüncü ücretli mesaj.
    [Fact]
    public async Task Tekrar_paylasimdan_once_alinan_ikinci_onay_yeniden_sorulur_son_gonderimi_gosterir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
        var ilkTeslim = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();   // ilk istek bir saat önce gitmiş
        // Teslim işi o anda kapattı (ClosedAt teslim anından sonra olamaz — olsaydı önceki sürümün sonraki
        // teslimi sayılırdı).
        _jobs.Seed(_jobs.Snapshot.Single() with { DeliveredAt = ilkTeslim, ClosedAt = ilkTeslim });

        Task<PaymentRequestOutcome> Iste(IReadOnlySet<string>? tokens = null)
            => sut.RequestPaymentAsync(customer, _ => 250m, T, "session:s1", tokens);
        var cekmece = await Iste();
        var rapor = await Iste();
        cekmece.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        rapor.Prompt!.Token.Should().Be(cekmece.Prompt!.Token, "iki soru aynı durumu gösteriyor");
        cekmece.Prompt.Requests.Single().At.Should().Be(DateTimeOffset.FromUnixTimeSeconds(ilkTeslim));

        (await Iste(new HashSet<string> { rapor.Prompt.Token })).Result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(2);

        var bayatOnay = await Iste(new HashSet<string> { cekmece.Prompt.Token });

        bayatOnay.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "onay tekrar gönderimden önceki duruma verildi");
        bayatOnay.Prompt!.Token.Should().NotBe(cekmece.Prompt.Token);
        bayatOnay.Prompt.Requests.Single().At.Should().BeAfter(DateTimeOffset.FromUnixTimeSeconds(ilkTeslim).AddMinutes(30),
            "soru SON gönderimin saatini gösterir");
        _launcher.LaunchedUrls.Should().HaveCount(2, "bayat onayla mesaj gitmez");
        handler.AppliedBalanceBodies.Should().ContainSingle();
    }

    // Çevrimdışı (lisans çözülemiyor) gönderilen istek de teslim edilmiştir: iş taze (created) kalır ama
    // teslim kaydı yazılır. İkinci tıklama "tekrar gönderilsin mi?" diye sorar — çevrimdışıyken de,
    // sunucu döndükten sonra da. Eskiden iş açık ve "teslim edilmemiş" kalıyordu: ikinci mesaj sorusuz
    // ve sunucu döndüyse düşümlü (farklı tutarla) gidiyordu.
    [Fact]
    public async Task Cevrimdisi_gonderilen_istek_sonraki_tiklamada_sorulur_cevrimiciye_donuste_de()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.Created);
        job.IsDelivered.Should().BeTrue("mesaj müşteriye ulaştı");
        job.ClosedAt.Should().BeNull("taze iş açık kalır — onaylı sonraki tıklama düşüm yapabilmeli");

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.AlreadyRequested, "çevrimdışı ikinci tıklama");

        handler.ThrowTimeoutOnLicenses = false;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.AlreadyRequested, "sunucu döndü — yine sorulur");
        _launcher.LaunchedUrls.Should().ContainSingle();
        handler.AppliedBalanceBodies.Should().BeEmpty();

        var dialogs = new FakeDialogService();
        (await EvetAsync(sut, customer, 250m, "session:s1", dialogs)).Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain("250,00 TL istendi");
        handler.AppliedBalanceBodies.Should().ContainSingle("onaylı çevrimiçi tıklama düşümü yapar");
        _launcher.LaunchedUrls.Should().HaveCount(2);
        _launcher.LaunchedUrls[^1].Should().Contain("150%2C00", "250 − 100");
    }

    // Para incelemesi (kritik), yol 1: S1'de aynı insan iki satırda — x (kopya) 100 ₺ bakiyeyle
    // ödendi (mesaj 0 ₺), y (asıl) bakiyesiz 200 ₺. Taşıma x'in teslim edilmiş işini açık mirasa
    // indiriyor (asıl kapsamı S1). S2'deki tıklama o düşümü ELLEMEMELİ: eskiden S2'nin taze işine
    // benimsenip revizyonla geri alınıyor, S1 ürünlerinin bakiye ödemesi iade edilip S2'de yeniden
    // harcanıyordu (yayıncı 100 ₺ kaybı). Mirası kendi kapsamının tıklaması uzlaştırır: önce sorar
    // (istek müşteriye gitmişti), sonra geri alır ve kalan işi kişinin S1 toplamına revize eder.
    [Fact]
    public async Task Tasinan_teslim_edilmis_S1_dususmu_S2_tiklamasinda_ellenmez_S1_tiklamasinda_uzlasir()
    {
        var db = FileDb();
        try
        {
            var x = Uye(db, "kisi.x");                      // kopya (başka platform yazımı)
            var y = Uye(db, "Kisi.X");                      // asıl kayıt
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(y)] = 100m };
            handler.CanonicalOf = id => id == G(x) ? G(y) : id;   // sunucu x'i y'nin kopyası biliyor (A5b)

            (await sut.OpenWhatsAppAsync(x, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(y, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var copyKey = Isler(db, "session:s1").Single(j => j.CustomerId == x.Id).ApplyKey!.Value;
            db.Sync.RekeyToLocal(x.Id, y.Id, pushedThroughSeq: long.MaxValue, nowUnix: SyncNow)
                .Should().Be(RekeyResult.Rekeyed);
            var miras = db.Jobs.GetOpenLegacies(y.Id).Should().ContainSingle().Subject;
            miras.PriorScopeKey.Should().Be("session:s1");

            // S2: başka yayının düşümü ne bloklar ne benimsenir ne geri alınır.
            (await sut.OpenWhatsAppAsync(y, 150m, T, "session:s2")).Should().Be(PaymentRequestResult.Opened);
            handler.ReverseCalls.Should().BeEmpty("S1'in düşümü S2'de iade edilmez");
            handler.AppliedBalanceBodies.Select(AppliedKey).Should().ContainSingle(k => k == copyKey,
                "S2 tıklaması S1 mirasını oynatmaz");
            Isler(db, "session:s2").Single().ApplyKey.Should().NotBe(copyKey);
            db.Jobs.GetOpenLegacies(y.Id).Should().ContainSingle().Which.Id.Should().Be(miras.Id);
            _launcher.LaunchedUrls[^1].Should().Contain("150%2C00", "S1 ürünlerinin bakiyesi S2'de harcanmaz");
            handler.RowBalances[G(y)].Should().Be(0m);

            // S1: miras istek müşteriye gitmişti — önce sorulur; onayda geri alınır, kalan iş 300'e revize.
            var dialogs = new FakeDialogService();
            (await EvetAsync(sut, y, 300m, "session:s1", dialogs)).Should().Be(PaymentRequestResult.Opened);
            dialogs.Confirmations.Should().ContainSingle().Which.Message.Should()
                .MatchRegex(@"bu yayın için 2 istek gitmiş \((0,00 TL, 200,00 TL|200,00 TL, 0,00 TL)\)",
                    "x'e 100 − 100, y'ye 200 istendi")
                .And.NotContain("başka bir hesabının", "taşımadan sonra iki düşüm de aynı asıl kayıtta");
            handler.ReverseCalls.Should().Equal(new[] { copyKey }, "S1 mirası kendi kapsamında geri alındı");
            db.Jobs.GetOpenLegacies(y.Id).Should().BeEmpty();
            var s1 = Isler(db, "session:s1").Should().ContainSingle().Subject;
            s1.CustomerId.Should().Be(y.Id);
            s1.ProductTotal.Should().Be(300m);
            _launcher.LaunchedUrls[^1].Should().Contain("200%2C00", "300 − 100: S1 için toplam tahsilat değişmedi");
        }
        finally { Drop(db); }
    }

    // Para incelemesi (kritik), yol 2: ayrı kartken S1 düşümleri yapıldı, birleştirme onaylandı ama
    // kaybedenin geri alması kesinleşmedi (açık miras, asıl kapsamı S1). S2 tıklaması onu ellemez;
    // S1'in sıradaki tıklaması yeniden sorar ve tamamlar.
    [Fact]
    public async Task Birlesme_kaybedeninin_geri_almasi_kalirsa_S2_ellemez_S1_tamamlar()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var c = Uye(db, "kisi.c");
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(a)] = 50m, [G(c)] = 50m };
            (await sut.OpenWhatsAppAsync(a, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(c, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Kisi(db, a, c);

            handler.ThrowTimeoutOnReverse = true;
            (await EvetAsync(sut, a, 300m, "session:s1")).Should().Be(PaymentRequestResult.BalanceUncertain);
            handler.ThrowTimeoutOnReverse = false;
            var kaybeden = db.Jobs.GetOpenLegacies(new[] { a.Id, c.Id }).Should().ContainSingle().Subject;
            kaybeden.PriorScopeKey.Should().Be("session:s1");
            var tersSayisi = handler.ReverseCalls.Count;
            var mesajlar = _launcher.LaunchedUrls.Count;

            (await sut.OpenWhatsAppAsync(a, 80m, T, "session:s2")).Should().Be(PaymentRequestResult.Opened);
            handler.ReverseCalls.Should().HaveCount(tersSayisi, "S1 kaybedeni S2'de geri alınmaz");
            Isler(db, "session:s2").Single().ApplyKey.Should().NotBe(kaybeden.ApplyKey!.Value, "S2'ye benimsenmez");
            db.Jobs.GetOpenLegacies(new[] { a.Id, c.Id }).Should().ContainSingle().Which.Id.Should().Be(kaybeden.Id);
            _launcher.LaunchedUrls.Should().HaveCount(mesajlar + 1);

            var dialogs = new FakeDialogService();
            (await EvetAsync(sut, a, 300m, "session:s1", dialogs)).Should().Be(PaymentRequestResult.Opened);
            dialogs.Confirmations.Should().ContainSingle("kaybedenin isteği müşteriye gitmişti — tamamlamadan önce yeniden sorulur");
            handler.ReverseCalls.Skip(tersSayisi).Should().Contain(kaybeden.ApplyKey!.Value);
            db.Jobs.GetOpenLegacies(new[] { a.Id, c.Id }).Should().BeEmpty();
            Isler(db, "session:s1").Should().ContainSingle().Which.ProductTotal.Should().Be(300m);
        }
        finally { Drop(db); }
    }

    // Test/arayüz incelemesi: taşıma teslim edilmiş (para taşıyan) kopya işini mirasa indirdi; asıl
    // kayıttaki aynı kapsamın işi taze (anahtarsız). Eskiden plan yalnız taze işi görüyor, miras
    // benimseniyor ve aynı mesaj müşteriye SORUSUZ ikinci kez gidiyordu. Teslim izi (DeliveredAt)
    // indirmede korunur; kapsamın tıklaması önce sorar.
    //
    // İnceleme turu 2: miras aynı sahipte ve taze işe BENİMSENİR — hiçbir şey iade edilmez, aynı istek
    // yeniden gider. Bu bir tekrar göndermedir: soru "birleştirilsin mi? önceki düşümler iade edilir"
    // değil, "HH:mm 150,00 TL istendi, tekrar gönderilsin mi?" olmalı (operatör iade olacağını sanmasın).
    [Fact]
    public async Task Tasinan_teslim_edilmis_kopya_isi_sorulmadan_yeniden_gonderilmez()
    {
        var db = FileDb();
        try
        {
            var copy = Uye(db, "kisi.d");
            var canonical = Uye(db, "Kisi.D");
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            (await sut.OpenWhatsAppAsync(copy, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            var copyKey = AppliedKey(handler.AppliedBalanceBodies.Single());
            db.Jobs.FindOrCreate(canonical.Id, "session:s1", 250m);   // asıl kayıtta taze iş (ör. önizleme düşmüştü)
            db.Sync.RekeyToLocal(copy.Id, canonical.Id, pushedThroughSeq: long.MaxValue, nowUnix: SyncNow)
                .Should().Be(RekeyResult.Rekeyed);
            var miras = db.Jobs.GetOpenLegacies(canonical.Id).Should().ContainSingle().Subject;
            miras.IsDelivered.Should().BeTrue("teslim izi indirmede kaybolmaz");

            (await sut.OpenWhatsAppAsync(canonical, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.AlreadyRequested, "istek müşteriye gitmişti — benimseme tekrar göndermedir");
            _launcher.LaunchedUrls.Should().ContainSingle("sorusuz ikinci mesaj yok");

            var dialogs = new FakeDialogService();
            (await EvetAsync(sut, canonical, 250m, "session:s1", dialogs)).Should().Be(PaymentRequestResult.Opened);
            var soru = dialogs.Confirmations.Should().ContainSingle().Subject;
            soru.Title.Should().Be("Ödeme isteği zaten gönderildi");
            soru.Message.Should().EndWith("150,00 TL istendi, tekrar gönderilsin mi?")
                .And.NotContain("iade", "hiçbir düşüm iade edilmiyor");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.ApplyKey.Should().Be(copyKey, "aynı kapsamın mirası taze işe benimsendi — ikinci düşüm yok");
            handler.AppliedBalanceBodies.Select(AppliedKey).Should().OnlyContain(k => k == copyKey);
            handler.ReverseCalls.Should().BeEmpty();
            _launcher.LaunchedUrls.Should().HaveCount(2).And.OnlyContain(u => u.Contains("150%2C00"));
        }
        finally { Drop(db); }
    }

    // ── İnceleme turu 2 ──────────────────────────────────────────────────────

    private const string InProgressJson =
        """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""";

    private static HashSet<string> Onay(PaymentRequestOutcome soru) => new(StringComparer.Ordinal) { soru.Prompt!.Token };

    // Eşzamanlılık incelemesi: iki pencerede aynı "tekrar gönderilsin mi?" sorusu (aynı belirteç). Birinin
    // "Evet"i Cloud API'den in_progress aldı (SendPending — mesaj gitmiş olabilir). Eskiden iş satırı hiç
    // değişmiyordu: öbür pencerenin "Evet"i bekleyen eski belirteçle sorusuz ikinci mesajı gönderiyordu.
    [Fact]
    public async Task SendPending_sonrasi_obur_pencerenin_bekleyen_onayi_yeniden_sorulur()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Sent);

        Task<PaymentRequestOutcome> Iste(IReadOnlySet<string>? tokens = null)
            => sut.RequestPaymentAsync(customer, _ => 250m, T, "session:s1", tokens);
        var musteriler = await Iste();
        var rapor = await Iste();
        rapor.Prompt!.Token.Should().Be(musteriler.Prompt!.Token, "iki soru aynı durumu gösteriyor");

        handler.SendResponseJson = InProgressJson;
        (await Iste(Onay(musteriler))).Result.Should().Be(PaymentRequestResult.SendPending);
        var gonderimler = handler.SentBodies.Count;

        var bayat = await Iste(Onay(rapor));

        bayat.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "öbür pencerenin gönderimi sonucu bilinmese de durumu değiştirdi");
        handler.SentBodies.Should().HaveCount(gonderimler, "bayat onayla Cloud API ikinci kez çağrılmaz");
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    // Eşzamanlılık incelemesi: mesaj gitti ama teslim kaydı yazılamadı (SQLITE_BUSY — hata yutulur). Gönderim
    // denemesi ağdan ÖNCE yazıldığı için iş yine "gitmiş olabilir" görünür: sıradaki tıklama sorar, öbür
    // pencerede bekleyen eski onay tutmaz. Eskiden iş teslim edilmemiş sayılıyordu: ikinci mesaj sorusuz.
    [Fact]
    public async Task Teslim_yazilamasa_da_gonderim_denemesi_sonraki_tiklamayi_ve_bekleyen_onayi_sordurur()
    {
        var depo = new TeslimYazimiDusenDepo(_jobs);
        var (sut, handler) = MakeCloudSut(_store, _launcher, jobs: depo);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        Task<PaymentRequestOutcome> Iste(IReadOnlySet<string>? tokens = null)
            => sut.RequestPaymentAsync(customer, _ => 250m, T, "session:s1", tokens);

        depo.TeslimDussun = true;
        (await Iste()).Result.Should().Be(PaymentRequestResult.Opened, "mesaj gitti; teslim yazımı akışı düşürmez");
        _jobs.Snapshot.Single().HasUnconfirmedSend.Should().BeTrue();

        var soru = await Iste();
        soru.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "mesaj gitmiş olabilir — sorusuz ikinci mesaj yok");
        soru.Prompt!.Requests.Should().ContainSingle().Which.Should().Be(new PriorPaymentRequest(150m, null),
            "gönderilen net 250 − 100; anı yazılamadı");
        _launcher.LaunchedUrls.Should().ContainSingle();

        // İki pencere aynı soruyu gösteriyor; birinin "Evet"i gönderdi, teslim yine yazılamadı.
        var obur = await Iste();
        obur.Prompt!.Token.Should().Be(soru.Prompt.Token);
        (await Iste(Onay(soru))).Result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(2);

        (await Iste(Onay(obur))).Result.Should().Be(PaymentRequestResult.AlreadyRequested,
            "öbür pencerenin onayı gönderimden önceki duruma verildi");
        _launcher.LaunchedUrls.Should().HaveCount(2);
        handler.AppliedBalanceBodies.Should().ContainSingle();
    }

    // wa.me açılamadıysa mesaj gitmedi: deneme geri alınır (sıradaki tıklama sorusuz dener — A2). Ama Cloud
    // API sonucu bilinmeden (zaman aşımı) wa.me'ye düşüldüyse o gönderim ulaşmış olabilir: deneme kalır.
    [Fact]
    public async Task CloudAPI_zaman_asimi_ve_wame_acilamazsa_sonraki_tiklama_sorar()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        handler.ThrowTimeoutOnSend = true;
        _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.LaunchFailed);
        _launcher.ThrowOnLaunch = null;
        handler.ThrowTimeoutOnSend = false;

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.AlreadyRequested, "Cloud API gönderimi ulaşmış olabilir");
        handler.SentBodies.Should().BeEmpty();
    }

    // Para incelemesi: onaylı birleştirmede kaybeden iade edilip kapandı, kalan işin geri alması düştü
    // (reverse_pending). Eskiden sıradaki tıklama bunu düz "tekrar gönderilsin mi?" diye soruyordu — kalan
    // işin geçersizleşmiş eski tutarını "istendi" diye göstererek; "Hayır" iadeleri yeni istek olmadan
    // bırakıyordu. Yarım kalan istek önceki sürüm gibi sorulmadan sürer.
    [Fact]
    public async Task Birlesmede_kalan_isin_geri_almasi_kalirsa_sonraki_tiklama_sormadan_surer()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, "kisi.a");
            var c = Uye(db, "kisi.c");
            var (sut, handler) = KisiSut(db);
            handler.RowBalances = new() { [G(a)] = 50m, [G(c)] = 50m };
            (await sut.OpenWhatsAppAsync(a, 100m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            (await sut.OpenWhatsAppAsync(c, 200m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);
            Kisi(db, a, c);

            // İlk geri alma (kaybeden) geçer, sonrakiler (kalan işin revizyonu, iki deneme) düşer.
            var tersler = 0;
            handler.OnReverse = _ =>
            {
                if (Interlocked.Increment(ref tersler) >= 2) throw new System.Net.Http.HttpRequestException("stub");
            };
            (await EvetAsync(sut, a, 300m, "session:s1")).Should().Be(PaymentRequestResult.BalanceUncertain);
            handler.OnReverse = null;
            var kalan = Isler(db, "session:s1").Should().ContainSingle().Subject;
            kalan.State.Should().Be(PaymentJobState.ReversePending);
            db.Jobs.GetOpenLegacies(new[] { a.Id, c.Id }).Should().BeEmpty("kaybeden iade edilip kapandı");
            var mesajlar = _launcher.LaunchedUrls.Count;

            var hayir = new FakeDialogService();
            var outcome = await PaymentResultPresenter.RequestConfirmedAsync(hayir, t =>
                sut.RequestPaymentAsync(a, _ => 300m, T, "session:s1", t));

            outcome.Result.Should().Be(PaymentRequestResult.Opened);
            hayir.Confirmations.Should().BeEmpty("birleştirme zaten onaylanmıştı; yarım kalan istek sorulmadan sürer");
            Isler(db, "session:s1").Single().ProductTotal.Should().Be(300m);
            _launcher.LaunchedUrls.Should().HaveCount(mesajlar + 1);
            _launcher.LaunchedUrls[^1].Should().Contain("250%2C00", "300 − 50: birleşik istek");
        }
        finally { Drop(db); }
    }

    /// <summary>Teslim kaydı yazımı düşen depo (SQLITE_BUSY taklidi) — geri kalanı asıl depoya geçer.</summary>
    private sealed class TeslimYazimiDusenDepo(IPaymentJobStore inner) : IPaymentJobStore
    {
        public bool TeslimDussun { get; set; }

        public void MarkDelivered(string id, decimal requestedNet)
        {
            if (TeslimDussun) throw new InvalidOperationException("database is locked");
            inner.MarkDelivered(id, requestedNet);
        }

        public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
            => inner.FindOrCreate(customerId, scopeKey, productTotal);
        public PersonJob FindOrCreateForPerson(PersonJobRequest request) => inner.FindOrCreateForPerson(request);
        public PaymentJob? Get(string id) => inner.Get(id);
        public IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId) => inner.GetOpenLegacies(customerId);
        public IReadOnlyList<PaymentJob> GetOpenLegacies(IReadOnlyCollection<string> customerIds)
            => inner.GetOpenLegacies(customerIds);
        public bool ReassignOwner(string id, string newCustomerId) => inner.ReassignOwner(id, newCustomerId);
        public bool BeginApply(string id, Guid applyKey) => inner.BeginApply(id, applyKey);
        public bool MarkApplied(string id, Guid expectedKey, int expectedRevision, decimal appliedAmount)
            => inner.MarkApplied(id, expectedKey, expectedRevision, appliedAmount);
        public bool MarkNoBalance(string id, Guid? expectedKey, int expectedRevision)
            => inner.MarkNoBalance(id, expectedKey, expectedRevision);
        public bool MarkUncertain(string id, Guid? expectedKey, int expectedRevision)
            => inner.MarkUncertain(id, expectedKey, expectedRevision);
        public void Close(string id) => inner.Close(id);
        public void BeginSend(string id) => inner.BeginSend(id);
        public void AbandonSend(string id) => inner.AbandonSend(id);
        public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
            => inner.BeginRevision(id, newProductTotal, newApplyKey, expectedRevision);
        public bool BeginReversal(string id, int expectedRevision, Guid expectedKey, decimal targetTotal)
            => inner.BeginReversal(id, expectedRevision, expectedKey, targetTotal);
        public bool CompleteReversal(string id, int expectedRevision, Guid expectedKey)
            => inner.CompleteReversal(id, expectedRevision, expectedKey);
        public void AdoptLegacyResult(string targetId, string legacyId) => inner.AdoptLegacyResult(targetId, legacyId);
        public bool AdoptRemoteResult(string id, Guid transactionId, decimal appliedAmount, decimal productTotal)
            => inner.AdoptRemoteResult(id, transactionId, appliedAmount, productTotal);
        public bool AdoptExternalReversal(string id, Guid expectedKey, int expectedRevision)
            => inner.AdoptExternalReversal(id, expectedKey, expectedRevision);
    }
}
