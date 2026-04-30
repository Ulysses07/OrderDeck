using FluentAssertions;
using LiveDeck.LicenseServer.Domain;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Domain;

public class IntakeFormEntitiesTests
{
    [Fact]
    public void IntakeFormConfig_default_is_active_with_empty_strings()
    {
        var cfg = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = "test",
            WhatsAppPhone = "+905551234567",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        cfg.IsActive.Should().BeTrue();
        cfg.CustomTitle.Should().BeNull();
        cfg.Slug.Should().Be("test");
    }

    [Fact]
    public void IntakeFormSubmission_holds_audit_fields()
    {
        var sub = new IntakeFormSubmission
        {
            Id = Guid.NewGuid(),
            IntakeFormConfigId = Guid.NewGuid(),
            Username = "bilalcanli",
            FullName = "Bilal Canlı",
            Address = "Atatürk Cad. No:12",
            SubmittedAt = DateTimeOffset.UtcNow,
            IpAddress = "10.0.0.5",
            UserAgent = "Mozilla/5.0"
        };

        sub.Username.Should().Be("bilalcanli");
        sub.IpAddress.Should().Be("10.0.0.5");
    }
}
