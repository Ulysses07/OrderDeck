using System.Linq;
using System.Windows;
using LiveDeck.Core.Shortcuts;

namespace LiveDeck.App.Views;

public partial class ShortcutHelpDialog : Window
{
    public ShortcutHelpDialog(ShortcutRegistry registry)
    {
        InitializeComponent();
        DataContext = new
        {
            Items = registry.GetActive()
                .Select(b => new
                {
                    DisplayName = ShortcutCommand.DisplayNames.TryGetValue(b.CommandId, out var n) ? n : b.CommandId,
                    ChordText = b.Chord.ToString()
                })
                .ToList()
        };
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
