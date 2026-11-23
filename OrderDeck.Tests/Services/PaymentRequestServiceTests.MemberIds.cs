using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── Ortak bakiye W-havuz: kişinin üyeleri sunucuya (spec 2026-10-11 §6.1-a, §8.4-1) ─────────────────────
//
// Havuz kipinde sunucu çekim kümesini WPF'in bildirdiği üyelerle kurar (W ∪ {a}); W göndermeyen istemci 400
// member-ids-required alır. Önizleme (tekrarlı sorgu parametresi) ve apply (gövde alanı) kişinin GUID'li BÜTÜN
// üyelerini taşır: silinmişler (sunucu onları S'den alır, yine de gönderilir — K3) ve sahip dahil; GUID'siz satır hiç
// gitmez (sunucu bozuk Guid'i satır kipinde de 400'ler). Liste AKIŞIN KİŞİSİDİR: tıklamanın girişinde okunan, kilitleri
// ve kiraları tutulan anlık görüntü; tıklama içinde depo yeniden okunmaz — çekme turu akış ortasında çapanın GroupId'sini
// yazsa da önizleme ve apply operatörün kişisini taşır (iskelet fark 4). Belirsiz işin ve miras işin oynatması aynı
// listeyi gönderir; iş açıldıktan sonra birleşen üye sonraki tıklamada (kişi girişte yeniden okunur) gider.
// Satır kipindeki sunucu alanı okumaz: stub da okumaz, var olan testler değişmeden yeşil kalır (R04).
public partial class PaymentRequestServiceTests
{
    /// <summary>Üyeyi KVKK silinmiş yapar (<c>PurgedAt</c> dolu). Tam silme yolu akıştan gelir; burada yalnız kişi
    /// okumasının (<c>PaymentPersonSql</c>) gördüğü kolon gerekir.</summary>
    private static void Silinmis(SyncedDb db, Customer c)
    {
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET PurgedAt = 1 WHERE Id = @id", new { id = c.Id });
    }

    /// <summary>Üretilmiş kullanıcı adı (repo public: literal ad yok). Hiçbir iddia kullanıcı adına bakmaz;
    /// 34 karakter sunucunun Username sınırının (128) altında, sahip seçimini değiştirmez.</summary>
    private static string UyeAdi() => $"u-{Guid.NewGuid():N}";

    /// <summary>GUID'siz (yerel) Id'li üye — sunucuya müşteri kimliği olarak gidemez.</summary>
    private static Customer YerelUye(SyncedDb db, string username)
        => Uye(db, username, id: $"yerel-{Guid.NewGuid():N}");

    /// <summary>Üyeleri <paramref name="grupId"/> grubuna yazar — çekme turunun var olan yerel satıra GroupId yazmasının
    /// yerine (<c>CustomerSyncRepository.ApplyServerCustomer</c> o kolda kira denetimi yapmaz). Dapper: ödeme akışının
    /// kişi kilidinden ve kiralarından geçmez; akış ortası kancasında çağrılır.</summary>
    private static void GrubaAl(SyncedDb db, string grupId, params Customer[] uyeler)
    {
        using var conn = db.Factory.Open();
        Dapper.SqlMapper.Execute(conn, "UPDATE Customer SET GroupId = @grupId WHERE Id IN @ids",
            new { grupId, ids = uyeler.Select(u => u.Id).ToList() });
    }

    [Fact] // §6.1-a, K3: GUID'li bütün üyeler (silinmiş dahil) önizlemeye gider; GUID'siz satır hiç gitmez
    public async Task Onizleme_kisinin_guidli_butun_uyelerini_gonderir_silinmis_dahil_guidsiz_haric()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());
            var b = Uye(db, UyeAdi(), phone: false);
            var c = YerelUye(db, UyeAdi());
            Kisi(db, a, b, c);
            Silinmis(db, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 0m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.PreviewMemberIds.Should().ContainSingle()
                .Which.Should().Equal(new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                    "silinmiş b sunucuda S'den gelir ama yine gönderilir; yerel c'nin Id'si Guid değil, hiç gitmez");
            handler.AppliedBalanceBodies.Should().BeEmpty("önizleme 0 — anahtar hiç yazılmaz");
            Isler(db, "session:s1").Should().ContainSingle()
                .Which.State.Should().Be(PaymentJobState.NoBalance, "kesin sonuç bugünkü yoldan");
            _launcher.LaunchedUrls.Should().ContainSingle("bakiye yok: mesaj düşümsüz gider");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a: apply gövdesi aynı listeyi taşır — sahip dahil; çapa (wpfCustomerId) değişmez; scope almaz (§6.1-h)
    public async Task Apply_govdesi_kisinin_guidli_uyelerini_tasir_sahip_dahil()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen);
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "sahip b de listede; sunucu çekim kümesini W ∪ {a} kurar");
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(b), "çapa işin sahip satırı");
            handler.ScopeQueries.Should().NotBeEmpty().And.OnlyContain(q => !q.Contains("memberIds"),
                "§6.1-h: scope çağrısı memberIds almaz");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a, §8.4-1: oynatma tıklamanın kişisini taşır — kişi her tıklamanın girişinde okunur, sonradan birleşen üye sonraki tıklamada gider
    public async Task Replay_uyeleri_tiklama_girisinde_okur_sonradan_birlesen_uye_sonraki_tiklamada_dahil()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ThrowTimeoutOnApply = true;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.BalanceUncertain);
            Isler(db, "session:s1").Should().ContainSingle()
                .Which.State.Should().Be(PaymentJobState.ApplyUncertain);
            var ilkGovdeSayisi = handler.AppliedBalanceBodies.Count;
            ilkGovdeSayisi.Should().BeGreaterThan(0);
            handler.AppliedMemberIds[0].Should().Equal(
                new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                "ilk denemede kişi {a, b}");

            // İş açıldıktan SONRA gruba yeni bir GUID'li üye katılır (başka PC'nin birleştirmesi indi).
            var d = Uye(db, UyeAdi(), phone: false);
            Kisi(db, a, b, d);
            handler.ThrowTimeoutOnApply = false;

            // İkinci tıklama kişiyi girişinde yeniden okur: {a, b, d}; belirsiz işin oynatması bu kişiyi taşır.
            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.AppliedBalanceBodies.Should().HaveCountGreaterThan(ilkGovdeSayisi, "belirsiz iş oynatıldı");
            AppliedKey(handler.AppliedBalanceBodies[^1]).Should().Be(AppliedKey(handler.AppliedBalanceBodies[0]),
                "oynatma: aynı anahtar, yeni düşüm yok");
            handler.AppliedMemberIds[^1].Should().Equal(
                new[] { G(a), G(b), G(d) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal),
                "oynatan tıklamanın kişisi girişinde okundu — sonradan birleşen d dahil");
            _launcher.LaunchedUrls.Should().ContainSingle("oynatma kesinleşti, mesaj gitti");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a, §15-5 (iskelet fark 4): liste akışın kişisidir — akış ortasında çapa başka kişiye geçse de önizleme ve apply x'i taşımaz
    public async Task Akis_ortasinda_capa_baska_kisiye_gecse_de_memberIds_akisin_kisisidir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());                   // telefonlu: sahip, çapa
            var b = Uye(db, UyeAdi(), phone: false);
            var x = Uye(db, UyeAdi(), phone: false);     // başka bir kişi
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            var yeniGrup = Guid.NewGuid().ToString("N");
            // Olay sırası: kancanın ilk koşusu ve önizlemenin stub'a varışı aynı sayaçtan numara alır. OnPreviewAsync stub'ın
            // önizleme memberIds'ini kaydettiği kilidin hemen ardından, aynı istekte koşar.
            var sayac = 0;
            int? kancaSira = null, onizlemeSira = null;
            // 4. adımın kapsam sorgusu (önizlemeden ve apply'dan önceki son ağ beklemesi) sürerken çekme turu iner: a'nın
            // yerel GroupId'si x'inkiyle aynı yeni gruba yazılır (PC-B a'yı {a, b}'den ayırıp x'le birleştirdi). Kanca
            // her kapsam sorgusunda koşar (aynı yazım, eş etkili); iz yok → 204.
            handler.ScopeResponseFor = _ =>
            {
                GrubaAl(db, yeniGrup, a, x);
                kancaSira ??= Interlocked.Increment(ref sayac);
                return null;
            };
            handler.OnPreviewAsync = () =>
            {
                onizlemeSira ??= Interlocked.Increment(ref sayac);
                return Task.CompletedTask;
            };

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            kancaSira.Should().NotBeNull("kanca koştu");
            onizlemeSira.Should().NotBeNull("önizleme gitti");
            kancaSira.Should().BeLessThan(onizlemeSira!.Value,
                "çapa önizleme isteği GİTMEDEN önce başka kişiye geçti — yoksa test akış ortasını sınamazdı");
            db.Customers.GetPaymentPerson(a.Id).MemberIds.Should().BeEquivalentTo(new[] { a.Id, x.Id },
                "kanca çalıştı: depoda çapanın kişisi artık {a, x}");
            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "önizleme tıklamanın kişisini taşır; depo çağrı anında okunsaydı {a, x} giderdi, sunucu bunu S(a) ile tutarlı bulurdu");
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "apply da aynı listeyi taşır: x'in bakiyesi b'nin mesajına harcanmaz (gerçek sunucu bu ayrışmayı 409 customer-not-synced ile kapatır)");
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(a), "çapa işin sahip satırı");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-a, iskelet fark 4: revizyonun apply'ı (BeginRevision → SettleAsync → ReplayAsync; önizlemesiz) da akışın kişisini taşır — kapsam sorgusu sürerken çapa başka kişiye geçse de
    public async Task Revizyonun_apply_i_akisin_kisisini_tasir_capa_akis_ortasinda_baska_kisiye_gecse_de()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());                   // telefonlu: sahip, çapa
            var b = Uye(db, UyeAdi(), phone: false);
            var x = Uye(db, UyeAdi(), phone: false);     // başka bir kişi
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            // İlk tıklamanın önizlemesi ağ hatasıyla düşer: iş anahtarsız 'created' kalır, toplamı 250.
            handler.PreviewResponse = n => n == 1 ? throw new HttpRequestException("stub") : null;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().NotBe(PaymentRequestResult.Opened);
            var ilk = Isler(db, "session:s1").Should().ContainSingle().Subject;
            ilk.State.Should().Be(PaymentJobState.Created, "ön koşul: önizleme düştü, anahtar yazılmadı");
            ilk.ApplyKey.Should().BeNull();
            ilk.ProductTotal.Should().Be(250m);
            handler.AppliedBalanceBodies.Should().BeEmpty();

            // İkinci tıklamada tutar 300: anahtarsız created iş kapsam uzlaştırmasından (4. adım) geçer, sonra 5. adımın
            // revizyonu (geri alınacak düşüm yok) BeginRevision ile yeni anahtarı yazar ve önizlemesiz apply eder. Kapsam
            // sorgusu sürerken çekme turu iner ve a'yı x'le yeni gruba yazar — apply'dan önceki son ağ beklemesi.
            var yeniGrup = Guid.NewGuid().ToString("N");
            var sayac = 0;
            int? kancaSira = null, applySira = null;
            handler.ScopeResponseFor = _ =>
            {
                GrubaAl(db, yeniGrup, a, x);
                kancaSira ??= Interlocked.Increment(ref sayac);
                return null;
            };
            handler.OnApplyAsync = _ =>
            {
                applySira ??= Interlocked.Increment(ref sayac);
                return Task.CompletedTask;
            };

            (await sut.OpenWhatsAppAsync(b, 300m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.PreviewMemberIds.Should().ContainSingle("revizyonun apply'ı önizlemesizdir; tek önizleme ilk tıklamanınki");
            var is2 = Isler(db, "session:s1").Should().ContainSingle().Subject;
            is2.ProductTotal.Should().Be(300m);
            is2.Revision.Should().BeGreaterThan(ilk.Revision, "BeginRevision revizyonu artırdı");
            handler.AppliedBalanceBodies.Should().ContainSingle();
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(is2.ApplyKey!.Value, "apply BeginRevision'ın anahtarıyla");
            kancaSira.Should().NotBeNull("kanca koştu");
            applySira.Should().NotBeNull("revizyonun apply'ı gitti");
            kancaSira.Should().BeLessThan(applySira!.Value, "çapa revizyonun apply'ı GİTMEDEN önce başka kişiye geçti");
            db.Customers.GetPaymentPerson(a.Id).MemberIds.Should().BeEquivalentTo(new[] { a.Id, x.Id },
                "kanca çalıştı: depoda çapanın kişisi artık {a, x}");

            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(beklenen,
                "revizyonun apply'ı ikinci tıklamanın girişinde okunan kişiyi taşır (GUID üyeler + çapa, tekil, ordinal "
                + "sıralı); depo revizyon yolunda yeniden okunsaydı {a, x} giderdi");
            handler.AppliedCustomerIds.Should().ContainSingle().Which.Should().Be(G(a), "çapa işin sahip satırı");
        }
        finally { Drop(db); }
    }

    [Fact] // Grupsuz tek satır: liste yalnız o satır
    public async Task Tek_satirlik_kiside_memberIds_yalniz_cikis_satiri()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi());
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(a, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(G(a));
            handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(G(a));
        }
        finally { Drop(db); }
    }

    [Fact] // Depo yok (test/kompozisyon): kişi tek satırlık yedek (ReadPerson → PaymentPerson.Single), liste yalnız çapa — sunucu a'yı zaten W'ye ekler, havuzda member-ids-required almaz
    public async Task Depo_yoksa_tek_satirlik_yedek_kisi_memberIds_yalniz_capa()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer(TestPhone.NewE164(), id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

        var capa = Guid.ParseExact(customer.Id, "N");
        handler.PreviewMemberIds.Should().ContainSingle().Which.Should().Equal(capa);
        handler.AppliedMemberIds.Should().ContainSingle().Which.Should().Equal(capa);
    }

    [Fact] // §8.4-1: miras işin oynatması da memberIds taşır (havuz kipinde member-ids-required almaz)
    public async Task Miras_isin_oynatmasi_da_memberIds_tasir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, UyeAdi(), phone: false);
            var b = Uye(db, UyeAdi());
            Kisi(db, a, b);
            var (_, k) = MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;

            (await sut.OpenWhatsAppAsync(b, 250m, T, "session:s1")).Should().Be(PaymentRequestResult.Opened);

            var beklenen = new[] { G(a), G(b) }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
            handler.AppliedBalanceBodies.Should().HaveCount(2);
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(k);
            handler.AppliedCustomerIds[0].Should().Be(G(a), "miras iş kendi sahip satırıyla (çapa a) oynatılır");
            handler.AppliedMemberIds[0].Should().Equal(beklenen, "miras işin oynatması da kişinin üyelerini taşır");
            handler.AppliedCustomerIds[1].Should().Be(G(b));
            handler.AppliedMemberIds[1].Should().Equal(beklenen, "taze apply (çapa b): aynı kişi, aynı liste");
        }
        finally { Drop(db); }
    }
}
