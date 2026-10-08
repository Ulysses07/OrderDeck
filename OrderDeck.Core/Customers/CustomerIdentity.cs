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
}
