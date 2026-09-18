using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentRecoveryJobTests
{
    private static LicenseDbContext NewDb() => new(
        new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-rec-{Guid.NewGuid():N}").Options);

    private static IysConsentRecoveryJob Job(LicenseDbContext db)
        => new(db, NullLogger<IysConsentRecoveryJob>.Instance);

    private static IysConsent Row(
        IysPushState state, DateTimeOffset updatedAt, DateTimeOffset? deadline)
        => new()
        {
            Id = Guid.NewGuid(),
            BrandCode = "731734",
            Recipient = "+905551234567",
            Status = IysConsentStatus.Onay,
            ConsentDate = updatedAt,
            LastLocalEventAt = updatedAt,
            PushState = state,
            PushDeadline = deadline,
            LastError = "timeout",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };

    [Fact]
    public async Task Bekleme_gecmis_Failed_kayit_Pending_e_doner()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        var row = Row(IysPushState.Failed, now - TimeSpan.FromHours(1), now.AddDays(2));
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        var after = await db.IysConsents.SingleAsync();
        after.PushState.Should().Be(IysPushState.Pending);
        after.LastError.Should().BeNull("yeniden denemeye temiz sayfayla girilir");
    }

    [Fact]
    public async Task Yeni_dusmus_Failed_kayit_hemen_geri_alinmaz()
    {
        // Bekleme olmadan sıcak döngü kurardık: push düşer, recovery anında
        // geri alır, push yine düşer — Netgsm kotası dakikalar içinde biter.
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(IysPushState.Failed, now, now.AddDays(2)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Failed);
    }

    [Fact]
    public async Task Son_tarihi_gecmis_Failed_kayit_Expired_olur()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(
            IysPushState.Failed, now - TimeSpan.FromDays(4), now - TimeSpan.FromHours(1)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        var after = await db.IysConsents.SingleAsync();
        after.PushState.Should().Be(IysPushState.Expired,
            "3 iş günü dolduktan sonra yeniden denemek H467'den başka bir şey getirmez");
    }

    [Fact]
    public async Task Confirmed_kayda_dokunulmaz()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        var row = Row(IysPushState.Confirmed, now - TimeSpan.FromDays(9), now.AddDays(-5));
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();

        await Job(db).RunAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Confirmed);
    }

    [Fact]
    public async Task Son_tarihi_yaklasan_dogrulanmamis_kayit_sayilir()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = NewDb();
        db.IysConsents.Add(Row(
            IysPushState.Pushed, now - TimeSpan.FromHours(2), now.AddHours(6)));
        db.IysConsents.Add(Row(
            IysPushState.Pushed, now - TimeSpan.FromHours(2), now.AddDays(2)));
        await db.SaveChangesAsync();

        (await Job(db).RunAsync()).Should().Be(1,
            "yalnız 24 saatten az kalmış ve onaylanmamış kayıt uyarı sayılır");
    }
}
