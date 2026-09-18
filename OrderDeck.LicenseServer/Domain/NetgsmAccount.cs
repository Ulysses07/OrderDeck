namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir yayıncının Netgsm hesabının kullanılabilirlik durumu.
///
/// <para>Gönderim kapısı <b>fail-closed</b>: yalnız <see cref="Verified"/>
/// onay toplar ve SMS gönderir. Bu yüzden durum serbest metin DEĞİL — bir kod
/// yolunun yanlış büyük/küçük harfle ("verified" yerine "Verified") yazması
/// hiçbir yerde istisna fırlatmadan yayıncının SMS'ini sessizce kapatırdı.</para>
/// </summary>
public enum NetgsmAccountStatus
{
    /// <summary>Doğrulama denendi ve başarısız — varsayılan. Kapı kapalı.</summary>
    Failed = 0,
    /// <summary><c>/iys/search</c> başarılı. Gönderim yalnız bu durumda serbest.</summary>
    Verified = 1,
    /// <summary>Yayıncı ayrıldı; kimlik bilgileri emekliye ayrıldı.</summary>
    Disabled = 2
}

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
/// <c>pending</c> durumu yok.</para>
///
/// <para><b>Satırın iki ayrı çıkış yolu var, ikisi de kasıtlı:</b>
/// <list type="bullet">
/// <item><description><b>Yayıncı ayrılır</b> → satır SİLİNMEZ,
/// <see cref="Status"/> <see cref="NetgsmAccountStatus.Disabled"/> olur.
/// Geçmiş gönderimlerin hangi kimlikle yapıldığı izlenebilir kalır.</description></item>
/// <item><description><b>Lisans/müşteri KVKK kapsamında silinir</b> → License
/// yabancı anahtarı <c>Cascade</c> olduğu için bu satır da gider. Kimlik
/// bilgileri müşteri kaydıyla birlikte imha edilmelidir; doğru davranış
/// budur.</description></item>
/// </list></para>
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

    /// <summary>Hesabın kullanılabilirlik durumu. Yalnız
    /// <see cref="NetgsmAccountStatus.Verified"/> hesap onay toplar ve SMS
    /// gönderir (fail-closed) — bu yüzden varsayılan
    /// <see cref="NetgsmAccountStatus.Failed"/>.</summary>
    public NetgsmAccountStatus Status { get; set; } = NetgsmAccountStatus.Failed;

    /// <summary>Panelde gösterilen son hata metni (ör. yanlış şifre).</summary>
    public string? LastError { get; set; }

    /// <summary>Son başarılı <c>/iys/search</c> doğrulaması.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
