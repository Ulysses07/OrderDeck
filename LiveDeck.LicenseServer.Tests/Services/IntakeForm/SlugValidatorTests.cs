using FluentAssertions;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public class SlugValidatorTests
{
    [Theory]
    [InlineData("burak")]
    [InlineData("burak-streamer")]
    [InlineData("a1b2")]
    [InlineData("abc123")]
    public void Validate_returns_Valid_for_well_formed_slugs(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.Valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_returns_Empty_for_blank_input(string? slug)
    {
        SlugValidator.Validate(slug!).Should().Be(SlugValidationResult.Empty);
    }

    [Theory]
    [InlineData("ab")]                               // 2 char
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 char
    public void Validate_returns_InvalidLength_outside_3_to_32(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.InvalidLength);
    }

    [Theory]
    [InlineData("BURAK")]      // uppercase
    [InlineData("burak_test")] // underscore
    [InlineData("burak.test")] // dot
    [InlineData("-burak")]     // leading dash
    [InlineData("burak-")]     // trailing dash
    public void Validate_returns_InvalidFormat_for_bad_chars_or_position(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.InvalidFormat);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("hangfire")]
    [InlineData("unsubscribe")]
    [InlineData("password-reset")]
    [InlineData("auth")]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("livedeck")]
    public void Validate_returns_Reserved_for_blacklisted_slugs(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.Reserved);
    }

    // "me" (2 chars) and "r" (1 char) are in the reserved blacklist HashSet but are
    // intercepted by the InvalidLength check (< 3) before reaching the Reserved check.
    [Theory]
    [InlineData("me")]
    [InlineData("r")]
    public void Validate_returns_InvalidLength_for_short_reserved_words(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.InvalidLength);
    }

    [Fact]
    public void Validate_is_case_insensitive_for_reserved_check()
    {
        SlugValidator.Validate("ADMIN").Should().Be(SlugValidationResult.InvalidFormat); // uppercase fails format first
    }
}
