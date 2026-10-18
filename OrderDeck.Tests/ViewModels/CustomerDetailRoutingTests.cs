using System.Net.Http;
using FluentAssertions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.Sync;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Müşteri kartının iptal ve iptali geri alması düzenleme yönlendiricisinden geçer (spec 2026-10-13 §5.3 "Müşteri
/// kartı", §5.7, §5.8; W2-05). Mod Off'ta veritabanı bugünküyle birebir aynı ve sıfır istek; mod On'da devredilmiş
/// yayının etiketi tek komutla (beklenen sürüm yerel ServerVersion, yerel satır yanıttan, ABORT yok), karışık seçim
/// bir yerel yazım + bir komut, iptalden sonra yedek çekmecesi yine açılır (ayna satırlarıyla), retler satır içi,
/// çevrimdışıyken sunucu satırlarının düğmesi pasif ve araç ipucu gösterir.
/// </summary>
public sealed class CustomerDetailRoutingTests : IDisposable
{
    private readonly List<SalesEditTestBed> _beds = new();

    public void Dispose()
    {
        foreach (var b in _beds) b.Dispose();
    }

    /// <summary>Çekmeceyi açmaz (WPF yok), başlıkları kaydeder.</summary>
    private sealed class RecordingDrawers : IDrawerService
    {
        public List<string> Titles { get; } = new();

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            Titles.Add(title);
            return Task.FromResult(false);
        }

        public bool CloseTop() => false;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class FixedClock : IClock
    {
        public long UnixNow() => 1_791_000_000L;
    }

    private SalesEditTestBed Bed()
    {
        var bed = new SalesEditTestBed { FixedUnixNow = 1_791_000_000L };
        _beds.Add(bed);
        return bed;
    }

    /// <param name="routed">false: düzenleyici verilmez (bugünkü kurulum, yalnız yerel).</param>
    private static (CustomerDetailViewModel Vm, RecordingDrawers Drawers) Detail(SalesEditTestBed bed, bool routed = true)
    {
        var drawers = new RecordingDrawers();
        // Bakiye isteği satış sunucusuna gitmesin: ayrı, sessiz istemci (kartın bakiye bölümü bu testin konusu değil).
        var balanceApi = new LicenseApiClient(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://stub") }, new LicenseTokenStore());
        var vm = new CustomerDetailViewModel(bed.Customers, bed.LabelRepo, bed.Labels, new GiveawayRepository(bed.Db),
            new StreamSessionService(bed.Sessions, new FixedClock()), balanceApi, drawers,
            editor: routed ? bed.Router : null);
        return (vm, drawers);
    }

    private static void Select(CustomerDetailViewModel vm, params string[] ids)
    {
        vm.SelectedLabels.Clear();
        foreach (var row in vm.Labels.Where(l => ids.Contains(l.Id))) vm.SelectedLabels.Add(row);
    }

    // ── mod Off: bugünkü yazım, sıfır istek ───────────────────────────────

    private static (string Customer, string Kept, string Target) SeedLocal(SalesEditTestBed bed, bool cancelTarget)
    {
        const string customer = "5f0c3b6a2e9d4c71a8b0e4d2f6a1c9b3";
        var session = bed.LocalSession(ended: true, id: "0a1b2c3d4e5f60718293a4b5c6d7e8f9");
        var kept = bed.LocalLabel(session, customer, 250m, id: "11111111111111111111111111111111");
        var target = bed.LocalLabel(session, customer, 150m, id: "22222222222222222222222222222222");
        if (cancelTarget) bed.Labels.Cancel([target], CancelReasonCodes.Customer);
        return (customer, kept, target);
    }

    [Fact]
    public async Task Mod_Offta_iptal_veritabanini_bugunku_cagriyla_birebir_ayni_yazar_ve_istek_gitmez()
    {
        var today = Bed();
        var (_, _, todayTarget) = SeedLocal(today, cancelTarget: false);
        today.Labels.Cancel([todayTarget], CancelReasonCodes.WrongProduct);   // bugünkü çağrı

        var routed = Bed();
        var (customer, _, target) = SeedLocal(routed, cancelTarget: false);
        var (vm, _) = Detail(routed);
        vm.Load(customer).Should().BeTrue();

        await vm.CancelLabelsAsync([target], CancelReasonCodes.WrongProduct);

        routed.Snapshot().Should().Be(today.Snapshot())
            .And.Contain($"Id={target}").And.Contain("CancelReason=wrong-product");
        routed.HttpCount.Should().Be(0);
        vm.LabelsMessage.Should().BeNull();
        vm.Labels.Single(l => l.Id == target).IsCancelled.Should().BeTrue();
    }

    [Fact]
    public void Mod_Offta_iptali_geri_alma_veritabanini_bugunku_cagriyla_birebir_ayni_yazar_ve_istek_gitmez()
    {
        var today = Bed();
        var (_, _, todayTarget) = SeedLocal(today, cancelTarget: true);
        today.Labels.Uncancel([todayTarget]);

        var routed = Bed();
        var (customer, _, target) = SeedLocal(routed, cancelTarget: true);
        var (vm, _) = Detail(routed);
        vm.Load(customer).Should().BeTrue();
        Select(vm, target);

        vm.UncancelSelectedCommand.Execute(null);   // mod Off'ta eşzamanlı biter (bugünkü gibi)

        routed.Snapshot().Should().Be(today.Snapshot()).And.Contain($"Id={target}");
        routed.HttpCount.Should().Be(0);
        vm.Labels.Single(l => l.Id == target).IsCancelled.Should().BeFalse();
    }

    // ── mod On ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Onda_devredilmis_yayinin_etiketi_tek_iptal_komutuyla_beklenen_surum_ve_yerel_satir_yanittan()
    {
        var bed = Bed();
        bed.ModeOn();
        var (_, customer, order) = await bed.RemoteLabelAsync();
        var before = bed.Local(order);
        var (vm, _) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();

        await vm.CancelLabelsAsync([N(order)], CancelReasonCodes.Customer);

        var call = bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("cancel");
        call.Expected!.Single()!.Version.Should().Be((int)before.ServerVersion!.Value);
        var after = bed.Local(order);
        after.CancelledAt.Should().NotBeNull();
        after.SyncedAt.Should().Be(before.SyncedAt, "yerel yazım değil, yanıt (ABORT yok, kuyruğa girmez)");
        after.Revision.Should().Be(before.Revision);
        vm.LabelsMessage.Should().BeNull("sales-server-authority ABORT'u olsaydı iptal düşerdi");
        vm.Labels.Single(l => l.Id == N(order)).IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Mod_Onda_iptali_geri_alma_komutla()
    {
        var bed = Bed();
        bed.ModeOn();
        var session = bed.RemoteSession();
        var customer = bed.RemoteCustomer();
        var order = bed.RemoteOrder(session, customer, cancelled: true);
        await bed.PullAsync();
        var (vm, _) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();
        Select(vm, N(order));

        await vm.UncancelSelectedCommand.ExecuteAsync(null);

        bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Kind.Should().Be("uncancel");
        bed.Local(order).CancelledAt.Should().BeNull();
        vm.Labels.Single(l => l.Id == N(order)).IsCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task Karisik_secim_bir_yerel_yazim_ve_bir_komut()
    {
        var bed = Bed();
        bed.ModeOn();
        var (_, customer, order) = await bed.RemoteLabelAsync();
        var local = bed.LocalLabel(bed.LocalSession(ended: true), N(customer));
        var localBefore = bed.Local(local);
        var (vm, _) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();

        await vm.CancelLabelsAsync([local, N(order)], CancelReasonCodes.Customer);

        bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Payload!.Value.GetProperty("items")
            .EnumerateArray().Select(e => e.GetGuid()).Should().Equal(order);
        bed.Local(local).CancelledAt.Should().NotBeNull();
        bed.Local(local).Revision.Should().Be(localBefore.Revision + 1);
        bed.Local(order).CancelledAt.Should().NotBeNull();
        vm.LabelsMessage.Should().BeNull();
    }

    [Fact]
    public async Task Komutla_iptalden_sonra_yedek_cekmecesi_ayna_satirlarla_acilir()
    {
        var bed = Bed();
        bed.ModeOn();
        var session = bed.RemoteSession();
        var customer = bed.RemoteCustomer();
        var parent = bed.RemoteOrder(session, customer);
        bed.RemoteOrder(session, customer, 120m, tentative: true, parent: parent, username: "yedek_alici");
        await bed.PullAsync();
        var (vm, drawers) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();

        await vm.CancelLabelsAsync([N(parent)], CancelReasonCodes.Customer);

        bed.Local(parent).CancelledAt.Should().NotBeNull();
        drawers.Titles.Should().Equal("'alici_ornek' yedekleri (1)");
    }

    [Fact]
    public async Task Session_live_satir_ici_gosterilir()
    {
        var bed = Bed();
        bed.ModeOn();
        var (_, customer, order) = await bed.RemoteLabelAsync();
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-3);
        bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "session-live",
            new Dictionary<string, object?> { ["machine"] = "PC-A", ["ownerLastSeenAt"] = lastSeen });
        var (vm, drawers) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();

        await vm.CancelLabelsAsync([N(order)], CancelReasonCodes.Customer);

        vm.LabelsMessage.Should().Be(
            $"Bu yayın PC-A'da canlı (son sinyal {OrderDeck.App.Services.Sync.SalesCommandMessages.Clock(lastSeen)}).");
        bed.Local(order).CancelledAt.Should().BeNull();
        drawers.Titles.Should().BeEmpty("iptal edilmeyen etiketin yedek çekmecesi açılmaz");
    }

    [Fact]
    public async Task Cevrimdisiyken_sunucu_satirlarinin_dugmesi_pasif_arac_ipucuyla()
    {
        var bed = Bed();
        bed.ModeOn();
        var session = bed.RemoteSession();
        var customer = bed.RemoteCustomer();
        var active = bed.RemoteOrder(session, customer);
        var cancelled = bed.RemoteOrder(session, customer, cancelled: true);
        await bed.PullAsync();
        bed.Online = false;
        var (vm, _) = Detail(bed);
        vm.Load(N(customer)).Should().BeTrue();

        Select(vm, N(active), N(cancelled));

        vm.CancelSelectedCommand.CanExecute(null).Should().BeFalse();
        vm.UncancelSelectedCommand.CanExecute(null).Should().BeFalse();
        vm.CancelBlockedReason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        vm.UncancelBlockedReason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");

        bed.Online = true;
        Select(vm, N(active));
        vm.CancelSelectedCommand.CanExecute(null).Should().BeTrue();
        vm.CancelBlockedReason.Should().BeNull();
    }

    [Fact]
    public async Task Duzenleyici_verilmezse_bugunku_yerel_yol()
    {
        var bed = Bed();
        bed.ModeOn();   // kip okunmaz bile
        var label = bed.LocalLabel(bed.LocalSession(ended: true), "7a8b9c0d1e2f30415263748596a7b8c9");
        var (vm, _) = Detail(bed, routed: false);
        vm.Load("7a8b9c0d1e2f30415263748596a7b8c9").Should().BeTrue();

        await vm.CancelLabelsAsync([label], CancelReasonCodes.Customer);

        bed.Local(label).CancelledAt.Should().NotBeNull();
        bed.HttpCount.Should().Be(0);
    }
}
