using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankMatchSweepJobTests
{
    /// <summary>Her SaveChanges'te izlenen satır sayısını kaydeder; istenirse belirli bir hareketin (ya da her hareketin)
    /// önerisini düşürür, ya da başarılı kayıttan sonra işin token'ını iptal eder.</summary>
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public List<int> TrackedPerSave { get; } = new();
        public Guid? FailFor { get; set; }
        public bool FailAll { get; set; }
        public CancellationTokenSource? CancelAfterSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            TrackedPerSave.Add(tracker.Entries().Count());
            if (FailAll || (FailFor is { } id && tracker.Entries<PaymentMatch>().Any(e => e.Entity.BankTransactionId == id)))
                throw new InvalidOperationException("db gitti");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            CancelAfterSave?.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Günlük kayıtlarını (düzey + biçimlenmiş metin + istisna) toplar.</summary>
    private sealed class LogRecorder : ILogger<BankMatchSweepJob>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public List<(LogLevel Level, string Message, Exception? Exception)> Errors
            => Entries.Where(e => e.Level >= LogLevel.Error).ToList();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"sweep-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    /// <summary>Üretimdeki gibi: iş, bağdaştırıcı ve eşleştirici aynı kapsamlı bağlamı paylaşır.</summary>
    private static BankMatchSweepJob Job(LicenseDbContext db, ILogger<BankMatchSweepJob>? log = null)
        => new(db, new PaymentMatchReconciler(db,
                new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
                Options.Create(new BankOptions()), NullLogger<PaymentMatchReconciler>.Instance),
            log ?? NullLogger<BankMatchSweepJob>.Instance);

    /// <summary>Obifin bağlantısı olan bir lisans (kaydetmez): tarama yalnız bunların hareketlerine bakar. Kimlik alanı
    /// üretilir.</summary>
    private static Guid ConnectedLicense(LicenseDbContext db)
    {
        var lic = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection
        {
            Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://obifin.invalid", UserCode = $"u-{Guid.NewGuid():N}",
            Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        return lic;
    }

    /// <param name="fetchedAgo">Çekimden bu yana geçen süre; verilmezse <paramref name="age"/>.</param>
    private static BankTransaction Tx(Guid lic, string description, TimeSpan age,
        BankTransactionDirection direction = BankTransactionDirection.Incoming, decimal amount = 100m, string code = "FT",
        TimeSpan? fetchedAgo = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = direction, Amount = amount, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow - age,
        Description = description, TransactionCode = code, FetchedAt = DateTimeOffset.UtcNow - (fetchedAgo ?? age),
    };

    [Fact]
    public async Task Eslesmesi_olmayan_gelen_hareket_eslestirilir_giden_sifir_ve_eski_hareket_atlanir()
    {
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow };
        var named = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(2));
        var noSignal = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(29));
        var outgoing = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(1), BankTransactionDirection.Outgoing);
        var zero = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(1), amount: 0m);
        var old = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(BankMatchSweepJob.LookbackDays + 1));
        db.WpfCustomerProjections.Add(customer);
        db.BankTransactions.AddRange(named, noSignal, outgoing, zero, old);
        await db.SaveChangesAsync();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(2);
        var matches = await db.PaymentMatches.AsNoTracking().ToListAsync();
        matches.Select(m => m.BankTransactionId).Should().BeEquivalentTo(new[] { named.Id, noSignal.Id });
        matches.Single(m => m.BankTransactionId == named.Id).ProposedWpfCustomerId.Should().Be(customer.Id);
        matches.Single(m => m.BankTransactionId == noSignal.Id).Status.Should().Be(PaymentMatchStatus.NoProposal);
    }

    [Fact]
    public async Task Eslesmesi_olan_harekete_dokunulmaz()
    {
        using var db = NewDb(); var lic = ConnectedLicense(db);
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow });
        var tx = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(1));
        var stamp = DateTimeOffset.UtcNow.AddDays(-1);
        // Eşleştirici bugün başka bir sonuç üretirdi (kullanıcı adı var): tarama yine de dokunmamalı.
        var existing = new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, Status = PaymentMatchStatus.NoProposal,
            Layer = PaymentMatchLayer.None, Evidence = "no-signal", CreatedAt = stamp, UpdatedAt = stamp,
        };
        db.BankTransactions.Add(tx); db.PaymentMatches.Add(existing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(0);
        var after = await db.PaymentMatches.AsNoTracking().SingleAsync();
        after.Status.Should().Be(PaymentMatchStatus.NoProposal);
        after.Evidence.Should().Be("no-signal");
        after.UpdatedAt.Should().Be(stamp);
    }

    [Fact]
    public async Task Tek_kosu_en_fazla_500_hareket_isler_en_eskiden_baslar_izleyiciyi_50de_bir_bosaltir()
    {
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = ConnectedLicense(db);
        var txs = Enumerable.Range(0, BankMatchSweepJob.MaxPerRun + 1)
            .Select(i => Tx(lic, "HAVALE siparis", TimeSpan.FromDays(20) - TimeSpan.FromMinutes(i)))
            .ToList();
        db.BankTransactions.AddRange(txs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        probe.TrackedPerSave.Clear();

        var first = await Job(db).RunAsync(CancellationToken.None);

        first.Should().Be(BankMatchSweepJob.MaxPerRun);
        var matched = await db.PaymentMatches.AsNoTracking().Select(m => m.BankTransactionId).ToListAsync();
        matched.Should().NotContain(txs[^1].Id, "en yeni hareket sıradaki koşuya kalır");
        probe.TrackedPerSave.Max().Should().BeLessThanOrEqualTo(BankMatchSweepJob.TrackerClearInterval,
            "öneriler koşu boyunca izleyicide birikmez");

        var second = await Job(db).RunAsync(CancellationToken.None);

        second.Should().Be(1);
        (await db.PaymentMatches.CountAsync()).Should().Be(BankMatchSweepJob.MaxPerRun + 1);
    }

    [Fact]
    public async Task Dislanan_hareket_eslesme_alir_sonraki_kosuda_yeniden_taranmaz()
    {
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var pos = Tx(lic, "POS TAHSILAT", TimeSpan.FromDays(3), code: "CCP");
        db.BankTransactions.Add(pos);
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(1);
        var match = await db.PaymentMatches.AsNoTracking().SingleAsync();
        match.Status.Should().Be(PaymentMatchStatus.NoProposal);
        match.Evidence.Should().Be("excluded:CCP");

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(0);
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(match.UpdatedAt);
    }

    [Fact]
    public async Task Dusen_hareket_digerlerini_bekletmez_kosu_sonda_hata_verir()
    {
        // En eskiden başlanır: kalıcı düşen tek bir hareket her koşunun başında durup arkasındakileri aç bırakmasın.
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = ConnectedLicense(db);
        var poisoned = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(10));
        var others = new[] { Tx(lic, "HAVALE siparis", TimeSpan.FromDays(5)), Tx(lic, "HAVALE siparis", TimeSpan.FromDays(4)) };
        db.BankTransactions.Add(poisoned); db.BankTransactions.AddRange(others);
        await db.SaveChangesAsync();
        probe.FailFor = poisoned.Id;
        var log = new LogRecorder();

        var act = () => Job(db, log).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AggregateException>();
        var matched = await db.PaymentMatches.AsNoTracking().Select(m => m.BankTransactionId).ToListAsync();
        matched.Should().BeEquivalentTo(others.Select(t => t.Id), "düşen hareket eşleşmesiz kalır, sonraki koşu yeniden dener");
        var (_, message, exception) = log.Errors.Should().ContainSingle().Subject;
        exception.Should().NotBeNull();
        message.Should().Contain(poisoned.Id.ToString());
        // Kimlik çıkarılınca kalan metinde açıklama da tutar da yok (Guid'in hex'i "100" içerebilir, önce o atılır).
        message.Replace(poisoned.Id.ToString(), "").Should().NotContainAny("HAVALE", "siparis", "100");
    }

    [Fact]
    public async Task Her_hareket_duserse_ilk_hatalar_raporlanir_art_arda_hatada_kosu_kesilir()
    {
        // Eşleştiricideki bir hata ya da DB kesintisi her hareketi düşürür: yüzlerce Error satırı ve yüzlerce iç istisnalı
        // tek bir AggregateException yerine ilk birkaçı raporlanır; art arda hata zehirli satır değil kesintidir.
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = ConnectedLicense(db);
        db.BankTransactions.AddRange(Enumerable.Range(0, BankMatchSweepJob.ConsecutiveFailureLimit + 5)
            .Select(i => Tx(lic, "HAVALE siparis", TimeSpan.FromDays(3) - TimeSpan.FromMinutes(i))));
        await db.SaveChangesAsync();
        probe.TrackedPerSave.Clear();
        probe.FailAll = true;
        var log = new LogRecorder();

        var act = () => Job(db, log).RunAsync(CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(BankMatchSweepJob.MaxReportedFailures);
        thrown.Message.Should().Contain($"{BankMatchSweepJob.ConsecutiveFailureLimit} harekette düştü");
        probe.TrackedPerSave.Should().HaveCount(BankMatchSweepJob.ConsecutiveFailureLimit, "kesintide kalan hareketler denenmez");
        log.Errors.Should().HaveCount(BankMatchSweepJob.MaxReportedFailures).And.OnlyContain(e => e.Exception != null);
    }

    [Fact]
    public async Task Isin_kendi_iptali_dongu_ortasinda_hemen_yukari_cikar_hata_sayilmaz()
    {
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = ConnectedLicense(db);
        db.BankTransactions.AddRange(Enumerable.Range(0, 3)
            .Select(i => Tx(lic, "HAVALE siparis", TimeSpan.FromDays(3) - TimeSpan.FromMinutes(i))));
        await db.SaveChangesAsync();
        using var cts = new CancellationTokenSource();
        probe.CancelAfterSave = cts;   // ilk hareketin önerisi kaydedilince iş iptal edilir
        var log = new LogRecorder();

        var act = () => Job(db, log).RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("iptal toplanmaz, AggregateException'a gömülmez");
        (await db.PaymentMatches.AsNoTracking().CountAsync()).Should().Be(1, "iptalden sonra sıradaki harekete geçilmez");
        log.Errors.Should().BeEmpty("işin kendi iptali hata değildir");
    }

    [Fact]
    public async Task Tarama_eslestirdigi_hareketle_acik_gap_i_cozer()
    {
        // Sink'in kaçırdığı hareketi tarama eşleştirir; o hareketi bekleyen onaylı dekontun gap'i de çözülmeli, yoksa
        // taramanın eşleştirdiği hareket bir daha taranmadığı için gap sonsuza dek açık kalırdı.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow };
        var shopperId = Guid.NewGuid();
        var tx = Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(1));
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = lic, ShopperId = shopperId, PayerName = "ORNEK MUSTERI", Amount = tx.Amount,
            PaidAt = tx.OccurredAt.AddHours(-2), ReferansNo = $"r-{Guid.NewGuid():N}", Status = PaymentStatus.Approved,
            ApprovedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WpfCustomerProjections.Add(customer);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34",
            WpfCustomerId = customer.Id, JoinedAt = DateTimeOffset.UtcNow,
        });
        db.BankTransactions.Add(tx);
        db.Payments.Add(payment);
        db.PaymentMatchGaps.Add(new PaymentMatchGap
        {
            Id = Guid.NewGuid(), LicenseId = lic, PaymentId = payment.Id, Reason = PaymentMatchGapReason.NoCandidate,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(1);

        var match = await db.PaymentMatches.AsNoTracking().SingleAsync();
        match.PaymentId.Should().Be(payment.Id);
        match.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        (await db.PaymentMatchGaps.AsNoTracking().SingleAsync()).ResolvedBankTransactionId.Should().Be(tx.Id);
    }

    [Fact]
    public async Task Obifin_baglantisi_olmayan_lisansin_hareketi_taranmaz()
    {
        using var db = NewDb();
        var connected = ConnectedLicense(db);
        var mine = Tx(connected, "HAVALE siparis", TimeSpan.FromDays(2));
        var foreign = Tx(Guid.NewGuid(), "HAVALE siparis", TimeSpan.FromDays(2));
        db.BankTransactions.AddRange(mine, foreign);
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(1);

        (await db.PaymentMatches.AsNoTracking().SingleAsync()).BankTransactionId.Should().Be(mine.Id);
    }

    [Fact]
    public async Task Yeni_cekilen_hareket_tampon_suresi_dolmadan_taranmaz()
    {
        // Uzun bir çekim sürerken kaydedilmiş, henüz sink'e verilmemiş hareketi tarama kapmasın.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var fresh = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(1), fetchedAgo: TimeSpan.FromMinutes(1));
        var settled = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(1),
            fetchedAgo: BankMatchSweepJob.FetchGrace + TimeSpan.FromMinutes(1));
        db.BankTransactions.AddRange(fresh, settled);
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(1);

        (await db.PaymentMatches.AsNoTracking().SingleAsync()).BankTransactionId.Should().Be(settled.Id);
    }

    [Theory]
    [InlineData(ObifinConnectionStatus.Disabled, false)]
    [InlineData(ObifinConnectionStatus.Unverified, true)]
    public async Task Durdurulmus_ya_da_verisi_silinmis_baglantinin_hareketi_taranmaz(ObifinConnectionStatus status, bool purged)
    {
        // KVKK K-6: telafi taraması durdurulmuş bağlantının saklı hareketini eşleştirmez (öneri, gap çözümü). İki koşul ayrı ayrı
        // sınanır (Unverified + işaret: elle düzeltilmiş satır).
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var conn = db.ObifinConnections.Local.Single(c => c.LicenseId == lic);
        conn.Status = status;
        if (purged) conn.BankDataPurgedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube",
            Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow });
        db.BankTransactions.Add(Tx(lic, "HAVALE ornek_musteri34", TimeSpan.FromDays(2)));
        await db.SaveChangesAsync();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(0);
        (await db.PaymentMatches.CountAsync()).Should().Be(0);
    }

    // ---- 2. aşama: IBAN hafızası değişince hesaplanmış kararsız satırlar (27.10) ----

    /// <summary>IBAN özetli, satırı (<paramref name="match"/>) olan hareket; kaydetmez. Özet <paramref name="hashValid"/> ise KVKK Ö-7
    /// kesmesinden sonra çekilmiş.</summary>
    private static BankTransaction HashedTx(LicenseDbContext db, Guid lic, string hash, TimeSpan age, PaymentMatch match,
        bool hashValid = true, string description = "EFT GELEN")
    {
        var tx = Tx(lic, description, age);
        tx.CounterpartyIbanHash = hash; tx.CounterpartyIbanHashValid = hashValid;
        match.Id = Guid.NewGuid(); match.LicenseId = lic; match.BankTransactionId = tx.Id;
        db.BankTransactions.Add(tx); db.PaymentMatches.Add(match);
        return tx;
    }

    private static PaymentMatch Undecided(DateTimeOffset computedAt, string evidence = "no-signal", Guid? proposed = null) => new()
    {
        Status = proposed is null ? PaymentMatchStatus.NoProposal : PaymentMatchStatus.Proposed, ProposedWpfCustomerId = proposed,
        Layer = proposed is null ? PaymentMatchLayer.None : PaymentMatchLayer.IbanMemory,
        Confidence = proposed is null ? 0m : PaymentMatcher.IbanConfidence, Evidence = evidence, CreatedAt = computedAt, UpdatedAt = computedAt,
    };

    private static PaymentMatch DecidedFor(Guid customer, DateTimeOffset at) => new()
    {
        Status = PaymentMatchStatus.ManualOnly, ActualWpfCustomerId = customer, DecidedAt = at, IbanConfirmedAt = at,
        Evidence = "no-signal", CreatedAt = at, UpdatedAt = at,
    };

    private static WpfCustomerProjection AddCustomer(LicenseDbContext db, Guid lic, string username, bool purged = false)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username,
            UpdatedAt = DateTimeOffset.UtcNow, PurgedAt = purged ? DateTimeOffset.UtcNow : null };
        db.WpfCustomerProjections.Add(c);
        return c;
    }

    private static CustomerIbanMemory Bond(LicenseDbContext db, Guid lic, Guid customer, string hash, DateTimeOffset createdAt, Guid? source)
    {
        var m = new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = customer, IbanHash = hash, IbanMasked = "TR..",
            LearnedFrom = IbanMemorySource.ManualMatch, SourceBankTransactionId = source, CreatedAt = createdAt, LastConfirmedAt = createdAt };
        db.CustomerIbanMemories.Add(m);
        return m;
    }

    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    [Fact]
    public async Task Bagdan_once_hesaplanmis_kararsiz_satir_taramada_IBAN_hafizasiyla_yeniden_degerlendirilir_ikinci_kosu_dokunmaz()
    {
        // Bugünkü (düzeltmeden önceki) elle eşlemelerin geriye dönük doldurması ve elle eşlemenin anlık yeniden değerlendirmesi
        // düştüğünde telafi: ayrı bir CLI ya da elle adım gerekmez. İdempotent: yeniden hesaplanan satır bağdan yenidir.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(2), DecidedFor(customer.Id, now.AddHours(-3)));
        Bond(db, lic, customer.Id, hash, now.AddHours(-3), source.Id);
        // Kesmeden önce çekilmiş (özeti HashValid=false) eski havale de: geçerli bağın özetine eşitlik aynı tam IBAN.
        var stale = HashedTx(db, lic, hash, TimeSpan.FromDays(8), Undecided(now.AddDays(-8)), hashValid: false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(0, "1. aşamanın sayısı: satırı olmayan hareket yok");

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == stale.Id);
        (row.Status, row.Layer, row.ProposedWpfCustomerId, row.Evidence).Should().Be((PaymentMatchStatus.Proposed,
            PaymentMatchLayer.IbanMemory, (Guid?)customer.Id, "iban-memory" + PaymentMatcher.ReevaluationEvidenceSuffix));
        (row.DecidedAt, row.ActualWpfCustomerId).Should().Be(((DateTimeOffset?)null, (Guid?)null));
        var decided = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == source.Id);
        decided.UpdatedAt.Should().Be(now.AddHours(-3), "insan kararına dokunulmaz");

        await Job(db).RunAsync(CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == stale.Id)).UpdatedAt
            .Should().Be(row.UpdatedAt, "ikinci koşu aynı satırı yeniden hesaplamaz");
    }

    [Fact]
    public async Task Gecerli_ozette_hic_gorunmeyen_bag_taramada_yayilmaz()
    {
        // Eski maskeli özetten öğrenilmiş olabilecek bağ ("TR12…345"in özeti farklı göndericilerde aynıdır): lisansta özeti geçerli
        // hiçbir harekette görünmüyorsa aynı maskeyi taşıyan başka göndericilere yayılmaz.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(20), DecidedFor(customer.Id, now.AddDays(-15)), hashValid: false);
        Bond(db, lic, customer.Id, hash, now.AddDays(-15), source.Id);
        var stale = HashedTx(db, lic, hash, TimeSpan.FromDays(18), Undecided(now.AddDays(-18)), hashValid: false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Job(db).RunAsync(CancellationToken.None);

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == stale.Id);
        (row.Status, row.UpdatedAt).Should().Be((PaymentMatchStatus.NoProposal, now.AddDays(-18)));
    }

    [Fact]
    public async Task Silinmis_bagdan_gelen_oneri_taramada_geri_alinir()
    {
        // Kaldırma bağı sildi ama anlık yeniden değerlendirme koşamadı (durdurulmuş bağlantı, düşen hesap): kanıtında
        // "iban-memory" geçen, bağı artık olmayan satır yeniden hesaplanır.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var now = DateTimeOffset.UtcNow;
        var orphan = HashedTx(db, lic, NewHash(), TimeSpan.FromDays(4), Undecided(now.AddDays(-1), "iban-memory", customer.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Job(db).RunAsync(CancellationToken.None);

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == orphan.Id);
        (row.Status, row.ProposedWpfCustomerId, row.Evidence).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, "no-signal" + PaymentMatcher.ReevaluationEvidenceSuffix));
    }

    [Fact]
    public async Task Silinmis_musteriye_taramada_oneri_yapilmaz()
    {
        // Eşleştirici silinmiş (PurgedAt) müşteriyi aday listesinden düşürür; hafızası henüz silinmemiş olsa da öneri olmaz.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var purged = AddCustomer(db, lic, "silinen_musteri", purged: true);
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(2), DecidedFor(purged.Id, now.AddHours(-3)));
        Bond(db, lic, purged.Id, hash, now.AddHours(-3), source.Id);
        var stale = HashedTx(db, lic, hash, TimeSpan.FromDays(6), Undecided(now.AddDays(-6)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Job(db).RunAsync(CancellationToken.None);

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == stale.Id);
        (row.Status, row.ProposedWpfCustomerId).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null));
    }

    [Fact]
    public async Task Bagdan_sonra_hesaplanmis_aciklamasi_silinmis_saklama_sinirindaki_ve_durdurulmus_baglantidaki_satira_tarama_dokunmaz()
    {
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(10), DecidedFor(customer.Id, now.AddDays(-9)));
        Bond(db, lic, customer.Id, hash, now.AddDays(-9), source.Id);
        // Bağdan sonra hesaplanmış: hafızayı zaten gördü (açıklamadaki kullanıcı adı çatıştı, öneri yok).
        var after = HashedTx(db, lic, hash, TimeSpan.FromDays(8), Undecided(now.AddDays(-8), "conflict:username=baska,iban-memory"));
        // Açıklaması silinmiş (saklama ya da kişi silmesi): kullanıcı adı katmanı kaybolur, öneri bozulurdu.
        var scrubbed = HashedTx(db, lic, hash, TimeSpan.FromDays(12), Undecided(now.AddDays(-12)));
        scrubbed.Description = null; scrubbed.DescriptionPurgedAt = now.AddDays(-1);
        // Bağdan önce hesaplanmış ama eşleşmeyen açıklamanın saklama sınırına bir günden yakın: saklama işi açıklamayı jetonu
        // ilerletmeden siler, yeniden hesap onunla çakışmasız yarışırdı (PaymentMatchReconciler.ReevaluationSince).
        var edge = HashedTx(db, lic, hash, TimeSpan.FromDays(BankMatchSweepJob.LookbackDays - 0.5), Undecided(now.AddDays(-20)));
        // Durdurulmuş bağlantının lisansı (KVKK K-6).
        var stopped = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = stopped, BaseUrl = "https://obifin.invalid",
            UserCode = $"u-{Guid.NewGuid():N}", Status = ObifinConnectionStatus.Disabled, DisabledAt = now, CreatedAt = now, UpdatedAt = now });
        var stoppedCustomer = AddCustomer(db, stopped, "ornek_musteri34");
        var stoppedSource = HashedTx(db, stopped, hash, TimeSpan.FromDays(3), DecidedFor(stoppedCustomer.Id, now.AddDays(-2)));
        Bond(db, stopped, stoppedCustomer.Id, hash, now.AddDays(-2), stoppedSource.Id);
        var stoppedStale = HashedTx(db, stopped, hash, TimeSpan.FromDays(5), Undecided(now.AddDays(-5)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.PaymentMatches.AsNoTracking().ToDictionaryAsync(m => m.BankTransactionId);

        await Job(db).RunAsync(CancellationToken.None);

        foreach (var tx in new[] { after, scrubbed, edge, stoppedStale, source })
            (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == tx.Id))
                .Should().BeEquivalentTo(before[tx.Id], o => o.Excluding(m => m.BankTransaction), tx.Id.ToString());
    }

    [Fact]
    public async Task Hesaplandiktan_sonra_dislanan_kodlu_hareket_taramada_dislama_kanitini_alir_bir_daha_secilmez()
    {
        // Dışlama kodları ayardır: kod listeye satır hesaplandıktan sonra girebilir. Bellekte atlansaydı satır her saat yeniden
        // seçilir, en yeniden sıralı kotada 30 gün yer kaplardı. Eşleştirici ona "excluded:KOD" yazar (önerisi düşer); sonraki
        // koşunun seçimi öneki eler.
        using var db = NewDb(); var lic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(2), DecidedFor(customer.Id, now.AddHours(-3)));
        Bond(db, lic, customer.Id, hash, now.AddHours(-3), source.Id);
        var pos = HashedTx(db, lic, hash, TimeSpan.FromDays(6), Undecided(now.AddDays(-6)));
        pos.TransactionCode = "CCP"; // varsayılan dışlama listesinde (POS tahsilatı)
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Job(db).RunAsync(CancellationToken.None);

        var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == pos.Id);
        (row.Status, row.ProposedWpfCustomerId, row.Evidence).Should().Be((PaymentMatchStatus.NoProposal, (Guid?)null, "excluded:CCP"));

        await Job(db).RunAsync(CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == pos.Id)).UpdatedAt
            .Should().Be(row.UpdatedAt, "ikinci koşu dışlanan satırı seçmez");
    }

    [Fact]
    public async Task Ikinci_asama_bagi_yalniz_kendi_lisansinda_uygular_ayni_ozetli_iki_etkin_lisans()
    {
        // Aynı gönderici iki yayıncıya da havale gönderebilir; özet lisanslar arasında aynıdır. Bir lisansın bağı öbür lisansın
        // satırına öneri üretmez ((a) aynı lisansın bağı); öbür lisansın bağı silinmişse onun "iban-memory" önerisi, ilk lisansın
        // aynı özetli bağı dursa da geri alınır ((b) aynı lisansta bağ yok).
        using var db = NewDb();
        var lic = ConnectedLicense(db); var otherLic = ConnectedLicense(db);
        var customer = AddCustomer(db, lic, "ornek_musteri34");
        var otherCustomer = AddCustomer(db, otherLic, "mehmet_k");
        var hash = NewHash();
        var now = DateTimeOffset.UtcNow;
        var source = HashedTx(db, lic, hash, TimeSpan.FromDays(2), DecidedFor(customer.Id, now.AddHours(-3)));
        Bond(db, lic, customer.Id, hash, now.AddHours(-3), source.Id);
        var otherStale = HashedTx(db, otherLic, hash, TimeSpan.FromDays(8), Undecided(now.AddDays(-8)));
        var otherOrphan = HashedTx(db, otherLic, hash, TimeSpan.FromDays(4), Undecided(now.AddDays(-1), "iban-memory", otherCustomer.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var staleBefore = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == otherStale.Id);

        await Job(db).RunAsync(CancellationToken.None);

        (await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == otherStale.Id))
            .Should().BeEquivalentTo(staleBefore, o => o.Excluding(m => m.BankTransaction), "öbür lisansın bağı yok");
        var orphan = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == otherOrphan.Id);
        (orphan.Status, orphan.ProposedWpfCustomerId, orphan.Evidence).Should().Be(
            (PaymentMatchStatus.NoProposal, (Guid?)null, "no-signal" + PaymentMatcher.ReevaluationEvidenceSuffix));
    }
}
