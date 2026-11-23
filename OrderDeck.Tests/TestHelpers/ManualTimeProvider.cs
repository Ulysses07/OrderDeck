namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Elle ilerletilen zaman (<see cref="TimeProvider"/>): zaman damgası tik cinsinden, başlangıç Unix çağından bir gün sonra.
/// Ödeme servisinin 5 dakika kuralı (spec 2026-10-11 §6.1-b, <c>PaymentRequestService.NotSyncedClock</c>) gibi süre
/// eşiklerini uykusuz, belirlenimli sınamak için. Emsal <c>Sync/SessionOrderSyncCadenceTests.ManualTime</c> (oradaki özel
/// kopyalar yerinde kalır).
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = TimeSpan.FromDays(1).Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
