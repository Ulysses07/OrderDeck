namespace OrderDeck.LicenseServer.Services.Audit;

public static class AuditEvents
{
    public const string AdminLogin = "admin.login";
    public const string AdminLogout = "admin.logout";
    public const string CustomerConfirmEmail = "customer.confirm-email";
    public const string LicenseIssue = "license.issue";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseExtend = "license.extend";
    public const string ActivationForceDeactivate = "activation.force-deactivate";
    public const string RefreshTokenIssued = "RefreshTokenIssued";
    public const string RefreshTokenRotated = "RefreshTokenRotated";
    public const string RefreshTokenRevoked = "RefreshTokenRevoked";
}

public static class AuditTargets
{
    public const string Admin = "admin";
    public const string Customer = "customer";
    public const string License = "license";
    public const string Activation = "activation";
    public const string RefreshToken = "RefreshToken";
}
