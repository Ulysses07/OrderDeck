using System;

namespace OrderDeck.Core.Sessions;

public sealed class SessionStartedEventArgs : EventArgs
{
    public string SessionId { get; }
    public long StartedAt { get; }
    public SessionStartedEventArgs(string sessionId, long startedAt)
    {
        SessionId = sessionId;
        StartedAt = startedAt;
    }
}
