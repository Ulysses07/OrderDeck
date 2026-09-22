using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>İYS'ye tek bir izin satırı bildirimi.</summary>
/// <param name="Recipient">E.164, <c>+905XXXXXXXXX</c>.</param>
/// <param name="RecipientType">BIREYSEL / TACIR.</param>
/// <param name="ChannelType">MESAJ / ARAMA / EPOSTA.</param>
/// <param name="Status">Beyan edilen izin durumu.</param>
/// <param name="ConsentDate">Beyan tarihi: ONAY için onayın alındığı an, RET için
/// reddin anı (TR yerel saate çevrilerek gönderilir).</param>
/// <param name="SourceCode">HS_WEB / HS_MOBIL.</param>
/// <param name="RefId">Bizim kayıt kimliğimiz — mükerrer push'u zararsız kılar.</param>
public sealed record IysConsentRecord(
    string Recipient,
    string RecipientType,
    string ChannelType,
    IysConsentStatus Status,
    DateTimeOffset ConsentDate,
    string SourceCode,
    string RefId);

/// <summary>
/// <c>/iys/add</c> yanıtı. <see cref="Queued"/> "kuyruğa alındı" demektir —
/// <b>kabul edildi demek DEĞİL</b>. Kabul yalnız <c>/iys/search</c> ile
/// doğrulanır; 2026-09-17'de 284 kaydı bu ayrımı yapmadığımız için kaybettik.
/// </summary>
/// <param name="RawBody">Tanı kopyası, en fazla 2000 karakter — ayrıştırılmaz.</param>
public sealed record IysAddResult(string Code, string RawBody, bool Queued);

/// <summary><c>/iys/search</c> yanıtı; <see cref="Statuses"/> alıcı → İYS durumu.</summary>
/// <param name="RawBody">Tanı kopyası, en fazla 2000 karakter — ayrıştırılmaz;
/// durumlar tam gövdeden çıkar.</param>
public sealed record IysSearchResult(
    string Code,
    string RawBody,
    IReadOnlyDictionary<string, IysConsentStatus> Statuses);

/// <summary>
/// Kalıcı <b>yapılandırma</b> hatası: marka kodu (<c>code 60</c>) ya da kimlik
/// (<c>code 30</c>). Boru hattı durur — sessizce devam etmek bekleyen her
/// kaydı sırayla harcar, çünkü hepsi aynı hatayla düşer.
/// </summary>
public sealed class IysConfigurationException : Exception
{
    public string Code { get; }
    public IysConfigurationException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// Tek bir İYS çağrısının <b>hangi yayıncı adına</b> yapıldığı.
///
/// <para>İstemci kimliği global config'ten OKUMAZ; çağıran vermek zorundadır.
/// "Yanlış markaya sorma" hatası böylece derleme zamanında imkânsız olur:
/// hesabı elde etmenin tek yolu <c>NetgsmAccountService</c>, o da her zaman
/// tek bir lisansa bağlı satır döner.</para>
///
/// <para><see cref="LicenseId"/> çağrının kendisinde kullanılmaz — log ve
/// olay satırlarının hangi kiracıya ait olduğunu yazabilmek için taşınır.</para>
/// </summary>
public sealed record IysAccountContext(
    Guid LicenseId,
    string UserCode,
    string Password,
    string BrandCode);

public interface IIysClient
{
    /// <summary>
    /// Toplu izin bildirimi. Tek HTTP isteği; <paramref name="items"/> en fazla 20 satır.
    /// İstek <paramref name="account"/> markası altında yapılır.
    /// </summary>
    Task<IysAddResult> AddAsync(
        IysAccountContext account,
        IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default);

    /// <summary>
    /// Toplu izin sorgusu. Yanıtta olmayan alıcı <see cref="IysConsentStatus.Unknown"/> sayılır.
    /// Sorgu <paramref name="account"/> markası altında yapılır — onay marka başına
    /// tutulduğu için başka markaya sormak anlamsız bir cevap döndürür.
    /// </summary>
    Task<IysSearchResult> SearchAsync(
        IysAccountContext account,
        IReadOnlyList<string> recipients,
        CancellationToken ct = default);
}
