using System;
using FluentAssertions;
using OrderDeck.Core.Customers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// Sunucudaki CustomerFieldMergeTests'in istemci aynası + istemciye özgü takma ad
/// kuralı. Bir senaryo sunucuda X, istemcide Y sonuç verirse satır bilgisayarlar
/// arasında gidip gelir — o yüzden aynı senaryolar.
/// </summary>
public sealed class CustomerUnitMergeTests
{
    private const long T1 = 1_759_312_800_000;  // yalnız sıra önemli
    private const long T2 = T1 + 3_600_000;
    private const long T3 = T1 + 7_200_000;

    // Sabit telefon / TCKN YAZILMAZ (CLAUDE.md, repo public): üretilir.
    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
    private static string NewTckn() => Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

    // ── son yazan kazanır (damgalı birim) ───────────────────────────────

    [Fact]
    public void Damgali_birim_daha_yeniyse_yazar_ve_damgayi_tasir()
    {
        var t = new CustomerSyncState { Address = "eski", AddressChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "yeni", City = "İzmir", AddressChangedAt = T2 })
            .Should().BeTrue();
        t.Address.Should().Be("yeni");
        t.City.Should().Be("İzmir");
        t.AddressChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Damgali_birim_daha_eskiyse_dokunmaz()
    {
        var t = new CustomerSyncState { Address = "yeni", AddressChangedAt = T2 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "eski", AddressChangedAt = T1 })
            .Should().BeFalse();
        t.Address.Should().Be("yeni");
        t.AddressChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Esit_damga_yankidir_hicbir_sey_degismez()
    {
        var t = new CustomerSyncState { Notes = "yerel", NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = "farklı", NotesChangedAt = T1 })
            .Should().BeFalse();
        t.Notes.Should().Be("yerel");
    }

    // ── eşit damga: sunucudan inen satırda sunucunun değeri (C2 kalite incelemesi) ──

    [Fact]
    public void Esit_damgada_farkli_deger_sunucudan_inerken_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { GroupId = "g-yerel", GroupIdChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { GroupId = "g-sunucu", GroupIdChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.GroupId.Should().Be("g-sunucu", "sunucu eşit damgada ilk geleni tutar; istemci ona yakınsar");
        t.GroupIdChangedAt.Should().Be(T1);
    }

    [Fact]
    public void Esit_damgada_ayni_deger_sunucudan_inerken_de_yankidir()
    {
        var t = new CustomerSyncState { Notes = "aynı", NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = "aynı", NotesChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse();
    }

    [Fact]
    public void Esit_damgada_sunucunun_kirptigi_yanki_yerel_uzun_metni_kirpmaz()
    {
        var uzun = new string('n', 2500);
        var t = new CustomerSyncState { Notes = uzun, NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = uzun[..2000], NotesChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse();
        t.Notes.Should().Be(uzun);
    }

    [Fact]
    public void Esit_damgada_kirpma_sinirindaki_surrogate_cifti_yanki_sayilir()
    {
        // NotesMax = 2000. Yerel değer 1999 dolgu + 2 UTF-16 birimlik bir vekil çift (😀) = 2001
        // karakter; çift tam 1999/2000 sınırını kapsıyor. Sunucu aynı kuralla kırpsa (vekil çifti
        // bölmemek için bir karakter geriden keser) çifti bütün bütün düşürür ve yalnız dolguyu
        // gönderir — bu yanki "farklı" sayılmamalı (mirror: sunucudaki Kirpma_surrogate_ciftini_bolmez).
        var dolgu = new string('n', 1999);
        var yerelUzun = dolgu + "😀";
        var sunucununKirptigi = dolgu;
        var t = new CustomerSyncState { Notes = yerelUzun, NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = sunucununKirptigi, NotesChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse("vekil çiftin bölünmemesi için kırpma bir karakter geriden kesmeli; bu yankı farklı sayılmamalı");
        t.Notes.Should().Be(yerelUzun);
    }

    [Fact]
    public void Esit_damgada_kirpilince_bosluktan_ibaret_kalan_yerel_kendi_yankisiyla_esit_sayilir()
    {
        // M-4: yerel değerin ilk 2000 (NotesMax) karakteri boşluk, 2001. karakter dolu — değerin
        // KENDİSİ boş değil, ama kırpılan ÖNEK boşluktan ibaret. Sunucu aynı değeri yazarken de
        // bu öneği üretir; iki taraf da "boş" sonucuna gelmeli, kırpılmış önek yanlışlıkla
        // "farklı" sayılıp yerel değeri boşa yazdırmamalı.
        var yerelUzun = new string(' ', 2000) + "x";
        var sunucununKirptigi = new string(' ', 2000);
        var t = new CustomerSyncState { Notes = yerelUzun, NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = sunucununKirptigi, NotesChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse("kırpılan önek boşluktan ibaretse kendi yankısıyla eşit sayılmalı");
        t.Notes.Should().Be(yerelUzun);
    }

    [Fact]
    public void Esit_damgada_harf_farki_da_farktir_sunucunun_yazimi_alinir()
    {
        var t = new CustomerSyncState { FullName = "ayşe kaya", FullNameChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "Ayşe Kaya", FullNameChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.FullName.Should().Be("Ayşe Kaya");
    }

    [Fact]
    public void Esit_damgada_sunucudan_null_TCKN_yerel_degeri_silmez()
    {
        var tckn = NewTckn();
        var t = new CustomerSyncState { Tckn = tckn, TcknChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Tckn = null, TcknChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse("sunucu çözemeyince null gönderir, damga aynı (S13)");
        t.Tckn.Should().Be(tckn);
    }

    [Fact]
    public void Esit_damgada_sunucudan_bos_TCKN_yerel_degeri_silmez()
    {
        // M-3: boş string de null kadar "çözülemedi" anlamına gelir — eski kod yalnız `is not null`
        // kontrol ettiği için "" yerel TCKN'yi silerdi.
        var tckn = NewTckn();
        var t = new CustomerSyncState { Tckn = tckn, TcknChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Tckn = "", TcknChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeFalse("boş TCKN de çözülemedi anlamına gelir (M-3)");
        t.Tckn.Should().Be(tckn);
    }

    [Fact]
    public void Esit_damgada_TCKN_farkliysa_ve_sunucu_doluysa_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { Tckn = NewTckn(), TcknChangedAt = T1 };
        var sunucununTckni = NewTckn();
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Tckn = sunucununTckni, TcknChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.Tckn.Should().Be(sunucununTckni);
    }

    [Fact]
    public void Esit_damgada_WhatsAppConsent_farkliysa_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { WhatsAppConsent = false, WhatsAppConsentChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { WhatsAppConsent = true, WhatsAppConsentChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.WhatsAppConsent.Should().BeTrue();
    }

    [Fact]
    public void Esit_damgada_SmsConsent_farkliysa_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { SmsConsent = true, SmsConsentChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { SmsConsent = false, SmsConsentChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.SmsConsent.Should().BeFalse();
    }

    [Fact]
    public void Esit_damgada_RecipientPaysActive_farkliysa_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { RecipientPaysActive = false, RecipientPaysChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { RecipientPaysActive = true, RecipientPaysChangedAt = T1 }, incomingWinsTie: true)
            .Should().BeTrue();
        t.RecipientPaysActive.Should().BeTrue();
    }

    [Fact]
    public void Esit_damgada_adres_blogu_butun_alinir()
    {
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir", AddressChangedAt = T1 };
        CustomerUnitMerge.Apply(t,
                new CustomerSyncState { Address = "Atatürk Cd. 1", City = "Manisa", District = "Merkez", AddressChangedAt = T1 },
                incomingWinsTie: true)
            .Should().BeTrue();
        t.City.Should().Be("Manisa");
        t.District.Should().Be("Merkez");
    }

    [Fact]
    public void Esit_damgada_kara_liste_yalniz_BlacklistedAt_farkliysa_sunucununki_yazilir()
    {
        var t = new CustomerSyncState { IsBlacklisted = true, BlacklistReason = "sebep", BlacklistedAt = 100, BlacklistChangedAt = T1 };
        CustomerUnitMerge.Apply(t,
                new CustomerSyncState { IsBlacklisted = true, BlacklistReason = "sebep", BlacklistedAt = 200, BlacklistChangedAt = T1 },
                incomingWinsTie: true)
            .Should().BeTrue();
        t.BlacklistedAt.Should().Be(200);
        t.IsBlacklisted.Should().BeTrue();
        t.BlacklistReason.Should().Be("sebep");
    }

    [Fact]
    public void Damgali_bos_deger_bilincli_silmedir()
    {
        var t = new CustomerSyncState { Notes = "kargo kapıya", NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = "   ", NotesChangedAt = T2 }).Should().BeTrue();
        t.Notes.Should().BeNull("yalnız boşluktan oluşan metin boş sayılır");
        t.NotesChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Damgali_yazimda_yerel_kirpma_yok_uzun_not_tam_yazilir()
    {
        // Sunucudan BİLEREK farklı nokta (1): yerel kolonların sınırı yok, yazarken kırpmaz.
        var uzun = new string('n', 2500);
        var t = new CustomerSyncState { Notes = "kısa", NotesChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = uzun, NotesChangedAt = T2 }).Should().BeTrue();
        t.Notes.Should().Be(uzun);
        t.Notes!.Length.Should().Be(2500);
    }

    // ── birimler birbirinden bağımsız ───────────────────────────────────

    [Fact]
    public void Telefon_degisikligi_eposta_ve_TCKN_yi_silmez()
    {
        var tckn = NewTckn();
        var t = new CustomerSyncState
        {
            Phone = NewPhone(), PhoneChangedAt = T1,
            Email = "kisi@example.test", EmailChangedAt = T1,
            Tckn = tckn, TcknChangedAt = T1,
        };
        var yeni = NewPhone();
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Phone = yeni, PhoneChangedAt = T2 });
        t.Phone.Should().Be(yeni);
        t.Email.Should().Be("kisi@example.test");
        t.Tckn.Should().Be(tckn);
    }

    [Fact]
    public void Alici_odemeli_isareti_adrese_dokunmaz()
    {
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir", AddressChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { RecipientPaysActive = true, RecipientPaysChangedAt = T2 });
        t.RecipientPaysActive.Should().BeTrue();
        t.Address.Should().Be("Atatürk Cd. 1");
        t.AddressChangedAt.Should().Be(T1);
    }

    // ── damgasız (geçmiş) veri ──────────────────────────────────────────

    [Fact]
    public void Damgasiz_birim_damgasiz_hedefte_yalniz_bos_alani_doldurur_ve_damgalamaz()
    {
        var t = new CustomerSyncState { FullName = "Gerçek Ad" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "Başka Ad", Notes = "geçmiş not" });
        t.FullName.Should().Be("Gerçek Ad");
        t.Notes.Should().Be("geçmiş not");
        t.FullNameChangedAt.Should().BeNull();
        t.NotesChangedAt.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_bosluktan_olusan_gelen_deger_bos_hedefi_doldurmaz()
    {
        var t = new CustomerSyncState();
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = "   " }).Should().BeFalse();
        t.Notes.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_deger_damgali_bilincli_silmeyi_geri_almaz()
    {
        var t = new CustomerSyncState { Notes = null, NotesChangedAt = T2 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Notes = "eski not" }).Should().BeFalse();
        t.Notes.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_il_damgali_adres_blogunu_tamamlamaz()
    {
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1", AddressChangedAt = T2 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir" })
            .Should().BeFalse();
        t.City.Should().BeNull();
    }

    [Fact]
    public void Damgasiz_kara_liste_damgali_kaldirma_kararini_geri_almaz()
    {
        var t = new CustomerSyncState { IsBlacklisted = false, BlacklistChangedAt = T2 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { IsBlacklisted = true, BlacklistReason = "eski" })
            .Should().BeFalse();
        t.IsBlacklisted.Should().BeFalse();
    }

    [Fact]
    public void Damgasiz_kara_liste_kaybolmaz_mevcut_sebep_ve_tarih_korunur()
    {
        var t = new CustomerSyncState { BlacklistReason = "eski sebep", BlacklistedAt = 100 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { IsBlacklisted = true, BlacklistReason = "yeni", BlacklistedAt = 200 });
        t.IsBlacklisted.Should().BeTrue();
        t.BlacklistReason.Should().Be("eski sebep");
        t.BlacklistedAt.Should().Be(100);
    }

    [Fact]
    public void Damgasiz_bayrak_yalniz_evet_tasir()
    {
        var t = new CustomerSyncState { SmsConsent = true };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { SmsConsent = false, WhatsAppConsent = true, RecipientPaysActive = true });
        t.SmsConsent.Should().BeTrue("damgasız 'hayır' çoğunlukla 'hiç ayarlanmadı' demek");
        t.WhatsAppConsent.Should().BeTrue();
        t.RecipientPaysActive.Should().BeTrue();
    }

    [Fact]
    public void Damgasiz_izin_damgali_reddi_geri_almaz()
    {
        var t = new CustomerSyncState { SmsConsent = false, SmsConsentChangedAt = T1 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { SmsConsent = true });
        t.SmsConsent.Should().BeFalse();
    }

    // ── adres bloğu bütünlüğü ───────────────────────────────────────────

    [Fact]
    public void Bos_hedef_adres_blogunu_butun_alir()
    {
        var t = new CustomerSyncState();
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir", District = "Bornova" })
            .Should().BeTrue();
        (t.Address, t.City, t.District).Should().Be(("Atatürk Cd. 1", "İzmir", "Bornova"));
    }

    [Fact]
    public void Ayni_adreste_eksik_il_ilce_tamamlanir_buyuk_kucuk_ve_turkce_i_farki_yok()
    {
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1", City = "izmir" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "ATATÜRK CD. 1", City = "İZMİR", District = "Bornova" })
            .Should().BeTrue();
        t.Address.Should().Be("Atatürk Cd. 1");
        t.City.Should().Be("izmir");
        t.District.Should().Be("Bornova");
    }

    [Fact]
    public void Ayni_adreste_eksik_il_ilce_tamamlanir_noktasiz_i_farki_yok()
    {
        // Mirror: sunucudaki Adres_karsilastirmasi_Turkce_I_harflerini_esit_sayar — burada
        // dotless 'ı' / düz 'I' çifti ("kadıköy" / "KADIKÖY"), üstteki test İ/i çiftini sınar.
        var t = new CustomerSyncState { Address = "Bağdat Cd. 1", District = "kadıköy" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "BAĞDAT CD. 1", City = "İstanbul", District = "KADIKÖY" })
            .Should().BeTrue();
        t.Address.Should().Be("Bağdat Cd. 1");
        t.District.Should().Be("kadıköy");
        t.City.Should().Be("İstanbul");
    }

    [Fact]
    public void Ortak_dolu_parca_yoksa_tamamlanmaz()
    {
        // Hedefte yalnız satır, gelende yalnız il: aynı adres olduğu bilinemez.
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { City = "Ankara" }).Should().BeFalse();
        t.City.Should().BeNull();
    }

    [Fact]
    public void Baska_adresin_il_ilcesi_eklenmez()
    {
        var t = new CustomerSyncState { Address = "Atatürk Cd. 1" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { Address = "Cumhuriyet Cd. 5", City = "Ankara", District = "Çankaya" })
            .Should().BeFalse();
        t.City.Should().BeNull();
    }

    // ── silinmiş hedef ──────────────────────────────────────────────────

    [Fact]
    public void Silinmis_hedefe_hicbir_birim_yazilmaz()
    {
        var t = new CustomerSyncState { PurgedAt = 5000 };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "x", FullNameChangedAt = T3, Phone = NewPhone() })
            .Should().BeFalse();
        t.FullName.Should().BeNull();
        t.Phone.Should().BeNull();
    }

    // ── takma ad yedeği (istemciye özgü kural; sunucu FillOrUpgradeName) ──

    [Theory]
    [InlineData("ayse_tt", null, "ayse")]        // gelenin takma adıyla aynı
    [InlineData(null, "AYSE_TT", "ayse")]        // yerelin takma adıyla aynı (harf farkı yok sayılır)
    [InlineData(null, null, "ayse_tt")]          // kullanıcı adıyla aynı
    public void Damgasiz_ad_takma_adsa_bos_yerel_ada_doldurulmaz(
        string? incomingDisplay, string? localDisplay, string username)
    {
        var t = new CustomerSyncState { Username = username, DisplayName = localDisplay };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "ayse_tt", DisplayName = incomingDisplay });
        t.FullName.Should().BeNull("eski sürüm gerçek ad yoksa takma adı FullName diye gönderiyordu (R3-02)");
    }

    [Fact]
    public void Yerel_ad_takma_adsa_gelen_gercek_ad_yerine_gecer()
    {
        var t = new CustomerSyncState { Username = "ayse_tt", FullName = "ayse_tt" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "Ayşe Yılmaz" }).Should().BeTrue();
        t.FullName.Should().Be("Ayşe Yılmaz");
        t.FullNameChangedAt.Should().BeNull("damgasız doldurma damgasız kalır");
    }

    [Fact]
    public void Yerel_gercek_ad_damgasiz_baska_gercek_adla_ezilmez()
    {
        var t = new CustomerSyncState { Username = "ayse_tt", FullName = "Ayşe Yılmaz" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "Ayşe Kaya" }).Should().BeFalse();
        t.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void Damgali_ad_takma_ad_kuralina_takilmaz()
    {
        // Damgalı ad bir bilgisayarda BİLEREK yazılmıştır — kural yalnız damgasız veriye.
        var t = new CustomerSyncState { Username = "ayse_tt" };
        CustomerUnitMerge.Apply(t, new CustomerSyncState { FullName = "ayse_tt", FullNameChangedAt = T1 });
        t.FullName.Should().Be("ayse_tt");
    }

    // ── sunucu aynası: StampedOnly / WithoutScrubbedUnits (S15) ─────────

    [Fact]
    public void StampedOnly_damgasiz_birimleri_tasimaz()
    {
        var copy = new CustomerSyncState
        {
            Phone = NewPhone(),                                   // damgasız: beyan / geçmiş
            Notes = "kargo kapıya", NotesChangedAt = T2,           // damgalı: yayıncı kararı
            SmsConsent = true,                                   // damgasız evet
        };
        var t = new CustomerSyncState();
        CustomerUnitMerge.Apply(t, copy.StampedOnly());
        t.Phone.Should().BeNull();
        t.SmsConsent.Should().BeFalse();
        t.Notes.Should().Be("kargo kapıya");
        t.NotesChangedAt.Should().Be(T2);
    }

    [Fact]
    public void WithoutScrubbedUnits_silinmis_kopyanin_damgali_bos_kisisel_birimini_tasimaz()
    {
        var phone = NewPhone();
        var t = new CustomerSyncState { Phone = phone, PhoneChangedAt = T1 };
        // Silinmiş kopya: telefon damgalıydı, boşaltıldı (damga kaldı) — bilinçli silme DEĞİL.
        var purgedCopy = new CustomerSyncState { Phone = null, PhoneChangedAt = T3, Notes = "not", NotesChangedAt = T3, PurgedAt = 5000 };
        CustomerUnitMerge.Apply(t, purgedCopy.StampedOnly().WithoutScrubbedUnits());
        t.Phone.Should().Be(phone);
        t.Notes.Should().Be("not", "iş notu ve kara liste silmede kalır, taşınır");
    }

    [Fact]
    public void WithoutScrubbedUnits_yedi_birimi_siler_kalanini_tasir()
    {
        // Doğrudan test: Apply'ın araya girmediği, yedi birimin HEPSİNİN silindiği ve
        // GroupId/alıcı ödemeli/kara liste/notların AYNEN kaldığı tek yerde pinlenir.
        var src = Dolu(T1);
        var s = src.WithoutScrubbedUnits();

        s.FullName.Should().BeNull();
        s.FullNameChangedAt.Should().BeNull();
        s.DisplayName.Should().BeNull();
        s.DisplayNameChangedAt.Should().BeNull();
        s.Address.Should().BeNull();
        s.City.Should().BeNull();
        s.District.Should().BeNull();
        s.AddressChangedAt.Should().BeNull();
        s.Phone.Should().BeNull();
        s.PhoneChangedAt.Should().BeNull();
        s.Email.Should().BeNull();
        s.EmailChangedAt.Should().BeNull();
        s.Tckn.Should().BeNull();
        s.TcknChangedAt.Should().BeNull();
        s.WhatsAppConsent.Should().BeFalse();
        s.WhatsAppConsentChangedAt.Should().BeNull();
        s.SmsConsent.Should().BeFalse();
        s.SmsConsentChangedAt.Should().BeNull();

        s.GroupId.Should().Be(src.GroupId);
        s.GroupIdChangedAt.Should().Be(src.GroupIdChangedAt);
        s.RecipientPaysActive.Should().Be(src.RecipientPaysActive);
        s.RecipientPaysChangedAt.Should().Be(src.RecipientPaysChangedAt);
        s.IsBlacklisted.Should().Be(src.IsBlacklisted);
        s.BlacklistReason.Should().Be(src.BlacklistReason);
        s.BlacklistedAt.Should().Be(src.BlacklistedAt);
        s.BlacklistChangedAt.Should().Be(src.BlacklistChangedAt);
        s.Notes.Should().Be(src.Notes);
        s.NotesChangedAt.Should().Be(src.NotesChangedAt);
    }

    // ── Shopper beyanı (kural 7, U3a, U9) ───────────────────────────────

    [Fact]
    public void WithoutShopperClaims_beyana_esit_damgasiz_birimleri_cikarir_farkli_olanlari_tutar()
    {
        var claimPhone = NewPhone();
        var claims = new CustomerSyncState { FullName = "Ayşe Yılmaz", Phone = claimPhone, Address = "Atatürk Cd. 1" };
        var legacy = new CustomerSyncState
        {
            DisplayName = "AYŞE YILMAZ",                       // eski ingest beyan adını takma ada yazdı
            Phone = "0" + claimPhone[3..],                     // aynı numara, başka yazım
            Address = "Atatürk Cd. 1", City = "İzmir",         // yayıncı ili eklemiş → blok farklı
            Notes = "kapıda",                                  // beyan olamaz
            Email = "a@example.test",                          // beyan olamaz
        };

        var s = legacy.WithoutShopperClaims(claims);

        s.DisplayName.Should().BeNull();
        s.Phone.Should().BeNull();
        s.Address.Should().Be("Atatürk Cd. 1", "blok yayıncının eliyle değişmiş — bütün kalır");
        s.City.Should().Be("İzmir");
        s.Notes.Should().Be("kapıda");
        s.Email.Should().Be("a@example.test");
    }

    [Fact]
    public void WithoutShopperClaims_esit_adres_blogu_cikar()
    {
        var claims = new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir" };
        var s = new CustomerSyncState { Address = "Atatürk Cd. 1", City = "İzmir" }.WithoutShopperClaims(claims);
        s.Address.Should().BeNull();
        s.City.Should().BeNull();
    }

    [Fact]
    public void WithoutShopperClaims_takma_ad_beyanin_takma_adiyla_esitse_cikar()
    {
        var claims = new CustomerSyncState { DisplayName = "ayse_tt" };
        var s = new CustomerSyncState { DisplayName = "AYSE_TT" }.WithoutShopperClaims(claims);
        s.DisplayName.Should().BeNull();
    }

    [Fact]
    public void WithoutShopperClaims_damgali_birime_dokunmaz()
    {
        var phone = NewPhone();
        var s = new CustomerSyncState { Phone = phone, PhoneChangedAt = T1, DisplayName = "Ayşe", DisplayNameChangedAt = T1 }
            .WithoutShopperClaims(new CustomerSyncState { Phone = phone, FullName = "Ayşe" });

        s.Phone.Should().Be(phone, "damgalı birim yayıncının kararıdır");
        s.DisplayName.Should().Be("Ayşe");
    }

    [Fact]
    public void WithoutShopperClaims_FullName_beyan_adiyla_esit_olsa_da_kalir()
    {
        // M-1: yerel FullName hiçbir zaman Shopper beyanı sayılmaz — eski ingest beyanı
        // yalnız DisplayName'e yazdı; FullName yalnız formdan (yayıncı verisi) gelir.
        var claims = new CustomerSyncState { FullName = "Ayşe Yılmaz" };
        var s = new CustomerSyncState { FullName = "Ayşe Yılmaz" }.WithoutShopperClaims(claims);
        s.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void WithoutShopperClaims_FullName_bilinmeyen_beyanda_da_kalir()
    {
        var s = new CustomerSyncState { FullName = "Ayşe Yılmaz" }.WithoutShopperClaims(claims: null);
        s.FullName.Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void WithoutShopperClaims_beyan_bilinmezse_beyan_olabilen_damgasiz_birimlerin_hepsi_cikar()
    {
        var s = new CustomerSyncState
        {
            FullName = "Gerçek Ad", DisplayName = "takma", Address = "adres", City = "İzmir", Phone = NewPhone(),
            Notes = "not", Email = "a@example.test", IsBlacklisted = true, GroupId = "g1", SmsConsent = true,
        }.WithoutShopperClaims(claims: null);

        s.FullName.Should().Be("Gerçek Ad", "M-1: yerel FullName hiçbir zaman Shopper beyanı sayılmaz");
        s.DisplayName.Should().BeNull();
        s.Address.Should().BeNull();
        s.City.Should().BeNull();
        s.Phone.Should().BeNull();
        s.Notes.Should().Be("not");
        s.Email.Should().Be("a@example.test");
        s.IsBlacklisted.Should().BeTrue();
        s.GroupId.Should().Be("g1");
        s.SmsConsent.Should().BeTrue("S17: beyan yalnız takma ad/telefon/adres (istemcide, M-1)");
    }

    [Fact]
    public void WithoutShopperClaims_bos_ama_null_olmayan_beyan_da_bilinmiyor_sayilir()
    {
        // M-2: silinmiş/boşaltılmış geçici satırın beyan alanları hep böyle gelir (hepsi boş,
        // ama nesne null DEĞİL) — "bilinen ama hiçbiri eşleşmiyor" (hepsini koru) değil, null'la
        // AYNI ("bilinmiyor", hepsini düşür) sayılmalı; gerçek beyan asla hepsi boş olamaz.
        var s = new CustomerSyncState
        {
            DisplayName = "takma", Address = "adres", City = "İzmir", Phone = NewPhone(),
            Notes = "not", Email = "a@example.test", IsBlacklisted = true, GroupId = "g1", SmsConsent = true,
        }.WithoutShopperClaims(claims: new CustomerSyncState());

        s.DisplayName.Should().BeNull();
        s.Address.Should().BeNull();
        s.City.Should().BeNull();
        s.Phone.Should().BeNull();
        s.Notes.Should().Be("not");
        s.Email.Should().Be("a@example.test");
        s.IsBlacklisted.Should().BeTrue();
        s.GroupId.Should().Be("g1");
        s.SmsConsent.Should().BeTrue();
    }

    [Fact]
    public void Bos_hedefe_damgali_kayit_tum_alanlariyla_gecer()
    {
        // Apply'da unutulan bir birim burada yakalanır.
        var src = Dolu(T1);
        var dst = new CustomerSyncState { Username = src.Username };
        CustomerUnitMerge.Apply(dst, src).Should().BeTrue();
        dst.Should().BeEquivalentTo(src);
    }

    [Fact]
    public void Bos_hedefe_damgasiz_kayit_tum_alanlariyla_dolar()
    {
        // Damgasız (geçmiş) taraf da — fill yolu — hiçbir birimi unutmamalı.
        var src = Dolu(null);
        var dst = new CustomerSyncState { Username = src.Username };
        CustomerUnitMerge.Apply(dst, src).Should().BeTrue();
        dst.Should().BeEquivalentTo(src);
    }

    private static CustomerSyncState Dolu(long? at) => new()
    {
        Username = "ayse",
        FullName = "Ayşe Yılmaz", FullNameChangedAt = at,
        DisplayName = "ayse🌸", DisplayNameChangedAt = at,
        GroupId = Guid.NewGuid().ToString("N"), GroupIdChangedAt = at,
        Address = "Atatürk Cd. 1", City = "İzmir", District = "Bornova", AddressChangedAt = at,
        RecipientPaysActive = true, RecipientPaysChangedAt = at,
        Phone = NewPhone(), PhoneChangedAt = at,
        Email = "ayse@example.test", EmailChangedAt = at,
        Tckn = NewTckn(), TcknChangedAt = at,
        WhatsAppConsent = true, WhatsAppConsentChangedAt = at,
        SmsConsent = true, SmsConsentChangedAt = at,
        IsBlacklisted = true, BlacklistReason = "ödemedi", BlacklistedAt = 1_759_000_000, BlacklistChangedAt = at,
        Notes = "not", NotesChangedAt = at,
    };
}
