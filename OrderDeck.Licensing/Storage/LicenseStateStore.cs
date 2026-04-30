namespace LiveDeck.Licensing.Storage;

public sealed class LicenseStateStore
{
    private readonly EncryptedStore _store;
    private readonly string _path;

    public LicenseStateStore(EncryptedStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public bool IsPresent => File.Exists(_path);

    public LicenseRecord? Load() => _store.TryLoad<LicenseRecord>(_path);

    public void Save(LicenseRecord record) => _store.Save(_path, record);

    public void Clear() => _store.Delete(_path);
}
