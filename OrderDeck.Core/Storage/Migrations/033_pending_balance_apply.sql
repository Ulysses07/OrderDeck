-- N02 (2026-09-10 denetimi): bakiye düşümü için KALICI ödeme-işi kimliği.
--
-- PaymentRequestService her "Ödeme iste" tıklamasında apply ucuna bir
-- idempotency anahtarı gönderiyor; sunucu o anahtarı ledger satırının PK'sı
-- yapıp tekrarı eliyor. Ama anahtar bellekte Guid.NewGuid() ile üretiliyordu:
-- düşüm BAŞARILI olup mesaj penceresi açılamazsa (LaunchFailed) operatörün
-- ikinci tıklaması YENİ anahtar üretir ve bakiye İKİNCİ kez düşerdi.
--
-- Çözüm: anahtar diske iner. Kayıt, mesaj müşteriye ulaşana kadar
-- (Cloud API Sent ya da wa.me penceresi açıldı) "çözülmemiş" kalır;
-- çözülmemiş kayıt varken yapılan yeni deneme AYNI anahtarı yeniden kullanır
-- → sunucu ilk sonucu oynatır, ikinci düşüm imkânsızlaşır.
--
-- ProductTotal TEXT: invariant kültürle yazılan ondalık — REAL'e çevirmek
-- eşitlik karşılaştırmasını (bekleyen iş aynı satış mı?) bozabilirdi.

CREATE TABLE PendingBalanceApply (
    IdempotencyKey TEXT NOT NULL PRIMARY KEY,
    CustomerId     TEXT NOT NULL,
    ProductTotal   TEXT NOT NULL,
    CreatedAt      INTEGER NOT NULL,
    ResolvedAt     INTEGER
);

CREATE INDEX IX_PendingBalanceApply_Unresolved
    ON PendingBalanceApply(CustomerId) WHERE ResolvedAt IS NULL;

UPDATE _meta SET SchemaVersion = 33 WHERE Id = 1;
