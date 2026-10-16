using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>finish</c> ve <c>backfill</c>'in eşzamanlılık sözleşmesi gerçek SQL Server'da — spec 2026-10-13 §6.4 "Teslim kaydı
/// olmadan dolan kira", §10.2. <c>Version</c> jetonunun koşullu UPDATE'i ve birincil anahtar ihlali InMemory'de yoktur.
///
/// <para>Yarış sırayla kurulur (zamanlamayla değil): isteğin yazımı diske inmeden hemen önce kanca rakip isteği
/// çalıştırır ve sonunu bekler. Rakip kilit tutmayan bir okumadan sonra commit eder; ilk isteğin yazımı onun üzerine
/// iner.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentRequestRecordRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private HookFactory _factory = null!;
    private Kit _kit = null!;
    private readonly Guid _pcA = Guid.NewGuid();
    private readonly Guid _pcB = Guid.NewGuid();

    public PaymentRequestRecordRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new HookFactory(await _sql.CreateDatabaseAsync());
        _kit = await Kit.CreateAsync(_factory);
        await _kit.SeedReplicaAsync(_pcA, "PC-A");
        await _kit.SeedReplicaAsync(_pcB, "PC-B");
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _kit.Sales.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// §6.4, §6.8: PC-A'nın kirası doldu. PC-A'nın geç <c>delivered</c>'ı kaydı okuduktan sonra, yazımı inmeden önce
    /// PC-B'nin <c>begin</c>'i kaydı Unconfirmed yapar (c adımı, Version + 1) ve <c>already-requested</c> alır.
    /// <c>finish</c>'in jetonlu UPDATE'i satır bulamaz; kayıt yeniden okunur ve Unconfirmed geç teslimle Delivered olur
    /// (Version 2). Yeniden değerlendirme olmasaydı istek 500 dönerdi; jeton olmasaydı süre dolumu görünmezdi.
    /// </summary>
    [Fact]
    public async Task Finish_sure_dolumu_yarisinda_yeniden_okur_Delivered_olur()
    {
        var owner = Kit.NewId();
        var lease = Guid.NewGuid();
        (await _kit.BeginAsync(Kit.Body(lease, _pcA, owner, [owner]))).StatusCode.Should().Be(HttpStatusCode.OK);
        await _kit.Sales.ReadDbAsync(db => db.PaymentRequestRecords
            .Where(r => r.Id == lease)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1))));
        HttpResponseMessage? rival = null;
        _factory.Hook.Arm(
            db => db.ChangeTracker.Entries<PaymentRequestRecord>().Any(e => e.State == EntityState.Modified),
            async () => rival = await _kit.BeginAsync(Kit.Body(Guid.NewGuid(), _pcB, owner, [owner])));

        var resp = await _kit.FinishAsync(lease, Kit.FinishBody(_pcA, "delivered", netAmount: 550m));

        _factory.Hook.Injected.Should().BeTrue("rakip finish'in yazımından önce koşmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.Conflict, await rival.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(rival);
        problem.GetProperty("title").GetString().Should().Be("already-requested");
        problem.GetProperty("priors").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("unconfirmed").GetBoolean().Should().BeTrue();

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadFinishAsync(resp)).State.Should().Be("delivered");
        var saved = await _kit.RecordAsync(lease);
        saved!.State.Should().Be(PaymentRequestState.Delivered);
        saved.NetAmount.Should().Be(550m);
        saved.Version.Should().Be(2);
    }

    /// <summary>
    /// §10.2: aynı kaydın iki geri doldurması eşzamanlı (yanıtı kaybolan partinin yinelemesi ilkiyle yarışır). Rakip
    /// ilk isteğin eklemesinden önce yazar; ilk isteğin eklemesi birincil anahtara çarpar ve parti baştan değerlendirilir:
    /// <c>unchanged</c>. Tek satır; 500 yok.
    /// </summary>
    [Fact]
    public async Task Backfill_ayni_kaydin_eszamanli_eklemesi_unchanged_olur()
    {
        var owner = Kit.NewId();
        var recordId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddDays(-2);
        object Body(Guid replica) => Kit.BackfillBody(replica,
            Kit.BackfillItem(recordId, owner, [owner], at, orderIds: [Guid.NewGuid()], netAmount: 80m));
        var item = Body(_pcA);
        HttpResponseMessage? rival = null;
        _factory.Hook.Arm(
            db => db.ChangeTracker.Entries<PaymentRequestRecord>().Any(e => e.State == EntityState.Added),
            async () => rival = await _kit.BackfillAsync(item));

        var resp = await _kit.BackfillAsync(item);

        _factory.Hook.Injected.Should().BeTrue("rakip ilk isteğin eklemesinden önce koşmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.OK, await rival.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(rival)).Records.Should().Equal(new Kit.BackfillResult(recordId, "applied"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadBackfillAsync(resp)).Records.Should().Equal(new Kit.BackfillResult(recordId, "unchanged"));
        var saved = (await _kit.RecordsAsync()).Should().ContainSingle().Subject;
        saved.Kind.Should().Be(PaymentRequestKind.Record);
        saved.Members.Should().ContainSingle();
        saved.RecordOrders.Should().ContainSingle();
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tek atımlık kanca: koşulu tutan ilk kaydetmede, yazım diske inmeden önce rakibi çalıştırır ve bekler. Rakibin kendi
    /// kaydetmeleri kancayı yeniden tetiklemez (kanca rakipten önce boşalır).
    /// </summary>
    private sealed class InjectHook : SaveChangesInterceptor
    {
        private readonly object _gate = new();
        private Func<DbContext, bool>? _when;
        private Func<Task>? _inject;

        public bool Injected { get; private set; }

        public void Arm(Func<DbContext, bool> when, Func<Task> inject)
        {
            lock (_gate)
            {
                _when = when;
                _inject = inject;
                Injected = false;
            }
        }

        public void Disarm()
        {
            lock (_gate)
            {
                _when = null;
                _inject = null;
            }
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Func<Task>? inject = null;
            lock (_gate)
            {
                if (_when is not null && eventData.Context is { } db && _when(db))
                {
                    inject = _inject;
                    _when = null;
                    _inject = null;
                }
            }

            if (inject is not null)
            {
                await inject();
                Injected = true;
            }
            return result;
        }
    }

    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public InjectHook Hook { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
