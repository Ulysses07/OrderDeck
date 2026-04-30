using System.Diagnostics;

namespace LiveDeck.Core.Customers;

/// <summary>Default IUrlLauncher: <c>Process.Start</c> + <c>UseShellExecute=true</c>.</summary>
public sealed class ProcessUrlLauncher : IUrlLauncher
{
    public void Launch(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
