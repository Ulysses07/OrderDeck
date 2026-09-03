using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.Pages.Public;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// Doğrulama ucunun İSTEMCİYLE arasındaki sözleşmesi.
///
/// İstemci (IntakeForm.cshtml) durum koduna bakıp karar veriyor: 200 değilse
/// "bakamadık" sayıp müşteriyi GEÇİRİYOR, "kanal yok" bilgisini yalnız gövdedeki
/// <c>exists:false</c>'tan okuyup ENGELLİYOR. İkisi anlamca zıt.
///
/// Bu yüzden "kanal bulunamadı" 404 ile İFADE EDİLEMEZ: uç bir gün
/// <c>NotFound()</c> dönmeye başlarsa istemci sessizce ters yöne — engelleme
/// yerine geçirmeye — döner ve hiçbir şey hata vermez. Yazılı olmayan bu
/// sözleşmeyi davranışla çiviliyoruz.
/// </summary>
public sealed class YouTubeVerifyControllerTests : IClassFixture<YouTubeIdentityFactory>
{
    private readonly YouTubeIdentityFactory _factory;
    public YouTubeVerifyControllerTests(YouTubeIdentityFactory factory) => _factory = factory;

    private sealed record VerifyBody(bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

    [Fact]
    public async Task Kanal_yoksa_404_degil_200_ve_exists_false_doner()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/public/verify/youtube?handle=hicvarolmayan");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "istemci 200 olmayan her yanıtı \"bakamadık\" sayıp müşteriyi geçiriyor; "
            + "yokluğu durum koduyla anlatmak engellemeyi sessizce kapatır");

        var body = await resp.Content.ReadFromJsonAsync<VerifyBody>();
        body.Should().NotBeNull();
        body!.Available.Should().BeTrue("baktık");
        body.Exists.Should().BeFalse("ve yok");
    }

    [Fact]
    public async Task Api_ulasilamazsa_yine_200_ama_available_false_doner()
    {
        _factory.Resolver.ForceUnavailable = true;
        try
        {
            var client = _factory.CreateClient();

            var resp = await client.GetAsync("/api/public/verify/youtube?handle=herhangi");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await resp.Content.ReadFromJsonAsync<VerifyBody>();
            body.Should().NotBeNull();
            body!.Available.Should().BeFalse();
            // Available:false iken Exists'in değeri ANLAMSIZ; istemci ona bakmıyor.
            // Buradaki iddia yalnız "bakamadık" ile "yok"un aynı gövdede
            // birbirine karışmadığı.
            body.Exists.Should().BeFalse();
        }
        finally
        {
            _factory.Resolver.ForceUnavailable = false;
        }
    }

    /// <summary>
    /// channelId doluysa kimlik yolu kazanmalı. Handle olarak sorulsaydı
    /// <c>forHandle=UCabc…</c> hiçbir kanala denk gelmez ve kanal adresini doğru
    /// yapıştıran müşteri "kanal bulunamadı" görürdü.
    /// </summary>
    [Fact]
    public async Task ChannelId_verilince_kimlik_yolu_kullanilir()
    {
        const string id = "UCverify00000000000000ab";
        _factory.Resolver.ById[id] = new YouTubeChannel(true, true, "Adres Kanalı", null, id);
        _factory.Resolver.Calls.Clear();

        var client = _factory.CreateClient();

        var resp = await client.GetAsync(
            $"/api/public/verify/youtube?handle=yoksayilmali&channelId={id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<VerifyBody>();
        body!.Exists.Should().BeTrue();
        body.ChannelId.Should().Be(id);

        _factory.Resolver.Calls.Should().Contain("id:" + id);
        _factory.Resolver.Calls.Should().NotContain("yoksayilmali",
            "channelId doluyken handle yoluna gidilmemeli");
    }
}
