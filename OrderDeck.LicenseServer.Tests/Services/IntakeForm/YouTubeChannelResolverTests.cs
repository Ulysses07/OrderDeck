using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class YouTubeChannelResolverTests
{
    // Sabit anahtar YAZMA (repo public, tarayıcı fixture ile gerçeği ayırt edemez).
    private static string NewApiKey() => $"ytkey-{Guid.NewGuid():N}";

    private const string FoundJson = """
    {"items":[{"id":"UCabcdefghijklmnopqrstuv","snippet":{"title":"OrderDeck",
    "thumbnails":{"default":{"url":"https://yt3.example/a.jpg"}}}}]}
    """;

    private const string EmptyJson = """{"items":[]}""";

    private static YouTubeChannelResolver Build(ScriptedHandler handler, string? apiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["YouTube:ApiKey"] = apiKey })
            .Build();
        return new YouTubeChannelResolver(
            new SingleHandlerFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config,
            NullLogger<YouTubeChannelResolver>.Instance);
    }

    [Fact]
    public async Task Kanal_bulununca_kimlik_ve_baslik_doner()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveHandleAsync("@OrderDeck", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeTrue();
        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        r.Title.Should().Be("OrderDeck");
        r.Thumbnail.Should().Be("https://yt3.example/a.jpg");
        // Handle @ atılıp küçük harfe indirilerek sorgulanır.
        handler.Requests[0].RequestUri!.Query.Should().Contain("forHandle=orderdeck");
        // channels.list kullanılır, search.list değil (search.list 100 kota birimi = yasak).
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/youtube/v3/channels");
        handler.Requests[0].RequestUri!.Query.Should().Contain("part=id,snippet");
        handler.Requests[0].RequestUri!.Query.Should().Contain("forHandle=");
    }

    [Fact]
    public async Task Kanal_yoksa_Exists_false_ama_Available_true()
    {
        var sut = Build(new ScriptedHandler((HttpStatusCode.OK, EmptyJson)), NewApiKey());

        var r = await sut.ResolveHandleAsync("yokboylekanal", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeFalse();
        r.ChannelId.Should().BeNull();
    }

    /// <summary>
    /// Kota/ağ arızasında Available:false. Bu ayrım kritik: çağıran taraf
    /// "bulunamadı" ile "bakamadık"ı karıştırırsa bizim arızamız müşteriyi kilitler.
    /// </summary>
    [Fact]
    public async Task Api_hatasinda_Available_false_doner()
    {
        var sut = Build(new ScriptedHandler((HttpStatusCode.Forbidden, "quota")), NewApiKey());

        var r = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        r.Available.Should().BeFalse();
        r.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Api_anahtari_yoksa_cagri_yapilmaz()
    {
        var handler = new ScriptedHandler();
        var sut = Build(handler, apiKey: null);

        var r = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        r.Available.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Cache olmadan her gönderim ikinci bir kota birimi harcardı; istemci zaten
    /// aynı handle'ı az önce sormuş oluyor.
    /// </summary>
    [Fact]
    public async Task Ikinci_cagri_cache_ten_gelir()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);
        var r = await sut.ResolveHandleAsync("@OrderDeck", CancellationToken.None);

        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Bos_handle_cagri_yapmadan_bulunamadi_doner()
    {
        var handler = new ScriptedHandler();
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveHandleAsync("  ", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Başarısız aramalar cache'lenmez. Aksi takdirde tek bir 403 yanıtı aynı
    /// handle'ı bir saat zehirler; sonraki başarılı deneme asla ağa çıkmaz.
    /// </summary>
    [Fact]
    public async Task Basarisiz_arama_cache_lenmez_sonraki_cagri_agdan_gider()
    {
        // İlk çağrıda Forbidden (kota/geçici hata), ikinci çağrıda gerçek sonuç.
        var handler = new ScriptedHandler(
            (HttpStatusCode.Forbidden, ""),
            (HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        var first = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);
        var second = await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        first.Available.Should().BeFalse();          // degrade
        second.Exists.Should().BeTrue();              // gerçek sonuç geldi
        second.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        // İki ayrı HTTP çağrısı yapıldı — cache zehirlenmesi yok.
        handler.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// HttpClient.Timeout süresi dolduğunda atılan TaskCanceledException (CancellationToken
    /// iptal edilmemişken) yumuşak degrade'e dönüşmeli — fırlatılmamalı. Aksi takdirde
    /// googleapis yavaş yanıt verdiğinde müşterinin kaydı kaybolur.
    ///
    /// NOT: Test, CancellationToken.None ile çağırır — bu zaman aşımı senaryosunun
    /// tam karşılığıdır (ct iptal edilmemiş ama handler fırlatıyor).
    /// </summary>
    [Fact]
    public async Task Zaman_asimi_exception_u_Available_false_a_donusur_musteri_kaybolmaz()
    {
        var handler = new TimeoutSimulatingHandler();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["YouTube:ApiKey"] = NewApiKey() })
            .Build();
        var sut = new YouTubeChannelResolver(
            new SingleHandlerFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            config,
            NullLogger<YouTubeChannelResolver>.Instance);

        // CancellationToken.None → ct.IsCancellationRequested == false
        // Handler fırlattığı TaskCanceledException catch (OperationCanceledException) when (ct.IsCancellationRequested)
        // koşulunu SAĞLAMAZ, dolayısıyla genel catch'e düşer ve Available:false döner.
        var act = () => sut.ResolveHandleAsync("orderdeck", CancellationToken.None);

        var r = await act.Should().NotThrowAsync();
        r.Subject.Available.Should().BeFalse();
        r.Subject.Exists.Should().BeFalse();
    }

    /// <summary>
    /// Kanal kimliği yolu <c>forHandle</c> DEĞİL <c>id</c> ile sorulur — forHandle'a
    /// "UCabc…" göndermek hiçbir kanala denk gelmez ve geçerli kanal adresini
    /// yapıştıran müşteri engellenirdi. Anahtar yine sorgu dizesinde değil
    /// başlıkta: HttpClient günlükleyicisi tam URI'yi Information seviyesinde
    /// konteyner günlüğüne yazıyor.
    /// </summary>
    [Fact]
    public async Task Kimlik_sorgusu_id_parametresi_kullanir_anahtar_baslikta()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveChannelIdAsync("UCabcdefghijklmnopqrstuv", CancellationToken.None);

        r.Exists.Should().BeTrue();
        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");

        var req = handler.Requests[0];
        req.RequestUri!.AbsolutePath.Should().Be("/youtube/v3/channels");
        req.RequestUri!.Query.Should().Contain("id=UCabcdefghijklmnopqrstuv");
        req.RequestUri!.Query.Should().NotContain("forHandle");
        req.RequestUri!.Query.Should().NotContain("key=");
        req.Headers.Contains("X-goog-api-key").Should().BeTrue();
    }

    [Fact]
    public async Task Kimlik_icin_ikinci_cagri_cache_ten_gelir()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        await sut.ResolveChannelIdAsync("UCabcdefghijklmnopqrstuv", CancellationToken.None);
        var r = await sut.ResolveChannelIdAsync("UCabcdefghijklmnopqrstuv", CancellationToken.None);

        r.ChannelId.Should().Be("UCabcdefghijklmnopqrstuv");
        handler.Requests.Should().HaveCount(1);
    }

    /// <summary>
    /// İki önbellek anahtar uzayı ÇAKIŞMAMALI. Aynı string önce handle sonra
    /// kimlik olarak sorulursa iki ayrı sorgu gitmeli: tek anahtar uzayı olsaydı
    /// handle sonucu kimlik sorgusunun cevabı yerine geçer ve yanlış kanal
    /// onaylatılırdı.
    /// </summary>
    [Fact]
    public async Task Handle_ve_kimlik_onbellekleri_cakismaz()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, FoundJson),
            (HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        await sut.ResolveHandleAsync("orderdeck", CancellationToken.None);
        await sut.ResolveChannelIdAsync("orderdeck", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("forHandle=");
        handler.Requests[1].RequestUri!.Query.Should().Contain("id=orderdeck");
    }

    /// <summary>
    /// Kimlik yolunda da yumuşak degrade: Available:false = "bakamadık", çağıran
    /// bunu müşteriyi engellemek için KULLANMAMALI.
    /// </summary>
    [Fact]
    public async Task Kimlik_sorgusu_hatasinda_Available_false_doner()
    {
        var sut = Build(new ScriptedHandler((HttpStatusCode.Forbidden, "quota")), NewApiKey());

        var r = await sut.ResolveChannelIdAsync("UCabcdefghijklmnopqrstuv", CancellationToken.None);

        r.Available.Should().BeFalse();
        r.Exists.Should().BeFalse();
    }

    /// <summary>
    /// Kanal kimlikleri büyük/küçük harf DUYARLI — handle yolundaki
    /// ToLowerInvariant burada uygulanamaz, uygulanırsa geçerli kimlik
    /// bulunamaz hâle gelir.
    /// </summary>
    [Fact]
    public async Task Kimlik_kucuk_harfe_indirilmez()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, FoundJson));
        var sut = Build(handler, NewApiKey());

        await sut.ResolveChannelIdAsync("UCabcdefghijklmnopqrstuv", CancellationToken.None);

        handler.Requests[0].RequestUri!.Query.Should().Contain("id=UCabcdefghijklmnopqrstuv");
    }

    [Fact]
    public async Task Bos_kimlik_cagri_yapmadan_bulunamadi_doner()
    {
        var handler = new ScriptedHandler();
        var sut = Build(handler, NewApiKey());

        var r = await sut.ResolveChannelIdAsync("  ", CancellationToken.None);

        r.Available.Should().BeTrue();
        r.Exists.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _script;
        public List<HttpRequestMessage> Requests { get; } = [];

        public ScriptedHandler(params (HttpStatusCode, string)[] script)
            => _script = new Queue<(HttpStatusCode, string)>(script);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = _script.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    // HttpClient.Timeout dolduğunda atılan TaskCanceledException'ı simüle eder.
    // İptal edilmemiş bir CancellationToken ile (None) fırlatır — timeout senaryosu budur.
    private sealed class TimeoutSimulatingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new TaskCanceledException("simüle edilmiş zaman aşımı", null, new CancellationToken());
    }
}
