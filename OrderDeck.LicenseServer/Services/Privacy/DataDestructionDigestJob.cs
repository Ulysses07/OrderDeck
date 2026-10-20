using System.Globalization;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// İmha kayıtlarının veritabanı DIŞINDAKİ kopyası (KVKK Ö-12, Ö-13): her saat, bir önceki tam saatin
/// <see cref="DataDestructionRecord"/> satırlarını yöneticiye (<c>Admin:AlertEmail</c>) CSV metni olarak e-postalar. Yedekten geri
/// yükleme tabloyu yedek anına sarar; geri yüklemeden sonra olay kaynaklı silmeler (shopper ve yayıncı silmesi, banka veri talebi,
/// bağlantı kaldırma, kimlik değişimi, temizlik komutu) bu e-postalardan yeniden uygulanır (<c>deploy/README.md</c> "Geri
/// yükleme"). Kişisel veri YOK: tarih, lisans/konu/talep kimliği, hedef, eylem, sayı, ölçüt, tetikleyici, yapan. Kayıt yoksa
/// e-posta gitmez. Pencere <c>[önceki saat başı, bu saat başı)</c>: durum tutmaz, pencereler örtüşmez. Gönderim düşerse
/// <see cref="BankJobAlerts"/> günlüğe yazar; o saatin kayıtları tabloda durur, <see cref="RunForWindowEndingAsync"/> elle
/// yeniden koşulabilir.
/// </summary>
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class DataDestructionDigestJob
{
    public const string JobId = "data-destruction-digest";
    /// <summary>Saat başı +13 dk: 5 dakikalık ızgara, :02 (hesap yenileme) ve :07 (eşleşme taraması) dışında.</summary>
    public const string Cron = "13 * * * *";
    public const string Subject = "[OrderDeck] İmha kaydı özeti";

    private readonly LicenseDbContext _db;
    private readonly BankJobAlerts _alerts;

    public DataDestructionDigestJob(LicenseDbContext db, BankJobAlerts alerts) { _db = db; _alerts = alerts; }

    public Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return RunForWindowEndingAsync(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero), ct);
    }

    /// <summary><c>[end − 1 saat, end)</c> penceresinin kayıtlarını gönderir; gönderilen satır sayısını döner.</summary>
    public async Task<int> RunForWindowEndingAsync(DateTimeOffset end, CancellationToken ct)
    {
        var start = end.AddHours(-1);
        var rows = await _db.DataDestructionRecords.AsNoTracking()
            .Where(r => r.OccurredAt >= start && r.OccurredAt < end).OrderBy(r => r.OccurredAt).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        await _alerts.SendAsync(Subject, ToCsv(start, end, rows), CancellationToken.None);
        return rows.Count;
    }

    public static string ToCsv(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<DataDestructionRecord> rows)
    {
        static string Clean(string? value) => (value ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        // Parçaların hepsi $"…": birleşik ifade tek interpolasyon olarak AppendLine(IFormatProvider, …)'a gider.
        sb.AppendLine(inv, $"# İmha kayıtları {start.UtcDateTime:yyyy-MM-dd HH:mm}–{end.UtcDateTime:HH:mm} UTC, {rows.Count} satır. " +
            $"Kişisel veri içermez. SİLMEYİN: yedekten geri yüklemeden sonra silmeler buradan yeniden uygulanır " +
            $"(deploy/README.md, Geri yükleme).");
        sb.AppendLine("zaman_utc;lisans;konu;hedef;eylem;satir;olcut;tetikleyici;talep;yapan");
        foreach (var r in rows)
            sb.AppendLine(string.Join(';',
                r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv), r.LicenseId?.ToString() ?? "",
                r.SubjectId?.ToString() ?? "", Clean(r.Target), Clean(r.Action), r.RowCount.ToString(inv), Clean(r.Criterion),
                Clean(r.Trigger), r.RequestId?.ToString() ?? "", Clean(r.Actor)));
        return sb.ToString();
    }
}
