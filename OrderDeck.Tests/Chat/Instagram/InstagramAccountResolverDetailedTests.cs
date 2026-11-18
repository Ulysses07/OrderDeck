using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Çözümleyicinin üç durumlu sonucu: bağlı hesap (<c>Found</c>), Sayfada Instagram hesabı
/// yok (<c>NotLinked</c>), çağrı düştü (<c>Failed</c>).
///
/// NEDEN: <c>ResolveAsync</c> son ikisini aynı null'a indirir. Sağlık göstergesi yalnız
/// Facebook kullanan kuruluma yanlış alarm vermemeli, yayın başındaki ağ kesintisine de
/// yanlış sebep söylememelidir; ayrım burada kilitlenir. İki yöntem tek önbelleği paylaşır
/// ve yalnız <c>Found</c> saklanır: geçici bir hata kalıcı "hesap yok"a dönüşmez.
///
/// Ağa çıkılmaz. Depo PUBLIC: kimlik, token ve kullanıcı adı literal yazılmaz, üretilir.
/// </summary>
public sealed class InstagramAccountResolverDetailedTests
{
    /// <summary>
    /// Yanıtı testin verdiği, isteği sayan sahte işleyici. Gerçek soket işleyicisi gibi
    /// iptali isteğe başlamadan yükseltir (HttpClient belirteci kendisi denetlemez), bu
    /// yüzden iptal edilmiş çağrı sayılmaz. <c>respond</c> fırlatırsa istek ağ hatasıyla
    /// düşmüş olur.
    /// </summary>
    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    /// <summary>Bağlı hesabı taşıyan yanıt gövdesi: <c>{"instagram_business_account":{"id":…,"username":…}}</c>.</summary>
    private static string Linked(InstagramAccount account) => JsonSerializer.Serialize(new
    {
        instagram_business_account = new { id = account.IgUserId, username = account.Username },
    });

    private static InstagramAccountResolver Make(CountingHandler handler) =>
        new(new HttpClient(handler), NullLogger<InstagramAccountResolver>.Instance);

    private static Task<(InstagramAccount? Account, InstagramResolveStatus Status)> Resolve(
        InstagramAccountResolver resolver, string pageId) =>
        resolver.ResolveDetailedAsync(pageId, New("t"), CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}", InstagramResolveStatus.NotLinked)]
    [InlineData(HttpStatusCode.OK, "{\"instagram_business_account\":{\"id\":\"\"}}", InstagramResolveStatus.NotLinked)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":190}}", InstagramResolveStatus.Failed)]
    [InlineData(HttpStatusCode.OK, "<html>", InstagramResolveStatus.Failed)]
    [InlineData(null, "", InstagramResolveStatus.Failed)] // reply null: istek ağ hatasıyla düşer
    public async Task Hesap_yoksa_NotLinked_cagri_dustuyse_Failed_doner_ikisi_de_onbelleklenmez(
        HttpStatusCode? reply, string body, InstagramResolveStatus expected)
    {
        var handler = new CountingHandler(() =>
            reply is { } code ? Reply(code, body) : throw new HttpRequestException("ağ yok"));
        var (resolver, pageId) = (Make(handler), New("p"));

        for (var call = 1; call <= 2; call++)
        {
            var (account, status) = await Resolve(resolver, pageId);

            account.Should().BeNull();
            status.Should().Be(expected);
            handler.Calls.Should().Be(call);
        }
    }

    [Fact]
    public async Task Zaman_asimi_cagiranin_iptali_sayilmaz_Failed_doner()
    {
        // HttpClient zaman aşımını da iptal istisnasıyla bildirir. Çağıranın belirteci iptal
        // edilmediyse bu iptal değil, düşen çağrıdır; yükselseydi sohbet döngüsü onu kapanış
        // sayıp sessizce biterdi.
        var handler = new CountingHandler(() => throw new TaskCanceledException("zaman aşımı"));

        var (account, status) = await Resolve(Make(handler), New("p"));

        account.Should().BeNull();
        status.Should().Be(InstagramResolveStatus.Failed);
    }

    [Fact]
    public async Task Bagli_hesap_Found_doner_onbellekten_gelir_ResolveAsync_ile_ortaktir_Invalidate_bosaltir()
    {
        var expected = new InstagramAccount(New("ig"), New("u"));
        var handler = new CountingHandler(() => Reply(HttpStatusCode.OK, Linked(expected)));
        var (resolver, pageId) = (Make(handler), New("p"));

        var (first, second) = (await Resolve(resolver, pageId), await Resolve(resolver, pageId));

        first.Account.Should().Be(expected);
        first.Status.Should().Be(InstagramResolveStatus.Found);
        second.Account.Should().Be(expected);
        second.Status.Should().Be(InstagramResolveStatus.Found);
        (await resolver.ResolveAsync(pageId, New("t"), CancellationToken.None)).Should().Be(expected);
        handler.Calls.Should().Be(1); // üçü de tek istek: ortak önbellek

        resolver.Invalidate();
        await Resolve(resolver, pageId);
        handler.Calls.Should().Be(2);

        await FluentActions.Awaiting(() => resolver.ResolveDetailedAsync(New("p"), New("t"), new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>(); // önbellekte olmayan Sayfa: iptal yükselir
        handler.Calls.Should().Be(2); // iptal edilen çağrı işleyicide sayılmadı
    }
}
