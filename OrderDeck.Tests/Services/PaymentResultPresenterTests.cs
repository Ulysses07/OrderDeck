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
}
