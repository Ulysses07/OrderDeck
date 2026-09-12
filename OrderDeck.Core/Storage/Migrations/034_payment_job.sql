-- R2-01..04 (2026-09-11 R3 denetimi): bakiye düşümünün KALICI durum makinesi.
--
-- 033'ün PendingBalanceApply'ı yalnız "çözülmemiş anahtar" tutuyordu; sonucun
-- ne olduğu (uygulandı mı, bakiye yok muydu, tutar neydi) bellekte kalıyordu.
-- PaymentJob bunları diske indirir:
--
--   created → apply_uncertain → applied | no_balance ; teslimatta ClosedAt dolar.
--
-- Kapsam (CustomerId, ScopeKey) UNIQUE: "session:{id}" | "cumulative" |
-- "legacy:{IdempotencyKey}".
-- INSERT OR IGNORE + bu indeks = atomik find-or-create (R2-04 TOCTOU ölür).
--
-- ProductTotal / AppliedAmount TEXT: invariant kültür ondalık — REAL, eşitlik
-- karşılaştırmasını (revizyon tespiti) bozardı. Id: Guid "N".

CREATE TABLE PaymentJob (
    Id            TEXT    NOT NULL PRIMARY KEY,
    CustomerId    TEXT    NOT NULL,
    ScopeKey      TEXT    NOT NULL,
    ProductTotal  TEXT    NOT NULL,
    Revision      INTEGER NOT NULL DEFAULT 0,
    ApplyKey      TEXT,
    AppliedAmount TEXT,
    State         TEXT    NOT NULL,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL,
    ClosedAt      INTEGER
);

CREATE UNIQUE INDEX UX_PaymentJob_Scope ON PaymentJob(CustomerId, ScopeKey);

-- Miras taşıma: her çözülmemiş 033 kaydı KENDİ 'legacy:{anahtar}' işine döner.
-- Durum apply_uncertain: anahtar sunucuda kullanılmış olabilir; ilk "Ödeme
-- iste"de replay gerçeği öğrenir (bkz. servis, legacy devralma).
--
-- R4-04 (2026-09-12): eskiden yalnız EN YENİ çözülmemiş kayıt taşınıyor, daha
-- eskileri "033 zaten müşteri başına tek kayıt vaat ediyordu" gerekçesiyle
-- atılıyordu. O vaat tutmuyor — R2-04 aynı müşteride iki anahtar oluşabildiğini
-- gösterdi. Atılan anahtar sunucuda ledger PK olarak durduğundan geçmişte fazla
-- düşüm olmuş olabilir ve yerelde İZİ KALMAZ: sonucu öğrenecek, geri alacak ya
-- da operatöre gösterecek hiçbir kayıt yoktur. Bu tablo hemen aşağıda DROP
-- ediliyor, yani sonraki bir göç kurtaramaz — koruma burada olmak zorunda.
--
-- Kapsam anahtarın kendisiyle benzersizleştiriliyor: UNIQUE (CustomerId,
-- ScopeKey) indeksi aynı müşterinin iki miras işini ancak böyle yan yana
-- tutabilir ve kapsam metni, uzlaştırılacak anahtarı doğrudan söyler.
INSERT INTO PaymentJob
    (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey, AppliedAmount,
     State, CreatedAt, UpdatedAt, ClosedAt)
SELECT lower(hex(randomblob(16))), p.CustomerId, 'legacy:' || p.IdempotencyKey,
       p.ProductTotal, 0, p.IdempotencyKey, NULL, 'apply_uncertain',
       p.CreatedAt, p.CreatedAt, NULL
FROM PendingBalanceApply p
WHERE p.ResolvedAt IS NULL;

DROP TABLE PendingBalanceApply;

UPDATE _meta SET SchemaVersion = 34 WHERE Id = 1;
