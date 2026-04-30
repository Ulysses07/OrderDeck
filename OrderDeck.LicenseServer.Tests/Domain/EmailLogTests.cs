using FluentAssertions;
using LiveDeck.LicenseServer.Domain;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Domain;

public class EmailLogTests
{
    [Fact]
    public void EmailLog_default_state_is_success_with_null_error()
    {
        var entry = new EmailLog
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TemplateKey = "renewal-14d",
            ContextKey = "LDK-XYZ",
            SentAt = DateTimeOffset.UtcNow,
            Error = null
        };

        entry.Error.Should().BeNull();
        entry.TemplateKey.Should().Be("renewal-14d");
    }
}
