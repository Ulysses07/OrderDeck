namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record PagedResult<T>(IReadOnlyList<T> Rows, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
