using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

public partial class ActiveProductBar : UserControl
{
    public ActiveProductBar() => InitializeComponent();

    /// <summary>
    /// Ctrl+K. Pencere seviyesindeki OnWindowPreviewKeyDown buraya yönlendirir
    /// — kısayolun tek sahibi pencere olsun diye kontrol kendi kısayolunu
    /// kurmuyor (iki yerde tanımlanınca hangisinin kazandığı belirsizleşir).
    /// </summary>
    public void FocusCode()
    {
        CodeBox.Focus();
        CodeBox.SelectAll();
    }
}
