using System.Globalization;
using System.Net.Mail;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>Veri sorumlusu bilgisinin dışa görünümü (panel, kayıt formu, Shopper). <see cref="Version"/>: gösterilen içeriğin
/// sürümü (kayıt formu gönderimle birlikte geri yollar, karar 24). JSON adları (camelCase) Shopper'ın sözleşmesidir
/// (<c>OrderDeck-Shopper apps/shopper/src/privacy/dataController.ts</c>): <c>legalName</c>, <c>postalAddress</c>,
/// <c>kvkkEmail</c>, <c>kepAddress</c>, <c>updatedAt</c>; ad değişirse orası da değişir.</summary>
public sealed record DataControllerView(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress, int Version,
    DateTimeOffset UpdatedAt);

/// <summary>Panel/yönetim girdisi.</summary>
public sealed record DataControllerInput(string? LegalName, string? PostalAddress, string? KvkkEmail, string? KepAddress);

/// <summary>Veri sorumlusu bilgisinin yazımı yeniden denemede de eşzamanlı bir yazımla çakıştı ya da okuduğu satır eşzamanlı
/// silindi; bu yazım kaydedilmedi (<see cref="DataControllerProfiles.UpsertAsync"/>). Panel 409, yönetim sayfası bildirim
/// gösterir.</summary>
public sealed class DataControllerConflictException() : InvalidOperationException(DataControllerProfiles.ConflictMessage);

/// <summary>Yayıncı hesabı KVKK silmesiyle anonimleştirilmiş (ya da yok): veri sorumlusu bilgisi yazılmadı
/// (<see cref="DataControllerProfiles.UpsertAsync"/>). Panel 410 <c>customer-purged</c>, yönetim sayfası bildirim gösterir.</summary>
public sealed class DataControllerCustomerPurgedException() : InvalidOperationException(DataControllerProfiles.PurgedMessage);

/// <summary>Yayıncının veri sorumlusu bilgilerinin (<see cref="DataControllerProfile"/>) tek doğrulama, okuma ve yazma yolu
/// (KVKK K-3). Panel ucu ve yönetim sayfası aynı kuralları kullanır.</summary>
public static class DataControllerProfiles
{
    public const int LegalNameMax = 200;
    public const int PostalAddressMax = 500;
    public const int EmailMax = 254;

    public const string ConflictMessage = "Veri sorumlusu bilgileri aynı anda başka bir yerden değiştirildi; yeniden deneyin.";

    public const string PurgedMessage = "Yayıncı hesabı silinmiş (KVKK silmesi); veri sorumlusu bilgisi yazılamaz.";

    /// <summary>Profilin birincil anahtarı (ilk kayıt yarışı) ve sürüm satırının (müşteri, sürüm) tekil indeksi
    /// (<c>LicenseDbContext</c>; EF adlandırması).</summary>
    private const string ProfileKeyName = "PK_DataControllerProfiles";
    private const string VersionIndexName = "IX_DataControllerProfileVersions_CustomerId_Version";

    /// <summary>Kırpar, boşlukları tekler ve doğrular: ad/unvan ve adres zorunlu, e-posta geçerli (görünen ad olmadan), KEP varsa
    /// e-posta biçiminde; boşluk KEP null olur. Değer ilgili kişiye tek satır kimlik olarak gösterilir (kayıt formu, Shopper'ın
    /// " · " ile birleştirdiği satır, WPF ödeme mesajı): satır sonu, sekme ve ardışık boşluk tek boşluğa iner; kalan denetim
    /// (Cc: NUL …) ya da biçim (Cf: yazı yönü işaretleri U+202A–U+202E, U+2066–U+2069, sıfır genişlikli karakterler) karakteri
    /// reddedilir — gösterilen kimliği bozar ya da görsel olarak çarpıtır. Hata varsa Türkçe ileti, yoksa null.</summary>
    public static string? Validate(DataControllerInput input, out DataControllerInput normalized)
    {
        var name = Collapse(input.LegalName);
        var address = Collapse(input.PostalAddress);
        var email = Collapse(input.KvkkEmail);
        var kep = string.IsNullOrWhiteSpace(input.KepAddress) ? null : Collapse(input.KepAddress);
        normalized = new DataControllerInput(name, address, email, kep);
        if (name.Length is 0 or > LegalNameMax) return $"Ad soyad / unvan zorunlu, en fazla {LegalNameMax} karakter.";
        if (HasHiddenCharacter(name)) return "Ad soyad / unvan görünmez ya da denetim karakteri (ör. yazı yönü işareti) içeremez.";
        if (address.Length is 0 or > PostalAddressMax) return $"Posta adresi zorunlu, en fazla {PostalAddressMax} karakter.";
        if (HasHiddenCharacter(address)) return "Posta adresi görünmez ya da denetim karakteri (ör. yazı yönü işareti) içeremez.";
        if (!IsEmail(email)) return "KVKK başvuru e-postası geçerli bir e-posta adresi olmalı.";
        if (kep is not null && !IsEmail(kep)) return "KEP adresi geçerli bir e-posta biçiminde olmalı.";
        return null;
    }

    /// <summary>Boşluk dizilerini (satır sonu, sekme, U+2028 dahil: <see cref="char.IsWhiteSpace(char)"/>) tek boşluğa indirir,
    /// baştan ve sondan kırpar.</summary>
    private static string Collapse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool HasHiddenCharacter(string value)
        => value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);

    private static bool IsEmail(string value)
        => value.Length is > 0 and <= EmailMax && !HasHiddenCharacter(value) && MailAddress.TryCreate(value, out var parsed)
            && parsed.Address == value;

    public static Task<DataControllerView?> ReadAsync(LicenseDbContext db, Guid customerId, CancellationToken ct)
        => db.DataControllerProfiles.AsNoTracking().Where(p => p.CustomerId == customerId)
            .Select(p => new DataControllerView(p.LegalName, p.PostalAddress, p.KvkkEmail, p.KepAddress, p.Version, p.UpdatedAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>Bu yayıncıda bu sürüm var mı (kayıt formunun gizli alanı yalnız o zaman kayda yazılır, Görev 22).</summary>
    public static Task<bool> VersionExistsAsync(LicenseDbContext db, Guid customerId, int version, CancellationToken ct)
        => db.DataControllerProfileVersions.AnyAsync(v => v.CustomerId == customerId && v.Version == version, ct);

    /// <summary><see cref="Validate"/>'ten geçmiş girdiyi yazar (yoksa ekler); kendi SaveChanges'ini çağırır. İçerik değiştiyse
    /// (ya da ilk kayıtsa) <see cref="DataControllerProfile.Version"/> artar ve içerik <see cref="DataControllerProfileVersion"/>'a
    /// eklenir, aynı SaveChanges'te (KVKK m. 10/1-a ispatı, karar 24). İçerik aynıysa hiçbir şey yazılmaz.
    /// <para>Eşzamanlı iki yazım (çift tıklama, panel ile yönetim aynı anda) aynı sürümü okur. İkincisi kayıtta çakışır: ilk
    /// kayıtta birincil anahtar, sonra sürüm jetonu ya da (müşteri, sürüm) tekil indeksi. Bütün kayıt geri alınır; bu yazımın
    /// girdileri izleyiciden çıkar, satır taze okunur ve bir kez yeniden denenir. İçerik artık aynıysa hiçbir şey yazılmaz,
    /// değilse sonraki sürüm yazılır. Hiçbir sürüm numarası iki içeriğe verilmez. İkinci çakışma
    /// <see cref="DataControllerConflictException"/>'dır (izleyicide girdi kalmaz).</para>
    /// <para>KVKK silmesi (<c>AdminCustomersController.Purge</c>) müşteri satırını anonimleştirip bırakır: FK geçerli kalır, silinmiş
    /// yayıncının hâlâ geçerli erişim jetonu (15 dk) ya da yöneticinin detay sayfası ona yeni kişisel veri yazabilirdi. Üç kural
    /// bunu her denemede kapatır. (1) Müşteri anonimleştirilmişse (<see cref="Customer.PurgedSentinel"/>) ya da yoksa
    /// <see cref="DataControllerCustomerPurgedException"/>, hiçbir şey yazılmaz. (2) Yazım müşterinin kimlik neslini
    /// (<see cref="Customer.AuthVersion"/>, eşzamanlılık jetonu) değiştirmeden koşul olarak kayda katar: denetimden sonra
    /// işlenen silme nesli artırdığı için kayıt düşer, yeniden deneme (1)'e takılır. Profil satırı yokken (ilk kayıt) sürüm
    /// jetonu bu yarışı göremezdi. (3) Önceki denemede okunan satır yeniden okumada yoksa eşzamanlı silinmiştir:
    /// yeniden EKLENMEZ, <see cref="DataControllerConflictException"/>. Silmenin kendi okumasıyla kaydı arasında ilk kez
    /// yazılan satırı silme kayıttan sonra yeniden arayıp siler (purge).</para></summary>
    public static async Task<DataControllerView> UpsertAsync(LicenseDbContext db, Guid customerId, DataControllerInput valid,
        string updatedBy, CancellationToken ct)
    {
        var sawRow = false;
        for (var attempt = 1; ; attempt++)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is null || customer.PasswordHash == Customer.PurgedSentinel)
                throw new DataControllerCustomerPurgedException();
            var row = await db.DataControllerProfiles.FirstOrDefaultAsync(p => p.CustomerId == customerId, ct);
            if (row is null && sawRow) throw new DataControllerConflictException();
            sawRow |= row is not null;
            var unchanged = row is not null && row.LegalName == valid.LegalName && row.PostalAddress == valid.PostalAddress
                && row.KvkkEmail == valid.KvkkEmail && row.KepAddress == valid.KepAddress;
            if (unchanged) return View(row!);

            if (row is null)
            {
                row = new DataControllerProfile { CustomerId = customerId };
                db.DataControllerProfiles.Add(row);
            }
            var now = DateTimeOffset.UtcNow;
            row.LegalName = valid.LegalName!;
            row.PostalAddress = valid.PostalAddress!;
            row.KvkkEmail = valid.KvkkEmail!;
            row.KepAddress = valid.KepAddress;
            row.Version++;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
            var version = new DataControllerProfileVersion
            {
                Id = Guid.NewGuid(), CustomerId = customerId, Version = row.Version, LegalName = row.LegalName,
                PostalAddress = row.PostalAddress, KvkkEmail = row.KvkkEmail, KepAddress = row.KepAddress, CreatedAt = now,
                CreatedBy = updatedBy,
            };
            db.DataControllerProfileVersions.Add(version);
            // (2): değeri değişmez, yalnız "UPDATE Customers … WHERE AuthVersion = <okunan>" koşulu olarak gider. Nesil artmadığı
            // için oturumlar (yenileme jetonları) etkilenmez.
            db.Entry(customer).Property(c => c.AuthVersion).IsModified = true;
            try
            {
                await db.SaveChangesAsync(ct);
                return View(row);
            }
            catch (DbUpdateException ex) when (IsConcurrentWrite(ex))
            {
                // Taze okuma için müşteri de çıkar: izlenen örnek yeniden sorgulanınca değerleri yenilenmezdi.
                db.Entry(row).State = EntityState.Detached;
                db.Entry(version).State = EntityState.Detached;
                db.Entry(customer).State = EntityState.Detached;
                if (attempt >= 2) throw new DataControllerConflictException();
            }
        }
    }

    private static DataControllerView View(DataControllerProfile row)
        => new(row.LegalName, row.PostalAddress, row.KvkkEmail, row.KepAddress, row.Version, row.UpdatedAt);

    /// <summary>Başka bir yazımla yarış: jeton (profilin sürümü ya da müşterinin kimlik nesli) ya da SQL Server'da (2601/2627)
    /// profilin birincil anahtarı veya sürüm satırının tekil indeksi. Başka hatalar (uzunluk, FK) programlama hatasıdır, yukarı
    /// gider.</summary>
    private static bool IsConcurrentWrite(DbUpdateException ex)
        => ex is DbUpdateConcurrencyException
            || (ex.InnerException is SqlException { Number: 2601 or 2627 } sql
                && (sql.Message.Contains(ProfileKeyName, StringComparison.Ordinal)
                    || sql.Message.Contains(VersionIndexName, StringComparison.Ordinal)));
}
