using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// R10-UX01: DrawerHost bilinçli olarak modal DEĞİL — varyant çekmecesi
/// açıkken "Yayını Bitir" (topbar butonu + kısayol) erişilebilir kalıyor.
/// Eski davranışta seçim onayı, çekmece açılırken yakalanan session.Id ile
/// KAPANMIŞ yayına etiket yazardı; yeni yayın başladıysa satırlar onun
/// kuyruğunda görünürdü (sessiz göç). İki katmanlı kapanış:
///
/// 1. EndStream kapısı: seçim açıkken yayın bitirilemez (çekiliş kapısıyla
///    aynı desen) — operatör önce seçimi tamamlar ya da vazgeçer.
/// 2. Onay dönüşünde bağlam doğrulaması: yakalanan oturum artık aktif olan
///    değilse (kapının kesemediği yollar: baskı await'i sırasında açılan
///    çekmece, testin buradaki doğrudan End'i) satır YAZILMAZ ve operatör
///    bilgilendirilir — ne kapanmış yayına yazım ne yeni yayına sessiz göç.
///
/// Çekmece task'ı TCS ile elle tamamlanıyor; akış await'e kadar senkron
/// olduğundan sıralama deterministik (Dispatcher turu paylaşımı dahil).
/// </summary>
public class VariantPickerEndStreamRaceTests
{
    /// <summary>
    /// İçerik fabrikasını hiç çağırmayan (STA istemesin), sonucu testin
    /// elindeki TCS'ten dönen çekmece. Onay/vazgeç anını test seçer.
    /// </summary>
    private sealed class GatedDrawerService : IDrawerService
    {
        private readonly TaskCompletionSource<bool> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ShowCount { get; private set; }

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            ShowCount++;
            return _result.Task;
        }

        public void Complete(bool confirmed) => _result.TrySetResult(confirmed);

        public bool CloseTop() => false;
    }

    private static readonly CatalogProduct Elbise = new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static MainShellTestHarness.Harness Seed(GatedDrawerService drawers)
    {
        var h = MainShellTestHarness.Build(drawers);

        var variants = new[]
        {
            new CatalogVariant("v1", "p1", "Siyah", "S", null, true, 0),
            new CatalogVariant("v2", "p1", "Siyah", "M", null, true, 1),
            new CatalogVariant("v3", "p1", "Siyah", "L", null, true, 2),
        };
        new CatalogReplicaRepository(h.Db).Replace(
            new[] { Elbise }, variants, Array.Empty<CatalogCategory>(),
            new[]
            {
                new CatalogBroadcastCode("p1", "Siyah", "Ateş",
                    SearchNormalizer.Normalize("Ateş"), 0, 0),
            });

        h.Vm.ActivePriceText = "100";
        h.Vm.ActiveCode = "ateş";
        return h;
    }

    [Fact]
    public async Task Secim_acikken_yayin_bitirilemez_onaydan_sonra_acik_yayina_yazilir()
    {
        var drawers = new GatedDrawerService();
        using var h = Seed(drawers);
        var sessionId = h.Sessions.GetActive()!.Id;

        // Yorum belirsiz → çekmece açılır ve TCS'te bekler.
        var addTask = h.Vm.AddChatToQueueAsync(
            MainShellTestHarness.ChatVm("@ali", "bana da"));
        drawers.ShowCount.Should().Be(1);
        addTask.IsCompleted.Should().BeFalse("çekmece hâlâ açık olmalı");

        // Kapı olmasa bu onay yayını bitirirdi — test onu bilerek EVET yapar.
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Sessions.GetActive().Should().NotBeNull(
            "bekleyen varyant seçimi varken yayın bitirilememeli");
        h.Dialogs.Shown.Should().Contain(s => s.Title == "Varyant seçimi açık");

        // Operatör seçimi tamamlar → satır hâlâ AÇIK olan yayına yazılır.
        h.Vm.ActiveVariantPicker!.Items.First(i => i.Value == "M").IsChecked = true;
        drawers.Complete(true);
        await addTask;

        h.Vm.PrintQueue.Should().ContainSingle()
            .Which.Label.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task Yayin_secim_sirasinda_bittiyse_onay_hicbir_sey_yazmaz_yeni_yayina_goc_etmez()
    {
        var drawers = new GatedDrawerService();
        using var h = Seed(drawers);
        var oldSessionId = h.Sessions.GetActive()!.Id;

        var addTask = h.Vm.AddChatToQueueAsync(
            MainShellTestHarness.ChatVm("@ali", "bana da"));
        drawers.ShowCount.Should().Be(1);

        // Kapının kesemediği bir yoldan yayın biter ve YENİSİ başlar
        // (EndStream'in baskı await'i penceresinin simülasyonu).
        h.Sessions.End(oldSessionId);
        var newSession = h.Sessions.Start("Yeni Yayın", new[] { "instagram" });

        h.Vm.ActiveVariantPicker!.Items.First(i => i.Value == "M").IsChecked = true;
        drawers.Complete(true);
        await addTask;

        // Ne eski yayına yazım ne yeni yayına sessiz göç; operatör bilgilenir.
        h.Vm.PrintQueue.Should().BeEmpty(
            "yakalanan oturum artık aktif değil — satır yazılmamalı");
        new LabelRepository(h.Db).GetUnprintedBySession(oldSessionId).Should().BeEmpty();
        new LabelRepository(h.Db).GetUnprintedBySession(newSession.Id).Should().BeEmpty();
        h.Dialogs.Shown.Should().Contain(s =>
            s.Severity == DialogSeverity.Warning && s.Message.Contains("yazılmadı"));
    }
}
