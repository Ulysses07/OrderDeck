using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Admin kimlik doğrulamasının TEK yeri: hesap kilidi + parola doğrulama.
///
/// <para><b>Neden ortak servis:</b> aynı kimlik bilgisine iki kapı açılıyor —
/// Razor <c>/admin/login</c> sayfası ve <c>POST /api/v1/admin/auth/login</c>.
/// Kilidi yalnız birine koymak kilit değil, tabela olur; saldırgan diğer kapıdan
/// aynı hesabı sınırsız denemeye devam eder.</para>
///
/// <para><b>Kilit neden asıl koruma:</b> <see cref="PasswordHasher"/> Argon2id
/// çalıştırıyor — istek başına 64 MB bellek ve gözle görülür CPU. Bu maliyet
/// yalnız kullanıcı adı VAR OLDUĞUNDA ödeniyor. IP başına oran sınırı bunu
/// bağlamıyor: saldırgan IP döndürerek istediği kadar paralel Argon2
/// tetikleyebilir. Hesap düzeyinde kilit ise toplamı bağlıyor — kilitli hesapta
/// parola hiç doğrulanmıyor, dolayısıyla admin hesabı sayısı × eşik kadar
/// Argon2'den fazlası hiçbir koşulda çalışmıyor.</para>
/// </summary>
public sealed class AdminLoginService
{
    /// <summary>Kilit öncesi izin verilen ardışık hata sayısı.</summary>
    private const int MaxFailedAttempts = 5;

    /// <summary>Kilit süresinin tavanı.</summary>
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public AdminLoginService(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public enum Outcome { Success, InvalidCredentials, LockedOut }

    public readonly record struct Result(Outcome Outcome, AdminUser? Admin, DateTimeOffset? LockedUntil)
    {
        public bool IsSuccess => Outcome == Outcome.Success;
    }

    public async Task<Result> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username, ct);

        // Bilinmeyen kullanıcı adı: Argon2 zaten çalışmıyor, yazacak durum da yok.
        if (admin is null) return new(Outcome.InvalidCredentials, null, null);

        var now = DateTimeOffset.UtcNow;
        if (admin.LockedOutUntil is { } until && until > now)
            return new(Outcome.LockedOut, null, until);

        if (!_hasher.Verify(admin.PasswordHash, password))
        {
            // Oku-değiştir-yaz: iki paralel denemede bir artış kaybolabilir.
            // Bilerek katlanılıyor — bedeli saldırgana fazladan bir-iki deneme,
            // çünkü sayaç her turda en az bir ilerliyor ve eşiğe yine ulaşıyor.
            // Atomik UPDATE'e geçmek sağlayıcıya özgü ikinci bir yol açardı.
            admin.FailedLoginAttempts++;
            if (admin.FailedLoginAttempts >= MaxFailedAttempts)
                admin.LockedOutUntil = now + LockoutFor(admin.FailedLoginAttempts);

            await _db.SaveChangesAsync(ct);
            return new(Outcome.InvalidCredentials, null, admin.LockedOutUntil);
        }

        admin.FailedLoginAttempts = 0;
        admin.LockedOutUntil = null;
        admin.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);
        return new(Outcome.Success, admin, null);
    }

    /// <summary>Tırmanan kilit: 1, 2, 4, 8 dakika, sonra 15'te sabit.
    ///
    /// <para><b>Neden sabit uzun bir süre değil:</b> kilit hesap düzeyinde
    /// olduğu için kullanıcı adını bilen biri gerçek operatörü dışarıda
    /// tutabilir. Sabit 15 dakika bu bedeli ilk hatada ödetirdi. Tırmanma
    /// şifresini yanlış giren operatöre bir dakika, ısrarlı bir saldırgana
    /// hızla tavanı veriyor.</para></summary>
    private static TimeSpan LockoutFor(int failedAttempts)
    {
        var minutes = Math.Pow(2, failedAttempts - MaxFailedAttempts);
        return minutes >= MaxLockout.TotalMinutes
            ? MaxLockout
            : TimeSpan.FromMinutes(minutes);
    }
}
