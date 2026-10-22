namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`Obifin` bölümü. Kimlikler burada DEĞİL (lisans başına DB'de, şifreli).</summary>
public sealed class ObifinOptions
{
    /// <summary>Bağlantı kaydında BaseUrl boşsa kullanılır.</summary>
    public string DefaultBaseUrl { get; set; } = "https://prodapio2.obifin.com";
    public int TimeoutSeconds { get; set; } = 40;
    /// <summary>Hareket sorgusunda API tavanı (25.09 ölçümü: 2000 istenince 1000 döndü).</summary>
    public const int DefaultPageSize = 1000;

    /// <summary>Hareket sorgusunun sayfa boyutu; ≤ 0 → <see cref="DefaultPageSize"/> (<see cref="TimeoutSeconds"/> gibi).</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>KVKK K-3 öneri 3 (plan karar 23): yayıncının veri sorumlusu bilgileri (<c>DataControllerProfiles</c>) olmadan ilk
    /// bağlantı kaydı, Doğrula ve Banka ekle reddedilir. Varsayılan AÇIK (fail-closed); yalnız pilot geçişinde, metin işi
    /// yayımlanıp bilgi girilene kadar <c>Obifin__RequireDataControllerProfile=false</c> ile kapatılır.</summary>
    public bool RequireDataControllerProfile { get; set; } = true;
}
