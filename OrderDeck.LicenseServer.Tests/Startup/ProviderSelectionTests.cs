using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Services.Configuration;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Startup;

/// <summary>
/// Sağlayıcı adı tanınmıyorsa açılış durmalı.
///
/// Email/SMS/WhatsApp seçimleri eskiden <c>else</c> ile sessizce yedeğe
/// düşüyordu; SMS ve WhatsApp'ın yedeği "log", yani hiçbir şey göndermeyen
/// sağlayıcı. <c>Sms__Provider=netgms</c> gibi tek harflik bir yapılandırma
/// hatası, ne hata ne uyarı vererek tüm SMS'i kapatırdı.
///
/// Test <see cref="ProviderName"/>'i doğrudan sınıyor, host üzerinden değil:
/// <c>WebApplicationFactory</c>'nin yapılandırma ezmeleri host <c>Build()</c>
/// anında uygulanıyor, oysa <c>Program</c> sağlayıcı adlarını kayıt sırasında
/// — yani daha önce — okuyor. Fabrika üzerinden kurulan bir test yanlış
/// yapılandırmayı hiç göremez, yeşil yanar ve hiçbir şey kanıtlamaz.
/// </summary>
public class ProviderSelectionTests
{
    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    [Theory]
    [InlineData("Email:Provider", "sendgrid", "smtp", new[] { "smtp", "disk" })]
    [InlineData("Sms:Provider", "netgms", "log", new[] { "log", "netgsm" })]   // yazım hatası
    [InlineData("OrderDeck:WhatsApp:Provider", "cloud-api", "log", new[] { "log", "cloud" })]
    [InlineData("OrderDeck:Push:Provider", "firebase", "stub", new[] { "stub", "fcm" })]
    [InlineData("OrderDeck:BroadcastMedia:Provider", "s3", "stub", new[] { "stub", "r2" })]
    public void Unknown_name_throws(string key, string value, string fallback, string[] valid)
    {
        var act = () => ProviderName.Resolve(Config(key, value), key, fallback, valid);

        act.Should().Throw<InvalidOperationException>(
                because: "tanınmayan sağlayıcı adı sessizce yedeğe düşmemeli")
           .WithMessage($"*{value}*").And.Message.Should().Contain(key);
    }

    [Theory]
    [InlineData("log", "log")]
    [InlineData("netgsm", "netgsm")]
    [InlineData("NETGSM", "netgsm")]        // büyük/küçük harf duyarsız, kanonik döner
    [InlineData("  ", "log")]               // boş değer = anahtar yok sayılır
    [InlineData(null, "log")]
    public void Known_or_missing_name_resolves(string? value, string expected)
    {
        ProviderName.Resolve(Config("Sms:Provider", value), "Sms:Provider", "log", "log", "netgsm")
            .Should().Be(expected);
    }
}
