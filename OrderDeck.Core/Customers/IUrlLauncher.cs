namespace OrderDeck.Core.Customers;

/// <summary>Phase 4g: OS handler'a URL gönderme abstraction'ı (Process.Start için mock noktası).</summary>
public interface IUrlLauncher
{
    /// <summary>URL'i OS default handler ile aç. Exception fırlatabilir.</summary>
    void Launch(string url);
}
