using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LiveDeck.App.Services;

/// <summary>
/// Registers a Windows-global hotkey via user32. Currently used for F9 → "Clipboard the
/// selected order's label". When the hotkey fires, <see cref="HotkeyPressed"/> is raised.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NONE = 0;
    private const uint VK_F9 = 0x78;
    private const int HotkeyId = 0xC001;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotkeyPressed;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source is null) return;
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_hwnd, HotkeyId, MOD_NONE, VK_F9);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
