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
/// Yayın geçmişi basılmış etiketi hiç olmayan bitmiş yayını listelemez (Burak, 25.10.2026: "ciro 0 olan yayınları yayın
/// geçmişinde göstermeye gerek yok"). Basılmış etiket, sayfanın "Etiket" ve "Ciro" sütunlarının saydığı küme
/// (<see cref="LabelRepository.GetSessionTotals"/>: basılmış, iptal edilmemiş, onaylanmamış yedek olmayan); ayrı bir
/// hesap yok. Satışsız, yalnız iptalli ya da yalnız basılmamış etiketli yayın gizlenir; 0 TL basılmış etiketi olan yayın
/// listede kalır (raporuna giden kalıcı tek yol bu liste). Kural yerel ve başka bilgisayarın yayınına aynıdır; yayının
/// verisi silinmez, yalnız listede görünmez.
/// </summary>
public sealed class StreamHistoryZeroRevenueTests : IDisposable
{
    private const string CustomerId = "izleyici-1";
    private const int MaxRows = 365;

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
    public void Basilmis_etiketi_olmayan_bitmis_yerel_yayin_listelenmez_verisi_silinmez()
    {
        var sold = LocalEnded(1_000, 250m);
        var empty = LocalEnded(2_000, 0m);
        var cancelledOnly = LocalEnded(3_000, 0m);
        _labels.MarkCancelled([Sale(cancelledOnly, 400m, printedAt: 3_060)], cancelledAt: 3_100, reason: "vazgeçti");

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Select(r => r.SessionId).Should().Equal(new[] { sold },
            "satışsız ya da yalnız iptalli yayının basılmış etiketi yok, listede yok");
        vm.Sessions.Single().TotalAmount.Should().Be(250m);
        _sessions.GetById(empty).Should().NotBeNull("yayın silinmez, yalnız listede görünmez");
        _sessions.GetById(cancelledOnly).Should().NotBeNull();
    }

    [Fact]
    public void Yalniz_basilmamis_etiketi_olan_yayin_listelenmez()
    {
        var id = LocalEnded(1_000, 0m);
        _labels.Insert(new Label(Guid.NewGuid().ToString("N"), id, CustomerId, "tiktok", "@izleyici",
            "A12 aldım", "A12", 300m, AddedAt: 1_050, PrintedAt: null));

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Should().BeEmpty("sayfanın etiket ve ciro tanımı yalnız basılmış etiketi sayar (GetSessionTotals)");
    }

    [Fact]
    public void Sifir_TL_basilmis_etiketi_olan_yayin_listede_kalir()
    {
        var zeroPriced = LocalEnded(1_000, 0m);
        Sale(zeroPriced, 0m, printedAt: 1_060);
        var empty = LocalEnded(2_000, 0m);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Select(r => r.SessionId).Should().Equal(new[] { zeroPriced },
            "0 TL satış meşru (K8); raporuna, Basılan etiketler ile fiyat düzeltmeye giden kalıcı tek yol bu liste");
        vm.Sessions.Single().TotalAmount.Should().Be(0m);
        vm.Sessions.Single().LabelCount.Should().Be(1);
        _sessions.GetById(empty).Should().NotBeNull();
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
    public void Yalniz_satissiz_uzak_yayin_varken_Bilgisayar_sutunu_gizli_kalir()
    {
        LocalEnded(1_000, 250m);
        Remote(2_000, "PC-A", 0m);

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.HasRemoteSessions.Should().BeFalse("sütun listede görünen satırlara bakar; gizlenen uzak yayın sütunu açmaz");
        vm.Sessions.Should().OnlyContain(r => r.Machine == null);
    }

    [Fact]
    public void Satir_siniri_gizlenen_yayinlari_saymaz()
    {
        var oldest = LocalEnded(10_000, 100m);
        for (var i = 1; i < MaxRows; i++) LocalEnded(10_000 + i * 10_000, 100m);
        LocalEnded(10_000 + MaxRows * 10_000, 0m);              // en yeni yayın satışsız

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Should().HaveCount(MaxRows, "sınır listelenen satır sayısıdır; gizlenen yayın yer kaplamaz");
        vm.Sessions.Should().Contain(r => r.SessionId == oldest);
        vm.Sessions.Should().OnlyContain(r => r.TotalAmount != 0m);
    }

    [Fact]
    public void Liste_sinira_dolunca_durur_en_eski_yayin_disarida_kalir()
    {
        var ids = new List<string>();                           // eskiden yeniye
        for (var i = 0; i <= MaxRows; i++) ids.Add(LocalEnded(10_000 + i * 10_000, 100m));

        var vm = new StreamHistoryViewModel(_sessions, _labels);

        vm.Sessions.Select(r => r.SessionId).Should().Equal(Enumerable.Reverse(ids).Take(MaxRows),
            "liste en yeniden eskiye 365 satırda durur");
        vm.Sessions.Should().NotContain(r => r.SessionId == ids[0], "366. (en eski) yayın sınırın dışında");
    }
}
