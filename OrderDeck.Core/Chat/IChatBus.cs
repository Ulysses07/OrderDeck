using System;
using System.Collections.Generic;

namespace LiveDeck.Core.Chat;

public interface IChatBus
{
    /// <summary>Subscribe to live messages. Disposing the returned token unsubscribes.</summary>
    IDisposable Subscribe(Action<ChatMessage> handler);

    /// <summary>Push a message to all current subscribers and add to recent ring buffer.</summary>
    void Publish(ChatMessage message);

    /// <summary>Last-N messages, oldest first. Used for overlay state snapshot.</summary>
    IReadOnlyList<ChatMessage> RecentMessages();
}
