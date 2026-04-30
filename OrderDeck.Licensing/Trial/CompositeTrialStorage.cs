using Microsoft.Extensions.Logging;

namespace OrderDeck.Licensing.Trial;

/// <summary>
/// Combines 3 trial state storages with OR-logic read (latest ExpiresAt wins) and
/// fan-out write. Partial write failures are logged and tolerated; total failure throws.
/// </summary>
public sealed class CompositeTrialStorage : ITrialStorage
{
    private readonly ITrialStorage[] _storages;
    private readonly ILogger<CompositeTrialStorage> _log;

    public CompositeTrialStorage(
        ITrialStorage hkcu,
        ITrialStorage programData,
        ITrialStorage localAppData,
        ILogger<CompositeTrialStorage> log)
    {
        _storages = new[] { hkcu, programData, localAppData };
        _log = log;
    }

    public string Name => "composite";

    public TrialRecord? TryRead()
    {
        var found = new List<TrialRecord>();
        foreach (var s in _storages)
        {
            try
            {
                var r = s.TryRead();
                if (r is not null) found.Add(r);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Trial storage {Name} read failed; skipping", s.Name);
            }
        }
        if (found.Count == 0) return null;
        return found.OrderByDescending(r => r.ExpiresAt).First();
    }

    public void Write(TrialRecord record)
    {
        var successCount = 0;
        foreach (var s in _storages)
        {
            try
            {
                s.Write(record);
                successCount++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Trial storage {Name} write failed; continuing", s.Name);
            }
        }
        if (successCount == 0)
            throw new InvalidOperationException("Trial state could not be persisted to any of the 3 locations.");
    }

    public void Clear()
    {
        foreach (var s in _storages)
        {
            try { s.Clear(); }
            catch (Exception ex) { _log.LogWarning(ex, "Trial storage {Name} clear failed; ignoring", s.Name); }
        }
    }
}
