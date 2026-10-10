-- 047 — Miras kapsamına indirilen ödeme işinin ASIL kapsamı (PriorScopeKey).
--
-- Bir iş iki yoldan 'legacy:{anahtar}:{Id}' kapsamına iner: yerel kopya taşıması
-- (CustomerSyncRepository.MoveReferences — kopyanın işi asıl kayıttaki aynı kapsamlı işe
-- çarpınca) ve kişi birleştirmesi (PaymentJobRepository.FindOrCreateForPerson — aynı kişi ve
-- kapsamda ikinci iş). İkisi de ortak PaymentJobSql.DemoteToLegacy metnini kullanır ve indirilen
-- işin eski kapsamını buraya yazar. Legacy adı asıl kapsamı taşımadığı için bilgi başka yerde
-- yoktur; banka beklentisi (W2) kaybeden işin (CustomerId, PriorScopeKey) beklentisini bu kolonla
-- iptal eder. Bellekte tutulan bir liste commit ile kuyruğa yazım arasında çökmede kaybolurdu.
--
-- Nullable, varsayılansız: eski satırlar NULL kalır (asıl kapsam o satırlarda zaten bilinmiyor).
-- Önceki sürüm kolonu tanımaz ama SELECT'leri kolon adıyla okuduğu için etkilenmez.

ALTER TABLE PaymentJob ADD COLUMN PriorScopeKey TEXT;

UPDATE _meta SET SchemaVersion = 47 WHERE Id = 1;
