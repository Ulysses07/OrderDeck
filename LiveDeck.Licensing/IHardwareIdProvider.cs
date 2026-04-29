namespace LiveDeck.Licensing;

public interface IHardwareIdProvider
{
    /// <summary>SHA-256 hex (lowercase, 64 chars) deterministically derived from machine + user identity.</summary>
    string GetHardwareId();
}
