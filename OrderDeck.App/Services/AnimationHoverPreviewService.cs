using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OrderDeck.App.Services;

/// <summary>
/// Singleton-style preview popup for animation cards. Owns one shared
/// WebView2 instance + WPF Popup. ShowFor positions the popup beside the
/// hover-anchor and navigates the WebView2 to the animation's preview URL.
/// Debounces by 300ms so a quick mouse-over doesn't trigger a flash.
///
/// Pattern: same as Linear's hover-preview, Stripe Dashboard's row-peek,
/// Spotify's track-card hover. Single WebView2 instance is industry standard
/// for memory efficiency vs. per-card embed.
/// </summary>
public sealed class AnimationHoverPreviewService : IDisposable
{
    private const int PopupWidthPx = 340;
    private const int PopupHeightPx = 260;
    private const int HoverDelayMs = 300;

    private readonly Popup _popup;
    private readonly Border _container;
    private WebView2? _webView;
    private bool _coreInitialized;
    private CancellationTokenSource? _showCts;
    private string? _currentAnimationId;

    public AnimationHoverPreviewService()
    {
        _container = new Border
        {
            Width = PopupWidthPx,
            Height = PopupHeightPx,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCE, 0x46)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Opacity = 0.6,
                Color = Colors.Black
            }
        };

        _popup = new Popup
        {
            Width = PopupWidthPx,
            Height = PopupHeightPx,
            AllowsTransparency = true,
            Placement = PlacementMode.Right,
            HorizontalOffset = 8,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,           // we manage visibility manually
            Child = _container
        };
    }

    /// <summary>Schedule a popup show after the hover-debounce. Call from MouseEnter.</summary>
    public async void ShowFor(FrameworkElement anchor, string overlayBase, string animationId)
    {
        // Cancel any in-flight show that hasn't fired yet (operator hopped to a different card).
        _showCts?.Cancel();
        _showCts = new CancellationTokenSource();
        var token = _showCts.Token;

        try { await System.Threading.Tasks.Task.Delay(HoverDelayMs, token); }
        catch (OperationCanceledException) { return; }

        if (token.IsCancellationRequested) return;

        await EnsureWebViewAsync();
        if (_webView is null) return;     // EnsureWebView failed (no Edge runtime); silently no-op

        if (_currentAnimationId != animationId)
        {
            var url = $"{overlayBase}/overlay/preview?animation={Uri.EscapeDataString(animationId)}";
            try { _webView.CoreWebView2.Navigate(url); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WebView2 nav failed: {ex.Message}"); return; }
            _currentAnimationId = animationId;
        }

        _popup.PlacementTarget = anchor;
        _popup.IsOpen = true;
    }

    /// <summary>Hide the popup. Call from MouseLeave.</summary>
    public void Hide()
    {
        _showCts?.Cancel();
        _showCts = null;
        if (!_popup.IsOpen) return;
        _popup.IsOpen = false;
        // Stop the animation: navigate to about:blank so the AudioContext + animation timers die.
        if (_webView?.CoreWebView2 != null)
        {
            try { _webView.CoreWebView2.Navigate("about:blank"); } catch { }
        }
        _currentAnimationId = null;
    }

    public void Dispose()
    {
        _showCts?.Cancel();
        _popup.IsOpen = false;
        _popup.Child = null;
        _webView?.Dispose();
        _webView = null;
    }

    private async System.Threading.Tasks.Task EnsureWebViewAsync()
    {
        if (_webView is not null && _coreInitialized) return;

        if (_webView is null)
        {
            _webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x11, 0x18),
            };
            _container.Child = _webView;
        }

        if (!_coreInitialized)
        {
            try
            {
                // Default user-data-folder is the app's exe directory, which
                // fails when the app is in a read-only location (Program Files,
                // Windows Store install, or any signed installer drop). Pin
                // explicitly to %LOCALAPPDATA%\OrderDeck\WebView2 so init
                // succeeds regardless of where the .exe lives.
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OrderDeck", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);
                _coreInitialized = true;
            }
            catch (Exception ex)
            {
                // Surface the init failure so it's not invisible. We append
                // to a small log file in the same user-data folder area so
                // the operator (or a later debug session) can see why hover
                // preview gave up. The "Önizle" button keeps working as
                // fallback regardless.
                try
                {
                    var logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OrderDeck");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(
                        Path.Combine(logDir, "webview2-init.log"),
                        $"[{DateTime.UtcNow:O}] WebView2 init failed: {ex}\n\n");
                }
                catch { /* logging is best-effort */ }
                System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
                _webView = null;   // Fallback: hover preview silently disabled, "Önizle" button still works
            }
        }
    }
}
