using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Extension;

public class ExtensionConfigControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ExtensionConfigControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_returns_200_with_etag_and_cache_headers()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/extension/selectors");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        resp.Headers.ETag.Should().NotBeNull("clients short-circuit on If-None-Match");
        resp.Headers.CacheControl?.Public.Should().BeTrue();
        resp.Headers.CacheControl?.MaxAge.Should().BeGreaterThan(System.TimeSpan.Zero);
    }

    [Fact]
    public async Task Get_returns_wildcard_cors_header()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/extension/selectors");

        // Wildcard CORS so the extension's fetch from a content-script context
        // is allowed without a negotiation. The bundle is public selector data
        // with no per-user state.
        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("*");
    }

    [Fact]
    public async Task Body_deserialises_into_known_schema()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/extension/selectors");
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("publishedAt").GetDateTimeOffset().Should().BeAfter(System.DateTimeOffset.MinValue);

        var platforms = root.GetProperty("platforms");
        platforms.TryGetProperty("instagram", out _).Should().BeTrue();
        platforms.TryGetProperty("tiktok",    out _).Should().BeTrue();
        platforms.TryGetProperty("facebook",  out _).Should().BeTrue();

        var ig = platforms.GetProperty("instagram");
        ig.GetProperty("comments").GetProperty("primaryContainers").GetString()
            .Should().NotBeNullOrWhiteSpace();
        ig.GetProperty("validators").GetProperty("uiTextBlocklist").GetArrayLength()
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task If_None_Match_returns_304_without_body()
    {
        var client = _factory.CreateClient();

        // First request — capture ETag.
        var first = await client.GetAsync("/api/v1/extension/selectors");
        var etag = first.Headers.ETag!.ToString();

        // Conditional request with the same tag — server should 304.
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/extension/selectors");
        req.Headers.Add("If-None-Match", etag);
        var second = await client.SendAsync(req);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        // 304 must not carry a body.
        var bodyBytes = await second.Content.ReadAsByteArrayAsync();
        bodyBytes.Length.Should().Be(0);
        second.Headers.ETag?.ToString().Should().Be(etag);
    }

    [Fact]
    public async Task ETag_is_stable_across_requests()
    {
        var client = _factory.CreateClient();

        var a = await client.GetAsync("/api/v1/extension/selectors");
        var b = await client.GetAsync("/api/v1/extension/selectors");

        a.Headers.ETag!.ToString().Should().Be(b.Headers.ETag!.ToString(),
            "the bundle is a static constant — same instance, same instance, same etag");
    }
}
