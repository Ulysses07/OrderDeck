using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

public sealed class CustomerGroupMergeTests
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);

    private static CustomerGroups Groups(
        string? fullName = null, DateTimeOffset? idAt = null,
        string? address = null, string? city = null, DateTimeOffset? addrAt = null,
        string? phone = null, string? tckn = null, bool sms = false, DateTimeOffset? contactAt = null,
        bool blacklisted = false, string? blReason = null, DateTimeOffset? blAt = null,
        string? notes = null, DateTimeOffset? notesAt = null)
        => new(
            new IdentityGroup(fullName, null, null, idAt),
            new AddressGroup(address, city, null, false, addrAt),
            new ContactGroup(phone, null, tckn, false, sms, contactAt),
            new BlacklistGroup(blacklisted, blReason, null, blAt),
            new NotesGroup(notes, notesAt));

    [Fact]
    public void Damgali_grup_daha_yeniyse_yazar()
    {
        var p = new WpfCustomerProjection { Address = "eski", AddressChangedAt = T1 };
        var changed = CustomerGroupMerge.Apply(p, Groups(address: "yeni", city: "İzmir", addrAt: T2));
        changed.Should().BeTrue();
        p.Address.Should().Be("yeni"); p.City.Should().Be("İzmir");
        p.AddressChangedAt.Should().Be(T2);
    }

    [Fact]
    public void Damgali_grup_daha_eskiyse_dokunmaz()
    {
        var p = new WpfCustomerProjection { Address = "yeni", AddressChangedAt = T2 };
        CustomerGroupMerge.Apply(p, Groups(address: "eski", addrAt: T1)).Should().BeFalse();
        p.Address.Should().Be("yeni");
    }

    [Fact]
    public void Farkli_gruplar_birbirini_ezmez()
    {
        var p = new WpfCustomerProjection();
        CustomerGroupMerge.Apply(p, Groups(address: "A adresi", addrAt: T1));
        CustomerGroupMerge.Apply(p, Groups(blacklisted: true, blAt: T2));
        p.Address.Should().Be("A adresi");
        p.IsBlacklisted.Should().BeTrue();
    }

    [Fact]
    public void Damgasiz_grup_yalniz_bos_alanlari_doldurur_ve_damgalamaz()
    {
        var p = new WpfCustomerProjection { FullName = "Gerçek Ad" };
        CustomerGroupMerge.Apply(p, Groups(fullName: null, address: "geçmiş adres"));
        p.FullName.Should().Be("Gerçek Ad");
        p.Address.Should().Be("geçmiş adres");
        p.AddressChangedAt.Should().BeNull();
    }

    [Fact]
    public void FillEmpty_dolu_alani_ezmez_damgayi_maksimuma_tasir()
    {
        var p = new WpfCustomerProjection { FullName = "Gerçek Ad", IdentityChangedAt = T1 };
        CustomerGroupMerge.FillEmpty(p, Groups(fullName: "takma", idAt: T2));
        p.FullName.Should().Be("Gerçek Ad");
        p.IdentityChangedAt.Should().Be(T2);
    }

    [Fact]
    public void FillEmpty_kara_listeyi_kaybetmez()
    {
        var p = new WpfCustomerProjection();
        CustomerGroupMerge.FillEmpty(p, Groups(blacklisted: true));
        p.IsBlacklisted.Should().BeTrue();
    }

    [Fact]
    public void Silinmis_hedefe_hicbir_kural_yazmaz()
    {
        var p = new WpfCustomerProjection { PurgedAt = T1 };
        CustomerGroupMerge.Apply(p, Groups(address: "x", addrAt: T2)).Should().BeFalse();
        CustomerGroupMerge.FillEmpty(p, Groups(address: "x")).Should().BeFalse();
        CustomerGroupMerge.ApplyLegacy(p, "ad", "tel", "adres").Should().BeFalse();
        p.Address.Should().BeNull();
    }

    [Fact]
    public void Eski_istemci_yalniz_damgalanmamis_gruplari_yazar()
    {
        // Sabit telefon YAZILMAZ (CLAUDE.md): üretilen değer kullanılır.
        var tel = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var p = new WpfCustomerProjection { Address = "yeni sürümden", AddressChangedAt = T1 };
        CustomerGroupMerge.ApplyLegacy(p, "Ad", tel, "eski sürümden");
        p.FullName.Should().Be("Ad");
        p.Phone.Should().Be(tel);
        p.Address.Should().Be("yeni sürümden");
    }

    [Fact]
    public void Asiri_uzun_metin_kolon_sinirina_kirpilir()
    {
        var p = new WpfCustomerProjection();
        CustomerGroupMerge.Apply(p, Groups(notes: new string('n', 5000), notesAt: T1));
        p.Notes!.Length.Should().Be(CustomerGroupMerge.NotesMax);
    }

    // ── (a) TCKN hiçbir yoldan kırpılmaz ────────────────────────────────

    [Fact]
    public void Tckn_damgali_yazimda_hic_kirpilmaz()
    {
        var p = new WpfCustomerProjection();
        // Şifreli metne benzer 600 karakterlik değer (gerçek Protect çıktısı
        // "CfDJ8" ile başlar — bkz. TcknProtector).
        var tckn = "CfDJ8" + new string('x', 595);
        CustomerGroupMerge.Apply(p, Groups(tckn: tckn, contactAt: T1));
        p.TcknProtected!.Length.Should().Be(600);
    }

    [Fact]
    public void Tckn_FillEmpty_ile_de_hic_kirpilmaz()
    {
        var p = new WpfCustomerProjection();
        var tckn = "CfDJ8" + new string('x', 595);
        CustomerGroupMerge.FillEmpty(p, Groups(tckn: tckn));
        p.TcknProtected!.Length.Should().Be(600);
    }

    // ── (b) Damgasız grupta izin yalnız damgasız hedefte taşınır ────────

    [Fact]
    public void Damgasiz_grup_damgasiz_hedefte_sms_iznini_acar()
    {
        var p = new WpfCustomerProjection();
        CustomerGroupMerge.Apply(p, Groups(sms: true));
        p.SmsConsent.Should().BeTrue();
    }

    [Fact]
    public void Damgasiz_grup_damgali_hedefte_sms_iznine_dokunmaz()
    {
        var p = new WpfCustomerProjection { ContactChangedAt = T1, SmsConsent = false };
        CustomerGroupMerge.Apply(p, Groups(sms: true)); // gelen grup damgasız
        p.SmsConsent.Should().BeFalse();
    }

    // ── (c) Damgalı grup izni kapatabilir (true → false, daha yeni) ─────

    [Fact]
    public void Damgali_grup_izni_kapatabilir()
    {
        var p = new WpfCustomerProjection { ContactChangedAt = T1, SmsConsent = true };
        CustomerGroupMerge.Apply(p, Groups(sms: false, contactAt: T2));
        p.SmsConsent.Should().BeFalse();
    }

    // ── (d) FillEmpty kara liste sebebini ezmez (??=) ───────────────────

    [Fact]
    public void FillEmpty_mevcut_kara_liste_sebebini_korur()
    {
        var p = new WpfCustomerProjection { BlacklistReason = "eski sebep" };
        CustomerGroupMerge.FillEmpty(p, Groups(blacklisted: true, blReason: "yeni sebep"));
        p.IsBlacklisted.Should().BeTrue();
        p.BlacklistReason.Should().Be("eski sebep");
    }

    // ── (e) Kırpma vekil (surrogate) çiftini bölmez ─────────────────────

    [Fact]
    public void Kirpma_surrogate_ciftini_bolmez()
    {
        var p = new WpfCustomerProjection();
        // 1999 'n' + 🌸 (emoji = 2 UTF-16 birimi) → toplam 2001, sınır 2000.
        // Düz s[..2000] kesimi çifti ortadan bölüp sonda tek vekil bırakırdı.
        var withEmoji = new string('n', 1999) + "🌸";
        CustomerGroupMerge.Apply(p, Groups(notes: withEmoji, notesAt: T1));
        p.Notes.Should().Be(new string('n', 1999));
        char.IsHighSurrogate(p.Notes![^1]).Should().BeFalse();
    }

    // ── (f) Apply: damgasız grup, boşluktan ibaret hedef alanı da doldurur ─

    [Fact]
    public void Apply_damgasiz_grup_bosluktan_ibaret_alani_doldurur()
    {
        var p = new WpfCustomerProjection { FullName = "   " };
        CustomerGroupMerge.Apply(p, Groups(fullName: "Gerçek Ad"));
        p.FullName.Should().Be("Gerçek Ad");
    }
}
