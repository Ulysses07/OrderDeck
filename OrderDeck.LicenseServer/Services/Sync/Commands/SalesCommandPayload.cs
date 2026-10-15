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
    /// İsteğe bağlı sipariş listesi (ör. <c>alsoSelectedIds</c>): yoksa boş; en çok
    /// <see cref="SalesCommandService.MaxOrders"/>, boş GUID yok. Tekilleştirilir ve sıralanır.
    /// </summary>
    public static bool TryReadOptionalIds(
        IReadOnlyCollection<Guid>? ids, string name, out IReadOnlyList<Guid> normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = [];
        error = null;
        if (ids is null || ids.Count == 0) return true;
        if (ids.Count > SalesCommandService.MaxOrders)
        {
            error = $"{name} en çok {SalesCommandService.MaxOrders} sipariş taşır.";
            return false;
        }
        if (ids.Contains(Guid.Empty))
        {
            error = $"{name} boş Id taşıyamaz.";
            return false;
        }

        normalized = ids.Distinct().Order().ToList();
        return true;
    }

    /// <summary>Fiyatın üst sınırı: <c>Orders.Price</c> kolonu decimal(18,2).</summary>
    public const decimal MaxPrice = 9_999_999_999_999_999.99m;

    /// <summary>
    /// Fiyat: 0 ya da daha büyük, en çok <see cref="MaxPrice"/> ve en çok iki ondalık — kolon decimal(18,2); fazla
    /// ondalık kayıtta sessizce yuvarlanır ve istemcinin gördüğü fiyattan ayrışırdı. Kanonik biçim iki ondalıktır:
    /// <c>120</c> ile <c>120.00</c> aynı fiyattır ve aynı istek özetini verir (<see cref="SalesCommandHash"/>).
    /// </summary>
    public static bool TryReadPrice(decimal value, string name, out decimal price, [NotNullWhen(false)] out string? error)
    {
        price = 0m;
        error = null;
        if (value < 0m || value > MaxPrice || decimal.Round(value, 2) != value)
        {
            error = $"{name} 0 ya da daha büyük, en çok iki ondalıklı bir tutar olmalı.";
            return false;
        }

        price = decimal.Round(value, 2) + 0.00m;
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
