namespace OrderDeck.Core.Chat;

/// <summary>İstemcinin yetkilendirme URL'ini kurabilmesi için gereken açık
/// değerler. App Secret burada YOKTUR ve olmamalıdır.</summary>
public sealed record FacebookOAuthConfig(
    string AppId, string LoginConfigId, string RedirectUri);

/// <summary>Uzun ömürlü kullanıcı token'ı (~60 gün). <paramref name="ExpiresInSeconds"/>
/// 0 ise Meta ömür bildirmemiştir — "bilinmiyor" demektir, uydurma.</summary>
public sealed record FacebookLongLivedToken(string AccessToken, long ExpiresInSeconds);

/// <summary>
/// Facebook OAuth'un sunucudan geçen ayağı.
///
/// <para><b>Neden aracı var:</b> code→token takası Meta App Secret'ı ister.
/// Sır masaüstü binary'sine derlenirse her kurulumdan çıkarılabilir; üstelik
/// yayın hattına enjeksiyon adımı hiç eklenmediği için sahaya BOŞ gidiyordu ve
/// Facebook'a bağlanma her kullanıcıda patlıyordu. Artık takası lisans
/// sunucusu yapıyor, masaüstü yalnız <c>code</c> gönderiyor.</para>
///
/// <para>Arayüz <c>OrderDeck.Core</c>'da çünkü çağıran <c>OrderDeck.Chat</c>,
/// uygulayan <c>OrderDeck.Licensing</c> — ikisi de yalnız Core'u görüyor.</para>
/// </summary>
public interface IFacebookOAuthBroker
{
    /// <summary>Yapılandırma sunucudan gelir: Meta panelinde config/izin seti
    /// değişince yeni bir masaüstü sürümü yayınlamak gerekmesin.</summary>
    Task<FacebookOAuthConfig> GetFacebookOAuthConfigAsync(CancellationToken ct = default);

    /// <summary>Tek kullanımlık <c>code</c>'u uzun ömürlü kullanıcı token'ına
    /// çevirir. Code kısa yaşıyor — çağrı gecikmeden yapılmalı.</summary>
    Task<FacebookLongLivedToken> ExchangeFacebookCodeAsync(
        string code, CancellationToken ct = default);
}
