using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// Sahip kararı Y5'in mod Off yerel yolu (spec 2026-10-13 §7.2 Off satırları, §7.4, M12; W1): dosyaya bağlandıktan
/// sonra iptal edilen etiket dosya tutarından ve eşik havuzundan düşer, iptali geri almak geri ekler; bağ
/// (<c>Label.ShipmentId</c>, "Ödendi" rozetinin kaynağı) hiç değişmez. Kargoya verilmiş dosya donmuştur, geçici yedek
/// dosyada hiç yoktur, başka bilgisayarın dosyası atlanır, mod On'da blok atlanır. Fiyat düzeltmesinin dosya bloğu
/// aynı yoldan geçer; <c>Classify</c>'ın yeni sınıfı "başka bilgisayarın dosyasında".
/// </summary>
public sealed class LabelServiceShipmentY5Tests : IDisposable
{
    private const string SessionId = "s1";

    private readonly InMemorySqlite _db = new();
    private readonly LabelRepository _labels;
    private readonly ShipmentRepository _shipments;
    private readonly LabelService _svc;
    private readonly ShipmentService _shipping;
    private readonly AppSettings _settings = new();

    public LabelServiceShipmentY5Tests()
    {
        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        new SessionRepository(_db).Insert(new StreamSession(SessionId, null, 1000, null, new[] { "instagram" }, null));
        _labels = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        var customers = new CustomerRepository(_db);
        _svc = new LabelService(_labels,
            new CustomerService(customers, new SessionRepository(_db), _labels, clock.Object), _db, clock.Object,
            _shipments);
        // §7.2'nin sayısal örneği: eşik 500 TL, kargo ücreti 60 TL.
        _settings.Shipping.FreeShippingThreshold = 500m;
        _settings.Shipping.ShippingFee = 60m;
        _shipping = new ShipmentService(_shipments, _labels, () => _settings, () => 1000L);
        // Y5'in tabanı açılışta türetilmiş say (ShipmentAmountDerivation); damgasız hâl ayrıca sınanır.
        SetDerivedAt(900);
    }

    public void Dispose() => _db.Dispose();

    private void SetDerivedAt(long? at)
    {
        using var c = _db.Open();
        c.Execute("UPDATE SalesSyncState SET ShipmentsDerivedAt = @at WHERE Id = 1", new { at });
    }

    private Label Add(string user, decimal price, string code = "A1")
        => _svc.Add(SessionId, new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, user, user, null,
            "aldım", 1000, Array.Empty<string>()), price, code);

    /// <summary>Basılmış etiket, müşterinin açık dosyasına dekont akışıyla (<see cref="ShipmentService.AttachLabels"/>)
    /// bağlı.</summary>
    private Label Attached(string user, decimal price, string code = "A1")
    {
        var label = Add(user, price, code);
        _svc.MarkPrintedAndRecord(new[] { label.Id });
        var file = _shipping.GetOrCreateOpenShipment(label.CustomerId);
        _shipping.AttachLabels(file.Id, new[] { label.Id });
        return _labels.GetById(label.Id)!;
    }

    private decimal Amount(string shipmentId) => _shipments.GetById(shipmentId)!.CumulativeAmount;

    private void SetLink(string labelId, string shipmentId)
    {
        using var c = _db.Open();
        c.Execute("UPDATE Label SET ShipmentId = @shipmentId WHERE Id = @labelId", new { labelId, shipmentId });
    }

    private void SetMode(int mode)
    {
        using var c = _db.Open();
        c.Execute("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
    }

    // ── §7.2 tablosunun Off satırları ──────────────────────────────────

    [Theory]
    [InlineData(ShipmentStatus.Pending)]
    [InlineData(ShipmentStatus.Held)]
    [InlineData(ShipmentStatus.RecipientPays)]
    public void Iptal_acik_dosyanin_tutarindan_fiyati_dusurur_geri_alma_geri_ekler_bag_kalir(ShipmentStatus status)
    {
        var k1 = Attached("@ornek_alici", 300m);
        var k5 = Attached("@ornek_alici", 250m, "K5");
        var file = k1.ShipmentId!;
        k5.ShipmentId.Should().Be(file, "ön koşul: iki etiket aynı dosyada");
        if (status == ShipmentStatus.Held) _shipping.ApplyDecision(file, ShipmentDecision.Hold);
        if (status == ShipmentStatus.RecipientPays) _shipping.ApplyDecision(file, ShipmentDecision.RecipientPays);
        _shipments.GetById(file)!.Status.Should().Be(status);
        Amount(file).Should().Be(550m);

        _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);

        Amount(file).Should().Be(300m, "iptal edilen etiketin fiyatı dosyadan düşer");
        _labels.GetById(k5.Id)!.ShipmentId.Should().Be(file, "iptal bağı çözmez — 'Ödendi' rozetinin kaynağı");

        _svc.Uncancel(new[] { k5.Id });

        Amount(file).Should().Be(550m, "geri alma tutarı eski hâline döndürür");
        _labels.GetById(k5.Id)!.ShipmentId.Should().Be(file);
        _labels.GetPrintedBySession(SessionId).Single(r => r.Label.Id == k5.Id).Label.ShipmentId
            .Should().Be(file, "Basılanlar'daki 'Ödendi' rozeti bu bağdan okunur");
    }

    [Fact]
    public void Kargoya_verilmis_dosyanin_tutari_iptalde_ve_geri_almada_degismez()
    {
        var k1 = Attached("@ornek_alici", 300m);
        var k5 = Attached("@ornek_alici", 250m, "K5");
        var file = k1.ShipmentId!;
        _shipping.ApplyDecision(file, ShipmentDecision.ShipNow);
        var revision = _shipments.GetById(file)!.Revision;

        _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);
        Amount(file).Should().Be(550m, "kargoya verilmiş dosyanın tutarı donmuştur");

        _svc.Uncancel(new[] { k5.Id });
        Amount(file).Should().Be(550m);
        _shipments.GetById(file)!.Revision.Should().Be(revision, "dosya yazılmadı");
        _labels.GetById(k5.Id)!.ShipmentId.Should().Be(file, "bağ kalır");
    }

    [Fact]
    public void Gecici_yedek_dosya_tutarini_degistirmez()
    {
        var parent = Attached("@asil", 300m);
        var file = parent.ShipmentId!;
        var backup = _svc.AddBackup(parent.Id, "instagram", "@yedek", "Yedek", "ben de");
        _svc.MarkPrintedAndRecord(new[] { backup.Id });
        // Yedek dosyaya dekont akışıyla hiç bağlanmaz (bağlanmamış etiket süzgeci); bağlıysa da dosya tutarında
        // değildir (LabelRules.DerivedShipmentAmount).
        SetLink(backup.Id, file);

        _svc.Cancel(new[] { backup.Id }, CancelReasonCodes.BackupRemoved);
        Amount(file).Should().Be(300m);
        _svc.Uncancel(new[] { backup.Id });
        Amount(file).Should().Be(300m);
        _svc.RemoveBackup(backup.Id);
        Amount(file).Should().Be(300m);
    }

    [Fact]
    public void Esik_havuzu_iptal_ve_geri_almayi_izler_ornek_7_2()
    {
        // §7.2 sayısal örneği, eşik 500 TL: F1 = 300 (beklet), K5 250 bağlanır → 550 ≥ 500; K5 iptal → 300, eşiğe
        // 200 kalır, "ücretsiz kargo" teklif edilmez; geri alma → yine 550.
        var k1 = Attached("@ornek_alici", 300m);
        var f1 = k1.ShipmentId!;
        _shipping.ApplyDecision(f1, ShipmentDecision.Hold);
        var k5 = Attached("@ornek_alici", 250m, "K5");
        k5.ShipmentId.Should().Be(f1);

        var reached = _shipping.EvaluateAfterPayment(k1.CustomerId, allLabelsPaid: true);
        reached.PooledAmount.Should().Be(550m);
        reached.ThresholdReached.Should().BeTrue();

        _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);
        var below = _shipping.EvaluateAfterPayment(k1.CustomerId, allLabelsPaid: true);
        below.PooledAmount.Should().Be(300m);
        below.ThresholdReached.Should().BeFalse("eşiğin altına inildi");
        below.AmountToThreshold.Should().Be(200m);

        _svc.Uncancel(new[] { k5.Id });
        var again = _shipping.EvaluateAfterPayment(k1.CustomerId, allLabelsPaid: true);
        again.PooledAmount.Should().Be(550m);
        again.ThresholdReached.Should().BeTrue();
    }

    [Fact]
    public void Iptalin_dosya_degisikligi_gonderilir_revision_tam_bir_kez_artar()
    {
        var k1 = Attached("@a", 300m);
        var k5 = Attached("@a", 250m, "K5");
        var file = k1.ShipmentId!;
        _shipments.MarkSynced(file, 2000, _shipments.GetById(file)!.Revision);
        _shipments.GetUnsynced().Should().BeEmpty("ön koşul: dosya gönderilmiş");
        var revision = _shipments.GetById(file)!.Revision;

        _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);

        var afterCancel = _shipments.GetById(file)!;
        afterCancel.SyncedAt.Should().BeNull("düşen tutar shipments/sync ile gitmeli");
        afterCancel.Revision.Should().Be(revision + 1, "tam bir kez: yazım kendisi artırır, tetikleyici etkisiz");
        _shipments.GetUnsynced().Should().ContainSingle().Which.Id.Should().Be(file);

        _shipments.MarkSynced(file, 2100, afterCancel.Revision);
        _svc.Uncancel(new[] { k5.Id });
        var afterUncancel = _shipments.GetById(file)!;
        afterUncancel.SyncedAt.Should().BeNull();
        afterUncancel.Revision.Should().Be(revision + 2);
    }

    [Fact]
    public void Etiket_ve_dosya_ayni_pakette_dosya_yazimi_patlarsa_iptal_de_geri_alinir()
    {
        var k1 = Attached("@a", 300m);
        var k5 = Attached("@a", 250m, "K5");
        var file = k1.ShipmentId!;
        using (var c = _db.Open())
            c.Execute("CREATE TRIGGER fail_shipment BEFORE UPDATE ON Shipment BEGIN SELECT RAISE(ABORT, 'disk dolu'); END;");

        var act = () => _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);

        act.Should().Throw<Exception>();
        _labels.GetById(k5.Id)!.CancelledAt.Should().BeNull("etiket ve dosya tek pakette: yarım iptal kalmaz");
        Amount(file).Should().Be(550m);
    }

    // ── Fark tabanı yok: açılış türetmesi düştü (§7.4 "Fark tabanı") ───

    [Fact]
    public void Turetme_tabani_yokken_iptal_ve_geri_alma_dosya_tutarina_dokunmaz_sonraki_turetme_kurar()
    {
        // ≤0.9.10'da iptal edilmiş 150 TL'lik etiket açık dosyaya bağlı kalmış; o sürümün Cancel'ı dosyaya
        // dokunmadığı için F = 400 (gerçek 250). Açılış türetmesi düştü: ShipmentAmountsBoot yalnız günlüğe yazar,
        // damga boş kalır, uygulama sürer.
        var k1 = Attached("@ornek_alici", 250m);
        var k5 = Attached("@ornek_alici", 150m, "K5");
        var file = k1.ShipmentId!;
        k5.ShipmentId.Should().Be(file, "ön koşul: iki etiket aynı dosyada");
        Amount(file).Should().Be(400m);
        _labels.MarkCancelled(new[] { k5.Id }, 1000, CancelReasonCodes.Customer);
        SetDerivedAt(null);
        var revision = _shipments.GetById(file)!.Revision;

        _svc.Uncancel(new[] { k5.Id });
        Amount(file).Should().Be(400m,
            "türetilmemiş tabana fark eklenmez: 550 olsaydı 500 TL'lik ücretsiz kargo eşiğini yanlışlıkla geçerdi");

        _svc.Cancel(new[] { k1.Id }, CancelReasonCodes.Customer);
        Amount(file).Should().Be(400m);
        _shipments.GetById(file)!.Revision.Should().Be(revision, "dosya yazılmadı");
        _labels.GetById(k1.Id)!.ShipmentId.Should().Be(file, "bağ yine değişmez");

        // Sonraki başarılı türetme tutarı etiketlerden kurar: K1 iptalli, K5 geri alınmış → 150.
        ShipmentAmountDerivation.DeriveAllOnce(_db, 2000).Should().NotBeNull();
        Amount(file).Should().Be(150m);

        // Damga yazıldı: Y5 bugünkü gibi farkla çalışır.
        _svc.Uncancel(new[] { k1.Id });
        Amount(file).Should().Be(400m);
    }

    // ── Bağlı dosyası yerelde olmayan etiket (§7.4, §15-57) ────────────

    [Fact]
    public void Bagli_dosyasi_yerelde_olmayan_etiket_iptal_edilir_ve_geri_alinir_istisna_yok()
    {
        var keep = Attached("@a", 100m);
        var label = Add("@b", 250m);
        _svc.MarkPrintedAndRecord(new[] { label.Id });
        var foreignFile = Guid.NewGuid().ToString("N");
        SetLink(label.Id, foreignFile);

        var cancel = () => _svc.Cancel(new[] { label.Id }, CancelReasonCodes.Customer);
        cancel.Should().NotThrow();
        _labels.GetById(label.Id)!.CancelledAt.Should().NotBeNull();

        var uncancel = () => _svc.Uncancel(new[] { label.Id });
        uncancel.Should().NotThrow();
        var after = _labels.GetById(label.Id)!;
        after.CancelledAt.Should().BeNull();
        after.ShipmentId.Should().Be(foreignFile, "bağ değişmez");
        Amount(keep.ShipmentId!).Should().Be(100m, "başka dosyaya dokunulmaz");
    }

    [Fact]
    public void Classify_yerelde_olmayan_dosyadaki_etiketi_ayri_listeler_ve_fiyatini_duzeltmez()
    {
        var elsewhere = Add("@baska", 250m);
        var open = Attached("@a", 250m);
        var plain = Add("@c", 250m);
        _svc.MarkPrintedAndRecord(new[] { elsewhere.Id });
        SetLink(elsewhere.Id, Guid.NewGuid().ToString("N"));

        var plan = _svc.PlanPriceCorrection(new[] { elsewhere.Id, open.Id, plain.Id });

        plan.Elsewhere.Select(l => l.Id).Should().Equal(elsewhere.Id);
        plan.Correctable.Select(l => l.Id).Should().BeEquivalentTo(new[] { open.Id, plain.Id });
        plan.Shipped.Should().BeEmpty();
        plan.PriceGroups.Should().Equal(new PriceGroup(250m, 2));

        var result = _svc.CorrectPrices(new[] { elsewhere.Id, open.Id, plain.Id }, 205m);

        _labels.GetById(elsewhere.Id)!.Price.Should().Be(250m, "dosyanın durumu bilinmiyor (kargoya verilmiş olabilir)");
        result.Elsewhere.Select(l => l.Id).Should().Equal(elsewhere.Id);
        result.Corrected.Select(c => c.Before.Id).Should().BeEquivalentTo(new[] { open.Id, plain.Id });
        result.ShipmentDeltas.Should().BeEquivalentTo(
            new System.Collections.Generic.Dictionary<string, decimal> { [open.ShipmentId!] = -45m });
        Amount(open.ShipmentId!).Should().Be(205m);
    }

    [Fact]
    public void Kod_fiyati_baska_bilgisayarin_dosyasindakini_kargoya_verilmis_gibi_yedek_kaynak_sayar()
    {
        // Ortak kural bilinmeyen dosyayı düzeltilebilir sayar (sunucuda her dosya bilinir); masaüstünde o etiket
        // düzeltilemez, kodun fiyat kaynağında kargoya verilmiş gibi durur.
        var elsewhere = Add("@baska", 100m);
        SetLink(elsewhere.Id, Guid.NewGuid().ToString("N"));
        var shipped = Attached("@gitti", 90m);
        _shipping.ApplyDecision(shipped.ShipmentId!, ShipmentDecision.ShipNow);

        var onlyUnchangeable = _svc.GetCodePrice(SessionId, "A1")!;
        onlyUnchangeable.Price.Should().Be(90m,
            "düzeltilebilir yok: kaynak değişmeyenler (yazılış sırasıyla 100, 90 → eşitlikte en son yazılan)");
        onlyUnchangeable.Correctable.Should().Be(0);
        onlyUnchangeable.Shipped.Should().Be(1, "başka bilgisayarın dosyası kargoya verilmiş sayılmaz");

        Add("@yeni", 120m);
        _svc.GetCodePrice(SessionId, "A1")!.Price.Should().Be(120m, "düzeltilebilir etiket kaynağı belirler");
    }

    [Fact]
    public void Fiyat_duzeltmede_dosya_bu_arada_kargoya_verildiyse_paket_geri_sarilir()
    {
        var label = Attached("@a", 250m);
        var file = label.ShipmentId!;
        // Planla yazım arasında dosya kargoya verilir: etiketin fiyatı yazılırken aynı işlemde dosya Shipped olur.
        using (var c = _db.Open())
            c.Execute(@"CREATE TRIGGER race_ship AFTER UPDATE OF Price ON Label
                        BEGIN UPDATE Shipment SET Status = 'Shipped', ShippedAt = 1500 WHERE Id = new.ShipmentId; END;");

        var act = () => _svc.CorrectPrices(new[] { label.Id }, 205m);

        act.Should().Throw<PriceCorrectionConflictException>().WithMessage("*kargoya verildi*");
        _labels.GetById(label.Id)!.Price.Should().Be(250m, "paket geri sarıldı");
        var s = _shipments.GetById(file)!;
        s.Status.Should().Be(ShipmentStatus.Pending);
        s.CumulativeAmount.Should().Be(250m);
    }

    [Fact]
    public void Fiyat_duzeltmede_dosya_bu_arada_yerelden_kalktiysa_atlanir_istisna_yok()
    {
        var label = Attached("@a", 250m);
        // Planla yazım arasında dosya yerelden kalkar (W2'nin disable turu başka bilgisayarın dosyasını siler).
        using (var c = _db.Open())
            c.Execute(@"CREATE TRIGGER race_delete AFTER UPDATE OF Price ON Label
                        BEGIN DELETE FROM Shipment WHERE Id = new.ShipmentId; END;");

        var result = _svc.CorrectPrices(new[] { label.Id }, 205m);

        _labels.GetById(label.Id)!.Price.Should().Be(205m);
        result.Corrected.Should().ContainSingle();
        result.ShipmentDeltas.Should().BeEmpty("yazılan dosya farkı yok");
    }

    // ── Mod On: blok atlanır, sunucu türetir (§5.1-a) ──────────────────

    [Fact]
    public void Mod_1de_iptal_geri_alma_ve_fiyat_duzeltme_dosyaya_dokunmaz()
    {
        var k1 = Attached("@a", 300m);
        var k5 = Attached("@a", 250m, "K5");
        var file = k1.ShipmentId!;
        var before = _shipments.GetById(file)!;
        SetMode(1);

        var cancel = () => _svc.Cancel(new[] { k5.Id }, CancelReasonCodes.Customer);
        cancel.Should().NotThrow("TR_Shipment_ServerOwned_U'ya varılmamalı");
        _labels.GetById(k5.Id)!.CancelledAt.Should().NotBeNull();

        var uncancel = () => _svc.Uncancel(new[] { k5.Id });
        uncancel.Should().NotThrow();

        var correct = () => _svc.CorrectPrices(new[] { k1.Id }, 280m);
        correct.Should().NotThrow();
        _labels.GetById(k1.Id)!.Price.Should().Be(280m);

        var after = _shipments.GetById(file)!;
        after.CumulativeAmount.Should().Be(before.CumulativeAmount, "dosya tutarını mod On'da sunucu türetir");
        after.Revision.Should().Be(before.Revision);
    }
}
