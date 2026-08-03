using System;
using System.Collections.Generic;
using System.Linq;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Dönem raporunun ham satırı: TEK bir platform hesabının TEK bir GÜNDEKİ
/// sipariş özeti. Aynı kişi birden fazla platformda ya da birden fazla günde
/// alışveriş yapmışsa burada birden fazla satır olarak görünür —
/// birleştirme <see cref="PeriodReportBuilder"/> işi.
///
/// Gün kırılımı e-Fatura için zorunlu: fatura günlük kesiliyor, yani aynı
/// kişinin farklı günlerdeki alımları ayrı faturalar.
/// </summary>
public sealed record PeriodAccountRow(
    string CustomerId,
    string? GroupId,
    string Platform,
    string Username,
    string? DisplayName,
    string? FullName,
    string? Tckn,
    string? Phone,
    string? Address,
    string? Email,
    long LastSeenAt,
    /// <summary>Yerel takvim günü, <c>yyyy-MM-dd</c>. Fatura gününü belirler.</summary>
    string Day,
    /// <summary>O gün bu hesap için basılan SON etiketin zamanı (unix saniye).
    /// Fatura saati buradan gelir.</summary>
    long LastPrintedAt,
    int OrderCount,
    decimal TotalAmount);

/// <summary>
/// Rapordaki bir KİŞİ — dönemin tamamı için toplanmış hâli. Aynı kişinin
/// farklı platform hesapları <c>GroupId</c> üzerinden tek satırda toplanmıştır.
/// Bu, operatörün ekranda gördüğü ve "Detay" sayfasına yazılan görünüm;
/// e-Fatura satırları için <see cref="PeriodInvoiceRow"/> kullanılır.
/// </summary>
public sealed record PeriodCustomerRow(
    string PersonKey,
    string? FullName,
    string? Tckn,
    string? Phone,
    string? Address,
    string? Email,
    string Accounts,
    string DisplayLabel,
    int AccountCount,
    int DayCount,
    int OrderCount,
    decimal TotalAmount)
{
    /// <summary>
    /// Fatura kesilebilir mi? Tek şart Ad Soyad. Muhasebenin kullandığı
    /// e-Arşiv şablonunda adres/telefon/e-posta sütunları boş bırakılıyor,
    /// TCKN de bilinmiyorsa <c>11111111111</c> ile dolduruluyor — dolayısıyla
    /// eksik olan tek şey isim olabilir.
    /// </summary>
    public bool HasInvoiceInfo => !string.IsNullOrWhiteSpace(FullName);

    public string InvoiceStatusLabel => HasInvoiceInfo ? "Tam" : "Eksik";
}

/// <summary>
/// Tek bir e-Fatura satırı = bir kişinin bir GÜNDEKİ toplam alımı.
/// Şablonda bir fatura tek kalem olarak gidiyor (miktar 1, tutar KDV dahil).
/// </summary>
public sealed record PeriodInvoiceRow(
    string PersonKey,
    string Day,
    DateTimeOffset IssuedAt,
    string FullName,
    string? Tckn,
    int OrderCount,
    decimal TotalAmount)
{
    /// <summary>Şablonda ad ve soyad ayrı sütun; elimizde tek alan var.
    /// Kural: SON kelime soyad, öncesi ad. Tek kelimelik isimde soyad boş.</summary>
    public string FirstName
    {
        get
        {
            var i = FullName.LastIndexOf(' ');
            return i < 0 ? FullName : FullName[..i];
        }
    }

    public string LastName
    {
        get
        {
            var i = FullName.LastIndexOf(' ');
            return i < 0 ? "" : FullName[(i + 1)..];
        }
    }
}

/// <summary>
/// Hesap+gün bazlı ham satırları iki görünüme indirger. Veritabanından ayrı
/// tutuldu: gruplama kuralı faturayı doğrudan etkilediği için DB'siz test
/// edilebilmeli.
/// </summary>
public static class PeriodReportBuilder
{
    /// <summary>Dönemin tamamı için kişi bazlı özet (ekran + Detay sayfası).</summary>
    public static IReadOnlyList<PeriodCustomerRow> Build(IEnumerable<PeriodAccountRow> rows)
    {
        return rows
            .GroupBy(PersonKeyOf, StringComparer.Ordinal)
            .Select(BuildPerson)
            .OrderByDescending(p => p.TotalAmount)
            .ThenBy(p => p.DisplayLabel, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// e-Fatura satırları: kişi × gün. Adı olmayan kişiler dışarıda bırakılır —
    /// isimsiz satır şablona yüklenemez, yüklemeyi tümden hataya düşürür.
    /// Sıralama şablondaki gibi tarih/saat artan.
    /// </summary>
    public static IReadOnlyList<PeriodInvoiceRow> BuildInvoices(IEnumerable<PeriodAccountRow> rows)
    {
        return rows
            .GroupBy(r => (Person: PersonKeyOf(r), r.Day))
            .Select(BuildInvoice)
            .Where(inv => inv is not null)
            .Select(inv => inv!)
            .OrderBy(inv => inv.IssuedAt)
            .ToList();
    }

    /// <summary>GroupId varsa kişi anahtarı odur; yoksa hesap kendi başına bir
    /// kişidir. Chat'ten gelen müşterilerin çoğunda GroupId boştur.</summary>
    private static string PersonKeyOf(PeriodAccountRow r) =>
        string.IsNullOrWhiteSpace(r.GroupId) ? r.CustomerId : r.GroupId.Trim();

    /// <summary>Kimlik alanları en SON görülen hesaptan alınır: aynı kişinin iki
    /// hesabında farklı bilgi varsa güncel olan kazanır. Boşsa bir alttakine
    /// düşer.</summary>
    private static Func<Func<PeriodAccountRow, string?>, string?> PickerFor(
        IEnumerable<PeriodAccountRow> rows)
    {
        var byRecency = rows.OrderByDescending(r => r.LastSeenAt).ToList();
        return field => byRecency.Select(field)
                                 .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }

    private static PeriodCustomerRow BuildPerson(IGrouping<string, PeriodAccountRow> g)
    {
        var pick = PickerFor(g);
        var fullName = pick(r => r.FullName);

        var accounts = g
            .Select(r => $"{r.Platform}/{r.Username}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        return new PeriodCustomerRow(
            PersonKey:    g.Key,
            FullName:     fullName,
            Tckn:         pick(r => r.Tckn),
            Phone:        pick(r => r.Phone),
            Address:      pick(r => r.Address),
            Email:        pick(r => r.Email),
            Accounts:     string.Join(", ", accounts),
            DisplayLabel: fullName
                          ?? pick(r => r.DisplayName)
                          ?? g.OrderByDescending(r => r.LastSeenAt).First().Username,
            AccountCount: g.Select(r => r.CustomerId).Distinct(StringComparer.Ordinal).Count(),
            DayCount:     g.Select(r => r.Day).Distinct(StringComparer.Ordinal).Count(),
            OrderCount:   g.Sum(r => r.OrderCount),
            TotalAmount:  g.Sum(r => r.TotalAmount));
    }

    private static PeriodInvoiceRow? BuildInvoice(IGrouping<(string Person, string Day), PeriodAccountRow> g)
    {
        var fullName = PickerFor(g)(r => r.FullName);
        if (string.IsNullOrWhiteSpace(fullName)) return null;

        // Fatura saati: o gün basılan son etiketin anı — satış o an tamamlanmış
        // sayılır. Yerel saate çevrilir, şablon yerel tarih/saat bekliyor.
        var lastPrinted = g.Max(r => r.LastPrintedAt);

        return new PeriodInvoiceRow(
            PersonKey:   g.Key.Person,
            Day:         g.Key.Day,
            IssuedAt:    DateTimeOffset.FromUnixTimeSeconds(lastPrinted).ToLocalTime(),
            FullName:    fullName,
            Tckn:        PickerFor(g)(r => r.Tckn),
            OrderCount:  g.Sum(r => r.OrderCount),
            TotalAmount: g.Sum(r => r.TotalAmount));
    }
}
