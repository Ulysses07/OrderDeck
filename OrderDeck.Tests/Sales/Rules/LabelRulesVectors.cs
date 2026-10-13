using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OrderDeck.Shared.Sales;

namespace OrderDeck.Tests.Sales.Rules;

/// <summary>
/// <c>OrderDeck.Shared/Sales/LabelRules.vectors.json</c>'ın okuyucusu. Bu dosya İKİ test projesine birden derlenir
/// (OrderDeck.Tests'te yerinde, OrderDeck.LicenseServer.Tests'te bağlantılı dosya olarak; ad alanı bu yüzden iki
/// derlemede de aynı). Vektörü iki projenin aynı biçimde okuması, koşucunun da ortak olmasını gerektirir: ayrı
/// yazılmış iki okuyucu, kuralın kendisi gibi sessizce ayrışabilirdi.
/// </summary>
public static class LabelRulesVectors
{
    /// <summary>Koşucunun bildiği bölümler. Dosyaya yeni bölüm eklenince <see cref="LabelRulesVectorTests"/>
    /// kırmızı yanar: yeni kural iki projede de koşmadan vektör eklenemez.</summary>
    public static readonly IReadOnlyList<string> KnownSections = new[]
    {
        "sameCode", "codeKey", "classify", "codePrice", "contribution", "paidPriceOnCorrection",
    };

    public sealed record SameCodeCase(string Name, string? A, string B, bool Expected);

    public sealed record CodeKeyCase(string Name, string? Code, string Expected);

    public sealed record ClassifiedLabel(LabelFacts Facts, LabelPriceClass Expected);

    public sealed record ClassifyCase(
        string Name, IReadOnlyDictionary<string, SalesShipmentStatus> Shipments, IReadOnlyList<ClassifiedLabel> Labels);

    public sealed record CodePriceCase(
        string Name, string Code, IReadOnlyDictionary<string, SalesShipmentStatus> Shipments,
        IReadOnlyList<LabelFacts> Labels, decimal? Expected);

    public sealed record ContributionCase(string Name, LabelFacts Label, int ExpectedCount, decimal ExpectedAmount);

    public sealed record PaidPriceCase(
        string Name, decimal? PaidPrice, decimal PriceBefore, bool LinkedToShipment, bool PaidFromBalance,
        decimal? Expected);

    private static readonly Lazy<string> Json = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sales", "LabelRules.vectors.json")));

    private static JsonDocument Open() => JsonDocument.Parse(Json.Value);

    /// <summary>Dosyadaki bölüm adları ("_" ile başlayan açıklama alanları hariç).</summary>
    public static IReadOnlyList<string> Sections()
    {
        using var doc = Open();
        return doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !n.StartsWith('_'))
            .ToList();
    }

    /// <summary>Bölümdeki vektör adları.</summary>
    public static IReadOnlyList<string> Names(string section)
    {
        using var doc = Open();
        return doc.RootElement.GetProperty(section).EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!)
            .ToList();
    }

    public static IReadOnlyList<SameCodeCase> SameCode() => Read("sameCode", v => new SameCodeCase(
        Name(v), NullableString(v, "a"), v.GetProperty("b").GetString()!, v.GetProperty("expected").GetBoolean()));

    public static IReadOnlyList<CodeKeyCase> CodeKey() => Read("codeKey", v => new CodeKeyCase(
        Name(v), NullableString(v, "code"), v.GetProperty("expected").GetString()!));

    public static IReadOnlyList<ClassifyCase> Classify() => Read("classify", v => new ClassifyCase(
        Name(v), Shipments(v),
        v.GetProperty("labels").EnumerateArray()
            .Select(l => new ClassifiedLabel(Facts(l), ParseClass(l.GetProperty("expected").GetString()!)))
            .ToList()));

    public static IReadOnlyList<CodePriceCase> CodePrice() => Read("codePrice", v => new CodePriceCase(
        Name(v), v.GetProperty("code").GetString()!, Shipments(v),
        v.GetProperty("labels").EnumerateArray().Select(Facts).ToList(),
        NullableDecimal(v, "expected")));

    public static IReadOnlyList<ContributionCase> Contribution() => Read("contribution", v => new ContributionCase(
        Name(v), Facts(v.GetProperty("label")),
        v.GetProperty("expected").GetProperty("count").GetInt32(),
        v.GetProperty("expected").GetProperty("amount").GetDecimal()));

    public static IReadOnlyList<PaidPriceCase> PaidPriceOnCorrection() => Read("paidPriceOnCorrection", v =>
        new PaidPriceCase(
            Name(v), NullableDecimal(v, "paidPrice"), v.GetProperty("priceBefore").GetDecimal(),
            v.GetProperty("linkedToShipment").GetBoolean(), v.GetProperty("paidFromBalance").GetBoolean(),
            NullableDecimal(v, "expected")));

    /// <summary>Vektörün dosya haritasından durum sorgusu: haritada olmayan dosya "bilinmiyor" (null).</summary>
    public static Func<string, SalesShipmentStatus?> StatusOf(IReadOnlyDictionary<string, SalesShipmentStatus> map)
        => id => map.TryGetValue(id, out var s) ? s : null;

    private static IReadOnlyList<T> Read<T>(string section, Func<JsonElement, T> map)
    {
        using var doc = Open();
        return doc.RootElement.GetProperty(section).EnumerateArray().Select(map).ToList();
    }

    private static string Name(JsonElement v) => v.GetProperty("name").GetString()!;

    private static LabelFacts Facts(JsonElement l) => new(
        Id: l.GetProperty("id").GetString()!,
        Code: NullableString(l, "code"),
        Price: l.GetProperty("price").GetDecimal(),
        Printed: Flag(l, "printed"),
        Cancelled: Flag(l, "cancelled"),
        Tentative: Flag(l, "tentative"),
        ShippingFee: Flag(l, "shippingFee"),
        ShipmentId: l.TryGetProperty("shipmentId", out var s) ? NullableString(s) : null);

    private static IReadOnlyDictionary<string, SalesShipmentStatus> Shipments(JsonElement v)
        => v.TryGetProperty("shipments", out var map)
            ? map.EnumerateObject().ToDictionary(
                p => p.Name, p => Enum.Parse<SalesShipmentStatus>(p.Value.GetString()!), StringComparer.Ordinal)
            : new Dictionary<string, SalesShipmentStatus>(StringComparer.Ordinal);

    private static LabelPriceClass ParseClass(string text) => Enum.Parse<LabelPriceClass>(text, ignoreCase: true);

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.GetBoolean();

    private static string? NullableString(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) ? NullableString(p) : null;

    private static string? NullableString(JsonElement p) => p.ValueKind == JsonValueKind.Null ? null : p.GetString();

    private static decimal? NullableDecimal(JsonElement e, string name)
        => e.GetProperty(name) is { ValueKind: not JsonValueKind.Null } p ? p.GetDecimal() : null;
}
