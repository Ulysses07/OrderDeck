namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// KVKK silme sırasında depodan (R2) silinemeyen bir nesnenin kalıcı takibi.
///
/// Neden ayrı bir tablo: silme iki ayrı sistemi değiştiriyor — veritabanı
/// satırı ve kova nesnesi — ve ikisi tek işlemde atomik değil. Eskiden
/// <c>DeleteAsync</c> patlayınca yalnızca log yazılıyor, ardından
/// <c>Payment.MediaObjectKey</c> koşulsuz <c>null</c>'a çekiliyordu: nesneye
/// giden TEK referans kayboluyor, purge tekrarlansa bile silme bir daha
/// denenmiyordu. Sonuç, kovada süresiz duran kişisel veri + "tamamen
/// silindi" diyen bir yönetim ekranı.
///
/// Bu satır o niyeti saklıyor. Ödemedeki anahtar hâlâ boşaltılıyor (kişiye
/// açık yüzeyde kalmasın), ama anahtarın kendisi burada, yalnız yöneticinin
/// gördüğü temizlik kuyruğunda yaşamaya devam ediyor. <c>DeletedAt</c>
/// dolana kadar iş bitmiş sayılmaz.
/// </summary>
public sealed class OrphanedMediaObject
{
    public Guid Id { get; set; }

    /// <summary>Kovadaki nesne anahtarı. Tekil — aynı anahtar iki kez kuyruğa girmez.</summary>
    public string ObjectKey { get; set; } = "";

    /// <summary>Hangi silme talebinden arta kaldı (teşhis + yeniden deneme kapsamı).</summary>
    public Guid? ShopperId { get; set; }

    /// <summary>
    /// Silme onaylandığında <c>PdfPurgedAt</c> damgasının vurulacağı ödeme satırı.
    /// Damga gerçek silme onayından önce vurulamaz; bu alan olmadan geciken
    /// silme başarıya ulaştığında hangi satırın damgalanacağı bilinemezdi.
    /// </summary>
    public Guid? PaymentId { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Son denemenin hata metni; yönetim ekranında görünür.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>
    /// Gerçek silme onayı. <c>null</c> ise nesne hâlâ kovada — silme talebi
    /// KAPANMAMIŞ demektir.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
