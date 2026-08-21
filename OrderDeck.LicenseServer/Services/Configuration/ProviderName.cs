using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace OrderDeck.LicenseServer.Services.Configuration;

/// <summary>
/// Yapılandırmadan sağlayıcı adı okur ve tanınmıyorsa açılışı durdurur.
///
/// Bu tip, "sessizce yedeğe düş" davranışını ortadan kaldırmak için var.
/// Email/SMS/WhatsApp seçimleri eskiden <c>else</c> ile bir varsayılana
/// düşüyordu; SMS ve WhatsApp'ın varsayılanı <c>log</c>, yani hiçbir şey
/// göndermeyen sağlayıcı. <c>Sms__Provider=netgms</c> gibi tek harflik bir
/// yapılandırma hatası, ne hata ne uyarı vererek tüm SMS'i kapatırdı — kayıp
/// ancak müşteriye ulaşmayan parola kodlarından fark edilirdi. Yanlış
/// yapılandırmayla çalışmaya devam etmektense açılışta patlamak yeğdir:
/// deploy anında görülür, üretimde sessizce sürünmez.
/// </summary>
public static class ProviderName
{
    /// <summary>
    /// <paramref name="key"/> altındaki değeri döndürür. Anahtar yoksa ya da
    /// boşsa <paramref name="fallback"/>; tanınmıyorsa
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// Dönen değer <paramref name="valid"/> içindeki kanonik yazımdır, yani
    /// çağrı yerleri ordinal karşılaştırma yapabilir.
    /// </remarks>
    public static string Resolve(
        IConfiguration config, string key, string fallback, params string[] valid)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        var match = valid.FirstOrDefault(
            v => raw.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        throw new InvalidOperationException(
            $"Unsupported provider '{raw}' for configuration key '{key}'. " +
            $"Valid values: {string.Join(", ", valid.Select(v => $"'{v}'"))}.");
    }

    /// <summary>
    /// <see cref="Resolve"/> ile aynı, ek olarak: <b>üretimde sahte sağlayıcı
    /// kabul edilmez.</b>
    ///
    /// SMS, WhatsApp, push ve medya seçimlerinin varsayılanı hiçbir iş
    /// yapmayan bir sağlayıcı (<c>log</c> / <c>stub</c>) — geliştirmede
    /// doğru olan bu, çünkü kimseye gerçek mesaj gitmemeli. Üretimde ise
    /// aynı varsayılan sessiz bir arıza: yapılandırma satırı eksik kalırsa
    /// sunucu hatasız açılır, sağlık kontrolü yeşil yanar ve gönderimler
    /// "başarılı" döner — ama SMS gitmez, WhatsApp gitmez, yüklenen dekont
    /// hiçbir yere yazılmaz. Kayıp ancak müşteri "mesaj gelmedi" dediğinde
    /// fark edilir, o da genelde günler sonra.
    ///
    /// Yanlış yapılandırmayla çalışmaya devam etmektense açılışta patlamak
    /// yeğdir: deploy anında görülür, üretimde sessizce sürünmez.
    /// </summary>
    /// <param name="fakeFallback">
    /// Hem anahtar boşken kullanılacak varsayılan hem de üretimde yasak olan
    /// değer. İkisi aynı: sahte sağlayıcı zaten yalnız varsayılan olduğu için
    /// tehlikeli — üretimde bilerek seçilmesinin de bir anlamı yok.
    /// </param>
    public static string ResolveLive(
        IConfiguration config, bool isProduction, string key,
        string fakeFallback, params string[] valid)
    {
        var resolved = Resolve(config, key, fakeFallback, valid);
        if (!isProduction || !resolved.Equals(fakeFallback, StringComparison.Ordinal))
            return resolved;

        throw new InvalidOperationException(
            $"Configuration key '{key}' resolves to the no-op provider " +
            $"'{fakeFallback}' in Production. That provider silently discards " +
            $"everything handed to it. Set '{key}' to one of: " +
            $"{string.Join(", ", valid.Where(v => v != fakeFallback).Select(v => $"'{v}'"))}.");
    }
}
