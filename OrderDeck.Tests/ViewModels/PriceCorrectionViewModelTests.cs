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

/// <summary>"Fiyatı düzelt" çekmecesinin akışı (K5, F3): seçimin KODLARI (kodun yayındaki bütün etiketleri),
/// özet, satır içi onaylar, uçuştaki baskı reddi, arada değişen etiketler.</summary>
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

    private Label Add(string user, decimal price, string? code = "A12")
        => _labels.Add("s1", new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, user, user, null,
            "aldım", 1000, Array.Empty<string>()), price, code);

    private PriceCorrectionViewModel Vm(IEnumerable<Label> labels)
        => new(_labels, payments: null, "s1", labels.Select(l => l.Id).ToList(), DateTime.Now,
            inFlight: () => _inFlight,
            applied: r => _applied.Add(r),
            reprint: ids => { _reprints.Add(ids); return Task.FromResult<string?>(null); },
            cloudApiEnabled: true);

    [Fact]
    public void Correction_covers_every_label_of_the_selected_code_not_only_the_selection()
    {
        // Sabit fiyat modeli (F3): A12'nin fiyatı 250 → 205 ise A12'nin yayındaki BÜTÜN etiketleri 205 olur —
        // yalnız seçilen satır değil. Başka kod değişmez.
        var a = Add("@a", 250m);
        var b = Add("@b", 250m);
        var c = Add("@c", 250m);
        var other = Add("@d", 300m, "B7");

        var vm = Vm(new[] { a });

        var option = vm.Codes.Should().ContainSingle().Subject;
        option.Title.Should().Be("A12");
        option.Text.Should().Be("250,00 TL × 3");
        option.IsChecked.Should().BeTrue("tek kod işaretli gelir");
        vm.Summary.Should().Contain("3 etiket / 3 müşteri")
            .And.Contain("2 etiket seçimde değil ama aynı kodda — onlar da düzelir");
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        new[] { a, b, c }.Select(l => _labelRepo.GetById(l.Id)!.Price).Should().OnlyContain(p => p == 205m);
        _labelRepo.GetById(other.Id)!.Price.Should().Be(300m, "başka kod değişmez");
        _applied.Should().ContainSingle();
        vm.IsDone.Should().BeTrue();
        vm.ResultText.Should().StartWith("A12: 3 etiket 205,00 TL'ye düzeltildi");
    }

    [Fact]
    public void Mixed_prices_in_a_code_all_go_to_the_new_price_and_the_summary_says_so()
    {
        // Fiyat grubu işaretleme (kısmi düzeltme) kaldırıldı: kodda karışık fiyat varsa (eski veri, pazarlıklı
        // yedek) hepsi yeni fiyata geçer.
        var labels = new[] { Add("@a", 250m), Add("@b", 250m), Add("@c", 300m) };

        var vm = Vm(labels.Take(1));

        vm.Codes.Should().ContainSingle().Which.Text.Should().Be("250,00 TL × 2, 300,00 TL × 1 — karışık");
        vm.Summary.Should().Contain("A12: karışık fiyat (250,00 TL × 2, 300,00 TL × 1) — hepsi yeni fiyata geçer");
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        labels.Select(l => _labelRepo.GetById(l.Id)!.Price).Should().OnlyContain(p => p == 205m);
    }

    [Fact]
    public void Several_codes_are_listed_unchecked_and_only_the_checked_code_changes()
    {
        // Bir baskıda farklı ürünler olur: yeni fiyat hepsine yanlışlıkla uygulanmasın — operatör kodu seçer.
        var a = Add("@a", 250m);
        var a2 = Add("@a2", 250m);
        var b = Add("@b", 80m, "B7");
        var vm = Vm(new[] { b, a });

        vm.Codes.Select(o => (o.Title, o.Text, o.IsChecked)).Should().Equal(
            ("B7", "80,00 TL × 1", false), ("A12", "250,00 TL × 2", false));
        vm.HasMultipleCodes.Should().BeTrue();
        vm.Summary.Should().Contain("Düzelteceğin kodu işaretle.");
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.BlockingMessage.Should().Contain("en az bir kodu işaretle");
        _labelRepo.GetById(a.Id)!.Price.Should().Be(250m);

        vm.Codes.Single(o => o.Title == "A12").IsChecked = true;
        vm.Summary.Should().Contain("2 etiket / 2 müşteri").And.Contain("1 kod işaretsiz, değişmeyecek");
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(a.Id)!.Price.Should().Be(205m);
        _labelRepo.GetById(a2.Id)!.Price.Should().Be(205m);
        _labelRepo.GetById(b.Id)!.Price.Should().Be(80m, "işaretsiz kod değişmez");
    }

    [Fact]
    public void Codeless_selected_labels_are_corrected_alone()
    {
        // Kodsuz etikette genişletilecek kod yok: yalnız seçilenler (yoksa kodsuz yazılmış her satır düzelirdi).
        var x = Add("@x", 40m, null);
        var y = Add("@y", 40m, null);
        var vm = Vm(new[] { x });

        vm.Codes.Should().ContainSingle().Which.Title.Should().Be("Kodsuz (yalnız seçilenler)");
        vm.NewPriceText = "45";
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(x.Id)!.Price.Should().Be(45m);
        _labelRepo.GetById(y.Id)!.Price.Should().Be(40m);
    }

    [Fact]
    public void Label_written_for_the_code_while_the_drawer_is_open_is_corrected_without_a_new_confirmation()
    {
        // Yayında yorumlar yağarken kodun yeni etiketleri yazılmaya devam eder (kutu henüz eski fiyatı gösteriyor).
        // Onlar da kodun etiketi: düzeltmeye girer; gösterilen etiketler değişmediği için yeniden onay istenmez —
        // istenseydi her yeni yorumda onay bayatlar, düzeltme hiç tutmazdı.
        var a = Add("@a", 250m);
        var vm = Vm(new[] { a });
        var late = Add("@gec", 250m);

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.BlockingMessage.Should().BeNull();
        vm.IsDone.Should().BeTrue();
        _labelRepo.GetById(late.Id)!.Price.Should().Be(205m);
    }

    [Fact]
    public void Label_of_the_code_on_the_printer_refuses_the_correction_even_if_not_selected()
    {
        var a = Add("@a", 250m);
        var b = Add("@b", 250m);
        var vm = Vm(new[] { a });
        _inFlight = new HashSet<string> { b.Id };

        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.BlockingMessage.Should().Contain("Baskı bitince tekrar deneyin");
        _labelRepo.GetById(a.Id)!.Price.Should().Be(250m);
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
        var vm = Vm(new[] { l });
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
        var vm = Vm(new[] { l });
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
        var vm = Vm(new[] { a, b });
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
            DateTime.Now, inFlight: () => null, applied: _ => { },
            reprint: _ => Task.FromResult<string?>("Başka bir baskı sürüyor; bitince tekrar dene."),
            cloudApiEnabled: true);
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        await vm.ReprintCommand.ExecuteAsync(null);

        vm.ReprintResult.Should().Contain("Başka bir baskı sürüyor");
        vm.IsReprintAsked.Should().BeTrue("'tekrar dene' düğmesi yerinde kalır");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Yeniden basılamadı: kâğıt bitti. Yazıcıyı kontrol edip tekrar dene.", true)]
    public async Task Reprint_report_says_whether_it_leaves_work_for_the_operator(string? error, bool leavesWork)
    {
        // Kalıcı sonuç şeridi sınırda yalnız bilgi kayıtlarını düşürür: basılamayan etiketlerin kâğıdında eski fiyat
        // kaldı — operatöre iş bırakır, kaydı düşmemeli.
        var a = Add("@a", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });
        var vm = new PriceCorrectionViewModel(_labels, payments: null, "s1", new[] { a.Id },
            DateTime.Now, inFlight: () => null, applied: _ => { },
            reprint: _ => Task.FromResult(error), cloudApiEnabled: true);
        var flags = new List<bool>();
        vm.WorkFinished = (_, needsAction) => flags.Add(needsAction);
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        await vm.ReprintCommand.ExecuteAsync(null);

        flags.Should().Equal(leavesWork);
    }

    [Fact]
    public async Task Esc_closes_the_drawer_while_the_reprint_runs_and_the_error_is_reported_when_it_ends()
    {
        // Çekmece yeniden baskı sürerken kilitleniyordu (Kapat ve çarpı pasif, ESC etkisiz): askıda kalan yazıcıda
        // kod/fiyat kutusu ve kuyruk yayının geri kalanında örtülü kalırdı. Kapanır; baskı arka planda sürer,
        // sonucu (hata dahil) bitince kabuğa bildirilir.
        var a = Add("@a", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PriceCorrectionViewModel(_labels, payments: null, "s1", new[] { a.Id },
            DateTime.Now, inFlight: () => null, applied: _ => { },
            reprint: _ => gate.Task, cloudApiEnabled: true);
        var reports = new List<string>();
        vm.WorkFinished = (report, _) => reports.Add(report);
        var stack = new OrderDeck.App.Services.Drawers.DrawerStack();
        var closed = stack.ShowAsync("Fiyatı Düzelt", d => { vm.Close = () => d.Close(true); return new object(); });
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        var reprinting = vm.ReprintCommand.ExecuteAsync(null);

        stack.CloseTop().Should().BeTrue();

        stack.IsOpen.Should().BeFalse("ESC yeniden baskı sürerken de kapatır");
        (await closed).Should().BeFalse();
        reports.Should().BeEmpty("baskı henüz bitmedi");

        gate.SetResult("Yeniden basılamadı: kâğıt bitti. Yazıcıyı kontrol edip tekrar dene.");
        await reprinting;

        reports.Should().ContainSingle().Which.Should().Contain("kâğıt bitti");
        vm.ReprintResult.Should().Contain("kâğıt bitti", "çekmece açık olsaydı orada da görünürdü");
    }

    [Fact]
    public void Conflict_on_write_keeps_the_operators_code_choice()
    {
        // B7 bilerek işaretsiz bırakıldı; yazım çakışma verdi, operatör yeniden onayladı: işaret korunmalı.
        var a = Add("@a", 250m);
        var b = Add("@b", 80m, "B7");
        var vm = Vm(new[] { a, b });
        vm.Codes.Single(o => o.Title == "A12").IsChecked = true;
        var conflicts = 1;
        vm.CorrectPricesSeam = (ids, price) =>
        {
            if (conflicts-- > 0) throw new PriceCorrectionConflictException("Etiketlerden biri bu arada değişti.");
            return _labels.CorrectPrices(ids, price);
        };
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.BlockingMessage.Should().Contain("bu arada değişti");

        vm.Codes.Single(o => o.Title == "B7").IsChecked.Should().BeFalse();
        vm.Codes.Single(o => o.Title == "A12").IsChecked.Should().BeTrue();
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(b.Id)!.Price.Should().Be(80m, "işaretsiz kod değişmez");
        _labelRepo.GetById(a.Id)!.Price.Should().Be(205m);
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
        var vm = Vm(new[] { q });

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
        vm.Codes.Should().ContainSingle().Which.Count.Should().Be(1);
    }

    [Fact]
    public void Labels_of_the_code_in_a_shipped_file_stay_and_are_listed_as_unchanged()
    {
        var shipped = Add("@gitti", 250m);
        var open = Add("@a", 250m);
        _labels.MarkPrintedAndRecord(new[] { shipped.Id, open.Id });
        var shipments = new ShipmentService(new ShipmentRepository(_db), _labelRepo,
            () => new OrderDeck.Core.Settings.AppSettings());
        var file = shipments.GetOrCreateOpenShipment(shipped.CustomerId);
        shipments.AttachLabels(file.Id, new[] { shipped.Id });
        shipments.ApplyDecision(file.Id, ShipmentDecision.ShipNow);
        var vm = Vm(new[] { open });

        vm.Summary.Should().Contain("1 etiket / 1 müşteri").And.Contain("1 etiket kargoya verildi, değişmeyecek");
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        _labelRepo.GetById(shipped.Id)!.Price.Should().Be(250m);
        _labelRepo.GetById(open.Id)!.Price.Should().Be(205m);
        vm.ResultText.Should().Contain("1 etiket kargoya verildi, değişmedi: @gitti");
    }
}
