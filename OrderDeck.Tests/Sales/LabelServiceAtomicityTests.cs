using System;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// Etiket damgası ile müşteri toplamı aynı pakette mi yazılıyor?
///
/// Bu iş akışlarının her biri iki ayrı tabloya yazıyor: Label (basıldı /
/// iptal edildi damgası) ve Customer (ciro + adet toplamı). İkinci yazma
/// patlarsa birincisi de geri gitmek zorunda — yoksa yerel veritabanında
/// kalıcı olarak yanlış bir tablo kalıyor: operatörün gördüğü ciro ile
/// etiketlerin toplamı birbirini tutmuyor ve bunu fark etmenin bir yolu yok.
///
/// Hata gerçek: <c>RAISE(ABORT)</c> tetikleyicisi SQLite'ın kendi hata
/// yolundan geliyor, sahadaki "dosya kilitli / disk dolu" hatasıyla aynı
/// noktada. Sarmalayıcı sahte bağlantı yok — bağlantı, transaction ve veri
/// yolu baştan sona gerçek.
/// </summary>
public class LabelServiceAtomicityTests
{
    private static (LabelService Svc, LabelRepository Labels, CustomerRepository Customers,
                    InMemorySqlite Db) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var labelRepo = new LabelRepository(db);
        var customerSvc = new CustomerService(customerRepo, new SessionRepository(db), labelRepo, clock);
        var svc = new LabelService(labelRepo, customerSvc, db, clock);

        return (svc, labelRepo, customerRepo, db);
    }

    private static ChatMessage Msg(string username = "@ayse_y") =>
        new(Guid.NewGuid().ToString("N"),
            "instagram", null, username, "Ayşe", null, "MAVI XL aldım", 1000,
            Array.Empty<string>());

    /// <summary>Kurulum bittikten SONRA çağrılır: bundan sonraki her Customer
    /// güncellemesi patlar.</summary>
    private static void BreakCustomerWrites(InMemorySqlite db)
    {
        using var conn = db.Open();
        conn.Execute(@"CREATE TRIGGER fail_customer BEFORE UPDATE ON Customer
                       BEGIN SELECT RAISE(ABORT, 'disk dolu'); END;");
    }

    private static void BreakLabelWrites(InMemorySqlite db)
    {
        using var conn = db.Open();
        conn.Execute(@"CREATE TRIGGER fail_label BEFORE UPDATE ON Label
                       BEGIN SELECT RAISE(ABORT, 'disk dolu'); END;");
    }

    [Fact]
    public void Yazdirma_ciro_yazmasi_patlarsa_etiket_basildi_kalmaz()
    {
        var (svc, labels, customers, db) = Fx();
        using var _ = db;

        var lbl = svc.Add("s1", Msg(), price: 199m, code: "MAVI");
        BreakCustomerWrites(db);

        var act = () => svc.MarkPrintedAndRecord(new[] { lbl.Id });
        act.Should().Throw<Exception>();

        labels.GetById(lbl.Id)!.PrintedAt.Should().BeNull(
            "ciro yazılamadıysa etiket de basılmış sayılmamalı");
        var c = customers.GetById(lbl.CustomerId)!;
        c.TotalLabelsPrinted.Should().Be(0);
        c.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public void Iptal_ciro_yazmasi_patlarsa_etiket_iptal_kalmaz()
    {
        var (svc, labels, customers, db) = Fx();
        using var _ = db;

        var lbl = svc.Add("s1", Msg(), price: 199m, code: "MAVI");
        svc.MarkPrintedAndRecord(new[] { lbl.Id });
        BreakCustomerWrites(db);

        var act = () => svc.Cancel(new[] { lbl.Id }, "müşteri vazgeçti");
        act.Should().Throw<Exception>();

        labels.GetById(lbl.Id)!.CancelledAt.Should().BeNull(
            "ciro düşülemediyse etiket de iptal edilmiş sayılmamalı");
        var c = customers.GetById(lbl.CustomerId)!;
        c.TotalLabelsPrinted.Should().Be(1, "satış hâlâ ayakta");
        c.TotalAmount.Should().Be(199m);
    }

    [Fact]
    public void Iptal_geri_alma_etiket_yazmasi_patlarsa_ciro_iki_kez_yazilmaz()
    {
        var (svc, labels, customers, db) = Fx();
        using var _ = db;

        var lbl = svc.Add("s1", Msg(), price: 199m, code: "MAVI");
        svc.MarkPrintedAndRecord(new[] { lbl.Id });
        svc.Cancel(new[] { lbl.Id }, "yanlışlıkla");
        BreakLabelWrites(db);

        var act = () => svc.Uncancel(new[] { lbl.Id });
        act.Should().Throw<Exception>();

        labels.GetById(lbl.Id)!.CancelledAt.Should().NotBeNull("etiket iptalde kaldı");
        var c = customers.GetById(lbl.CustomerId)!;
        c.TotalLabelsPrinted.Should().Be(0, "iptal edilmiş satışın cirosu geri gelmemeli");
        c.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public void Yedek_onayi_ciro_yazmasi_patlarsa_fiyat_da_degismez()
    {
        var (svc, labels, customers, db) = Fx();
        using var _ = db;

        var parent = svc.Add("s1", Msg("@asil"), price: 199m, code: "MAVI");
        var backup = svc.AddBackup(parent.Id, "tiktok", "@yedek", "Yedek", null);
        svc.MarkPrintedAndRecord(new[] { backup.Id }); // geçici yedek: basılır, ciro yazılmaz
        BreakCustomerWrites(db);

        var act = () => svc.ConfirmBackup(backup.Id, newPrice: 250m);
        act.Should().Throw<Exception>();

        var after = labels.GetById(backup.Id)!;
        after.Price.Should().Be(199m, "onay tamamlanmadıysa pazarlık fiyatı da yazılmamalı");
        after.IsTentativeBackup.Should().BeTrue();
        var c = customers.GetById(backup.CustomerId)!;
        c.TotalLabelsPrinted.Should().Be(0);
        c.TotalAmount.Should().Be(0m);
    }
}
