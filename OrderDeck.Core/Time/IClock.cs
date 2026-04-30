namespace LiveDeck.Core.Time;

public interface IClock
{
    /// <summary>Current unix-seconds timestamp.</summary>
    long UnixNow();
}
