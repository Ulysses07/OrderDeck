namespace OrderDeck.LicenseServer.Domain.Bank;

public enum ObifinConnectionStatus { Unverified = 0, Verified = 1, Failed = 2, Disabled = 3 }

/// <summary>Lisans başına Obifin web servis kimliği + çekim imleci (spec §3).
/// Şifre ve API key DataProtection ile şifreli; görünüme yalnız "kayıtlı" bayrağı çıkar.</summary>
public sealed class ObifinConnection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string BaseUrl { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public ObifinConnectionStatus Status { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    /// <summary>Görülen en büyük Obifin hareket Id'si; yalnız tam başarılı turda ilerler.</summary>
    public long? LastObifinTransactionId { get; set; }
    public DateTimeOffset? BackfillCompletedAt { get; set; }
    /// <summary>İlk çekim çapası (TR takvim günü, KVKK Ö-5): ilk çekim turunun günü − <c>ObifinPollJob.InitialGraceDays</c>.
    /// O turun başında, Obifin'e gidilmeden kaydedilir; tur yarıda kalsa ya da hiç kayıt yapmadan düşse de (geçici hata,
    /// Obifin reddi) kalır, sonraki turlar aynı çapadan sürer. Bu günden eski işlem tarihli hareket hiçbir turda yazılmaz.
    /// Kimlik değişene kadar değişmez, değişince boşalır. Eski bağlantılara <c>bank-data-cleanup</c> yazar.</summary>
    public DateOnly? InitialCollectFrom { get; set; }
    /// <summary>Bağlantının <see cref="ObifinConnectionStatus.Disabled"/>'a çekildiği an (KVKK K-6). Bugün yalnız lisans bitişi
    /// yazar (<c>ObifinConnectionService.DisableEndedLicensesAsync</c>); zaten Disabled olan bağlantıda yeniden yazılmaz.
    /// Görev 13/14 ile: yöneticinin durdurması da yazar, hizmet sonu silmesi (BankServiceEndPurgeJob) bu andan 30 gün sonra
    /// koşar, Disabled'dan çıkan her yol boşaltır. O zamana dek bilinen boşluk: kimlik değişimi Disabled bağlantıyı
    /// Unverified'a çevirir ama bu alanı boşaltmaz; lisans hâlâ bitmişse sonraki koşu yeni tarih yazar, sayaç baştan
    /// başlar (Görev 13 kapatır).</summary>
    public DateTimeOffset? DisabledAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
