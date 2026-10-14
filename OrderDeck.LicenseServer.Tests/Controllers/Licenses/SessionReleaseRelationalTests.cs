using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Spec 2026-10-13 §10.1-2 (yazma tarafı), gerçek SQL Server: devir, yayının siparişlerini aynı işlemde damgalar;
/// damgalanan her siparişin <c>ChangeSeq</c>'i oturumunkinden büyüktür (akış FK sırası, §4.1 "sıra önemli");
/// devirden sonra eklenen sipariş devir anını kopyalar (DM3). InMemory'de ne işlem ne rowversion ne de
/// <c>ExecuteUpdate</c> var — üçü de ancak burada kanıtlanır. Akışta görünürlük T6'nın testlerinde.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SessionReleaseRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddHours(2);

    private readonly SqlServerContainerFixture _sql;
    private FaultFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;
    private readonly Guid _replicaId = Guid.NewGuid();

    public SessionReleaseRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new FaultFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _licenseId = await SeedLicenseAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sahibin <c>Release = true</c> kapanışı: oturum devredilir, yayının bütün siparişleri aynı devir anıyla
    /// damgalanır ve her birinin ChangeSeq'i oturumunkinden büyüktür. Başka yayının siparişine dokunulmaz.
    /// </summary>
    [Fact]
    public async Task Devir_siparisleri_damgalar_ve_siparislerin_ChangeSeq_i_oturumdan_buyuk()
    {
        var sessionId = Guid.NewGuid();
        var other = Guid.NewGuid();
        await PushSessionAsync(sessionId, endedAt: null, release: false);
        await PushSessionAsync(other, endedAt: null, release: false);
        var orders = new[] { await PushOrderAsync(sessionId), await PushOrderAsync(sessionId), await PushOrderAsync(sessionId) };
        var unrelated = await PushOrderAsync(other);

        await PushSessionAsync(sessionId, EndedAt, release: true);

        var session = await SessionAsync(sessionId);
        session.ReleasedAt.Should().NotBeNull();
        foreach (var id in orders)
        {
            var order = await OrderAsync(id);
            order.SessionReleasedAt.Should().Be(session.ReleasedAt);
            order.ChangeSeq.Should().BeGreaterThan(session.ChangeSeq,
                "akış oturumu siparişlerinden önce görmeli: önce oturum, sonra siparişler yazılır");
        }
        (await OrderAsync(unrelated)).SessionReleasedAt.Should().BeNull();
    }

    /// <summary>
    /// Aynı işlem: siparişlerin damgası düşerse (enjekte edilmiş hata) oturumun devri ve aynı gönderimin bütün
    /// yazımları geri alınır — "oturum devredildi ama siparişler akışta değil" ara durumu oluşmaz. Hata kalkınca
    /// istemcinin yeniden gönderimi devreder.
    /// </summary>
    [Fact]
    public async Task Siparis_damgasi_duserse_devir_de_geri_alinir()
    {
        var sessionId = Guid.NewGuid();
        await PushSessionAsync(sessionId, endedAt: null, release: false);
        var orderId = await PushOrderAsync(sessionId);

        _factory.Fault.FailOrdersStamp = true;
        HttpStatusCode status;
        try
        {
            status = (await PostSessionAsync(sessionId, EndedAt, release: true)).StatusCode;
        }
        catch (Exception)
        {
            // TestServer işlenmemiş istisnayı istemciye fırlatır; prod'da 500.
            status = HttpStatusCode.InternalServerError;
        }
        _factory.Fault.FailOrdersStamp = false;

        _factory.Fault.Hits.Should().Be(1, "hata siparişlerin damgasında enjekte edildi");
        status.Should().NotBe(HttpStatusCode.OK);

        var rolledBack = await SessionAsync(sessionId);
        rolledBack.ReleasedAt.Should().BeNull();
        rolledBack.EndedAt.Should().BeNull("kapanışın kendisi de aynı işlemdeydi");
        (await OrderAsync(orderId)).SessionReleasedAt.Should().BeNull();

        await PushSessionAsync(sessionId, EndedAt, release: true);
        var released = await SessionAsync(sessionId);
        released.ReleasedAt.Should().NotBeNull();
        (await OrderAsync(orderId)).SessionReleasedAt.Should().Be(released.ReleasedAt);
    }

    /// <summary>
    /// DM3: devirden sonra eklenen sipariş (v1 ve v2 zarfı) oturumun devir anını kopyalar; ChangeSeq'i oturumunkinden
    /// büyük olduğu için akışta oturumundan sonra gelir.
    /// </summary>
    [Fact]
    public async Task Devirden_sonra_eklenen_siparis_devir_anini_kopyalar()
    {
        var sessionId = Guid.NewGuid();
        await PushSessionAsync(sessionId, EndedAt, release: true);
        var session = await SessionAsync(sessionId);
        session.ReleasedAt.Should().NotBeNull();

        var v1 = await PushOrderAsync(sessionId, v2: false);
        var v2 = await PushOrderAsync(sessionId, v2: true);

        foreach (var id in new[] { v1, v2 })
        {
            var order = await OrderAsync(id);
            order.SessionReleasedAt.Should().Be(session.ReleasedAt);
            order.ChangeSeq.Should().BeGreaterThan(session.ChangeSeq);
        }
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private string Url(string endpoint) => $"/api/v1/licenses/{_licenseId}/{endpoint}";

    private Task<HttpResponseMessage> PostSessionAsync(Guid sessionId, DateTimeOffset? endedAt, bool release) =>
        _client.PostAsJsonAsync(Url("sessions/sync"), new
        {
            sessions = new[]
            {
                new
                {
                    id = sessionId, title = "Akşam yayını", startedAt = StartedAt, endedAt, platforms = "youtube",
                    notes = (string?)null, release, machineName = "PC-A", unsentLabelCount = (int?)null,
                },
            },
            replicaId = _replicaId,
            protocol = 2,
            seenMode = 0,
        });

    private async Task PushSessionAsync(Guid sessionId, DateTimeOffset? endedAt, bool release)
    {
        var resp = await PostSessionAsync(sessionId, endedAt, release);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private async Task<Guid> PushOrderAsync(Guid sessionId, bool v2 = true)
    {
        var orderId = Guid.NewGuid();
        var item = new
        {
            id = orderId, sessionId = (Guid?)sessionId, customerId = $"c-{Guid.NewGuid():N}", platform = "youtube",
            username = $"u-{Guid.NewGuid():N}", displayName = (string?)null, messageText = "A1", code = "A1",
            price = 100m, addedAt = StartedAt.AddMinutes(10), printedAt = (DateTimeOffset?)null,
            cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
            isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
        };
        object body = v2
            ? new { orders = new[] { item }, replicaId = _replicaId, protocol = 2, seenMode = 0 }
            : new { orders = new[] { item } };
        var resp = await _client.PostAsJsonAsync(Url("orders/sync"), body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return orderId;
    }

    private async Task<StreamSession> SessionAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    private async Task<Order> OrderAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<Guid> SeedLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary><see cref="RelationalApiFactory"/> + siparişlerin devir damgasını düşüren komut kancası.</summary>
    private sealed class FaultFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public OrdersStampFault Fault { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Fault);
    }

    /// <summary>Açıkken Orders'a <c>SessionReleasedAt</c> yazan UPDATE komutunu çalışmadan önce düşürür.</summary>
    private sealed class OrdersStampFault : DbCommandInterceptor
    {
        private int _hits;

        public volatile bool FailOrdersStamp;

        public int Hits => Volatile.Read(ref _hits);

        private bool Matches(DbCommand command)
        {
            var hit = FailOrdersStamp
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[Orders]", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[SessionReleasedAt]", StringComparison.OrdinalIgnoreCase);
            if (hit) Interlocked.Increment(ref _hits);
            return hit;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Matches(command)
                ? throw new InvalidOperationException("enjekte edilmiş hata: sipariş damgası")
                : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            Matches(command)
                ? throw new InvalidOperationException("enjekte edilmiş hata: sipariş damgası")
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
