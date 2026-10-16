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

    // ── finish ────────────────────────────────────────────────────────────────

    public string FinishUrl(Guid recordId) => $"/api/v1/licenses/{LicenseId}/payment-requests/{recordId}/finish";

    /// <summary><c>finish</c> gövdesi (v2, <c>seenMode = 1</c>). Sözlük: 400 testleri alanı siler ya da bozar.</summary>
    public static Dictionary<string, object?> FinishBody(
        Guid replicaId,
        string outcome,
        decimal netAmount = 0m,
        decimal? appliedAmount = 0m,
        bool fullyFromBalance = false) => new()
    {
        ["replicaId"] = replicaId,
        ["protocol"] = 2,
        ["seenMode"] = 1,
        ["outcome"] = outcome,
        ["netAmount"] = netAmount,
        ["appliedAmount"] = appliedAmount,
        ["fullyFromBalance"] = fullyFromBalance,
    };

    public Task<HttpResponseMessage> FinishAsync(Guid recordId, object body, string? userAgent = SalesKit.WpfUserAgent) =>
        Sales.SendToAsync(FinishUrl(recordId), body, userAgent);

    /// <summary><c>finish</c>'in 200 yanıtı (<see cref="PaymentRequestFinishResponse"/>).</summary>
    public sealed record FinishResponse(Guid RecordId, string State, DateTimeOffset? FinishedAt);

    public static async Task<FinishResponse> ReadFinishAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<FinishResponse>())!;

    // ── liste ─────────────────────────────────────────────────────────────────

    /// <summary><c>GET payment-requests?scopeKey&amp;memberIds</c>; üyeler yinelenen anahtarla.</summary>
    public string ListUrl(string? scopeKey, IEnumerable<string> memberIds)
    {
        var query = new List<string>();
        if (scopeKey is not null) query.Add("scopeKey=" + Uri.EscapeDataString(scopeKey));
        query.AddRange(memberIds.Select(m => "memberIds=" + Uri.EscapeDataString(m)));
        return $"/api/v1/licenses/{LicenseId}/payment-requests?{string.Join('&', query)}";
    }

    public Task<HttpResponseMessage> ListAsync(string? scopeKey, IEnumerable<string> memberIds) =>
        Sales.Client.GetAsync(ListUrl(scopeKey, memberIds));

    /// <summary>Liste öğesi (<see cref="PaymentRequestListItem"/>).</summary>
    public sealed record ListItem(
        Guid Id,
        Guid ReplicaId,
        string? Machine,
        string Kind,
        string State,
        decimal? NetAmount,
        decimal? AppliedAmount,
        bool? FullyFromBalance,
        decimal? ProductTotal,
        decimal? TotalBeforeBalance,
        DateTimeOffset StartedAt,
        DateTimeOffset? FinishedAt,
        List<Guid> OrderIds);

    public sealed record ListResponse(List<ListItem> Records);

    public static async Task<ListResponse> ReadListAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ListResponse>())!;

    // ── backfill ──────────────────────────────────────────────────────────────

    public string BackfillUrl => $"/api/v1/licenses/{LicenseId}/payment-requests/backfill";

    /// <summary>Geri doldurma öğesi. Sözlük: 400 testleri alanı siler ya da bozar.</summary>
    public static Dictionary<string, object?> BackfillItem(
        Guid recordId,
        string ownerId,
        IEnumerable<string> memberIds,
        DateTimeOffset at,
        string scopeKey = Cumulative,
        IEnumerable<Guid>? orderIds = null,
        string state = "delivered",
        decimal netAmount = 0m,
        decimal? appliedAmount = 0m,
        bool fullyFromBalance = false) => new()
    {
        ["recordId"] = recordId,
        ["ownerId"] = ownerId,
        ["memberIds"] = memberIds.ToArray(),
        ["scopeKey"] = scopeKey,
        ["orderIds"] = (orderIds ?? []).ToArray(),
        ["state"] = state,
        ["netAmount"] = netAmount,
        ["appliedAmount"] = appliedAmount,
        ["fullyFromBalance"] = fullyFromBalance,
        ["at"] = at,
    };

    /// <summary>Geri doldurma partisi (v2, <c>seenMode = 0</c>: mod Off'taki Ws'nin ilk açılışı).</summary>
    public static Dictionary<string, object?> BackfillBody(Guid replicaId, params object?[] records) => new()
    {
        ["replicaId"] = replicaId,
        ["protocol"] = 2,
        ["seenMode"] = 0,
        ["records"] = records,
    };

    public Task<HttpResponseMessage> BackfillAsync(object body, string? userAgent = SalesKit.WpfUserAgent) =>
        Sales.SendToAsync(BackfillUrl, body, userAgent);

    public sealed record BackfillResult(Guid RecordId, string Status);

    public sealed record BackfillResponse(List<BackfillResult> Records);

    public static async Task<BackfillResponse> ReadBackfillAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<BackfillResponse>())!;

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

    /// <summary>Makine adı bilinen bir kopya (<c>sales/hello</c>'nun yazdığı gibi); <paramref name="previousReplicaId"/>
    /// doluysa yedekten geri yüklenmiş kopya (§8.7).</summary>
    public async Task SeedReplicaAsync(Guid replicaId, string machineName, Guid? previousReplicaId = null)
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
                PreviousReplicaId = previousReplicaId,
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
        Guid? id = null,
        IEnumerable<Guid>? orderIds = null,
        decimal? appliedAmount = null,
        bool? fullyFromBalance = null,
        decimal? productTotal = null,
        decimal? totalBeforeBalance = null,
        int version = 0)
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
            ProductTotal = productTotal,
            TotalBeforeBalance = totalBeforeBalance,
            NetAmount = netAmount,
            AppliedAmount = appliedAmount,
            FullyFromBalance = fullyFromBalance,
            RequestHash = PaymentPersonKey.Hash($"istek-{recordId:N}"),
            StartedAt = startedAt,
            LeaseUntil = kind == PaymentRequestKind.Lease ? startedAt.AddSeconds(120) : null,
            FinishedAt = finishedAt,
            Version = version,
            Members = members
                .Select(m => new PaymentRequestMember { LicenseId = license, WpfCustomerId = m, ScopeKey = scopeKey })
                .ToList(),
            RecordOrders = (orderIds ?? [])
                .Select(o => new PaymentRequestRecordOrder { LicenseId = license, OrderId = o })
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
