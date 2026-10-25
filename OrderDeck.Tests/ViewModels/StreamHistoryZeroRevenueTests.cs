using Dapper;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Yayın geçmişi cirosu 0 olan bitmiş yayını listelemez (Burak, 25.10.2026: "ciro 0 olan yayınları yayın geçmişinde
/// göstermeye gerek yok"). "Ciro" sayfanın "Ciro" sütununun gösterdiği değerdir (<see cref="LabelRepository.GetSessionTotals"/>
/// → <c>TotalAmount</c>: basılmış, iptal edilmemiş, onaylanmamış yedek olmayan etiketlerin toplamı); ayrı bir hesap yok.
/// Kural yerel ve başka bilgisayarın yayınına aynıdır; yayının verisi silinmez, yalnız listede görünmez. Süren (açık)
/// yayın cirosu 0 olsa da gizlenmez.
/// </summary>
public sealed class StreamHistoryZeroRevenueTests : IDisposable
{
    private const string CustomerId = "izleyici-1";

    private readonly InMemorySqlite _db = new();
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public StreamHistoryZeroRevenueTests()
    {
        new MigrationRunner(_db).Run();
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        new CustomerRepository(_db).Insert(
            new Customer(CustomerId, "tiktok", "@izleyici", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
    }

    public void Dispose() => _db.Dispose();

    private string Sale(string sessionId, decimal price, long printedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, sessionId, CustomerId, "tiktok", "@izleyici",
            "A12 aldım", "A12", price, AddedAt: printedAt - 10, PrintedAt: printedAt));
        return id;
    }

    private string LocalEnded(long startedAt, decimal ciro)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Yerel yayın", startedAt, startedAt + 3600, ["tiktok"], null));
        if (ciro != 0m) Sale(id, ciro, startedAt + 60);
        return id;
    }

    /// <summary>Akıştan inmiş başka bilgisayarın devredilmiş yayını (<c>SalesFeedRepository</c>'nin eklediği biçim).
    /// Akış etiketi kilit altında yazar; testte önce etiket yerel yetkiyle eklenir, sonra yetki sunucuya geçer.</summary>
    private string Remote(long startedAt, string? machine, decimal ciro)
    {
        var id = Guid.NewGuid().ToString("N");
        using (var c = _db.Open())
            c.Execute(
                @"INSERT INTO StreamSession
                    (Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Origin, Authority, OwnerMachineName, ReleasedAt)
                  VALUES (@id, 'Akşam yayını', @startedAt, @endedAt, '[""tiktok""]', NULL, @endedAt, 'remote', 'local',
                          @machine, @endedAt)",
                new { id, startedAt, endedAt = startedAt + 3600, machine });
        if (ciro != 0m) Sale(id, ciro, startedAt + 60);
        using (var c = _db.Open())
            c.Execute("UPDATE StreamSession SET Authority = 'server' WHERE Id = @id", new { id });
        return id;
    }

    [Fact]
    public void Cirosu_sifir_olan_bitmis_yerel_yayin_listelenmez_verisi_silinmez()
    {
        var sold = LocalEnded(1_000, 250m);
        var empty = LocalEnded(2_000, 0m);
        var cancelledOnly = LocalEnded(3_000, 0m);
        _labels.MarkCancelled([Sale(cancelledOnly, 400m, printedAt: 3_060)], cancelledAt: 3_100, reason: "vazgeçti");

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Select(r => r.SessionId).Should().Equal(new[] { sold },
            "'Ciro' sütunu 0,00 TL gösterecek yayınlar (satışsız ya da yalnız iptal) listede yok");
        vm.Sessions.Single().TotalAmount.Should().Be(250m);
        _sessions.GetById(empty).Should().NotBeNull("yayın silinmez, yalnız listede görünmez");
        _sessions.GetById(cancelledOnly).Should().NotBeNull();
    }

    [Fact]
    public void Basilmamis_etiketi_olan_yayinin_cirosu_sayfadaki_tanimla_sifirdir()
    {
        var id = LocalEnded(1_000, 0m);
        _labels.Insert(new Label(Guid.NewGuid().ToString("N"), id, CustomerId, "tiktok", "@izleyici",
            "A12 aldım", "A12", 300m, AddedAt: 1_050, PrintedAt: null));

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Should().BeEmpty("sayfanın ciro tanımı yalnız basılmış etiketi sayar (GetSessionTotals)");
    }

    [Fact]
    public void Baska_bilgisayarin_yayinina_ayni_kural_uygulanir()
    {
        var sold = Remote(1_000, "PC-A", 180m);
        var empty = Remote(2_000, "PC-B", 0m);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Select(r => r.SessionId).Should().Equal(sold);
        vm.Sessions.Single().Machine.Should().Be("PC-A");
        _sessions.GetById(empty).Should().NotBeNull();
    }

    [Fact]
    public void Yalniz_cirosuz_uzak_yayin_varken_Bilgisayar_sutunu_gizli_kalir()
    {
        LocalEnded(1_000, 250m);
        Remote(2_000, "PC-A", 0m);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.HasRemoteSessions.Should().BeFalse("sütun listede görünen satırlara bakar; gizlenen uzak yayın sütunu açmaz");
        vm.Sessions.Should().OnlyContain(r => r.Machine == null);
    }

    [Fact]
    public void Suren_yayin_cirosu_sifir_olsa_da_gizlenmez()
    {
        var live = new StreamSession("canli", "Canlı yayın", 1_000, null, ["tiktok"], null);
        var ended = live with { EndedAt = 4_600 };

        StreamHistoryViewModel.IsListed(live, totalAmount: 0m).Should().BeTrue("süren yayın satışsız da görünür");
        StreamHistoryViewModel.IsListed(ended, totalAmount: 0m).Should().BeFalse();
        StreamHistoryViewModel.IsListed(ended, totalAmount: 0.01m).Should().BeTrue();
    }

    [Fact]
    public void Satir_siniri_gizlenen_yayinlari_saymaz()
    {
        const int limit = 365;
        var oldest = LocalEnded(10_000, 100m);
        for (var i = 1; i < limit; i++) LocalEnded(10_000 + i * 10_000, 100m);
        LocalEnded(10_000 + limit * 10_000, 0m);                // en yeni yayın satışsız

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Should().HaveCount(limit, "sınır listelenen satır sayısıdır; gizlenen yayın yer kaplamaz");
        vm.Sessions.Should().Contain(r => r.SessionId == oldest);
        vm.Sessions.Should().OnlyContain(r => r.TotalAmount != 0m);
    }
}
