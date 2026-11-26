using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Havale onayının iki ayarı (spec 2026-10-10 §2.1, §7; S2a planı Karar 9): ödeme isteğinin ömrü gün, banka
/// verisinin bayatlık eşiği dakika cinsinden. Dakika, çünkü TimeSpan bağlaması <c>=2</c>'yi 2 gün okur. Okuyanı S3a ve
/// S3c.</summary>
public sealed class BankOptionsTests
{
    [Fact]
    public void Varsayilanlar()
    {
        var o = new BankOptions();

        (o.ExpectationTtlDays, o.StaleFeedAfterMinutes, o.StaleFeedAfter).Should().Be((14, 120, TimeSpan.FromHours(2)));
    }

    /// <summary>Prod'da değer compose'un ortam değişkeninden gelir (<c>OrderDeck__Bank__…</c>; sağlayıcı <c>__</c>'yi
    /// <c>:</c>'ya çevirir), bölüm Program.cs'teki <c>OrderDeck:Bank</c>.</summary>
    [Fact]
    public void Ortam_anahtarlari_baglanir()
    {
        var o = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderDeck:Bank:ExpectationTtlDays"] = "21",
                ["OrderDeck:Bank:StaleFeedAfterMinutes"] = "30",
            })
            .Build().GetSection("OrderDeck:Bank").Get<BankOptions>()!;

        (o.ExpectationTtlDays, o.StaleFeedAfterMinutes, o.StaleFeedAfter).Should().Be((21, 30, TimeSpan.FromMinutes(30)));
    }
}
