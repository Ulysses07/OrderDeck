using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

/// <summary>
/// R10-S03: OTP üretiminde kontrol (cooldown/kota sorguları) ile yazma
/// (SaveChanges) ayrı adımlar — iki eşzamanlı üretim ikisi de kontrollerden
/// geçip iki geçerli kod ekleyebiliyordu (tek cooldown penceresinde çift SMS).
/// Bariyer-interceptor iki üretimi de kontrol sorgularından geçirip
/// SaveChanges anında buluşturur: yarış penceresi determinist hâle gelir.
/// Beklenen: yalnız BİRİ kod alır (Shopper.LastResetCodeIssuedAt
/// eşzamanlılık jetonu kaybedeni düşürür).
///
/// SINIR: InMemory sağlayıcı transaksiyonsuz — kaybedenin Added kod satırı,
/// Shopper güncellemesi fırlatmadan ÖNCE store'a uygulanmış oluyor. Gerçek
/// SQL'de SaveChanges tek transaction, kaybedenin TÜM batch'i geri alınır;
/// "DB'de tek satır kalır" kanıtı bu yüzden Testcontainers aynasında
/// (PasswordResetCodeIssueConcurrencyTests). Burada kanıtlanan: EF jeton
/// semantiği + yalnız bir çağıranın kod (→ SMS) alması + kaybeden context'in
/// temiz kalması.
/// </summary>
public class PasswordResetCodeIssueRaceTests
{
    private static readonly PasswordHasher Hasher = new();

    [Fact]
    public async Task Eszamanli_iki_uretimden_yalniz_biri_kod_alir()
    {
        // Aynı InMemory store'u gören iki bağımsız context (iki paralel istek).
        var root = new InMemoryDatabaseRoot();
        var storeName = Guid.NewGuid().ToString();
        var rendezvous = new IssueRendezvous();

        using var seedDb = NewDb(storeName, root, interceptor: null);
        var shopperId = await SeedShopperAsync(seedDb);

        using var db1 = NewDb(storeName, root, new ResetCodeSaveBarrier(rendezvous));
        using var db2 = NewDb(storeName, root, new ResetCodeSaveBarrier(rendezvous));
        var svc1 = NewService(db1);
        var svc2 = NewService(db2);

        // Controller'lar gibi: her istek shopper'ı KENDİ context'inden izlenir yükler.
        var shopper1 = await db1.Shoppers.SingleAsync(s => s.Id == shopperId);
        var shopper2 = await db2.Shoppers.SingleAsync(s => s.Id == shopperId);

        var t1 = svc1.IssueWithHandleAsync(shopper1, "1.2.3.4");
        var t2 = svc2.IssueWithHandleAsync(shopper2, "5.6.7.8");
        var results = await Task.WhenAll(t1, t2);

        // Bugünkü kodda ikisi de kod alır (iddia kırmızı); jetonla yalnız biri.
        results.Count(r => r is not null).Should().Be(1,
            "aynı shopper için eşzamanlı iki üretimden yalnız biri kod almalı");

        // Kazananın satırı store'da olmalı. Satır SAYISI burada iddia
        // edilmiyor — InMemory transaksiyonsuz, kaybedenin satırı sızabilir;
        // gerçek rollback kanıtı Testcontainers aynasında (bkz. sınıf yorumu).
        var winner = results.Single(r => r is not null)!;
        using var verifyDb = NewDb(storeName, root, interceptor: null);
        (await verifyDb.ShopperPasswordResetCodes.AnyAsync(c => c.Id == winner.Id))
            .Should().BeTrue("kazananın kod satırı kalıcı olmalı");
    }

    [Fact]
    public async Task Kaybeden_context_saglikli_kalir_sonraki_uretim_calisir()
    {
        // Kaybedenin temizliği (Added satırı detach + shopper reload) sonrası
        // aynı scoped context bir SONRAKİ kaydı sorunsuz yapabilmeli —
        // PanelSupportRequestsController başarıda talep kapatıp SaveChanges
        // çağırıyor; kirli ChangeTracker orada patlardı.
        var root = new InMemoryDatabaseRoot();
        var storeName = Guid.NewGuid().ToString();
        var rendezvous = new IssueRendezvous();

        using var seedDb = NewDb(storeName, root, interceptor: null);
        var shopperId = await SeedShopperAsync(seedDb);

        using var db1 = NewDb(storeName, root, new ResetCodeSaveBarrier(rendezvous));
        using var db2 = NewDb(storeName, root, new ResetCodeSaveBarrier(rendezvous));
        var svc1 = NewService(db1);
        var svc2 = NewService(db2);
        var shopper1 = await db1.Shoppers.SingleAsync(s => s.Id == shopperId);
        var shopper2 = await db2.Shoppers.SingleAsync(s => s.Id == shopperId);

        var results = await Task.WhenAll(
            svc1.IssueWithHandleAsync(shopper1, "1.2.3.4"),
            svc2.IssueWithHandleAsync(shopper2, "5.6.7.8"));
        results.Count(r => r is not null).Should().Be(1);

        // Kaybeden context'te ilgisiz bir yazma daha yap (bariyer artık dolu,
        // beklemez): ChangeTracker temiz kalmışsa sorunsuz geçer.
        var loserDb = results[0] is null ? db1 : db2;
        var tracked = await loserDb.Shoppers.SingleAsync(s => s.Id == shopperId);
        tracked.UpdatedAt = DateTimeOffset.UtcNow;
        var act = () => loserDb.SaveChangesAsync();
        await act.Should().NotThrowAsync(
            "kaybedenin context'i temizlik sonrası sağlıklı kalmalı");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LicenseDbContext NewDb(
        string storeName, InMemoryDatabaseRoot root, IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(storeName, root);
        if (interceptor is not null)
            opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private static PasswordResetCodeService NewService(LicenseDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new PasswordResetCodeService(db, Hasher, config,
            NullLogger<PasswordResetCodeService>.Instance);
    }

    private static async Task<Guid> SeedShopperAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "S",
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = Hasher.Hash("Password1!"),
            Address = "A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return shopper.Id;
    }

    /// <summary>
    /// İki katılımcılı bariyer: ikisi de gelene dek bekletir. İkisi geldikten
    /// sonra kapı açık kalır (kaybedenin retry/sonraki kayıtları beklemesin).
    /// </summary>
    private sealed class IssueRendezvous
    {
        private readonly TaskCompletionSource<bool> _both =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) >= 2)
                _both.TrySetResult(true);
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>
    /// Yeni ShopperPasswordResetCode ekleyen SaveChanges'ı bariyerde tutar:
    /// iki üretim de kontrol sorgularını (cooldown/kota) satırlar yokken
    /// geçmiş olur, yazma anında yarışırlar.
    /// </summary>
    private sealed class ResetCodeSaveBarrier : SaveChangesInterceptor
    {
        private readonly IssueRendezvous _rendezvous;
        public ResetCodeSaveBarrier(IssueRendezvous rendezvous)
            => _rendezvous = rendezvous;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker
                    .Entries<ShopperPasswordResetCode>()
                    .Any(e => e.State == EntityState.Added))
            {
                await _rendezvous.ArriveAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
