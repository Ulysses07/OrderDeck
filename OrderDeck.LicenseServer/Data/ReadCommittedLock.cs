using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OrderDeck.LicenseServer.Data;

/// <summary>
/// RCSI açık veritabanında kilitli okuma (<c>WITH (READCOMMITTEDLOCK)</c>).
///
/// <para>Prod veritabanında <c>READ_COMMITTED_SNAPSHOT</c> açık (27.11.2026'da sorguyla doğrulandı; veritabanı
/// <c>EnsureCreated()</c> ile kuruldu ve EF onu RCSI açık oluşturur). Satır sürümlü okuma commit olmamış bir yazımı
/// beklemez, ifadenin başındaki son commit'i okur. Uçuştaki bir işlemin sonucunu görmesi gereken okuma o tabloyu kilitli
/// okumalıdır: okuma yazarın X kilidinde bekler, commit'ten sonra yeni değeri görür. Örnekler: <c>sales-sync
/// disable</c>/<c>enable</c>'ın koşullu UPDATE'inden sonra modu okuyan istek, devrin oturuma yazıp siparişleri
/// damgaladığı işlemle yarışan ekleme (DM3). RCSI kapalı veritabanında ipucu varsayılan okumayla aynıdır.</para>
///
/// <para>Sorgu bu kaynağın üstüne kurulur (<c>Where</c>, <c>Select</c>, izleme); EF onu türetilmiş tablo olarak sarar,
/// ipucu tablo başvurusunda kalır. InMemory sağlayıcıda ham SQL yok: kaynak tablonun kendisidir (orada eşzamanlılık da
/// yok).</para>
///
/// <para>Bekleyen okuma hiçbir kilit tutmamalı. İşlemin içinde, kendi yazımlarından sonra yapılan kilitli okuma
/// kilitlenmeye (1205) açılır; bugünkü çağıranlar okumayı işlem açılmadan yapar.</para>
/// </summary>
public static class ReadCommittedLock
{
    /// <summary><paramref name="set"/>'in tablosu; ilişkisel sağlayıcıda <c>WITH (READCOMMITTEDLOCK)</c> ile okunur.</summary>
    public static IQueryable<T> WithReadCommittedLock<T>(this DbSet<T> set) where T : class
    {
        var db = set.GetService<ICurrentDbContext>().Context;
        if (!db.Database.IsRelational()) return set;

        var entity = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} modelde yok.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(T).Name} bir tabloya eşlenmemiş.");
        var schema = entity.GetSchema();
        // Ad modelden gelir, dışarıdan veri almaz.
        var sql = "SELECT * FROM " + (schema is null ? $"[{table}]" : $"[{schema}].[{table}]") + " WITH (READCOMMITTEDLOCK)";
        return set.FromSqlRaw(sql);
    }
}
