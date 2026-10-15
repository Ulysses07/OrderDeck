using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>
/// Sunucunun kişi anahtarı (PersonKey) — spec 2026-10-13 §2, §6.4-b. Ödeme isteğinin sahip satırından türer:
/// <list type="bullet">
///   <item>Sahip GUID'li (asıl kayda çözülmüş, <c>CustomerIdResolver</c>) ve <c>GroupId</c>'si dolu →
///   <c>g:{GroupId}</c>, ham değer: kırpma ve harf katlaması yok.</item>
///   <item>Sahip GUID'li, gruplanmamış → <c>c:{asıl kayıt "N" hex}</c>.</item>
///   <item>Sahip GUID'siz yerel kimlik (projeksiyonu yok) → <c>r:{ham Id}</c>.</item>
/// </list>
/// Gruplu sayılma kuralı yereldeki <c>CustomerGroup.IsGrouped</c> ile aynıdır: boş ya da yalnız boşluktan oluşan
/// <c>GroupId</c> gruplanmamış demektir. Yereldeki <c>CustomerGroup.PersonKey</c> ile aynı sınıflama, önekler farklı
/// (<c>grp:</c>/<c>id:</c>).
///
/// <para><b>Özet.</b> SQL karşılaştırması harf ve sondaki boşluk farkını yutabilir (BIN2 harmanında bile <c>=</c> sondaki
/// boşluğu yok sayar); DM6'nın tekil indeksi ve uygulama kilidi bu yüzden anahtarın UTF-8 baytlarının SHA-256 özetiyle
/// çalışır (<see cref="Hash"/>). Metin yalnız bellekte ordinal yeniden denetim içindir. Bu sınıf saftır: veritabanına
/// gitmez, metni hiçbir biçimde normalleştirmez.</para>
///
/// <para><b>KVKK.</b> Anahtar bir Id sınıflamasıdır, ad değildir; yine de <c>r:</c> anahtarı ham yerel Id taşıdığı için
/// günlüğe yazılmaz.</para>
/// </summary>
public static class PaymentPersonKey
{
    public const string GroupPrefix = "g:";
    public const string CanonicalPrefix = "c:";
    public const string RawPrefix = "r:";

    /// <summary>Satır bir gruba bağlı mı (<c>CustomerGroup.IsGrouped</c> ile aynı kural).</summary>
    public static bool IsGrouped(string? groupId) => !string.IsNullOrWhiteSpace(groupId);

    /// <summary>
    /// Müşteri Id'si GUID mi: sipariş, kargo ve WPF müşteri Id'leri "N" biçimli hex'tir (<c>PaymentOwnerRule.IsOwnerCapable</c>,
    /// <c>SalesCustomerDirectory</c>). Başka biçim GUID'siz yerel Id sayılır ve ham kalır.
    /// </summary>
    public static bool TryParseGuid(string id, out Guid guid) => Guid.TryParseExact(id, "N", out guid);

    /// <summary>GUID'li sahibin anahtarı.</summary>
    /// <param name="canonicalOwnerId">Sahibin asıl kaydı.</param>
    /// <param name="groupId">Asıl kaydın ham <c>GroupId</c>'si; projeksiyon yoksa null.</param>
    public static string ForCanonical(Guid canonicalOwnerId, string? groupId) =>
        IsGrouped(groupId) ? GroupPrefix + groupId : CanonicalPrefix + canonicalOwnerId.ToString("N");

    /// <summary>GUID'siz sahibin anahtarı: ham Id, olduğu gibi.</summary>
    public static string ForRaw(string rawOwnerId) => RawPrefix + rawOwnerId;

    /// <summary>Anahtarın UTF-8 baytlarının SHA-256 özeti (<c>PersonKeyHash binary(32)</c>).</summary>
    public static byte[] Hash(string personKey) => SHA256.HashData(Encoding.UTF8.GetBytes(personKey));
}
