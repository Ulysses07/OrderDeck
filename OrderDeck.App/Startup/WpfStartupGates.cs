using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Gates;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Startup;

/// <summary>
/// <see cref="IStartupGates"/>'in gerçek uygulaması: her adımı gate
/// yığınına basar. ViewModel'ler burada çözülüyor — StartupFlow'un DI
/// tanımamasının bedeli bu ince katman.
/// </summary>
public sealed class WpfStartupGates : IStartupGates
{
    private readonly IAppGateService _gates;
    private readonly IServiceProvider _services;

    public WpfStartupGates(IAppGateService gates, IServiceProvider services)
    {
        _gates = gates;
        _services = services;
    }

    public async Task ShowBootAsync(Func<Task> work)
    {
        // ShowAsync içerik kurucusunu SENKRON çağırıyor (AppGateStack), bu
        // yüzden gate referansı await'ten önce elimizde oluyor.
        AppGate? opened = null;
        var pending = _gates.ShowAsync(g =>
        {
            opened = g;
            return new BootGate();
        });

        try
        {
            await work();
        }
        finally
        {
            opened?.Close(true);
            await pending;
        }
    }

    public Task<bool> ShowLoginAsync(bool isStartupGate)
    {
        var vm = _services.GetRequiredService<LoginDialogViewModel>();
        return _gates.ShowAsync(g => LoginGate.Create(g, vm, isStartupGate));
    }

    public async Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups)
    {
        // RestoreDialogViewModel DI'da kayıtlı DEĞİL (eski pencere de elle
        // kuruyordu) ve yedek listesini dışarıdan alması gerekiyor.
        var vm = new RestoreDialogViewModel(_services.GetRequiredService<RestoreService>());
        vm.Populate(backups);

        var restored = await _gates.ShowAsync(g => RestoreGate.Create(g, vm));
        return restored ? RestoreOutcome.Restored : RestoreOutcome.Skipped;
    }

    public Task ShowFirstRunAsync()
    {
        var vm = _services.GetRequiredService<FirstRunWizardViewModel>();
        return _gates.ShowAsync(g => FirstRunGate.Create(g, vm));
    }

    public async Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session)
    {
        // Üç sonuç ikili Close()'a sığmıyor; karar gate'in üzerinde duruyor.
        SessionRecoveryGate? view = null;
        await _gates.ShowAsync(g => view = SessionRecoveryGate.Create(g, session));
        return view?.Choice ?? SessionRecoveryChoice.Exit;
    }
}
