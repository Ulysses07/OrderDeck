using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>U12: taşınmış (silinmiş) Id ile gelen her yazım asıl kayda iner — yazımla aynı ifadede.</summary>
public sealed class CustomerIdResolutionTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly CustomerSyncRepository _sync;
    private const long Now = 1_791_000_000;

    public CustomerIdResolutionTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _sync = new CustomerSyncRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private string Local(string username)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, "tiktok", username, "takma", null, 100, 200,
            false, null, null, 0, 0m, null, null, null));
        return id;
    }

    /// <summary>Kopya → asıl kayıt taşındı; pencereler, listeler ve uçuştaki akışlar hâlâ kopyanın Id'sini tutuyor.</summary>
    private (string Stale, string Canonical) Rekeyed()
    {
        var stale = Local("ayse");
        var canonical = Local("Ayse");
        _sync.RekeyToLocal(stale, canonical, pushedThroughSeq: long.MaxValue, nowUnix: Now).Should().Be(RekeyResult.Rekeyed);
        return (stale, canonical);
    }

    private int Count(string sql, object? p = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>(sql, p);
    }

    [Fact]
    public void Id_ile_yazan_yollar_tasinmis_Idyi_asil_kayda_cozer()
    {
        var (stale, canonical) = Rekeyed();
        var phone = TestPhone.NewE164();

        _customers.UpdateNotes(stale, "kapıda");
        _customers.UpdatePhone(stale, phone).Should().Be(1, "0 dönseydi pencere 'silme talebi' hatası gösterirdi");
        _customers.SetRecipientPaysActive(stale, true);
        _customers.UpdateBlacklist(stale, true, "ödemedi", 5000);
        _customers.IncrementLabelStats(stale, 1, 10m, lastSeenAt: 300);
        _customers.SetGroupId(stale, "g1");

        var c = _customers.GetById(canonical)!;
        c.Notes.Should().Be("kapıda");
        c.Phone.Should().Be(phone);
        c.RecipientPaysActive.Should().BeTrue();
        c.IsBlacklisted.Should().BeTrue();
        c.TotalLabelsPrinted.Should().Be(1);
        c.GroupId.Should().Be("g1");
        Count("SELECT COUNT(*) FROM Customer").Should().Be(1);
    }

    [Fact]
    public void ScrubPersonalData_tasinmis_Idyi_asil_kayda_cozer()
    {
        var (stale, canonical) = Rekeyed();

        _customers.ScrubPersonalData(stale).Should().Be(1);

        Count("SELECT COUNT(*) FROM Customer WHERE Id = @canonical AND PurgedAt IS NOT NULL", new { canonical })
            .Should().Be(1);
    }

    [Fact]
    public void GetById_ve_ResolveId_tasinmis_Idyi_guncel_kayda_cozer()
    {
        var (stale, canonical) = Rekeyed();

        _customers.GetById(stale)!.Id.Should().Be(canonical, "açık pencere eski Id'yle yeniden yüklenince asıl kaydı göstersin");
        _customers.ResolveId(stale).Should().Be(canonical);
        _customers.ResolveId(canonical).Should().Be(canonical);
        _customers.ResolveId("hic-yok").Should().Be("hic-yok");
    }

    [Fact]
    public void ResolveIds_ve_AnyRedirectedTo_tek_sorguda_zinciri_cozer()
    {
        // İki taşıma: ilk → ara, ara → asıl. Yönlendirme zinciri yazımda kısaltılır.
        var first = Local("ayse");
        var middle = Local("AYSE");
        var canonical = Local("Ayse");
        _sync.RekeyToLocal(first, middle, pushedThroughSeq: long.MaxValue, nowUnix: Now).Should().Be(RekeyResult.Rekeyed);
        _sync.RekeyToLocal(middle, canonical, pushedThroughSeq: long.MaxValue, nowUnix: Now).Should().Be(RekeyResult.Rekeyed);

        var map = _customers.ResolveIds(new[] { first, middle, canonical, "hic-yok", first });

        map.Should().HaveCount(4);
        map[first].Should().Be(canonical);
        map[middle].Should().Be(canonical);
        map[canonical].Should().Be(canonical);
        map["hic-yok"].Should().Be("hic-yok");
        _customers.AnyRedirectedTo(canonical, new[] { "hic-yok", first }).Should().BeTrue();
        _customers.AnyRedirectedTo(canonical, new[] { "hic-yok", canonical }).Should().BeFalse("canlı Id yönlendirme kaynağı değildir");
        _customers.AnyRedirectedTo(canonical, Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void MergeIntoGroup_tasinmis_Idleri_cozer()
    {
        var (stale, canonical) = Rekeyed();
        var other = Local("mehmet");

        var groupId = _customers.MergeIntoGroup(new[] { stale, other });

        _customers.GetById(canonical)!.GroupId.Should().Be(groupId);
        _customers.GetById(other)!.GroupId.Should().Be(groupId);
    }

    [Fact]
    public void MergeIntoGroup_tek_kisiye_coken_secimde_grup_acmaz()
    {
        // Arama listesinde iki kart seçildi; pencere açıkken biri ötekine taşındı.
        var (stale, canonical) = Rekeyed();
        var seqBefore = _customers.GetById(canonical)!.SyncSeq;

        var groupId = _customers.MergeIntoGroup(new[] { stale, canonical });

        groupId.Should().BeNull("kişinin grubu yoktu — tek üyeli grup açılıp gönderilmez");
        var c = _customers.GetById(canonical)!;
        c.GroupId.Should().BeNull();
        c.SyncSeq.Should().Be(seqBefore, "hiçbir şey yazılmadı");
    }

    private void SeedGiveaway()
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
        c.Execute(@"INSERT INTO Giveaway (Id, SessionId, Keyword, DurationSeconds, WinnerCount, RandomSeed, StartedAt)
                    VALUES ('g1', 's1', 'k', 60, 1, 'seed', 1)");
    }

    [Fact]
    public void Sohbet_katilimi_tasinmis_Idyi_cozer_ve_ayni_kisiyi_ikinci_kez_almaz()
    {
        // Üretim yolu (GiveawayService → TryAddParticipant). Çözülmeseydi FK hatası (787)
        // DrainPendingChat'ten kaçar, sohbet partisinin kalanı düşerdi.
        var (stale, canonical) = Rekeyed();
        SeedGiveaway();
        var repo = new GiveawayRepository(_db);

        repo.TryAddParticipant(new GiveawayParticipant(Guid.NewGuid().ToString("N"), "g1", stale, "tiktok", "ayse", 1, false))
            .Should().BeTrue();
        repo.TryAddParticipant(new GiveawayParticipant(Guid.NewGuid().ToString("N"), "g1", canonical, "tiktok", "Ayse", 2, false))
            .Should().BeFalse("aynı kişi farklı yazılışla ikinci şans almaz");

        repo.GetParticipants("g1").Should().ContainSingle().Which.CustomerId.Should().Be(canonical);
    }

    [Fact]
    public void Etiket_cekilis_ve_kargo_yazimlari_tasinmis_Idyi_cozer()
    {
        var (stale, canonical) = Rekeyed();
        SeedGiveaway();
        var labels = new LabelRepository(_db);
        var shipments = new ShipmentRepository(_db);
        var shipmentService = new ShipmentService(shipments, labels, () => new AppSettings());

        // FK açık: çözülmeseydi INSERT "FOREIGN KEY constraint failed" ile düşerdi (kayıp satış).
        labels.Insert(new Label(Guid.NewGuid().ToString("N"), "s1", stale, "tiktok", "ayse", "A1", null, 10m, 1, null));
        new GiveawayRepository(_db).AddParticipant(
            new GiveawayParticipant(Guid.NewGuid().ToString("N"), "g1", stale, "tiktok", "ayse", 1, false));
        var shipment = shipmentService.GetOrCreateOpenShipment(stale);

        Count("SELECT COUNT(*) FROM Label WHERE CustomerId = @canonical", new { canonical }).Should().Be(1);
        Count("SELECT COUNT(*) FROM GiveawayParticipant WHERE CustomerId = @canonical", new { canonical }).Should().Be(1);
        shipments.GetById(shipment.Id)!.CustomerId.Should().Be(canonical, "INSERT çözdü — kargo silinmiş Id'de öksüz kalmaz");
        labels.GetUnattachedByCustomer(stale).Should().ContainSingle();
        shipments.GetOpenByCustomer(stale)!.Id.Should().Be(shipment.Id);
        shipments.CountOpenByCustomer(stale).Should().Be(1);
    }

    [Fact]
    public void Iki_acik_kargoda_secim_belirlenimli_ve_sayi_bilinir()
    {
        // Taşıma kargoları birleştirmez — kişi iki açık kargoyla kalabilir.
        var id = Local("ayse");
        var shipments = new ShipmentRepository(_db);
        var a = new Shipment("a" + Guid.NewGuid().ToString("N"), id, ShipmentStatus.Pending, 100, null, null, 0m);
        var b = new Shipment("b" + Guid.NewGuid().ToString("N"), id, ShipmentStatus.Pending, 100, null, null, 0m);
        shipments.Insert(a);
        shipments.Insert(b);

        shipments.CountOpenByCustomer(id).Should().Be(2);
        shipments.GetOpenByCustomer(id)!.Id.Should().Be(b.Id, "aynı CreatedAt'te Id DESC — her bilgisayarda aynı dosya seçilir");
    }
}
