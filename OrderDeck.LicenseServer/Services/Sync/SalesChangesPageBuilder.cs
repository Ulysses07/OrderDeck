using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>Akış sayfasına seçilen bir kaynak satırı; tam olarak biri dolu.</summary>
public sealed record SalesFeedRow(long ChangeSeq, StreamSession? Session, Order? Order, Shipment? Shipment);

/// <summary>
/// Değişiklik akışı sayfasının kurulumu — spec 2026-10-13 §4.4. Veritabanına dokunmaz: kaynak satırları ve yan
/// verileri <see cref="SalesChangesFeed"/> okur, kararı bu sınıf verir. İki adım, çünkü yan liste yalnız sayfaya
/// giren siparişler için okunur:
/// <list type="number">
///   <item><see cref="Select"/>: üç kaynağın birleşimi, ChangeSeq sırası, ilk <c>take</c> öğe.</item>
///   <item><see cref="Assemble"/>: öğeler, imleç ve yan liste (oturumlar, müşteri taslakları).</item>
/// </list>
/// </summary>
public static class SalesChangesPageBuilder
{
    /// <summary>
    /// Üç kaynağı birleştirir. Her kaynak kendi içinde en çok <paramref name="take"/> satırla okunmuş olmalı; bu
    /// yeterlidir, çünkü bir kaynağın ilk <paramref name="take"/> satırının dışındaki satır birleşimin de ilk
    /// <paramref name="take"/> öğesine giremez. rowversion veritabanı genelinde tekildir; eşitlik beklenmez, yine de
    /// sıra kararlıdır (oturum, sipariş, kargo).
    /// </summary>
    public static IReadOnlyList<SalesFeedRow> Select(
        IReadOnlyList<StreamSession> sessions,
        IReadOnlyList<Order> orders,
        IReadOnlyList<Shipment> shipments,
        int take) =>
        sessions.Select(s => new SalesFeedRow(s.ChangeSeq, s, null, null))
            .Concat(orders.Select(o => new SalesFeedRow(o.ChangeSeq, null, o, null)))
            .Concat(shipments.Select(s => new SalesFeedRow(s.ChangeSeq, null, null, s)))
            .OrderBy(r => r.ChangeSeq)
            .Take(take)
            .ToList();

    /// <summary>Sayfanın siparişleri; yan liste bunlar için okunur.</summary>
    public static IReadOnlyList<Order> OrdersOf(IReadOnlyList<SalesFeedRow> selected) =>
        selected.Where(r => r.Order is not null).Select(r => r.Order!).ToList();

    /// <summary>
    /// Sayfayı kurar.
    /// </summary>
    /// <param name="afterSeq">İstemcinin imleci; sıfırlandıysa 0. Boş sayfada <c>next</c> budur.</param>
    /// <param name="orderSessions">Sayfadaki siparişlerin oturumları (güncel hâl). Bulunamayan oturum yan listeye
    /// girmez; sipariş yine verilir.</param>
    /// <param name="customers">Sayfadaki siparişlerin müşterileri.</param>
    public static SalesChangesPage Assemble(
        long afterSeq,
        bool cursorReset,
        IReadOnlyList<SalesFeedRow> selected,
        IReadOnlyDictionary<Guid, StreamSession> orderSessions,
        SalesCustomerDirectory customers)
    {
        var items = new List<SalesChangeItem>(selected.Count);
        var sideSessions = new List<SalesSessionItem>();
        var seenSessions = new HashSet<Guid>();
        // Taslak, müşteri başına bir kez; projeksiyonu olmayanınki sayfadaki son siparişinden.
        var drafts = new Dictionary<string, SalesCustomerDraft>(StringComparer.Ordinal);

        foreach (var row in selected)
        {
            if (row.Session is { } session)
            {
                items.Add(new SalesChangeItem(
                    SalesChangeItem.KindSession, row.ChangeSeq, SalesSessionItem.From(session), null, null));
            }
            else if (row.Order is { } order)
            {
                var customer = customers.Resolve(order.CustomerId);
                items.Add(new SalesChangeItem(
                    SalesChangeItem.KindOrder, row.ChangeSeq, null, SalesOrderItem.From(order, customer), null));

                if (order.SessionId is { } sessionId
                    && seenSessions.Add(sessionId)
                    && orderSessions.TryGetValue(sessionId, out var orderSession))
                    sideSessions.Add(SalesSessionItem.From(orderSession));

                // Silinmiş asıl kaydın taslağı takma adsız: projeksiyon silmede zaten boşaltılır, maske savunma
                // amaçlı (siparişin okuma maskesiyle aynı kural, §9).
                drafts[customer.Id] = customer.Canonical is { } canonical
                    ? new SalesCustomerDraft(
                        customer.Id, canonical.Platform, canonical.Username,
                        OrderTextScrub.MasksOnRead(canonical.PurgedAt) ? null : canonical.DisplayName,
                        canonical.PurgedAt)
                    : new SalesCustomerDraft(customer.Id, order.Platform, order.Username, order.DisplayName, null);
            }
            else if (row.Shipment is { } shipment)
            {
                items.Add(new SalesChangeItem(
                    SalesChangeItem.KindShipment, row.ChangeSeq, null, null, SalesShipmentItem.From(shipment)));
            }
        }

        var next = items.Count == 0 ? afterSeq : items[^1].ChangeSeq;
        return new SalesChangesPage(items, next, cursorReset, sideSessions, drafts.Values.ToList());
    }
}
