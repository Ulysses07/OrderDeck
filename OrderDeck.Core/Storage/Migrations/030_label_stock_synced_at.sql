-- Stok sayımı için AYRI bir "sunucu bunu gördü mü" damgası.
--
-- Neden SyncedAt yetmiyor: SyncedAt bir OUTBOX bayrağı — "bu satırın sunucudaki
-- kopyası bayat mı" sorusunu yanıtlıyor. MarkPrinted ve UpdatePrice, PrintedAt
-- ya da Price değişince satırı yeniden push kuyruğuna almak için onu NULL'a
-- çekiyor. Oysa stok açısından o satır ÇOKTAN sayıldı: sunucunun defterinde
-- hareketi duruyor. "Bekleyen" sorgusu SyncedAt'e bakınca aynı etiket ikinci
-- kez düşülüyordu ve operatör kuyruğu yazdırdığı anda kart, yazdırılan adet
-- kadar eksik gösteriyordu (bir sonraki push'a, ≤60 sn, kadar).
--
-- StockSyncedAt yalnız STOK-İLGİLİ alanlar değişince NULL'lanır. Stok-ilgili
-- alan kümesi sunucudaki StockLedgerReconciler.Desired kuralının aynası:
-- ProductId, ProductVariantId, IsShippingFee, CancelledAt, IsTentativeBackup.
-- Price ve PrintedAt bu kümede DEĞİL — o yüzden MarkPrinted/UpdatePrice bu
-- kolona dokunmaz.
--
-- Backfill: mevcut satırlar için SyncedAt'in kendisi. Bugüne dek push edilmiş
-- her satırın sunucuda hareketi var; push edilmemişlerin ikisi de NULL kalır.
-- Yani göç, açılıştaki gösterimi değiştirmez.
ALTER TABLE Label ADD COLUMN StockSyncedAt INTEGER;

UPDATE Label SET StockSyncedAt = SyncedAt;

UPDATE _meta SET SchemaVersion = 30 WHERE Id = 1;
