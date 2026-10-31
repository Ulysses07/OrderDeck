using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <c>PersonBalance</c> ayarları — spec 2026-10-11 §7.2, M1, §8.3. Alt sınır (Wh) koda gömülmez, yapılandırmadan
/// okunur ve satış senkronunun <see cref="ClientVersion"/> yardımcısıyla ayrıştırılır (yeni ayrıştırıcı yok, §15-3);
/// boş ya da bozuk değer tanımsızdır (kapalı-hata). Etkin PC penceresi varsayılan 14 gün; 0 ya da eksiyle sunucu açılmaz.
/// </summary>
public sealed class PersonBalanceOptionsTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;

    public PersonBalanceOptionsTests(PersonBalanceConfiguredApiFactory factory) => _factory = factory;

    /// <summary>§7.2-1: pencere varsayılanı 14 gün; alt sınır verilmemişse tanımsız.</summary>
    [Fact]
    public void Varsayilan_pencere_14_gun_alt_sinir_tanimsiz()
    {
        var options = new PersonBalanceOptions();

        options.ActivePcWindowDays.Should().Be(14);
        options.ActivePcWindow.Should().Be(TimeSpan.FromDays(14));
        options.ParseMinClientVersion().Should().BeNull("alt sınır yazılmadan havuz kipi kapalı-hata çalışır");
    }

    /// <summary>§7.2: "+" sonrası atılır, "-" sonrası ön sürüm eki; boş, boşluk ve ayrıştırılamayan değer tanımsız.</summary>
    [Theory]
    [InlineData("0.9.11", "0.9.11.0", null)]
    [InlineData("0.9.11+abc", "0.9.11.0", null)]
    [InlineData("0.9.11-rc1", "0.9.11.0", "rc1")]
    [InlineData("", null, null)]
    [InlineData("  ", null, null)]
    [InlineData("abc", null, null)]
    public void Alt_sinir_ClientVersion_ile_ayristirilir(string raw, string? number, string? preRelease)
    {
        var parsed = new PersonBalanceOptions { MinClientVersion = raw }.ParseMinClientVersion();

        if (number is null)
        {
            parsed.Should().BeNull("boş ya da ayrıştırılamayan alt sınır tanımsızdır (kapalı-hata, §7.2)");
            return;
        }
        parsed.Should().NotBeNull();
        parsed!.Number.Should().Be(Version.Parse(number));
        parsed.PreRelease.Should().Be(preRelease);
    }

    /// <summary>§8.3: karşılaştırma metin değil <see cref="System.Version"/> — "0.9.10", "0.9.9" alt sınırını geçer.</summary>
    [Fact]
    public void Alt_sinir_System_Version_ile_karsilastirilir()
    {
        var min = new PersonBalanceOptions { MinClientVersion = "0.9.9" }.ParseMinClientVersion();

        ClientVersion.IsAtLeast(ClientVersion.Parse("0.9.10"), min)
            .Should().BeTrue("metin sırası 0.9.10'u 0.9.9'dan küçük bulurdu");
        ClientVersion.IsAtLeast(ClientVersion.Parse("0.9.8"), min).Should().BeFalse();
    }

    /// <summary>§7.2: alt sınır <c>PersonBalance</c> bölümünden bağlanır; pencere compose'da yok, kod varsayılanı.</summary>
    [Fact]
    public void Yapilandirmadan_baglanir()
    {
        var options = _factory.Services.GetRequiredService<IOptions<PersonBalanceOptions>>().Value;

        options.MinClientVersion.Should().Be(PersonBalanceConfiguredApiFactory.MinClientVersion);
        options.ActivePcWindowDays.Should().Be(PersonBalanceOptions.DefaultActivePcWindowDays,
            "compose pencereyi vermez; prod'da kod varsayılanı geçerli");
    }

    /// <summary>0 ya da eksi pencere hiçbir PC'yi etkin saymaz ve <c>enable</c>'ın 1. koşulunu boşuna geçirirdi
    /// (§7.2-1): sunucu açılmaz.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Sifir_pencere_acilisi_durdurur(string days)
    {
        using var factory = new WindowApiFactory(days);

        // İstemci oluşturmak host'u kurar ve başlatır.
        var act = () => factory.CreateClient();

        var failure = act.Should().Throw<Exception>().Which;
        FindOptionsValidationException(failure)
            .Should().NotBeNull("pencere ValidateOnStart ile bağlı olmalı")
            .And.Subject.As<OptionsValidationException>()
            .Failures.Should().ContainMatch("*ActivePcWindowDays*");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary><see cref="ApiFactory"/> + <c>PersonBalance:ActivePcWindowDays</c>.</summary>
    public sealed class WindowApiFactory(string days) : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:ActivePcWindowDays"] = days,
        };
    }

    /// <summary>Doğrulama hatası doğrudan ya da <c>AggregateException</c> içinde gelebilir
    /// (<c>ForceEndSilenceOptionsTests</c> deseni).</summary>
    private static OptionsValidationException? FindOptionsValidationException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OptionsValidationException ove) return ove;
            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    var found = FindOptionsValidationException(inner);
                    if (found is not null) return found;
                }
                return null;
            }
            ex = ex.InnerException;
        }
        return null;
    }
}
