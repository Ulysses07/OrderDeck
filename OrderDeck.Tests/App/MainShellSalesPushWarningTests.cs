using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.Sync;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Etiket gönderiminin sonucu kabukta (spec 2026-10-13 §5.2, §5.6-5, §5.7; W1-08): arka plandaki gönderim turu
/// sunucunun reddettiği satırları ve başka bilgisayarda iptal edilmiş basılı kâğıtları
/// <see cref="SessionOrderSyncService.PushOutcome"/> ile bildirir; <c>MainShellViewModel</c> dispatcher üzerinden,
/// açılır pencere olmadan, yalnız sayılarla uyarır. Uçtan uca: <see cref="FakeSalesServer"/> → servis → VM.
/// </summary>
public sealed class MainShellSalesPushWarningTests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private const string DisplayName = "Örnek Alıcı";
    private const string Username = "ornek_alici";

    private readonly FakeSalesServer _server = new();
    private SessionOrderSyncService? _svc;

    public void Dispose() => _server.Dispose();

    private MainShellTestHarness.Harness Build() => MainShellTestHarness.Build(orderSync: db =>
    {
        var license = new License(_server.Lisans);
        _svc = new SessionOrderSyncService(
            new LicenseApiClient(
                new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore()),
            new SessionRepository(db), new LabelRepository(db), license, TestSalesEnvelope.For(db, license),
            new HandoverGate(), new SalesPushApply(db), new SystemClock(),
            new CapturingLogger<SessionOrderSyncService>());
        return _svc;
    });

    private static string AddLabel(MainShellTestHarness.Harness h, long? printedAt)
    {
        var customerId = Guid.NewGuid().ToString("N");
        h.CustomerRepo.Insert(new Customer(customerId, "instagram", $"{Username}_{customerId[..6]}", DisplayName,
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        var sessionId = new SessionRepository(h.Db).GetActive()!.Id;
        var id = Guid.NewGuid().ToString("N");
        new LabelRepository(h.Db).Insert(new Label(id, sessionId, customerId, "instagram", Username, "alıyorum",
            "A1", 100m, 1000, printedAt, DisplayName: DisplayName));
        return id;
    }

    /// <summary>
    /// Gövdeyi KENDİ Dispatcher döngüsü olan yeni bir STA iş parçacığında koşturur. Sonuç olayı arka plandan
    /// dispatcher'a taşındığı için kuyruğun işlenmesi gerekir; xunit iş parçacığının paylaşılan Dispatcher'ını
    /// pompalamak, aynı iş parçacığında daha önce kurulup kapatılmamış başka VM'lerin zamanlayıcılarını da
    /// koşturur (veritabanı çoktan kapanmış: "no such table"). Burada kuyrukta yalnız bu testin VM'i vardır.
    /// </summary>
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

    /// <summary>Gönderim turu, ardından dispatcher kuyruğu (sonuç olayının taşınması) boşalır. Tur arka planda
    /// koşar (servis her await'te ConfigureAwait(false)); devamı STA iş parçacığına döner.</summary>
    private async Task SyncAndDrainAsync()
    {
        await _svc!.SyncOnceAsync();
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    [Fact]
    public void Reddedilen_ekleme_ve_baskasinda_iptal_edilmis_kagit_seritte_yalniz_sayilarla_gorunur()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        var printed = AddLabel(h, printedAt: 1000);
        var foreign = AddLabel(h, printedAt: null);
        _server.OrderVerdicts[Guid.Parse(printed)] = new("ignored", "cancelled-elsewhere", ["cancel", "print"],
            Current: c => c with
            {
                PrintedAt = null, CancelledAt = DateTimeOffset.FromUnixTimeSeconds(1500), CancelReason = "x",
                CancelSetAt = DateTimeOffset.FromUnixTimeSeconds(1500),
            });
        _server.OrderVerdicts[Guid.Parse(foreign)] = new("rejected", "session-owned-elsewhere", [],
            WithoutCurrent: true);

        await SyncAndDrainAsync();

        h.Vm.SalesPushWarning.Should().Be(
            "1 değişiklik uygulanamadı: yayın başka bilgisayarda açık. " +
            "1 etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");
        h.Vm.SalesPushWarning.Should().NotContain(DisplayName).And.NotContain(Username);
        h.Dialogs.Shown.Should().BeEmpty("uyarı açılır pencere değil, şerit");

        h.Vm.DismissSalesPushWarningCommand.Execute(null);
        h.Vm.SalesPushWarning.Should().BeNull();
    });

    [Fact]
    public void Yalniz_yok_sayilan_fiyat_operatoru_uyarmaz()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        var id = AddLabel(h, printedAt: 1000);
        _server.OrderVerdicts[Guid.Parse(id)] = new("ignored", "unit-stamped", ["price"],
            Current: c => c with { Price = 90m });

        await SyncAndDrainAsync();

        _server.Calls(FakeSalesServer.OrdersSync).Should().NotBeEmpty("ön koşul: tur etiketi gönderdi");
        new LabelRepository(h.Db).GetById(id)!.Price.Should().Be(90m, "ön koşul: sunucunun değeri uygulandı");
        h.Vm.SalesPushWarning.Should().BeNull();
    });

    [Fact]
    public void Serit_kapatilana_kadar_sonuclar_birikir_ayni_etiket_iki_kez_sayilmaz()
    {
        using var h = Build();

        h.Vm.ShowSalesPushOutcome(new SalesPushOutcome(0,
            [new RejectedLabelPush("l1", "session-released")], Array.Empty<string>()));
        h.Vm.ShowSalesPushOutcome(new SalesPushOutcome(1,
            [new RejectedLabelPush("l1", "session-released"), new RejectedLabelPush("l2", "shipment-shipped")],
            ["l3"]));

        h.Vm.SalesPushWarning.Should().Be(
            "2 değişiklik uygulanamadı: yayın sunucuya devredilmişti (1), kargoya verilmiş dosya (1). " +
            "1 etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");

        h.Vm.DismissSalesPushWarningCommand.Execute(null);
        h.Vm.ShowSalesPushOutcome(new SalesPushOutcome(0, [new RejectedLabelPush("l4", null)], Array.Empty<string>()));
        h.Vm.SalesPushWarning.Should().Be("1 değişiklik uygulanamadı: sunucu kabul etmedi.",
            "kapatılan şeridin sayıları sıfırlanır");
    }

    [Fact]
    public void Metin_nedenleri_sayilariyla_siralar()
    {
        MainShellViewModelText(new() { ["session-released"] = 2, ["shipment-shipped"] = 1 }, 0)
            .Should().Be("3 değişiklik uygulanamadı: yayın sunucuya devredilmişti (2), kargoya verilmiş dosya (1).");
        MainShellViewModelText(new() { ["shipment-shipped"] = 1 }, 0)
            .Should().Be("1 değişiklik uygulanamadı: kargoya verilmiş dosya.");
        MainShellViewModelText(new(), 2)
            .Should().Be("2 etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");
        MainShellViewModelText(new(), 0).Should().BeNull();
    }

    private static string? MainShellViewModelText(Dictionary<string, int> byReason, int discard)
        => OrderDeck.App.ViewModels.MainShellViewModel.SalesPushWarningText(byReason, discard);
}
