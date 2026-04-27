namespace LiveDeck.Core.Sales;

public sealed record OrderItem(
    string Id,
    string SessionId,
    string ActiveCodeId,
    string CustomerId,
    string Code,
    string Size,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    int Confidence,
    string Status,
    string OriginalMessageText,
    long CapturedAt,
    long StatusUpdatedAt,
    long? LabelPrintedAt,
    string? Notes);
