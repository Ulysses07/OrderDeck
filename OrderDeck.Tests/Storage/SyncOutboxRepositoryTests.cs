using System;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Faz 0 (D1): gönderilmemiş kayıt sayısı ve dikkat sayacı. SyncSeq ardışık değildir: imleç
/// değeri sabit yazılmaz, satırın kendi SyncSeq'i veritabanından okunur; sayım bir fark değil
/// <c>COUNT(*)</c>'tır.
/// </summary>
public sealed class SyncOutboxRepositoryTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly SyncOutboxRepository _outbox;

    public SyncOutboxRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _outbox = new SyncOutboxRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private string Local(string username)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, "tiktok", username, "Örnek Müşteri", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        return id;
    }

    [Fact]
    public void Gonderilmemis_musteri_sayilir_imlecin_gerisindekiler_sayilmaz()
    {
        Local("ornek_musteri_1");
        var second = Local("ornek_musteri_2");

        _outbox.CountPending(customerPushCursor: 0).Should().Be(2);
        _outbox.CountPending(customerPushCursor: _customers.GetById(second)!.SyncSeq).Should().Be(0,
            "imlecin gerisindeki (gönderilmiş) satır sayılmaz");
    }

    [Fact]
    public void Silinmis_musteri_hic_gonderilmez_bekleyen_sayilmaz()
    {
        var first = Local("ornek_musteri_1");
        var cursor = _customers.GetById(first)!.SyncSeq;
        Local("silinecek_musteri");
        _outbox.CountPending(cursor).Should().Be(1);

        _customers.RecordPurge("tiktok", "silinecek_musteri", purgedAtUnix: 1_791_000_000);

        _outbox.CountPending(cursor).Should().Be(0,
            "gönderim PurgedAt dolu satırı atlar (C6); imleç onu bir sonraki turda geçene dek sayılmamalı");
    }

    [Fact]
    public void Diger_tablolarin_gonderilmemis_satirlari_da_sayilir()
    {
        var sessions = new SessionRepository(_db);
        sessions.Insert(new StreamSession("yayin-1", "Örnek yayın", 100, null, new[] { "tiktok" }, null));
        _outbox.CountPending(customerPushCursor: 0).Should().Be(1);

        sessions.MarkSynced("yayin-1", syncedAt: 200, revision: sessions.GetById("yayin-1")!.Revision);

        _outbox.CountPending(customerPushCursor: 0).Should().Be(0);
    }

    [Fact]
    public void Dikkat_sayaci_atlanan_akis_ogesini_ve_acik_anahtarli_miras_isi_sayar()
    {
        using (var c = _db.Open())
        {
            c.Execute("INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, FirstFailedAt, SkippedAt) VALUES ('x', 1, 5, 1, 2)");
            c.Execute("INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, FirstFailedAt) VALUES ('y', 1, 2, 1)");
            c.Execute(@"INSERT INTO PaymentJob (Id, CustomerId, ScopeKey, ProductTotal, State, CreatedAt, UpdatedAt, ApplyKey, ClosedAt)
                        VALUES ('j1', 'c1', 'legacy:k1', '100', 'applied', 1, 1, 'k1', NULL),
                               ('j2', 'c1', 'legacy:j2', '100', 'created', 1, 1, NULL, NULL),
                               ('j3', 'c1', 'legacy:k3', '100', 'applied', 1, 1, 'k3', 2),
                               ('j4', 'c1', 'stream:s1', '100', 'applied', 1, 1, 'k4', NULL)");
        }

        _outbox.CountAttention().Should().Be(new SyncAttention(1, 1),
            "henüz atlanmamış deneme, anahtarsız (kapatılacak) ya da kapalı miras iş ve miras olmayan iş sayılmaz");
        _outbox.CountAttention().OpenLegacyPaymentJobs.Should().Be(
            new CustomerSyncRepository(_db, new CustomerBusySet()).CountOpenKeyedLegacyJobs(),
            "durum satırı akış servisinin günlüğe yazdığı ölçütle aynı işleri sayar (U8)");
    }

    [Fact]
    public void Dikkat_yoksa_Any_false()
    {
        _outbox.CountAttention().Should().Be(default(SyncAttention));
        _outbox.CountAttention().Any.Should().BeFalse();
        new SyncAttention(0, 1).Any.Should().BeTrue();
    }
}
