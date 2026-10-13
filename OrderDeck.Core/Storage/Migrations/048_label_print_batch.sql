-- 048 — Basılan etiketler sekmesi (sahip kararları K1–K5, 2026-10-12).
--
-- PrintBatchId / PrintSeq. "Basılanlar" listesi müşteri adına göre değil "Yazdır" basışına göre
-- gruplanır ve grubun içi KÂĞIDIN ÇIKTIĞI sıradadır — operatör yanlış fiyatlı kâğıtları paket
-- yığınında bu sırayla bulur. Bugünkü veriden ikisi de güvenle türetilemiyor: PrintedAt saniyelik
-- (aynı saniyedeki iki "Yazdır" tek grup görünür) ve kâğıt sırası hiç saklanmıyor (seçili satırlar
-- seçim sırasıyla basılır, AddedAt sırasıyla değil). LabelService.MarkPrintedAndRecord her basışa
-- yeni bir kimlik ve kâğıttaki konumu (1'den) yazar. Yeniden baskı ikisine de DOKUNMAZ: etiket ilk
-- basıldığı grupta kalır, ilk PrintedAt bozulmaz.
--
-- Eski satırlarda ikisi de NULL. Okuyucunun yedek kuralı: grup = aynı PrintedAt, sıra = AddedAt, Id
-- (LabelRepository.GetPrintedBySession). Geriye dönük doldurma YOK — uydurulmuş bir kâğıt sırası
-- operatöre gerçek gibi görünürdü.
--
-- PriceCorrectedAt. Fiyatı "Fiyatı düzelt" ile değiştirilmiş etiketin damgası
-- (LabelService.CorrectPrices). Yeniden baskı kâğıdındaki "DÜZELTME" işareti buna bakar: operatör
-- düzeltmenin hemen ardından yeniden basmasa da sonradan basılan kâğıt eski (yanlış fiyatlı) kâğıtla
-- karışmasın. Sunucuya gitmez; fiyatın kendisi mevcut senkronla gider (SyncedAt = NULL).
--
-- PaidPrice. Ödenmiş etiketin ÖDENDİĞİ fiyat. Ödenmiş = kargoya verilmemiş dosyaya bağlı (dekont kaydı bağlar)
-- ya da onu içeren teslim edilmiş istek tamamen bakiyeden düşülmüş. Etiket ödenmişken İLK düzeltildiğinde
-- düzeltmeden önceki fiyatı yazılır, sonraki düzeltmeler dokunmaz (LabelRepository.UpdatePriceForCorrection).
-- NULL = ödenmişken hiç düzeltilmedi, fiyatı hâlâ ödendiği fiyat. Düzeltme sonucundaki "ödendi, fark ±X"
-- o düzeltmenin ödenmiş etiketlerinin (yeni fiyat − PaidPrice) toplamıdır (CorrectedPaymentRequests.Plan):
-- yalnız son düzeltmenin farkı gösterilseydi 250 → 200 → 190'da müşterinin gerçek farkı (−60) yerine −10
-- görünürdü; dekont tutarı etikete ya da dosyaya bağlı saklanmıyor.
--
-- Dördü de yalnız bu bilgisayarın yerel verisi: etiketler başka bilgisayarlara inmiyor (K7).
-- Yalnız ekleyici; hiçbir şema nesnesi uygulama fonksiyonu çağırmaz. Önceki sürüm kolonları
-- tanımaz ama SELECT'leri kolon adıyla okuduğu için etkilenmez.

ALTER TABLE Label ADD COLUMN PrintBatchId TEXT;
ALTER TABLE Label ADD COLUMN PrintSeq INTEGER;
ALTER TABLE Label ADD COLUMN PriceCorrectedAt INTEGER;
ALTER TABLE Label ADD COLUMN PaidPrice REAL;

UPDATE _meta SET SchemaVersion = 48 WHERE Id = 1;
