using System.Windows;

namespace LiveDeck.App.Services;

public sealed class ClipboardService
{
    public void SetText(string text)
    {
        if (Application.Current.Dispatcher.CheckAccess())
            Clipboard.SetText(text);
        else
            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
    }
}
