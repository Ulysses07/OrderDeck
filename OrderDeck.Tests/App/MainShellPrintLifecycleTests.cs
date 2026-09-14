using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Labeling;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>R8-02: baskı yaşam döngüsünün Dispatcher gerçekliğinde testi.
///
/// xunit'in varsayılan ortamında await devamları kayıt sırasıyla (FIFO) aynı
/// thread üzerinde koşar; bu sıra <c>PrintGuardedAsync</c>'in finally
/// temizliğini EndStream'in devamından ÖNCE çalıştırıp R8-02A yarışını
/// maskeler. Gerçek WPF Dispatcher'da sıra farklı olabiliyor — R8 denetimi
/// üç bağımsız STA deneyinde, baskı başarılı ve kuyruk boşken "Etiketler
/// basılamadı; yayın açık bırakıldı" mesajını ve açık kalan yayını gösterdi
/// (tmp/audit-2026-09-14-r8/print-probe.log). Bu küme aynı senaryoları
/// gerçek bir STA Dispatcher üzerinde koşturur.</summary>
public sealed class MainShellPrintLifecycleTests
{
    /// <summary>Test gövdesini kendi Dispatcher döngüsü olan STA thread'de
    /// koşturur — üretimdeki devam sıralamasının aynısı.</summary>
    private static async Task RunOnStaDispatcherAsync(Func<Task> body)
    {
        Exception? failure = null;
        var done = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
            done.TrySetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        finished.Should().Be(done.Task, "STA dispatcher koşusu zaman aşımına uğramamalı");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>İlk çağrıda içeri girildiğini bildirir, serbest bırakılana
    /// kadar bloklar. Bildirim TCS ile: Dispatcher thread'i bloklamadan
    /// await edilebilsin.</summary>
    private sealed class BarrierPrinter : ILabelPrinter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<string> PrintedIds { get; } = new();
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool ThrowAfterRelease { get; set; }

        public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null)
        {
            Interlocked.Increment(ref _calls);
            lock (PrintedIds) PrintedIds.AddRange(labels.Select(l => l.Id));
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("BarrierPrinter serbest bırakılmadı");
            if (ThrowAfterRelease)
                throw new InvalidOperationException("yazıcı arızası (test)");
        }
        public void PrintGiftLabels(IReadOnlyList<Label> labels) { }
    }

    // R8-02A: baskı başarılı, kuyruk boş — yayın yine de açık kalıyordu.
    // EndStream, _printTask içindeki ÇEKİRDEK görevi bekliyor; görevin
    // tamamlanması, wrapper'ın finally ile alanı temizlemesiyle aynı olay
    // değil. Dispatcher'da EndStream'in devamı önce koşarsa guard dolu alanı
    // görüp false dönüyor → yanlış "basılamadı" uyarısı + açık yayın.
    [Fact]
    public async Task YayiniBitir_bas_secenegi_dispatcher_uzerinde_yayini_gercekten_kapatir()
        => await RunOnStaDispatcherAsync(async () =>
        {
            var printer = new BarrierPrinter();
            using var h = MainShellTestHarness.Build(printerOverride: printer);
            h.Dialogs.ThreeWayResult = _ => true; // Evet: hepsini bas ve bitir
            MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100);

            var print = h.Vm.PrintCommand.ExecuteAsync(null);
            await printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var end = h.Vm.EndStreamCommand.ExecuteAsync(null);
            printer.Release.Set();
            await Task.WhenAll(print, end);

            printer.Calls.Should().Be(1, "aynı etiket ikinci kez yazıcıya gitmemeli");
            h.Sessions.GetActive().Should().BeNull(
                "baskı başarılı ve kuyruk boşken yayın kapanmalı");
            h.Dialogs.Shown.Should().NotContain(
                x => x.Message.Contains("açık bırakıldı"),
                "başarılı baskı yanlış başarısızlık uyarısı üretmemeli");
        });

    // R8-02B: baskı sürerken "basmadan bitir" + yeni yayında eski etiketleri
    // taşıma kabulü, aynı etiket kimliğini YENİ bir VM nesnesi olarak kuyruğa
    // koyar. Baskı bitince kuyruktan çıkarma referansla çalıştığı için klon
    // kuyrukta kalıyor ve normal ikinci Print yazıcı adaptörünü ikinci kez
    // çağırıyordu (ciro tek kalsa da).
    [Fact]
    public async Task Basmadan_bitir_tasima_baski_bitince_kuyrukta_hayalet_klon_birakmaz()
        => await RunOnStaDispatcherAsync(async () =>
        {
            var printer = new BarrierPrinter();
            using var h = MainShellTestHarness.Build(printerOverride: printer);
            var labels = new LabelRepository(h.Db);
            MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100);
            var labelId = h.Vm.PrintQueue.Single().Id;

            var print = h.Vm.PrintCommand.ExecuteAsync(null);
            await printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            h.Dialogs.ThreeWayResult = _ => false; // Hayır: basmadan bitir
            await h.Vm.EndStreamCommand.ExecuteAsync(null);
            h.Sessions.GetActive().Should().BeNull("basmadan bitir yayını kapatır");

            h.Dialogs.ConfirmResult = _ => true;   // eski etiketleri yeni kuyruğa taşı
            h.Vm.StartStreamCommand.Execute(null);
            h.Vm.PrintQueue.Should().ContainSingle(vm => vm.Id == labelId,
                "taşıma aynı etiketi yeni VM olarak kuyruğa koyar");

            printer.Release.Set();
            await print;

            labels.GetById(labelId)!.PrintedAt.Should().NotBeNull();
            h.Vm.PrintQueue.Should().BeEmpty(
                "basılmış etiketin klonu kuyrukta hayalet olarak kalmamalı");

            // Kuyruk gerçekten temizse bu çağrı yazıcıya gitmez; hayalet
            // kalmışsa aynı etiket ikinci kez basılır.
            printer.Release.Set();
            await h.Vm.PrintCommand.ExecuteAsync(null);
            printer.Calls.Should().Be(1, "basılmış etiket ikinci kez basılmamalı");
        });

    // R8-02A'nın başarısızlık yüzü: uçuştaki baskı BAŞARISIZ bittiğinde
    // EndStream sonucu tüketmeden PrintGuardedAsync'i yeniden çağırıyor →
    // operatör istemeden örtük bir İKİNCİ baskı denemesi başlıyor. Bu test
    // bilinçli olarak düz xunit ortamında: FIFO devam sırası temizliği önce
    // çalıştırır ve örtük yeniden baskıyı deterministik gösterir (Dispatcher
    // sırasında ise aynı kod yanlış "basılamadı" yoluna girer — iki yüz de
    // aynı kökten gelir: sonuç tüketilmiyor).
    [Fact]
    public async Task YayiniBitir_uctaki_baski_basarisizsa_ortuk_ikinci_baski_denemez()
    {
        var printer = new BarrierPrinter { ThrowAfterRelease = true };
        using var h = MainShellTestHarness.Build(printerOverride: printer);
        h.Dialogs.ThreeWayResult = _ => true;
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100);

        var print = h.Vm.PrintCommand.ExecuteAsync(null);
        await printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var end = h.Vm.EndStreamCommand.ExecuteAsync(null);
        printer.Release.Set();
        await Task.WhenAll(print, end);

        printer.Calls.Should().Be(1,
            "başarısız baskı sonrası ikinci deneme operatörün açık kararı olmalı");
        h.Sessions.GetActive().Should().NotBeNull("baskı başarısızken yayın açık kalır");
        h.Dialogs.Shown.Should().Contain(x => x.Message.Contains("açık bırakıldı"));
    }
}
