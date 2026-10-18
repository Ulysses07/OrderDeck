using FluentAssertions;
using OrderDeck.App.Views.Drawers;
using OrderDeck.Tests.Sync;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Yedek çekmecesinin "Onayla"sı düzenleme yönlendiricisinden geçer (spec 2026-10-13 §5.3 "Yedek çekmecesi",
/// <c>confirm-backup</c>; §5.7; W2-05): mod Off'ta bugünkü yerel onay (ağ yok), mod On'da devredilmiş yayının yedeği
/// komutla; çevrimdışıyken o kartın düğmesi pasif ve araç ipucu gösterir; ret satır içi (kartın kendi satırı).
/// </summary>
public sealed class BackupTransferRoutingTests : IDisposable
{
    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private async Task<(Guid Parent, Guid Backup)> RemoteBackupAsync()
    {
        var session = _bed.RemoteSession();
        var customer = _bed.RemoteCustomer();
        var parent = _bed.RemoteOrder(session, customer, cancelled: true);
        var backup = _bed.RemoteOrder(session, customer, 120m, tentative: true, parent: parent);
        await _bed.PullAsync();
        return (parent, backup);
    }

    private BackupRowViewModel Row(Guid backup) => new(_bed.LabelRepo.GetById(N(backup))!);

    [Fact]
    public async Task Mod_Onda_devredilmis_yayinin_yedegi_tek_confirm_backup_komutuyla_onaylanir()
    {
        _bed.ModeOn();
        var (_, backup) = await RemoteBackupAsync();
        var row = Row(backup);
        row.PriceText = "110";
        string? notified = null;

        await row.PromoteAsync(_bed.Router, id => notified = id);

        var call = _bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("confirm-backup");
        row.ActionButtonText.Should().Be("Onaylandı");
        row.IsActionable.Should().BeFalse();
        notified.Should().Be(N(backup), "kabuk kuyruktaki kopyayı onaydan sonra günceller");
        _bed.Local(backup).IsTentativeBackup.Should().Be(0, "yerel ayna yanıttan");
        _bed.Local(backup).Price.Should().Be(110m);
    }

    [Fact]
    public async Task Mod_Offta_yerel_onay_beklemeden_ve_istek_gitmeden()
    {
        _bed.ModeOff();
        var session = _bed.LocalSession(ended: true);
        var customer = Guid.NewGuid().ToString("N");
        var parent = _bed.LocalLabel(session, customer);
        var backup = _bed.LocalLabel(session, customer, 90m, parentLabelId: parent, tentative: true);
        var row = new BackupRowViewModel(_bed.LabelRepo.GetById(backup)!);

        var task = row.PromoteAsync(_bed.Router, null);

        task.IsCompletedSuccessfully.Should().BeTrue("mod Off'ta bugünkü gibi eşzamanlı");
        await task;
        row.ActionButtonText.Should().Be("Onaylandı");
        _bed.Local(backup).IsTentativeBackup.Should().Be(0);
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Cevrimdisiyken_sunucu_yedeginin_karti_pasif_ve_arac_ipucu_gosterir()
    {
        _bed.ModeOn();
        var (_, backup) = await RemoteBackupAsync();
        _bed.Online = false;

        var row = BackupRowViewModel.Create(_bed.LabelRepo.GetById(N(backup))!, _bed.Router);

        row.IsActionable.Should().BeFalse();
        row.ActionToolTip.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
    }

    [Fact]
    public async Task Cevrimiciyken_kart_etkin_arac_ipucu_yok()
    {
        _bed.ModeOn();
        var (_, backup) = await RemoteBackupAsync();

        var row = BackupRowViewModel.Create(_bed.LabelRepo.GetById(N(backup))!, _bed.Router);

        row.IsActionable.Should().BeTrue();
        row.ActionToolTip.Should().BeNull();
    }

    [Fact]
    public async Task Ret_kartin_satirinda_bildirim_gitmez_yeniden_denenebilir()
    {
        _bed.ModeOn();
        var (_, backup) = await RemoteBackupAsync();
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "session-live",
            new Dictionary<string, object?> { ["machine"] = "PC-A" });
        var row = Row(backup);
        string? notified = null;

        await row.PromoteAsync(_bed.Router, id => notified = id);

        row.ErrorText.Should().Be("Bu yayın PC-A'da canlı.");
        row.IsActionable.Should().BeTrue();
        notified.Should().BeNull();
        _bed.Local(backup).IsTentativeBackup.Should().Be(1);
    }
}
