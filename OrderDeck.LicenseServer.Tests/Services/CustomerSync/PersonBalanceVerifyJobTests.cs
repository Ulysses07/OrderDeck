using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary><c>person-balance-verify</c> (spec 2026-10-11 §8.1): günlük denetim yalnız yeni sapmada
/// <c>Admin:AlertEmail</c>'e yazar; taban yokken eksi satır yalnız sayılır. Her test kendi fabrikasıyla (iş bütün
/// lisansları tarar).</summary>
public sealed class PersonBalanceVerifyJobTests
{
    private static readonly string AlertTo = $"uyari-{Guid.NewGuid():N}@example.test";

    private static async Task<(TestEmailSender Email, PersonBalanceVerifier.Report Report)> RunJobAsync(
        ApiFactory factory, string? alertTo = null)
    {
        var email = new TestEmailSender();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:AlertEmail"] = alertTo ?? AlertTo })
            .Build();
        using var scope = factory.Services.CreateScope();
        var job = new PersonBalanceVerifyJob(
            scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>(), email, config,
            NullLogger<PersonBalanceVerifyJob>.Instance);
        var report = await job.VerifyAndAlertAsync(default);
        return (email, report);
    }

    private static Task MarkerAsync(Kit kit) =>
        kit.Sales.WithDbAsync(db =>
        {
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Tabanli_lisansta_taban_disi_eksi_satir_e_posta_ile_uyarir_kisisel_veri_yok()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        var username = $"u{Guid.NewGuid():N}";
        await kit.MemberAsync(null, -10m, username: username);

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).Alerts.Should().ContainSingle();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.ToEmail.Should().Be(AlertTo);
        sent.PlainBody.Should().Contain(kit.LicenseId.ToString()).And.Contain("eksi: taban dışı 1").And.NotContain(username);
        sent.HtmlBody.Should().NotContain(username);
    }

    [Fact]
    public async Task Taban_yokken_eksi_satir_uyarmaz()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await kit.MemberAsync(null, -10m);

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).NegativeRows.Outside.Should().Be(1);
        email.Sent.Should().BeEmpty("§8.1: taban alınana kadar yalnız sayı");
    }

    [Fact]
    public async Task Sapma_yoksa_e_posta_gitmez()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        await kit.Example1Async();

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).HasBaseline.Should().BeTrue();
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AlertEmail_tanimsizsa_gondermez()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        await kit.MemberAsync(null, -10m);

        var (email, report) = await RunJobAsync(factory, alertTo: "");

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).Alerts.Should().ContainSingle("uyarı var, yalnız alıcı yok");
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public void Is_0458_UTC_takvimiyle_kaydolur()
    {
        var storage = new MemoryStorage();

        Program.SchedulePersonBalanceJobs(new RecurringJobManager(storage));

        using var connection = storage.GetConnection();
        var job = connection.GetRecurringJobs().Should().ContainSingle(j => j.Id == "person-balance-verify").Subject;
        (job.Cron, job.TimeZoneId).Should().Be(("58 4 * * *", "UTC"));
        job.Job.Type.Should().Be(typeof(PersonBalanceVerifyJob));
        job.Job.Method.Name.Should().Be(nameof(PersonBalanceVerifyJob.RunAsync));
    }

    [Fact]
    public void Is_DI_kapsamindan_cozulur()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PersonBalanceVerifyJob>().Should().NotBeNull();
    }
}
