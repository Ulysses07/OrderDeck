using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>payment-requests</c> testlerinin ortak kurulumu (InMemory ve gerçek SQL Server). Lisans ve istemci
/// <see cref="SalesCommandTestKit"/>'ten; projeksiyonlar, kopyalar ve önceki kayıtlar doğrudan veritabanına yazılır.
/// Kişisel alan yoktur: Id, GroupId ve makine adı üretilir.
/// </summary>
public sealed class PaymentRequestTestKit
{
    public const string Cumulative = "cumulative";

    private PaymentRequestTestKit(SalesKit sales) => Sales = sales;

    public SalesKit Sales { get; }

    public Guid LicenseId => Sales.LicenseId;

    public string BeginUrl => $"/api/v1/licenses/{LicenseId}/payment-requests/begin";

    public static async Task<PaymentRequestTestKit> CreateAsync(ApiFactory factory, SalesSyncMode mode = SalesSyncMode.On) =>
        new(await SalesKit.CreateAsync(factory, mode));

    /// <summary>Yeni bir müşteri Id'si ("N" hex).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Bir yayının kapsamı.</summary>
    public static string SessionScope(Guid sessionId) => $"session:{sessionId:N}";

    // ── İstek ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>begin</c> gövdesi (v2, <c>seenMode = 1</c>). Sözlük: 400 testleri alanı siler ya da bozar. Varsayılanlar boş
    /// bir sunucu kümesine ve yazılmamış kargo ayarına uyar (§6.4-f, g): sipariş yok, ürün toplamı 0, ayar sürümü 0,
    /// <paramref name="totalBeforeBalance"/> verilmezse ürün toplamı (kargo yok).
    /// </summary>
    public static Dictionary<string, object?> Body(
        Guid recordId,
        Guid replicaId,
        string ownerId,
        IEnumerable<string> memberIds,
        string scopeKey = Cumulative,
        IEnumerable<Guid>? orderIds = null,
        decimal productTotal = 0m,
        decimal? totalBeforeBalance = null,
        bool recipientPays = false,
        int shippingSettingsVersion = 0,
        IEnumerable<Guid>? confirmedPriorIds = null) => new()
    {
        ["recordId"] = recordId,
        ["replicaId"] = replicaId,
        ["protocol"] = 2,
        ["seenMode"] = 1,
        ["ownerId"] = ownerId,
        ["memberIds"] = memberIds.ToArray(),
        ["scopeKey"] = scopeKey,
        ["orderIds"] = (orderIds ?? []).ToArray(),
        ["productTotal"] = productTotal,
        ["totalBeforeBalance"] = totalBeforeBalance ?? productTotal,
        ["recipientPays"] = recipientPays,
        ["shippingSettingsVersion"] = shippingSettingsVersion,
        ["confirmedPriorIds"] = (confirmedPriorIds ?? []).ToArray(),
    };

    public Task<HttpResponseMessage> BeginAsync(object body, string? userAgent = SalesKit.WpfUserAgent) =>
        Sales.SendToAsync(BeginUrl, body, userAgent);

    /// <summary>200 yanıtının biçimi (<see cref="PaymentRequestBeginResponse"/>).</summary>
    public sealed record BeginResponse(Guid RecordId, string State, DateTimeOffset? LeaseUntil, List<string> Members);

    public static async Task<BeginResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<BeginResponse>())!;

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>Bir müşteri projeksiyonu; <paramref name="mergedInto"/> doluysa kopya. Id üretilir.</summary>
    public async Task<string> SeedProjectionAsync(string? groupId, Guid? mergedInto = null)
    {
        var id = Guid.NewGuid();
        await Sales.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id,
                LicenseId = LicenseId,
                Platform = "youtube",
                Username = $"p-{Guid.NewGuid():N}",
                GroupId = groupId,
                MergedIntoId = mergedInto,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });
        return id.ToString("N");
    }

    /// <summary>Devredilmiş bir yayın (sahibi <see cref="SalesCommandTestKit.OwnerReplicaId"/>).</summary>
    public Task<Guid> SeedReleasedSessionAsync() => Sales.SeedSessionAsync(SalesKit.Phase.Released);

    /// <summary>
    /// Verilen müşteriye basılmış bir sipariş (toplama girer); yayın verilmezse yeni, devredilmiş bir yayında.
    /// <paramref name="shape"/> onu toplamın dışına itebilir (basılmamış, iptal, geçici yedek).
    /// </summary>
    public async Task<Order> SeedOrderAsync(
        string customerId, decimal price, Guid? sessionId = null, Action<Order>? shape = null)
    {
        var session = sessionId ?? await SeedReleasedSessionAsync();
        return await Sales.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customerId;
            o.Price = price;
            shape?.Invoke(o);
        });
    }

    /// <summary>Lisans geneli kargo ayarı, verilen sürümle (CLI'ın yazdığı gibi).</summary>
    public async Task SeedShippingAsync(int version, decimal? threshold, decimal? fee)
    {
        await Sales.WithDbAsync(db =>
        {
            db.LicenseShippingSettings.Add(new LicenseShippingSettings
            {
                LicenseId = LicenseId,
                FreeShippingThreshold = threshold,
                ShippingFee = fee,
                Version = version,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = LicenseShippingSettings.UpdatedByCli,
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>Makine adı bilinen bir kopya (<c>sales/hello</c>'nun yazdığı gibi).</summary>
    public async Task SeedReplicaAsync(Guid replicaId, string machineName)
    {
        await Sales.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = LicenseId,
                ReplicaId = replicaId,
                MachineName = machineName,
                Protocol = 2,
                FeatureLevel = 3,
                FirstSeenAt = now.AddDays(-3),
                LastSeenAt = now.AddMinutes(-1),
            });
            return Task.CompletedTask;
        });
    }

    /// <summary>Önceki bir kayıt, verilen kişi anahtarı ve üyelerle (ham Id'ler). Kira ise kira sonu başlangıçtan 120 sn
    /// sonra.</summary>
    public async Task<PaymentRequestRecord> SeedRecordAsync(
        string personKey,
        IEnumerable<string> members,
        PaymentRequestKind kind,
        PaymentRequestState state,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt = null,
        decimal? netAmount = null,
        string scopeKey = Cumulative,
        Guid? replicaId = null,
        string? machineName = "PC-X",
        Guid? licenseId = null,
        Guid? id = null)
    {
        var license = licenseId ?? LicenseId;
        var recordId = id ?? Guid.NewGuid();
        var record = new PaymentRequestRecord
        {
            Id = recordId,
            LicenseId = license,
            PersonKey = personKey,
            PersonKeyHash = PaymentPersonKey.Hash(personKey),
            ScopeKey = scopeKey,
            OwnerWpfCustomerId = members.First(),
            ReplicaId = replicaId ?? Guid.NewGuid(),
            MachineName = machineName,
            Kind = kind,
            State = state,
            NetAmount = netAmount,
            RequestHash = PaymentPersonKey.Hash($"istek-{recordId:N}"),
            StartedAt = startedAt,
            LeaseUntil = kind == PaymentRequestKind.Lease ? startedAt.AddSeconds(120) : null,
            FinishedAt = finishedAt,
            Members = members
                .Select(m => new PaymentRequestMember { LicenseId = license, WpfCustomerId = m, ScopeKey = scopeKey })
                .ToList(),
        };
        await Sales.WithDbAsync(db =>
        {
            db.PaymentRequestRecords.Add(record);
            return Task.CompletedTask;
        });
        return record;
    }

    // ── Okuma ─────────────────────────────────────────────────────────────────

    /// <summary>Lisansın bütün kayıtları, üyeleri ve siparişleriyle.</summary>
    public async Task<List<PaymentRequestRecord>> RecordsAsync() =>
        await Sales.ReadDbAsync(db => db.PaymentRequestRecords.AsNoTracking()
            .Include(r => r.Members)
            .Include(r => r.RecordOrders)
            .Where(r => r.LicenseId == LicenseId)
            .ToListAsync());

    public async Task<PaymentRequestRecord?> RecordAsync(Guid id) =>
        await Sales.ReadDbAsync(db => db.PaymentRequestRecords.AsNoTracking()
            .Include(r => r.Members)
            .Include(r => r.RecordOrders)
            .SingleOrDefaultAsync(r => r.Id == id));

    /// <summary>Bir kaydın üye ve sipariş satırları, kayıttan bağımsız (yarım yazım denetimi).</summary>
    public async Task<(int Members, int Orders)> ChildRowsAsync(Guid recordId) =>
        await Sales.ReadDbAsync(async db => (
            await db.PaymentRequestMembers.CountAsync(m => m.RecordId == recordId),
            await db.PaymentRequestRecordOrders.CountAsync(o => o.RecordId == recordId)));
}
