using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Kişi başına TEK asıl kayıt kuralını veritabanına koyan filtreli tekil indeks
/// (B1): <c>(LicenseId, Platform, IdentityKey)</c>, yalnız asıl kayıtlar
/// (<c>MergedIntoId IS NULL</c>) ve boş olmayan anahtarlar. Uygulamanın "bu
/// kimliğin asıl kaydı var mı" okuması iki eşzamanlı isteğin ikisine de "yok"
/// diyebilir; yarışı kapatan bu indeks, kaybeden INSERT'ü ise sync ucu ve Shopper
/// kayıt/katılma yolları kendi içinde çözer (yeniden oku → asıl kaydı bul).
/// </summary>
public static class CustomerIdentityIndex
{
    public const string Name = "UX_WpfCustomerProjections_Identity";

    /// <summary>Kopyalar (MergedIntoId dolu) aynı kimliği taşır, filtre onları
    /// dışarıda bırakır; boş anahtar (yalnız boşluktan oluşan eski kullanıcı adı)
    /// kimlik değildir. <see cref="CustomerIdentityMergeJob.DuplicateHeadsSql"/>
    /// aynı filtreyle sayar.</summary>
    public const string Filter = "[MergedIntoId] IS NULL AND [IdentityKey] <> N''";

    /// <summary>Kayıt bu indekse mi çarptı (SQL Server 2601/2627 ve iletide indeks
    /// adı). İleti tekil anahtarın DEĞERİNİ (kullanıcı adı) taşır — günlüğe
    /// yazılmaz.</summary>
    public static bool IsViolation(DbUpdateException ex)
        => ex.InnerException is SqlException sql && IsViolation(sql);

    /// <inheritdoc cref="IsViolation(DbUpdateException)"/>
    public static bool IsViolation(SqlException ex)
        => ex.Number is 2601 or 2627 && ex.Message.Contains(Name, StringComparison.Ordinal);
}
