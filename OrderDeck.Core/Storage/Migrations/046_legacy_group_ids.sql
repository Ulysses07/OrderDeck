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
-- telefonu olan üyeler önce (form kimlikleri — formlar her bilgisayara aynı iner; kamuya açık
-- form telefonu zorunlu tutar), sonra en küçük (platform, kimlik anahtarı; IG/TikTok/FB'de
-- baştaki '@' sayılmaz — aşağıda); yeni numara od_legacy_group_id(çapa) — çapanın
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

-- ÇAPADA "@ad" = "ad" (Instagram/TikTok/Facebook): 2026-08-05'ten bu sürüme dek Instagram API yolu
-- adı "@ad" yazdı; aynı kişinin iki yazımı bir bilgisayarda aynı grupta olabilir (ör. operatörün
-- "@ad" satırına girdiği telefonla form grubuna çekilmesi), öbüründe yalnız "ad". '@' her harften
-- önce sıralandığı için "@ad" çapayı kazanır ve iki bilgisayar ayrışırdı. Yalnız "@"tan oluşan ad
-- boş anahtara inmez (hepsi tek çapada birleşirdi). Anahtar kolondan değil od_identity_key'den:
-- 045 sürümüyle önceki sürüm arasında gidip gelmiş veritabanında kolon boş kalmış olabilir.

INSERT OR IGNORE INTO SyncApplyGuard (Id) VALUES (1);

DROP TABLE IF EXISTS temp.LegacyGroupMap;

CREATE TEMP TABLE LegacyGroupMap AS
SELECT GroupId AS OldId,
       od_legacy_group_id(substr(MIN(Anchor), 3)) AS NewId
FROM (
    SELECT GroupId,
           CASE WHEN NULLIF(TRIM(Phone), '') IS NOT NULL THEN '0' ELSE '1' END
               || '|' || LOWER(Platform) || '|'
               || CASE WHEN LOWER(Platform) IN ('instagram', 'tiktok', 'facebook')
                       THEN COALESCE(NULLIF(LTRIM(od_identity_key(Username), '@'), ''), od_identity_key(Username))
                       ELSE od_identity_key(Username)
                  END AS Anchor
    FROM Customer
    WHERE NULLIF(TRIM(GroupId), '') IS NOT NULL
      AND od_identity_key(Username) IS NOT NULL
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
