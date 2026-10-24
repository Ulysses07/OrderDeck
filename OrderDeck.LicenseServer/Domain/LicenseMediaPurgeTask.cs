namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncı silmesinde (KVKK, <c>AdminCustomersController.Purge</c>) bir lisansın kova nesnelerini silme niyeti: müşterilerin
/// yüklediği dekontlar, WhatsApp medyası, yayın paylaşımı medyası ve ürün fotoğrafları (<c>LicenseMediaPurge</c>).
///
/// <para>Neden kalıcı satır: lisans satırı silinince lisansın önekleri başka hiçbir yerden bulunamaz. Niyet yalnız bellekte
/// dursaydı, satırlar kaydedildikten sonra istek iptal edilince, konteyner yeniden başlayınca (master'a merge = otomatik deploy)
/// ya da veritabanı geçici hata verince nesneler kovada süresiz kalırdı; silmeyi yinelemek de çare olmazdı (lisans yok,
/// e-posta anonimleşmiş). Bu satır lisans satırlarının silindiği SaveChanges'te yazılır: ikisi birlikte olur ya da hiçbiri.</para>
///
/// <para>Kapanış: bir tur dört öneki listeler, bulduğunu siler. Satır ancak geç yazım penceresi
/// (<c>LicenseMediaPurge.LateWriteWindow</c>; imzalı yükleme URL'si 10 dk yaşar) geçtikten sonraki bir tur dört öneki de BOŞ
/// görünce kapanır: silme sırasında imzalı URL'yle yüklenen nesne de gider. Silinemeyen nesne, listelenemeyen depo satırı
/// açık tutar; sonraki tur yeniden listeler (yeniden deneme idempotent: silinmiş nesne listede yoktur).</para>
///
/// <para>Kişisel veri içermez: lisans ve yayıncı hesabının kimliği, imha kayıtlarında da bulunan değerler.</para>
/// </summary>
public sealed class LicenseMediaPurgeTask
{
    public Guid Id { get; set; }

    /// <summary>Nesneleri silinecek lisans (satırı artık yok). Tekil: bir lisans bir kez silinir.</summary>
    public Guid LicenseId { get; set; }

    /// <summary>Silinen yayıncı hesabının kimliği (Customer satırı anonimleşip kalır); imha kayıtlarının konusu.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Lisans satırlarının silindiği an; geç yazım penceresi buradan sayılır.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>İşin bu satırı yeniden ele alacağı en erken an.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Son turun hatası (listelenemedi ya da silinemeyen nesne); başarılı turda boşalır.</summary>
    public string? LastError { get; set; }

    /// <summary>Pencereden sonra dört önek boş görüldü: silme kapandı. <c>null</c> iken iş sürer.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
