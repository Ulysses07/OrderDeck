namespace OrderDeck.LicenseServer.Domain;

public enum BroadcastPostType
{
    Text = 0,
    Photo = 1,
    Video = 2
}

public sealed class BroadcastPost
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public BroadcastPostType Type { get; set; }
    public string? TextBody { get; set; }

    public string? MediaObjectKey { get; set; }
    public string? MediaContentType { get; set; }
    public long? MediaSizeBytes { get; set; }
    public int? MediaDurationSec { get; set; }
    public int? MediaWidth { get; set; }
    public int? MediaHeight { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
