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
    private readonly ShipmentAmountDeriver _shipmentAmounts;
    private readonly IReadOnlyDictionary<Guid, int> _expected;
    private readonly Dictionary<Guid, Order> _orders = new();
    private readonly Dictionary<Guid, Guid?> _shipmentBefore = new();
    private readonly Dictionary<Guid, HashSet<OrderUnit>> _changes = new();
    private readonly List<Guid> _changeOrder = new();
    private readonly HashSet<Guid> _inserted = new();
    private readonly Dictionary<Guid, Shipment> _shipments = new();
    private readonly HashSet<Guid> _touchedShipments = new();
    private readonly List<Guid> _derivedShipments = new();
    private readonly HashSet<Guid> _insertedShipments = new();

    internal SalesCommandScope(
        LicenseDbContext db,
        ShipmentAmountDeriver shipmentAmounts,
        Guid licenseId,
        Guid replicaId,
        DateTimeOffset now,
        IReadOnlyList<SalesCommandExpected> expected)
    {
        _db = db;
        _shipmentAmounts = shipmentAmounts;
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
    public Task EnsureReleasedAsync(IEnumerable<Order> orders, CancellationToken ct) =>
        EnsureSessionsAsync(orders, allowCallerOwned: false, ct);

    /// <summary>
    /// Okuma görünürlüğü (yazmayan komut, ör. <c>reprint</c>): her siparişin yayını devredilmiş ya da çağıranın kendi
    /// yayını (sahibi çağıran kopya) olmalı — §4.6 adım 3'teki "çağıranın görebildiği siparişler". Başka kopyanın canlı
    /// ya da devrediliyor yayını (Y1, Y8) ve yayınsız sipariş 409 <c>session-live</c>
    /// (<see cref="EnsureReleasedAsync"/> ile aynı ret).
    /// </summary>
    public Task EnsureVisibleAsync(IEnumerable<Order> orders, CancellationToken ct) =>
        EnsureSessionsAsync(orders, allowCallerOwned: true, ct);

    private async Task EnsureSessionsAsync(IEnumerable<Order> orders, bool allowCallerOwned, CancellationToken ct)
    {
        var list = orders.ToList();
        var sessionIds = list.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value).Distinct().ToList();
        var sessions = await ReadVisibilityAsync(sessionIds, ct);

        foreach (var order in list)
        {
            if (order.SessionId is not { } sessionId)
                throw SalesCommandRejection.SessionLive(null, null, null);
            var session = sessions.GetValueOrDefault(sessionId);
            if (session?.ReleasedAt is not null) continue;
            if (allowCallerOwned && session?.OwnerReplicaId == ReplicaId) continue;
            throw await SessionLiveAsync(sessionId, session, ct);
        }
    }

    /// <summary>
    /// Komutun hedef yayını (ör. <c>set-code-price</c>'ın <c>sessionId</c>'si) bu lisansta olmalı (yoksa 400
    /// <c>unknown-session</c>) ve devredilmiş olmalı (yoksa 409 <c>session-live</c>, <see cref="EnsureReleasedAsync"/>
    /// ile aynı ret). Devredilmemiş yayının siparişleri sunucuda eksik olabilir — canlı yayının etiketleri sahipte,
    /// devrediliyor yayınınkiler gönderilmeyi bekliyor (Y1, Y8) — ve o yayından sunucunun kurduğu plan eksik kalırdı.
    /// </summary>
    /// <returns>Yayının devir anı (<c>ReleasedAt</c>): devredilmiş yayına eklenen sipariş onu kopyalar (DM3).</returns>
    public async Task<DateTimeOffset> EnsureSessionReleasedAsync(Guid sessionId, CancellationToken ct)
    {
        var session = (await ReadVisibilityAsync([sessionId], ct)).GetValueOrDefault(sessionId)
            ?? throw SalesCommandRejection.UnknownSession(sessionId);
        return session.ReleasedAt ?? throw await SessionLiveAsync(sessionId, session, ct);
    }

    private async Task<Dictionary<Guid, SessionVisibility>> ReadVisibilityAsync(
        IReadOnlyCollection<Guid> sessionIds, CancellationToken ct) =>
        sessionIds.Count == 0
            ? new Dictionary<Guid, SessionVisibility>()
            : await _db.StreamSessions.AsNoTracking()
                .Where(s => s.LicenseId == LicenseId && sessionIds.Contains(s.Id))
                .Select(s => new SessionVisibility(s.Id, s.ReleasedAt, s.OwnerReplicaId, s.OwnerMachineName))
                .ToDictionaryAsync(s => s.Id, ct);

    /// <summary>409 <c>session-live</c>: yayının sahibi bilgisayar ve sahip kopyanın son kalp atışıyla.</summary>
    private async Task<SalesCommandRejection> SessionLiveAsync(
        Guid sessionId, SessionVisibility? session, CancellationToken ct)
    {
        DateTimeOffset? ownerLastSeenAt = session?.OwnerReplicaId is { } owner
            ? await _db.WpfReplicas.AsNoTracking()
                .Where(r => r.LicenseId == LicenseId && r.ReplicaId == owner)
                .Select(r => (DateTimeOffset?)r.LastSeenAt)
                .SingleOrDefaultAsync(ct)
            : null;
        return SalesCommandRejection.SessionLive(sessionId, session?.OwnerMachineName, ownerLastSeenAt);
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

    /// <summary>
    /// Plan karşılaştırması (<c>set-code-price</c>, §4.6): <c>expected</c> tam olarak planın satırlarını ve güncel
    /// sürümlerini taşımalı — eksik, fazla ya da farklı sürümlü satır uyuşmazlıktır.
    /// </summary>
    public bool MatchesExpected(IReadOnlyCollection<Order> plan) =>
        plan.Count == _expected.Count
        && plan.All(o => _expected.TryGetValue(o.Id, out var version) && version == o.SyncVersion);

    /// <summary>İşlem kapsamlı uygulama kilidi (<see cref="SalesCommandLock"/>); zaman aşımı 409 <c>stale</c>.</summary>
    public Task AcquireLockAsync(string resource, CancellationToken ct) =>
        SalesCommandLock.AcquireAsync(_db, resource, SalesCommandLock.DefaultTimeout, ct);

    /// <summary>
    /// Yayın kilitlerini (<see cref="SalesCommandLock.SessionResource"/>) tekil ve sıralı alır: aynı yayınları isteyen
    /// iki komut kilitleri aynı sırayla ister, birbirini kilitlenmeye sokmaz.
    /// </summary>
    public async Task AcquireSessionLocksAsync(IEnumerable<Guid> sessionIds, CancellationToken ct)
    {
        foreach (var sessionId in sessionIds.Distinct().Order())
            await AcquireLockAsync(SalesCommandLock.SessionResource(LicenseId, sessionId), ct);
    }

    /// <summary>
    /// Siparişlerin yayınını ve kargo bağını İZLEMEDEN okur — kilit ya da dosya yüklemesi için ön okuma. Yayın oluşum
    /// alanıdır (K1, DM4), sonra da aynıdır; bağ ise değişebilir ve satırın kendisi sonra <see cref="LoadOrdersAsync"/>
    /// ile izlenerek okunmalı (izlenen bir satırı ikinci okuma yenilemezdi). Bulunamayan sipariş 400
    /// <c>unknown-order</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, OrderPeek>> PeekOrdersAsync(
        IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, OrderPeek>();
        var peeks = await _db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == LicenseId && ids.Contains(o.Id))
            .Select(o => new OrderPeek(o.Id, o.SessionId, o.ShipmentId))
            .ToDictionaryAsync(o => o.Id, ct);

        var unknown = ids.Where(id => !peeks.ContainsKey(id)).ToList();
        if (unknown.Count > 0) throw SalesCommandRejection.UnknownOrders(unknown);
        return peeks;
    }

    /// <summary>
    /// Bir yayında bir kodun siparişleri (§4.11): kod eşleşmesi SQL'de değil bellekte, <see cref="LabelRules.SameCode"/>
    /// ile — SQL Server harmanı Türkçe harfleri ortak koddaki gibi katlamaz ve sondaki boşluğu yok sayar; iki taraf
    /// aynı kodu ayrı ürün sayardı. Saklanan kod kırpılmaz; <paramref name="code"/>'u çağıran kırpar. SQL adayları
    /// yalnız uzunlukla süzer (masaüstündeki <c>GetActiveBySessionAndCode</c> gibi): harf katlama uzunluğu
    /// değiştirmez. SQL Server'ın <c>LEN</c>'i sondaki boşluğu saymaz, ama kırpılmış koda eşleşen kodun sonunda boşluk
    /// olamaz: süzgeç eşleşeni dışarıda bırakmaz, boşluklu adayı <see cref="LabelRules.SameCode"/> eler. İzlemeden okur
    /// (Id'ler; satırlar sonra <see cref="LoadOrdersAsync"/> ile).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> FindSessionCodeOrdersAsync(Guid sessionId, string code, CancellationToken ct)
    {
        var candidates = await _db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == LicenseId && o.SessionId == sessionId && o.Code != null
                        && o.Code.Length == code.Length)
            .Select(o => new { o.Id, o.Code })
            .ToListAsync(ct);
        return candidates.Where(o => LabelRules.SameCode(o.Code, code)).Select(o => o.Id).ToList();
    }

    /// <summary>
    /// Bu lisansın verilen dosyalarını İZLEYEREK yükler (başka lisansın ya da var olmayan dosya dönmez). Kurala (ör.
    /// <see cref="LabelRules.Classify"/>) giren dosya durumu böylece türetmenin yazdığı satırla aynıdır: dosya türetme
    /// (K7) aynı izlenen satırı yazar ve jetonu (<see cref="Shipment.Version"/>) bu okumadan sonraki eşzamanlı bir
    /// kararı (ör. kargoya verme) yakalar. Türetilecek dosya, bağlı siparişlerden ÖNCE yüklenmeli
    /// (<see cref="ShipmentAmountDeriver"/>'ın sıra gerekçesi): sonra yüklenirse, arada bağlı bir siparişi değiştirip
    /// dosyayı türeten eşzamanlı yazıcı jetonla yakalanmaz.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Shipment>> LoadShipmentsAsync(
        IEnumerable<Guid> shipmentIds, CancellationToken ct)
    {
        var ids = shipmentIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Shipment>();
        var loaded = await _db.Shipments
            .Where(s => s.LicenseId == LicenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        foreach (var shipment in loaded.Values) _shipments.TryAdd(shipment.Id, shipment);
        return loaded;
    }

    // ── Kargo dosyası komutları (attach-shipment, shipment-decision; §4.6, §7) ─────────────────────────

    /// <summary>
    /// Bir müşterinin açık (Pending ya da Held) dosyalarını İZLEYEREK yükler; en yenisi başta — masaüstündeki
    /// <c>ShipmentRepository.GetAllOpenByCustomer</c> sırası (<c>CreatedAt</c> azalan, sonra "N" biçimli Id azalan,
    /// ordinal). Müşteri Id'si çağıranın çözdüğü asıl kayıttır; birleştirici dosyaları asıl kayda taşır
    /// (<c>CustomerIdentityMerger</c>), <paramref name="alsoCustomerId"/> taşınmamış bir kopya yazımı içindir.
    /// Dosyalar bağlı siparişlerden ÖNCE yüklenmeli (<see cref="LoadShipmentsAsync"/>'in sıra gerekçesi).
    /// </summary>
    public async Task<IReadOnlyList<Shipment>> LoadOpenShipmentsAsync(
        string customerId, string? alsoCustomerId, CancellationToken ct)
    {
        var customers = new[] { customerId, alsoCustomerId ?? customerId }.Distinct().ToList();
        var open = await _db.Shipments
            .Where(s => s.LicenseId == LicenseId && customers.Contains(s.CustomerId)
                        && (s.Status == ShipmentStatus.Pending || s.Status == ShipmentStatus.Held))
            .ToListAsync(ct);
        foreach (var shipment in open) _shipments.TryAdd(shipment.Id, shipment);
        return NewestFirst(open);
    }

    /// <summary>Dosyalar en yenisi başta: <c>CreatedAt</c> azalan, sonra "N" biçimli Id azalan (ordinal) — masaüstündeki
    /// açık dosya sırası; sunucu GUID sıralaması (SQL Server'ın bayt sırası) yerel aynayla ayrışırdı.</summary>
    public static IReadOnlyList<Shipment> NewestFirst(IEnumerable<Shipment> shipments) =>
        shipments
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id.ToString("N"), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <c>attach-shipment</c>'ın adayları (§4.6, M22), İZLEYEREK: müşterinin bu lisanstaki, kargo bağı olmayan, iptal
    /// edilmemiş, geçici yedek olmayan ve kargo bağı bilinen (<c>OriginReplicaId</c> dolu) siparişleri; yayını
    /// devredilmiş ya da çağıranın kendi yayını (sahibi çağıran kopya; canlı olsa da) olanlar. Başka kopyanın canlı ya
    /// da devrediliyor yayını görünmez (Y1, Y8) ve yayınsız sipariş aday değildir. Kargo ücreti satırı adaydır
    /// (yerel <c>AttachLabels</c> gibi). Sipariş Id'sine göre sıralı.
    /// </summary>
    public async Task<IReadOnlyList<Order>> LoadAttachCandidatesAsync(string customerId, CancellationToken ct)
    {
        var licenseId = LicenseId;
        var caller = ReplicaId;
        var sessions = _db.StreamSessions;
        var candidates = await _db.Orders
            .Where(o => o.LicenseId == licenseId && o.CustomerId == customerId
                        && o.ShipmentId == null && o.CancelledAt == null && !o.IsTentativeBackup
                        && o.OriginReplicaId != null && o.SessionId != null
                        && sessions.Any(s => s.LicenseId == licenseId && s.Id == o.SessionId
                                             && (s.ReleasedAt != null || s.OwnerReplicaId == caller)))
            .ToListAsync(ct);
        foreach (var order in candidates) Track(order);
        return candidates.OrderBy(o => o.Id).ToList();
    }

    private void Track(Order order)
    {
        if (_orders.TryAdd(order.Id, order)) _shipmentBefore[order.Id] = order.ShipmentId;
    }

    /// <summary>Dosya Id'si sunucuda HERHANGİ bir lisansta var mı (Shipments'ın birincil anahtarı yalnız Id)? Yalnız
    /// varlık döner; başka lisansın satırı okunmaz.</summary>
    public Task<bool> ShipmentIdTakenAsync(Guid shipmentId, CancellationToken ct) =>
        _db.Shipments.AsNoTracking().AnyAsync(s => s.Id == shipmentId, ct);

    /// <summary>
    /// Komutun açtığı yeni dosyayı kaydeder (<c>attach-shipment</c>): bu lisansa ait olmalı. Kaydetmedeki eşzamanlı Id
    /// çakışmasını servis <c>content-conflict</c>'e çevirir (<see cref="InsertedShipmentIds"/>).
    /// </summary>
    public void AddShipment(Shipment shipment)
    {
        if (shipment.LicenseId != LicenseId)
            throw new InvalidOperationException("Komut yalnız kendi lisansına dosya açar.");
        if (!_insertedShipments.Add(shipment.Id) || _shipments.ContainsKey(shipment.Id))
            throw new InvalidOperationException("Dosya kapsamda zaten var.");

        _db.Shipments.Add(shipment);
        _shipments[shipment.Id] = shipment;
    }

    /// <summary>
    /// Dosyaları şimdi, bu kapsamın izlediği siparişlerin birleşik hâliyle yeniden türetir
    /// (<see cref="ShipmentAmountDeriver.DeriveAsync"/>): yalnız türetilmiş (<c>AmountDerivedAt</c> dolu) ve Shipped
    /// olmayanlar; <c>Version</c> bir artar, <c>UpdatedAt</c> = <see cref="Now"/>. İşleyici dosyanın bağlı siparişlerine
    /// son değişikliğini yaptıktan SONRA çağırır: servisin K7 adımı burada türetilen dosyayı yeniden türetmez (sürüm
    /// komut başına bir kez ilerler). Kargoya verilecek dosya, durumu değişmeden önce türetilir — Shipped dosya
    /// türetilmez, tutarı o anda donar (DM7).
    /// </summary>
    /// <returns>Türetilen dosyalar.</returns>
    public async Task<IReadOnlyList<Guid>> DeriveShipmentsAsync(IEnumerable<Shipment> shipments, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Shipment>();
        foreach (var shipment in shipments)
        {
            if (!_shipments.TryGetValue(shipment.Id, out var tracked) || !ReferenceEquals(tracked, shipment))
                throw new InvalidOperationException("Yalnız kapsamın yüklediği ya da açtığı dosya türetilir.");
            map[shipment.Id] = shipment;
        }

        var derived = await _shipmentAmounts.DeriveAsync(LicenseId, map, map.Keys, _orders.Values, Now, ct);
        foreach (var id in derived)
        {
            _touchedShipments.Add(id);
            if (!_derivedShipments.Contains(id)) _derivedShipments.Add(id);
        }
        return derived;
    }

    /// <summary>
    /// Dosyanın bu komutla değiştiğini bildirir: komut başına ilk dokunuşta <c>Version</c> bir artar (bu komutta
    /// türetildiyse türetme artırmıştır); <c>UpdatedAt</c> = <see cref="Now"/>.
    /// </summary>
    public void MarkShipmentChanged(Shipment shipment)
    {
        if (!_shipments.TryGetValue(shipment.Id, out var tracked) || !ReferenceEquals(tracked, shipment))
            throw new InvalidOperationException("Yalnız kapsamın yüklediği ya da açtığı dosya değişmiş bildirilir.");
        if (_touchedShipments.Add(shipment.Id)) shipment.Version++;
        shipment.UpdatedAt = Now;
    }

    /// <summary>Kapsamın yüklediği ya da açtığı dosyalar (değişenler bellekteki hâlleriyle).</summary>
    internal IReadOnlyDictionary<Guid, Shipment> TrackedShipments => _shipments;

    /// <summary>İşleyicinin bu komutta türettiği dosyalar (<see cref="DeriveShipmentsAsync"/>), türetme sırasıyla.</summary>
    internal IReadOnlyList<Guid> DerivedShipmentIds => _derivedShipments;

    /// <summary>Komutun açtığı dosyalar (<see cref="AddShipment"/>).</summary>
    internal IReadOnlyCollection<Guid> InsertedShipmentIds => _insertedShipments;

    /// <summary>Komut en az bir dosyayı açtı, türetti ya da değiştirdi.</summary>
    internal bool ShipmentsChanged => _touchedShipments.Count > 0 || _insertedShipments.Count > 0;

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

    /// <summary>
    /// Sipariş Id'si sunucuda HERHANGİ bir lisansta var mı? Orders'ın birincil anahtarı yalnız Id'dir: başka lisansın
    /// satırıyla aynı Id'yi eklemek kaydetmede birincil anahtar ihlali olurdu. Yalnız varlık döner; başka lisansın
    /// satırı okunmaz.
    /// </summary>
    public Task<bool> OrderIdTakenAsync(Guid orderId, CancellationToken ct) =>
        _db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId, ct);

    /// <summary>
    /// Müşteri Id'lerinin asıl kayıt projeksiyonları (<see cref="SalesCustomerDirectory"/>; KVKK ekleme kuralı için
    /// <c>PurgedAt</c>). Yalnız bu lisanstan.
    /// </summary>
    public Task<SalesCustomerDirectory> LoadCustomersAsync(IEnumerable<string> rawCustomerIds, CancellationToken ct) =>
        SalesCustomerDirectory.LoadAsync(_db, LicenseId, rawCustomerIds, ct);

    /// <summary>
    /// Komutun eklediği yeni siparişi kaydeder (<c>add-backup</c>): bu lisansa ait olmalı. Eklenen satır komutun
    /// değişikliğidir — <c>UpdatedAt</c> = <see cref="Now"/>, sürüm ilk değerinde kalır (<c>orders/sync</c>'in ekleme
    /// dalı gibi: sürüm yalnız güncellemede ilerler) ve sonuçta döner. Yüklemeden önce bağı yoktur (K7'de eski dosya
    /// yok). Id'nin boş olduğunu çağıran <see cref="OrderIdTakenAsync"/> ile denetler; kaydetmedeki eşzamanlı çakışmayı
    /// servis <c>content-conflict</c>'e çevirir (<see cref="InsertedOrderIds"/>).
    /// </summary>
    public void AddOrder(Order order, params OrderUnit[] units)
    {
        if (order.LicenseId != LicenseId)
            throw new InvalidOperationException("Komut yalnız kendi lisansına sipariş ekler.");
        if (!_inserted.Add(order.Id) || _orders.ContainsKey(order.Id))
            throw new InvalidOperationException("Sipariş kapsamda zaten var.");

        order.UpdatedAt = Now;
        _db.Orders.Add(order);
        _orders[order.Id] = order;
        _shipmentBefore[order.Id] = null;
        _changes[order.Id] = new HashSet<OrderUnit>(units);
        _changeOrder.Add(order.Id);
    }

    /// <summary>Komutun eklediği siparişler (<see cref="AddOrder"/>).</summary>
    internal IReadOnlyCollection<Guid> InsertedOrderIds => _inserted;

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

/// <summary>Bir siparişin izlenmeden okunan yayını ve kargo bağı (<see cref="SalesCommandScope.PeekOrdersAsync"/>).</summary>
public sealed record OrderPeek(Guid Id, Guid? SessionId, Guid? ShipmentId);
