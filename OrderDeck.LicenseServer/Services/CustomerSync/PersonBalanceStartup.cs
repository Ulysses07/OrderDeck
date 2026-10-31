using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiyenin açılış denetimi (spec 2026-10-11 §7.2): "Açılışta anahtar boşken havuz kipinde lisans varsa uyarı
/// günlüğe yazılır." Sunucu yine açılır; kapı kapalı-hata çalışır: o lisanslarda WPF preview ve apply'ın yeni
/// yazımı 409 client-update-required alır (panel yazımları, WPF scope ve reverse ve kayıtlı anahtarın oynatması
/// kapıdan geçmez, spec §7.2, iskelet fark 4). Uyarı yalnız durumu görünür kılar. Göçten sonra çağrılır (sayım bayrak
/// sütununu okur).
/// </summary>
public static class PersonBalanceStartup
{
    /// <summary>
    /// Alt sınır (<see cref="PersonBalanceOptions.MinClientVersion"/>) boş ya da ayrıştırılamıyorsa VE en az bir lisans
    /// havuz kipindeyse tek uyarı yazar. Alt sınır doluysa veritabanına gitmez.
    /// </summary>
    /// <returns>Uyarı yazıldıysa true.</returns>
    public static async Task<bool> WarnIfPoolWithoutMinimumAsync(
        LicenseDbContext db, PersonBalanceOptions options, ILogger logger, CancellationToken ct)
    {
        if (options.ParseMinClientVersion() is not null) return false;

        var count = await db.Licenses.CountAsync(l => l.PersonBalancePoolSince != null, ct);
        if (count == 0) return false;

        logger.LogWarning(
            "PersonBalance:MinClientVersion tanımsız ya da ayrıştırılamıyor ve havuz kipinde {Count} lisans var — bu lisanslarda preview ve yeni bakiye yazımı 409 client-update-required döner (kapalı-hata, spec 2026-10-11 §7.2). .env'e PersonBalance__MinClientVersion=<Wh> yazıp docker compose up -d license-server",
            count);
        return true;
    }
}
