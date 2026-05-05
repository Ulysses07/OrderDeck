using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;

namespace OrderDeck.App.ViewModels;

public sealed partial class AnimationPickerViewModel : ViewModelBase
{
    public ObservableCollection<AnimationCatalogEntry> Animations { get; } = new();

    private string _selectedId = "wheel";
    public string SelectedId
    {
        get => _selectedId;
        set
        {
            // Reject ids not in the catalog so the operator can never persist
            // an invalid selection. Empty list = bootstrap (accept any).
            if (Animations.Count > 0 && !Animations.Any(a => a.Id == value)) return;
            if (_selectedId == value) return;
            _selectedId = value;
            OnPropertyChanged();
        }
    }

    public void LoadAnimations(IReadOnlyList<AnimationCatalogEntry> entries)
    {
        Animations.Clear();
        foreach (var e in entries) Animations.Add(e);
        // Re-validate current selection against the new list.
        if (Animations.Count > 0 && !Animations.Any(a => a.Id == _selectedId))
        {
            _selectedId = Animations[0].Id;
            OnPropertyChanged(nameof(SelectedId));
        }
    }
}
