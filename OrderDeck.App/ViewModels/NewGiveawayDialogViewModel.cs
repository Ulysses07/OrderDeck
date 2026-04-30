using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrderDeck.App.ViewModels;

public sealed partial class NewGiveawayDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _keyword = "🌹";
    [ObservableProperty] private DurationOption _selectedDuration = new("1 dakika (60sn)", 60);
    [ObservableProperty] private int _winnerCount = 1;
    [ObservableProperty] private PlatformOption _selectedPlatform = new("Tümü", null);
    [ObservableProperty] private bool _preventRewinning = true;
    [ObservableProperty] private string? _validationError;

    public bool Saved { get; private set; }

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
