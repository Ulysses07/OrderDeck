-- R11-D02 (2026-09-16 denetimi): ödeme hesabı gönderiminin SONUCU bilinmiyorsa,
-- bu "hiç denenmedi" ile aynı şey değildir.
--
-- 041 kaydı yalnız BAŞARIDA yazıyor. Sunucuya ULAŞAN ama yanıtı kaybolan bir
-- gönderimden sonra satır hiç oluşmuyor; operatör sonra hesabı boşaltıp
-- uygulamayı yeniden başlatınca "satır yok + yerel boş" dalına düşülüyor ve
-- ATLANIYOR. Kaldırılmak istenen hesap sunucuda kalıyor — üstelik shopper
-- dekont fraud kontrolü o hesabı karşılaştırmaya devam ediyor.
--
-- ÇÖZÜM: denemenin kendisi, sonucundan ÖNCE kalıcılaşır. PendingSince dolu bir
-- satır "gönderdim, karşılığını görmedim" demektir; sonraki tur bu durumda
-- karşılaştırma yapmadan yeniden gönderir. Başarıda PendingSince NULL'a
-- çekilir ve satır yeniden "sunucunun bildiği değerler" anlamına gelir.
--
-- Üç durumun ayrımı korunuyor:
--   satır YOK             → hiç denenmedi, sunucu durumu bilinmiyor → yerel de
--                           boşsa DOKUNMA (AC46/AC23: taze profil meşru uzak
--                           hesabı körlemesine silemez)
--   PendingSince DOLU     → sonuç bilinmiyor → koşulsuz gönder
--   PendingSince NULL     → değerler doğrulandı → yalnız fark varsa gönder
--
-- Mevcut satırlar doğrulanmış sayılır (NULL varsayılan): 041'den beri yazılan
-- her satır zaten yalnız başarıda yazıldı.

ALTER TABLE PaymentAccountSyncState ADD COLUMN PendingSince TEXT NULL;

UPDATE _meta SET SchemaVersion = 43 WHERE Id = 1;
