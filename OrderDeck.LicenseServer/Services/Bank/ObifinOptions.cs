namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`Obifin` bölümü. Kimlikler burada DEĞİL (lisans başına DB'de, şifreli).</summary>
public sealed class ObifinOptions
{
    /// <summary>Bağlantı kaydında BaseUrl boşsa kullanılır.</summary>
    public string DefaultBaseUrl { get; set; } = "https://prodapio2.obifin.com";
    public int TimeoutSeconds { get; set; } = 40;
    /// <summary>Hareket sorgusunda API tavanı (25.09 ölçümü: 2000 istenince 1000 döndü).</summary>
    public int PageSize { get; set; } = 1000;
}
