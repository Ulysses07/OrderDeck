using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// §5.6 (W1): kuyruk baskısı kâğıdı ekrandaki kopyadan değil, baskıdan hemen önce veritabanından yeniden
/// okunan satırlardan basar. Kopya bayat olabilir: satır VM'e haber verilmeden başka bir yoldan (müşteri
/// detayı, Basılanlar, ileride başka bilgisayarın komutu) iptal edilmiş, basılmış ya da fiyatı
/// düzeltilmiş olabilir. Ayrıca §5.5: yayın başlatırken sorulan kalıntı kuyruk yalnız bu bilgisayarın
/// oturumlarını sayar. <see cref="MainShellTestHarness"/> ile, <c>MainShellPrintTests</c> deseninde.
/// </summary>
public sealed class MainShellPrintRereadTests
{
    private static List<string> QueueIds(MainShellViewModel vm) => vm.PrintQueue.Select(r => r.Id).ToList();

    [Fact]
    public async Task Baska_yoldan_iptal_edilen_kuyruk_etiketi_basilmaz_ve_kuyruktan_duser()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@c", 300m);
        var ids = QueueIds(h.Vm);

        // VM'e dokunmadan: satır yalnız veritabanında iptal edildi.
        h.Labels.Cancel(new[] { ids[1] }, CancelReasonCodes.Customer);

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(
            new[] { ids[0], ids[2] }, "iptal edilmiş satır kâğıda gitmez; kalanların sırası korunur");
        h.Vm.PrintQueue.Should().BeEmpty("iptal edilmiş satır da kuyruktan düşer");
        new LabelRepository(h.Db).GetById(ids[1])!.PrintedAt.Should().BeNull();
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@b")!.TotalLabelsPrinted.Should().Be(0);
    }

    [Fact]
    public async Task Kismi_secimde_iptal_edilen_satir_duser_secilmeyen_kuyrukta_kalir()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@c", 300m);
        var ids = QueueIds(h.Vm);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[2]);

        h.Labels.Cancel(new[] { ids[0] }, CancelReasonCodes.Customer);

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(ids[2]);
        QueueIds(h.Vm).Should().Equal(new[] { ids[1] }, "seçilmeyen satır kalır, iptal edilen düşer");
        h.Vm.SelectedQueueItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Veritabaninda_degisen_fiyat_kagida_ve_kuyruk_satirina_yansir()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        var row = h.Vm.PrintQueue.Single();
        var repo = new LabelRepository(h.Db);
        using (var write = DbWrite.Begin(h.Db))
        {
            repo.UpdatePriceForCorrection(row.Id, expectedPrice: 100m, expectPrinted: false,
                expectTentative: false, paidFromBalance: false, newPrice: 150m, correctedAt: 1000, write)
                .Should().Be(1);
            write.Commit();
        }
        row.Price.Should().Be(100m, "ön koşul: kuyruktaki kopya Replace ile tazelenmedi");

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().ContainSingle().Which.Single().Price.Should().Be(150m,
            "kâğıda veritabanındaki güncel fiyat basılır");
        row.Price.Should().Be(150m, "kopya da yeniden okunan satırla tazelenir");
        row.Label.Price.Should().Be(150m);
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(150m);
    }

    [Fact]
    public async Task Veritabaninda_basilmis_etiket_ikinci_kez_basilmaz()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        var ids = QueueIds(h.Vm);

        // Başka bir yoldan basılıp damgalandı; kuyruktaki kopyanın haberi yok.
        h.Labels.MarkPrintedAndRecord(new[] { ids[0] });

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(ids[1]);
        h.Vm.PrintQueue.Should().BeEmpty();
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalLabelsPrinted.Should().Be(1);
    }

    [Fact]
    public async Task Yazdir_hepsi_dusmusse_yaziciyi_cagirmaz_ve_kuyrugu_bosaltir()
    {
        using var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        var ids = QueueIds(h.Vm);
        h.Labels.Cancel(new[] { ids[0] }, CancelReasonCodes.Customer);
        h.Labels.MarkPrintedAndRecord(new[] { ids[1] });

        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().BeEmpty("basılacak satır kalmadı; boş iş yazıcıya gönderilmez");
        h.Vm.PrintQueue.Should().BeEmpty();
        h.Vm.PrintCommand.CanExecute(null).Should().BeTrue("yazıcı kapısı kapanmış olmalı");
    }

    [Fact]
    public async Task YayiniBitir_hepsi_dusmusse_yazici_cagrilmaz_ve_yayin_biter()
    {
        using var h = MainShellTestHarness.Build();
        // Evet: hepsini bas ve bitir. Düşen satırlar kuyrukta kalsaydı soru yinelenirdi; ikinci soruya
        // Vazgeç denir ki hata sonsuz döngü değil görünür başarısızlık olsun.
        var answers = new Queue<bool?>(new bool?[] { true });
        h.Dialogs.ThreeWayResult = _ => answers.Count > 0 ? answers.Dequeue() : null;
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        var ids = QueueIds(h.Vm);
        h.Labels.Cancel(new[] { ids[0] }, CancelReasonCodes.Customer);
        h.Labels.MarkPrintedAndRecord(new[] { ids[1] });

        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().BeEmpty();
        h.Vm.PrintQueue.Should().BeEmpty();
        h.Dialogs.ThreeWayConfirmations.Should().ContainSingle();
        h.Dialogs.Shown.Should().NotContain(x => x.Message.Contains("açık bırakıldı"),
            "basılacak iş kalmaması başarıdır, kapanışı engellemez");
        h.Sessions.GetActive().Should().BeNull("yayın bitmiş olmalı");
    }

    [Fact]
    public async Task YayinBaslat_kalinti_sorusu_yalniz_yerel_oturumlari_sayar()
    {
        using var h = MainShellTestHarness.Build();
        h.Dialogs.ThreeWayResult = _ => false;   // basmadan bitir
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 200m);
        var localIds = QueueIds(h.Vm);
        var customerId = h.Vm.PrintQueue[0].CustomerId;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        // Başka bilgisayarın devredilmiş yayını ve basılmamış iki etiketi akışla inmiş. Bu hâli yazan
        // akış W2'de; burada SQL ile kurulur.
        new SessionRepository(h.Db).Insert(
            new StreamSession("uzak-yayin", null, 500, 600, new[] { "instagram" }, null));
        var labels = new LabelRepository(h.Db);
        foreach (var id in new[] { "uzak-1", "uzak-2" })
            labels.Insert(new Label(id, "uzak-yayin", customerId, "instagram", "@a", "uzak satış", null,
                50m, AddedAt: 550, PrintedAt: null));
        using (var c = h.Db.Open())
            c.Execute("UPDATE StreamSession SET Origin = 'remote', Authority = 'server' WHERE Id = 'uzak-yayin'");

        h.Dialogs.ConfirmResult = _ => true;   // kalıntıları yeni kuyruğa taşı
        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().ContainSingle(c => c.Message.Contains("2 basılmamış"),
            "yalnız bu bilgisayarın oturumundan kalan iki etiket sayılır");
        QueueIds(h.Vm).Should().BeEquivalentTo(localIds, "başka bilgisayarın kuyruğu teklif edilmez");
    }
}
