namespace OrderDeck.Core.Customers;

/// <summary>
/// Sunucudaki müşteri kimliğinin yerel aynası. Sunucu kişi başına tek asıl kaydı
/// (LicenseId, platform, IdentityKey) ile tutar; yerel eşleştirme sunucunun "aynı
/// kişi" dediğiyle birebir örtüşmezse ya yerelde iki kişi tek satıra taşınır ya da
/// aynı kişi iki satırda kalıp her gönderimde yeniden yönlendirilir.
/// </summary>
public static class CustomerIdentity
{
    /// <summary>
    /// SUNUCUDAKİ <c>WpfCustomerProjection.IdentityKeyOf</c> İLE BİREBİR AYNI:
    /// kırp, invariant küçült, 'İ'→'i'. Noktasız 'ı' DOKUNULMAZ (sunucu da
    /// dokunmuyor). Metin eşitliği için kullanılan <c>CustomerUnitMerge.SameText</c>
    /// (C3) ile KARIŞTIRMA — o ı→i de yapar. Biri değişirse öbürü de değişmeli.
    /// </summary>
    public static string KeyOf(string username)
        => username.Trim().ToLowerInvariant().Replace('İ', 'i');

    /// <summary>
    /// <c>IdentityKey</c> kolonuna YAZILACAK değer: kullanıcı adı yoksa ya da anahtarı boşsa (boş,
    /// yalnız boşluk) null. Boş anahtar yazılmaz — platformun bütün boş adlı satırlarını tek kişi
    /// sayardı (kimlik araması onları birleştirir, mezar taşı eşleşmesiyle bir KVKK silmesi hepsini
    /// boşaltırdı). Göç 045'in geri doldurmasındaki <c>NULLIF(od_identity_key(Username), '')</c>
    /// ile aynı; kolonu yazan ya da onaran her yol bunu kullanır.
    /// </summary>
    public static string? KeyOrNull(string? username)
    {
        if (username is null) return null;
        var key = KeyOf(username);
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// Formdaki platform tanıtıcısının kimlik olarak kullanılan hâli: kırp, baştaki '@'leri at, yine
    /// kırp. Boşsa tanıtıcı kullanılamaz (ör. yalnız "@" yazılmış). Form yolunun hepsi (kişi upsert'ü,
    /// ad backfill'i, form senkronunun "kimliksiz form" kararı) bunu kullanır: servis tanıtıcıyı
    /// geçerli sayıp depo boş sayarsa form depoda reddedilir ve imleci kilitler.
    /// </summary>
    public static string IntakeHandleOf(string? username) => (username ?? "").Trim().TrimStart('@').Trim();

    /// <summary>
    /// Göç 046: göç öncesi (damgasız) bir grubun bilgisayardan bağımsız numarası. <paramref name="anchor"/>
    /// = çapa üyenin "platform|kimlik anahtarı"; numara onun SHA-256'sının ilk 16 baytı, Guid "N"
    /// biçiminde (32 küçük onaltılık hane) — form gruplarının numarası da bu biçimde. Aynı çapa her
    /// bilgisayarda aynı numarayı verir. DEĞİŞTİRİLMEZ: uygulanmış göç yeniden koşmaz, farklı bir
    /// türetme sonradan güncellenen bilgisayarla eskileri ayrıştırır.
    /// </summary>
    public static string LegacyGroupIdOf(string anchor)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("orderdeck-legacy-group|" + anchor));
        return System.Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Sohbetten gelen kullanıcı adının saklanan hâli. Instagram/TikTok/Facebook'ta '@' adın parçası
    /// değildir: form (<see cref="IntakeHandleOf"/>) ve eski Instagram eklentisi onu atar, kimlik "@"sız
    /// tutulur. YouTube'a dokunulmaz (sohbet satırının adı kanal kimliği, @tanıtıcı ayrı eşleşir).
    /// "@"tan başka bir şey kalmazsa ad olduğu gibi döner — boş adlı satır açılmaz.
    /// </summary>
    public static string ChatHandleOf(string platform, string username)
    {
        if (!AtIsNotPartOfHandle(platform)) return username;
        var bare = IntakeHandleOf(username);
        return bare.Length == 0 ? username : bare;
    }

    /// <summary>
    /// Aynı tanıtıcının öbür yazımı ("ad" ↔ "@ad"), yoksa null. 2026-08-05'ten 2026-10 sürümüne dek
    /// Instagram resmi API yolu adı "@ad" diye yazdı; o dönemde açılan satırlar "@"lı kaldı. Arama
    /// önce verilen yazımı dener, bulamazsa bunu — böylece eski "@ad" satırı olan müşteri yeni yorumda
    /// ikinci kez bölünmez, form da ona bağlanır. Yalnız sohbet/form yolunun araması kullanır; senkron
    /// kimliği (<see cref="KeyOf"/>) iki yazımı ayrı tutar (sunucuyla aynı).
    /// </summary>
    public static string? AlternateAtSpellingOf(string platform, string username)
    {
        if (!AtIsNotPartOfHandle(platform)) return null;
        var bare = IntakeHandleOf(username);
        if (bare.Length == 0) return null;
        return username.TrimStart().StartsWith('@') ? bare : "@" + bare;
    }

    private static bool AtIsNotPartOfHandle(string platform)
        => platform.Equals("instagram", System.StringComparison.OrdinalIgnoreCase)
           || platform.Equals("tiktok", System.StringComparison.OrdinalIgnoreCase)
           || platform.Equals("facebook", System.StringComparison.OrdinalIgnoreCase);
}
