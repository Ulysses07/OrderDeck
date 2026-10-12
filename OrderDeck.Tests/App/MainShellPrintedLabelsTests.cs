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
        public List<string> Titles { get; } = new();

        public async Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            Titles.Add(title);
            if (Vm?.ActivePriceCorrection is { } pc && OnPriceCorrection is not null)
            {
                await OnPriceCorrection(pc);
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
