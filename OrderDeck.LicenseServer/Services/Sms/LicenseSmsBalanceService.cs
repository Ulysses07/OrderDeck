using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans SMS kredi bakiyesi üzerinde ledger-tutarlı işlemler. Her değişiklik
/// bir <see cref="LicenseSmsTransaction"/> ekler ve cache <see cref="LicenseSmsBalance"/>
/// .CreditsRemaining'i günceller (invariant: CreditsRemaining = SUM(Amount)).
///
/// F03 (2026-09-09 denetimi): CreditsRemaining okuma-hesapla-yazma ile
/// güncellenir; UpdatedAt concurrency token'ı sayesinde eşzamanlı yazım
/// çakışması SaveChanges'te yakalanır ve <see cref="ApplyAndSaveAsync"/>
/// güncel değer üzerinden yeniden hesaplar. Bu yüzden SaveChanges bu servisin
/// İÇİNDE: retry döngüsü kaydı sarmak zorunda. Çağıranın aynı transaction'da
/// gitmesi gereken diğer değişiklikleri (ör. kampanya + alıcı satırları)
/// çağrıdan ÖNCE context'e eklenmiş olmalı — hepsi aynı SaveChanges'le yazılır.
/// </summary>
public sealed class LicenseSmsBalanceService
{
    private readonly LicenseDbContext _db;
    public LicenseSmsBalanceService(LicenseDbContext db) => _db = db;

    public sealed record BalanceInfo(int CreditsRemaining, DateTimeOffset UpdatedAt);

    /// <summary>Mevcut bakiye (satır yoksa 0).</summary>
    public async Task<BalanceInfo> GetAsync(Guid licenseId, CancellationToken ct)
    {
        var row = await _db.LicenseSmsBalances
            .Where(b => b.LicenseId == licenseId)
            .Select(b => new BalanceInfo(b.CreditsRemaining, b.UpdatedAt))
            .FirstOrDefaultAsync(ct);
        return row ?? new BalanceInfo(0, DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// Ledger'a tx ekler, cache bakiyeyi günceller ve <b>SaveChanges yapar</b>
    /// (context'te bekleyen diğer değişikliklerle birlikte, tek transaction).
    /// <paramref name="amount"/> işaretli (+ ekle / − kullan).
    ///
    /// Eşzamanlı yazım çakışmasında bakiye DB'den yeniden yüklenir, delta
    /// yeniden uygulanır ve <paramref name="disallowNegative"/> kontrolü
    /// GÜNCEL değer üzerinden tekrarlanır.
    /// </summary>
    /// <returns>Yeni bakiye; <c>null</c> = işlem bakiyeyi sıfırın altına
    /// düşürürdü, hiçbir şey yazılmadı.</returns>
    /// <remarks>
    /// <b>Sözleşme — başarısız çağrı iz bırakmaz.</b> Hem <c>null</c> dönüşte
    /// hem de dışarı fırlayan her hatada bu metodun izleyiciye eklediği ledger
    /// satırı atılır ve bakiye satırı orijinal değerlerine döndürülür
    /// (<see cref="DiscardPending"/>). Aksi hâlde artıklar ÇAĞIRANIN bir
    /// sonraki <c>SaveChanges</c>'ine biner: reddedilen hareket gerçekleşmiş,
    /// düşen bir iade ödenmiş olur. İkincisi süpürme döngülerinde çifte iade
    /// demektir — kampanya "paused" kalır ama parası çıkmıştır.
    /// </remarks>
    public async Task<int?> ApplyAndSaveAsync(
        Guid licenseId,
        int amount,
        string kind,
        string? reason,
        Guid? createdByCustomerId,
        bool disallowNegative,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        var now = DateTimeOffset.UtcNow;

        var tx = new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Amount = amount,
            Kind = kind,
            Reason = reason,
            CreatedByCustomerId = createdByCustomerId,
            CreatedAt = now,
        };
        _db.LicenseSmsTransactions.Add(tx);

        var balance = await _db.LicenseSmsBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId, ct);

        if (balance is null)
        {
            if (disallowNegative && amount < 0)
            {
                _db.Entry(tx).State = EntityState.Detached;
                return null;
            }

            balance = new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = amount,
                UpdatedAt = now,
            };

            _db.LicenseSmsBalances.Add(balance);
            // Yeni satır insert'i token'la korunmaz; eşzamanlı iki "ilk yazım"
            // unique LicenseId index'ine takılır → gürültülü DbUpdateException.
            // Aşağıdaki retry döngüsü bu dalı KAPSAMAZ: orası yalnız mevcut
            // satırın sürüm çakışmasını onarıyor, indeks ihlalini değil.
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch
            {
                DiscardPending(tx, balance);
                throw;
            }
            return balance.CreditsRemaining;
        }

        balance.CreditsRemaining += amount;
        // Jeton KESİN ilerlemeli — `UtcNow` monoton değil.
        balance.UpdatedAt = now > balance.UpdatedAt ? now : balance.UpdatedAt.AddTicks(1);

        for (var attempt = 1; ; attempt++)
        {
            if (disallowNegative && balance.CreditsRemaining < 0)
            {
                DiscardPending(tx, balance);
                return null;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return balance.CreditsRemaining;
            }
            catch (DbUpdateConcurrencyException ex) when (
                attempt < maxAttempts
                && ex.Entries.Count > 0
                && ex.Entries.All(e => ReferenceEquals(e.Entity, balance)))
            {
                // YALNIZ bakiye satırı. Çağıran bu SaveChanges'e kendi
                // kararlarını da (kampanya tamamlanması, RefundedCredits)
                // iliştirmiş olabilir; `ex.Entries`'i toptan reload etmek
                // onları siler ve `amount`u ikinci kez ekler. Kampanya
                // çakışması buraya AİT DEĞİLDİR: dışarı çıkar, çağıranın
                // kararı düşer, iş yeniden koştuğunda taze okunur.
                await _db.Entry(balance).ReloadAsync(ct);

                // Satır silinmişse tazeleyecek bir şey yok.
                if (_db.Entry(balance).State == EntityState.Detached)
                {
                    DiscardPending(tx, balance);
                    throw;
                }

                balance.CreditsRemaining += amount;

                var retryAt = DateTimeOffset.UtcNow;
                balance.UpdatedAt = retryAt > balance.UpdatedAt
                    ? retryAt
                    : balance.UpdatedAt.AddTicks(1);
            }
            catch
            {
                // Çakışma bize AİT DEĞİL (yukarıdaki filtre elemedi — ör.
                // çağıranın kampanyası) ya da retry tükendi. Her iki hâlde de
                // karar düşüyor; düşen kararın artıkları izleyicide kalırsa
                // çağıranın bir sonraki SaveChanges'i onları yazar.
                DiscardPending(tx, balance);
                throw;
            }
        }
    }

    /// <summary>
    /// <see cref="ApplyAndSaveAsync"/>'in izleyiciye bıraktığı izleri siler:
    /// ledger satırı atılır, bakiye satırı orijinal değerlerine döndürülür.
    ///
    /// <para>Bakiye <c>Detach</c> EDİLMEZ, orijinal değerlerine döndürülür:
    /// aynı context'te akan bir sonraki çağrı onu yeniden sorgulayacak ve
    /// izleyicideki kopyayı bulacaktır. Detach etmek de çalışırdı ama
    /// çağıranın elindeki referansı sessizce ölü bir nesneye çevirirdi.</para>
    /// </summary>
    private void DiscardPending(LicenseSmsTransaction tx, LicenseSmsBalance balance)
    {
        _db.Entry(tx).State = EntityState.Detached;

        var entry = _db.Entry(balance);
        switch (entry.State)
        {
            // Hiç yazılamamış yeni satır: izleyicide kalmasının anlamı yok.
            case EntityState.Added:
                entry.State = EntityState.Detached;
                break;
            // Bellekte uygulanmış delta geri alınır — DB'deki değer geçerli.
            case EntityState.Modified:
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                break;
        }
    }
}
