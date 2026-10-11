-- 047 — Ödeme işinin ASIL kapsamı (PriorScopeKey), TESLİM kaydı (DeliveredAt, DeliveredNet,
-- Deliveries), GÖNDERİM denemesi (SendAttempts, AttemptNet, PriorAttemptNet) ve ONAY damgası
-- (ResumeCovers).
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
-- Gönderim denemesi. SendAttempts her gönderimden (Cloud API ya da wa.me) ÖNCE artar, teslim kaydı
-- SONRA yazılır. SendAttempts > Deliveries = gönderim başladı ama sonucu yazılamadı (süreç düştü,
-- teslim yazımı hata verdi): mesaj gitmiş olabilir, sıradaki tıklama sorar. Sayaç da onay
-- belirtecine girer: öbür pencerenin gönderimi, teslim kaydı yazılamasa bile, bekleyen eski onayı
-- geçersiz kılar. Teslim o ana kadarki bütün denemeleri karşılar (SendAttempts = Deliveries).
-- AttemptNet = son denemenin mesajındaki net: sonucu yazılamamış denemenin sorusu ("gönderilmiş
-- olabilir") o tutarı gösterir — işin alanları denemeden sonra değişebilir (revizyon, geri alma).
-- PriorAttemptNet bir önceki denemenin netidir: başlamamış deneme (wa.me açılamadı) geri alınınca
-- AttemptNet ona döner; daha eski yetim deneme duruyorsa soru onun tutarını gösterir.
--
-- Onay damgası. Kalan işin para adımı yarım kalmışsa (apply_uncertain/reverse_pending) sıradaki
-- tıklama tekrar göndermeyi SORMADAN sürer — o tıklama kapıda sorulmuş ya da sorulacak bir şey
-- yoktu. Muafiyet yalnız o kapının gördüğü izlere uygulanabilir: kapı (FindOrCreateForPerson) işi
-- kalan iş olarak her geçirişinde plandaki iş ve miras Id'lerini ResumeCovers'a yazar. Yarım adımdan
-- sonra kapsama giren teslim izi (ör. taşımanın kapalı mirasa indirdiği teslim edilmiş kopya işi)
-- damgada yoktur ve yine sorulur. Zaman karşılaştırması değil: izleri farklı yollar farklı saatlerle
-- yazar.
--
-- Nullable, varsayılansız (Deliveries/SendAttempts 0): eski satırlarda asıl kapsam bilinmiyor. Eski
-- teslim edilmiş işler (legacy olmayan kapsamda ClosedAt dolu = mesaj ulaşmıştı; o kapsamda
-- ClosedAt'ı yalnız teslim yazar) DeliveredAt = ClosedAt ile işaretlenir ve net tutar ŞİMDİ
-- dondurulur: kapalı işte alanlar gönderilen mesajın alanlarıdır, ama iş sonra yeniden açılabilir
-- (revizyon, geri alma, dış iade) ve alanları değişir — okuyucu türetseydi "tekrar gönderilsin mi?"
-- sorusu müşteriden hiç istenmemiş bir tutarı gösterirdi. Tutarlar TEXT ondalık: SQL aritmetiği
-- REAL'e çevirip yuvarlardı; od_payment_net (SqliteSearchFunctions, C#) kayıpsız hesaplar.
--
-- Geri dönüş (Velopack ile önceki sürüm): önceki sürüm kolonları tanımaz ama SELECT'leri kolon adıyla
-- okuduğu için etkilenmez; teslimde yalnız ClosedAt yazar. Bu sürüme dönünce okuyucu aynı kuralla
-- (legacy olmayan kapsamda ClosedAt = teslim; teslim kaydından yeniyse en son teslim odur) o
-- teslimleri görür (PaymentJob.LastDeliveredAt); işi yeniden açan, tutarını değiştiren ya da indiren
-- her yazım (PaymentJobSql.FreezeDelivery) bu izi netiyle teslim kaydına geçirir.

ALTER TABLE PaymentJob ADD COLUMN PriorScopeKey TEXT;
ALTER TABLE PaymentJob ADD COLUMN DeliveredAt INTEGER;
ALTER TABLE PaymentJob ADD COLUMN DeliveredNet TEXT;
ALTER TABLE PaymentJob ADD COLUMN Deliveries INTEGER NOT NULL DEFAULT 0;
ALTER TABLE PaymentJob ADD COLUMN SendAttempts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE PaymentJob ADD COLUMN AttemptNet TEXT;
ALTER TABLE PaymentJob ADD COLUMN PriorAttemptNet TEXT;
ALTER TABLE PaymentJob ADD COLUMN ResumeCovers TEXT;

UPDATE PaymentJob SET DeliveredAt = ClosedAt, DeliveredNet = od_payment_net(ProductTotal, AppliedAmount),
                      Deliveries = 1, SendAttempts = 1
 WHERE ClosedAt IS NOT NULL AND ScopeKey <> 'legacy' AND ScopeKey NOT LIKE 'legacy:%';

UPDATE _meta SET SchemaVersion = 47 WHERE Id = 1;
