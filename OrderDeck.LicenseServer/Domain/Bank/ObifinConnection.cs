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
    /// Obifin reddi) kalır, sonraki turlar aynı çapadan sürer. Hiçbir turda bu günden eski gün Obifin'den istenmez, bu
    /// günden eski işlem tarihli hareket (Obifin istenen aralığın dışından döndürse de) yazılmaz. Kimlik değişene kadar
    /// değişmez, değişince boşalır. Eski bağlantılara <c>bank-data-cleanup</c> yazar.</summary>
    public DateOnly? InitialCollectFrom { get; set; }
    /// <summary>Bağlantının <see cref="ObifinConnectionStatus.Disabled"/>'a çekildiği an (KVKK K-6): lisans bitti
    /// (<c>ObifinConnectionService.DisableEndedLicensesAsync</c>) ya da yönetici durdurdu (<c>BankConnectionTerminator</c>).
    /// Zaten Disabled olan bağlantıda yeniden yazılmaz: sayaç baştan başlamaz. Hizmet sonu silmesi (Görev 14) bu andan 30 gün
    /// sonra koşar; Disabled'dan çıkan her yol <c>ObifinConnectionService.ClearServiceEnd</c> ile boşaltır.</summary>
    public DateTimeOffset? DisabledAt { get; set; }
    /// <summary>Bağlantıyı yönetici durdurdu (KVKK K-6). Lisans bitişiyle kendiliğinden durdurulan bağlantı lisans yenilenince
    /// kendiliğinden Unverified'a döner (Görev 14); yöneticinin durdurduğu dönmez. Disabled'dan çıkınca boşalır.</summary>
    public bool DisabledByAdmin { get; set; }
    /// <summary>Hizmet sonu işinin bu bağlantının banka verisini YEREL olarak sildiği an (Obifin'e gidilmez;
    /// <c>BankConnectionTerminator.PurgeLocalDataAsync</c>). Obifin kimliği, yerel <c>BankConnections</c> ve onaylı hesap satırları
    /// (IBAN alanları boş) durur: yönetici Obifin tarafını "Kaldır ve sil" ile kapatır, o da bu lisansa ait bağlantıyı onlardan
    /// bilir. Doluyken bağlantı eşleştirmeye kapalıdır (<c>ObifinConnectionService.MatchingActive</c>) ve iş onu yeniden işlemez;
    /// Disabled'dan çıkınca boşalır.</summary>
    public DateTimeOffset? BankDataPurgedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
