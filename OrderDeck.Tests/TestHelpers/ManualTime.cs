namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Elle ilerletilen zaman: zaman damgası tik cinsinden. Başlangıç anı sabittir, saat yalnız
/// <see cref="Advance"/> ile ilerler. Zamanlayıcı SÜRMEZ (<c>CreateTimer</c> ezilmez): bu sağlayıcıyla yapılan
/// <c>Task.Delay(süre, time, ct)</c> GERÇEK süre bekler. Bekleyen kodu sınayan test saati ilerletip beklemenin
/// bitmesini ummaz; koşulu yoklar (üst sınır <see cref="TestPatience.Upper"/>).
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = TimeSpan.FromDays(1).Ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
