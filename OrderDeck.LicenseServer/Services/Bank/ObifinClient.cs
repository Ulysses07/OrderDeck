using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Obifin web servisi (doküman v1.03.04). Kimlik header'da, gövde form-urlencoded, HTTP 200 +
/// `Hata:[]` başarı. Sayısal alanlar STRING ("10.00"), tarih "yyyy-MM-dd HH:mm:ss" TR yerel.
/// Ham gövde yalnız tanı kopyası olarak kesilir — ayrıştırma TAM gövdeden (İYS 2026-09-22 dersi).
/// </summary>
public sealed class ObifinClient : IObifinClient
{
    public const int MaxRangeDays = 31;
    private const int DiagnosticCap = 2000;

    private readonly HttpClient _http;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinClient> _log;

    public ObifinClient(HttpClient http, IOptions<ObifinOptions> opt, ILogger<ObifinClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials creds, CancellationToken ct = default)
    {
        using var doc = await PostAsync(creds, "/webservis/hesaplar/hesaplistesi/", null, ct);
        return ReadList(doc, row => new ObifinAccountDto(
            Id: Long(row, "Id") ?? throw new ObifinProtocolException("hesap Id yok"),
            BankaKodu: Str(row, "BankaKodu") ?? "",
            BankaApiId: Long(row, "BankaApiId"),
            HesapNo: Str(row, "HesapNo"),
            Iban: Str(row, "IBAN"),
            Currency: Str(row, "ParaBirimi") ?? "TL",
            Balance: Dec(row, "Bakiye"),
            UpdatedAtTr: DateTr(row, "GuncellemeTarihi"),
            Active: Str(row, "Durum") == "1",
            NotificationNote: Str(row, "BildirimNotu")));
    }

    public async Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials creds, CancellationToken ct = default)
    {
        using var doc = await PostAsync(creds, "/webservis/bankaapi/liste/", null, ct);
        // BankaApiId sessizce 0 olsaydı bağlantı servisi 0'ı gerçek kimlik diye saklar, silme yanlış kaydı bulurdu.
        return ReadList(doc, row => new ObifinBankConnectionDto(
            BankaApiId: Long(row, "BankaApiId") ?? throw new ObifinProtocolException("banka bağlantısı BankaApiId yok"),
            BankaKodu: Str(row, "BankaKodu") ?? "",
            Name: Str(row, "BankaApiAdi"),
            Active: Str(row, "Durum") is null or "1"));
    }

    public async Task AddBankConnectionAsync(ObifinCredentials creds, string bankaKodu, IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bankaKodu) || bankaKodu.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Banka kodu yalnız harf/rakam olabilir.", nameof(bankaKodu));
        using var _ = await PostAsync(creds, $"/webservis/bankaapi/ekle/{bankaKodu}/", form, ct);
    }

    public async Task RemoveBankConnectionAsync(ObifinCredentials creds, long bankaApiId, CancellationToken ct = default)
    {
        using var _ = await PostAsync(creds, "/webservis/bankaapi/sil/",
            new Dictionary<string, string> { ["BankaApiId"] = bankaApiId.ToString(CultureInfo.InvariantCulture) }, ct);
    }

    public async Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(
        ObifinCredentials creds, DateOnly fromTr, DateOnly toTr, long? sinceId, int pageNo, int pageSize,
        CancellationToken ct = default)
    {
        if (toTr < fromTr) throw new ArgumentOutOfRangeException(nameof(toTr), "Bitiş başlangıçtan önce.");
        if (toTr.DayNumber - fromTr.DayNumber + 1 > MaxRangeDays)
            throw new ArgumentOutOfRangeException(nameof(toTr), "Obifin tarih aralığı 31 günü aşamaz.");
        if (pageNo < 1) throw new ArgumentOutOfRangeException(nameof(pageNo));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var form = new Dictionary<string, string>
        {
            ["BaslangicTarihi"] = fromTr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["BitisTarihi"] = toTr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["SayfaBasinaKayitSayisi"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["SayfaNo"] = pageNo.ToString(CultureInfo.InvariantCulture),
        };
        if (sinceId is { } s) form["BaslangicHareketId"] = s.ToString(CultureInfo.InvariantCulture);

        using var doc = await PostAsync(creds, "/webservis/hesaphareketleri/hesaphareketleriliste/", form, ct);
        var root = doc.RootElement;
        var items = ReadList(doc, row =>
        {
            var signed = Dec(row, "TutarEksiArti") ?? Dec(row, "Tutar")
                ?? throw new ObifinProtocolException("hareket tutarı yok");
            return new ObifinTransactionDto(
                Id: Long(row, "Id") ?? throw new ObifinProtocolException("hareket Id yok"),
                AccountId: Long(row, "HesapId") ?? 0,
                BankaKodu: Str(row, "BankaKodu") ?? "",
                OccurredAtTr: DateTr(row, "IslemZamaniDT") ?? throw new ObifinProtocolException("IslemZamaniDT yok"),
                SignedAmount: signed,
                Currency: Str(row, "ParaBirimi") ?? "TL",
                Description: Str(row, "Aciklama") ?? Str(row, "IslemAciklama"),
                TransactionCode: Str(row, "IslemKodu"),
                CommonType: Str(row, "OrtakIslemTipi"),
                BankReference: Str(row, "IslemNo"),
                CounterpartyIban: Str(row, "KarsiHesapIBAN"),
                CounterpartyName: Str(row, "GonderenAdi"),
                CounterpartyTaxId: Str(row, "BorcluVKN") ?? Str(row, "AmirVKN") ?? Str(row, "LehdarTCKN"),
                RawJson: row.GetRawText());
        });
        return new ObifinPage<ObifinTransactionDto>(items,
            PageNo: Int(root, "SayfaNo") ?? pageNo,
            TotalPages: Int(root, "ToplamSayfaSayisi"),
            TotalCount: Int(root, "ToplamKayitSayisi"),
            PageSize: Int(root, "SayfaBasinaKayitSayisi") ?? pageSize);
    }

    // ---- ortak ----

    private async Task<JsonDocument> PostAsync(ObifinCredentials creds, string path,
        IReadOnlyDictionary<string, string>? form, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(creds.BaseUrl) ? _opt.DefaultBaseUrl : creds.BaseUrl;
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + path)
        {
            Content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>()),
        };
        req.Headers.TryAddWithoutValidation("KullaniciAdi", creds.UserCode);
        req.Headers.TryAddWithoutValidation("Sifre", creds.Password);
        req.Headers.TryAddWithoutValidation("APIKey", creds.ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            _log.LogWarning("Obifin JSON olmayan yanıt ({Status}) {Path}: {Head}", (int)resp.StatusCode, path, Diagnostic(body));
            throw new ObifinProtocolException($"Obifin JSON olmayan yanıt ({(int)resp.StatusCode}) {path}");
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new ObifinProtocolException($"Obifin beklenmeyen JSON kökü {path}");
        }
        // Doküman `Hata` için dizi anlatır; tek mesajın düz dize ("Hata":"…") geldiği de görüldü.
        // İkisi de Obifin hatasıdır — dize diye başarı sayılsaydı boş liste "hesap yok" sanılırdı.
        if (doc.RootElement.TryGetProperty("Hata", out var hata) && ReadHata(hata) is { } msgs)
        {
            doc.Dispose();
            throw new ObifinApiException(msgs);
        }
        // Sözleşmenin ikinci yarısı: Hata boş olsa da HTTP 2xx değilse başarı DEĞİL. Vekil/WAF JSON hatası
        // (429/502/503), bakım sayfası vb. Liste'siz döner; denetlenmezse boş liste = "yeni hareket yok /
        // hesaplar kayboldu" sanılır. Hata denetimi ÖNCE: Obifin'in kendi 4xx'i mesajlarını korur.
        if (!resp.IsSuccessStatusCode)
        {
            doc.Dispose();
            _log.LogWarning("Obifin HTTP {Status} {Path}: {Head}", (int)resp.StatusCode, path, Diagnostic(body));
            throw new ObifinProtocolException($"Obifin HTTP {(int)resp.StatusCode} {path}");
        }
        return doc;
    }

    /// <summary>Dolu dizi, dolu nesne ya da boş olmayan dize → mesaj listesi; boş dizi / boş nesne / boş dize / null →
    /// null (hata yok). Nesne: PHP anahtarlı diziyi <c>{"Hata":{"0":"…"}}</c> diye basar; mesajlar değerlerdir.</summary>
    private static List<string>? ReadHata(JsonElement hata) => hata.ValueKind switch
    {
        JsonValueKind.Array when hata.GetArrayLength() > 0 => hata.EnumerateArray().Select(e => e.ToString()).ToList(),
        JsonValueKind.Object when hata.EnumerateObject().Any() => hata.EnumerateObject().Select(p => p.Value.ToString()).ToList(),
        JsonValueKind.String when !string.IsNullOrWhiteSpace(hata.GetString()) => [hata.GetString()!],
        _ => null,
    };

    private static IReadOnlyList<T> ReadList<T>(JsonDocument doc, Func<JsonElement, T> map)
    {
        if (!doc.RootElement.TryGetProperty("Liste", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<T>();
        return list.EnumerateArray().Select(map).ToList();
    }

    private static string? Str(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Long(JsonElement row, string name)
        => long.TryParse(Str(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? Int(JsonElement row, string name)
        => int.TryParse(Str(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Yalnız "-10.00" biçimi. Binlik ayracı KABUL EDİLMEZ: `NumberStyles.Number` "10,50"yi
    /// InvariantCulture'da 1050 (100x) okurdu; tutar alanında sessiz hata yerine null → gürültülü hata.</summary>
    private static decimal? Dec(JsonElement row, string name)
        => decimal.TryParse(Str(row, name), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime? DateTr(JsonElement row, string name)
        => DateTime.TryParseExact(Str(row, name), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    private static string Diagnostic(string body) => body.Length > DiagnosticCap ? body[..DiagnosticCap] : body;
}
