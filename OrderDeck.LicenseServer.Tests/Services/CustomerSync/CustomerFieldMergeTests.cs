using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

public sealed class CustomerFieldMergeTests
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);
    private static readonly DateTimeOffset T3 = T1.AddHours(2);

    // Sabit telefon / şifreli metin YAZILMAZ (CLAUDE.md, repo public): üretilir.
    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    private static string NewCipher() => "CfDJ8" + Guid.NewGuid().ToString("N");

    // ── son yazan kazanır (damgalı birim) ───────────────────────────────

    [Fact]
    public void Damgali_birim_daha_yeniyse_yazar_ve_damgayi_tasir()
    {
        var p = new WpfCustomerProjection { Address = "eski", AddressChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "yeni", City = "İzmir", AddressChangedAt = T2 })
            .Should().BeTrue();
        p.Address.Should().Be("yeni");
        p.City.Should().Be("İzmir");
        p.AddressChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Damgali_birim_daha_eskiyse_dokunmaz()
    {
        var p = new WpfCustomerProjection { Address = "yeni", AddressChangedAt = T2 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "eski", AddressChangedAt = T1 })
            .Should().BeFalse();
        p.Address.Should().Be("yeni");
        p.AddressChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Esit_damga_yanki_sayilir_hicbir_sey_degismez()
    {
        // Bilgisayar kendi gönderdiği birimi akıştan geri alır ya da sunucu
        // aynı birimi ikinci kez alır: eşit damga yazım üretmez (`<=`, `<` değil).
        var p = new WpfCustomerProjection { Notes = "sunucu", NotesChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Notes = "farklı", NotesChangedAt = T1 })
            .Should().BeFalse();
        p.Notes.Should().Be("sunucu");
    }

    [Fact]
    public void Damgali_bos_deger_bilincli_silmedir()
    {
        var p = new WpfCustomerProjection { Notes = "kargo kapıya", NotesChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Notes = null, NotesChangedAt = T2 }).Should().BeTrue();
        p.Notes.Should().BeNull();
        p.NotesChangedAt.Should().Be(T2);
    }

    // ── birimler birbirinden bağımsız ───────────────────────────────────

    [Fact]
    public void Farkli_birimler_birbirini_ezmez()
    {
        var p = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "A adresi", AddressChangedAt = T1 });
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { IsBlacklisted = true, BlacklistChangedAt = T2 });
        p.Address.Should().Be("A adresi");
        p.IsBlacklisted.Should().BeTrue();
    }

    [Fact]
    public void Telefon_degisikligi_eposta_ve_TCKN_yi_silmez()
    {
        // Güncel veriyi almamış bilgisayar yalnız telefonu değiştirdi; e-posta
        // ve TCKN'si boş ama damgasız (dokunmadı) → sunucudakiler kalır.
        var cipher = NewCipher();
        var p = new WpfCustomerProjection
        {
            Phone = NewPhone(), PhoneChangedAt = T1,
            Email = "kisi@example.test", EmailChangedAt = T1,
            TcknProtected = cipher, TcknChangedAt = T1,
        };
        var yeni = NewPhone();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Phone = yeni, PhoneChangedAt = T2 });
        p.Phone.Should().Be(yeni);
        p.Email.Should().Be("kisi@example.test");
        p.TcknProtected.Should().Be(cipher);
    }

    [Fact]
    public void Alici_odemeli_isareti_adrese_dokunmaz()
    {
        // Dekont girilince otomatik açılan işaret, güncel adresi almamış bir
        // bilgisayardan gelse de adresi silmez.
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1", City = "İzmir", AddressChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { RecipientPaysActive = true, RecipientPaysChangedAt = T2 });
        p.RecipientPaysActive.Should().BeTrue();
        p.RecipientPaysChangedAt.Should().Be(T2);
        p.Address.Should().Be("Atatürk Cd. 1");
        p.City.Should().Be("İzmir");
        p.AddressChangedAt.Should().Be(T1);
    }

    // ── damgasız (geçmiş) veri ──────────────────────────────────────────

    [Fact]
    public void Damgasiz_birim_damgasiz_hedefte_yalniz_bos_alani_doldurur_ve_damgalamaz()
    {
        var p = new WpfCustomerProjection { FullName = "Gerçek Ad" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Başka Ad", Notes = "geçmiş not" });
        p.FullName.Should().Be("Gerçek Ad");
        p.Notes.Should().Be("geçmiş not");
        p.FullNameChangedAt.Should().BeNull();
        p.NotesChangedAt.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_deger_damgali_hedefin_bilincli_silmesini_geri_almaz()
    {
        // Not T2'de bilerek silindi. Geri getirilseydi damga T2'de kalıp veri
        // değişirdi; T2'yi zaten bilen bilgisayarlar bu değeri hiç almazdı.
        var p = new WpfCustomerProjection { Notes = null, NotesChangedAt = T2 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Notes = "eski not" }).Should().BeFalse();
        p.Notes.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_il_damgali_adres_blogunu_tamamlamaz()
    {
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1", City = null, AddressChangedAt = T2 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "Atatürk Cd. 1", City = "İzmir" })
            .Should().BeFalse();
        p.City.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_kara_liste_damgali_hedefin_kaldirma_kararini_geri_almaz()
    {
        var p = new WpfCustomerProjection { IsBlacklisted = false, BlacklistChangedAt = T2 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { IsBlacklisted = true, BlacklistReason = "eski" })
            .Should().BeFalse();
        p.IsBlacklisted.Should().BeFalse();
    }

    [Fact]
    public void Damgasiz_kara_liste_damgasiz_hedefte_kaybolmaz_mevcut_sebep_korunur()
    {
        var p = new WpfCustomerProjection { BlacklistReason = "eski sebep" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { IsBlacklisted = true, BlacklistReason = "yeni sebep" });
        p.IsBlacklisted.Should().BeTrue();
        p.BlacklistReason.Should().Be("eski sebep");
    }

    [Fact]
    public void Damgasiz_izin_damgasiz_hedefte_acilir()
    {
        var p = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { SmsConsent = true, WhatsAppConsent = true });
        p.SmsConsent.Should().BeTrue();
        p.WhatsAppConsent.Should().BeTrue();
    }

    [Fact]
    public void Damgasiz_izin_damgali_hedefin_reddini_geri_almaz()
    {
        var p = new WpfCustomerProjection { SmsConsent = false, SmsConsentChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { SmsConsent = true });
        p.SmsConsent.Should().BeFalse();
    }

    [Fact]
    public void Damgali_izin_kapatabilir_ve_obur_izne_dokunmaz()
    {
        var p = new WpfCustomerProjection
        {
            SmsConsent = true, SmsConsentChangedAt = T1,
            WhatsAppConsent = true, WhatsAppConsentChangedAt = T1,
        };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { SmsConsent = false, SmsConsentChangedAt = T2 });
        p.SmsConsent.Should().BeFalse();
        p.WhatsAppConsent.Should().BeTrue();
    }

    // ── adres bloğu bütünlüğü ───────────────────────────────────────────

    [Fact]
    public void Bos_hedef_adres_blogunu_butun_alir()
    {
        var p = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "Atatürk Cd. 1", City = "İzmir", District = "Bornova" })
            .Should().BeTrue();
        p.Address.Should().Be("Atatürk Cd. 1");
        p.City.Should().Be("İzmir");
        p.District.Should().Be("Bornova");
    }

    [Fact]
    public void Ayni_adreste_eksik_il_ilce_tamamlanir()
    {
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "ATATÜRK CD. 1", City = "İzmir", District = "Bornova" })
            .Should().BeTrue();
        p.Address.Should().Be("Atatürk Cd. 1");
        p.City.Should().Be("İzmir");
        p.District.Should().Be("Bornova");
    }

    [Fact]
    public void Baska_adresin_il_ilcesi_eklenmez()
    {
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "Cumhuriyet Cd. 5", City = "Ankara", District = "Çankaya" })
            .Should().BeFalse();
        p.Address.Should().Be("Atatürk Cd. 1");
        p.City.Should().BeNull();
        p.District.Should().BeNull();
    }

    // ── kopya → asıl kayıt (aynı kural) ─────────────────────────────────

    [Fact]
    public void Kopyanin_daha_yeni_damgali_degeri_asil_kayda_damgasiyla_gecer()
    {
        // İnceleme bulgusu 1a: eski FillEmpty asıl kaydı X'te bırakıp damgayı
        // T3 yapıyordu; B'nin Y@T3 gönderimi "eşit damga" diye reddediliyordu.
        var canonical = new WpfCustomerProjection { Address = "X", AddressChangedAt = T1 };
        var copy = new WpfCustomerProjection { Address = "Y", AddressChangedAt = T3 };
        CustomerFieldMerge.Apply(canonical, CustomerSyncFields.From(copy)).Should().BeTrue();
        canonical.Address.Should().Be("Y");
        canonical.AddressChangedAt.Should().Be(T3);
        // B'nin aynı birimi tekrar göndermesi yankıdır.
        CustomerFieldMerge.Apply(canonical, new CustomerSyncFields { Address = "Y", AddressChangedAt = T3 })
            .Should().BeFalse();
    }

    [Fact]
    public void Kopyanin_eski_damgali_degeri_asil_kaydi_ezmez()
    {
        var canonical = new WpfCustomerProjection { Address = "X", AddressChangedAt = T3 };
        var copy = new WpfCustomerProjection { Address = "Y", AddressChangedAt = T1 };
        CustomerFieldMerge.Apply(canonical, CustomerSyncFields.From(copy)).Should().BeFalse();
        canonical.Address.Should().Be("X");
        canonical.AddressChangedAt.Should().Be(T3);
    }

    [Fact]
    public void Sohbet_kopyasi_gercek_adi_ezemez()
    {
        // Sohbetten yeni açılmış kopya: yalnız takma ad dolu ve damgalı; boş
        // FullName damgasız (istemci yalnız dolu ya da değiştirilen alanı damgalar).
        var canonical = new WpfCustomerProjection { FullName = "Ayşe Yılmaz", FullNameChangedAt = T1 };
        var copy = new WpfCustomerProjection { DisplayName = "ayse_tt", DisplayNameChangedAt = T3 };
        CustomerFieldMerge.Apply(canonical, CustomerSyncFields.From(copy));
        canonical.FullName.Should().Be("Ayşe Yılmaz");
        canonical.DisplayName.Should().Be("ayse_tt");
    }

    [Fact]
    public void Damgasiz_bos_takma_ad_mevcut_takma_adi_silmez()
    {
        var p = new WpfCustomerProjection { DisplayName = "ayse_tt" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Ayşe" });
        p.DisplayName.Should().Be("ayse_tt");
    }

    [Fact]
    public void Bos_hedefe_damgali_kayit_tum_alanlariyla_gecer()
    {
        // From() ya da Apply()'da unutulan bir alan burada yakalanır.
        var src = Dolu(stamp: T1);
        var dst = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(dst, CustomerSyncFields.From(src)).Should().BeTrue();
        CustomerSyncFields.From(dst).Should().Be(CustomerSyncFields.From(src));
    }

    [Fact]
    public void Bos_hedefe_damgasiz_kayit_tum_alanlariyla_dolar()
    {
        var src = Dolu(stamp: null);
        var dst = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(dst, CustomerSyncFields.From(src)).Should().BeTrue();
        CustomerSyncFields.From(dst).Should().Be(CustomerSyncFields.From(src));
    }

    private static WpfCustomerProjection Dolu(DateTimeOffset? stamp) => new()
    {
        FullName = "Ad Soyad", FullNameChangedAt = stamp,
        DisplayName = "takma", DisplayNameChangedAt = stamp,
        GroupId = "g-" + Guid.NewGuid().ToString("N")[..8], GroupIdChangedAt = stamp,
        Address = "Atatürk Cd. 1", City = "İzmir", District = "Bornova", AddressChangedAt = stamp,
        RecipientPaysActive = true, RecipientPaysChangedAt = stamp,
        Phone = NewPhone(), PhoneChangedAt = stamp,
        Email = "kisi@example.test", EmailChangedAt = stamp,
        TcknProtected = NewCipher(), TcknChangedAt = stamp,
        WhatsAppConsent = true, WhatsAppConsentChangedAt = stamp,
        SmsConsent = true, SmsConsentChangedAt = stamp,
        IsBlacklisted = true, BlacklistReason = "sebep", BlacklistedAt = T1, BlacklistChangedAt = stamp,
        Notes = "not", NotesChangedAt = stamp,
    };

    // ── eski sürümün takma ad yedeği (R3-02) ────────────────────────────

    [Fact]
    public void Takma_ad_yedegi_olan_ad_gercek_adla_degisir()
    {
        // Eski sürüm gerçek ad yoksa takma adı FullName diye gönderiyordu.
        var p = new WpfCustomerProjection { FullName = "ayse_tt" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Ayşe Yılmaz", DisplayName = "ayse_tt" })
            .Should().BeTrue();
        p.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Takma_ad_olmayan_gercek_ad_damgasiz_baska_adla_degismez()
    {
        var p = new WpfCustomerProjection { FullName = "Ayşe Yılmaz", DisplayName = "ayse_tt" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Ayşe Kaya", DisplayName = "ayse_tt" });
        p.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Damgali_ad_takma_adla_ayni_olsa_da_damgasiz_adla_degismez()
    {
        // Damgalı ad bilinçli girilmiştir.
        var p = new WpfCustomerProjection { FullName = "ayse_tt", FullNameChangedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Ayşe Yılmaz", DisplayName = "ayse_tt" });
        p.FullName.Should().Be("ayse_tt");
    }

    // ── eski sürüm (format 1) ──────────────────────────────────────────

    [Fact]
    public void Eski_surum_yalniz_damgasiz_birimlere_yazar()
    {
        var tel = NewPhone();
        var p = new WpfCustomerProjection { Address = "yeni sürümden", AddressChangedAt = T1 };
        CustomerFieldMerge.ApplyLegacy(p, "Ad", tel, "eski sürümden");
        p.FullName.Should().Be("Ad");
        p.Phone.Should().Be(tel);
        p.Address.Should().Be("yeni sürümden");
    }

    [Fact]
    public void Eski_surum_takma_adla_birlesik_kaydin_gercek_adini_ezmez()
    {
        // İnceleme bulgusu 2: birleştirmeden sonra asıl kayıt B'nin gerçek
        // adını taşıyor; A'nın eski sürümü takma adı gönderiyor.
        var p = new WpfCustomerProjection { FullName = "Ayşe Yılmaz" };
        CustomerFieldMerge.ApplyLegacy(p, "ayse_tt", null, null).Should().BeFalse();
        p.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Eski_surum_bos_telefon_ve_adresle_silmez()
    {
        var tel = NewPhone();
        var p = new WpfCustomerProjection { Phone = tel, Address = "adres" };
        CustomerFieldMerge.ApplyLegacy(p, null, null, "   ").Should().BeFalse();
        p.Phone.Should().Be(tel);
        p.Address.Should().Be("adres");
    }

    [Fact]
    public void Eski_surumun_dolu_telefonu_son_gonderim_olarak_yazilir()
    {
        var p = new WpfCustomerProjection { Phone = NewPhone() };
        var yeni = NewPhone();
        CustomerFieldMerge.ApplyLegacy(p, null, yeni, null).Should().BeTrue();
        p.Phone.Should().Be(yeni);
    }

    [Fact]
    public void Eski_surum_il_ilcesi_olan_adresi_baska_adresle_degistirmez()
    {
        // Eski sürüm yalnız adres satırını bilir; il/ilçesi dolu bloğu ezse
        // etikete karışık adres basılırdı.
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1", City = "İzmir" };
        CustomerFieldMerge.ApplyLegacy(p, null, null, "Cumhuriyet Cd. 5").Should().BeFalse();
        p.Address.Should().Be("Atatürk Cd. 1");
    }

    // ── KVKK kapısı ─────────────────────────────────────────────────────

    [Fact]
    public void Silinmis_hedefe_hicbir_kural_yazmaz()
    {
        var p = new WpfCustomerProjection { PurgedAt = T1 };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "x", AddressChangedAt = T2 }).Should().BeFalse();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "x" }).Should().BeFalse();
        CustomerFieldMerge.ApplyLegacy(p, "ad", NewPhone(), "adres").Should().BeFalse();
        p.Address.Should().BeNull();
        p.Phone.Should().BeNull();
        p.FullName.Should().BeNull();
    }

    // ── metin sınırları ─────────────────────────────────────────────────

    [Fact]
    public void Yalniz_bosluktan_olusan_metin_bos_sayilir()
    {
        var p = new WpfCustomerProjection { FullName = "   " };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { FullName = "Gerçek Ad" });
        p.FullName.Should().Be("Gerçek Ad");

        var q = new WpfCustomerProjection { Notes = "not", NotesChangedAt = T1 };
        CustomerFieldMerge.Apply(q, new CustomerSyncFields { Notes = "   ", NotesChangedAt = T2 });
        q.Notes.Should().BeNull();
    }

    [Fact]
    public void Asiri_uzun_metin_kolon_sinirina_kirpilir()
    {
        var p = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Notes = new string('n', 5000), NotesChangedAt = T1 });
        p.Notes!.Length.Should().Be(CustomerFieldMerge.NotesMax);
    }

    [Fact]
    public void Kirpma_surrogate_ciftini_bolmez()
    {
        // 1999 'n' + 🌸 (2 UTF-16 birimi) = 2001, sınır 2000. Düz s[..2000]
        // çifti ortadan bölüp sonda tek vekil bırakırdı.
        var p = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Notes = new string('n', 1999) + "🌸", NotesChangedAt = T1 });
        p.Notes.Should().Be(new string('n', 1999));
        char.IsHighSurrogate(p.Notes![^1]).Should().BeFalse();
    }

    [Fact]
    public void Tckn_hicbir_yoldan_kirpilmaz()
    {
        var tckn = "CfDJ8" + new string('x', 595);
        var stamped = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(stamped, new CustomerSyncFields { TcknProtected = tckn, TcknChangedAt = T1 });
        stamped.TcknProtected!.Length.Should().Be(600);

        var filled = new WpfCustomerProjection();
        CustomerFieldMerge.Apply(filled, new CustomerSyncFields { TcknProtected = tckn });
        filled.TcknProtected!.Length.Should().Be(600);
    }

    // ── A3-düzeltme 2: kullanıcı adı takma ad sinyali, ortak parçasız adres ──

    [Fact]
    public void Birlestirmede_kullanici_adiyla_ayni_ad_takma_ad_sayilir()
    {
        // E2: sunucuda DisplayName hiç yok (eski sürüm göndermez). Asıl kaydın
        // adı eski sürümün takma ad yedeği = kullanıcı adı; kopyada gerçek ad.
        var canonical = new WpfCustomerProjection { Username = "ayse_tt", FullName = "ayse_tt" };
        var copy = new WpfCustomerProjection { Username = "AYSE_TT", FullName = "Ayşe Yılmaz" };
        CustomerFieldMerge.Apply(canonical, CustomerSyncFields.From(copy)).Should().BeTrue();
        canonical.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Takma_adla_dolan_ad_sonra_gercek_adla_degisir()
    {
        var p = new WpfCustomerProjection { Username = "ayse_tt" };
        CustomerFieldMerge.ApplyLegacy(p, "ayse_tt", null, null);
        p.FullName.Should().Be("ayse_tt");
        // Gerçek adı bilen (eski sürüm) bilgisayarın gönderimi takma adın yerine geçer.
        CustomerFieldMerge.ApplyLegacy(p, "Ayşe Yılmaz", null, null).Should().BeTrue();
        p.FullName.Should().Be("Ayşe Yılmaz");
        // Takma ad gerçek adın yerine GEÇMEZ.
        CustomerFieldMerge.ApplyLegacy(p, "ayse_tt", null, null).Should().BeFalse();
        p.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Ortak_parcasi_olmayan_adres_blogu_tamamlanmaz()
    {
        var p = new WpfCustomerProjection { Address = "Atatürk Cd. 1" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { City = "Ankara", District = "Çankaya" }).Should().BeFalse();
        p.City.Should().BeNull();
        p.District.Should().BeNull();
    }

    [Fact]
    public void Eski_surum_il_ilcesi_olan_bloga_adres_satiri_yazmaz()
    {
        var p = new WpfCustomerProjection { City = "Ankara", District = "Çankaya" };
        CustomerFieldMerge.ApplyLegacy(p, null, null, "Atatürk Cd. 1, İzmir").Should().BeFalse();
        p.Address.Should().BeNull();
    }

    [Fact]
    public void Adres_karsilastirmasi_Turkce_I_harflerini_esit_sayar()
    {
        var p = new WpfCustomerProjection { Address = "IŞIK SK. 5" };
        CustomerFieldMerge.Apply(p, new CustomerSyncFields { Address = "ışık sk. 5", City = "İstanbul" }).Should().BeTrue();
        p.Address.Should().Be("IŞIK SK. 5");
        p.City.Should().Be("İstanbul");
    }
}
