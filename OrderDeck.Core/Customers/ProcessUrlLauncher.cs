using System;
using System.Diagnostics;

namespace OrderDeck.Core.Customers;

/// <summary>Default IUrlLauncher: <c>Process.Start</c> + <c>UseShellExecute=true</c>.
/// Yalnız http/https URL açar — <c>UseShellExecute=true</c> ile keyfi bir FileName
/// (exe yolu, "cmd /c ...", dosya yolu) OS handler'ı üzerinden çalıştırılabildiği
/// için (komut enjeksiyonu), şema açıkça doğrulanır. Bu abstraction yalnız
/// WhatsApp/ödeme (wa.me/https) linkleri açmak için kullanılıyor.</summary>
public sealed class ProcessUrlLauncher : IUrlLauncher
{
    public void Launch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Yalnız http/https URL açılabilir.", nameof(url));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }
}
