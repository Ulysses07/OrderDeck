using FluentAssertions;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class S3SinkRegistrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public S3SinkRegistrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Default_registration_uses_no_op_sink()
    {
        // ApiFactory does not set Backup:S3:Enabled=true, so the no-op
        // implementation must be wired. Otherwise S3BackupSink ctor would
        // throw on missing ServiceUrl/AccessKey/etc, breaking every test.
        using var scope = _factory.Services.CreateScope();
        var sink = scope.ServiceProvider.GetRequiredService<IS3BackupSink>();
        sink.IsEnabled.Should().BeFalse();
        sink.Should().BeOfType<NoOpS3BackupSink>();
    }

    [Fact]
    public async Task NoOp_upload_returns_true_without_io()
    {
        var sink = new NoOpS3BackupSink();
        var ok = await sink.UploadAsync("/nonexistent/path", Guid.NewGuid());
        ok.Should().BeTrue("the no-op success contract is what keeps controllers from branching on IsEnabled");
    }
}
