using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using OrderDeck.LicenseServer.Services.Instagram;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Instagram;

public sealed class IntakeIgTokenServiceTests
{
    private static IntakeIgTokenService NewService()
        => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Uretilen_token_geri_okunur()
    {
        var svc = NewService();
        var token = svc.Create("royalmezat", "musa.sevinc");

        token.Should().NotContain("royalmezat", "payload şifreli olmalı, düz metin sızmamalı");
        Uri.EscapeDataString(token).Should().Be(token, "token URL-güvenli olmalı — kaçış gerektirmemeli");

        var payload = svc.TryRead(token);
        payload.Should().Be(("royalmezat", "musa.sevinc"));
    }

    [Fact]
    public void Bozuk_token_null_doner()
    {
        NewService().TryRead("bozuk-token").Should().BeNull();
    }

    [Fact]
    public void Baska_anahtarin_tokeni_null_doner()
    {
        var token = NewService().Create("royalmezat", "musa");
        NewService().TryRead(token).Should().BeNull("EphemeralDataProtectionProvider her seferinde ayrı anahtar üretir");
    }

    [Fact]
    public void Gecersiz_base64_tokeni_null_doner()
    {
        // Sorgu dizisinden gelen token güvenilmez girdi — bozuk base64url
        // FormatException'a dönüşür, 500 değil null beklenir.
        NewService().TryRead("!!!").Should().BeNull();
    }
}
