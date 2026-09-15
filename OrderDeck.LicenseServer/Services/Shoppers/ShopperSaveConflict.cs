using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Shoppers;

/// <summary>
/// Shopper yazan uçların DbUpdateConcurrencyException ayrıştırıcısı.
///
/// Shopper'da birden çok eşzamanlılık jetonu var (DeletedAt — R10-S02,
/// LastResetCodeIssuedAt — R10-S03) ve HER jeton, HER Shopper UPDATE'inin
/// WHERE'ine girer. "Çakışma = hesap silindi" varsayımı bu yüzden yanlış:
/// bir OTP üretimiyle çakışan PATCH'e "silindi" demek, DELETE'e "zaten
/// silinmiş, 204" demek (hesap açık kalır, KVKK silme talebi hiç açılmaz!)
/// gerçek hatalar üretir.
///
/// Sözleşme: çakışmada DB'nin güncel hâline bakılır.
/// - Shopper gerçekten silinmişse <c>true</c> döner; çağıran KENDİ
///   silinmiş-hesap yanıtını verir ve KAYIT DENENMEZ — retry, purge'ün
///   temizlediği veriyi/parolayı geri doldururdu (S02 diriltme yasağı).
/// - Silinmemişse çakışma başka bir jetonun çapraz ateşidir: jeton/orijinal
///   değerler DB'den tazelenir (isteğin BİLEREK değiştirdiği alanlar korunur,
///   kalanında DB kazanır — bayat değer geri yazılmaz) ve kayıt yeniden
///   denenir. Başarıda <c>false</c> döner; isteğin amacı yerine gelmiştir.
/// </summary>
public static class ShopperSaveConflict
{
    /// <summary>
    /// <c>true</c>: silinme yarışı kazandı, çağıran silinmiş-hesap yanıtını
    /// versin. <c>false</c>: çapraz-jeton çakışmasıydı, kayıt yeniden denendi
    /// ve BAŞARDI. Üst üste üç çakışma pratikte imkânsız; olursa istisna
    /// yükselir (500 — sessizce yanlış cevap vermekten iyidir).
    /// </summary>
    public static async Task<bool> DeletedWonAsync(
        LicenseDbContext db, DbUpdateConcurrencyException ex, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            foreach (var entry in ex.Entries)
            {
                var dbValues = await entry.GetDatabaseValuesAsync(ct);

                if (entry.Entity is Shopper)
                {
                    // Satır yoksa (hard delete — bugün olmuyor ama) ya da
                    // DeletedAt dolmuşsa: silinme kazandı.
                    if (dbValues is null
                        || dbValues[nameof(Shopper.DeletedAt)] is not null)
                        return true;
                }

                if (dbValues is null)
                {
                    // Bağımlı satırı purge silmiş (örn. refresh token);
                    // güncellenecek bir şey kalmadı, entry'yi bırak.
                    entry.State = EntityState.Detached;
                    continue;
                }

                if (entry.State == EntityState.Modified)
                {
                    // İsteğin bilinçli değişiklikleri korunur; kalan her
                    // alanda (jetonlar dahil) DB kazanır. Yalnız
                    // OriginalValues tazelemek YETMEZ: bayat current değer
                    // DetectChanges'ta "değişmiş" sayılıp DB'deki güncel
                    // değeri (örn. yeni jeton damgasını) geri ezerdi.
                    var deliberate = entry.Properties
                        .Where(p => p.IsModified)
                        .Select(p => p.Metadata.Name)
                        .ToHashSet();
                    foreach (var p in entry.Properties)
                    {
                        p.OriginalValue = dbValues[p.Metadata.Name];
                        if (!deliberate.Contains(p.Metadata.Name))
                        {
                            p.CurrentValue = dbValues[p.Metadata.Name];
                            p.IsModified = false;
                        }
                    }
                }
                else
                {
                    entry.OriginalValues.SetValues(dbValues);
                }
            }

            // Başarısız batch'teki Added satırlar sağlayıcıya göre uygulanmış
            // olabilir: gerçek SQL tüm batch'i geri alır ama InMemory
            // (testler) transaksiyonsuz — satır store'a girmiş olabilir ve
            // retry aynı PK'yı ikinci kez INSERT etmeye kalkar. DB'de zaten
            // varsa (bu entry'nin kendi değerleriyle yazıldı) Unchanged'e
            // çek; gerçek SQL'de sorgu boş döner ve entry Added kalır.
            foreach (var added in db.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added).ToList())
            {
                if (await added.GetDatabaseValuesAsync(ct) is not null)
                    added.State = EntityState.Unchanged;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return false;
            }
            catch (DbUpdateConcurrencyException next) when (attempt < 2)
            {
                ex = next;
            }
        }
    }
}
