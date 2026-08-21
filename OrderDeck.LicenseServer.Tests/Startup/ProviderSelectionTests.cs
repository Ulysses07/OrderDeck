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

    // ─── Üretimde sahte sağlayıcı yasağı ────────────────────────────────────
    // Yazım hatası korumasının kapatmadığı ikinci delik: anahtar HİÇ yoksa
    // Resolve sorgusuz varsayılana döner, o da SMS/WhatsApp/push/medya için
    // hiçbir iş yapmayan sağlayıcı. Üretimde bu, sağlık kontrolü yeşil yanan
    // sessiz bir arıza — gönderimler "başarılı" döner ama hiçbir yere gitmez.

    [Theory]
    [InlineData("Sms:Provider", "log", new[] { "log", "netgsm" })]
    [InlineData("OrderDeck:WhatsApp:Provider", "log", new[] { "log", "cloud" })]
    [InlineData("OrderDeck:Push:Provider", "stub", new[] { "stub", "fcm" })]
    [InlineData("OrderDeck:BroadcastMedia:Provider", "stub", new[] { "stub", "r2" })]
    public void Fake_provider_rejected_in_production_when_key_missing(
        string key, string fake, string[] valid)
    {
        var act = () => ProviderName.ResolveLive(
            Config(key, null), isProduction: true, key, fake, valid);

        act.Should().Throw<InvalidOperationException>(
                because: "eksik yapılandırma üretimde sessizce sahte sağlayıcıya düşmemeli")
           .And.Message.Should().Contain(key);
    }

    [Theory]
    [InlineData("Sms:Provider", "log", new[] { "log", "netgsm" })]
    [InlineData("OrderDeck:BroadcastMedia:Provider", "stub", new[] { "stub", "r2" })]
    public void Fake_provider_rejected_in_production_when_written_explicitly(
        string key, string fake, string[] valid)
    {
        // Açıkça yazılmış olması da kurtarmaz: sahte sağlayıcının üretimde
        // bilinçli olarak seçilmesinin bir anlamı yok, tek yaptığı veriyi yutmak.
        var act = () => ProviderName.ResolveLive(
            Config(key, fake), isProduction: true, key, fake, valid);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Production_error_does_not_suggest_the_fake_provider()
    {
        var act = () => ProviderName.ResolveLive(
            Config("Sms:Provider", null), isProduction: true, "Sms:Provider",
            "log", "log", "netgsm");

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("netgsm", "operatöre ne yazması gerektiği söylenmeli");
        message.Should().NotContain("'log'.", "çıkmaz sokağı çözüm diye önermemeli");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("log")]
    public void Fake_provider_allowed_outside_production(string? value)
    {
        // Geliştirme ve testte doğru olan bu: kimseye gerçek SMS gitmemeli.
        ProviderName.ResolveLive(
                Config("Sms:Provider", value), isProduction: false, "Sms:Provider",
                "log", "log", "netgsm")
            .Should().Be("log");
    }

    [Theory]
    [InlineData("netgsm")]
    [InlineData("NETGSM")]  // kanonik yazıma normalize edilir, karşılaştırma bozulmaz
    public void Real_provider_passes_in_production(string value)
    {
        ProviderName.ResolveLive(
                Config("Sms:Provider", value), isProduction: true, "Sms:Provider",
                "log", "log", "netgsm")
            .Should().Be("netgsm");
    }

    [Fact]
    public void Unknown_name_still_throws_in_production_path()
    {
        // ResolveLive, Resolve'un yazım hatası korumasını kaybetmemeli.
        var act = () => ProviderName.ResolveLive(
            Config("Sms:Provider", "netgms"), isProduction: true, "Sms:Provider",
            "log", "log", "netgsm");

        act.Should().Throw<InvalidOperationException>()
           .And.Message.Should().Contain("netgms");
    }
}
