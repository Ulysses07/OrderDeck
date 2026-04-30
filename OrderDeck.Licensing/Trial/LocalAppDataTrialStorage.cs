using OrderDeck.Licensing.Storage;

namespace OrderDeck.Licensing.Trial;

/// <summary>
/// User-bound DPAPI-encrypted trial state at %LOCALAPPDATA%\OrderDeck\trial.dat.
/// Reuses Phase 4b <see cref="EncryptedStore"/> for serialization + DPAPI.
/// </summary>
public sealed class LocalAppDataTrialStorage : ITrialStorage
{
    private readonly EncryptedStore _store;
    private readonly string _path;

    public LocalAppDataTrialStorage(EncryptedStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public string Name => "localappdata";

    public TrialRecord? TryRead() => _store.TryLoad<TrialRecord>(_path);

    public void Write(TrialRecord record) => _store.Save(_path, record);

    public void Clear() => _store.Delete(_path);
}
