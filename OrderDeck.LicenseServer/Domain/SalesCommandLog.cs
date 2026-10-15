namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Satış komutu günlüğü — yayın ve etiket senkronu spec'i 2026-10-13 §3.1 (S2 satırı), §4.6. <c>POST sales/commands</c>
/// her başarılı komutu, uygulamasıyla AYNI işlemde buraya yazar. İki işi var:
/// <list type="bullet">
///   <item><b>İdempotency.</b> Aynı <see cref="CommandId"/> ve aynı içerik (<see cref="RequestHash"/>) yeniden gelirse
///   komut yeniden uygulanmaz; kayıtlı durum ve sonuç, satırların güncel hâliyle döner. Aynı Id'ye farklı içerik (ya da
///   başka lisans) 409 <c>content-conflict</c>: istemci hatasıdır, sessizce yutulmaz.</item>
///   <item><b>Denetim izi.</b> Hangi kopya, hangi tür, ne zaman, hangi sürümleri yazdı.</item>
/// </list>
///
/// <para><b>KVKK (§9).</b> Satır kişisel metin taşımaz: <see cref="ResultJson"/> yalnız Id, sürüm, tutar ve sonuç Id
/// listeleri içerir; görünen ad, yorum metni ve iptal nedeni (serbest metin olabilir, <c>custom:</c>) yazılmaz. İsteğin
/// içeriği yalnız özet olarak (<see cref="RequestHash"/>) kalır. Satırlar 90 gün sonra silinir
/// (<c>sales-command-log-retention</c>, §4.9).</para>
///
/// <para>Yalnız başarılı komut yazılır: reddedilen komut (409, 400) hiçbir şey yazmaz; aynı Id'yle yeniden denendiğinde
/// baştan değerlendirilir.</para>
/// </summary>
public sealed class SalesCommandLog
{
    /// <summary>Komut en az bir satırı yazdı.</summary>
    public const string StatusApplied = "applied";

    /// <summary>Komutun yazacak bir şeyi yoktu (ör. bütün öğeler zaten iptal); yine kaydedilir ki yeniden oynatma
    /// aynı sonucu dönsün.</summary>
    public const string StatusUnchanged = "unchanged";

    /// <summary><see cref="Kind"/> kolonunun uzunluğu.</summary>
    public const int KindMaxLength = 32;

    /// <summary><see cref="Status"/> kolonunun uzunluğu.</summary>
    public const int StatusMaxLength = 16;

    /// <summary>SHA-256 özetinin bayt uzunluğu (<c>binary(32)</c>).</summary>
    public const int RequestHashLength = 32;

    /// <summary>İstemcinin ürettiği komut kimliği; birincil anahtar. Lisans sınırı yok: aynı Id başka lisansta
    /// görülürse <c>content-conflict</c>.</summary>
    public Guid CommandId { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Komutu gönderen kopya.</summary>
    public Guid ReplicaId { get; set; }

    /// <summary>Komut türü (<c>cancel</c>, <c>uncancel</c>, …).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Kanonik isteğin SHA-256 özeti: tür, kopya, tipli yük ve sipariş Id'sine göre sıralı beklenen sürümler.</summary>
    public byte[] RequestHash { get; set; } = [];

    /// <summary><see cref="StatusApplied"/> ya da <see cref="StatusUnchanged"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>Komutun sonucu (camelCase JSON): yalnız Id, sürüm, tutar ve sonuç Id listeleri.</summary>
    public string ResultJson { get; set; } = "";

    /// <summary>Uygulama anı (sunucu saati); saklama süresi buna göre.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
