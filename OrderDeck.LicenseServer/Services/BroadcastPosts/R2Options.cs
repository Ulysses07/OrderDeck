namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2Options
{
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string BucketName { get; set; } = "";

    public string ServiceUrl =>
        string.IsNullOrWhiteSpace(AccountId)
            ? ""
            : $"https://{AccountId}.r2.cloudflarestorage.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && !string.IsNullOrWhiteSpace(BucketName);
}
