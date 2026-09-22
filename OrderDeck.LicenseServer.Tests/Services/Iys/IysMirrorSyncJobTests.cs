using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>Günlük eşitleme yalnız DOĞRULANMIŞ hesaplar için ayna işi kuyruğa
/// atar; kendisi ayna koşturmaz (kilit ve kota temposu ayna işinde).</summary>
public sealed class IysMirrorSyncJobTests
{
    /// <summary>Enqueue çağrılarını kaydeden IBackgroundJobClient — gerçek
    /// Hangfire storage'a yazmadan hangi lisansların kuyruğa alındığını
    /// doğrulamak için (kalıp: SmsCampaignRecoveryJobTests).</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = new();

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-sync-{Guid.NewGuid():N}").Options);

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    /// <summary>Abone numarası ve parola ÜRETİLİR (depo public).</summary>
    private static Guid SeedAccount(LicenseDbContext db, NetgsmAccountStatus status)
    {
        var licenseId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return licenseId;
    }

    private static IysMirrorSyncJob Job(LicenseDbContext db, IBackgroundJobClient jobs)
        => new(Accounts(db), jobs, NullLogger<IysMirrorSyncJob>.Instance);

    [Fact]
    public async Task Yalniz_dogrulanmis_hesaplar_icin_ayna_isi_kuyruga_girer()
    {
        using var db = NewDb();
        var a = SeedAccount(db, NetgsmAccountStatus.Verified);
        var b = SeedAccount(db, NetgsmAccountStatus.Verified);
        SeedAccount(db, NetgsmAccountStatus.Failed);
        SeedAccount(db, NetgsmAccountStatus.Disabled);
        var jobs = new RecordingJobClient();

        var count = await Job(db, jobs).RunAsync();

        count.Should().Be(2);
        jobs.Created.Should().HaveCount(2);
        jobs.Created.Should().OnlyContain(j => j.Type == typeof(IysMirrorImportJob),
            "eşitleme işi ayna koşturmaz, ayna İŞİNİ kuyruğa atar");
        jobs.Created.Select(j => (Guid)j.Args[0]).Should().BeEquivalentTo(new[] { a, b },
            "Failed ve Disabled hesaplar için ayna anlamsız — iş 'hesap yok' diye çıkardı");
    }

    [Fact]
    public async Task Dogrulanmis_hesap_yoksa_hicbir_is_kuyruga_girmez()
    {
        using var db = NewDb();
        SeedAccount(db, NetgsmAccountStatus.Failed);
        var jobs = new RecordingJobClient();

        (await Job(db, jobs).RunAsync()).Should().Be(0);
        jobs.Created.Should().BeEmpty();
    }
}
