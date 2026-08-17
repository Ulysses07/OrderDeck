using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Integration;

/// <summary>
/// Oturum ve CSRF çerezlerinin Secure bayrağı üretimde koşulsuz olmalı.
///
/// Eskiden bu bayrak <c>SameAsRequest</c> ile Request.Scheme'e bağlıydı; ters
/// vekil arkasında scheme'in doğru olması da UseForwardedHeaders'ın varlığına
/// bağlıydı. İki dolaylı bağ üst üste gelince çerez üretimden Secure'suz
/// çıkıyordu ve bunu kimse fark etmiyordu. Bayrağın ortama göre nasıl seçildiği
/// artık burada sabit.
/// </summary>
public sealed class CookieSecurePolicyTests
{
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Uretimde_Secure_kosulsuz()
    {
        Program.AdminCookieSecurePolicy(new Env { EnvironmentName = "Production" })
            .Should().Be(CookieSecurePolicy.Always);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void Uretim_disinda_istege_gore(string environment)
    {
        // Always olsaydı düz HTTP konuşan yerel ve test ortamlarında çerez hiç
        // yazılmaz, admin girişi sessizce sonsuz yönlendirmeye düşerdi.
        Program.AdminCookieSecurePolicy(new Env { EnvironmentName = environment })
            .Should().Be(CookieSecurePolicy.SameAsRequest);
    }
}
