using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Doğrulayıcı üç sonuç üretir, iki değil. "Reddedildi" ile "şu an
/// ulaşamadım"ı ayırmak kritik: ikisini birleştirirsek İYS'nin geçici bir
/// arızası çalışan her yayıncının kurulumunu sessizce kapatır.
/// </summary>
public sealed class NetgsmAccountVerifierTests
{
    /// <summary>Kimlik-benzeri sabit metin YASAK (depo public, GitGuardian PR
    /// check'i sabit bir fixture ile gerçek bir sırrı ayırt edemiyor).</summary>
    private static IysAccountContext NewAccount() => new(
        LicenseId: Guid.NewGuid(),
        UserCode: Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        Password: $"pw-{Guid.NewGuid():N}",
        BrandCode: Random.Shared.Next(100_000, 999_999).ToString());

    private static NetgsmAccountVerifier Verifier(IIysClient client)
        => new(client, NullLogger<NetgsmAccountVerifier>.Instance);

    [Fact]
    public async Task Kod_sifir_donerse_Ok()
    {
        var client = new StubIysClient(_ => new IysSearchResult(
            "0", "{\"code\":\"0\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Ok);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Sorgu_sabit_prob_numarasiyla_yapilir()
    {
        // Doğrulama gerçek bir kişinin numarasını KULLANMAMALI: /iys/search
        // salt-okunur olsa da yayıncının müşteri listesinden rastgele bir
        // numara seçmek, doğrulama günlüklerine ilgisiz bir kişiyi düşürür.
        List<string>? seen = null;
        var client = new StubIysClient(r =>
        {
            seen = r.ToList();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });

        await Verifier(client).VerifyAsync(NewAccount());

        seen.Should().ContainSingle().Which.Should().Be(NetgsmAccountVerifier.ProbeRecipient);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("60")]
    public async Task Yapilandirma_hatasi_Rejected(string code)
    {
        var client = new StubIysClient(_ => throw new IysConfigurationException(
            code, $"İYS yapılandırma hatası (code={code})"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Rejected);
        // Kodu mesajda ARA: yalnız "boş değil" demek, iki dalın metnini
        // takas eden ya da switch'i tek mesaja indiren bir mutasyonu
        // yakalamaz — oysa 30 ile 60 yayıncıya BAŞKA bir iş söylüyor
        // (birinde kimlik, diğerinde marka kodu düzeltilecek).
        result.Message.Should().Contain(code,
            "yayıncı panelde ne düzelteceğini okuyabilmeli");
    }

    [Fact]
    public async Task Red_mesaji_ham_IYS_govdesini_tasimaz()
    {
        // Ham gövde İYS header'ında API ŞİFRESİNİ taşıyor. LastError panele
        // dönüyor ve DB'de duruyor — oraya ham gövde sızarsa şifre, şifrelenmiş
        // sütunun yanındaki düz metin bir sütuna kopyalanmış olur.
        var secret = $"pw-{Guid.NewGuid():N}";
        var client = new StubIysClient(_ => throw new IysConfigurationException(
            "30", $"ham gövde: {{\"password\":\"{secret}\"}}"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Message.Should().NotContain(secret);
    }

    [Fact]
    public async Task Beklenmeyen_yanitta_ham_govde_mesaja_girmez()
    {
        // `ReadCode`, gövdede `code` alanı bulamazsa BÜTÜN gövdeyi kod diye
        // döndürüyor (NetgsmIysClient.cs:147-159) ve bunu KIRPILMAMIŞ gövde
        // üzerinde yapıyor (`:133`) — 2000 karakterlik sınır yalnız `RawBody`
        // için (`:144`), yani `Code` sınırsız uzunlukta olabilir.
        // Ağ geçidi HTML hata sayfası verdiğinde `result.Code` işte budur.
        // O metin mesaja girerse LastError'ın 500 karakterlik sütununu taşırır
        // ve ham sağlayıcı yanıtı yayıncının paneline düşer.
        var html = "<html><body>" + new string('x', 1500) + "</body></html>";
        var client = new StubIysClient(_ => new IysSearchResult(
            html, html, new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().NotContain("<html>");
        result.Message!.Length.Should().BeLessThan(200,
            "LastError sütunu 500 karakter; mesaj ham gövdeyle şişmemeli");
    }

    [Fact]
    public async Task Bilinen_kisa_kod_mesajda_gosterilir()
    {
        // Sanitize gereğinden fazla bastırmamalı: kısa rakamsal kod, yayıncının
        // Netgsm'e danışırken söyleyeceği tek somut bilgi.
        var client = new StubIysClient(_ => new IysSearchResult(
            "70", "{\"code\":\"70\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().Contain("70");
    }

    private sealed class StubIysClient : IIysClient
    {
        private readonly Func<IReadOnlyList<string>, IysSearchResult> _search;
        public StubIysClient(Func<IReadOnlyList<string>, IysSearchResult> search) => _search = search;

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Doğrulama yalnız search kullanır.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(_search(recipients));
    }
}
