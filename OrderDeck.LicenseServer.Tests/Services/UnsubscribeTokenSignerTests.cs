using FluentAssertions;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class UnsubscribeTokenSignerTests
{
    private static UnsubscribeTokenSigner Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-must-be-at-least-32-bytes-long-for-hmac"
            })
            .Build();
        return new UnsubscribeTokenSigner(config);
    }

    [Fact]
    public void Sign_then_TryVerify_roundtrips_customerId()
    {
        var signer = Build();
        var customerId = Guid.NewGuid();
        var issuedAt = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);

        var token = signer.Sign(customerId, issuedAt);

        signer.TryVerify(token, out var parsedId, out var parsedTime).Should().BeTrue();
        parsedId.Should().Be(customerId);
        parsedTime.Should().Be(issuedAt);
    }

    [Fact]
    public void TryVerify_returns_false_for_tampered_payload()
    {
        var signer = Build();
        var token = signer.Sign(Guid.NewGuid(), DateTimeOffset.UtcNow);

        // Token format: parts[0].parts[1].parts[2] — middle part'ı bozalım
        var parts = token.Split('.');
        parts[0] = "aaaaaaaa";   // farklı customer id base64
        var tampered = string.Join('.', parts);

        signer.TryVerify(tampered, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_returns_false_for_garbage_token()
    {
        var signer = Build();
        signer.TryVerify("not.a.valid.token", out _, out _).Should().BeFalse();
        signer.TryVerify("", out _, out _).Should().BeFalse();
        signer.TryVerify("only-one-part", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_accepts_old_timestamps()
    {
        var signer = Build();
        var ancient = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var token = signer.Sign(Guid.NewGuid(), ancient);

        // Süresiz — eski tarih kabul edilir
        signer.TryVerify(token, out _, out var parsedTime).Should().BeTrue();
        parsedTime.Should().Be(ancient);
    }
}
