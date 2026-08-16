using System.Net;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Licensing;

public class LicenseApiClientStockTests
{
    private static LicenseApiClient BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder, out List<string> paths)
    {
        var seen = new List<string>();
        paths = seen;
        var http = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            seen.Add(req.RequestUri!.PathAndQuery);
            return responder(req);
        }))
        { BaseAddress = new Uri("https://test.local") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    [Fact]
    public async Task Sends_composite_cursor_and_parses_response()
    {
        const string json = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":"22222222-2222-2222-2222-222222222222",
                          "quantity":7},
                         {"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":-2}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":true}
            """;
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json), out var paths);

        var licenseId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var sinceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var res = await client.GetStockBalancesSinceAsync(
            licenseId, DateTimeOffset.UnixEpoch, sinceId, take: 500);

        res.Balances.Should().HaveCount(2);
        res.Balances[0].Quantity.Should().Be(7);
        res.Balances[1].ProductVariantId.Should().BeNull();
        res.Balances[1].Quantity.Should().Be(-2);
        res.CursorId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        res.HasMore.Should().BeTrue();

        paths.Should().ContainSingle();
        paths[0].Should().StartWith($"/api/v1/licenses/{licenseId}/stock/balances/since?");
        paths[0].Should().Contain($"sinceId={sinceId}");
        paths[0].Should().Contain("take=500");
    }

    [Fact]
    public async Task Rejects_take_outside_server_range()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "{}"), out _);

        var act = () => client.GetStockBalancesSinceAsync(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.Empty, take: 1001);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Null_body_throws_instead_of_looking_like_an_empty_page()
    {
        // Boş sayfa DÖNGÜ SONLANDIRICISI ve imleç ilerleticisi. Bozuk gövdeyi
        // "boş sayfa" saymak imleci sessizce ileri sarardı → hareketler kaybolur.
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"), out _);

        var act = () => client.GetStockBalancesSinceAsync(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.Empty);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }
}
