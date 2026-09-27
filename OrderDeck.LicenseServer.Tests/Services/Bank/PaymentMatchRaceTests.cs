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

    /// <summary>Aday seçim sorgusu verilen hareketi okurken (satır henüz izlenerek okunmadan) öteki bağlamın yazısını
    /// koşturur; bir kez.</summary>
    private sealed class OnMaterialize(Guid transactionId, Action interleave) : IMaterializationInterceptor
    {
        public int Fired { get; private set; }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (Fired == 0 && entity is BankTransaction t && t.Id == transactionId)
            {
                Fired++;
                interleave();
            }
            return entity;
        }
    }

    internal sealed class LogRecorder<T> : ILogger<T>
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

    private async Task<Guid> OtherCustomerAsync(Guid lic)
    {
        await using var db = Ctx();
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "mehmet_k", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
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
    public async Task Bagdastirici_yeniden_denemede_arada_baska_musteriye_verilen_elle_karari_ezmez()
    {
        // Onay işi hareketi okuduktan sonra admin onu elle başka müşteriye verir. Jeton işin kaydını reddeder; yeniden
        // denemenin aday seçimi o hareketi artık saymaz (başka müşteriye verilmiş karar): dekont gap'e düşer.
        var s = await SeedAsync();
        var other = await OtherCustomerAsync(s.LicenseId);
        var payment = await ApprovedAsync(s, s.Tx.Amount);
        await using var admin = Ctx();
        var race = new BeforeSave(LinksPayment,
            () => Recon(admin).ManualMatchAsync(s.LicenseId, s.Tx.Id, other, CancellationToken.None));
        await using var job = Ctx(race);
        var log = new LogRecorder<PaymentMatchReconciler>();

        await Recon(job, log).ReconcileApprovalAsync(payment, CancellationToken.None);

        race.Fired.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("yeniden deneniyor"));
        var row = await RowAsync(s.Tx.Id);
        row.ActualWpfCustomerId.Should().Be(other, "admin'in kararı korunur");
        row.PaymentId.Should().BeNull();
        row.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        await using var verify = Ctx();
        (await verify.PaymentMatchGaps.AsNoTracking().SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.NoCandidate);
        NoPendingWrites(job);
    }

    [Fact]
    public async Task Bagdastirici_secimden_sonra_baska_musteriye_verilen_adaya_dekont_eklemez()
    {
        // Aday seçimi satırı izlemeden okur; admin'in elle kararı seçimle satırın izlenerek okunması arasına düşer. Jeton
        // bu pencereyi görmez (satır karardan SONRA okundu): bağdaştırıcı satırı yeniden denetler, seçimi bayat sayar ve
        // taze seçimle yeniden dener.
        var s = await SeedAsync();
        var other = await OtherCustomerAsync(s.LicenseId);
        var payment = await ApprovedAsync(s, s.Tx.Amount);
        var race = new OnMaterialize(s.Tx.Id, () =>
        {
            // Admin'in elle kararının yazdığı satır. Materyalizasyon kancası eşzamanlıdır: yazı doğrudan, eşzamanlı yapılır.
            using var admin = Ctx();
            var m = admin.PaymentMatches.Single(x => x.BankTransactionId == s.Tx.Id);
            var now = DateTimeOffset.UtcNow;
            m.ActualWpfCustomerId = other; m.DecidedAt = now; m.UpdatedAt = now; m.Status = PaymentMatchStatus.ManualOnly;
            admin.SaveChanges();
        });
        await using var job = Ctx(race);

        await Recon(job).ReconcileApprovalAsync(payment, CancellationToken.None);

        race.Fired.Should().Be(1);
        var row = await RowAsync(s.Tx.Id);
        row.ActualWpfCustomerId.Should().Be(other);
        row.PaymentId.Should().BeNull("başka müşteriye verilmiş karara bu dekont eklenmez");
        await using var verify = Ctx();
        (await verify.PaymentMatchGaps.AsNoTracking().SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.NoCandidate);
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
    public async Task Kaldirma_arada_eklenen_dekont_bagini_gormeden_kaldirmaz_admin_mesajiyla_duser()
    {
        // Admin elle eşlemeyi kaldırırken aynı müşterinin dekont onayı satıra dekontu ekler (eşzamanlı insan kararı). Jeton
        // kaldırmanın kaydını reddeder; yeniden deneme taze satırda ilk denemenin görmediği dekont bağını bulur. Admin'in
        // görmediği bağı kaldırıp dekontu ölçümden düşürmek yerine vazgeçer: admin sayfayı yenileyip yeniden karar verir.
        var s = await SeedAsync();
        var payment = await ApprovedAsync(s, s.Tx.Amount);
        await using (var first = Ctx())
            await Recon(first).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None);
        await using var other = Ctx();
        var race = new BeforeSave(ModifiesMatch, () => Recon(other).ReconcileApprovalAsync(payment, CancellationToken.None));
        await using var admin = Ctx(race);
        var log = new LogRecorder<PaymentMatchReconciler>();

        var act = () => Recon(admin, log).UnmatchAsync(s.LicenseId, s.Tx.Id, CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>().WithMessage(PaymentMatchReconciler.ConflictMessage);
        race.Fired.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("yeniden deneniyor"), "onay satırı kaldırmanın okumasından sonra değiştirdi");
        var row = await RowAsync(s.Tx.Id);
        row.PaymentId.Should().Be(payment.Id, "admin'in görmediği dekont bağı kaldırılmaz");
        row.ActualWpfCustomerId.Should().Be(s.WpfCustomerId);
        row.DecidedAt.Should().NotBeNull();
        row.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman, "elle verilen karar olduğu gibi kalır");
        await using var verify = Ctx();
        (await verify.PaymentMatchGaps.AsNoTracking().CountAsync()).Should().Be(0, "dekont bağlı kalır, gap'e düşmez");
        NoPendingWrites(admin);
    }

    [Fact]
    public async Task Kaldirma_karari_degistirmeyen_eszamanli_yaziyla_carpisinca_bir_kez_yeniden_uygular()
    {
        // Kanıt temizliği (saklama işi ya da KVKK silmesi) Evidence'ı boşaltıp jetonu ilerletir, insan kararına dokunmaz.
        // Kaldırmanın kaydı jetona takılır; taze satırdaki karar ilk denemenin gördüğüyle aynı: kaldırma yeniden uygulanır.
        var s = await SeedAsync();
        await using (var first = Ctx())
            await Recon(first).ManualMatchAsync(s.LicenseId, s.Tx.Id, s.WpfCustomerId, CancellationToken.None);
        var race = new BeforeSave(ModifiesMatch, async () =>
        {
            await using var cleanup = Ctx();
            var m = await cleanup.PaymentMatches.SingleAsync(x => x.BankTransactionId == s.Tx.Id);
            // Jeton kesin değişsin diye aynı tick'e düşmeden ilerletilir.
            m.Evidence = null; m.UpdatedAt = m.UpdatedAt.AddMilliseconds(1);
            await cleanup.SaveChangesAsync();
        });
        await using var admin = Ctx(race);
        var log = new LogRecorder<PaymentMatchReconciler>();

        await Recon(admin, log).UnmatchAsync(s.LicenseId, s.Tx.Id, CancellationToken.None);

        race.Fired.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("yeniden deneniyor"));
        var row = await RowAsync(s.Tx.Id);
        row.ActualWpfCustomerId.Should().BeNull();
        row.PaymentId.Should().BeNull();
        row.DecidedAt.Should().BeNull();
        row.Status.Should().Be(PaymentMatchStatus.Proposed, "insan kararı kalkınca öneri yeniden hesaplanır");
        NoPendingWrites(admin);
    }

    [Fact]
    public async Task Ayni_odemeyi_ikinci_harekete_baglayan_indeks_ihlali_zaten_bagli_sayilir()
    {
        // Aynı dekontun iki bağdaştırma koşusu (ör. Hangfire'ın çift teslimi) DB'yi farklı anlarda okur: biri yalnız s.Tx'i
        // görür; öteki arada gelen, gönderen adını taşıyan hareketi adla seçip bağlar. SQL Server'da filtreli PaymentId
        // indeksi ikinci bağı reddeder; InMemory indeksi uygulamaz, ret burada benzetilir (gerçeği:
        // PaymentMatchReconcilerConcurrencyTests). Kaybeden yeniden dener, dekontu bağlı bulur, sessizce çıkar.
        var s = await SeedAsync(amount: 450m);
        var payment = await ApprovedAsync(s, 450m);
        var named = Guid.Empty;
        await using var other = Ctx();
        var race = new BeforeSave(LinksPayment, async () =>
            {
                await using (var db = Ctx())
                {
                    named = Incoming(db, s.LicenseId, 450m, "HAVALE AYSE GUL").Id;
                    await db.SaveChangesAsync();
                }
                await Recon(other).ReconcileApprovalAsync(payment, CancellationToken.None);
            },
            thenThrow: () => new DbUpdateException("tekil indeks ihlali (PaymentId) benzetimi"));
        await using var job = Ctx(race);

        var act = () => Recon(job).ReconcileApprovalAsync(payment, CancellationToken.None);

        await act.Should().NotThrowAsync();
        race.Fired.Should().Be(1);
        (await RowAsync(named)).PaymentId.Should().Be(payment.Id, "öteki koşu adla daraltıp bunu seçti");
        (await RowAsync(s.Tx.Id)).PaymentId.Should().BeNull("bir dekont iki harekete bağlanamaz");
        await using var verify = Ctx();
        (await verify.PaymentMatchGaps.AsNoTracking().CountAsync()).Should().Be(0, "kaybeden dekontu bağlı bulur, gap yazmaz");
        NoPendingWrites(job);
    }
}
