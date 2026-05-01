using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Backup;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class RestoreDialogViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RestoreDialogViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rd-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeBackupClient : IBackupClient
    {
        public Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default) =>
            Task.FromResult(new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, false, machineName));
        public Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupMetadata>>(Array.Empty<BackupMetadata>());
        public Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task DeleteAsync(Guid backupId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Populate_OrdersBackupsByCreatedAtDescending()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));

        var older = new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddDays(-2), false, "A");
        var newer = new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, false, "A");
        sut.Populate(new[] { older, newer });

        sut.AvailableBackups[0].Should().BeEquivalentTo(newer);
        sut.AvailableBackups[1].Should().BeEquivalentTo(older);
    }

    [Fact]
    public void RestoreLatestCommand_Disabled_WhenAvailableBackupsEmpty()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));
        sut.RestoreLatestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SkipCommand_FiresCloseRequestedEvent()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));
        var fired = false;
        sut.CloseRequested += (_, _) => fired = true;
        sut.SkipCommand.Execute(null);
        fired.Should().BeTrue();
    }
}
