using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.ViewModels;

public sealed partial class RestoreDialogViewModel : ObservableObject
{
    private readonly RestoreService _service;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private BackupMetadata? _selectedBackup;
    [ObservableProperty] private bool _restoreCompleted;

    public ObservableCollection<BackupMetadata> AvailableBackups { get; } = new();

    public RestoreDialogViewModel(RestoreService service)
    {
        _service = service;
        RestoreLatestCommand = new AsyncRelayCommand(RestoreLatestAsync, () => !IsBusy && AvailableBackups.Count > 0);
        RestoreSelectedCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => !IsBusy && SelectedBackup is not null);
        SkipCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public IAsyncRelayCommand RestoreLatestCommand { get; }
    public IAsyncRelayCommand RestoreSelectedCommand { get; }
    public IRelayCommand SkipCommand { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler? RestoreCompletedEvent;

    partial void OnIsBusyChanged(bool value)
    {
        RestoreLatestCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBackupChanged(BackupMetadata? value)
    {
        RestoreSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Populate(IReadOnlyList<BackupMetadata> backups)
    {
        AvailableBackups.Clear();
        foreach (var b in backups.OrderByDescending(b => b.CreatedAt))
            AvailableBackups.Add(b);
        RestoreLatestCommand.NotifyCanExecuteChanged();
    }

    private Task RestoreLatestAsync() =>
        AvailableBackups.Count == 0
            ? Task.CompletedTask
            : RestoreInternalAsync(AvailableBackups[0]);

    private Task RestoreSelectedAsync() =>
        SelectedBackup is null
            ? Task.CompletedTask
            : RestoreInternalAsync(SelectedBackup);

    private async Task RestoreInternalAsync(BackupMetadata backup)
    {
        IsBusy = true;
        StatusMessage = "Yedek indiriliyor ve geri yükleniyor…";
        try
        {
            var result = await _service.RestoreAsync(backup.Id);
            if (result.Success)
            {
                StatusMessage = "Geri yükleme tamamlandı. Uygulama yeniden başlatılacak.";
                RestoreCompleted = true;
                RestoreCompletedEvent?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusMessage = $"Hata: {result.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
