-- 047 — Ödeme işinin ASIL kapsamı (PriorScopeKey) ve TESLİM kaydı (DeliveredAt, DeliveredNet,
-- Deliveries).
--
-- PriorScopeKey. Bir iş iki yoldan 'legacy:{anahtar}:{Id}' kapsamına iner: yerel kopya taşıması
-- (CustomerSyncRepository.MoveReferences — kopyanın işi asıl kayıttaki aynı kapsamlı işe
-- çarpınca) ve kişi birleştirmesi (PaymentJobRepository.FindOrCreateForPerson — aynı kişi ve
-- kapsamda ikinci iş). İkisi de ortak PaymentJobSql.DemoteToLegacy metnini kullanır ve indirilen
-- işin eski kapsamını buraya yazar. Legacy adı asıl kapsamı taşımadığı için bilgi başka yerde
-- yoktur. İki okuyucusu var: banka beklentisi (W2) kaybeden işin (CustomerId, PriorScopeKey)
-- beklentisini iptal eder; miras uzlaştırması da yalnız AYNI kapsamın tıklamasında çalışır (başka
-- kapsamın düşümü o kapsamın satışına aittir — başka yayında geri alınıp yeniden harcanmaz).
--
-- Teslim kaydı. ClosedAt bakiye yaşam döngüsünün kapanışıdır: revizyon, geri alma ve indirme onu
-- siler/yeniden yazar, çevrimdışı gönderilen (created) iş hiç kapanmaz. "Bu kişiye bu kapsam için
-- istek gitti mi, ne zaman, ne kadar" sorusu (sahip kararı 2: tekrar göndermeden önce sor) ayrı
-- kayıt ister: her başarılı teslimde DeliveredAt = o an, DeliveredNet = mesajdaki net tutar,
-- Deliveries + 1. Hiçbir yol bunları silmez. Deliveries onay belirtecine girer: saniyelik
-- damga aynı saniyedeki iki gönderimi ayırmaz, sayaç ayırır.
--
-- Nullable, varsayılansız (Deliveries 0): eski satırlarda asıl kapsam bilinmiyor. Eski teslim
-- edilmiş işler (legacy olmayan kapsamda ClosedAt dolu = mesaj ulaşmıştı; o kapsamda ClosedAt'ı
-- yalnız teslim yazar) DeliveredAt = ClosedAt ile işaretlenir; net tutarı okuyucu işin kendi
-- alanlarından hesaplar (kapalı işte o alanlar gönderilen mesajın alanlarıdır).
-- Önceki sürüm kolonları tanımaz ama SELECT'leri kolon adıyla okuduğu için etkilenmez.

ALTER TABLE PaymentJob ADD COLUMN PriorScopeKey TEXT;
ALTER TABLE PaymentJob ADD COLUMN DeliveredAt INTEGER;
ALTER TABLE PaymentJob ADD COLUMN DeliveredNet TEXT;
ALTER TABLE PaymentJob ADD COLUMN Deliveries INTEGER NOT NULL DEFAULT 0;

UPDATE PaymentJob SET DeliveredAt = ClosedAt, Deliveries = 1
 WHERE ClosedAt IS NOT NULL AND ScopeKey <> 'legacy' AND ScopeKey NOT LIKE 'legacy:%';

UPDATE _meta SET SchemaVersion = 47 WHERE Id = 1;
