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
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "0", "{\"code\":\"0\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Ok);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Sorgu_verilen_hesap_baglamiyla_yapilir()
    {
        // Çok kiracıda en pahalı hata sınıfı: YANLIŞ markaya sormak. İstemciyi
        // `account` yerine boş/başka bir bağlamla çağıran bir mutasyon, alıcı da
        // sonuç da doğru kaldığı için diğer TÜM testleri geçerdi. `IIysClient`
        // bunu kendi doc'unda en kritik hata diye tanımlıyor; kardeş test
        // `NetgsmIysClientTests.Istek_basliginin_UCU_de_hesap_baglamindan_gelir`
        // bir alt katmanda aynı şeyi kilitliyor.
        IysAccountContext? seen = null;
        var client = new StubIysClient((a, _) =>
        {
            seen = a;
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });
        var account = NewAccount();

        await Verifier(client).VerifyAsync(account);

        seen.Should().BeSameAs(account,
            "istemciye VerifyAsync'e verilen hesabın ta kendisi geçmeli");
    }

    [Fact]
    public async Task Sorgu_sabit_prob_numarasiyla_yapilir()
    {
        // Doğrulama gerçek bir kişinin numarasını KULLANMAMALI: /iys/search
        // salt-okunur olsa da yayıncının müşteri listesinden rastgele bir
        // numara seçmek, doğrulama günlüklerine ilgisiz bir kişiyi düşürür.
        List<string>? seen = null;
        var client = new StubIysClient((_, r) =>
        {
            seen = r.ToList();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });

        await Verifier(client).VerifyAsync(NewAccount());

        seen.Should().ContainSingle().Which.Should().Be(NetgsmAccountVerifier.ProbeRecipient);
    }

    [Theory]
    [InlineData("30", "API şifresi", "marka kodu")]
    [InlineData("60", "marka kodu", "API şifresi")]
    public async Task Yapilandirma_hatasi_Rejected(string code, string beklenenIs, string digerIs)
    {
        var client = new StubIysClient((_, _) => throw new IysConfigurationException(
            code, $"İYS yapılandırma hatası (code={code})"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Rejected);
        // Korunan şey kodun kendisi değil, yayıncıya verilen FARKLI iş
        // talimatı: 60'ta İYS panelinden marka kodu, 30'da abone no / API
        // şifresi düzeltilecek. Bu yüzden ipucunun VARLIĞI kadar diğerinin
        // YOKLUĞU da iddia ediliyor — `switch` silinip yalnız `_` dalı kalsa
        // mesaj hem "30"/"60"yı hem de her iki ipucunu birden içerirdi
        // ("Abone numarası, API şifresi ve marka kodunu kontrol edin"),
        // yani yalnız pozitif iddia o mutasyonu yakalamazdı.
        result.Message.Should().Contain(code,
                "yayıncı panelde ne düzelteceğini okuyabilmeli")
            .And.Contain(beklenenIs).And.NotContain(digerIs);
    }

    [Fact]
    public async Task Red_mesaji_ham_IYS_govdesini_tasimaz()
    {
        // Ham sağlayıcı yanıtı LastError'a, oradan da panele dönüyor: yayıncıya
        // gösterilecek bir metin değil. İstisna mesajı bugün gövdeyi taşımıyor
        // ama taşımaya başlarsa mesaj sabit kalmalı — gerekçenin tamamı için
        // `NetgsmVerifyResult.Message` doc'u. Sızıntıyı ölçmek için şifre
        // şeklinde bir belirteç kullanıyoruz.
        var secret = $"pw-{Guid.NewGuid():N}";
        var client = new StubIysClient((_, _) => throw new IysConfigurationException(
            "30", $"ham gövde: {{\"password\":\"{secret}\"}}"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Message.Should().NotContain(secret);
    }

    [Fact]
    public async Task Beklenmeyen_yanitta_ham_govde_mesaja_girmez()
    {
        // `result.Code` sınırsız uzunlukta olabilir (mekanizma:
        // `NetgsmAccountVerifier.Sanitize` doc'u). Ağ geçidi HTML hata sayfası
        // verdiğinde `result.Code` işte budur; o metin mesaja girerse
        // LastError'ın 500 karakterlik sütununu taşırır ve ham sağlayıcı yanıtı
        // yayıncının paneline düşer.
        var html = "<html><body>" + new string('x', 1500) + "</body></html>";
        var client = new StubIysClient((_, _) => new IysSearchResult(
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
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "70", "{\"code\":\"70\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().Contain("70");
    }

    [Fact]
    public async Task Esik_ustu_rakamsal_kod_bastirilir()
    {
        // Eşiğin var olma sebebi: kısa rakamsal kod yayıncının Netgsm'e
        // danışırken söyleyeceği tek somut bilgi; ondan uzun olan her şey ham
        // gövdedir (`Code` bütün yanıt gövdesi olabiliyor). 2 haneli kod ile
        // 1500 karakterlik HTML arasındaki mesafe o kadar geniş ki eşiği
        // 8'den 80'e çeken bir mutasyon ikisinde de hayatta kalır — sınırın
        // hemen üstünde, 9 haneli bir örnek şart.
        var code = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();
        var client = new StubIysClient((_, _) => new IysSearchResult(
            code, $"{{\"code\":\"{code}\"}}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().Contain("tanınmayan yanıt").And.NotContain(code);
    }

    [Fact]
    public async Task Ag_hatasi_Unavailable()
    {
        var client = new StubIysClient((_, _) => throw new HttpRequestException("bağlantı yok"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable,
            "ağ arızası hesap hakkında HİÇBİR ŞEY söylemez; Rejected desek "
            + "İYS'nin yarım saatlik kesintisi çalışan her yayıncıyı kapatırdı");
    }

    [Fact]
    public async Task Zaman_asimi_Unavailable()
    {
        var client = new StubIysClient((_, _) => throw new TaskCanceledException("timeout"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
    }

    [Fact]
    public async Task Sistem_hatasi_kodu_Unavailable()
    {
        // İYS "100 = sistem hatası" gibi kodlar da döndürüyor. Bunlar
        // yapılandırmayla ilgili DEĞİL; NetgsmIysClient yalnız 30/60'ı
        // IysConfigurationException'a çeviriyor, gerisi buraya düz kod olarak
        // geliyor ve hesabı düşürmemeli.
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "100", "{\"code\":100}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
    }

    [Fact]
    public async Task Iptal_istegi_yutulmaz()
    {
        // Uygulama kapanırken CancellationToken tetiklenir. Bunu Unavailable'a
        // çevirip yutarsak, kapanış turunda her hesaba "ulaşılamadı" yazar ve
        // LastError'lar gerçek bir sorun varmış gibi görünür.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new StubIysClient((_, _) => throw new TaskCanceledException("shutdown"));

        var act = async () => await Verifier(client).VerifyAsync(NewAccount(), cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    private sealed class StubIysClient : IIysClient
    {
        // Delegate `account`'u DA taşıyor: atarsak, doğrulayıcıyı boş ya da
        // başka bir `IysAccountContext` ile çağıran mutasyon görünmez olur.
        private readonly Func<IysAccountContext, IReadOnlyList<string>, IysSearchResult> _search;
        public StubIysClient(Func<IysAccountContext, IReadOnlyList<string>, IysSearchResult> search)
            => _search = search;

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Doğrulama yalnız search kullanır.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(_search(account, recipients));
    }
}
