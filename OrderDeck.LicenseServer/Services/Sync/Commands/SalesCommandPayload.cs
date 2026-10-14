using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>İşleyicilerin ortak yük okuma ve öğe doğrulama yardımcıları (spec 2026-10-13 §4.6).</summary>
internal static class SalesCommandPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Yükü tipli okur; nesne değilse ya da okunamıyorsa hata.</summary>
    public static bool TryRead<T>(
        JsonElement payload, [NotNullWhen(true)] out T? body, [NotNullWhen(false)] out string? error)
        where T : class
    {
        body = null;
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "payload bir nesne olmalı.";
            return false;
        }

        try
        {
            body = payload.Deserialize<T>(Options);
        }
        catch (JsonException)
        {
            error = "payload okunamadı.";
            return false;
        }

        if (body is null)
        {
            error = "payload okunamadı.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Sipariş listesi (<c>items</c>): en az bir, en çok <see cref="SalesCommandService.MaxOrders"/>, boş GUID yok.
    /// Tekilleştirilir ve sıralanır — sıra komutun anlamını değiştirmez; istek özeti ve yanıt sırası buna göre.
    /// </summary>
    public static bool TryReadItems(
        IReadOnlyCollection<Guid>? items, out IReadOnlyList<Guid> normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = [];
        error = null;
        if (items is null || items.Count == 0)
        {
            error = "items en az bir sipariş taşımalı.";
            return false;
        }
        if (items.Count > SalesCommandService.MaxOrders)
        {
            error = $"items en çok {SalesCommandService.MaxOrders} sipariş taşır.";
            return false;
        }
        if (items.Contains(Guid.Empty))
        {
            error = "items boş Id taşıyamaz.";
            return false;
        }

        normalized = items.Distinct().Order().ToList();
        return true;
    }

    /// <summary>
    /// Öğe başına sürüm: <c>expected</c> tam olarak öğeleri kapsamalı (ne eksik ne fazla). İptal ve geri alma her öğenin
    /// birimini yazar; sürümü bilinmeyen satırı yazmak bayat bir kararı uygulamak olurdu.
    /// </summary>
    public static bool TryMatchExpected(
        IReadOnlyList<Guid> items, IReadOnlyList<SalesCommandExpected> expected, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (expected.Count == items.Count && expected.Select(e => e.OrderId).ToHashSet().SetEquals(items)) return true;
        error = "expected her öğe için tam bir sürüm taşımalı (ne eksik ne fazla).";
        return false;
    }
}
