using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentVerifyJobTests
{
    private const string Phone = "+905551112233";

    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Answer { get; set; } = new();
        public List<IReadOnlyList<string>> SearchCalls { get; } = new();
        public List<IysAccountContext> SearchAccounts { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => Task.FromResult(new IysAddResult("0", "{}", true));

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            SearchAccounts.Add(account);
            SearchCalls.Add(recipients);
            return Task.FromResult(new IysSearchResult("0", "{\"code\":\"0\"}", Answer));
        }
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-verify-{Guid.NewGuid():N}").Options);

    private static IysConsentVerifyJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentVerifyJob>.Instance);

    private static async Task<LicenseDbContext> SeedPushedAsync(
        DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = "731734",
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = Phone,
            Status = IysConsentStatus.Onay,
            ConsentDate = now.AddMinutes(-30),
            PushState = IysPushState.Pushed,
            PushDeadline = now.AddDays(3),
            LastPushedAt = now.AddMinutes(-20),
            VerifyAttempts = attempts,
            NextVerifyAt = nextVerifyAt ?? now.AddMinutes(-1),
            LastLocalEventAt = now.AddMinutes(-30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ONAY_donerse_Confirmed_olur()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);
        row.LastVerifiedAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Confirmed);
        IysConsentGate.CanSend(row).Should().BeTrue();
    }

    [Fact]
    public async Task RET_donerse_yerel_ONAY_ezilmez_ama_gonderim_kesilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay, "kişinin bize verdiği onay ispat olarak durur");
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Ret);
        IysConsentGate.CanSend(row).Should().BeFalse();
    }

    [Fact]
    public async Task Randevusu_gelmemis_kayit_sorgulanmaz()
    {
        using var db = await SeedPushedAsync(nextVerifyAt: DateTimeOffset.UtcNow.AddHours(1));
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchCalls.Should().BeEmpty(
            "İYS işleme anlık değil; erken sorgu işlenmemiş kaydı RET sanar");
    }

    [Fact]
    public async Task ONAY_gelmezse_bir_sonraki_randevu_alinir()
    {
        using var db = await SeedPushedAsync(attempts: 0);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.VerifyAttempts.Should().Be(1);
        row.NextVerifyAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Pushed, "takvim bitmeden karar kesinleşmez");
    }

    [Fact]
    public async Task Takvim_tukenince_Failed_olur_ve_admin_listesine_duser()
    {
        using var db = await SeedPushedAsync(attempts: 3);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.NextVerifyAt.Should().BeNull();
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sonuc_olay_tablosuna_yazilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.SearchResult);
        ev.Status.Should().Be(IysConsentStatus.Onay);
        ev.ApiResponseCode.Should().Be("0");
    }
}
