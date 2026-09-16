using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Self-service parola sıfırlama OTP'sini üretir/doğrular. SMS maliyetini
/// OrderDeck karşıladığı için anti-abuse kritik: telefon/IP rate-limit +
/// global günlük tavan. Kod Argon2id ile hash'lenir, plaintext saklanmaz.
/// </summary>
public sealed class PasswordResetCodeService
{
    public sealed record IssuedCode(Guid Id, string Code);

    public const int CodeLength = 6;
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PerPhoneCooldown = TimeSpan.FromSeconds(60);
    public const int PerPhoneDailyMax = 5;
    public const int PerIpHourlyMax = 5;
    public const int PerIpDailyMax = 20;
    public const int MaxVerifyAttempts = 5;

    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly int _dailyGlobalCap;
    private readonly ILogger<PasswordResetCodeService> _log;

    public PasswordResetCodeService(
        LicenseDbContext db,
        PasswordHasher hasher,
        IConfiguration config,
        ILogger<PasswordResetCodeService> log)
    {
        _db = db;
        _hasher = hasher;
        _dailyGlobalCap = config.GetValue("Sms:DailyGlobalCap", 500);
        _log = log;
    }

    /// <summary>
    /// Rate-limit + global tavan geçilirse 6 haneli kodu üretir, hash'leyip
    /// satır ekler ve **plaintext kodu döner** (SMS'e gider). Throttle/cap
    /// durumunda <c>null</c> döner. Anonim forgot-password ucu hesap varlığını
    /// sızdırmamak için yine 202 verir; yetkili uçlar 429 bildirebilir.
    /// </summary>
    public async Task<string?> IssueAsync(
        Shopper shopper, string? ip, CancellationToken ct = default)
        => (await IssueWithHandleAsync(shopper, ip, ct))?.Code;

    /// <summary>
    /// SMS teslimatı başarısız olursa yalnız bu üretimi geri alabilmek için
    /// satır kimliğiyle birlikte kodu döndürür.
    /// </summary>
    public async Task<IssuedCode?> IssueWithHandleAsync(
        Shopper shopper, string? ip, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        // 1. Global günlük tavan — maliyet tavanı. Aşılırsa gönderme + alarm.
        var globalToday = await _db.ShopperPasswordResetCodes
            .CountAsync(c => c.CreatedAt >= todayStart, ct);
        if (globalToday >= _dailyGlobalCap)
        {
            _log.LogWarning(
                "Password-reset SMS global daily cap reached ({Cap}); suppressing further sends today.",
                _dailyGlobalCap);
            return null;
        }

        // 2. Telefon başına: 60sn cooldown + günlük max.
        var shopperCodesToday = await _db.ShopperPasswordResetCodes
            .Where(c => c.ShopperId == shopper.Id && c.CreatedAt >= todayStart)
            .ToListAsync(ct);
        if (shopperCodesToday.Count >= PerPhoneDailyMax)
            return null;
        if (shopperCodesToday.Any(c => now - c.CreatedAt < PerPhoneCooldown))
            return null;

        // 3. IP başına: saatlik + günlük.
        if (!string.IsNullOrWhiteSpace(ip))
        {
            var hourAgo = now - TimeSpan.FromHours(1);
            var ipHour = await _db.ShopperPasswordResetCodes
                .CountAsync(c => c.RequestIp == ip && c.CreatedAt >= hourAgo, ct);
            if (ipHour >= PerIpHourlyMax)
                return null;
            var ipDay = await _db.ShopperPasswordResetCodes
                .CountAsync(c => c.RequestIp == ip && c.CreatedAt >= todayStart, ct);
            if (ipDay >= PerIpDailyMax)
                return null;
        }

        // 4. Kod üret (6 hane, kriptografik), hash'le, satır ekle.
        var code = GenerateCode();
        var row = new ShopperPasswordResetCode
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            CodeHash = _hasher.Hash(code),
            ExpiresAt = now + Expiry,
            CreatedAt = now,
            RequestIp = ip,
            AttemptCount = 0,
        };
        _db.ShopperPasswordResetCodes.Add(row);

        // R10-S03: shopper başına CAS. Yukarıdaki kontroller ile INSERT ayrı
        // adımlar; aynı shopper için iki eşzamanlı üretim ikisi de kontrolleri
        // geçebilir. LastResetCodeIssuedAt eşzamanlılık jetonu (bkz.
        // LicenseDbContext) kod satırıyla AYNI SaveChanges'ta güncellenir:
        // kaybedenin TÜM kaydı (kod satırı dahil) geri alınır ve null döneriz —
        // çağıranlar bunu throttle gibi işler (429/202). Jeton kota/cooldown
        // hesabına girmez; DiscardAsync'in satır silmesi bu alanı geri almaz.
        // Global/IP tavanlarındaki yarışlar bilerek yaklaşık bırakıldı
        // (maliyet tavanı, ±1 kritik değil).
        shopper.LastResetCodeIssuedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Kaybeden: eklenmemiş kod satırını bırak, shopper'ı DB'deki
            // güncel hâline döndür ki scoped context sonraki kayıtlar için
            // (örn. PanelSupportRequestsController talep kapatma) temiz kalsın.
            _db.Entry(row).State = EntityState.Detached;
            await _db.Entry(shopper).ReloadAsync(ct);
            _log.LogDebug(
                "Concurrent password-reset issue lost the race for shopper {ShopperId}; treated as throttled.",
                shopper.Id);
            return null;
        }

        return new IssuedCode(row.Id, code);
    }

    /// <summary>
    /// Sağlayıcıya teslim edilemeyen kodu kaldırır; cooldown ve günlük kota
    /// gerçekte gönderilmemiş SMS yüzünden tüketilmez.
    /// </summary>
    public async Task DiscardAsync(Guid codeId, CancellationToken ct = default)
    {
        var row = await _db.ShopperPasswordResetCodes.FindAsync(
            new object[] { codeId }, ct);
        if (row is null)
            return;

        _db.ShopperPasswordResetCodes.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// En son, tüketilmemiş ve süresi dolmamış kodu doğrular. Her çağrıda
    /// deneme sayacı artar; <see cref="MaxVerifyAttempts"/> aşılırsa satır
    /// geçersiz kılınır. Başarıda satır consumed işaretlenir.
    /// </summary>
    public async Task<bool> VerifyAndConsumeAsync(Guid shopperId, string code, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _db.ShopperPasswordResetCodes
            .Where(c => c.ShopperId == shopperId && c.ConsumedAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return false;

        row.AttemptCount++;
        if (row.AttemptCount > MaxVerifyAttempts)
        {
            row.ConsumedAt = now;   // brute-force kilidi
            return await SaveOrDenyAsync(false, ct);
        }

        var ok = _hasher.Verify(row.CodeHash, code ?? "");
        if (ok)
            row.ConsumedAt = now;
        return await SaveOrDenyAsync(ok, ct);
    }

    /// <summary>
    /// Yazma çakışırsa (AttemptCount eşzamanlılık jetonu) isteği REDDEDER.
    /// Okuma ile yazma arasındaki pencerede aynı satıra paralel bir doğrulama
    /// girdiyse bizim okuduğumuz sayaç eskimiştir; "başarılı" demek o paralel
    /// denemenin sayacını yutmak, yani tavanı delmek olur. Kaba kuvvet
    /// denemesinin kazanabileceği tek senaryo bu; çakışmada hep hayır diyoruz.
    /// Meşru kullanıcı için maliyeti yok — o zaten tek istek yolluyor.
    /// </summary>
    private async Task<bool> SaveOrDenyAsync(bool result, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static string GenerateCode()
    {
        // 000000-999999 arası, baştaki sıfırlar korunur.
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6");
    }
}
