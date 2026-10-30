using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Satış senkronu mod On + ortak bakiye havuz kipi: kapsam başına tek düşüm kuralı çapa düzeyinde, gerçek SQL Server'da
/// (ortak bakiye spec 2026-10-11 §8.2-15, §4.2, §4.13, §15-2; plan farkı 7). Kuralın hakemi asıl kayıt×kapsam uygulama
/// kilididir (<c>bal:{lisans}:{asıl kayıt}:{kapsam}</c>); havuz yolunda her deneme kendi işlemini açar, kilidi alır,
/// Lookup'u ve kapsam yüklemini kilit altında koşar. 409 <c>scope-has-deduction</c>'ın tutarı <c>−Σ(grup)</c>'tur:
/// çapanın payı 0 olan planda (Örnek 1) çapa satırının tutarı değil. Başka üyedeki çapa kuralı tetiklemez (M10).
/// Jeton çakışmasından sonraki deneme kilidi yeniden alır ve yüklemi yeniden koşar: arada kazanan rakibin çapasını
/// görür (§4.13). Satır kipinin aynı sözleşmesi <see cref="CustomerBalanceScopeRuleRelationalTests"/>'te, değişmeden.
///
/// <para>Yarış sırayla kurulur: ilk apply'ın düşüm kaydında kanca rakip apply'ı başlatır ve rakibin kilitte beklediği
/// SQL Server'da görülene (<see cref="AppLockWaiterProbe"/>) ya da rakip düşüm yazımına gelene kadar bekler; üst sınır
/// kilit bekleme sınırının altındadır. Kişi {T (sahip, 0), I (100), Y (30)}; kullanıcı adları ve grup numarası
/// üretilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceScopeRuleRelationalTests : IAsyncLifetime
{
    /// <summary>Kancanın rakibi bekleme üst sınırı; kilit bekleme sınırının (<see cref="SalesCommandLock.DefaultTimeout"/>,
    /// 5 sn) altında. Olağan durumda kanca rakibin kilitte beklediğini görür görmez bırakır.</summary>
    private static readonly TimeSpan RivalWait = TimeSpan.FromSeconds(4);

    private const decimal PoolAtStart = 130m;

    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private HookFactory _factory = null!;
    private Kit _kit = null!;
    private Guid _t;
    private Guid _i;
    private Guid _y;

    public PersonBalanceScopeRuleRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new HookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var g = Kit.NewGroup();
        _t = await _kit.MemberAsync(g);
        _i = await _kit.MemberAsync(g, 100m, platform: "instagram");
        _y = await _kit.MemberAsync(g, 30m);
        await _kit.EnablePoolAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _kit.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §8.2-15: farklı anahtarlı iki apply aynı çapa ve kapsamda ────────────

    /// <summary>
    /// §8.2-15, §15-2: T'ye ve <c>session:42</c>'ye farklı anahtarlı eşzamanlı iki apply. Tam olarak biri 200, öbürü 409
    /// <c>scope-has-deduction</c>: <c>transactionId</c> kazananın çapası, <c>appliedAmount</c> = <c>−Σ(grup)</c> (çapanın
    /// payı 0 — T'nin bakiyesi yok), <c>productTotal</c> kazananınki. Tek geri alınmamış kapsamlı çapa; havuz bir kez düşer.
    /// </summary>
    [Fact]
    public async Task Tc15_farkli_anahtarli_iki_apply_biri_200_obur_409_scope_has_deduction_eksi_sigma()
    {
        var first = Guid.NewGuid();
        var rival = Guid.NewGuid();
        _factory.Hook.Arm(() => ApplyAsync(_t, 50m, rival), RivalWait);

        var firstResp = await ApplyAsync(_t, 120m, first);
        var rivalResp = await _factory.Hook.RivalAsync();

        var bodies = $"{await firstResp.Content.ReadAsStringAsync()} | {await rivalResp.Content.ReadAsStringAsync()}";
        new[] { firstResp.StatusCode, rivalResp.StatusCode }
            .Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict], bodies);
        var (winner, loser) = firstResp.StatusCode == HttpStatusCode.OK
            ? ((Resp: firstResp, Key: first, Amount: 120m), (Resp: rivalResp, Key: rival, Amount: 50m))
            : ((Resp: rivalResp, Key: rival, Amount: 50m), (Resp: firstResp, Key: first, Amount: 120m));

        var winnerRows = await _kit.GroupRowsAsync(winner.Key);
        winnerRows[0].Amount.Should().Be(0m, "çapanın payı 0: 409'un tutarı çapa satırından değil grubun toplamından");
        (-winnerRows.Sum(t => t.Amount)).Should().Be(winner.Amount);
        await ShouldBeScopeHasDeductionAsync(loser.Resp, winner.Key, appliedAmount: winner.Amount, productTotal: winner.Amount);
        (await UnreversedScopeAnchorsAsync()).Should().Equal((winner.Key, _t));
        (await _kit.GroupRowsAsync(loser.Key)).Should().BeEmpty("kaybeden hiç yazmaz");
        (await PoolAsync()).Should().Be(PoolAtStart - winner.Amount, "havuz bir kez düşer");
        await ShouldHoldD1Async();
    }

    /// <summary>
    /// Aynı anahtarın eşzamanlı iki tekrarı: kilitte bekleyen ikinci istek kilidi aldığı denemede, kilit altında
    /// anahtarı yeniden arar ve kayıtlı sonucu oynatır — <c>scope-has-deduction</c> değil. İkisi de 200 ve aynı çapa;
    /// tek çekim grubu.
    /// </summary>
    [Fact]
    public async Task Tc15_ayni_anahtarli_iki_apply_ikisi_de_200_tek_grup()
    {
        var key = Guid.NewGuid();
        _factory.Hook.Arm(() => ApplyAsync(_t, 120m, key), RivalWait);

        var firstResp = await ApplyAsync(_t, 120m, key);
        var rivalResp = await _factory.Hook.RivalAsync();

        firstResp.StatusCode.Should().Be(HttpStatusCode.OK, await firstResp.Content.ReadAsStringAsync());
        rivalResp.StatusCode.Should().Be(HttpStatusCode.OK, await rivalResp.Content.ReadAsStringAsync());
        foreach (var resp in new[] { firstResp, rivalResp })
            (await ReadApplyAsync(resp)).Should().Be(
                new LicensesCustomerBalanceApplyController.ApplyResponse(key, 120m, 10m));
        (await _kit.GroupRowsAsync(key)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().BeEquivalentTo([(_t, 0m), (_i, -100m), (_y, -20m)], "Örnek 1'in tek grubu");
        (await UnreversedScopeAnchorsAsync()).Should().Equal((key, _t));
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(0m);
        (await _kit.BalanceAsync(_y)).Should().Be(10m);
        await ShouldHoldD1Async();
    }

    /// <summary>
    /// §15-2, M10: kural çapa düzeyinde. Aynı kişinin I üyesine ve sonra T'ye aynı kapsamla iki apply (farklı anahtar,
    /// sırayla): T'nin kilidinde ve yükleminde I'nin çapası görünmez → ikisi de 200 (#506'nın çok izli akışı).
    /// </summary>
    [Fact]
    public async Task Tc15_baska_uyedeki_capa_kapsam_kuralini_tetiklemez()
    {
        var onI = Guid.NewGuid();
        var onT = Guid.NewGuid();

        var iResp = await ApplyAsync(_i, 10m, onI);
        var tResp = await ApplyAsync(_t, 20m, onT);

        iResp.StatusCode.Should().Be(HttpStatusCode.OK, await iResp.Content.ReadAsStringAsync());
        tResp.StatusCode.Should().Be(HttpStatusCode.OK, await tResp.Content.ReadAsStringAsync());
        (await UnreversedScopeAnchorsAsync()).Should().BeEquivalentTo([(onI, _i), (onT, _t)], "her çapa kendi üyesinde");
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(70m, "önce kendi çapasından 10, sonra T'nin çekiminde 20");
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await ShouldHoldD1Async();
    }

    // ── İkinci deneme: kilit, Lookup ve yüklem yeniden (deneme başına işlem, plan farkı 7) ──

    /// <summary>
    /// §4.13 ("her denemede Lookup'tan sonra yeniden"), plan farkı 7: X (120) düşüm kaydında rakip R'yi (50, farklı
    /// anahtar) başlatır; R kapsam kilidinde bekler. Kanca R'nin beklediğini görünce panel I'ye 10 iade eder (artı hareket:
    /// kapsam kilidi almaz, yalnız I'nin jetonunu ilerletir). X'in SQL'i I'nin bayat jetonuna çarpar; X'in 1. deneme
    /// işlemi atılır, kilit bırakılır. Kilitte bekleyen R kilidi alır, yüklemi temiz bulur, 50 çeker ve commit eder.
    /// X'in 2. denemesi kilidi YENİDEN alır (R'nin commit'ini bekler), Lookup'tan sonra yüklemi yeniden koşar ve R'nin
    /// çapasını görür → 409 <c>scope-has-deduction</c>, tutarı R'nin <c>−Σ(grup)</c>'u. Tek işlem/tek kilitle (satır kipinin
    /// <c>ApplyOncePerScopeAsync</c>'i gibi) R, X bitene kadar bekler ve X 200 alırdı; 2. denemede yüklem atlansaydı iki
    /// geri alınmamış kapsamlı çapa kalırdı.
    /// </summary>
    [Fact]
    public async Task Ikinci_denemede_kilit_yeniden_alinir_rakip_kazanirsa_409_scope_has_deduction()
    {
        var first = Guid.NewGuid();
        var rival = Guid.NewGuid();
        _factory.Hook.Arm(
            () => ApplyAsync(_t, 50m, rival),
            RivalWait,
            async () => (await _kit.PanelRefundAsync(_i, 10m)).EnsureSuccessStatusCode());

        var x = await ApplyAsync(_t, 120m, first);
        var r = await _factory.Hook.RivalAsync();

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThanOrEqualTo(1,
            "X'in 1. denemesi panel iadesinin ilerlettiği I jetonuna çarpmalı");
        await ShouldBeScopeHasDeductionAsync(x, rival, appliedAmount: 50m, productTotal: 50m);
        (await UnreversedScopeAnchorsAsync()).Should().Equal((rival, _t));
        (await _kit.GroupRowsAsync(first)).Should().BeEmpty("X'in 1. denemesi geri alındı, 2. denemesi yazmadı");
        (await _kit.GroupRowsAsync(rival)).Select(t => (t.WpfCustomerId, t.Amount))
            .Should().Equal((_t, 0m), (_i, -50m));
        (await PoolAsync()).Should().Be(PoolAtStart + 10m - 50m, "iade +10, yalnız R'nin 50'si düştü");
        (await _kit.BalanceAsync(_i)).Should().Be(60m);
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await ShouldHoldD1Async();
    }

    // ── Kilit zaman aşımı (deneme başına işlem, plan farkı 7) ────────────────

    /// <summary>
    /// Havuz yolunda da asıl kayıt×kapsam kilidi başka bir işlemde tutulurken gelen apply kilit bekleme sınırında 409
    /// <c>stale</c> alır (<see cref="SalesCommandLock"/>; <c>DrawAsync</c> <c>SalesCommandRejection</c>'ı yakalamaz,
    /// Görev 7 eşler). Hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_kapsam_kilidi_zaman_asimi_409_stale_yazim_yok()
    {
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(
            holder,
            LicensesCustomerBalanceApplyController.ScopeLockResource(_kit.LicenseId, _t, Kit.Session42),
            TimeSpan.FromSeconds(1),
            default);
        var key = Guid.NewGuid();

        var resp = await ApplyAsync(_t, 100m, key);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("stale");
        (await _kit.GroupRowsAsync(key)).Should().BeEmpty();
        (await UnreversedScopeAnchorsAsync()).Should().BeEmpty();
        (await _kit.BalanceAsync(_t)).Should().Be(0m);
        (await _kit.BalanceAsync(_i)).Should().Be(100m);
        (await _kit.BalanceAsync(_y)).Should().Be(30m);
        await held.RollbackAsync();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>Kişinin bütün üyeleriyle (<c>W = S</c>), <c>session:42</c> kapsamında, ürün toplamı = tutar.</summary>
    private Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, Guid key) =>
        _kit.ApplyAsync(row, amount, amount, key, Kit.Session42, [_t, _i, _y]);

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadApplyAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;

    /// <summary>Lisansta <c>session:42</c> kapsamlı, geri alınmamış düşüm çapaları (kapsam yalnız çapada, §3.2).</summary>
    private Task<List<(Guid Id, Guid Row)>> UnreversedScopeAnchorsAsync() =>
        _kit.Sales.ReadDbAsync(async db =>
        {
            var rows = await db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => t.LicenseId == _kit.LicenseId && t.Kind == Kit.KindDeduction && t.SaleScope == Kit.Session42
                    && !db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
                .Select(t => new { t.Id, t.WpfCustomerId })
                .ToListAsync();
            return rows.Select(r => (Id: r.Id, Row: r.WpfCustomerId)).ToList();
        });

    private async Task<decimal> PoolAsync() =>
        await _kit.BalanceAsync(_t) + await _kit.BalanceAsync(_i) + await _kit.BalanceAsync(_y);

    private async Task ShouldHoldD1Async()
    {
        foreach (var row in new[] { _t, _i, _y })
        {
            (await _kit.BalanceAsync(row)).Should().BeGreaterThanOrEqualTo(0m);
            (await _kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        }
    }

    private static async Task ShouldBeScopeHasDeductionAsync(
        HttpResponseMessage resp, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(transactionId);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(appliedAmount, "−Σ(grup), §15-2");
        problem.GetProperty("productTotal").GetDecimal().Should().Be(productTotal);
    }

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>
    /// Düşüm yazımı kancası (<c>CustomerBalanceScopeRuleRelationalTests.RivalHook</c>'un kopyası, iki ekle): yalnız
    /// kurulduğunda ve yalnız <c>purchase-deduction</c> satırı ekleyen kaydetmede tetiklenir — havuz yolunda çapa ve
    /// bacaklar tek kayıttadır, tek varış sayılır. İlk tetiklenişte rakip isteği başlatır (sonunu beklemez) ve rakip düşüm
    /// yazımına gelene, kilitte beklediği görülene ya da süre dolana kadar bekler; ardından, verildiyse,
    /// <c>afterWaiter</c>'ı bekler (ilk isteğin SQL'i gitmeden araya bir yazım sokmak için). Ekler: <c>afterWaiter</c> ve
    /// jeton çakışması sayacı (<see cref="RaceSaveHook"/> kalıbı).
    /// </summary>
    private sealed class RivalHook(string connectionString) : SaveChangesInterceptor
    {
        private readonly object _gate = new();
        private bool _armed;
        private int _arrivals;
        private int _concurrencyFailures;
        private TimeSpan _wait;
        private Func<Task<HttpResponseMessage>>? _rival;
        private Func<Task>? _afterWaiter;
        private Task<HttpResponseMessage>? _rivalTask;
        private TaskCompletionSource _rivalArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Kurulduğundan beri jeton çakışmaları (<see cref="DbUpdateConcurrencyException"/>).</summary>
        public int ConcurrencyFailures => Volatile.Read(ref _concurrencyFailures);

        public void Arm(Func<Task<HttpResponseMessage>> rival, TimeSpan wait, Func<Task>? afterWaiter = null)
        {
            lock (_gate)
            {
                _arrivals = 0;
                Volatile.Write(ref _concurrencyFailures, 0);
                _wait = wait;
                _rival = rival;
                _afterWaiter = afterWaiter;
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
                    .Any(e => e.State == EntityState.Added && e.Entity.Kind == Kit.KindDeduction))
                return result;

            Func<Task<HttpResponseMessage>>? rival;
            Func<Task>? afterWaiter;
            int arrival;
            lock (_gate)
            {
                if (!_armed) return result;
                arrival = ++_arrivals;
                rival = arrival == 1 ? _rival : null;
                afterWaiter = arrival == 1 ? _afterWaiter : null;
                if (rival is not null) _rivalTask = Task.Run(rival);
            }

            if (rival is not null)
            {
                await Task.WhenAny(
                    _rivalArrived.Task,
                    AppLockWaiterProbe.WaitForWaiterAsync(connectionString, _wait, cancellationToken));
                if (afterWaiter is not null) await afterWaiter();
            }
            else if (arrival == 2)
                _rivalArrived.TrySetResult();
            return result;
        }

        /// <summary>
        /// Jeton çakışmasını sayar. <c>SaveChangesFailedAsync</c> değil: EF <see cref="DbUpdateConcurrencyException"/>'ı
        /// o olaya uğratmaz (<see cref="RaceSaveHook.ThrowingConcurrencyExceptionAsync"/> gerekçesi).
        /// </summary>
        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _concurrencyFailures);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Gerçek SQL Server + rakip kancası; iki sürüm kapısı da kitin UA'sını (0.9.11) geçer.</summary>
    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public RivalHook Hook { get; } = new(connectionString);

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
            ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
