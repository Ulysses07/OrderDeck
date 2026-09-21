namespace OrderDeck.LicenseServer.Services.Audit;

public static class AuditEvents
{
    public const string AdminLogin = "admin.login";
    public const string AdminLogout = "admin.logout";
    public const string CustomerConfirmEmail = "customer.confirm-email";
    public const string LicenseIssue = "license.issue";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseExtend = "license.extend";
    public const string LicenseSlotsChange = "license.slots-change";
    public const string ActivationForceDeactivate = "activation.force-deactivate";
    public const string RefreshTokenIssued = "RefreshTokenIssued";
    public const string RefreshTokenRotated = "RefreshTokenRotated";
    public const string RefreshTokenRevoked = "RefreshTokenRevoked";

    // KVKK / GDPR compliance (Phase 5d)
    public const string CustomerDataExported = "customer.data-exported";
    public const string CustomerPurged = "customer.purged";

    // Shopper (müşteri app) silme talebi — elle işlenen KVKK akışı.
    public const string ShopperPurged = "shopper.purged";

    // Multi-operator (PR-5 Faz 1+2) — owner Customer'ın staff hesap CRUD'u.
    public const string OperatorInvited = "operator.invited";
    public const string OperatorDeleted = "operator.deleted";

    // Netgsm kurulum kapatma anahtarı (§2.5). Kapatma bir yayıncının tüm
    // SMS'ini keser; kimin ne zaman kestiği iz bırakmadan olmamalı.
    public const string NetgsmAccountDisable = "netgsm.account.disable";
    public const string NetgsmAccountEnable = "netgsm.account.enable";

    // §6 — ayrılışta İYS onay listesi dışa aktarımı. Dönüş yolunun taşıyıcısı:
    // İYS bir markanın onay listesini vermez, /iys/search numara listesi ister.
    public const string NetgsmAccountExport = "netgsm.account.export";
}

public static class AuditTargets
{
    public const string Admin = "admin";
    public const string Customer = "customer";
    public const string License = "license";
    public const string Activation = "activation";
    public const string RefreshToken = "RefreshToken";
    public const string Operator = "operator";
    public const string Shopper = "shopper";
    public const string NetgsmAccount = "netgsm-account";
}
