using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
            config);
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
}
