namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çağrı başına çözülen kimlik; loglanmaz, saklanmaz. Positional record'un üretilmiş
/// <c>ToString()</c>'i tüm alanları basacağından burada ezilir: parola ve API anahtarı maskelenir ki kayıt
/// yanlışlıkla loga ya da bir istisna mesajına düşerse sır sızmasın.</summary>
public sealed record ObifinCredentials(string BaseUrl, string UserCode, string Password, string ApiKey)
{
    public override string ToString()
        => $"ObifinCredentials {{ BaseUrl = {BaseUrl}, UserCode = {UserCode}, Password = ***, ApiKey = *** }}";
}

public sealed record ObifinAccountDto(
    long Id, string BankaKodu, long? BankaApiId, string? HesapNo, string? Iban, string Currency,
    decimal? Balance, DateTime? UpdatedAtTr, bool Active, string? NotificationNote);

public sealed record ObifinBankConnectionDto(long BankaApiId, string BankaKodu, string? Name, bool Active);

/// <summary>Tek hareket. Tarih TR yerel (dönüşüm çağıranda); tutar işaretli.</summary>
public sealed record ObifinTransactionDto(
    long Id, long AccountId, string BankaKodu, DateTime OccurredAtTr, decimal SignedAmount, string Currency,
    string? Description, string? TransactionCode, string? CommonType, string? BankReference,
    string? CounterpartyIban, string? CounterpartyName, string? CounterpartyTaxId, string RawJson);

public sealed record ObifinPage<T>(IReadOnlyList<T> Items, int PageNo, int? TotalPages, int? TotalCount, int PageSize);

/// <summary>Obifin `Hata[]` dolu döndü — mesajlar Obifin'in Türkçe metinleri.</summary>
public sealed class ObifinApiException : Exception
{
    public IReadOnlyList<string> Messages { get; }
    public ObifinApiException(IReadOnlyList<string> messages)
        : base("Obifin: " + string.Join(" | ", messages)) => Messages = messages;
}

/// <summary>JSON değil / beklenen şekil değil / HTTP 2xx dışı ve `Hata` boş (ör. vekil 502 HTML ya da
/// JSON gövdeli 503). Obifin'in kendi `Hata[]` mesajları ise <see cref="ObifinApiException"/>.</summary>
public sealed class ObifinProtocolException : Exception
{
    public ObifinProtocolException(string message) : base(message) { }
}

public interface IObifinClient
{
    Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials creds, CancellationToken ct = default);
    Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials creds, CancellationToken ct = default);
    /// <summary>`bankaapi/ekle/{bankaKodu}/`; form = bankaya özel alanlar (+ BankaApiAdi). Başarı = Hata boş.</summary>
    Task AddBankConnectionAsync(ObifinCredentials creds, string bankaKodu, IReadOnlyDictionary<string, string> form, CancellationToken ct = default);
    Task RemoveBankConnectionAsync(ObifinCredentials creds, long bankaApiId, CancellationToken ct = default);
    /// <summary>Aralık ≤ 31 gün (aksi ArgumentOutOfRange); `sinceId` = yalnız Id &gt; değer.</summary>
    Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(
        ObifinCredentials creds, DateOnly fromTr, DateOnly toTr, long? sinceId, int pageNo, int pageSize,
        CancellationToken ct = default);
}
