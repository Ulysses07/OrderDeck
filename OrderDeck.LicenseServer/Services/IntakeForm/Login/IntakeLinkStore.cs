using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>OAuth state kaydı: dönüşte hangi tarayıcıya (CookieNonce), hangi
/// forma (Slug) ve hangi platforma ait olduğunu söyler.</summary>
public sealed record IntakeLinkState(string CookieNonce, string Slug, string Platform, string ReturnPath);

/// <summary>Sağlayıcıdan alınan kimlik. Token BURADA YOK — bilerek: takas
/// sonrası tek kullanımlık, saklamak yalnız sızma yüzeyi açar.</summary>
public sealed record IntakeLinkedIdentity(string DisplayName, string? Handle, string? ChannelId);

/// <summary>
/// İki kısa ömürlü kayıt türü, tek süreç içi depo:
///
///   "ils:{token}"          → state, 10 dk, TEK kullanımlık (Consume siler).
///   "ili:{nonce}:{platform}" → bağlı kimlik, 30 dk (form doldurma süresi).
///
/// Önekler AYRI kalmalı — YouTubeChannelResolver'daki "ytv:"/"ytid:" dersi:
/// tek anahtar uzayında bir tür diğerinin cevabı yerine geçebilir.
/// </summary>
public sealed class IntakeLinkStore
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdentityTtl = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;
    public IntakeLinkStore(IMemoryCache cache) => _cache = cache;

    /// <summary>32 bayt CSPRNG → 64 hex karakter. Hem state token'ı hem çerez
    /// nonce'u bunu kullanır.</summary>
    public static string RandomToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public string SaveState(IntakeLinkState state)
    {
        var token = RandomToken();
        _cache.Set("ils:" + token, state, StateTtl);
        return token;
    }

    public IntakeLinkState? ConsumeState(string token)
    {
        var key = "ils:" + token;
        if (!_cache.TryGetValue(key, out IntakeLinkState? state) || state is null)
            return null;
        _cache.Remove(key); // tek kullanımlık — tekrar oynatma burada ölür
        return state;
    }

    public void SaveIdentity(string cookieNonce, string platform, IntakeLinkedIdentity identity)
        => _cache.Set($"ili:{cookieNonce}:{platform}", identity, IdentityTtl);

    public IntakeLinkedIdentity? GetIdentity(string cookieNonce, string platform)
        => _cache.TryGetValue($"ili:{cookieNonce}:{platform}", out IntakeLinkedIdentity? id) ? id : null;

    public void RemoveIdentity(string cookieNonce, string platform)
        => _cache.Remove($"ili:{cookieNonce}:{platform}");
}
