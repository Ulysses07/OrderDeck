using System;
using System.Globalization;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>Sahip kararlarındaki soruların metni (tekrar gönderme, birleştirme) — operatörün karar
/// verdiği tek bilgi bu metin; yanlış bir şey söylemesi yanlış karara yol açar.</summary>
public sealed class PaymentResultPresenterTests
{
    private static readonly DateTimeOffset Simdi = new(2026, 10, 11, 21, 45, 0, TimeSpan.FromHours(3));

    private static PriorPaymentRequest Istek(decimal amount, int minutesAgo = 30)
        => new(amount, Simdi.AddMinutes(-minutesAgo));

    [Fact] // Tutar uygulamanın öbür tutar metinleriyle aynı biçimde: iki kuruş hanesi + TL
    public void Tekrar_gonderme_son_istegi_iki_kurus_hanesiyle_gosterir()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, "session:s1",
            new[] { Istek(1149.5m, minutesAgo: 90), Istek(149.5m) }, "t");

        var (title, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        title.Should().Be("Ödeme isteği zaten gönderildi");
        var saat = Simdi.AddMinutes(-30).ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
        message.Should().Be($"{saat} 149,50 TL istendi, tekrar gönderilsin mi?",
            "SON gönderim sorulur; '149,5 ₺' değil");
    }

    // K5 (2026-10-12): tutar değiştiyse (fiyat düzeltmesi) soru eski ve yeni tutarı BİRLİKTE gösterir —
    // "tekrar gönder" aynı isteği değil düzeltilmiş isteği gönderir.
    [Fact]
    public void Tekrar_gonderme_tutar_degistiyse_eski_ve_yeni_tutari_birlikte_gosterir()
    {
        var at = new DateTimeOffset(2026, 10, 11, 21, 14, 0, TimeSpan.FromHours(3));
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, "session:s1",
            new[] { new PriorPaymentRequest(250m, at) }, "t", NewTotal: 205m);

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        var saat = at.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
        message.Should().Be(
            $"{saat}{PaymentResultPresenter.Locative(at)} 250,00 TL istendi; yeni tutar 205,00 TL. Düzeltilmiş istek gönderilsin mi?");
    }

    [Fact]
    public void Tekrar_gonderme_bakiye_dusulmus_istekte_yeni_toplamin_bakiye_oncesi_oldugunu_soyler()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, "session:s1",
            new[] { Istek(200m) }, "t", NewTotal: 205m, PriorHadBalance: true);

        PaymentResultPresenter.PromptText(prompt, Simdi).Message
            .Should().Contain("200,00 TL istendi; yeni toplam 205,00 TL (bakiye düşülmeden önce)");
    }

    [Theory]
    [InlineData(21, 14, "'te")]
    [InlineData(21, 30, "'da")]
    [InlineData(21, 0, "'de")]
    [InlineData(20, 40, "'ta")]
    [InlineData(10, 0, "'da")]
    [InlineData(9, 6, "'da")]
    public void Saat_eki_okunusa_uyar(int hour, int minute, string expected)
        => PaymentResultPresenter.Locative(new DateTimeOffset(2026, 10, 11, hour, minute, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 11))))
            .Should().Be(expected);

    [Fact] // Yalnız teslim edilmiş istekler "gitmiş" sayılır; gönderilmemiş düşüm ayrıca söylenir
    public void Birlestirme_gonderilmemis_dusumu_gitmis_saymaz()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(150m) }, "t", UnsentDeductions: 1);

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().Be(
            "Bu kişiye bu yayın için 1 istek gitmiş (150,00 TL); ayrıca 1 gönderilmemiş bakiye düşümü var; " +
            "tek istekte birleştirilsin mi? Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir.");
    }

    [Fact] // Hiç istek gitmemişse "0 istek gitmiş" denmez
    public void Birlestirme_hic_istek_gitmemisse_yalniz_dusumleri_soyler()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "cumulative",
            Array.Empty<PriorPaymentRequest>(), "t", UnsentDeductions: 2);

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().StartWith("Bu kişinin genel toplam için 2 gönderilmemiş bakiye düşümü var; tek istekte birleştirilsin mi?")
            .And.NotContain("istek gitmiş");
    }

    [Fact] // Para incelemesi: başka satırdan yapılmış düşümün iadesi o satırda kalır — "fazladan" denmez
    public void Birlestirme_baska_hesabin_dusumunu_iade_ediyorsa_soyler()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(50m, 60), Istek(150m) }, "t", RefundsToOtherRow: true);

        var (title, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        title.Should().Be("Birden çok ödeme isteği");
        message.Should().StartWith("Bu kişiye bu yayın için 2 istek gitmiş (50,00 TL, 150,00 TL); tek istekte birleştirilsin mi?")
            .And.EndWith("Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; " +
                         "iade o hesaba döner, bu istekte kullanılmaz.")
            .And.NotContain("Fazladan");
    }

    [Fact] // İnceleme turu 2: iade edilecek düşüm yoksa (parasız istekler birleşiyor) iade cümlesi kurulmaz
    public void Birlestirme_iade_yoksa_iade_edilir_demez()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(250m, 60), Istek(300m) }, "t", RefundsDeductions: false);

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().Be("Bu kişiye bu yayın için 2 istek gitmiş (250,00 TL, 300,00 TL); " +
                            "tek istekte birleştirilsin mi? Kişinin toplamı tek istekte yeniden istenir.");
    }

    // İnceleme turu 2: sunucunun bulduğu ayrı düşümler (kişi düzeyinde R4-03) "gitmiş istek" değildir —
    // sunucu teslimi bilmez; biri eski sürüm bilgisayarda gönderilememiş bir düşüm olabilir.
    [Fact]
    public void Uzak_dusumler_gitmis_istek_diye_anlatilmaz()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            Array.Empty<PriorPaymentRequest>(), "t", RefundsToOtherRow: true,
            RemoteDeductions: new[] { 40m, 210m });

        var (title, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        title.Should().Be("Birden çok ödeme isteği");
        message.Should().StartWith(
                "Sunucuda bu kişinin bu yayın için 2 ayrı bakiye düşümü bulundu (40,00 TL, 210,00 TL); " +
                "müşteriye gidip gitmedikleri bilinmiyor. Tek istekte birleştirilsin mi? " +
                "En eski düşüm kalır, ötekiler iade edilir")
            .And.EndWith("iade o hesaba döner, bu istekte kullanılmaz.")
            .And.NotContain("istek gitmiş");
    }

    // Son inceleme: sonucu yazılamamış gönderim olgu diye anlatılmaz. Operatör az önce "WhatsApp açılamadı"
    // görmüş olabilir; "zaten gönderildi / 150 TL istendi" derse "Hayır"la müşteriye hiç istek gitmeyebilir.
    // Tutar O denemenin tutarıdır (an verilmezse saat yazılmaz; verilirse denemenin anıdır).
    [Fact]
    public void Tekrar_gonderme_sonucu_yazilamamis_istegi_gonderilmis_olabilir_diye_sorar()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, "session:s1",
            new[] { Istek(90m, 120), new PriorPaymentRequest(150m, null, Unconfirmed: true) }, "t");

        var (title, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        title.Should().Be("Ödeme isteği gönderilmiş olabilir");
        message.Should().Be(
            "150,00 TL tutarındaki istek gönderilmiş olabilir (sonucu kaydedilemedi), tekrar gönderilsin mi?");
        message.Should().NotContain("istendi");
    }

    [Fact] // Birleştirmede de sonucu yazılamamış istek "gitmiş" diye kesin sayılmaz
    public void Birlestirme_sonucu_yazilamamis_istegi_gitmis_olabilir_diye_isaretler()
    {
        var prompt = new PaymentPrompt(PersonScopePromptKind.Merge, "session:s1",
            new[] { Istek(50m, 60), new PriorPaymentRequest(150m, null, Unconfirmed: true) }, "t");

        var (_, message) = PaymentResultPresenter.PromptText(prompt, Simdi);

        message.Should().StartWith(
            "Bu kişiye bu yayın için 2 istek gitmiş (50,00 TL, 150,00 TL (gitmiş olabilir)); tek istekte birleştirilsin mi?");
    }
}
