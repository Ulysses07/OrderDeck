using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>
/// Gölge eşleşme satırında yeniden hesap (çekim sink'i, telafi taraması) ile insan kararının (bağdaştırıcı, admin elle
/// eşleme) yarışı. İki bağlam tek InMemory deposunu paylaşır; InMemory eşzamanlılık jetonunu (PaymentMatch.UpdatedAt)
/// uygular. Yarış, birinci bağlamın SaveChanges'inden hemen önce öteki bağlamın yazısıyla kurulur: birincinin okuduğu
/// satır kaydetmeden önce değişmiş olur. Jeton olmasaydı birincinin yazısı ötekini sessizce ezerdi.
/// </summary>
public sealed class PaymentMatchRaceTests
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _name = $"race-{Guid.NewGuid():N}";

    /// <summary>Koşulu tutan SaveChanges'ten hemen önce öteki bağlamın yazısını koşturur (en çok <paramref name="times"/>
    /// kez); istenirse ardından bu kaydı düşürür.</summary>
    private sealed class BeforeSave(Func<DbContext, bool> when, Func<Task> interleave, int times = 1,
        Func<Exception>? thenThrow = null) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired < times && when(eventData.Context!))
            {
                Fired++;
                await interleave();
                if (thenThrow is not null) throw thenThrow();
            }
            return result;
        }
    }

    private sealed class LogRecorder<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static bool ModifiesMatch(DbContext db)
        => db.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified);

    private static bool LinksPayment(DbContext db)
        => db.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified && e.Entity.PaymentId != null);

    private LicenseDbContext Ctx(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(_name, _root);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static PaymentMatcher Matcher(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);

    private static PaymentMatchReconciler Recon(LicenseDbContext db, ILogger<PaymentMatchReconciler>? log = null)
        => new(db, Matcher(db), log ?? NullLogger<PaymentMatchReconciler>.Instance);

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid WpfCustomerId, BankTransaction Tx);

    /// <summary>Lisans, müşteri + shopper bağı, açıklamasında kullanıcı adı geçen gelen hareket ve onun Proposed önerisi.</summary>
    private async Task<Seed> SeedAsync(decimal amount = 300m)
    {
        await using var db = Ctx();
        var lic = Guid.NewGuid(); var shopperId = Guid.NewGuid();
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink { Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", WpfCustomerId = wpf.Id, JoinedAt = DateTimeOffset.UtcNow });
        var tx = Incoming(db, lic, amount, "HAVALE ayse_gul34");
        await db.SaveChangesAsync();
        (await Matcher(db).MatchAsync(tx, CancellationToken.None)).Status.Should().Be(PaymentMatchStatus.Proposed);
        return new Seed(lic, shopperId, wpf.Id, tx);
    }

    private static BankTransaction Incoming(LicenseDbContext db, Guid lic, decimal amount, string description)
    {
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = amount, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow.AddHours(-1),
            Description = description, FetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.BankTransactions.Add(tx);
        return tx;
    }

    private async Task<Payment> ApprovedAsync(Seed s, decimal amount)
    {
        await using var db = Ctx();
        var p = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = s.LicenseId, ShopperId = s.ShopperId, PayerName = "AYSE GUL", Amount = amount, PaidAt = s.Tx.OccurredAt,
            ReferansNo = $"r-{Guid.NewGuid():N}", Status = PaymentStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payments.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<PaymentMatch> RowAsync(Guid txId)
    {
        await using var db = Ctx();
        return await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == txId);
    }

    private static void NoPendingWrites(LicenseDbContext db)
        => db.ChangeTracker.Entries().Where(e => e.Entity is PaymentMatch or PaymentMatchGap or CustomerIbanMemory)
            .Should().NotContain(e => e.State != EntityState.Unchanged, "düşen deneme izleyicide iz bırakmaz");

    [Fact]
    public async Task Eslestirici_yeniden_hesabi_arada_verilen_insan_kararini_ezmez()
    {
        var s = await SeedAsync();
        await using var admin = Ctx();
        var race = new BeforeSave(ModifiesMatch,
            () => Recon(admin).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None));
        await using var sweep = Ctx(race);

        var result = await Matcher(sweep).MatchAsync(s.Tx, CancellationToken.None);

        race.Fired.Should().Be(1);
        result.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman, "taze satır insan kararıysa dokunulmadan döner");
        var row = await RowAsync(s.Tx.Id);
        row.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        row.ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        row.DecidedAt.Should().NotBeNull();
        NoPendingWrites(sweep);
    }

    [Fact]
    public async Task Eslestirici_eszamanli_yeniden_hesapla_carpisinca_taze_satirla_bir_kez_yeniden_dener()
    {
        var s = await SeedAsync();
        await using var other = Ctx();
        var race = new BeforeSave(ModifiesMatch, () => Matcher(other).MatchAsync(s.Tx, CancellationToken.None));
        await using var sweep = Ctx(race);

        var result = await Matcher(sweep).MatchAsync(s.Tx, CancellationToken.None);

        race.Fired.Should().Be(1);
        result.Status.Should().Be(PaymentMatchStatus.Proposed);
        (await RowAsync(s.Tx.Id)).UpdatedAt.Should().Be(result.UpdatedAt, "yeniden deneme taze satırın üstüne yazdı");
        NoPendingWrites(sweep);
    }

    [Fact]
    public async Task Bagdastirici_yeniden_hesapla_carpisinca_karari_taze_satira_yeniden_uygular()
    {
        var s = await SeedAsync();
        var payment = await ApprovedAsync(s, s.Tx.Amount);
        await using var sweep = Ctx();
        var race = new BeforeSave(ModifiesMatch, () => Matcher(sweep).MatchAsync(s.Tx, CancellationToken.None));
        await using var job = Ctx(race);

        await Recon(job).ReconcileApprovalAsync(payment, CancellationToken.None);

        race.Fired.Should().Be(1);
        var row = await RowAsync(s.Tx.Id);
        row.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        row.PaymentId.Should().Be(payment.Id);
        NoPendingWrites(job);
    }

    [Fact]
    public async Task Bagdastirici_iki_carpismada_vazgecer_uyari_loglar_iz_birakmaz()
    {
        var s = await SeedAsync();
        var payment = await ApprovedAsync(s, s.Tx.Amount);
        await using var sweep = Ctx();
        var race = new BeforeSave(ModifiesMatch, () => Matcher(sweep).MatchAsync(s.Tx, CancellationToken.None), times: 2);
        await using var job = Ctx(race);
        var log = new LogRecorder<PaymentMatchReconciler>();

        var act = () => Recon(job, log).ReconcileApprovalAsync(payment, CancellationToken.None);

        await act.Should().NotThrowAsync("iş yolu vazgeçer, Hangfire'ı yeniden denemeye zorlamaz");
        race.Fired.Should().Be(2);
        var row = await RowAsync(s.Tx.Id);
        row.Status.Should().Be(PaymentMatchStatus.Proposed);
        row.PaymentId.Should().BeNull();
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(payment.Id.ToString()));
        NoPendingWrites(job);
    }

    [Fact]
    public async Task Elle_esleme_iki_carpismada_admin_mesajiyla_duser()
    {
        var s = await SeedAsync();
        await using var sweep = Ctx();
        var race = new BeforeSave(ModifiesMatch, () => Matcher(sweep).MatchAsync(s.Tx, CancellationToken.None), times: 2);
        await using var admin = Ctx(race);

        var act = () => Recon(admin).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.ConflictMessage);
        (await RowAsync(s.Tx.Id)).DecidedAt.Should().BeNull();
        NoPendingWrites(admin);
    }

    [Fact]
    public async Task Kaldirma_eszamanli_insan_karariyla_carpisinca_bir_kez_yeniden_dener()
    {
        var s = await SeedAsync();
        await using (var first = Ctx())
            await Recon(first).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None);
        await using var other = Ctx();
        var race = new BeforeSave(ModifiesMatch,
            () => Recon(other).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None));
        await using var admin = Ctx(race);

        await Recon(admin).UnmatchAsync(s.LicenseId, s.Tx.Id, CancellationToken.None);

        race.Fired.Should().Be(1);
        var row = await RowAsync(s.Tx.Id);
        row.ActualWpfCustomerId.Should().BeNull();
        row.DecidedAt.Should().BeNull();
        row.Status.Should().Be(PaymentMatchStatus.Proposed, "insan kararı kalkınca öneri yeniden hesaplanır");
        NoPendingWrites(admin);
    }

    [Fact]
    public async Task Ayni_odemeyi_ikinci_harekete_baglayan_indeks_ihlali_zaten_bagli_sayilir()
    {
        // Tek açık gap'i iki aynı tutarlı hareket aynı anda çözmeye çalışır (sink + telafi taraması). SQL Server'da filtreli
        // PaymentId indeksi ikinci bağı reddeder; InMemory indeksi uygulamaz, ret burada benzetilir (gerçeği:
        // PaymentMatchReconcilerConcurrencyTests). Kaybeden yeniden dener, gap'i çözülmüş bulur, sessizce çıkar.
        var s = await SeedAsync(amount: 450m);
        var payment = await ApprovedAsync(s, 450m);
        BankTransaction second;
        await using (var db = Ctx())
        {
            // Onay anında hareket yoktu (NoCandidate); iki hareket sonradan geldi.
            db.PaymentMatchGaps.Add(new PaymentMatchGap
            {
                Id = Guid.NewGuid(), LicenseId = s.LicenseId, PaymentId = payment.Id, Reason = PaymentMatchGapReason.NoCandidate,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            second = Incoming(db, s.LicenseId, 450m, "EFT GELEN");
            await db.SaveChangesAsync();
            await Matcher(db).MatchAsync(second, CancellationToken.None);
        }
        await using var sink = Ctx();
        var race = new BeforeSave(LinksPayment, () => Recon(sink).TryResolveGapAsync(s.Tx, CancellationToken.None),
            thenThrow: () => new DbUpdateException("tekil indeks ihlali (PaymentId) benzetimi"));
        await using var sweep = Ctx(race);

        var act = () => Recon(sweep).TryResolveGapAsync(second, CancellationToken.None);

        await act.Should().NotThrowAsync();
        race.Fired.Should().Be(1);
        (await RowAsync(s.Tx.Id)).PaymentId.Should().Be(payment.Id);
        (await RowAsync(second.Id)).PaymentId.Should().BeNull("bir dekont iki harekete bağlanamaz");
        await using var verify = Ctx();
        (await verify.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedBankTransactionId.Should().Be(s.Tx.Id);
        NoPendingWrites(sweep);
    }
}
