using System;

namespace LiveDeck.Core.Time;

public sealed class SystemClock : IClock
{
    public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
