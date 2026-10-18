using System.IO;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Yayın geçmişi ve rapor başka bilgisayarın yayınını makine adıyla gösterir (W2-09; spec 2026-10-13 §5.7: "Rapor ve
/// geçmiş ekranları başka bilgisayarın yayınını makine adıyla gösterir (OwnerMachineName)"). Uzak yayın yalnız mod On'da
/// satış akışından iner; yalnız yerel yayın varken (mod Off, bugün her lisans) geçmiş bugünkü gibi.
/// </summary>
public sealed class StreamHistoryMachineNameTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public StreamHistoryMachineNameTests()
    {
        new MigrationRunner(_db).Run();
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private string LocalEnded(long startedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Yerel yayın", startedAt, startedAt + 3600, ["tiktok"], null));
        return id;
    }

    /// <summary>Akıştan inmiş başka bilgisayarın devredilmiş yayını (<c>SalesFeedRepository</c>'nin eklediği biçim).</summary>
    private string Remote(long startedAt, string? machine)
    {
        var id = Guid.NewGuid().ToString("N");
        using var c = _db.Open();
        c.Execute(
            @"INSERT INTO StreamSession
                (Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Origin, Authority, OwnerMachineName, ReleasedAt)
              VALUES (@id, 'Akşam yayını', @startedAt, @endedAt, '[""tiktok""]', NULL, @endedAt, 'remote', 'server',
                      @machine, @endedAt)",
            new { id, startedAt, endedAt = startedAt + 3600, machine });
        return id;
    }

    [Fact]
    public void Gecmis_uzak_yayini_makine_adiyla_yerel_yayini_bos_gosterir()
    {
        var local = LocalEnded(1_000);
        var named = Remote(2_000, "PC-A");
        var unnamed = Remote(3_000, null);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.HasRemoteSessions.Should().BeTrue("'Bilgisayar' sütunu görünür");
        vm.Sessions.Single(r => r.SessionId == named).Machine.Should().Be("PC-A");
        vm.Sessions.Single(r => r.SessionId == unnamed).Machine.Should().Be("Başka bilgisayar");
        vm.Sessions.Single(r => r.SessionId == local).Machine.Should().BeNull("bu bilgisayarın yayını");
    }

    [Fact]
    public void Yalniz_yerel_yayin_varken_gecmis_bugunku_gibi()
    {
        LocalEnded(1_000);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.HasRemoteSessions.Should().BeFalse("mod Off'ta uzak yayın yok: sütun gizli");
        vm.Sessions.Should().OnlyContain(r => r.Machine == null);
    }

    [Fact]
    public void Gecmis_sayfasinda_Bilgisayar_sutunu_yalniz_uzak_yayin_varken_gorunur()
    {
        LocalEnded(1_000);
        var localOnly = new StreamHistoryViewModel(_sessions, _labels);
        Remote(2_000, "PC-A");
        var withRemote = new StreamHistoryViewModel(_sessions, _labels);

        System.Windows.Visibility? hidden = null, shown = null;
        var error = OrderDeck.Tests.App.ThemeTestHost.RunOnSta(() =>
        {
            System.Windows.Visibility Column(StreamHistoryViewModel vm)
            {
                var page = OrderDeck.App.Views.Pages.StreamHistoryPage.Create(vm);
                var grid = (System.Windows.Controls.DataGrid)page.FindName("HistoryGrid")!;
                return grid.Columns.Single(c => (string)c.Header == "Bilgisayar").Visibility;
            }
            hidden = Column(localOnly);
            shown = Column(withRemote);
        });

        error.Should().BeNull();
        hidden.Should().Be(System.Windows.Visibility.Collapsed, "mod Off'ta tablo bugünkü gibi");
        shown.Should().Be(System.Windows.Visibility.Visible);
    }

    [Fact]
    public void Rapor_uzak_yayinda_makine_adini_yerel_yayinda_hicbir_sey_gostermez()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-shmn-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(settingsPath);
            store.Save(new AppSettings());
            var payments = new PaymentRequestService(store, new WhatsAppMessageBuilder(), new FakeUrlLauncher(),
                PaymentRequestServiceTestHelpers.StubApiClient(), new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                new InMemoryPaymentJobStore(), LocalShipping.Of(store));
            var report = new StreamReportViewModel(_labels, _sessions, new GiveawayRepository(_db),
                new CustomerRepository(_db), payments, new FakeDialogService());

            report.Load(Remote(2_000, "PC-A"));
            report.RemoteMachineText.Should().Be("PC-A'da yapılan yayın");

            report.Load(Remote(3_000, null));
            report.RemoteMachineText.Should().Be("Başka bir bilgisayarda yapılan yayın");

            report.Load(LocalEnded(1_000));
            report.RemoteMachineText.Should().BeNull("bu bilgisayarın yayını");
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}
