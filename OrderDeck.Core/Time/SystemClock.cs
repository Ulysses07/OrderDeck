using System;

namespace OrderDeck.Core.Time;

public sealed class SystemClock : IClock
{
    public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
