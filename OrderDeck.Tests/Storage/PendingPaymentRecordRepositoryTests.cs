using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Bekleyen ödeme kayıtları (spec 2026-10-13 §3.2, §4.7, §6.4; W3-05): kayıt Id'sine göre idempotent kuyruğa alma
/// (yeniden yazım satıra dokunmaz), eskiden yeniye sıra, deneme sayacı ve son hata, tekli ve toplu silme, sayım. Kip
/// kapısı yok: mod Off'ta da yazılır (bugün her lisans Off).
/// </summary>
public sealed class PendingPaymentRecordRepositoryTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly PendingPaymentRecordRepository _repo;

    public PendingPaymentRecordRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _repo = new PendingPaymentRecordRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public void Enqueue_kayit_Idsine_gore_idempotent_ikinci_yazim_satira_dokunmaz()
    {
        var id = NewId();
        _repo.Enqueue(id, PendingPaymentRecordKinds.Finish, """{"v":1}""", 100).Should().BeTrue();
        _repo.RecordAttempt(id, "Offline");

        _repo.Enqueue(id, PendingPaymentRecordKinds.Finish, """{"v":2}""", 200).Should().BeFalse("aynı Id kuyrukta");

        var row = _repo.Get(id)!;
        row.PayloadJson.Should().Be("""{"v":1}""", "ilk yazılan kalır");
        row.CreatedAt.Should().Be(100);
        row.Attempts.Should().Be(1, "deneme sayacı sıfırlanmaz");
        row.LastError.Should().Be("Offline");
        _repo.Count().Should().Be(1);
    }

    [Fact]
    public void Mod_Offta_yazilir_kip_kapisi_yok()
    {
        using (var c = _db.Open())
            c.ExecuteScalar<int>("SELECT Mode FROM SalesSyncState WHERE Id = 1").Should().Be(0, "ön koşul: mod Off");

        _repo.Enqueue(NewId(), PendingPaymentRecordKinds.Record, "{}", 1).Should().BeTrue();

        _repo.Count().Should().Be(1);
    }

    [Fact]
    public void Bilinmeyen_tur_ve_bos_alanlar_reddedilir()
    {
        var unknown = () => _repo.Enqueue(NewId(), "cancel", "{}", 1);
        unknown.Should().Throw<ArgumentException>();
        var noId = () => _repo.Enqueue(" ", PendingPaymentRecordKinds.Finish, "{}", 1);
        noId.Should().Throw<ArgumentException>();
        var noPayload = () => _repo.Enqueue(NewId(), PendingPaymentRecordKinds.Finish, "", 1);
        noPayload.Should().Throw<ArgumentException>();
        _repo.Count().Should().Be(0);
    }

    [Fact]
    public void List_eskiden_yeniye_esit_anda_yazilis_sirasiyla()
    {
        var late = NewId();
        var first = NewId();
        var second = NewId();
        _repo.Enqueue(late, PendingPaymentRecordKinds.Record, "{}", 300);
        _repo.Enqueue(first, PendingPaymentRecordKinds.Finish, "{}", 100);
        _repo.Enqueue(second, PendingPaymentRecordKinds.Record, "{}", 100);

        _repo.List().Select(r => r.RecordId).Should().Equal(first, second, late);
        _repo.List().Select(r => r.Kind).Should().Equal(
            PendingPaymentRecordKinds.Finish, PendingPaymentRecordKinds.Record, PendingPaymentRecordKinds.Record);
    }

    [Fact]
    public void RecordAttempt_sayaci_artirir_son_hatayi_yazar_uzun_hatayi_kirpar_toplu_tek_islemde()
    {
        var a = NewId();
        var b = NewId();
        var untouched = NewId();
        _repo.Enqueue(a, PendingPaymentRecordKinds.Record, "{}", 1);
        _repo.Enqueue(b, PendingPaymentRecordKinds.Record, "{}", 2);
        _repo.Enqueue(untouched, PendingPaymentRecordKinds.Record, "{}", 3);

        _repo.RecordAttempt([a, b, a], "Failed HTTP 500");
        _repo.RecordAttempt(a, new string('x', 500));

        _repo.Get(a)!.Attempts.Should().Be(2, "aynı partide yinelenen Id bir kez sayılır");
        _repo.Get(a)!.LastError.Should().HaveLength(200);
        _repo.Get(b)!.Attempts.Should().Be(1);
        _repo.Get(b)!.LastError.Should().Be("Failed HTTP 500");
        _repo.Get(untouched)!.Attempts.Should().Be(0);
        _repo.Get(untouched)!.LastError.Should().BeNull();
    }

    [Fact]
    public void Delete_tekli_ve_toplu_yoksa_false_ve_sifir()
    {
        var a = NewId();
        var b = NewId();
        var c = NewId();
        _repo.Enqueue(a, PendingPaymentRecordKinds.Finish, "{}", 1);
        _repo.Enqueue(b, PendingPaymentRecordKinds.Record, "{}", 2);
        _repo.Enqueue(c, PendingPaymentRecordKinds.Record, "{}", 3);

        _repo.Delete(a).Should().BeTrue();
        _repo.Delete(a).Should().BeFalse("satır yok");
        _repo.Delete([b, c, NewId()]).Should().Be(2);
        _repo.Delete(Array.Empty<string>()).Should().Be(0);

        _repo.Count().Should().Be(0);
        _repo.List().Should().BeEmpty();
        _repo.Get(b).Should().BeNull();
    }

    [Fact]
    public void Acilis_birikimi_kuyrugu_sayar()
    {
        _repo.Enqueue(NewId(), PendingPaymentRecordKinds.Finish, "{}", 1);
        _repo.Enqueue(NewId(), PendingPaymentRecordKinds.Record, "{}", 2);

        new SalesSyncStateRepository(_db).GetOpeningBacklog(0, 0).PaymentRecords.Should().Be(2,
            "mod On açılışının 1. adımı bu kuyruğun boşalmasını bekler (§8.4-1)");
    }
}
