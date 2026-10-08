-- 046 — Eski grup numaraları bilgisayarlar arası belirlenimli.
--
-- SORUN. v0.9.8 ve öncesi kişi grubunu (GroupId) her bilgisayarda kendisi açtı:
-- form, kimliklerden biri zaten grupta değilse Guid.NewGuid() aldı. Aynı formu işleyen
-- üç bilgisayarda aynı kişinin üç ayrı grup numarası var; sunucuda hiç yok (eski gönderim
-- grubu taşımıyordu). 045'ten sonra grup damgalı birim olarak senkronlanır, ama damgasız
-- eski numara yalnız-boşu-doldur ile taşınır: her bilgisayar kendi numarasını tutar. Bir
-- bilgisayarda yapılan elle birleştirme yalnız DEĞİŞEN satırı (yeni üyeyi) damgalar; öbür
-- bilgisayar o satırı birleştiren bilgisayarın numarasıyla alır, kendi grubunda o numara
-- yok — birleştirme orada görünmez. Sunucuda yapılacak toplu birleştirme de aynı sebeple
-- form satırını yerel grubundan koparırdı.
--
-- ÇÖZÜM. Damgasız (göç öncesi) her grubun numarası üyelerinden türetilir: çapa üye =
-- telefonu olan üyeler önce (form kimlikleri — formlar her bilgisayara aynı iner), sonra
-- en küçük (platform, kimlik anahtarı); yeni numara od_legacy_group_id(çapa) — çapanın
-- SHA-256'sından 32 onaltılık hane (Guid "N" biçimi). Aynı üyeler her bilgisayarda aynı
-- numarayı alır; bilgisayara özgü fazladan bir sohbet üyesi (telefonsuz) çapayı
-- değiştirmez. Kimse gruba girmez ya da gruptan çıkmaz; tek istisna: çapası aynı iki grup
-- (yerelde aynı kimliğin iki kopyası iki ayrı grupta) birleşir — aynı kişi, PR-3'ün kopya
-- taşıması da onları zaten tek satıra indirir.
--
-- DAMGA YOK (045 kural 1: göç damga uydurmaz) ve SyncSeq ilerlemez: SyncApplyGuard altında.
-- Gerek de yok — 045 ile gelen biçim-2 imleci ilk açılışta bütün satırları gönderir; sunucu
-- damgasız grubu boş alana doldurur, her bilgisayar aynı numarayı gönderdiği için tutarlı.
-- Kilit satırı kalmışsa (çökmüş bir uygulama işleminden) göç onu da temizler; açılıştaki
-- onarım da aynısını yapardı.
--
-- od_legacy_group_id yalnız bu ifadede kullanılır, hiçbir şema nesnesinde DEĞİL (önceki sürüm
-- onu kaydetmez; bkz. SqliteSearchFunctions).

INSERT OR IGNORE INTO SyncApplyGuard (Id) VALUES (1);

CREATE TEMP TABLE LegacyGroupMap AS
SELECT GroupId AS OldId,
       od_legacy_group_id(substr(MIN(Anchor), 3)) AS NewId
FROM (
    SELECT GroupId,
           CASE WHEN NULLIF(TRIM(Phone), '') IS NOT NULL THEN '0' ELSE '1' END
               || '|' || LOWER(Platform) || '|' || IdentityKey AS Anchor
    FROM Customer
    WHERE NULLIF(TRIM(GroupId), '') IS NOT NULL
      AND IdentityKey IS NOT NULL
)
WHERE GroupId NOT IN (SELECT GroupId FROM Customer
                      WHERE GroupIdChangedAt IS NOT NULL AND GroupId IS NOT NULL)
GROUP BY GroupId;

UPDATE Customer
   SET GroupId = (SELECT m.NewId FROM LegacyGroupMap m WHERE m.OldId = Customer.GroupId)
 WHERE GroupId IN (SELECT OldId FROM LegacyGroupMap WHERE NewId IS NOT OldId);

DROP TABLE LegacyGroupMap;

DELETE FROM SyncApplyGuard;

UPDATE _meta SET SchemaVersion = 46 WHERE Id = 1;
