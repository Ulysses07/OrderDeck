using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;
using OrderDeck.Core.Settings;

namespace OrderDeck.App.ViewModels;

public sealed partial class NewGiveawayDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _keyword = "🌹";
    [ObservableProperty] private DurationOption _selectedDuration = new("1 dakika (60sn)", 60);
    [ObservableProperty] private int _winnerCount = 1;
    [ObservableProperty] private PlatformOption _selectedPlatform = new("Tümü", null);
    [ObservableProperty] private bool _preventRewinning = true;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string? _selectedAnimationId;
    [ObservableProperty] private IReadOnlyList<AnimationCatalogEntry> _availableAnimations = System.Array.Empty<AnimationCatalogEntry>();

    public bool Saved { get; private set; }

    public NewGiveawayDialogViewModel() { }

    public NewGiveawayDialogViewModel(AppSettings settings, AnimationCatalogClient? catalogClient = null)
    {
        _selectedAnimationId = settings.GiveawayAnimation.DefaultId;

        if (catalogClient is not null)
            _ = LoadCatalogAsync(catalogClient, settings.GiveawayAnimation.DefaultId);
    }

    private async System.Threading.Tasks.Task LoadCatalogAsync(AnimationCatalogClient client, string defaultId)
    {
        try
        {
            var entries = await client.LoadAsync();
            AvailableAnimations = entries;
            // Re-apply default selection after catalog loaded (ensure it's in the list)
            if (SelectedAnimationId is null)
                SelectedAnimationId = defaultId;
        }
        catch
        {
            // Silently ignore — ComboBox stays empty, Start() falls back to settings default
        }
    }

    public ObservableCollection<DurationOption> DurationOptions { get; } = new()
    {
        new("30 saniye", 30),
        new("1 dakika (60sn)", 60),
        new("2 dakika", 120),
        new("5 dakika", 300),
        new("Manuel bitir", 0)
    };

    public ObservableCollection<PlatformOption> PlatformOptions { get; } = new()
    {
        new("Tümü", null),
        new("Yalnız Instagram", new[] { "instagram" }),
        new("Yalnız TikTok",    new[] { "tiktok" })
    };

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Keyword) || Keyword.Length > 32)
        { ValidationError = "Anahtar kelime 1-32 karakter olmalı."; return false; }

        if (WinnerCount < 1 || WinnerCount > 50)
        { ValidationError = "Kazanan sayısı 1-50 arasında olmalı."; return false; }

        ValidationError = null;
        return true;
    }

    public void MarkSaved() => Saved = true;
}

public sealed record DurationOption(string Label, int Seconds);

public sealed record PlatformOption(string Label, IReadOnlyList<string>? Filter);
