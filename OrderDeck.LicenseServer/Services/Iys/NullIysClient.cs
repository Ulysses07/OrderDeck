using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Netgsm yapılandırılmamış ortamlar için istemci. Hiçbir şey göndermez ve
/// <b>hiçbir şeyi onaylamaz</b>: <c>Queued=false</c> döner, sorguya
/// <see cref="IysConsentStatus.Unknown"/> der. Sahte ONAY döndürmek dev'de
/// gerçek gönderim kapısını açar — fail-closed kuralı ortamdan bağımsızdır.
/// </summary>
public sealed class NullIysClient : IIysClient
{
    private readonly ILogger<NullIysClient> _log;

    public NullIysClient(ILogger<NullIysClient> log) => _log = log;

    public Task<IysAddResult> AddAsync(
        IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "İYS yapılandırılmamış: {Count} kayıt gönderilmedi (brand={Brand})",
            items.Count, account.BrandCode);
        return Task.FromResult(new IysAddResult("not-configured", "", Queued: false));
    }

    public Task<IysSearchResult> SearchAsync(
        IysAccountContext account, IReadOnlyList<string> recipients,
        CancellationToken ct = default)
        => Task.FromResult(new IysSearchResult(
            "not-configured", "", new Dictionary<string, IysConsentStatus>()));
}
