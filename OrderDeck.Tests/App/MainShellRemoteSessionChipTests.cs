using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.Sync;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kabukta uzak yayın çipleri ve kendi yayınının bandı (W2-09; spec 2026-10-13 §5.7, M18): band açıkken canlı yol
/// (sohbetten etiket, baskı) bugünkü gibi çalışır ve hiçbir komut gitmez; çipler her satış akışı turundan sonra
/// dispatcher üzerinden tazelenir; mod Off'ta çip ve band yoktur. Yönlendirici, akış turu ve çipler aynı yatakla
/// (<see cref="SalesEditTestBed"/>, <see cref="FakeSalesServer"/>) kurulur.
/// </summary>
public sealed class MainShellRemoteSessionChipTests : IDisposable
{
    private SalesEditTestBed? _bed;
    private SalesChangesPullService? _pull;

    public void Dispose() => _bed?.Dispose();

    private MainShellTestHarness.Harness Build() => MainShellTestHarness.Build(
        labelEditor: (db, labels) =>
        {
            _bed = new SalesEditTestBed(db, labels);
            return _bed.Router;
        },
        salesPull: _ => _pull = _bed!.CreatePull(),
        remoteSessions: db => new RemoteSessionsViewModel(
            new RemoteLiveSessionRepository(db), new SalesSyncStateRepository(db), new SessionRepository(db),
            _bed!.Commands, _bed.Connectivity,
            () => new SalesStateSnapshot(_bed.Server.Lisans, 1, null, null, [], _bed.Server.ForceEndSilenceMinutes, [],
                DateTimeOffset.UtcNow),
            () => _bed.Server.Lisans,
            ct => _pull!.TourOnceAsync(ct),
            new SystemClock()));

    private static void Exec(IDbConnectionFactory db, string sql, object? param = null)
    {
        using var c = db.Open();
        c.Execute(sql, param);
    }

    /// <summary>Gövdeyi KENDİ Dispatcher döngüsü olan STA iş parçacığında koşturur (bkz.
    /// <c>MainShellSalesFeedTests.RunOnOwnDispatcher</c>): turun olayı arka plandan dispatcher'a taşınır.</summary>
    private static void RunOnOwnDispatcher(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("STA dispatcher koşusu zaman aşımına uğramamalı");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task Band_acikken_sohbetten_etiket_ve_baski_bugunku_gibi_calisir_komut_gitmez()
    {
        using var h = Build();
        _bed!.ModeOn();
        var active = new SessionRepository(h.Db).GetActive()!;
        // Bu bilgisayarın canlı yayını başka bilgisayardan bitirildi (force-end): devir anı saklandı, yayın açık (M18).
        Exec(h.Db, "UPDATE StreamSession SET ReleasedAt = 950 WHERE Id = @id", new { id = active.Id });
        h.Vm.RefreshRemoteSessions();
        h.Vm.RemoteSessions!.OwnSessionBand.Should().StartWith("Bu yayın başka bir bilgisayardan bitirildi olarak işaretlendi");

        MainShellTestHarness.EnqueueLabel(h.Vm, "ornek_alici", 100m);
        h.Vm.PrintQueue.Should().ContainSingle();
        var labelId = h.Vm.PrintQueue.Single().Id;
        new LabelRepository(h.Db).GetById(labelId)!.SessionId.Should().Be(active.Id, "etiket canlı yayına yazıldı");

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().ContainSingle().Which.Should().ContainSingle(l => l.Id == labelId);
        new LabelRepository(h.Db).GetById(labelId)!.PrintedAt.Should().NotBeNull("yerel baskı işareti");
        h.Vm.PrintQueue.Should().BeEmpty();
        new SessionRepository(h.Db).GetActive()!.Id.Should().Be(active.Id, "yayın kesilmez");
        _bed.Server.CommandCalls.Should().BeEmpty("canlı yol komut göndermez, ağı beklemez");
        h.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
    }

    [Fact]
    public void Cipler_her_akis_turundan_sonra_dispatcher_uzerinden_tazelenir_mod_Offta_gizlenir()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        _bed!.ModeOn();
        _bed.Server.DeriveLiveSessions = true;
        var owner = Guid.NewGuid();
        var remote = Guid.NewGuid();
        _bed.Server.SeedSession(remote, DateTimeOffset.UtcNow.AddHours(-1), null, owner: owner,
            ownerMachineName: "PC-A");
        _bed.Server.SetReplicaLastSeen(owner, DateTimeOffset.UtcNow.AddMinutes(-1));
        h.Vm.RemoteSessions!.HasChips.Should().BeFalse("ön koşul: tur koşmadan çip yok");

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        await Dispatcher.Yield(DispatcherPriority.Background);

        h.Vm.RemoteSessions.HasChips.Should().BeTrue("tur aynayı yazdı, kabuk çipleri yeniden okudu");
        h.Vm.RemoteSessions.Chips.Single().Text.Should().StartWith("PC-A'da canlı · son sinyal ");
        h.Vm.RemoteSessions.Chips.Single().CanForceEnd.Should().BeFalse("sahip az önce sinyal verdi");

        _bed.ModeOff();
        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.ModeOff);
        await Dispatcher.Yield(DispatcherPriority.Background);

        h.Vm.RemoteSessions.HasChips.Should().BeFalse("mod Off'ta çip yok");
        h.Vm.RemoteSessions.Chips.Should().BeEmpty();
        h.Vm.RemoteSessions.OwnSessionBand.Should().BeNull();
        _bed.Server.Faults.Should().BeEmpty();
    });
}
