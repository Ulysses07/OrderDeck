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
}
