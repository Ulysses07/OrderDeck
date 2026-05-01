using System;

namespace OrderDeck.Core.Sessions;

public sealed class SessionEndedEventArgs : EventArgs
{
    public string SessionId { get; }
    public long EndedAt { get; }
    public SessionEndedEventArgs(string sessionId, long endedAt)
    {
        SessionId = sessionId;
        EndedAt = endedAt;
    }
}
