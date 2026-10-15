using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST sales/commands</c> testlerinin ortak kurulumu (InMemory ve gerçek SQL Server). Satırlar doğrudan veritabanına
/// yazılır: komutların hedefi devredilmiş yayının siparişleridir ve onların nasıl geldiği (push, geri doldurma) burada
/// konu değil. Kişisel alanlar üretilir (repo herkese açık).
/// </summary>
public sealed class SalesCommandTestKit
{
    public const string WpfUserAgent = "OrderDeck-WPF/0.9.11";

    public static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;

    private SalesCommandTestKit(ApiFactory factory, HttpClient client, Guid customerId, Guid licenseId)
    {
        _factory = factory;
        Client = client;
        CustomerId = customerId;
        LicenseId = licenseId;
    }

    public HttpClient Client { get; }
    public Guid CustomerId { get; }
    public Guid LicenseId { get; }

    /// <summary>Komutu gönderen kopya.</summary>
    public Guid ReplicaId { get; } = Guid.NewGuid();

    /// <summary>Yayının sahibi kopya (PC-A).</summary>
    public Guid OwnerReplicaId { get; } = Guid.NewGuid();

    /// <summary>Kimliği doğrulanmış yayıncı ve ona ait bir lisans.</summary>
    public static async Task<SalesCommandTestKit> CreateAsync(ApiFactory factory, SalesSyncMode mode = SalesSyncMode.On)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var licenseId = await AddLicenseAsync(factory, customerId, mode);
        return new SalesCommandTestKit(factory, client, customerId, licenseId);
    }

    /// <summary>Aynı yayıncının ikinci lisansı (aynı istemciyle).</summary>
    public async Task<SalesCommandTestKit> SecondLicenseAsync(SalesSyncMode mode = SalesSyncMode.On) =>
        new(_factory, Client, CustomerId, await AddLicenseAsync(_factory, CustomerId, mode));

    private static async Task<Guid> AddLicenseAsync(ApiFactory factory, Guid customerId, SalesSyncMode mode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? now.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    // ── Kurulum ───────────────────────────────────────────────────────────────

    public enum Phase { Live, HandingOver, Released }

    /// <summary>Sahibi <see cref="OwnerReplicaId"/> (PC-A) — ya da verilen kopya — olan bir yayın; devrediliyor hâlinde
    /// sahibin bildirdiği gönderilmemiş etiket sayısıyla (<paramref name="unsentLabelCount"/>).</summary>
    public async Task<Guid> SeedSessionAsync(
        Phase phase, string? forceEndedBy = null, Guid? owner = null, int? unsentLabelCount = null)
    {
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseId,
            Title = "Akşam yayını",
            StartedAt = StartedAt,
            EndedAt = phase == Phase.Live ? null : StartedAt.AddHours(2),
            ReleasedAt = phase == Phase.Released ? StartedAt.AddHours(2) : null,
            Platforms = "youtube",
            UpdatedAt = StartedAt,
            OwnerReplicaId = owner ?? OwnerReplicaId,
            OwnerMachineName = "PC-A",
            ForceEndedBy = forceEndedBy,
            UnsentLabelCount = unsentLabelCount,
        };
        await WithDbAsync(db =>
        {
            db.StreamSessions.Add(session);
            return Task.CompletedTask;
        });
        return session.Id;
    }

    /// <summary>Basılmış bir sipariş; yayın devredilmişse devir anı kopyalanır (DM3).</summary>
    public async Task<Order> SeedOrderAsync(Guid? sessionId, Action<Order>? shape = null) =>
        (await SeedOrdersAsync(sessionId, 1, shape)).Single();

    /// <summary>Çok sayıda basılmış sipariş, tek kaydetmede (<see cref="SeedOrderAsync"/> biçiminde).</summary>
    public async Task<List<Order>> SeedOrdersAsync(Guid? sessionId, int count, Action<Order>? shape = null)
    {
        var orders = Enumerable.Range(0, count).Select(_ => new Order
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseId,
            SessionId = sessionId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Platform = "youtube",
            Username = $"u-{Guid.NewGuid():N}",
            DisplayName = $"ad-{Guid.NewGuid():N}",
            MessageText = $"yorum-{Guid.NewGuid():N}",
            Code = "A1",
            Price = 100m,
            AddedAt = StartedAt.AddMinutes(10),
            PrintedAt = StartedAt.AddMinutes(11),
            UpdatedAt = StartedAt.AddMinutes(11),
            SyncVersion = 1,
            OriginReplicaId = OwnerReplicaId,
        }).ToList();
        await WithDbAsync(async db =>
        {
            var releasedAt = sessionId is { } sid
                ? await db.StreamSessions.Where(s => s.Id == sid).Select(s => s.ReleasedAt).SingleAsync()
                : null;
            foreach (var order in orders)
            {
                order.SessionReleasedAt = releasedAt;
                shape?.Invoke(order);
            }
            db.Orders.AddRange(orders);
        });
        return orders;
    }

    public async Task<Shipment> SeedShipmentAsync(
        string customerId, ShipmentStatus status, decimal amount, bool derived, int version = 1,
        DateTimeOffset? createdAt = null)
    {
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseId,
            CustomerId = customerId,
            Status = status,
            CumulativeAmount = amount,
            CreatedAt = createdAt ?? StartedAt.AddHours(3),
            ShippedAt = status == ShipmentStatus.Shipped ? StartedAt.AddHours(4) : null,
            UpdatedAt = StartedAt.AddHours(3),
            Version = version,
            AmountDerivedAt = derived ? StartedAt.AddHours(3) : null,
            OriginReplicaId = ReplicaId,
        };
        await WithDbAsync(db =>
        {
            db.Shipments.Add(shipment);
            return Task.CompletedTask;
        });
        return shipment;
    }

    public async Task SeedReplicaAsync(Guid replicaId, DateTimeOffset lastSeenAt)
    {
        await WithDbAsync(db =>
        {
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = LicenseId,
                ReplicaId = replicaId,
                Protocol = 2,
                FeatureLevel = 3,
                FirstSeenAt = lastSeenAt.AddDays(-3),
                LastSeenAt = lastSeenAt,
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>Katalogda bir ürün ve o ürünün siparişe bağlı satış düşümü (orders/sync'in yazdığı gibi).</summary>
    public async Task<Guid> SeedProductAsync()
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseId,
            Code = "P" + Guid.NewGuid().ToString("N")[..8],
            Name = "Komut Ürünü",
            NameSearch = "komut urunu",
            DefaultPrice = 100m,
            CreatedAt = StartedAt,
            UpdatedAt = StartedAt,
        };
        await WithDbAsync(db =>
        {
            db.Products.Add(product);
            return Task.CompletedTask;
        });
        return product.Id;
    }

    public async Task SeedSaleMovementAsync(Order order)
    {
        await WithDbAsync(db =>
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                LicenseId = LicenseId,
                ProductId = order.ProductId!.Value,
                Quantity = -1,
                Reason = StockMovementReason.Sale,
                OrderId = order.Id,
                OccurredAt = order.AddedAt,
                CreatedAt = order.AddedAt,
            });
            return Task.CompletedTask;
        });
    }

    // ── İstek ─────────────────────────────────────────────────────────────────

    public string Url => $"/api/v1/licenses/{LicenseId}/sales/commands";

    /// <summary>Komut zarfı (v2, <c>seenMode = 1</c>).</summary>
    public object Body(
        Guid commandId, string kind, object payload, IEnumerable<(Guid Id, int Version)> expected,
        Guid? replicaId = null) => new
    {
        commandId,
        replicaId = replicaId ?? ReplicaId,
        protocol = 2,
        seenMode = 1,
        kind,
        payload,
        expected = expected.Select(e => new { orderId = e.Id, version = e.Version }).ToArray(),
    };

    /// <summary><c>cancel</c>; beklenen sürümler verilen satırlardan.</summary>
    public object Cancel(Guid commandId, string reason, params Order[] orders) =>
        Body(commandId, "cancel", new { items = orders.Select(o => o.Id).ToArray(), reason },
            orders.Select(o => (o.Id, o.SyncVersion)));

    /// <summary><c>uncancel</c>; beklenen sürümler verilen satırlardan.</summary>
    public object Uncancel(Guid commandId, params Order[] orders) =>
        Body(commandId, "uncancel", new { items = orders.Select(o => o.Id).ToArray() },
            orders.Select(o => (o.Id, o.SyncVersion)));

    /// <summary><c>set-code-price</c>; beklenen sürümler istemcinin gördüğü plan satırlarından.</summary>
    public object SetCodePrice(
        Guid commandId,
        Guid sessionId,
        string? code,
        decimal newPrice,
        IEnumerable<Order> plan,
        IEnumerable<Guid>? alsoSelected = null,
        IEnumerable<Guid>? paidFromBalance = null,
        Guid? replicaId = null) =>
        SetCodePrice(commandId, sessionId, code, newPrice, plan.Select(o => (o.Id, o.SyncVersion)), alsoSelected,
            paidFromBalance, replicaId);

    /// <summary><c>set-code-price</c>; istemcinin gördüğü plan Id ve sürüm olarak.</summary>
    public object SetCodePrice(
        Guid commandId,
        Guid sessionId,
        string? code,
        decimal newPrice,
        IEnumerable<(Guid Id, int Version)> plan,
        IEnumerable<Guid>? alsoSelected = null,
        IEnumerable<Guid>? paidFromBalance = null,
        Guid? replicaId = null) =>
        Body(commandId, "set-code-price",
            new
            {
                sessionId,
                code,
                alsoSelectedIds = (alsoSelected ?? []).ToArray(),
                newPrice,
                paidFromBalanceIds = (paidFromBalance ?? []).ToArray(),
            },
            plan,
            replicaId);

    /// <summary><c>confirm-backup</c>; beklenen sürüm yedeğin satırından.</summary>
    public object ConfirmBackup(Guid commandId, Order backup, decimal? newPrice = null) =>
        Body(commandId, "confirm-backup", new { backupOrderId = backup.Id, newPrice },
            [(backup.Id, backup.SyncVersion)]);

    /// <summary><c>add-backup</c>'ın müşterisi; kişisel alanlar üretilir.</summary>
    public sealed record BackupCustomer(string Id, string Platform, string Username, string? DisplayName)
    {
        public static BackupCustomer New(string? id = null) =>
            new(id ?? Guid.NewGuid().ToString("N"), "youtube", $"u-{Guid.NewGuid():N}", $"ad-{Guid.NewGuid():N}");
    }

    /// <summary><c>add-backup</c>; komut var olan satır yazmadığı için <c>expected</c> boş.</summary>
    public object AddBackup(
        Guid commandId,
        Guid newOrderId,
        Guid parentOrderId,
        BackupCustomer customer,
        string? messageText,
        decimal? price = null,
        string? code = null,
        Guid? replicaId = null) =>
        Body(commandId, "add-backup",
            new { newOrderId, parentOrderId, customer, messageText, price, code }, [], replicaId);

    /// <summary><c>mark-printed</c>; kâğıttaki sıra verilen sıradır (1'den), beklenen sürümler satırlardan.</summary>
    public object MarkPrinted(Guid commandId, string batchId, params Order[] orders) =>
        Body(commandId, "mark-printed",
            new { batchId, items = orders.Select((o, i) => new { orderId = o.Id, seq = i + 1 }).ToArray() },
            orders.Select(o => (o.Id, o.SyncVersion)));

    /// <summary><c>attach-shipment</c>; <c>expected</c> istemcinin gördüğü bağlanmamış aday kümesi (satırlardan).</summary>
    public object AttachShipment(
        Guid commandId, string customerId, Guid proposedShipmentId, IEnumerable<Order> candidates,
        Guid? replicaId = null) =>
        Body(commandId, "attach-shipment", new { customerId, proposedShipmentId },
            candidates.Select(o => (o.Id, o.SyncVersion)), replicaId);

    /// <summary><c>shipment-decision</c>; karar teldeki adıyla (<c>ship-now</c>, <c>hold</c>, <c>recipient-pays</c>),
    /// sipariş sürümü yok.</summary>
    public object ShipmentDecision(
        Guid commandId, Guid shipmentId, string decision, int expectedVersion, Guid? replicaId = null) =>
        Body(commandId, "shipment-decision", new { shipmentId, decision, expectedVersion }, [], replicaId);

    /// <summary><c>reprint</c>; yazmadığı için <c>expected</c> boş.</summary>
    public object Reprint(Guid commandId, IEnumerable<Guid> orderIds, Guid? replicaId = null) =>
        Body(commandId, "reprint", new { orderIds = orderIds.ToArray() }, [], replicaId);

    /// <summary><c>force-end</c>; onay teldeki gibi (<c>confirm</c>), sipariş sürümü yok.</summary>
    public object ForceEnd(Guid commandId, Guid sessionId, bool? confirm = true, Guid? replicaId = null) =>
        Body(commandId, "force-end", new { sessionId, confirm }, [], replicaId);

    public string SessionsUrl => $"/api/v1/licenses/{LicenseId}/sessions/sync";

    /// <summary>Yayın sahibinin (varsayılan PC-A) v2 kapanış gönderimi (<c>sessions/sync</c>, §4.1, §5.2):
    /// <paramref name="release"/> etiket kuyruğunun boş olduğunu söyler; değilse gönderilmemiş sayı taşır.</summary>
    public object OwnerClose(
        Guid sessionId, DateTimeOffset endedAt, bool release, int? unsentLabelCount = null, Guid? replicaId = null) =>
        new
        {
            sessions = new[]
            {
                new
                {
                    id = sessionId, title = "Akşam yayını", startedAt = StartedAt, endedAt = (DateTimeOffset?)endedAt,
                    platforms = "youtube", notes = (string?)null, release, machineName = "PC-A", unsentLabelCount,
                },
            },
            replicaId = replicaId ?? OwnerReplicaId,
            protocol = 2,
            seenMode = 1,
        };

    public Task<HttpResponseMessage> SendAsync(object body, string? userAgent = WpfUserAgent) =>
        SendToAsync(Url, body, userAgent);

    public async Task<HttpResponseMessage> SendToAsync(string url, object body, string? userAgent = WpfUserAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, body.GetType()),
        };
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return await Client.SendAsync(request);
    }

    // ── Okuma ─────────────────────────────────────────────────────────────────

    public async Task<Order> OrderAsync(Guid id) =>
        await ReadDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == id));

    public async Task<StreamSession> SessionAsync(Guid id) =>
        await ReadDbAsync(db => db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    /// <summary>Id'li siparişler, lisanstan bağımsız (Orders'ın birincil anahtarı yalnız Id).</summary>
    public async Task<List<Order>> OrdersWithIdAsync(Guid id) =>
        await ReadDbAsync(db => db.Orders.AsNoTracking().Where(o => o.Id == id).ToListAsync());

    /// <summary>Bir müşteri projeksiyonu; <paramref name="mergedInto"/> doluysa kopya, <paramref name="purged"/> ise
    /// KVKK ile silinmiş.</summary>
    public async Task SeedProjectionAsync(Guid id, Guid? mergedInto = null, bool purged = false)
    {
        await WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = LicenseId, Platform = "youtube", Username = $"p-{Guid.NewGuid():N}",
                UpdatedAt = now, MergedIntoId = mergedInto, PurgedAt = purged ? now.AddMinutes(-5) : null,
            });
            return Task.CompletedTask;
        });
    }

    public async Task<Shipment> ShipmentAsync(Guid id) =>
        await ReadDbAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id));

    /// <summary>Müşterinin bu lisanstaki bütün dosyaları.</summary>
    public async Task<List<Shipment>> ShipmentsOfAsync(string customerId) =>
        await ReadDbAsync(db => db.Shipments.AsNoTracking()
            .Where(s => s.LicenseId == LicenseId && s.CustomerId == customerId).ToListAsync());

    /// <summary>DM7, veritabanından: dosyaya bağlı, iptal edilmemiş, geçici yedek olmayan siparişlerin fiyat toplamı.</summary>
    public async Task<decimal> DerivedAmountInDbAsync(Guid shipmentId) =>
        await ReadDbAsync(async db => (await db.Orders.AsNoTracking()
                .Where(o => o.ShipmentId == shipmentId && o.CancelledAt == null && !o.IsTentativeBackup)
                .Select(o => o.Price)
                .ToListAsync())
            .Sum());

    public async Task<List<SalesCommandLog>> LogsAsync() =>
        await ReadDbAsync(db => db.SalesCommandLogs.AsNoTracking().Where(c => c.LicenseId == LicenseId).ToListAsync());

    public async Task<List<StockMovement>> MovementsAsync(Guid orderId) =>
        await ReadDbAsync(db => db.StockMovements.AsNoTracking()
            .Where(m => m.OrderId == orderId).OrderBy(m => m.CreatedAt).ToListAsync());

    /// <summary>
    /// DM5 denetimi: yayında koda (<see cref="LabelRules.SameCode"/>, bellekte) eşleşen ve <see cref="LabelRules.Classify"/>'ın
    /// düzeltilebilir saydığı siparişlerin farklı fiyatları. Tek fiyat kuralı tutuyorsa en çok bir değer.
    /// </summary>
    public async Task<List<decimal>> CorrectableCodePricesAsync(Guid sessionId, string code) =>
        await ReadDbAsync(async db =>
        {
            var orders = await db.Orders.AsNoTracking()
                .Where(o => o.LicenseId == LicenseId && o.SessionId == sessionId)
                .ToListAsync();
            var statuses = await db.Shipments.AsNoTracking()
                .Where(s => s.LicenseId == LicenseId)
                .ToDictionaryAsync(s => s.Id.ToString("N"), s => (SalesShipmentStatus)(int)s.Status);
            return orders
                .Select(o => o.ToLabelFacts())
                .Where(f => LabelRules.SameCode(f.Code, code)
                            && LabelRules.Classify(f, sid => statuses.TryGetValue(sid, out var s) ? s : null)
                            == LabelPriceClass.Correctable)
                .Select(f => f.Price)
                .Distinct()
                .ToList();
        });

    public async Task<WpfReplica?> ReplicaAsync(Guid replicaId) =>
        await ReadDbAsync(db => db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == LicenseId && r.ReplicaId == replicaId));

    public async Task WithDbAsync(Func<LicenseDbContext, Task> body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await body(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> ReadDbAsync<T>(Func<LicenseDbContext, Task<T>> body)
    {
        using var scope = _factory.Services.CreateScope();
        return await body(scope.ServiceProvider.GetRequiredService<LicenseDbContext>());
    }

    public static async Task<CommandResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<CommandResponse>())!;

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>200 yanıtının biçimi (<c>SalesCommandResponse</c>).</summary>
    public sealed record CommandResponse(
        string Status,
        List<SalesOrderItem> Orders,
        List<SalesShipmentItem> Shipments,
        List<SalesSessionItem> Sessions,
        JsonElement Result);
}
