using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using OrderDeck.App.Views.Shell;

namespace OrderDeck.Tests.App;

/// <summary>
/// Dar modda (pencere &lt; 1360 DIP, <c>IsCompact</c>) bağlantı paneli — senkron durum satırı dahil —
/// gizli. Senkron sağlıksızsa operatör yine görsün: panelin yerinde küçük sarı nokta, ipucu durum
/// satırınınkiyle aynı (D3 incelemesi). Geniş modda nokta yok (satır zaten görünür).
/// </summary>
public class ShellSidebarSyncDotTests
{
    /// <summary>Kabuk ViewModel'inin yalnız noktanın okuduğu üç özelliği. WPF bağlaması public tip ister.</summary>
    public sealed class FakeShell : INotifyPropertyChanged
    {
        private bool _isCompact;
        private bool _isSyncHealthy = true;

        public bool IsCompact { get => _isCompact; set { _isCompact = value; Raise(); } }
        public bool IsSyncHealthy { get => _isSyncHealthy; set { _isSyncHealthy = value; Raise(); } }
        public string SyncStatusTooltip { get; } = "Çevrimdışı — 2 değişiklik bekliyor";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [Fact]
    public void Dar_modda_sagliksiz_senkron_sari_nokta_olarak_gorunur()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var vm = new FakeShell();
            var sidebar = new ShellSidebar { DataContext = vm };
            void Layout()
            {
                ThemeTestHost.Pump();
                sidebar.Measure(new Size(400, 900));
                sidebar.Arrange(new Rect(0, 0, 400, 900));
                sidebar.UpdateLayout();
            }
            var dot = (FrameworkElement)sidebar.FindName("SyncDot");

            Layout();
            Assert.Equal(Visibility.Collapsed, dot.Visibility);      // geniş, sağlıklı

            vm.IsSyncHealthy = false;
            Layout();
            Assert.Equal(Visibility.Collapsed, dot.Visibility);      // geniş: satırın kendisi görünür

            vm.IsCompact = true;
            Layout();
            Assert.Equal(Visibility.Visible, dot.Visibility);
            Assert.Equal(vm.SyncStatusTooltip, dot.ToolTip);
            var rail = (double)sidebar.FindResource("OD.Layout.SideWidthMin");
            Assert.True(dot.ActualWidth > 0 && dot.ActualWidth + dot.Margin.Left + dot.Margin.Right < rail,
                $"nokta {rail} DIP'lik dar rayı taşırmamalı ({dot.ActualWidth})");

            vm.IsSyncHealthy = true;
            Layout();
            Assert.Equal(Visibility.Collapsed, dot.Visibility);      // dar, sağlıklı
        });

        Assert.Null(error);
    }
}
