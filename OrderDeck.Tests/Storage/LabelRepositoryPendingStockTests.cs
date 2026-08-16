using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Gösterilen bakiyenin yerel yarısı. Filtre sunucudaki defter sayma kuralının
/// birebir aynası olmalı — biri kayarsa iki taraf kalıcı olarak ayrışır.
/// </summary>
public class LabelRepositoryPendingStockTests
{
    // Label'ın SessionId ve CustomerId'si FK ile korunuyor ve InMemorySqlite
    // bağlantı dizesinde "Foreign Keys=true" var — ham INSERT çalışmaz, önce
    // oturum ve müşteri açılmalı. Kalıp LabelRepositoryProductCountTests'ten.
    private static (InMemorySqlite Db, LabelRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        return (db, new LabelRepository(db));
    }

    private static Label Row(string id, string productId, string? variantId,
                             bool shippingFee = false, bool tentative = false,
                             long? syncedAt = null) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", "A12", 100m, 150, 200,
            IsTentativeBackup: tentative, IsShippingFee: shippingFee,
            SyncedAt: syncedAt, ProductId: productId, ProductVariantId: variantId);

    [Fact]
    public void Counts_unsynced_labels_per_variant()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, vid));
        repo.Insert(Row("l2", pid, vid));
        repo.Insert(Row("l3", pid, null));

        var deltas = repo.GetPendingStockDeltas(pid);

        deltas.Should().HaveCount(2);
        deltas.Single(d => d.ProductVariantId == vid).PendingCount.Should().Be(2);
        deltas.Single(d => d.ProductVariantId == null).PendingCount.Should().Be(1);
    }

    [Fact]
    public void Excludes_synced_shipping_and_backup_labels()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));                        // sayılır
        repo.Insert(Row("l2", pid, null, syncedAt: 2000));        // sunucuya gitti
        repo.Insert(Row("l3", pid, null, shippingFee: true));     // kargo bedeli
        repo.Insert(Row("l4", pid, null, tentative: true));       // onaysız yedek

        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Excludes_cancelled_labels()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));
        repo.Insert(Row("l2", pid, null));
        // Insert kasıtlı olarak CancelledAt yazmıyor; iptal sonradan oluyor.
        repo.MarkCancelled(new[] { "l2" }, 300, "test");

        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Ignores_other_products()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", a, null));
        repo.Insert(Row("l2", b, null));

        repo.GetPendingStockDeltas(a).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Returns_empty_when_nothing_pending()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.GetPendingStockDeltas(Guid.NewGuid().ToString("N")).Should().BeEmpty();
    }

    // ── Damganın SyncedAt'ten ayrılması (göç 030) ─────────────────────────────
    //
    // SyncedAt bir OUTBOX bayrağı: "sunucudaki kopya bayat mı". Stokta sorulan
    // soru başka: "sunucu bu satırı defterine yazdı mı". İkisi yalnız STOK-İLGİLİ
    // alanlar değişince birlikte düşmeli; aşağıdaki dört test o ayrımı sabitliyor.

    [Fact]
    public void Printing_does_not_make_a_synced_label_pending_again()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));
        repo.MarkSynced("l1", 2000);

        repo.MarkPrinted(new[] { "l1" }, 2500);

        // Yazdırmak satırı push kuyruğuna geri alır (SyncedAt=NULL) ama stok
        // açısından hiçbir şey değişmedi — hareket sunucunun defterinde duruyor.
        // Bu satır bekleyen sayılırsa operatör kuyruğu yazdırdığı anda kart,
        // yazdırılan adet kadar EKSİK gösterir.
        repo.GetPendingStockDeltas(pid).Should().BeEmpty();
        repo.GetUnsynced().Should().ContainSingle().Which.Id.Should().Be("l1");
    }

    [Fact]
    public void Price_edit_does_not_make_a_synced_label_pending_again()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));
        repo.MarkSynced("l1", 2000);

        repo.UpdatePrice("l1", 250m);

        repo.GetPendingStockDeltas(pid).Should().BeEmpty();
    }

    [Fact]
    public void Uncancelling_a_synced_label_makes_it_pending_again()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));
        repo.MarkSynced("l1", 2000);
        repo.MarkCancelled(new[] { "l1" }, 2100, "test");
        repo.MarkSynced("l1", 2200);   // iptal sunucuya gitti → hareket silindi

        repo.Uncancel(new[] { "l1" });

        // Burada damga DÜŞMELİ: CancelledAt stok-ilgili, sunucu hareketi
        // kaldırmıştı. Düşmezse satış bakiyeden hiç düşmez.
        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Confirming_a_tentative_backup_makes_it_pending()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null, tentative: true));
        repo.MarkSynced("l1", 2000);   // sunucu geçici yedek gördü → hareket yok

        repo.ConfirmTentativeBackups(new[] { "l1" });

        // IsTentativeBackup da stok-ilgili: onaylanan yedek artık gerçek satış.
        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }
}
