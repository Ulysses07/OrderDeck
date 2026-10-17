using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Sales;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Satış akışının tel kayıtlarını (<see cref="SalesChangesPage"/>, spec 2026-10-13 §4.4) Core'un yerel biçimine çevirir
/// (<see cref="SalesFeedItem"/>): GUID'ler "N" biçimli metin, zamanlar unix saniye, oturumun virgüllü platformları
/// liste, dosyanın teldeki durumu (<c>recipientpays</c>) yerel enum. Saf: G/Ç yok, durum yok. Müşteri Id'si ham
/// geçer — sunucu GUID olmayan değeri aynen saklar, "N" olmayan yazımı ham sayar (taslağın Id'si de aynı metin).
///
/// <para>Öğe başına çevrilir: bozuk tek öğe (bilinmeyen tür ya da durum) <see cref="FormatException"/> atar ve akış
/// servisi onu uygulanamayan öğe sayar (U10); sayfanın geri kalanı çevrilmeye devam eder.</para>
/// </summary>
internal static class SalesFeedMapper
{
    public static SalesFeedItem ToCore(SalesChangeItem item) => item.Kind switch
    {
        SalesChangeItem.KindSession => new SalesFeedItem(SalesFeedItemKind.Session, item.ChangeSeq,
            ToCore(item.Session ?? throw MissingRow(item)), null, null),
        SalesChangeItem.KindOrder => new SalesFeedItem(SalesFeedItemKind.Order, item.ChangeSeq,
            null, ToCore(item.Order ?? throw MissingRow(item)), null),
        SalesChangeItem.KindShipment => new SalesFeedItem(SalesFeedItemKind.Shipment, item.ChangeSeq,
            null, null, ToCore(item.Shipment ?? throw MissingRow(item))),
        _ => throw new FormatException($"Bilinmeyen akış öğesi türü (seq {item.ChangeSeq})."),
    };

    public static SalesFeedSession ToCore(SalesSessionItem s) => new(
        Id: s.Id.ToString("N"),
        Title: s.Title,
        StartedAt: s.StartedAt.ToUnixTimeSeconds(),
        EndedAt: s.EndedAt?.ToUnixTimeSeconds(),
        Platforms: SplitPlatforms(s.Platforms),
        Notes: s.Notes,
        OwnerMachineName: s.OwnerMachineName,
        ReleasedAt: s.ReleasedAt?.ToUnixTimeSeconds());

    public static SalesFeedOrder ToCore(SalesOrderItem o) => new(
        Id: o.Id.ToString("N"),
        SessionId: o.SessionId?.ToString("N"),
        CustomerId: o.CustomerId,
        Platform: o.Platform,
        Username: o.Username,
        DisplayName: o.DisplayName,
        MessageText: o.MessageText ?? "",
        Code: o.Code,
        Price: o.Price,
        AddedAt: o.AddedAt.ToUnixTimeSeconds(),
        PrintedAt: o.PrintedAt?.ToUnixTimeSeconds(),
        CancelledAt: o.CancelledAt?.ToUnixTimeSeconds(),
        CancelReason: o.CancelReason,
        IsShippingFee: o.IsShippingFee,
        IsBackupPromoted: o.IsBackupPromoted,
        IsTentativeBackup: o.IsTentativeBackup,
        ProductId: o.ProductId?.ToString("N"),
        ProductVariantId: o.ProductVariantId?.ToString("N"),
        Version: o.Version,
        ParentLabelId: o.ParentOrderId?.ToString("N"),
        ShipmentId: o.ShipmentId?.ToString("N"),
        PrintBatchId: o.PrintBatchId,
        PrintSeq: o.PrintSeq,
        PriceCorrectedAt: o.PriceCorrectedAt?.ToUnixTimeSeconds(),
        PaidPrice: o.PaidPrice);

    public static SalesFeedShipment ToCore(SalesShipmentItem s) => new(
        Id: s.Id.ToString("N"),
        CustomerId: s.CustomerId,
        Status: ParseStatus(s.Status, s.ChangeSeq),
        CumulativeAmount: s.CumulativeAmount,
        CreatedAt: s.CreatedAt.ToUnixTimeSeconds(),
        HeldAt: s.HeldAt?.ToUnixTimeSeconds(),
        ShippedAt: s.ShippedAt?.ToUnixTimeSeconds(),
        Version: s.Version,
        OriginReplicaId: s.OriginReplicaId?.ToString("N"));

    public static SalesFeedCustomerDraft ToCore(SalesCustomerDraft d) => new(
        Id: d.Id,
        Platform: d.Platform,
        Username: d.Username,
        DisplayName: d.DisplayName,
        PurgedAt: d.PurgedAt?.ToUnixTimeSeconds());

    /// <summary>Sayfanın yan listesindeki oturumlar (yoksa boş).</summary>
    public static IReadOnlyList<SalesFeedSession> SideSessions(SalesChangesPage page) =>
        page.SideSessions?.Select(ToCore).ToList() ?? (IReadOnlyList<SalesFeedSession>)Array.Empty<SalesFeedSession>();

    /// <summary>Sayfanın yan listesindeki müşteri taslakları (yoksa boş).</summary>
    public static IReadOnlyList<SalesFeedCustomerDraft> Drafts(SalesChangesPage page) =>
        page.CustomerDrafts?.Select(ToCore).ToList()
        ?? (IReadOnlyList<SalesFeedCustomerDraft>)Array.Empty<SalesFeedCustomerDraft>();

    /// <summary><c>sessions/sync</c> ile aynı yazım: virgülle ayrılmış, boşluklar kırpılır, boş parçalar atılır.</summary>
    private static IReadOnlyList<string> SplitPlatforms(string? platforms) =>
        string.IsNullOrWhiteSpace(platforms)
            ? Array.Empty<string>()
            : platforms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Teldeki küçük harfli durum (<c>pending</c>, <c>held</c>, <c>recipientpays</c>, <c>shipped</c>) → yerel
    /// enum (yerelde <c>Pending</c>, <c>RecipientPays</c>, … olarak saklanır).</summary>
    private static ShipmentStatus ParseStatus(string? status, long changeSeq) =>
        Enum.TryParse<ShipmentStatus>(status, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
        && !int.TryParse(status, out _)
            ? parsed
            : throw new FormatException($"Bilinmeyen kargo durumu (seq {changeSeq}).");

    private static FormatException MissingRow(SalesChangeItem item) =>
        new($"Akış öğesinin satırı yok: {item.Kind} (seq {item.ChangeSeq}).");
}
