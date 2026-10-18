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
using OrderDeck.Tests.Sync;
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
        var shipments = new ShipmentService(new ShipmentRepository(_db), _labelRepo, LocalShipping.Off());
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

    [Fact]
    public void Labels_of_the_code_in_another_pcs_file_stay_and_are_listed_as_unchanged()
    {
        // Spec 2026-10-13 §7.4: bağlı dosyası bu bilgisayarda olmayan etiket (mod Off'ta disable'dan kalan karışık
        // dosya) düzeltilmez; özet ve sonuç onu "başka bilgisayarın dosyasında" diye ayrıca söyler.
        var elsewhere = Add("@baska", 250m);
        var open = Add("@a", 250m);
        using (var c = _db.Open())
            Dapper.SqlMapper.Execute(c, "UPDATE Label SET ShipmentId = @sid WHERE Id = @id",
                new { sid = Guid.NewGuid().ToString("N"), id = elsewhere.Id });
        var vm = Vm(new[] { open });

        vm.Codes.Should().ContainSingle().Which.Count.Should().Be(1);
        vm.Summary.Should().Contain("1 etiket / 1 müşteri")
            .And.Contain("1 etiket başka bilgisayarın dosyasında, değişmeyecek");
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);

        vm.IsDone.Should().BeTrue();
        _labelRepo.GetById(elsewhere.Id)!.Price.Should().Be(250m);
        _labelRepo.GetById(open.Id)!.Price.Should().Be(205m);
        vm.ResultText.Should().Contain("1 etiket başka bilgisayarın dosyasında, değişmedi: @baska");
    }

    // ── yönlendirici (spec 2026-10-13 §4.6 set-code-price, §5.3 "Fiyatı düzelt"; W2-06) ──────────────

    private static PriceCorrectionViewModel RoutedVm(
        SalesEditTestBed bed, string sessionId, IEnumerable<string> selected, List<PriceCorrectionResult> applied)
        => new(bed.Labels, payments: null, sessionId, selected.ToList(), DateTime.Now,
            inFlight: () => null,
            applied: applied.Add,
            reprint: _ => Task.FromResult<string?>(null),
            cloudApiEnabled: true,
            editor: bed.Router);

    private static string N(Guid id) => id.ToString("N");

    private static long? Unix(SalesEditTestBed bed, string column, Guid id)
        => bed.Scalar<long?>($"SELECT {column} FROM Label WHERE Id = @id", new { id = N(id) });

    [Fact]
    public async Task Mod_Onda_devredilmis_yayinda_tek_set_code_price_gider_fiyat_ve_damgalar_yanittan_yazilir()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var session = bed.RemoteSession();
        var customer = bed.RemoteCustomer();
        var printed = bed.RemoteOrder(session, customer, 150m);
        var queued = bed.RemoteOrder(session, customer, 150m, printed: false);
        var paid = bed.RemoteOrder(session, customer, 150m);
        var shipment = Guid.NewGuid();
        bed.Server.SeedShipment(new FakeSalesServer.ShipmentBody(shipment, N(customer), "pending", 150m,
            SalesEditTestBed.T0, null, null));
        bed.Server.MutateOrder(paid, row => row with { ShipmentId = shipment });   // dekontla ödenmiş (dosyaya bağlı)
        await bed.PullAsync();
        var before = bed.Local(printed);
        var applied = new List<PriceCorrectionResult>();
        var vm = RoutedVm(bed, N(session), [N(printed)], applied);
        vm.Codes.Should().ContainSingle().Which.Count.Should().Be(3);

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        var call = bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("set-code-price");
        var payload = call.Payload!.Value;
        payload.GetProperty("sessionId").GetGuid().Should().Be(session);
        payload.GetProperty("code").GetString().Should().Be("A1");
        payload.GetProperty("alsoSelectedIds").EnumerateArray().Should().BeEmpty("seçilen etiket yayın×kod'un içinde");
        payload.GetProperty("newPrice").GetDecimal().Should().Be(200m);
        payload.GetProperty("paidFromBalanceIds").EnumerateArray().Should().BeEmpty();
        call.Expected!.Select(e => (e!.OrderId!.Value, e.Version!.Value)).Should().BeEquivalentTo(
            new[] { (printed, 1), (queued, 1), (paid, 2) }, "beklenen küme planın kendisi, yerel ServerVersion'larla");

        new[] { printed, queued, paid }.Select(id => bed.Local(id).Price).Should().OnlyContain(p => p == 200m);
        Unix(bed, "PriceCorrectedAt", printed).Should().Be(
            bed.Server.Order(printed)!.Row.PriceCorrectedAt!.Value.ToUnixTimeSeconds(), "damga sunucunun saati, yanıttan");
        Unix(bed, "PriceCorrectedAt", queued).Should().BeNull("basılmamış satırda damga yok");
        bed.Scalar<decimal?>("SELECT PaidPrice FROM Label WHERE Id = @id", new { id = N(paid) }).Should().Be(150m,
            "dosyaya bağlı (ödenmiş) satırın ödendiği fiyat yanıttan");
        bed.Scalar<decimal?>("SELECT PaidPrice FROM Label WHERE Id = @id", new { id = N(printed) }).Should().BeNull();
        bed.Local(printed).ServerVersion.Should().Be(2);
        bed.Local(printed).SyncedAt.Should().Be(before.SyncedAt, "yanıt gönderim kuyruğuna dokunmaz");
        bed.Local(printed).Revision.Should().Be(before.Revision);

        applied.Should().ContainSingle().Which.Corrected.Select(c => c.Before.Id)
            .Should().BeEquivalentTo(new[] { N(printed), N(queued), N(paid) }, "kabuk kuyruk kopyalarını bunlarla tazeler");
        vm.IsDone.Should().BeTrue();
        vm.ResultText.Should().StartWith("A1: 3 etiket 200,00 TL'ye düzeltildi (1 kuyrukta");
        vm.IsReprintAsked.Should().BeTrue("basılmış etiket düzeldi");
    }

    [Fact]
    public async Task Mod_Onda_plan_stale_plani_yeniler_mesaji_gosterir_ve_sessizce_yeniden_denemez()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var session = bed.RemoteSession();
        var customer = bed.RemoteCustomer();
        var a = bed.RemoteOrder(session, customer, 150m);
        var b = bed.RemoteOrder(session, customer, 150m);
        await bed.PullAsync();
        var applied = new List<PriceCorrectionResult>();
        var vm = RoutedVm(bed, N(session), [N(a)], applied);
        // Başka bilgisayar aynı kodun bir etiketinin fiyatını değiştirdi; yerel ayna henüz bilmiyor.
        bed.Server.MutateOrder(b, row => row with { Price = 160m });

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        bed.Server.CommandCalls.Should().ContainSingle("fiyatta sessiz yeniden deneme yok");
        vm.BlockingMessage.Should().Be("Bu arada başka bir bilgisayarda değişti; plan yenilendi.");
        vm.IsDone.Should().BeFalse();
        applied.Should().BeEmpty();
        bed.Local(b).Price.Should().Be(160m, "ret'in güncel satırı yazıldı");
        bed.Local(a).Price.Should().Be(150m);
        bed.Server.Order(a)!.Row.Price.Should().Be(150m, "plan-stale hiçbir şey yazmaz");
        vm.Codes.Should().ContainSingle().Which.Text.Should().Be("150,00 TL × 1, 160,00 TL × 1 — karışık",
            "plan yerel aynadan yeniden okundu");

        // Operatör yenilenen planı onaylar: yeni bir komut, bu kez uyuşan planla.
        await vm.ConfirmCommand.ExecuteAsync(null);

        bed.Server.CommandCalls.Should().HaveCount(2);
        vm.IsDone.Should().BeTrue();
        new[] { a, b }.Select(id => bed.Local(id).Price).Should().OnlyContain(p => p == 200m);
    }

    [Fact]
    public async Task Mod_Onda_too_many_candidates_metni_satir_icinde_ve_catisma_kaydi_yazilir()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var (session, _, order) = await bed.RemoteLabelAsync(150m);
        var vm = RoutedVm(bed, N(session), [N(order)], new List<PriceCorrectionResult>());
        bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "too-many-candidates",
            new Dictionary<string, object?> { ["count"] = 12000, ["max"] = 10000 });

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        vm.BlockingMessage.Should().Be("Bu işlem çok fazla etiketi kapsıyor (12000); destek ile iletişime geç.");
        vm.IsDone.Should().BeFalse();
        bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1);
        bed.Local(order).Price.Should().Be(150m);
        bed.Server.CommandCalls.Should().ContainSingle("kalıcıdır, yeniden denenmez");
    }

    [Fact]
    public async Task Mod_Onda_cevrimdisiyken_devredilmis_yayinin_fiyati_duzeltilmez_ve_istek_gitmez()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var (session, _, order) = await bed.RemoteLabelAsync(150m);
        var http = bed.HttpCount;
        bed.Online = false;
        var vm = RoutedVm(bed, N(session), [N(order)], new List<PriceCorrectionResult>());

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        vm.BlockingMessage.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        vm.IsDone.Should().BeFalse();
        bed.HttpCount.Should().Be(http);
        bed.Local(order).Price.Should().Be(150m);
    }

    [Fact]
    public async Task Mod_Onda_yaniti_kaybolan_duzeltme_yeniden_onayda_ayni_CommandId_ile_gider_sunucu_bir_kez_uygular()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var (session, _, order) = await bed.RemoteLabelAsync(150m);
        var vm = RoutedVm(bed, N(session), [N(order)], new List<PriceCorrectionResult>());
        bed.Server.LoseNextResponse(FakeSalesServer.Commands);

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        vm.BlockingMessage.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        vm.IsDone.Should().BeFalse();
        bed.Local(order).Price.Should().Be(150m, "yanıt gelmedi");
        bed.Server.Order(order)!.Row.Price.Should().Be(200m, "sunucu uygulamıştı");

        bed.StateReceivedAt = DateTimeOffset.UtcNow.AddSeconds(1);   // sonraki tur: bağlantı geri geldi
        await vm.ConfirmCommand.ExecuteAsync(null);

        var calls = bed.Server.CommandCalls.Select(c => c.Command).ToList();
        calls.Should().HaveCount(2);
        calls[1].CommandId.Should().Be(calls[0].CommandId, "aynı niyet aynı kimlikle");
        var log = bed.Server.CommandLog(calls[0].CommandId!.Value)!;
        log.Applications.Should().Be(1);
        log.Replays.Should().Be(1);
        vm.IsDone.Should().BeTrue();
        bed.Local(order).Price.Should().Be(200m);
    }

    [Fact]
    public async Task Mod_Onda_canli_yayin_ve_kalinti_kuyruk_karisik_secimi_yerel_yazim_ve_tek_kodsuz_komuta_bolunur()
    {
        // Kuyrukta canlı yayının A1'i ve önceki (devredilmiş) yayından kalmış bir A1 (§5.5): canlı parça bugünkü yerel
        // yazımla, kalıntı yalnız kendisiyle (kodsuz, alsoSelected) komutla — yerel planın kümesi.
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var live = bed.LocalSession(ended: false);
        var liveLabel = bed.LocalLabel(live, Guid.NewGuid().ToString("N"), 150m, printed: false);
        var (oldSession, _, leftover) = await bed.RemoteLabelAsync(150m, printed: false);
        var applied = new List<PriceCorrectionResult>();
        var vm = RoutedVm(bed, live, [liveLabel, N(leftover)], applied);
        vm.Codes.Should().ContainSingle().Which.Count.Should().Be(2);

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        var call = bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        var payload = call.Payload!.Value;
        payload.GetProperty("sessionId").GetGuid().Should().Be(oldSession);
        payload.GetProperty("code").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        payload.GetProperty("alsoSelectedIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(leftover);
        call.Expected!.Select(e => e!.OrderId!.Value).Should().Equal(leftover);
        bed.Local(liveLabel).Price.Should().Be(200m);
        bed.Local(liveLabel).SyncedAt.Should().BeNull("canlı parça bugünkü yerel yazım: gönderim kuyruğuna girer");
        bed.Local(leftover).Price.Should().Be(200m);
        bed.Local(leftover).SyncedAt.Should().NotBeNull("kalıntı komut yanıtından yazıldı");
        applied.Should().ContainSingle().Which.Corrected.Should().HaveCount(2);
        vm.IsDone.Should().BeTrue();
    }

    [Fact]
    public async Task Karisik_secimde_komut_reddedilirse_yazilan_yerel_parca_sonraki_onayin_sonucuna_katilir()
    {
        // Yerel parça yazıldı, kalıntının komutu plan-stale aldı. Operatör yenilenen planı onaylayınca canlı etiket artık
        // yeni fiyatta (yeniden düzelmez): sonuç onu da saymalı — yeniden baskı sorusu ve düzeltilmiş istek kaçmasın.
        using var bed = new SalesEditTestBed();
        bed.ModeOn();
        var live = bed.LocalSession(ended: false);
        var liveLabel = bed.LocalLabel(live, Guid.NewGuid().ToString("N"), 150m, printed: false);
        var (_, _, leftover) = await bed.RemoteLabelAsync(150m, printed: false);
        var applied = new List<PriceCorrectionResult>();
        var vm = RoutedVm(bed, live, [liveLabel, N(leftover)], applied);
        bed.Server.MutateOrder(leftover, row => row with { Price = 155m });

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        vm.BlockingMessage.Should().Be("Bu arada başka bir bilgisayarda değişti; plan yenilendi.");
        bed.Local(liveLabel).Price.Should().Be(200m, "yerel parça komuttan önce yazıldı");
        applied.Should().ContainSingle("yazılan parça kabukta tazelenir")
            .Which.Corrected.Select(c => c.Before.Id).Should().Equal(liveLabel);

        await vm.ConfirmCommand.ExecuteAsync(null);

        vm.IsDone.Should().BeTrue();
        applied.Should().HaveCount(2);
        applied[1].Corrected.Select(c => (c.Before.Id, c.Before.Price)).Should().BeEquivalentTo(
            new[] { (liveLabel, 150m), (N(leftover), 155m) });
        vm.ResultText.Should().StartWith("A1: 2 etiket 200,00 TL'ye düzeltildi");
    }

    [Fact]
    public async Task Mod_Offta_yonlendiriciyle_bugunku_yerel_duzeltme_yazilir_ve_istek_gitmez()
    {
        using var bed = new SalesEditTestBed();
        bed.ModeOff();
        var session = bed.LocalSession(ended: true, releasedAt: 950);
        var customer = Guid.NewGuid().ToString("N");
        var a = bed.LocalLabel(session, customer, 150m);
        var b = bed.LocalLabel(session, customer, 150m, printed: false);
        var applied = new List<PriceCorrectionResult>();
        var vm = RoutedVm(bed, session, [a], applied);

        vm.NewPriceText = "200";
        var confirm = vm.ConfirmCommand.ExecuteAsync(null);

        confirm.IsCompleted.Should().BeTrue("mod Off'ta beklemeden biter");
        await confirm;
        vm.IsDone.Should().BeTrue();
        new[] { a, b }.Select(id => bed.Local(id).Price).Should().OnlyContain(p => p == 200m);
        bed.Local(a).SyncedAt.Should().BeNull("bugünkü gibi gönderim kuyruğuna girer");
        bed.HttpCount.Should().Be(0);
        applied.Should().ContainSingle().Which.Corrected.Should().HaveCount(2);
    }
}
