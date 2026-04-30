namespace OrderDeck.Licensing.Trial;

/// <summary>
/// One of three persistent trial state locations. Read returns null when
/// the location is empty or unreadable; Write fails-fast on permission errors
/// and the caller logs warning + tries other locations.
/// </summary>
public interface ITrialStorage
{
    /// <summary>Human-readable identifier used in logs ("hkcu", "programdata", "localappdata").</summary>
    string Name { get; }

    /// <summary>Returns the persisted record, or null when missing/unreadable/tampered.</summary>
    TrialRecord? TryRead();

    /// <summary>Writes the record. Throws on failure — caller logs and continues with other storages.</summary>
    void Write(TrialRecord record);

    /// <summary>Removes the record. Used by tests; production code never clears.</summary>
    void Clear();
}
