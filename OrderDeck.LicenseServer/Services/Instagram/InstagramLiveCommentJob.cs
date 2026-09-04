namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// live_comments webhook olayını işler. Task 8'de doldurulacak iskelet;
/// şimdilik yalnız Hangfire'ın kuyruklayabilmesi için mevcuttur.
/// </summary>
public sealed class InstagramLiveCommentJob
{
    public Task ProcessAsync(string rawBody, CancellationToken ct) => Task.CompletedTask;
}
