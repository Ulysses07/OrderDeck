using System.Globalization;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// İmha kayıtlarının veritabanı DIŞINDAKİ kopyası (KVKK Ö-12, Ö-13): her saat, UTC saat başından önce yazılmış ve henüz
/// gönderilmemiş (<see cref="DataDestructionRecord.DigestedAt"/> boş) bütün <see cref="DataDestructionRecord"/> satırlarını
/// yöneticiye (<c>Admin:AlertEmail</c>) CSV metni olarak e-postalar. Yedekten geri yükleme tabloyu yedek anına sarar; geri
/// yüklemeden sonra olay kaynaklı silmeler (shopper ve yayıncı silmesi, banka veri talebi, bağlantı kaldırma, kimlik değişimi,
/// temizlik komutu) bu e-postalardan yeniden uygulanır (<c>deploy/README.md</c> "Geri yükleme"). Kişisel veri YOK: tarih,
/// lisans/konu/talep kimliği, hedef, eylem, sayı, ölçüt, tetikleyici, yapan. Kayıt yoksa e-posta gitmez.
/// <para><b>Durum tutar.</b> <see cref="DataDestructionRecord.DigestedAt"/> yalnız gönderim başarılı olunca yazılır
/// (<see cref="BankJobAlerts.SendOrThrowAsync"/>). Gönderim düşerse (SMTP hatası, <c>Admin:AlertEmail</c> boş) iş Failed olur
/// ve kayıtlar sonraki koşuda yeniden denenir; saatlerce süren bir kesinti ya da kuyruk gecikmesinden sonraki ilk koşu biriken
/// bütün kayıtları gönderir. Birikimi hemen göndermek için Hangfire panosundan <c>data-destruction-digest</c> elle tetiklenir.
/// E-posta gidip işaret yazılamazsa kayıtlar sonraki koşuda bir kez daha gider: çift satır zararsızdır, yeniden uygulama
/// idempotenttir. Aynı nedenle geri yüklemeden sonra, yedek anında henüz gönderilmemiş kayıtlar yeniden gönderilir.</para>
/// </summary>
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class DataDestructionDigestJob
{
    public const string JobId = "data-destruction-digest";
    /// <summary>Saat başı +13 dk: 5 dakikalık ızgara, :02 (hesap yenileme) ve :07 (eşleşme taraması) dışında.</summary>
    public const string Cron = "13 * * * *";
    public const string Subject = "[OrderDeck] İmha kaydı özeti";
    /// <summary>Bir e-postadaki en çok satır: uzun bir kesintinin birikimi tek dev e-postaya dönmesin.</summary>
    public const int MaxRowsPerEmail = 1000;

    private readonly LicenseDbContext _db;
    private readonly BankJobAlerts _alerts;
    private readonly TimeProvider _clock;

    public DataDestructionDigestJob(LicenseDbContext db, BankJobAlerts alerts, TimeProvider? clock = null)
    { _db = db; _alerts = alerts; _clock = clock ?? TimeProvider.System; }

    /// <summary>UTC saat başından önceki, gönderilmemiş kayıtları <see cref="MaxRowsPerEmail"/>'lik e-postalarla gönderir ve
    /// her e-postanın satırlarını gönderimden sonra işaretler; gönderilen satır sayısını döner.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var utc = _clock.GetUtcNow().UtcDateTime;
        var end = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        var total = 0;
        while (true)
        {
            var rows = await _db.DataDestructionRecords
                .Where(r => r.DigestedAt == null && r.OccurredAt < end)
                .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id)
                .Take(MaxRowsPerEmail).ToListAsync(ct);
            if (rows.Count == 0) return total;
            // Gönderim düşerse fırlar: işaret yazılmaz, iş Failed, kayıtlar sonraki koşuda yeniden denenir.
            await _alerts.SendOrThrowAsync(Subject, ToCsv(rows), CancellationToken.None);
            var sentAt = _clock.GetUtcNow();
            foreach (var r in rows) r.DigestedAt = sentAt;
            await _db.SaveChangesAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            total += rows.Count;
            if (rows.Count < MaxRowsPerEmail) return total;
        }
    }

    public static string ToCsv(IReadOnlyList<DataDestructionRecord> rows)
    {
        // Savunma: DataDestruction yazarken zaten çevirir (dış kopya tabloyla birebir); elle ya da eski kodla yazılmış satır için.
        static string Clean(string? value) => DataDestruction.CsvSafe(value);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var first = rows.Count == 0 ? default : rows.Min(r => r.OccurredAt).UtcDateTime;
        var last = rows.Count == 0 ? default : rows.Max(r => r.OccurredAt).UtcDateTime;
        // Parçaların hepsi $"…": birleşik ifade tek interpolasyon olarak AppendLine(IFormatProvider, …)'a gider.
        sb.AppendLine(inv, $"# İmha kayıtları {first:yyyy-MM-dd HH:mm}–{last:yyyy-MM-dd HH:mm} UTC, {rows.Count} satır. " +
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
