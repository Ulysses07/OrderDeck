namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Test/dev ortamı: Obifin yok. Her çağrı yapılandırma hatası döner ki iş
/// "sessizce boş" değil "açıkça kapalı" görünsün.</summary>
public sealed class NullObifinClient : IObifinClient
{
    private static ObifinApiException Off() => new(new[] { "obifin-not-configured" });
    public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Off();
    public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Off();
    public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => throw Off();
    public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => throw Off();
    public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default) => throw Off();
}
