using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Facebook;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Facebook canlı yayın çözümleyicisinin ayrıntılı cevabı (tasarım: Sohbeti yenile §5.4).
///
/// NEDEN: çözümleyici her hatayı "yayın yok" sayıyordu; geçersiz Sayfa token'ı da öyle görünüyor,
/// şeritte soluk bir "yayın bekleniyor" olarak kalıyordu. Operatör yayını açıp sohbetin gelmesini
/// bekliyor, oysa beklemek hiçbir şeyi düzeltmiyor. Ayrıntılı çağrı oturum ve izin hatasını
/// ayırt eder; sade çağrının sözleşmesi (canlı değilse ya da hata varsa null) aynı kalır, bekçi onu
/// kullanmayı sürdürür.
/// </summary>
public sealed class FacebookLiveVideoResolverTests
{
    private readonly FakeGraph _graph = new();
    private readonly string _pageId = FacebookOAuthTestKit.NewId();
    private readonly string _token = FacebookOAuthTestKit.NewToken();

    private FacebookLiveVideoResolver Resolver() =>
        new(new HttpClient(_graph, disposeHandler: false), NullLogger<FacebookLiveVideoResolver>.Instance);

    private void LiveVideosAnswer(Func<HttpResponseMessage> respond) => _graph.Map(
        request => request.AbsolutePath.EndsWith($"/{_pageId}/live_videos", StringComparison.Ordinal),
        _ => respond());

    private static string LiveVideosBody(params (string LiveVideoId, string Status, string VideoId)[] rows) =>
        new JsonObject
        {
            ["data"] = new JsonArray(rows.Select(row => (JsonNode?)new JsonObject
            {
                ["id"] = row.LiveVideoId,
                ["status"] = row.Status,
                ["video"] = new JsonObject { ["id"] = row.VideoId },
            }).ToArray()),
        }.ToJsonString();

    [Fact]
    public async Task Live_video_is_found()
    {
        var videoId = FacebookOAuthTestKit.NewId();
        LiveVideosAnswer(() => FakeGraph.Json(LiveVideosBody((FacebookOAuthTestKit.NewId(), "LIVE", videoId))));

        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.Live, videoId));
        (await Resolver().ResolveAsync(_pageId, _token, default)).Should().Be(videoId);
    }

    [Fact]
    public async Task No_live_video_is_not_live()
    {
        LiveVideosAnswer(() => FakeGraph.Json("""{"data":[]}"""));
        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.NotLive));

        // Listede yalnız bitmiş yayın varsa da canlı değildir.
        LiveVideosAnswer(() => FakeGraph.Json(
            LiveVideosBody((FacebookOAuthTestKit.NewId(), "VOD", FacebookOAuthTestKit.NewId()))));
        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.NotLive));
        (await Resolver().ResolveAsync(_pageId, _token, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(190)] // token süresi doldu ya da geri alındı
    [InlineData(463)]
    [InlineData(200)] // izin verilmemiş
    [InlineData(10)]
    public async Task Invalid_token_is_an_auth_problem_but_the_plain_call_still_returns_null(int graphCode)
    {
        LiveVideosAnswer(() => FakeGraph.Error(graphCode));

        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.AuthProblem));
        (await Resolver().ResolveAsync(_pageId, _token, default))
            .Should().BeNull("bekçi sade çağrıyı kullanır; onun sözleşmesi değişmez");
    }

    [Fact]
    public async Task Server_error_is_failed()
    {
        LiveVideosAnswer(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.Failed));

        // Hız sınırı da beklemekle geçer: "oturum geçersiz" denmez.
        LiveVideosAnswer(() => FakeGraph.Error(4));
        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.Failed));

        LiveVideosAnswer(() => throw new HttpRequestException("ağ yok"));
        (await Resolver().ResolveDetailedAsync(_pageId, _token, default))
            .Should().Be(new FacebookLiveLookup(FacebookLiveLookupStatus.Failed));
        (await Resolver().ResolveAsync(_pageId, _token, default)).Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_the_detailed_call_throws_while_the_plain_call_returns_null()
    {
        // Yenileme süren aramayı keser: ayrıntılı çağrı iptali yukarı taşımalı ki döngü hemen baştan
        // değerlendirsin. Sade çağrı ise bugünkü gibi hiçbir şey fırlatmaz.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        LiveVideosAnswer(() => FakeGraph.Json("""{"data":[]}"""));

        var detailed = () => Resolver().ResolveDetailedAsync(_pageId, _token, cancelled.Token);

        await detailed.Should().ThrowAsync<OperationCanceledException>();
        (await Resolver().ResolveAsync(_pageId, _token, cancelled.Token)).Should().BeNull();
    }
}
