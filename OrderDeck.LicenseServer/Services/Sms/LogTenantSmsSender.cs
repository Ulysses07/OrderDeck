using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Dev/test: kiracı SMS'ini yalnız loglar. Kardeşi <see cref="LogSmsSender"/>.</summary>
public sealed class LogTenantSmsSender : ITenantSmsSender
{
    private readonly ILogger<LogTenantSmsSender> _log;
    public LogTenantSmsSender(ILogger<LogTenantSmsSender> log) => _log = log;

    public Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        _log.LogInformation("(log) tenant SMS to {Phone} header={Header}: {Message}",
            PiiMasker.MaskPhone(toPhone), credentials.Header, message);
        return Task.FromResult<string?>(null);
    }
}
