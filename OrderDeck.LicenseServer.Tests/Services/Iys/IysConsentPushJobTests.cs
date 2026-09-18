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

public class IysConsentPushJobTests
{
    private sealed class FakeIysClient : IIysClient
    {
        public List<IReadOnlyList<IysConsentRecord>> AddCalls { get; } = new();
        public Func<IReadOnlyList<IysConsentRecord>, IysAddResult>? AddBehavior { get; set; }
        public Exception? ThrowOnAdd { get; set; }

        public Task<IysAddResult> AddAsync(IReadOnlyList<IysConsentRecord> items, CancellationToken ct = default)
        {
            AddCalls.Add(items);
            if (ThrowOnAdd is not null) throw ThrowOnAdd;
            return Task.FromResult(AddBehavior?.Invoke(items)
                ?? new IysAddResult("0", "{\"code\":\"0\"}", Queued: true));
        }

        public Task<IysSearchResult> SearchAsync(IReadOnlyList<string> recipients, CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-push-{Guid.NewGuid():N}").Options);

    private static IysConsentPushJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentPushJob>.Instance);

    private static IysConsent Pending(string phone) => new()
    {
        Id = Guid.NewGuid(),
        BrandCode = "731734",
        ChannelType = "MESAJ",
        RecipientType = "BIREYSEL",
        Recipient = phone,
        Status = IysConsentStatus.Onay,
        ConsentDate = DateTimeOffset.UtcNow,
        SourceCode = "HS_WEB",
        PushState = IysPushState.Pending,
        PushDeadline = DateTimeOffset.UtcNow.AddDays(3),
        LastLocalEventAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<LicenseDbContext> SeedAsync(int count)
    {
        var db = NewDb();
        for (var i = 0; i < count; i++)
            db.IysConsents.Add(Pending($"+90555{i:D7}"));
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Code_sifir_Confirmed_YAPMAZ_yalnizca_Pushed()
    {
        // 2026-09-17'de 284 kaydı kaybettiren hata tam olarak buydu:
        // "code 0" kuyruğa alındı demek, kabul edildi değil. Kabul kararını
        // yalnız IysConsentVerifyJob (/iys/search) verebilir.
        using var db = await SeedAsync(1);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Pushed);
        row.PushState.Should().NotBe(IysPushState.Confirmed);
        row.LastVerifiedStatus.Should().BeNull("İYS'ye henüz sorulmadı");
        row.NextVerifyAt.Should().NotBeNull("doğrulama randevusu alınmalı");
    }

    [Fact]
    public async Task Ham_yanit_olay_tablosuna_yazilir()
    {
        using var db = await SeedAsync(1);

        await Job(db, new FakeIysClient()).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.PushAttempt);
        ev.ApiResponseCode.Should().Be("0");
        ev.ApiResponseBody.Should().Contain("code");
    }

    [Fact]
    public async Task Bekleyenler_yirmiserli_partilenir()
    {
        using var db = await SeedAsync(45);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Select(c => c.Count).Should().Equal(20, 20, 5);
    }

    [Fact]
    public async Task Gecici_hata_Failed_birakir_deadline_icinde_yeniden_denenir()
    {
        using var db = await SeedAsync(1);
        var client = new FakeIysClient { ThrowOnAdd = new HttpRequestException("ağ") };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Son_tarihi_gecmis_kayit_itilmez_Expired_olur()
    {
        using var db = NewDb();
        var row = Pending("+905551112233");
        row.PushDeadline = DateTimeOffset.UtcNow.AddHours(-1);
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Should().BeEmpty("süresi dolmuş kayıt İYS'ye gönderilmez");
        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Expired);
    }

    [Fact]
    public async Task Yapilandirma_hatasi_boru_hattini_durdurur()
    {
        using var db = await SeedAsync(45);
        var client = new FakeIysClient
        {
            ThrowOnAdd = new IysConfigurationException("60", "marka kodu"),
        };

        var act = async () => await Job(db, client).RunAsync();

        await act.Should().ThrowAsync<IysConfigurationException>();
        client.AddCalls.Should().HaveCount(1, "ilk partiden sonra durmalı, 45 kaydı harcamamalı");
    }
}
