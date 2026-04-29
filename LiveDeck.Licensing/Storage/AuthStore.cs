namespace LiveDeck.Licensing.Storage;

public sealed class AuthStore
{
    private readonly EncryptedStore _store;
    private readonly string _path;

    public AuthStore(EncryptedStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public bool IsPresent => File.Exists(_path);

    public AuthRecord? Load() => _store.TryLoad<AuthRecord>(_path);

    public void Save(AuthRecord record) => _store.Save(_path, record);

    public void Clear() => _store.Delete(_path);
}
