using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Yönetici e-postası: <see cref="BankJobAlerts.SendAsync"/> uyarıdır, düşmesi işi düşürmez;
/// <see cref="BankJobAlerts.SendOrThrowAsync"/> teslimi şart olan e-postadır (imha özeti), düşerse çağırana çıkar.</summary>
public sealed class BankJobAlertsTests
{
    private static BankJobAlerts Alerts(TestEmailSender email, string? address)
        => new(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BankJobAlerts.AlertEmailKey] = address,
        }).Build(), NullLogger<BankJobAlerts>.Instance);

    private static string Address() => $"ops-{Guid.NewGuid():N}@example.test";

    [Fact]
    public async Task Gonderir_govde_duz_metin_ve_kacislanmis_HTML()
    {
        var email = new TestEmailSender(); var to = Address();

        await Alerts(email, to).SendOrThrowAsync("konu", "a<b", CancellationToken.None);

        var sent = email.Sent.Should().ContainSingle().Subject;
        (sent.ToEmail, sent.Subject, sent.PlainBody).Should().Be((to, "konu", "a<b"));
        sent.HtmlBody.Should().Contain("a&lt;b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Uyari_adres_bossa_sessizce_gecer_teslimi_sart_olan_firlatir(string? address)
    {
        var email = new TestEmailSender();

        await Alerts(email, address).SendAsync("konu", "govde", CancellationToken.None);
        var act = () => Alerts(email, address).SendOrThrowAsync("konu", "govde", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{BankJobAlerts.AlertEmailKey}*");
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Gonderici_duserse_uyari_yutar_teslimi_sart_olan_firlatir()
    {
        var email = new TestEmailSender { FailWith = _ => new InvalidOperationException("smtp kapalı") };

        await Alerts(email, Address()).SendAsync("konu", "govde", CancellationToken.None);
        var act = () => Alerts(email, Address()).SendOrThrowAsync("konu", "govde", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("smtp kapalı");
    }
}
