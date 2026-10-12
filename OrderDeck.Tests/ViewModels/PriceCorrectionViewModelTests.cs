using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>"Fiyatı düzelt" çekmecesinin akışı (K5): fiyat grupları, özet, satır içi onaylar, uçuştaki
/// baskı reddi, arada değişen etiketler.</summary>
public sealed class PriceCorrectionViewModelTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly LabelService _labels;
    private readonly LabelRepository _labelRepo;
    private readonly List<PriceCorrectionResult> _applied = new();
    private IReadOnlySet<string>? _inFlight;
    private readonly List<IReadOnlyList<string>> _reprints = new();

    public PriceCorrectionViewModelTests()
    {
        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        new SessionRepository(_db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        var customers = new CustomerRepository(_db);
        _labelRepo = new LabelRepository(_db);
        _labels = new LabelService(_labelRepo,
            new CustomerService(customers, new SessionRepository(_db), _labelRepo, clock.Object), _db, clock.Object);
    }

    public void Dispose() => _db.Dispose();

    private Label Add(string user, decimal price)
        => _labels.Add("s1", new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, user, user, null,
            "aldım", 1000, Array.Empty<string>()), price, "A12");

    private PriceCorrectionViewModel Vm(IEnumerable<Label> labels, PriceCorrectionMode mode = PriceCorrectionMode.Printed)
        => new(_labels, payments: null, "s1", labels.Select(l => l.Id).ToList(), mode, DateTime.Now,
            inFlight: () => _inFlight,
            applied: r => _applied.Add(r),
            reprint: ids => { _reprints.Add(ids); return Task.FromResult<string?>(null); },
            cloudApiEnabled: true);

    [Fact]
    public void Price_groups_are_listed_all_checked_with_a_warning_when_mixed()
    {
        var labels = new[] { Add("@a", 250m), Add("@b", 250m), Add("@c", 300m) };

        var vm = Vm(labels);

        vm.PriceGroups.Select(g => g.Text).Should().Equal("250,00 TL × 2", "300,00 TL × 1");
        vm.PriceGroups.Should().OnlyContain(g => g.IsChecked);
        vm.HasMultiplePrices.Should().BeTrue();
    }

    [Fact]
    public void Only_checked_price_groups_are_corrected()
    {
        var labels = new[] { Add("@a", 250m), Add("@b", 250m), Add("@c", 300m) };
        var vm = Vm(labels);
        vm.PriceGroups.Single(g => g.Price == 300m).IsChecked = false;

        vm.Summary.Should().Contain("2 etiket / 2 müşteri").And.Contain(s => s.Contains("işaretsiz"));
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(labels[0].Id)!.Price.Should().Be(205m);
        _labelRepo.GetById(labels[2].Id)!.Price.Should().Be(300m, "işaretsiz fiyat grubu değişmez");
        _applied.Should().ContainSingle();
        vm.IsDone.Should().BeTrue();
    }

    [Fact]
    public void Negative_price_is_rejected_inline()
    {
        var l = Add("@a", 250m);
        var vm = Vm(new[] { l });

        vm.NewPriceText = "-5";
        vm.ConfirmCommand.Execute(null);

        vm.NewPriceError.Should().Contain("eksi");
        _labelRepo.GetById(l.Id)!.Price.Should().Be(250m);
        vm.IsDone.Should().BeFalse();
    }

    [Fact]
    public void Zero_price_needs_an_inline_second_confirmation()
    {
        var l = Add("@a", 250m);
        var vm = Vm(new[] { l });
        vm.NewPriceText = "0";

        vm.ConfirmCommand.Execute(null);
        vm.IsZeroConfirmPending.Should().BeTrue();
        vm.ConfirmButtonText.Should().Be("Evet, 0 TL yap");
        _labelRepo.GetById(l.Id)!.Price.Should().Be(250m);

        vm.ConfirmCommand.Execute(null);
        _labelRepo.GetById(l.Id)!.Price.Should().Be(0m);
    }

    [Fact]
    public void Correction_is_refused_while_a_selected_label_is_on_the_printer()
    {
        var l = Add("@a", 250m);
        var vm = Vm(new[] { l }, PriceCorrectionMode.Queue);
        _inFlight = new HashSet<string> { l.Id };

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.BlockingMessage.Should().Contain("Baskı bitince tekrar deneyin");
        _labelRepo.GetById(l.Id)!.Price.Should().Be(250m);
        _applied.Should().BeEmpty();

        _inFlight = null;
        vm.ConfirmCommand.Execute(null);
        _labelRepo.GetById(l.Id)!.Price.Should().Be(205m);
    }

    [Fact]
    public void Labels_changing_under_the_open_drawer_force_a_fresh_summary()
    {
        // Çekmece açıkken kuyruk basıldı: özet "1 kuyrukta" diyordu, artık basılmış.
        var l = Add("@a", 250m);
        var vm = Vm(new[] { l }, PriceCorrectionMode.Queue);
        vm.Summary.Should().Contain("1 kuyrukta, 0 basılmış");
        _labels.MarkPrintedAndRecord(new[] { l.Id });

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.BlockingMessage.Should().Contain("değişti");
        vm.Summary.Should().Contain("0 kuyrukta, 1 basılmış");
        _labelRepo.GetById(l.Id)!.Price.Should().Be(250m, "operatör yeni özeti görmeden yazılmaz");

        vm.ConfirmCommand.Execute(null);
        _labelRepo.GetById(l.Id)!.Price.Should().Be(205m);
    }

    [Fact]
    public async Task Printed_correction_asks_to_reprint_defaulting_to_yes()
    {
        var a = Add("@a", 250m);
        var q = Add("@b", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });
        var vm = Vm(new[] { a, q });

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.IsReprintAsked.Should().BeTrue();
        vm.ReprintQuestion.Should().Be("1 basılmış etiket düzeltildi — yeniden basılsın mı?");
        await vm.ReprintCommand.ExecuteAsync(null);
        _reprints.Should().ContainSingle().Which.Should().Equal(a.Id);
        vm.ReprintResult.Should().Contain("DÜZELTME");
    }

    [Fact]
    public void Queue_correction_asks_to_reprint_labels_printed_while_the_drawer_was_open()
    {
        // Kuyruk kipinde açıldı; çekmece açıkken satırlar basıldı (eski fiyatla). Onay onları basılmış
        // olarak düzeltir — kâğıtta eski fiyat kalır, yeniden baskı sorulmalı (K5).
        var a = Add("@a", 250m);
        var b = Add("@b", 250m);
        var vm = Vm(new[] { a, b }, PriceCorrectionMode.Queue);
        _labels.MarkPrintedAndRecord(new[] { a.Id, b.Id });
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.BlockingMessage.Should().Contain("değişti");

        vm.ConfirmCommand.Execute(null);

        vm.IsDone.Should().BeTrue();
        vm.ResultText.Should().Contain("2 basılmış");
        vm.IsReprintAsked.Should().BeTrue();
        vm.ReprintQuestion.Should().Be("2 basılmış etiket düzeltildi — yeniden basılsın mı?");
    }

    [Fact]
    public async Task Failed_reprint_keeps_the_question_with_the_error()
    {
        var a = Add("@a", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });
        var vm = new PriceCorrectionViewModel(_labels, payments: null, "s1", new[] { a.Id },
            PriceCorrectionMode.Printed, DateTime.Now, inFlight: () => null, applied: _ => { },
            reprint: _ => Task.FromResult<string?>("Başka bir baskı sürüyor; bitince tekrar dene."),
            cloudApiEnabled: true);
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        await vm.ReprintCommand.ExecuteAsync(null);

        vm.ReprintResult.Should().Contain("Başka bir baskı sürüyor");
        vm.IsReprintAsked.Should().BeTrue("'tekrar dene' düğmesi yerinde kalır");
    }

    [Fact]
    public async Task Drawer_stays_open_while_the_reprint_runs_and_its_error_outlives_it()
    {
        // Yeniden baskı sürerken çekmece kapatılabiliyordu (Kapat, çarpı, ESC): baskı hatası operatöre hiç
        // ulaşmıyordu — kâğıtlarda eski fiyat kalırdı.
        var a = Add("@a", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PriceCorrectionViewModel(_labels, payments: null, "s1", new[] { a.Id },
            PriceCorrectionMode.Printed, DateTime.Now, inFlight: () => null, applied: _ => { },
            reprint: _ => gate.Task, cloudApiEnabled: true);
        var stack = new OrderDeck.App.Services.Drawers.DrawerStack();
        var closed = stack.ShowAsync("Fiyatı Düzelt", d => { vm.AttachTo(d); return new object(); });
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        var reprinting = vm.ReprintCommand.ExecuteAsync(null);

        vm.DoneCommand.CanExecute(null).Should().BeFalse("Kapat düğmesi pasif");
        vm.DoneCommand.Execute(null);
        stack.Top!.CanClose.Should().BeFalse("başlıktaki çarpı pasif");
        stack.CloseTop().Should().BeTrue("ESC tüketilir");
        stack.IsOpen.Should().BeTrue("yeniden baskı sürerken çekmece kapanmaz");

        gate.SetResult("Yeniden basılamadı: kâğıt bitti. Yazıcıyı kontrol edip tekrar dene.");
        await reprinting;

        vm.CanClose.Should().BeTrue();
        stack.Top!.CanClose.Should().BeTrue();
        vm.OutcomeReport.Should().Contain("kâğıt bitti", "kabuk bunu Basılanlar ileti satırına yazar");
        stack.CloseTop().Should().BeTrue();
        (await closed).Should().BeFalse();
    }

    [Fact]
    public void Conflict_on_write_keeps_the_operators_unchecked_price_groups()
    {
        // 300 TL'lik (pazarlıklı) grup bilerek işaretten çıkarıldı; yazım çakışma verdi, operatör yeniden
        // onayladı: 300'lük etiketler sessizce 205'e düşüyordu.
        var labels = new[] { Add("@a", 250m), Add("@b", 250m), Add("@c", 300m) };
        var vm = Vm(labels);
        vm.PriceGroups.Single(g => g.Price == 300m).IsChecked = false;
        var conflicts = 1;
        vm.CorrectPricesSeam = (ids, price) =>
        {
            if (conflicts-- > 0) throw new PriceCorrectionConflictException("Etiketlerden biri bu arada değişti.");
            return _labels.CorrectPrices(ids, price);
        };
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.BlockingMessage.Should().Contain("bu arada değişti");

        vm.PriceGroups.Single(g => g.Price == 300m).IsChecked.Should().BeFalse();
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(labels[2].Id)!.Price.Should().Be(300m, "işaretsiz grup değişmez");
        _labelRepo.GetById(labels[0].Id)!.Price.Should().Be(205m);
    }

    [Fact]
    public void Resend_row_shows_the_date_of_a_request_from_another_day()
    {
        var at = DateTimeOffset.Now.AddDays(-3);
        var row = new ResendRowViewModel(new OrderDeck.App.Services.PriceCorrection.CorrectedRequestCandidate(
            "c1", "Ayşe", "cumulative", 250m, at, false, 205m));

        row.Text.Should().Contain(at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")), "genel istek günler önce gitmiş olabilir");
    }

    [Fact]
    public void Queue_correction_has_no_reprint_step()
    {
        var q = Add("@a", 250m);
        var vm = Vm(new[] { q }, PriceCorrectionMode.Queue);

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.IsDone.Should().BeTrue();
        vm.IsReprintAsked.Should().BeFalse();
    }

    [Fact]
    public void Shipped_and_cancelled_labels_are_summarised_as_unchanged()
    {
        var keep = Add("@a", 250m);
        var cancelled = Add("@b", 250m);
        _labels.Cancel(new[] { cancelled.Id }, CancelReasonCodes.Customer);

        var vm = Vm(new[] { keep, cancelled });

        vm.Summary.Should().Contain(s => s.Contains("1 etiket (iptal ya da kargo ücreti) düzeltmeye girmez"));
        vm.PriceGroups.Should().ContainSingle().Which.Group.Count.Should().Be(1);
    }
}
