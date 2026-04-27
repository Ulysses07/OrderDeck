namespace LiveDeck.Core.Sales;

/// <summary>Workflow states for an OrderItem. Stored as TEXT in the DB.</summary>
public static class OrderStatus
{
    public const string New          = "new";
    public const string Pending      = "pending";       // confidence 50-79, awaiting operator approval
    public const string DmSent       = "dm_sent";
    public const string Paid         = "paid";
    public const string Shipped      = "shipped";
    public const string Completed    = "completed";
    public const string Cancelled    = "cancelled";

    public static readonly string[] All =
    {
        New, Pending, DmSent, Paid, Shipped, Completed, Cancelled
    };
}
