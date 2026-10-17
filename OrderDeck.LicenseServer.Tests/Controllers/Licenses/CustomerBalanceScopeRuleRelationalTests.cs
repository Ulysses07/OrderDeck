using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.CustomerBalanceTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Mod On'da kapsam başına tek düşüm kuralının eşzamanlılık sözleşmesi gerçek SQL Server'da — spec 2026-10-13 §6.6, DM6,
/// M8, §10.1-9, §10.1-10. Kuralın hakemi asıl kayıt×kapsam uygulama kilididir (<c>bal:{lisans}:{asıl kayıt}:{kapsam}</c>):
/// defter yüklemi kilit altında koşar. InMemory'de kilit ve işlem yoktur; orada yalnız sıralı davranış sınanır
/// (<see cref="CustomerBalanceScopeRuleTests"/>).
///
/// <para>Yarış sırayla kurulur (zamanlamayla değil): bir apply'ın düşüm satırı diske inmeden hemen önce kanca rakip
/// apply'ı BAŞLATIR ama sonunu beklemez — rakip ilk isteğin kilidinde bekliyor olabilir. Kanca rakip düşüm yazımına
/// gelene, ya da rakibin kilitte beklediği SQL Server'da görülene (<see cref="AppLockWaiterProbe"/>) kadar bekler; üst
/// sınır kilit bekleme sınırının altındadır. Bekleme sabit bir süreyle varsayılsaydı tam paketin yükü altında rakip
/// kilit sınırını aşıp 409 <c>stale</c> yolunu koşardı. Kilit olmasaydı rakip ilk isteğin yazımından önce yüklemi geçerdi
/// ve iki düşüm kalırdı.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerBalanceScopeRuleRelationalTests : IAsyncLifetime
{
    /// <summary>Kancanın rakibi bekleme üst sınırı; kilit bekleme sınırının (<see cref="SalesCommandLock.DefaultTimeout"/>,
    /// 5 sn) altında. Olağan durumda kanca rakibin kilitte beklediğini görür görmez bırakır.</summary>
    private static readonly TimeSpan RivalWait = TimeSpan.FromSeconds(4);

    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private HookFactory _factory = null!;
    private Kit _kit = null!;

    public CustomerBalanceScopeRuleRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new HookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory, SalesSyncMode.On, 500m);
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _kit.Sales.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §10.1-9: farklı anahtarlı iki apply aynı kapsamda ────────────────────

    /// <summary>
    /// §10.1-9, DM6: aynı asıl kayıt×kapsama farklı anahtarlı eşzamanlı iki apply. Tam olarak biri 200, öbürü 409
    /// <c>scope-has-deduction</c> (kazananın düşümüyle); tek geri alınmamış düşüm, bakiye bir kez düşer.
    /// </summary>
    [Fact]
    public async Task Farkli_anahtarli_eszamanli_iki_apply_biri_200_obur_409_scope_has_deduction()
    {
        var first = Guid.NewGuid();
        var rival = Guid.NewGuid();
        _factory.Hook.Arm(() => _kit.ApplyAsync(150m, 3000m, rival, Kit.Cumulative), RivalWait);

        var firstResp = await _kit.ApplyAsync(100m, 2100m, first, Kit.Cumulative);
        var rivalResp = await _factory.Hook.RivalAsync();

        await ShouldBeOneDeductionAndOneScopeConflictAsync(
            (firstResp, first, 100m, 2100m), (rivalResp, rival, 150m, 3000m));
    }

    /// <summary>
    /// Aynı anahtarın eşzamanlı iki tekrarı: kilitte bekleyen ikinci istek kilit altında anahtarı yeniden arar ve
    /// kayıtlı sonucu oynatır — <c>scope-has-deduction</c> değil. İkisi de 200 ve aynı işlem; tek düşüm.
    /// </summary>
    [Fact]
    public async Task Ayni_anahtarli_eszamanli_iki_apply_ikisi_de_200_tek_dusum()
    {
        var key = Guid.NewGuid();
        _factory.Hook.Arm(() => _kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative), RivalWait);

        var firstResp = await _kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative);
        var rivalResp = await _factory.Hook.RivalAsync();

        firstResp.StatusCode.Should().Be(HttpStatusCode.OK, await firstResp.Content.ReadAsStringAsync());
        rivalResp.StatusCode.Should().Be(HttpStatusCode.OK, await rivalResp.Content.ReadAsStringAsync());
        (await Kit.ReadApplyAsync(firstResp)).TransactionId.Should().Be(key);
        (await Kit.ReadApplyAsync(rivalResp)).TransactionId.Should().Be(key);
        (await _kit.DeductionsAsync()).Should().ContainSingle().Which.Id.Should().Be(key);
        (await _kit.BalanceAsync()).Should().Be(400m);
    }

    // ── §10.1-10: bayat B revizyonu ──────────────────────────────────────────

    /// <summary>
    /// §10.1-10: B'nin KB1'i A'nın revizyonunda geri alınmış, A KA'yı uygulamış. Bayat B kendi KB1'ini geri almaya
    /// çalışır (409 <c>already-reversed</c>, istemci bunu başarı sayar) ve KB2'yi uygular: KB2 409
    /// <c>scope-has-deduction</c> alır (KA ile). Defterde tek geri alınmamış düşüm kalır: KA.
    /// </summary>
    [Fact]
    public async Task Bayat_B_revizyonu_KB2_409_tek_gecerli_dusum_KA()
    {
        var kb1 = await ApplyOkAsync(100m, 2100m);
        (await _kit.ReverseAsync(kb1)).StatusCode.Should().Be(HttpStatusCode.OK, "A'nın revizyonu KB1'i geri alır");
        var ka = await ApplyOkAsync(120m, 2500m);

        var staleReverse = await _kit.ReverseAsync(kb1);
        var kb2 = Guid.NewGuid();
        var kb2Resp = await _kit.ApplyAsync(150m, 3000m, kb2, Kit.Cumulative);

        staleReverse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SalesKit.ProblemAsync(staleReverse)).GetProperty("title").GetString().Should().Be("already-reversed");
        await ShouldBeScopeHasDeductionAsync(kb2Resp, ka, appliedAmount: 120m, productTotal: 2500m);
        (await _kit.UnreversedDeductionIdsAsync()).Should().Equal(ka);
        (await _kit.BalanceAsync()).Should().Be(380m);
    }

    /// <summary>
    /// §10.1-10, eşzamanlı: KB1 geri alınmış; A'nın KA'sı ve bayat B'nin KB2'si aynı anda gelir. Tam olarak biri 200,
    /// öbürü 409 <c>scope-has-deduction</c>; tek geri alınmamış düşüm, bakiye bir kez düşer.
    /// </summary>
    [Fact]
    public async Task Bayat_B_revizyonu_eszamanli_KA_KB2_biri_409_tek_gecerli_dusum()
    {
        var kb1 = await ApplyOkAsync(100m, 2100m);
        (await _kit.ReverseAsync(kb1)).StatusCode.Should().Be(HttpStatusCode.OK);
        var ka = Guid.NewGuid();
        var kb2 = Guid.NewGuid();
        _factory.Hook.Arm(() => _kit.ApplyAsync(150m, 3000m, kb2, Kit.Cumulative), RivalWait);

        var kaResp = await _kit.ApplyAsync(120m, 2500m, ka, Kit.Cumulative);
        var kb2Resp = await _factory.Hook.RivalAsync();

        await ShouldBeOneDeductionAndOneScopeConflictAsync(
            (kaResp, ka, 120m, 2500m), (kb2Resp, kb2, 150m, 3000m));
    }

    // ── Kilit zaman aşımı ────────────────────────────────────────────────────

    /// <summary>
    /// Asıl kayıt×kapsam kilidi başka bir işlemde tutulurken gelen apply kilit bekleme sınırında 409 <c>stale</c> alır
    /// (<see cref="SalesCommandLock"/> davranışı); hiçbir şey yazılmaz. Kilit adı kapsamı küçük harfe katlar ve sondaki
    /// boşluğu atar: SQL'in kapsam eşitliği harf duyarsızdır ve sondaki boşluğu yok sayar, kilit en az onun kadar kaba
    /// olmalı.
    /// </summary>
    [Fact]
    public async Task Kilit_zaman_asimi_409_stale_yazim_yok()
    {
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(
            holder,
            LicensesCustomerBalanceApplyController.ScopeLockResource(_kit.LicenseId, _kit.WpfCustomerId, "CUMULATIVE  "),
            TimeSpan.FromSeconds(1),
            default);

        var resp = await _kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("stale");
        (await _kit.DeductionsAsync()).Should().BeEmpty();
        (await _kit.BalanceAsync()).Should().Be(500m);
        await held.RollbackAsync();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<Guid> ApplyOkAsync(decimal amount, decimal productTotal)
    {
        var key = Guid.NewGuid();
        var resp = await _kit.ApplyAsync(amount, productTotal, key, Kit.Cumulative);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return key;
    }

    /// <summary>İki istekten tam olarak biri düştü, öbürü 409 <c>scope-has-deduction</c> ile kazananı gördü; tek geri
    /// alınmamış düşüm (kazanan), bakiye kazananın tutarı kadar düştü.</summary>
    private async Task ShouldBeOneDeductionAndOneScopeConflictAsync(
        (HttpResponseMessage Resp, Guid Key, decimal Amount, decimal ProductTotal) first,
        (HttpResponseMessage Resp, Guid Key, decimal Amount, decimal ProductTotal) rival)
    {
        var bodies = $"{await first.Resp.Content.ReadAsStringAsync()} | {await rival.Resp.Content.ReadAsStringAsync()}";
        new[] { first.Resp.StatusCode, rival.Resp.StatusCode }
            .Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], bodies);
        var (winner, loser) = first.Resp.StatusCode == HttpStatusCode.OK ? (first, rival) : (rival, first);

        await ShouldBeScopeHasDeductionAsync(loser.Resp, winner.Key, winner.Amount, winner.ProductTotal);
        (await _kit.UnreversedDeductionIdsAsync()).Should().Equal(winner.Key);
        (await _kit.DeductionsAsync()).Should().NotContain(t => t.Id == loser.Key, "kaybeden hiç yazmaz");
        (await _kit.BalanceAsync()).Should().Be(500m - winner.Amount);
    }

    private static async Task ShouldBeScopeHasDeductionAsync(
        HttpResponseMessage resp, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(transactionId);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(appliedAmount);
        problem.GetProperty("productTotal").GetDecimal().Should().Be(productTotal);
    }

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>
    /// Düşüm yazımı kancası: yalnız kurulduğunda ve yalnız <c>purchase-deduction</c> satırı ekleyen kaydetmede
    /// tetiklenir. İlk tetiklenişte rakip isteği başlatır (sonunu beklemez) ve rakip düşüm yazımına gelene, kilitte
    /// beklediği görülene ya da süre dolana kadar bekler.
    /// </summary>
    private sealed class RivalHook(string connectionString) : SaveChangesInterceptor
    {
        private readonly object _gate = new();
        private bool _armed;
        private int _arrivals;
        private TimeSpan _wait;
        private Func<Task<HttpResponseMessage>>? _rival;
        private Task<HttpResponseMessage>? _rivalTask;
        private TaskCompletionSource _rivalArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Func<Task<HttpResponseMessage>> rival, TimeSpan wait)
        {
            lock (_gate)
            {
                _arrivals = 0;
                _wait = wait;
                _rival = rival;
                _rivalTask = null;
                _rivalArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _armed = true;
            }
        }

        public void Disarm()
        {
            lock (_gate) _armed = false;
        }

        /// <summary>Kancanın başlattığı rakip isteğin yanıtı.</summary>
        public async Task<HttpResponseMessage> RivalAsync()
        {
            Task<HttpResponseMessage>? task;
            lock (_gate) task = _rivalTask;
            task.Should().NotBeNull("kanca rakibi ilk isteğin düşüm yazımından önce başlatmalı");
            return await task!;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not { } db
                || !db.ChangeTracker.Entries<CustomerBalanceTransaction>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Kind == "purchase-deduction"))
                return result;

            Func<Task<HttpResponseMessage>>? rival;
            int arrival;
            lock (_gate)
            {
                if (!_armed) return result;
                arrival = ++_arrivals;
                rival = arrival == 1 ? _rival : null;
                if (rival is not null) _rivalTask = Task.Run(rival);
            }

            if (rival is not null)
                await Task.WhenAny(
                    _rivalArrived.Task,
                    AppLockWaiterProbe.WaitForWaiterAsync(connectionString, _wait, cancellationToken));
            else if (arrival == 2)
                _rivalArrived.TrySetResult();
            return result;
        }
    }

    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public RivalHook Hook { get; } = new(connectionString);

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
