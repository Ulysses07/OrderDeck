using System.Linq;
using System.Windows.Controls;
using OrderDeck.Core.Shortcuts;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Aktif kısayolların listesi (eski <c>ShortcutHelpDialog</c> penceresi).
/// ViewModel'i yok; kayıttan okunan anonim satırlara bağlanıyor — pencere
/// sürümünden aynen taşındı.
/// </summary>
public partial class ShortcutHelpPage : UserControl
{
    private ShortcutHelpPage(ShortcutRegistry registry)
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

    public static ShortcutHelpPage Create(ShortcutRegistry registry) => new(registry);
}
