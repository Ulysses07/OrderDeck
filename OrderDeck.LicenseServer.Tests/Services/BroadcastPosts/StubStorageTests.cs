using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.BroadcastPosts;

public class StubStorageTests
{
    private static StubBroadcastMediaStorage New() =>
        new(NullLogger<StubBroadcastMediaStorage>.Instance);

    [Fact]
    public async Task Head_returns_null_for_missing_key()
        => (await New().HeadAsync("nope")).Should().BeNull();

    [Fact]
    public async Task Seed_then_Head_returns_info()
    {
        var s = New();
        s.Seed("k1", 1024, "image/jpeg");
        var info = await s.HeadAsync("k1");
        info.Should().NotBeNull();
        info!.SizeBytes.Should().Be(1024);
        info.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Delete_removes_seeded_object()
    {
        var s = New();
        s.Seed("k1", 1, "x");
        await s.DeleteAsync("k1");
        (await s.HeadAsync("k1")).Should().BeNull();
    }

    [Fact]
    public async Task CreateUploadUrl_returns_stub_url()
    {
        var url = await New().CreateUploadUrlAsync("k", "image/jpeg", 1024);
        url.Should().Contain("stub.local").And.Contain("upload=1");
    }
}
