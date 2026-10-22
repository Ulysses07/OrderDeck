using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        ApiFactory factory, string? alertTo = null, TestEmailSender? email = null,
        LogRecorder<PersonBalanceVerifyJob>? log = null)
    {
        email ??= new TestEmailSender();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:AlertEmail"] = alertTo ?? AlertTo })
            .Build();
        using var scope = factory.Services.CreateScope();
        var job = new PersonBalanceVerifyJob(
            scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>(), email, config,
            log ?? (ILogger<PersonBalanceVerifyJob>)NullLogger<PersonBalanceVerifyJob>.Instance);
        var report = await job.VerifyAndAlertAsync(default);
        return (email, report);
    }

    /// <summary>Taban satırı (dünkü yakalamadan); <see cref="PersonBalanceVerifier.LicenseMarker"/> = lisans işareti.</summary>
    private static Task BaselineAsync(Kit kit, Guid row, decimal balance = 0m) =>
        kit.Sales.WithDbAsync(db =>
        {
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = row, Balance = balance, LedgerSum = balance,
                CapturedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });

    private static Task MarkerAsync(Kit kit) => BaselineAsync(kit, PersonBalanceVerifier.LicenseMarker);

    [Fact]
    public async Task Tabanli_lisansta_taban_disi_eksi_satir_e_posta_ile_uyarir_kisisel_veri_yok()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        var username = $"u{Guid.NewGuid():N}";
        var row = await kit.MemberAsync(null, -10m, username: username);

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).Alerts.Should().ContainSingle();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.ToEmail.Should().Be(AlertTo);
        sent.PlainBody.Should().Contain(kit.LicenseId.ToString()).And.Contain("eksi: taban dışı 1").And.NotContain(username);
        sent.HtmlBody.Should().NotContain(username);
        // Gövdede yalnız lisans Id'leri ve sayılar (§8.1): raporun Details'i satır Id'lerini taşır, gövdeye girmez.
        foreach (var body in new[] { sent.PlainBody, sent.HtmlBody })
            body.ToLowerInvariant().Should().NotContainAny(row.ToString("D"), row.ToString("N"));
    }

    [Fact]
    public async Task Tek_e_postaya_yalniz_uyaran_lisanslar_girer()
    {
        using var factory = new ApiFactory();
        var first = await Kit.CreateAsync(factory);
        var second = await Kit.CreateAsync(factory);
        var quiet = await Kit.CreateAsync(factory);
        foreach (var kit in new[] { first, second, quiet }) await MarkerAsync(kit);
        await first.MemberAsync(null, -10m);
        await second.MemberAsync(null, -20m);
        // Sessiz lisansın eksi satırı tabanda (S1 öncesinden kalan, kabul edilmiş): sayılır, uyarmaz.
        var accepted = await quiet.MemberAsync(null, -10m);
        await BaselineAsync(quiet, accepted, -10m);

        var (email, report) = await RunJobAsync(factory);

        var quietReport = report.Licenses.Single(l => l.LicenseId == quiet.LicenseId);
        (quietReport.NegativeRows.InBaseline, quietReport.Alerts.Count).Should().Be((1, 0));
        var sent = email.Sent.Should().ContainSingle("uyaran lisanslar tek e-postada").Subject;
        foreach (var body in new[] { sent.PlainBody, sent.HtmlBody })
            body.Should().Contain(first.LicenseId.ToString()).And.Contain(second.LicenseId.ToString())
                .And.NotContain(quiet.LicenseId.ToString());
    }

    [Fact]
    public async Task Smtp_hatasi_isi_dusurmez_raporu_dondurur_hatayi_gunluge_yazar()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        await kit.MemberAsync(null, -10m);
        var email = new TestEmailSender { FailWith = _ => new InvalidOperationException($"smtp-{Guid.NewGuid():N}") };
        var log = new LogRecorder<PersonBalanceVerifyJob>();

        var (_, report) = await RunJobAsync(factory, email: email, log: log);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).Alerts.Should().ContainSingle();
        email.Sent.Should().BeEmpty();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Message.Should().Contain("uyarı e-postası gönderilemedi");
    }

    [Fact]
    public async Task Gunluk_satiri_sekme_sayisini_ve_tabanla_aciklanan_eksi_kisiyi_basar()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        // İç sekme: numara gruplu kalır, sunucu ↔ WPF ayrışma riski (plan fark 15; S2 enable'ının ön koşulu).
        await kit.MemberAsync($"{Kit.NewGroup()}\tx");
        var accepted = await kit.MemberAsync(null, -10m);
        await BaselineAsync(kit, accepted, -10m);
        var log = new LogRecorder<PersonBalanceVerifyJob>();

        var (email, _) = await RunJobAsync(factory, log: log);

        email.Sent.Should().BeEmpty("eksi satır tabanda, sekme yalnız sayılır");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains(kit.LicenseId.ToString()))
            .Which.Message.Should().Contain("havuzu<0 kişi 1 (tabanla açıklanan 1)").And.Contain("sekme/satır sonu 1");
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
