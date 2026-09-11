-- R2-01..04 (2026-09-11 R3 denetimi): bakiye düşümünün KALICI durum makinesi.
--
-- 033'ün PendingBalanceApply'ı yalnız "çözülmemiş anahtar" tutuyordu; sonucun
-- ne olduğu (uygulandı mı, bakiye yok muydu, tutar neydi) bellekte kalıyordu.
-- PaymentJob bunları diske indirir:
--
--   created → apply_uncertain → applied | no_balance ; teslimatta ClosedAt dolar.
--
-- Kapsam (CustomerId, ScopeKey) UNIQUE: "session:{id}" | "cumulative" | "legacy".
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

-- Miras taşıma: her müşterinin EN YENİ çözülmemiş 033 kaydı 'legacy' işine
-- döner. Durum apply_uncertain: anahtar sunucuda kullanılmış olabilir; ilk
-- "Ödeme iste"de replay gerçeği öğrenir (bkz. servis, legacy devralma).
-- Daha eski çözülmemiş kayıtlar bilerek atılır: 033 akışı zaten müşteri başına
-- tek çözülmemiş kayıt vaat ediyordu; birden fazlası ancak yarım kalmış eski
-- denemedir ve anahtarları sunucuda ledger PK olarak duruyor — tekrar
-- kullanılmadıkça zararsız.
INSERT INTO PaymentJob
    (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey, AppliedAmount,
     State, CreatedAt, UpdatedAt, ClosedAt)
SELECT lower(hex(randomblob(16))), p.CustomerId, 'legacy', p.ProductTotal, 0,
       p.IdempotencyKey, NULL, 'apply_uncertain', p.CreatedAt, p.CreatedAt, NULL
FROM PendingBalanceApply p
WHERE p.ResolvedAt IS NULL
  AND p.rowid IN (
      SELECT p2.rowid FROM PendingBalanceApply p2
      WHERE p2.CustomerId = p.CustomerId AND p2.ResolvedAt IS NULL
      ORDER BY p2.CreatedAt DESC, p2.rowid DESC LIMIT 1);

DROP TABLE PendingBalanceApply;

UPDATE _meta SET SchemaVersion = 34 WHERE Id = 1;
