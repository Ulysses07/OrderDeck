namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir yayıncının (lisansın) kendi Netgsm aboneliği: abone no, API şifresi,
/// onaylı gönderici başlığı ve İYS marka kodu.
///
/// <para><b>Neden kiracı başına:</b> İYS onayı MARKA başına tutulur. 2026-09-17'de
/// ölçüldü — aynı numara marka <c>731734</c> altında ONAY, <c>763208</c> altında
/// RET. Merkezî tek marka altında tüm yayıncıların listelerini toplamak, onayı
/// hukuken sahibi olmayan tarafa yazmak olurdu.</para>
///
/// <para><b>Satırın yokluğu "hiç girilmemiş" demektir</b> — ayrı bir
/// <c>pending</c> durumu yok. Yayıncı ayrılırsa satır silinmez,
/// <see cref="Status"/> <c>disabled</c> olur.</para>
/// </summary>
public sealed class NetgsmAccount
{
    public Guid Id { get; set; }

    /// <summary>Kiracı anahtarı. Bir lisansın tek hesabı olur (tekil index).</summary>
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Netgsm abone numarası. Gizli değil — panelde açık gösterilir.</summary>
    public string UserCode { get; set; } = "";

    /// <summary>Netgsm API şifresi — <c>IDataProtector</c> ile şifreli saklanır,
    /// asla düz metin dönmez ve asla log'lanmaz.</summary>
    public string PasswordProtected { get; set; } = "";

    /// <summary>Netgsm'de onaylı gönderici başlığı (sender ID).</summary>
    public string Header { get; set; } = "";

    /// <summary>İYS marka kodu. <b>Global tekil</b>: push/verify işleri markadan
    /// hesaba geri dönüyor; iki lisans aynı markayı paylaşırsa hangi kimlikle
    /// gidileceği belirsizleşir.</summary>
    public string BrandCode { get; set; } = "";

    /// <summary>"verified" | "failed" | "disabled". Yalnız <c>verified</c> hesap
    /// onay toplar ve SMS gönderir (fail-closed).</summary>
    public string Status { get; set; } = "failed";

    /// <summary>Panelde gösterilen son hata metni (ör. yanlış şifre).</summary>
    public string? LastError { get; set; }

    /// <summary>Son başarılı <c>/iys/search</c> doğrulaması.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
