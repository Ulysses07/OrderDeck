using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrderDeck.Core.Shortcuts;

namespace OrderDeck.App.Controls;

/// <summary>
/// Click → "tuş bekleniyor" moduna; bir sonraki KeyDown chord'u kaydeder
/// (modifier-only basışlar yok sayılır). Esc capture'ı iptal eder, Backspace temizler.
/// </summary>
public sealed class ShortcutCaptureButton : Button
{
    public static readonly DependencyProperty ChordProperty =
        DependencyProperty.Register(
            nameof(Chord), typeof(KeyChord), typeof(ShortcutCaptureButton),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnChordChanged));

    public KeyChord? Chord
    {
        get => (KeyChord?)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    private bool _capturing;
    private Brush? _originalBackground;

    public ShortcutCaptureButton()
    {
        Click += (_, _) => StartCapture();
        PreviewKeyDown += OnKeyDown;
        LostFocus += (_, _) => StopCapture();
        Loaded += (_, _) => UpdateLabel();
    }

    private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutCaptureButton btn) btn.UpdateLabel();
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _originalBackground = Background;
        Content = "… bekleniyor (Esc)";
        Background = Brushes.DarkOrange;
        Focus();
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        Background = _originalBackground;
        UpdateLabel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape) { StopCapture(); return; }
        if (e.Key == Key.Back)   { Chord = null; StopCapture(); return; }
        if (IsModifierOnly(e.Key)) return;

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        Chord = new KeyChord(modifiers, e.Key.ToString());
        StopCapture();
    }

    private void UpdateLabel()
    {
        if (_capturing) return;
        Content = Chord?.ToString() ?? "(atanmadı)";
    }

    private static bool IsModifierOnly(Key k) =>
        k is Key.LeftCtrl  or Key.RightCtrl
          or Key.LeftShift or Key.RightShift
          or Key.LeftAlt   or Key.RightAlt
          or Key.LWin      or Key.RWin
          or Key.System;

    private static KeyModifiers ConvertModifiers(ModifierKeys m)
    {
        var r = KeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control))  r |= KeyModifiers.Ctrl;
        if (m.HasFlag(ModifierKeys.Shift))    r |= KeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Alt))      r |= KeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Windows))  r |= KeyModifiers.Win;
        return r;
    }
}
