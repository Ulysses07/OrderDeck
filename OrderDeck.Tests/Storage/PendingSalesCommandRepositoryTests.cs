using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Bekleyen satış komutları ve örtüsü (spec 2026-10-13 §3.2, §5.5, §5.6-1; W2-07): komut ve örttüğü etiketler tek
/// işlemde yazılır, yalnız yerel kip On iken; örtülen etiket kuyrukta (<see cref="LabelService.GetQueue"/>), kalıntı
/// teklifinde (<see cref="LabelRepository.GetUnprintedOutsideSession"/>) ve baskı öncesi yeniden okumada
/// (<see cref="LabelRepository.GetPrintableByIds"/>) görünmez; satır silinince örtü kalkar. Etiket satırının kendisine
/// dokunulmaz.
/// </summary>
public sealed class PendingSalesCommandRepositoryTests : IDisposable
{
    private sealed class Clock : IClock
    {
        public long UnixNow() => 1_000;
    }

    private readonly InMemorySqlite _db = new();
    private readonly PendingSalesCommandRepository _pending;
    private readonly LabelRepository _labels;
    private readonly SessionRepository _sessions;
    private readonly CustomerRepository _customers;

    public PendingSalesCommandRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _pending = new PendingSalesCommandRepository(_db);
        _labels = new LabelRepository(_db);
        _sessions = new SessionRepository(_db);
        _customers = new CustomerRepository(_db);
        _customers.Insert(new Customer("musteri-1", "tiktok", "ornek_alici", null, null, 100, 200, false, null, null, 0,
            0m, null, null, null));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private T Scalar<T>(string sql, object? param = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    private void ModeOn() => Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

    private string Session(bool ended)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Yayın", 500, ended ? 900 : null, ["tiktok"], null));
        return id;
    }

    private string Label(string sessionId, long addedAt = 600)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, sessionId, "musteri-1", "tiktok", "ornek_alici", "aldım", "A1", 100m, addedAt, null));
        return id;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public void Mod_Onda_komut_ve_ortulen_etiketler_tek_islemde_yazilir_ayni_kimlik_ikinci_kez_yazilmaz()
    {
        ModeOn();
        var commandId = NewId();
        var (a, b) = (NewId(), NewId());

        _pending.Enqueue(commandId, PendingSalesCommandKinds.Cancel, "{\"v\":1}", [b, a, a], createdAt: 700)
            .Should().BeTrue();
        _pending.Enqueue(commandId, PendingSalesCommandKinds.Cancel, "{\"v\":2}", [NewId()], createdAt: 800)
            .Should().BeTrue("aynı niyetin yeniden kaydı: var olan satır yeter");

        var row = _pending.List().Should().ContainSingle().Subject;
        row.CommandId.Should().Be(commandId);
        row.Kind.Should().Be("cancel");
        row.PayloadJson.Should().Be("{\"v\":1}", "var olan kayıt değişmez");
        row.CreatedAt.Should().Be(700);
        row.Attempts.Should().Be(0);
        row.LabelIds.Should().BeEquivalentTo([a, b], "tekilleştirilmiş, yalnız ilk kaydın bağları");
        Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommandLabel").Should().Be(2);
    }

    [Fact]
    public void Mod_Offta_hicbir_satir_yazilmaz()
    {
        _pending.Enqueue(NewId(), PendingSalesCommandKinds.MarkPrinted, "{}", [NewId()], createdAt: 700)
            .Should().BeFalse("mod Off'ta (bugün her lisans) bekleyen komut doğmaz");

        Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommand").Should().Be(0);
        Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommandLabel").Should().Be(0);
        new SalesSyncStateRepository(_db).GetSeenMode().Should().Be(0, "W1'in görülen kip kuralı değişmedi");
    }

    [Fact]
    public void Gecersiz_tur_ya_da_bos_etiket_listesi_reddedilir()
    {
        ModeOn();
        var badKind = () => _pending.Enqueue(NewId(), "uncancel", "{}", [NewId()], 700);
        var noLabels = () => _pending.Enqueue(NewId(), PendingSalesCommandKinds.Cancel, "{}", [], 700);

        badKind.Should().Throw<ArgumentException>();
        noLabels.Should().Throw<ArgumentException>();
        Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommand").Should().Be(0);
    }

    [Fact]
    public void Liste_gonderim_sirasiyla_deneme_sayaci_ve_son_kayit_yazilir()
    {
        ModeOn();
        var (first, second) = (NewId(), NewId());
        _pending.Enqueue(second, PendingSalesCommandKinds.MarkPrinted, "{\"n\":2}", [NewId()], createdAt: 800);
        _pending.Enqueue(first, PendingSalesCommandKinds.Cancel, "{\"n\":1}", [NewId()], createdAt: 700);

        _pending.RecordAttempt(first, "Offline");
        _pending.RecordAttempt(first, "Stale", payloadJson: "{\"n\":1,\"fresh\":true}");

        var rows = _pending.List();
        rows.Select(r => r.CommandId).Should().Equal(first, second);
        rows[0].Attempts.Should().Be(2);
        rows[0].LastError.Should().Be("Stale");
        rows[0].PayloadJson.Should().Be("{\"n\":1,\"fresh\":true}", "son gönderilen niyet saklanır");
        rows[1].Attempts.Should().Be(0);
        _pending.Get(second)!.PayloadJson.Should().Be("{\"n\":2}");
        _pending.Get(NewId()).Should().BeNull();
    }

    [Fact]
    public void Silme_baglari_da_siler_ve_ortu_kalkar()
    {
        ModeOn();
        var session = Session(ended: true);
        var label = Label(session);
        var commandId = NewId();
        _pending.Enqueue(commandId, PendingSalesCommandKinds.Cancel, "{}", [label], 700);
        _pending.GetCoveredLabelIds([label, NewId()]).Should().Equal(label);

        _pending.Delete(commandId).Should().BeTrue();
        _pending.Delete(commandId).Should().BeFalse();

        Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommandLabel").Should().Be(0);
        _pending.GetCoveredLabelIds([label]).Should().BeEmpty();
        _labels.GetUnprintedBySession(session).Select(l => l.Id).Should().Equal(label);
    }

    [Fact]
    public void Ortulen_etiket_kuyrukta_kalinti_teklifinde_ve_baski_okumasinda_gorunmez_satiri_degismez()
    {
        ModeOn();
        var residualSession = Session(ended: true);
        var active = Session(ended: false);
        var covered = Label(residualSession, addedAt: 600);
        var free = Label(residualSession, addedAt: 610);
        var before = Scalar<string>("SELECT Id || '|' || Revision || '|' || IFNULL(SyncedAt, '-') FROM Label WHERE Id = @covered",
            new { covered });
        _pending.Enqueue(NewId(), PendingSalesCommandKinds.MarkPrinted, "{}", [covered], 700);

        var labelService = new LabelService(_labels, new CustomerService(_customers, _sessions, _labels, new Clock()), _db,
            new Clock());
        labelService.GetQueue(residualSession).Select(l => l.Id).Should().Equal(free);
        _labels.GetUnprintedOutsideSession(active).Select(l => l.Id).Should().Equal(free);
        _labels.GetPrintableByIds([covered, free]).Select(l => l.Id).Should().Equal(free);
        Scalar<string>("SELECT Id || '|' || Revision || '|' || IFNULL(SyncedAt, '-') FROM Label WHERE Id = @covered",
            new { covered }).Should().Be(before, "etiket satırına dokunulmaz");
    }

    [Fact]
    public void Baski_okumasi_verilen_sirayla_basilmis_ve_iptal_edilmis_satiri_dusurur()
    {
        var session = Session(ended: false);
        var (a, b, c, d) = (Label(session), Label(session), Label(session), Label(session));
        Exec("UPDATE Label SET PrintedAt = 650 WHERE Id = @b", new { b });
        Exec("UPDATE Label SET CancelledAt = 650 WHERE Id = @c", new { c });

        _labels.GetPrintableByIds([d, c, b, a, d]).Select(l => l.Id).Should().Equal(d, a);
        _labels.GetPrintableByIds([]).Should().BeEmpty();
    }

    [Fact]
    public void Basis_kimligi_ve_sirasi_disaridan_verilir_atlanan_satir_konumunu_korur()
    {
        var session = Session(ended: false);
        var (a, b, c) = (Label(session), Label(session), Label(session));
        Exec("UPDATE Label SET CancelledAt = 650 WHERE Id = @b", new { b });
        var labelService = new LabelService(_labels, new CustomerService(_customers, _sessions, _labels, new Clock()), _db,
            new Clock());
        var batchId = LabelService.NewPrintBatchId();

        labelService.MarkPrintedAndRecord([a, b, c], batchId, [2, 3, 5]);

        var rows = new Dictionary<string, (string? Batch, long? Seq, long? PrintedAt)>();
        using (var conn = _db.Open())
            foreach (var r in conn.Query<(string Id, string? PrintBatchId, long? PrintSeq, long? PrintedAt)>(
                         "SELECT Id, PrintBatchId, PrintSeq, PrintedAt FROM Label"))
                rows[r.Id] = (r.PrintBatchId, r.PrintSeq, r.PrintedAt);
        rows[a].Should().Be((batchId, 2L, 1_000L));
        rows[b].Should().Be(((string?)null, (long?)null, (long?)null), "iptal edilmiş satır basılmaz");
        rows[c].Should().Be((batchId, 5L, 1_000L));
        _customers.GetById("musteri-1")!.TotalLabelsPrinted.Should().Be(2);

        var mismatch = () => labelService.MarkPrintedAndRecord([a], batchId, [1, 2]);
        mismatch.Should().Throw<ArgumentException>();
    }
}
