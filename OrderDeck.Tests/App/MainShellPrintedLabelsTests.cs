using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Labeling;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// "Basılanlar" sekmesi ve fiyat düzeltmenin kabuk tarafı (sahip kararları K1–K7): basışa göre gruplar,
/// seçim, kuyruk satırlarının YERİNDE güncellenmesi (kâğıda yeni fiyat), uçuştaki baskı reddi, yeniden
/// baskı, toplu iptal, rapordan sekmeye geçiş.
/// </summary>
public class MainShellPrintedLabelsTests
{
    /// <summary>Sahte çekmece: içerik fabrikasını ÇAĞIRMAZ (WPF yok); fiyat düzeltme çekmecesinde test
    /// betiğini kabuğun <see cref="MainShellViewModel.ActivePriceCorrection"/>'ı üzerinde koşar.</summary>
    private sealed class ScriptedDrawers : IDrawerService
    {
        public MainShellViewModel? Vm { get; set; }
        public Func<PriceCorrectionViewModel, Task>? OnPriceCorrection { get; set; }

        /// <summary>Öbür çekmeceler (ör. yedek çekmecesi): operatörün çekmecede yaptığı işi taklit eder.</summary>
        public Func<string, Task>? OnOther { get; set; }
        public List<string> Titles { get; } = new();

        public async Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            Titles.Add(title);
            if (Vm?.ActivePriceCorrection is { } pc && OnPriceCorrection is not null)
            {
                await OnPriceCorrection(pc);
                return true;
            }
            if (OnOther is not null)
            {
                await OnOther(title);
                return true;
            }
            return false;
        }

        public bool CloseTop() => false;
    }

    private sealed class GatedPrinter : ILabelPrinter
    {
        public ManualResetEventSlim Release { get; } = new(false);
        public List<List<Label>> Calls { get; } = new();

        public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null,
            IReadOnlySet<string>? correctionLabelIds = null)
        {
            lock (Calls) Calls.Add(labels.ToList());
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        public void PrintGiftLabels(IReadOnlyList<Label> labels) { }
    }

    private static (MainShellTestHarness.Harness H, ScriptedDrawers Drawers) Build(ILabelPrinter? printer = null)
    {
        var drawers = new ScriptedDrawers();
        var h = MainShellTestHarness.Build(drawers, printerOverride: printer);
        drawers.Vm = h.Vm;
        return (h, drawers);
    }

    private static LabelViewModel Enqueue(MainShellTestHarness.Harness h, string user, decimal price, string code = "A12")
    {
        h.Vm.ActiveCode = code;
        MainShellTestHarness.EnqueueLabel(h.Vm, user, price);
        return h.Vm.PrintQueue[^1];
    }

    private static async Task PrintAsync(MainShellTestHarness.Harness h, params LabelViewModel[] rows)
    {
        h.Vm.SelectedQueueItems.Clear();
        foreach (var r in rows) h.Vm.SelectedQueueItems.Add(r);
        await h.Vm.PrintCommand.ExecuteAsync(null);
    }

    // ── K4: kuyrukta fiyat düzeltme ─────────────────────────────────────

    [Fact]
    public async Task Queue_correction_updates_rows_in_place_and_the_paper_gets_the_new_price()
    {
        var (h, drawers) = Build();
        using var _ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        var c = Enqueue(h, "@c", 300m);
        h.Vm.SelectedQueueItems.Add(a);
        h.Vm.SelectedQueueItems.Add(b);
        drawers.OnPriceCorrection = pc =>
        {
            pc.NewPriceText = "205";
            pc.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        };

        await h.Vm.FixQueuePriceCommand.ExecuteAsync(null);

        h.Vm.PrintQueue.Should().Equal(new[] { a, b, c }, "satırlar yerinde: sıra aynı, nesne aynı");
        h.Vm.SelectedQueueItems.Should().Equal(a, b);
        a.Price.Should().Be(205m);
        b.Price.Should().Be(205m);
        c.Price.Should().Be(300m);

        await PrintAsync(h);
        h.Printer.Calls.Single().Select(l => l.Price).Should().Equal(new[] { 205m, 205m, 300m },
            "kâğıt ekrandaki kopyadan basılır — yerinde güncellenmeseydi 250 basılırdı");
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(205m);
    }

    [Fact]
    public async Task Correction_is_refused_while_the_label_is_on_the_printer()
    {
        var printer = new GatedPrinter();
        var (h, drawers) = Build(printer);
        using var _ = h;
        var a = Enqueue(h, "@a", 250m);
        var printing = h.Vm.PrintCommand.ExecuteAsync(null);   // baskı uçuşta
        try
        {
            h.Vm.SelectedQueueItems.Add(a);
            string? blocked = null;
            drawers.OnPriceCorrection = pc =>
            {
                pc.NewPriceText = "205";
                pc.ConfirmCommand.Execute(null);
                blocked = pc.BlockingMessage;
                return Task.CompletedTask;
            };

            await h.Vm.FixQueuePriceCommand.ExecuteAsync(null);

            blocked.Should().Contain("Baskı bitince tekrar deneyin");
            new LabelRepository(h.Db).GetById(a.Id)!.Price.Should().Be(250m);
        }
        finally
        {
            printer.Release.Set();
            await printing;
        }
    }

    // ── K1/K2: Basılanlar sekmesi ───────────────────────────────────────

    [Fact]
    public async Task Printed_tab_groups_by_print_press_newest_first_in_paper_order()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m, "A12");
        var b = Enqueue(h, "@b", 100m, "B7");
        await PrintAsync(h, a);                          // 1. basış
        var c = Enqueue(h, "@c", 250m, "A12");
        h.Clock.Setup(x => x.UnixNow()).Returns(2000L);
        await PrintAsync(h, c, b);                       // 2. basış: seçim sırası c, b

        h.Vm.IsPrintedTabActive = true;

        var rows = h.Vm.PrintedLabels.VisibleRows;
        rows.Select(r => (r.Display, r.Seq)).Should().Equal(("@c", 1), ("@b", 2), ("@a", 1));
        rows[0].Batch.Should().BeSameAs(rows[1].Batch);
        rows[2].Batch.Should().NotBeSameAs(rows[0].Batch);
        rows[0].CodeText.Should().Be("A12");
        rows[0].PriceText.Should().Be("250,00 TL");
        h.Vm.PrintedLabels.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Presses_in_the_same_second_are_listed_newest_first()
    {
        // PrintedAt saniyelik; aynı saniyedeki basışların sırası rastgele kimliğe kalıyordu (K1: en yeni üstte).
        var (h, _) = Build();
        using var __ = h;
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var row = Enqueue(h, $"@k{i}", 100m);
            await PrintAsync(h, row);                     // saat sabit: hepsi aynı saniye
            ids.Add(row.Id);
        }

        h.Vm.IsPrintedTabActive = true;

        h.Vm.PrintedLabels.VisibleRows.Select(r => r.Id).Should().Equal(Enumerable.Reverse(ids));
    }

    [Fact]
    public async Task Hidden_printed_tab_is_not_rebuilt_after_each_print()
    {
        // Gruplu, sanallaştırmasız liste her Yazdır'dan sonra (sekme gizliyken de) baştan kuruluyordu:
        // yüzlerce etikette UI yarım saniyeden fazla kilitleniyordu.
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);

        await PrintAsync(h, a);

        h.Vm.PrintedLabels.TotalCount.Should().Be(1, "sekme başlığındaki sayı güncel");
        h.Vm.PrintedLabels.Rows.Should().BeEmpty("sekme kapalıyken liste kurulmaz");

        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.VisibleRows.Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task Open_printed_tab_adds_the_new_press_on_top_without_rebuilding()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        await PrintAsync(h, a, b);
        h.Vm.IsPrintedTabActive = true;
        var before = h.Vm.PrintedLabels.VisibleRows.ToList();
        var removals = 0;
        h.Vm.PrintedLabels.VisibleRows.CollectionChanged += (_, e) =>
        {
            if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                or System.Collections.Specialized.NotifyCollectionChangedAction.Reset) removals++;
        };
        var c = Enqueue(h, "@c", 250m);
        h.Clock.Setup(x => x.UnixNow()).Returns(2000L);

        await PrintAsync(h, c);

        var rows = h.Vm.PrintedLabels.VisibleRows;
        rows.Select(r => r.Id).Should().Equal(c.Id, a.Id, b.Id);
        rows[1].Should().BeSameAs(before[0], "değişmeyen satır yerinde kalır");
        rows[2].Should().BeSameAs(before[1]);
        removals.Should().Be(0, "liste silinip baştan kurulmaz");
    }

    [Fact]
    public async Task Select_batch_and_select_code_across_batches()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m, "A12");
        var b = Enqueue(h, "@b", 100m, "B7");
        await PrintAsync(h, a);
        var c = Enqueue(h, "@c", 250m, "A12");
        h.Clock.Setup(x => x.UnixNow()).Returns(2000L);
        await PrintAsync(h, c, b);
        h.Vm.IsPrintedTabActive = true;
        var p = h.Vm.PrintedLabels;

        p.SelectBatchCommand.Execute(p.VisibleRows[0].Batch);
        p.SelectedIds.Should().Equal(c.Id, b.Id);

        p.ClearSelectionCommand.Execute(null);
        p.SelectCodeCommand.Execute(p.VisibleRows.Single(r => r.Id == c.Id));
        p.SelectedIds.Should().BeEquivalentTo(new[] { c.Id, a.Id }, "aynı kod, gruplar arası");
        p.SearchText.Should().Be("A12");
        p.VisibleRows.Select(r => r.Id).Should().BeEquivalentTo(new[] { c.Id, a.Id });

        p.SearchText = "@a";
        p.SelectedIds.Should().Equal(new[] { a.Id }, "görünmeyen satır seçimden düşer");
    }

    [Fact]
    public async Task Select_code_replaces_the_selection_even_when_the_search_matches_longer_codes()
    {
        // "A1" araması A12 satırlarını da gösterir (alt dize); önceden seçili A12'ler seçili kalıyordu —
        // "yalnız A1" sanılan düzeltme onlara da uygulanırdı.
        var (h, _) = Build();
        using var __ = h;
        var a12 = Enqueue(h, "@a", 250m, "A12");
        var a1 = Enqueue(h, "@b", 100m, "A1");
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        var p = h.Vm.PrintedLabels;
        p.SelectCodeCommand.Execute(p.VisibleRows.Single(r => r.Id == a12.Id));
        p.SelectedIds.Should().Equal(a12.Id);

        p.SelectCodeCommand.Execute(p.Rows.Single(r => r.Id == a1.Id));   // ör. sağ tık menüsünden

        p.SearchText.Should().Be("A1");
        p.VisibleRows.Select(r => r.Id).Should().Contain(a12.Id, "alt dize araması A12'yi de gösterir");
        p.SelectedIds.Should().Equal(new[] { a1.Id }, "seçim kodun satırlarıyla değişir, eklenmez");
    }

    [Fact]
    public async Task Toolbar_select_code_with_one_code_narrows_the_search_to_it()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m, "A12");
        var b = Enqueue(h, "@b", 100m, "B7");
        await PrintAsync(h, a, b);
        var c = Enqueue(h, "@c", 250m, "A12");
        h.Clock.Setup(x => x.UnixNow()).Returns(2000L);
        await PrintAsync(h, c);
        h.Vm.IsPrintedTabActive = true;
        var p = h.Vm.PrintedLabels;
        p.VisibleRows.Single(r => r.Id == c.Id).IsSelected = true;

        p.SelectCodeOfSelectionCommand.Execute(null);

        p.SearchText.Should().Be("A12");
        p.SelectedIds.Should().BeEquivalentTo(new[] { c.Id, a.Id }, "aynı kod, basışlar arası");
    }

    [Fact]
    public void Queue_fix_price_button_is_hidden_on_the_printed_tab()
    {
        var (h, _) = Build();
        using var __ = h;
        h.Vm.SelectedQueueItems.Add(Enqueue(h, "@a", 250m));
        h.Vm.ShowQueueFixPrice.Should().BeTrue();

        h.Vm.IsPrintedTabActive = true;

        h.Vm.ShowQueueFixPrice.Should().BeFalse("Basılanlar'da kuyruğun seçimi görünmüyor");
    }

    [Fact]
    public async Task Status_badges_show_backup()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.BeginAddBackupCommand.Execute(a);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de"));
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;

        h.Vm.PrintedLabels.VisibleRows.Single(r => r.Display == "@yedek").StatusText.Should().Be("yedek");
    }

    // ── K5: basılmış etiketlerde düzeltme + yeniden baskı ───────────────

    [Fact]
    public async Task Printed_correction_reprints_with_correction_mark_without_counting_revenue_twice()
    {
        var (h, drawers) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        await PrintAsync(h);
        var printedAt = new LabelRepository(h.Db).GetById(a.Id)!.PrintedAt;
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.SelectBatchCommand.Execute(h.Vm.PrintedLabels.VisibleRows[0].Batch);
        drawers.OnPriceCorrection = async pc =>
        {
            pc.NewPriceText = "205";
            pc.ConfirmCommand.Execute(null);
            pc.IsReprintAsked.Should().BeTrue();
            await pc.ReprintCommand.ExecuteAsync(null);
        };

        await h.Vm.FixPrintedPriceCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().HaveCount(2);
        h.Printer.Calls[1].Select(l => l.Price).Should().OnlyContain(p => p == 205m);
        h.Printer.CorrectionCalls[1].Should().BeEquivalentTo(new[] { a.Id, b.Id }, "kâğıtta DÜZELTME işareti");
        new LabelRepository(h.Db).GetSessionTotals(h.Sessions.GetActive()!.Id).TotalAmount
            .Should().Be(410m, "yeniden baskı ciroyu ikinci kez artırmaz");
        new LabelRepository(h.Db).GetById(a.Id)!.PrintedAt.Should().Be(printedAt, "ilk PrintedAt bozulmaz");
        h.Vm.PrintedLabels.VisibleRows.Should().OnlyContain(r => r.IsCorrected && r.Label.Price == 205m);
    }

    [Fact]
    public async Task Correction_reprint_outcome_stays_on_the_printed_tab_after_the_drawer_closes()
    {
        // Yeniden baskının sonucu yalnız çekmecedeydi: çekmece kapanınca (baskı hatası dahil) kayboluyordu.
        var (h, drawers) = Build();
        using var __ = h;
        Enqueue(h, "@a", 250m);
        Enqueue(h, "@b", 250m);
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.SelectBatchCommand.Execute(h.Vm.PrintedLabels.VisibleRows[0].Batch);
        drawers.OnPriceCorrection = async pc =>
        {
            pc.NewPriceText = "205";
            pc.ConfirmCommand.Execute(null);
            await pc.ReprintCommand.ExecuteAsync(null);
        };

        await h.Vm.FixPrintedPriceCommand.ExecuteAsync(null);

        h.Vm.PrintedLabels.Message.Should().Contain("2 etiket \"DÜZELTME\" işaretiyle yeniden basıldı");
    }

    [Fact]
    public async Task Queue_corrected_label_reprint_has_no_correction_mark()
    {
        // Kuyrukta düzeltilen etiketin ilk kâğıdı zaten yeni fiyatla çıktı; yeniden baskısı yine "DÜZELTME"
        // alıyordu — paketleyen ortada olmayan eski fiyatlı kâğıdı arıyordu.
        var (h, drawers) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.SelectedQueueItems.Add(a);
        drawers.OnPriceCorrection = pc =>
        {
            pc.NewPriceText = "205";
            pc.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        };
        await h.Vm.FixQueuePriceCommand.ExecuteAsync(null);
        await PrintAsync(h, a);
        h.Printer.Calls[0].Single().Price.Should().Be(205m);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.VisibleRows.Single().IsCorrected.Should().BeFalse();
        h.Vm.PrintedLabels.VisibleRows[0].IsSelected = true;

        await h.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().HaveCount(2);
        h.Printer.CorrectionCalls[1].Should().BeEmpty("hiç yanlış fiyatlı kâğıt çıkmadı");
    }

    [Fact]
    public async Task Reprint_message_counts_the_labels_actually_printed()
    {
        // Seçimdeki bir etiket başka yoldan iptal edildi (liste henüz bayat): basılmaz ama ileti seçili sayıyı
        // yazıyordu — "2 etiket yeniden basıldı", çıkan kâğıt 1.
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        foreach (var r in h.Vm.PrintedLabels.VisibleRows) r.IsSelected = true;
        h.Labels.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);

        await h.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        h.Printer.Calls[^1].Select(l => l.Id).Should().Equal(b.Id);
        h.Vm.PrintedLabels.Message.Should().Be("1 etiket yeniden basıldı.");
    }

    [Fact]
    public async Task Printed_tab_is_refreshed_after_customer_detail_opened_from_chat()
    {
        // Sohbetten açılan müşteri detayında bir etiket iptal edildi. Basılanlar yalnız kendi satırından
        // açılan detaydan sonra tazeleniyordu: iptalli satır listede, sayı yanlış kalıyordu.
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.CustomerDetailSeam = _ =>
        {
            h.Labels.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);   // operatör detayda iptal etti
            return Task.CompletedTask;
        };

        await h.Vm.OpenCustomerDetailFromChatCommand.ExecuteAsync(MainShellTestHarness.ChatVm("@a", "merhaba"));

        h.Vm.PrintedLabels.VisibleRows.Select(r => r.Id).Should().Equal(b.Id);
        h.Vm.PrintedLabels.TotalCount.Should().Be(1);
    }

    private sealed class SecondCallGatedPrinter : ILabelPrinter
    {
        private int _calls;
        public List<List<Label>> Calls { get; } = new();
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null,
            IReadOnlySet<string>? correctionLabelIds = null)
        {
            lock (Calls) Calls.Add(labels.ToList());
            if (Interlocked.Increment(ref _calls) != 2) return;   // 2. çağrı: yeniden baskı, yazıcıda bekler
            SecondEntered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        public void PrintGiftLabels(IReadOnlyList<Label> labels) { }
    }

    [Fact]
    public async Task Print_is_disabled_while_a_reprint_holds_the_printer()
    {
        // Yeniden baskı yazıcı kapısını tutarken "Yazdır" etkin görünüyor, tıklama sessizce hiçbir şey
        // yapmıyordu: yeni etiketler kuyrukta kalıyor, uyarı çıkmıyordu.
        var printer = new SecondCallGatedPrinter();
        var (h, _) = Build(printer);
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        await PrintAsync(h, a);
        Enqueue(h, "@b", 100m);
        h.Vm.PrintCommand.CanExecute(null).Should().BeTrue();
        var reprint = h.Vm.ReprintAsync(new[] { a.Id });
        try
        {
            await printer.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Vm.PrintCommand.CanExecute(null).Should().BeFalse("yazıcı yeniden baskıda");
        }
        finally
        {
            printer.Release.Set();
            (await reprint).Should().BeNull();
        }
        h.Vm.PrintCommand.CanExecute(null).Should().BeTrue("kapı açıldı");
    }

    [Fact]
    public async Task Bulk_cancel_warns_when_a_label_is_in_the_running_reprint()
    {
        // Kuyruk iptali uçuştaki baskıyla kesişimi söylüyor (R7-04 Soru B); Basılanlar'daki toplu iptal
        // yeniden baskı kümesine hiç bakmıyordu — "DÜZELTME" işaretli kâğıtlar çıkıyor, imha edilmeleri
        // söylenmiyordu.
        var printer = new SecondCallGatedPrinter();
        var (h, _) = Build(printer);
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        var reprint = h.Vm.ReprintAsync(new[] { a.Id, b.Id });
        try
        {
            await printer.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            h.Vm.CancelLabels(new[] { a.Id }, CancelReasonCodes.WrongProduct);

            h.Vm.PrintedLabels.Message.Should().StartWith("1 etiket iptal edildi.")
                .And.Contain("1 tanesi devam eden baskı işinde").And.Contain("imha et");
        }
        finally
        {
            printer.Release.Set();
            await reprint;
        }
    }

    [Fact]
    public async Task Plain_reprint_has_no_correction_mark()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.VisibleRows[0].IsSelected = true;

        await h.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().HaveCount(2);
        h.Printer.Calls[1].Single().Id.Should().Be(a.Id);
        h.Printer.CorrectionCalls[1].Should().BeEmpty();
        h.Vm.PrintedLabels.Message.Should().Be("1 etiket yeniden basıldı.");
    }

    // ── K3: toplu iptal ────────────────────────────────────────────────

    [Fact]
    public async Task Bulk_cancel_reverses_totals_and_collects_backups_for_one_drawer()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        h.Vm.BeginAddBackupCommand.Execute(a);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de"));
        await PrintAsync(h);
        h.Vm.IsPrintedTabActive = true;

        var withBackups = h.Vm.CancelLabels(new[] { a.Id, b.Id }, CancelReasonCodes.WrongProduct);

        withBackups.Should().ContainSingle().Which.Parent.Id.Should().Be(a.Id);
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(0m);
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@b")!.TotalAmount.Should().Be(0m);
        h.Vm.PrintedLabels.VisibleRows.Select(r => r.Display).Should().Equal("@yedek");
        new LabelRepository(h.Db).GetById(a.Id)!.CancelReason.Should().Be(CancelReasonCodes.WrongProduct);
    }

    [Fact]
    public async Task Bulk_cancel_lists_backups_of_several_labels_in_one_drawer()
    {
        // K3: "25 ayrı pencere değil" — iki ana etiketin yedekleri TEK yedek çekmecesinde.
        var (h, drawers) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        var b = Enqueue(h, "@b", 250m);
        h.Vm.BeginAddBackupCommand.Execute(a);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@y1", "ben de"));
        h.Vm.BeginAddBackupCommand.Execute(b);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@y2", "ben de"));
        await PrintAsync(h);
        drawers.OnOther = _ => Task.CompletedTask;

        await h.Vm.CancelAndTransferBackupsAsync(new[] { a.Id, b.Id }, CancelReasonCodes.WrongProduct);

        drawers.Titles.Should().Equal("İptal edilen etiketlerin yedekleri (2)");
    }

    [Fact]
    public async Task Backup_confirmed_in_the_open_drawer_prints_at_the_new_price()
    {
        // Yedek çekmecesi açıkken (alttaki yazdır yuvası açık) yedek 200'e onaylandı ve "Yazdır"a basıldı:
        // kuyruk kopyası ancak çekmece kapanınca güncelleniyordu — kâğıda 250 basılıp ciroya 200 yazılıyordu.
        var (h, drawers) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.BeginAddBackupCommand.Execute(a);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de"));
        var backup = h.Vm.PrintQueue[^1];
        await PrintAsync(h, a);
        drawers.OnOther = async _ =>
        {
            // Çekmecedeki kartın "Onayla"sı (BackupTransferDrawer.OnPromote ile aynı çağrı).
            var card = new OrderDeck.App.Views.Drawers.BackupRowViewModel(new LabelRepository(h.Db).GetById(backup.Id)!)
            {
                PriceText = "200",
            };
            card.Promote((id, price) => h.Labels.ConfirmBackup(id, newPrice: price), h.Vm.OnBackupConfirmed);
            await PrintAsync(h);   // çekmece hâlâ açık
        };

        await h.Vm.CancelAndTransferBackupsAsync(new[] { a.Id }, CancelReasonCodes.Customer);

        h.Printer.Calls[^1].Single().Price.Should().Be(200m, "kâğıt ekrandaki kopyadan basılır");
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@yedek")!.TotalAmount.Should().Be(200m);
    }

    [Fact]
    public async Task Backup_confirmed_at_a_new_price_updates_its_queue_row()
    {
        // Ana etiket basıldı, yedeği kuyrukta (basılmamış). Toplu iptalin yedek çekmecesinde yedek 200'e
        // onaylandı: kuyruk kopyası 250'de kalıyor, kâğıda 250 basılıp ciroya 200 yazılıyordu.
        var (h, drawers) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.BeginAddBackupCommand.Execute(a);
        h.Vm.TryAssignChatAsBackup(MainShellTestHarness.ChatVm("@yedek", "ben de"));
        var backup = h.Vm.PrintQueue[^1];
        await PrintAsync(h, a);
        drawers.OnOther = _ =>
        {
            h.Labels.ConfirmBackup(backup.Id, newPrice: 200m);
            return Task.CompletedTask;
        };

        await h.Vm.CancelAndTransferBackupsAsync(new[] { a.Id }, CancelReasonCodes.Customer);

        h.Vm.PrintQueue.Should().ContainSingle().Which.Should().BeSameAs(backup, "satır yerinde güncellenir");
        backup.Price.Should().Be(200m);
        backup.Label.IsTentativeBackup.Should().BeFalse();

        await PrintAsync(h);
        h.Printer.Calls[^1].Single().Price.Should().Be(200m, "kâğıt ekrandaki kopyadan basılır");
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@yedek")!.TotalAmount.Should().Be(200m);
    }

    private sealed class ReprintFailsPrinter : ILabelPrinter
    {
        private int _calls;
        public List<List<Label>> Calls { get; } = new();
        public TaskCompletionSource ReprintEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null,
            IReadOnlySet<string>? correctionLabelIds = null)
        {
            lock (Calls) Calls.Add(labels.ToList());
            if (Interlocked.Increment(ref _calls) != 2) return;   // 2. çağrı: yeniden baskı
            ReprintEntered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("yazıcı arızası (test)");
        }

        public void PrintGiftLabels(IReadOnlyList<Label> labels) { }
    }

    [Fact]
    public async Task End_stream_waits_for_an_in_flight_reprint_and_still_prints_the_queue()
    {
        // Yeniden baskı uçuştayken "Yayını Bitir → hepsini bas": yeniden baskının sonucu kuyruğun sonucu
        // sayılıyordu — hata verince hiç denenmemiş kuyruk için "basılamadı; yayın açık bırakıldı".
        var printer = new ReprintFailsPrinter();
        var (h, _) = Build(printer);
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        await PrintAsync(h, a);
        var b = Enqueue(h, "@b", 100m);
        var reprint = h.Vm.ReprintAsync(new[] { a.Id });
        await printer.ReprintEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Dialogs.ThreeWayResult = _ => true;   // Evet: hepsini bas ve bitir

        var end = h.Vm.EndStreamCommand.ExecuteAsync(null);
        printer.Release.Set();

        (await reprint).Should().Contain("Yeniden basılamadı");
        await end;
        printer.Calls.Should().HaveCount(3);
        printer.Calls[2].Single().Id.Should().Be(b.Id, "kuyruk yeniden baskıdan sonra basıldı");
        h.Sessions.GetActive().Should().BeNull("kuyruk basıldı, yayın kapandı");
        h.Dialogs.Shown.Should().NotContain(x => x.Message.Contains("açık bırakıldı"));
    }

    [Fact]
    public void Delete_button_does_not_cancel_hidden_queue_selection_on_printed_tab()
    {
        // Alttaki yuvadaki "Seçilenleri Sil" Basılanlar açıkken de etkindi: görünmeyen kuyruk seçimini
        // sessizce iptal ediyordu (uçuşta etiket yoksa onay da sormadan).
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.SelectedQueueItems.Add(a);
        h.Vm.IsPrintedTabActive = true;

        h.Vm.RemoveSelectedFromQueueCommand.CanExecute(null).Should().BeFalse("düğme Basılanlar'da devre dışı");
        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.PrintQueue.Should().ContainSingle();
        new LabelRepository(h.Db).GetById(a.Id)!.CancelledAt.Should().BeNull();
        h.Vm.IsPrintedTabActive = false;
        h.Vm.RemoveSelectedFromQueueCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_shortcut_does_not_cancel_hidden_queue_selection_on_printed_tab()
    {
        var (h, _) = Build();
        using var __ = h;
        var a = Enqueue(h, "@a", 250m);
        h.Vm.SelectedQueueItems.Add(a);
        h.Vm.IsPrintedTabActive = true;

        h.Vm.DeleteSelectedFromQueueViaShortcutCommand.Execute(null);

        h.Vm.PrintQueue.Should().ContainSingle();
        await Task.CompletedTask;
    }

    // ── K6: rapordan "Basılan etiketler" ────────────────────────────────

    private sealed class FakePages : IPageService
    {
        public int Open { get; set; } = 1;
        public int BackCalls { get; private set; }
        public Task ShowAsync(string key, string title, Func<Page, object> buildContent) => Task.CompletedTask;
        public string? CurrentKey => Open > 0 ? "stream-report" : null;

        public bool Back()
        {
            BackCalls++;
            if (Open == 0) return false;
            Open--;
            return true;
        }
    }

    [Fact]
    public void Report_request_closes_pages_and_opens_the_tab_on_that_session()
    {
        var navigator = new PrintedLabelsNavigator();
        var pages = new FakePages { Open = 2 };
        using var h = MainShellTestHarness.Build(printedNavigator: navigator, pages: pages);
        var ended = Guid.NewGuid().ToString("N");
        new SessionRepository(h.Db).Insert(new StreamSession(ended, "Eski", 500, 900, new[] { "instagram" }, null));
        var old = h.Labels.Add(ended, new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, "@eski",
            "@eski", null, "aldım", 500, Array.Empty<string>()), 150m, "Z1");
        h.Labels.MarkPrintedAndRecord(new[] { old.Id });

        navigator.Request(new PrintedLabelsRequest(ended, "11.10.2026 yayını", DateTime.Now));

        pages.Open.Should().Be(0, "rapor sayfası kapanır — çekmeceler sayfanın arkasında kalırdı");
        h.Vm.IsPrintedTabActive.Should().BeTrue();
        h.Vm.PrintedLabels.SessionId.Should().Be(ended);
        h.Vm.PrintedLabels.IsOtherSession.Should().BeTrue();
        h.Vm.PrintedLabels.VisibleRows.Should().ContainSingle().Which.Id.Should().Be(old.Id);

        h.Vm.ShowActiveSessionInPrintedTabCommand.Execute(null);
        h.Vm.PrintedLabels.SessionId.Should().Be(h.Sessions.GetActive()!.Id);
        h.Vm.PrintedLabels.IsOtherSession.Should().BeFalse();
    }
}
