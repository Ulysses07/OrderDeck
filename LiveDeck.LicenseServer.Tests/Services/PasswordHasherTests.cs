using FluentAssertions;
using LiveDeck.LicenseServer.Services.Auth;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_produces_argon2id_formatted_string()
    {
        var hash = _hasher.Hash("test-password-123");
        hash.Should().StartWith("$argon2id$v=19$m=65536,t=4,p=2$");
        hash.Split('$').Should().HaveCount(6);
    }

    [Fact]
    public void Verify_returns_true_for_correct_password()
    {
        var hash = _hasher.Hash("correct-horse-battery-staple");
        _hasher.Verify(hash, "correct-horse-battery-staple").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_wrong_password()
    {
        var hash = _hasher.Hash("correct-horse");
        _hasher.Verify(hash, "wrong-password").Should().BeFalse();
    }

    [Fact]
    public void Hash_produces_different_output_for_same_input_due_to_random_salt()
    {
        var hash1 = _hasher.Hash("same-password");
        var hash2 = _hasher.Hash("same-password");
        hash1.Should().NotBe(hash2);
        _hasher.Verify(hash1, "same-password").Should().BeTrue();
        _hasher.Verify(hash2, "same-password").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_malformed_hash()
    {
        _hasher.Verify("not-a-valid-hash", "any").Should().BeFalse();
        _hasher.Verify("", "any").Should().BeFalse();
    }
}
