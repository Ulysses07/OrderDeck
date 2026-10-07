using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class MatchingSinkTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MatchingSinkTests(ApiFactory factory) => _factory = factory;

    /// <summary>Kaydı istenirse düşürür: eşleştiricinin yazısı patlasın (DB kesintisi, zaman aşımı).</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public Func<Exception>? Fail { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail is { } fail) throw fail();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Hata günlüklerini (biçimlenmiş metin + istisna) toplar.</summary>
    private sealed class ErrorRecorder : ILogger<MatchingBankTransactionSink>
    {
        public List<(string Message, Exception? Exception)> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add((formatter(state, exception), exception));
        }
    }

    /// <summary>Açılan ve kapanan kapsamları sayar; sink'in alt kapsamına (ve bağlamına) testten erişim verir.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public IServiceScope? Last { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            var scope = new Scope(this, inner.CreateScope());
            Last = scope;
            return scope;
        }

        private sealed class Scope(CountingScopeFactory owner, IServiceScope scope) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;
            public void Dispose() { owner.Disposed++; scope.Dispose(); }
            public ValueTask DisposeAsync() { owner.Disposed++; return ((IAsyncDisposable)scope).DisposeAsync(); }
        }
    }

    /// <summary>Üretimdeki kayıtların eşleştirme için gereken kısmı; her çağrı kendi InMemory veritabanı.</summary>
    private static ServiceProvider Provider(IInterceptor? interceptor = null)
    {
        var name = $"sink-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LicenseDbContext>(o =>
        {
            o.UseInMemoryDatabase(name);
            if (interceptor is not null) o.AddInterceptors(interceptor);
        });
        services.AddOptions<BankOptions>();
        services.AddScoped<PaymentMatcher>();
        services.AddScoped<PaymentMatchReconciler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static BankTransaction Incoming(Guid lic, string description) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 100m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow, Description = description, FetchedAt = DateTimeOffset.UtcNow,
    };

    private static WpfCustomerProjection Customer(Guid lic, string username) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Lisansa bir müşteri ve açıklamasında onun kullanıcı adı geçen gelen hareketler yazar.</summary>
    private static async Task<(Guid CustomerId, List<BankTransaction> Txs)> SeedAsync(IServiceProvider sp, int count = 1)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = Guid.NewGuid();
        var customer = Customer(lic, "ornek_musteri34");
        db.WpfCustomerProjections.Add(customer);
        var txs = Enumerable.Range(0, count).Select(_ => Incoming(lic, "HAVALE ornek_musteri34")).ToList();
        db.BankTransactions.AddRange(txs);
        await db.SaveChangesAsync();
        return (customer.Id, txs);
    }

    private static async Task<List<PaymentMatch>> MatchesAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.AsNoTracking().ToListAsync();
    }

    [Fact]
    public void DI_de_sink_eslestirici_olani()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBankTransactionSink>().Should().BeOfType<MatchingBankTransactionSink>();
    }

    [Fact]
    public async Task Sink_gelen_hareket_icin_PaymentMatch_yazar()
    {
        await using var sp = Provider();
        var (customerId, txs) = await SeedAsync(sp);
        using var sink = new MatchingBankTransactionSink(sp.GetRequiredService<IServiceScopeFactory>(), new ErrorRecorder());

        await sink.OnNewIncomingAsync(txs[0], CancellationToken.None);

        var match = (await MatchesAsync(sp)).Should().ContainSingle().Subject;
        match.Status.Should().Be(PaymentMatchStatus.Proposed);
        match.ProposedWpfCustomerId.Should().Be(customerId);
    }

    [Fact]
    public async Task Sink_gecikmeli_gelen_hareketle_acik_gap_i_cozer()
    {
        // Dekont hareketten önce onaylandı (gap NoCandidate); hareket çekimle gelince sink öneriyi yazar ve gap'i çözer.
        await using var sp = Provider();
        var (customerId, txs) = await SeedAsync(sp);
        var tx = txs[0];
        Guid paymentId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopperId = Guid.NewGuid();
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = tx.LicenseId, Platform = "youtube", Username = "ornek_musteri34",
                WpfCustomerId = customerId, JoinedAt = DateTimeOffset.UtcNow,
            });
            var payment = new Payment
            {
                Id = Guid.NewGuid(), LicenseId = tx.LicenseId, ShopperId = shopperId, PayerName = "ORNEK MUSTERI", Amount = tx.Amount,
                PaidAt = tx.OccurredAt.AddHours(-1), ReferansNo = $"r-{Guid.NewGuid():N}", Status = PaymentStatus.Approved,
                ApprovedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Payments.Add(payment);
            db.PaymentMatchGaps.Add(new PaymentMatchGap
            {
                Id = Guid.NewGuid(), LicenseId = tx.LicenseId, PaymentId = payment.Id, Reason = PaymentMatchGapReason.NoCandidate,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }
        var scopes = new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
        using var sink = new MatchingBankTransactionSink(scopes, new ErrorRecorder());

        await sink.OnNewIncomingAsync(tx, CancellationToken.None);

        var match = (await MatchesAsync(sp)).Should().ContainSingle().Subject;
        match.PaymentId.Should().Be(paymentId);
        match.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        await using var verify = sp.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatchGaps.AsNoTracking().SingleAsync())
            .ResolvedBankTransactionId.Should().Be(tx.Id);
        scopes.Last!.ServiceProvider.GetRequiredService<LicenseDbContext>().ChangeTracker.Entries()
            .Should().BeEmpty("gap çözümü de alt bağlamın boşaltılan izleyicisinde koşar");
    }

    [Fact]
    public async Task Sink_cekim_isinin_kaydedilmemis_degisikligini_yazmaz()
    {
        // Üretimdeki gibi: sink çekim işinin kapsamından çözülür, işin bağlamında izlenen bağlantının kaydedilmemiş bir
        // değişikliği var. Eşleştirici o bağlamda koşsaydı kendi SaveChanges'i bunu işin kimlik denetimini atlayarak yazardı.
        var lic = Guid.NewGuid();
        using var jobScope = _factory.Services.CreateScope();
        var jobDb = jobScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var conn = new ObifinConnection
        {
            Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://obifin.invalid", UserCode = $"u-{Guid.NewGuid():N}",
            Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var customer = Customer(lic, "ornek_musteri34");
        var tx = Incoming(lic, "HAVALE ornek_musteri34");
        jobDb.AddRange(conn, customer, tx);
        await jobDb.SaveChangesAsync();
        conn.LastError = "kaydedilmemiş iş değişikliği";
        var sink = jobScope.ServiceProvider.GetRequiredService<IBankTransactionSink>();

        await sink.OnNewIncomingAsync(tx, CancellationToken.None);

        using var fresh = _factory.Services.CreateScope();
        var freshDb = fresh.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await freshDb.ObifinConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id)).LastError
            .Should().BeNull("işin bekleyen değişikliği yalnız işin kendi kaydıyla yazılır");
        (await freshDb.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == tx.Id)).ProposedWpfCustomerId
            .Should().Be(customer.Id);
        jobDb.Entry(conn).State.Should().Be(EntityState.Modified, "değişiklik işin izleyicisinde, işe ait kalır");
        jobDb.ChangeTracker.Entries<PaymentMatch>().Should().BeEmpty("öneri satırları işin izleyicisinde birikmez");
        jobDb.Entry(tx).State.Should().Be(EntityState.Unchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sink_eslestirme_hatasini_yutar_hareket_kimligiyle_loglar_aciklamayi_yazmaz(bool foreignCancellation)
    {
        // Hareket zaten kaydedildi; eşleştirme hatası çekimi düşürmemeli. İşin kendi token'ı dışındaki iptal (ör. komut
        // zaman aşımı) da bir hatadır, yutulur.
        var interceptor = new FailingSaveInterceptor();
        await using var sp = Provider(interceptor);
        var (_, txs) = await SeedAsync(sp, count: 2);
        var log = new ErrorRecorder();
        using var sink = new MatchingBankTransactionSink(sp.GetRequiredService<IServiceScopeFactory>(), log);
        interceptor.Fail = () => foreignCancellation ? new TaskCanceledException() : new InvalidOperationException("db gitti");

        var act = () => sink.OnNewIncomingAsync(txs[0], CancellationToken.None);

        await act.Should().NotThrowAsync();
        var (message, exception) = log.Errors.Should().ContainSingle().Subject;
        exception.Should().NotBeNull();
        message.Should().Contain(txs[0].Id.ToString());
        // Kimlik çıkarılınca kalan metinde açıklama da tutar da yok (Guid'in hex'i "100" içerebilir, önce o atılır).
        message.Replace(txs[0].Id.ToString(), "").Should().NotContainAny("HAVALE", "ornek", "100");
        (await MatchesAsync(sp)).Should().BeEmpty();

        // Düşen çağrı alt bağlamda iz bırakmaz: aynı koşunun sonraki hareketi normal eşleşir.
        interceptor.Fail = null;
        await sink.OnNewIncomingAsync(txs[1], CancellationToken.None);
        (await MatchesAsync(sp)).Should().ContainSingle().Which.BankTransactionId.Should().Be(txs[1].Id);
    }

    [Fact]
    public async Task Sink_isin_kendi_iptalini_yukari_tasir()
    {
        await using var sp = Provider();
        var (_, txs) = await SeedAsync(sp);
        var log = new ErrorRecorder();
        using var sink = new MatchingBankTransactionSink(sp.GetRequiredService<IServiceScopeFactory>(), log);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sink.OnNewIncomingAsync(txs[0], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Errors.Should().BeEmpty("işin kendi iptali hata değildir");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sink_kosu_boyunca_tek_alt_kapsam_acar_izleyiciyi_bosaltir_dispose_ile_kapatir(bool async)
    {
        await using var sp = Provider();
        var (_, txs) = await SeedAsync(sp, count: 3);
        var scopes = new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
        var sink = new MatchingBankTransactionSink(scopes, new ErrorRecorder());

        foreach (var tx in txs)
        {
            await sink.OnNewIncomingAsync(tx, CancellationToken.None);
            scopes.Last!.ServiceProvider.GetRequiredService<LicenseDbContext>().ChangeTracker.Entries()
                .Should().BeEmpty("öneri satırları koşu boyunca alt bağlamda da birikmez");
        }

        scopes.Created.Should().Be(1, "bir koşu (sink örneği) tek alt kapsam kullanır");
        (await MatchesAsync(sp)).Should().HaveCount(3);
        if (async) { await sink.DisposeAsync(); await sink.DisposeAsync(); }
        else { sink.Dispose(); sink.Dispose(); }
        scopes.Disposed.Should().Be(1, "alt kapsam sink ile birlikte bir kez kapanır");
    }
}
