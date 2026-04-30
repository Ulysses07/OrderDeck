namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Customer'a admin işlemleri sonrası bilgilendirme emaili gönderir
/// (license issued / revoked / extended). Sync — Phase 4a SmtpEmailSender
/// catch-and-log pattern'i kullandığı için admin sayfası blocking olmaz.
/// </summary>
public sealed class AdminActionEmailService
{
    private readonly EmailSendCoordinator _coordinator;

    public AdminActionEmailService(EmailSendCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task NotifyLicenseIssuedAsync(
        Guid customerId, string licenseKey, string skuCode, DateTimeOffset expiresAt,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-issued",
            contextKey: licenseKey,
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseIssued(c.Name, licenseKey, skuCode, expiresAt, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);

    public Task NotifyLicenseRevokedAsync(
        Guid customerId, string licenseKey, string reason,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-revoked",
            contextKey: licenseKey + ":revoke",
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseRevoked(c.Name, licenseKey, reason, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);

    public Task NotifyLicenseExtendedAsync(
        Guid customerId, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-extended",
            contextKey: licenseKey + ":extend:" + Guid.NewGuid().ToString("N"),
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseExtended(c.Name, licenseKey, newExpiresAt, additionalDays, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);
}
