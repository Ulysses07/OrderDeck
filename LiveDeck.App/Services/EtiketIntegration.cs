using System;
using System.Globalization;
using System.Runtime.InteropServices;
using LiveDeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services;

/// <summary>
/// Optional Win32 integration with the legacy etiket.exe app: sets its first textbox
/// (price field) to the current order's unit price before LiveDeck writes the comment to
/// clipboard. Enabled via <see cref="AppSettings.EtiketIntegrationEnabled"/>.
/// </summary>
public sealed class EtiketIntegration
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

    private const uint WM_SETTEXT = 0x000C;

    private readonly AppSettings _settings;
    private readonly ILogger<EtiketIntegration> _log;

    public EtiketIntegration(AppSettings settings, ILogger<EtiketIntegration> log)
    {
        _settings = settings;
        _log = log;
    }

    public bool TrySetPrice(decimal price)
    {
        if (!_settings.EtiketIntegrationEnabled) return false;

        var title = _settings.EtiketWindowTitle ?? "etiket";
        var window = FindWindow(null, title);
        if (window == IntPtr.Zero)
        {
            _log.LogDebug("Etiket window '{Title}' not found", title);
            return false;
        }

        var firstEdit = FindWindowEx(window, IntPtr.Zero, "WindowsForms10.EDIT.app.0.bf7d44_r0_ad1", null);
        if (firstEdit == IntPtr.Zero)
            firstEdit = FindWindowEx(window, IntPtr.Zero, "Edit", null);

        if (firstEdit == IntPtr.Zero)
        {
            _log.LogDebug("Etiket textBox1 not found (window class names vary by .NET version)");
            return false;
        }

        var priceText = ((int)price).ToString(CultureInfo.InvariantCulture);
        SendMessage(firstEdit, WM_SETTEXT, IntPtr.Zero, priceText);
        return true;
    }
}
