using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Bir komutun uygulama kapsamı (spec 2026-10-13 §4.6 adım 3–5): işleyicinin kullandığı ortak denetimler ve yazım
/// kaydı. Hepsi aynı DbContext'te ve servisin açtığı işlemde; denetimlerin her biri ret olarak
/// <see cref="SalesCommandRejection"/> atar, hiçbiri yazmaz. Kaydetmeyi, defteri, dosya türetmesini ve günlüğü servis
/// yapar.
/// </summary>
public sealed class SalesCommandScope
{
    private readonly LicenseDbContext _db;
    private readonly IReadOnlyDictionary<Guid, int> _expected;
    private readonly Dictionary<Guid, Order> _orders = new();
    private readonly Dictionary<Guid, Guid?> _shipmentBefore = new();
    private readonly Dictionary<Guid, HashSet<OrderUnit>> _changes = new();
    private readonly List<Guid> _changeOrder = new();

    internal SalesCommandScope(
        LicenseDbContext db, Guid licenseId, Guid replicaId, DateTimeOffset now, IReadOnlyList<SalesCommandExpected> expected)
    {
        _db = db;
        LicenseId = licenseId;
        ReplicaId = replicaId;
        Now = now;
        _expected = expected.ToDictionary(e => e.OrderId, e => e.Version);
    }

    public Guid LicenseId { get; }

    /// <summary>Komutu gönderen kopya.</summary>
    public Guid ReplicaId { get; }

    /// <summary>Komutun sunucu anı: yazılan her zaman alanı ve birim damgası bu (istemci saati hiçbir yerde yok, §2).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>
    /// Siparişleri bu lisanstan izleyerek yükler (birim yazımı <c>SyncVersion</c> jetonu altında). Kargo bağının yükleme
    /// anındaki değeri saklanır: bağ değişirse eski ve yeni dosya türetilir (K7). Bulunamayan sipariş 400
    /// <c>unknown-order</c>. Aynı sipariş ikinci kez okunmaz.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Order>> LoadOrdersAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var missing = orderIds.Where(id => !_orders.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var loaded = await _db.Orders
                .Where(o => o.LicenseId == LicenseId && missing.Contains(o.Id))
                .ToListAsync(ct);
            foreach (var order in loaded)
            {
                _orders[order.Id] = order;
                _shipmentBefore[order.Id] = order.ShipmentId;
            }
        }

        var unknown = orderIds.Where(id => !_orders.ContainsKey(id)).Distinct().ToList();
        if (unknown.Count > 0) throw SalesCommandRejection.UnknownOrders(unknown);
        return orderIds.Distinct().ToDictionary(id => id, id => _orders[id]);
    }

    /// <summary>
    /// Görünürlük (§4.6 adım 3, DM1, Y1): fiyat, iptal ya da yedek birimine dokunulan her siparişin yayını devredilmiş
    /// olmalı. Canlı, devrediliyor ya da yayınsız (SessionId boş) sipariş 409 <c>session-live</c>: canlı yayının
    /// birimlerini yalnız sahibi yazar; devrediliyor yayının etiketleri sahipte bekliyor olabilir (Y8). Ret, ilk
    /// görünmez siparişin yayınının sahibini ve sahibin son kalp atışını taşır. Kargo bağı bu denetime girmez (K4).
    /// </summary>
    public async Task EnsureReleasedAsync(IEnumerable<Order> orders, CancellationToken ct)
    {
        var list = orders.ToList();
        var sessionIds = list.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value).Distinct().ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, SessionVisibility>()
            : await _db.StreamSessions.AsNoTracking()
                .Where(s => s.LicenseId == LicenseId && sessionIds.Contains(s.Id))
                .Select(s => new SessionVisibility(s.Id, s.ReleasedAt, s.OwnerReplicaId, s.OwnerMachineName))
                .ToDictionaryAsync(s => s.Id, ct);

        foreach (var order in list)
        {
            if (order.SessionId is not { } sessionId)
                throw SalesCommandRejection.SessionLive(null, null, null);
            var session = sessions.GetValueOrDefault(sessionId);
            if (session?.ReleasedAt is not null) continue;

            DateTimeOffset? ownerLastSeenAt = session?.OwnerReplicaId is { } owner
                ? await _db.WpfReplicas.AsNoTracking()
                    .Where(r => r.LicenseId == LicenseId && r.ReplicaId == owner)
                    .Select(r => (DateTimeOffset?)r.LastSeenAt)
                    .SingleOrDefaultAsync(ct)
                : null;
            throw SalesCommandRejection.SessionLive(sessionId, session?.OwnerMachineName, ownerLastSeenAt);
        }
    }

    /// <summary>
    /// Beklenen sürüm (§4.6 adım 4): verilen her siparişin istemcinin gördüğü sürümü (<c>expected</c>) güncel
    /// <c>Version</c>'a eşit olmalı; eksik ya da farklıysa 409 <c>stale</c> ve güncel satırlar, hiçbir şey yazılmaz.
    /// </summary>
    public void EnsureExpectedVersions(IEnumerable<Order> orders)
    {
        foreach (var order in orders)
        {
            if (!_expected.TryGetValue(order.Id, out var version) || version != order.SyncVersion)
                throw SalesCommandRejection.Stale("Satırlar bu arada değişti; güncel sürümlerle yeniden deneyin.");
        }
    }

    /// <summary>İşlem kapsamlı uygulama kilidi (<see cref="SalesCommandLock"/>); zaman aşımı 409 <c>stale</c>.</summary>
    public Task AcquireLockAsync(string resource, CancellationToken ct) =>
        SalesCommandLock.AcquireAsync(_db, resource, SalesCommandLock.DefaultTimeout, ct);

    /// <summary>
    /// Siparişin bu komutla değiştiğini bildirir: ilk bildirimde <c>SyncVersion</c> bir artar (jeton; komutun beklenen
    /// sürümü) ve <c>UpdatedAt</c> = <see cref="Now"/>. Birimler dosya türetmesini (K7) ve defteri belirler. Yalnız
    /// <see cref="LoadOrdersAsync"/> ile yüklenmiş sipariş.
    /// </summary>
    public void MarkChanged(Order order, params OrderUnit[] units)
    {
        if (!_orders.TryGetValue(order.Id, out var tracked) || !ReferenceEquals(tracked, order))
            throw new InvalidOperationException("Yalnız kapsamın yüklediği sipariş değişmiş bildirilir.");
        if (!_changes.TryGetValue(order.Id, out var set))
        {
            _changes[order.Id] = set = new HashSet<OrderUnit>();
            _changeOrder.Add(order.Id);
            order.SyncVersion++;
            order.UpdatedAt = Now;
        }
        set.UnionWith(units);
    }

    /// <summary>Kapsamın izlediği siparişler (değişenler bellekteki hâlleriyle).</summary>
    internal IReadOnlyCollection<Order> TrackedOrders => _orders.Values;

    /// <summary>Değişen siparişler bildirim sırasıyla: yükleme anındaki kargo bağı ve değişen birimler.</summary>
    internal IReadOnlyList<SalesCommandChange> Changes =>
        _changeOrder.Select(id => new SalesCommandChange(_orders[id], _shipmentBefore[id], _changes[id])).ToList();

    private sealed record SessionVisibility(
        Guid Id, DateTimeOffset? ReleasedAt, Guid? OwnerReplicaId, string? OwnerMachineName);
}

/// <summary>Kapsamın kaydettiği bir sipariş değişikliği.</summary>
internal sealed record SalesCommandChange(Order Order, Guid? ShipmentBefore, IReadOnlyCollection<OrderUnit> Units);
