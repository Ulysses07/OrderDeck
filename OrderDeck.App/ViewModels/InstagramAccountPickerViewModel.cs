using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services.Instagram;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.App.ViewModels;

/// <summary>"Instagram hesabı ekle" çekmecesinde bir seçenek.</summary>
/// <param name="Display">Seçenek metni.</param>
/// <param name="CanSelect">Sayfanın Instagram hesabı var: seçilebilir. Yoksa seçenek pasif görünür.</param>
public sealed record InstagramAccountOption(string PageId, string PageName, string Display, bool CanSelect);

/// <summary>
/// "Instagram hesabı ekle" çekmecesinin modeli (çoklu hesap tasarımı §8.6.1): zaten bağlı Sayfalar listelenmez,
/// Instagram hesabı olmayan Sayfa pasif görünür, ilk seçilebilir seçenek önceden seçilidir. Seçenekler listeleme
/// sırasıyladır.
/// </summary>
public sealed partial class InstagramAccountPickerViewModel : ObservableObject
{
    public InstagramAccountPickerViewModel(IReadOnlyList<FacebookAddablePage> pages)
    {
        Options = pages
            .Where(page => !page.AlreadyConnected)
            .Select(page => page.IgUserId is { } igUserId
                ? new InstagramAccountOption(page.PageId, page.PageName,
                    InstagramSettingsTexts.OptionWithAccount(
                        InstagramSettingsTexts.AccountLabel(igUserId, page.IgUsername), page.PageName),
                    CanSelect: true)
                : new InstagramAccountOption(page.PageId, page.PageName,
                    InstagramSettingsTexts.OptionWithoutAccount(page.PageName), CanSelect: false))
            .ToList();
        _selected = Options.FirstOrDefault(option => option.CanSelect);
    }

    /// <summary>Çekmecede gösterilen seçenekler.</summary>
    public IReadOnlyList<InstagramAccountOption> Options { get; }

    /// <summary>Seçili seçenek; seçim yoksa null.</summary>
    [ObservableProperty] private InstagramAccountOption? _selected;
}
