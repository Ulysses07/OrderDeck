using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramAccountResolverTests
{
    /// <summary>Tek bir sabit yanıt döndüren sahte handler; kaç kez çağrıldığını sayar.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Calls { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private static InstagramAccountResolver Make(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<InstagramAccountResolver>.Instance);

    [Fact]
    public async Task Resolves_linked_business_account()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"17841400000000000\",\"username\":\"mezatdunyasi\"}," +
            "\"id\":\"811177875420245\"}");

        var account = await Make(handler).ResolveAsync("811177875420245", "tok", CancellationToken.None);

        account.Should().NotBeNull();
        account!.Value.IgUserId.Should().Be("17841400000000000");
        account.Value.Username.Should().Be("mezatdunyasi");
    }

    [Fact]
    public async Task Returns_null_when_no_instagram_account_linked()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"811177875420245\"}");

        var account = await Make(handler).ResolveAsync("811177875420245", "tok", CancellationToken.None);

        account.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_on_error_response()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"nope\",\"code\":100}}");

        var account = await Make(handler).ResolveAsync("p", "tok", CancellationToken.None);

        account.Should().BeNull();
    }

    [Fact]
    public async Task Successful_result_is_cached_per_page()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"ig1\",\"username\":\"u\"}}");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Failure_is_not_cached()
    {
        // Geçici hata kalıcı "bağlı hesap yok"a dönüşmemeli.
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Different_page_bypasses_cache()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"instagram_business_account\":{\"id\":\"ig1\",\"username\":\"u\"}}");
        var resolver = Make(handler);

        await resolver.ResolveAsync("page1", "tok", CancellationToken.None);
        await resolver.ResolveAsync("page2", "tok", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }
}
