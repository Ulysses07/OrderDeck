-- 045 — Çoklu bilgisayar senkronu, PR-3 (spec: 2026-10-05-coklu-bilgisayar-senkron).
--
-- BİRİM DAMGALARI. Müşteri alanları 12 birime ayrılır; her birimin son düzenleme
-- anı (unix MS) kendi kolonunda. Birim = tek alan ya da ayrılırsa anlamsızlaşan
-- blok: adres (Address+City+District) ve kara liste (IsBlacklisted+BlacklistReason+
-- BlacklistedAt). Sunucu (CustomerFieldMerge) birim başına son-yazan-kazanır uygular.
-- Grup başına tek damga YANLIŞTI: güncel veriyi almamış bilgisayardaki tek alan
-- değişikliği (ör. dekontla otomatik RecipientPaysActive) grubun dokunulmamış
-- alanlarını boşla ezip yayıyordu.
--
-- DAMGAYI KİM BASIYOR: tetikleyiciler (036 ile aynı gerekçe — Customer'a yazan
-- çok sayıda yol var, birini unutmak o değişikliğin hiçbir bilgisayara gitmemesi).
-- Kural: birimin bir alanı DEĞİŞTİ ve damga aynı UPDATE'te açıkça yazılMADIYSA
-- damga = MAX(şimdi, eski damga + 1). "Açıkça yazıldı" DEĞERLE anlaşılır: damga
-- yeni bir değer aldıysa açıktır; değer değişikliğiyle birlikte damga ESKİ değeriyle
-- yeniden yazılırsa (new.Damga IS old.Damga) yazılmamış sayılır ve "şimdi" basılır.
-- "+1": saati ileri bir bilgisayardan inmiş damga yerelde dururken yapılan düzenleme
-- onu yenmeli, yoksa sunucu reddeder. "UPDATE OF" yalnız SET'te geçen kolonlarda
-- tetikler; WHEN değer değişimine bakar — aynı değeri yeniden yazmak damga üretmez.
-- Metin birimleri BOŞA göre karşılaştırılır, NULLIF(TRIM(x), '') — ekleme tetikleyicisi
-- ve sunucu da böyle sayar: NULL, '' ve yalnız boşluk aynı "boş"tur. Boşu boşla
-- değiştirmek ya da yalnız kenar boşluğunu değiştirmek düzenleme değildir (sayılsaydı
-- taze damgalı bir "silme" son-yazan-kazanır ile öbür bilgisayarların gerçek değerini
-- ezerdi); 'a' → '' gerçek bir silmedir ve damgalanır.
--
-- YENİ SATIRDA yalnız DOLU birimler damgalanır (açık damga varsa o kalır). Sohbetten
-- açılan, takma addan başka bilgisi olmayan satır "şimdi değişti" sayılsaydı başka
-- bilgisayarın girdiği gerçek adı/adresi boşla ezerdi. false bayrak boş birimdir.
--
-- MEVCUT SATIRLAR DAMGALANMAZ, hiçbir göç damga UYDURMAZ: bugünkü veride güvenilir
-- bir "ne zaman değişti" yok (LastSeenAt iş zamanı). Damgasız birim sunucuda ve
-- istemcide yalnız-boşu-doldur ile taşınır.
--
-- SyncApplyGuard: sunucudan inen satırı uygulamak, yerel yeniden anahtarlama, miras
-- satırı dönüştürme, akıştan inen KVKK silmesi ve taze bilgisayarın form oynatması
-- düzenleme DEĞİLDİR. Bunlar aynı yazma işleminde bu
-- tabloya tek satır ekleyip sonunda siler (SyncApplyScope); satır varken damga
-- tetikleyicileri ve SyncSeq GÜNCELLEME tetikleyicisi çalışmaz. TEMP tablo olamaz:
-- TEMP olmayan tetikleyici temp şemaya başvuramaz. SyncSeq EKLEME tetikleyicisi kilide
-- UYMAZ: eklemede numara her zaman verilir (SyncSeq benzersiz kalmalı — F07 sayfa
-- sözleşmesi).
--
-- SYNCSEQ SAYACI. 036 numarayı MAX(SyncSeq)+1 ile veriyordu; bu yalnız SİLME YOKKEN
-- tekdüze artar. PR-3 Customer'dan satır siler: yeniden anahtarlama yeni gönderilmiş
-- sohbet kopyasını siler, o da çoğu zaman en büyük SyncSeq'li satırdır. En büyük satır
-- silinince sonraki numaralar gönderim imlecinin ALTINA düşer — asıl kaydın ilerletmesi
-- ve BAŞKA müşterilerin sonraki düzenlemeleri hiç gönderilmez, bekleyen sayacı 0 gösterir.
-- Bu yüzden iki SyncSeq tetikleyicisi de (036'nın ekleme tetikleyicisi dahil) burada
-- yeniden kurulur ve numarayı tek satırlık SyncSeqCounter'dan alır: sayaç yalnız artar,
-- silme onu geri almaz, numaralar benzersiz (F07) ve tekdüze kalır. Sayaç bugünkü en
-- büyük SyncSeq'le tohumlanır (bugüne kadar hiçbir yol Customer'dan satır silmedi: en
-- büyük = verilmiş en büyük numara). Açık ilerletme (yeniden anahtarlama, dönüştürme,
-- form oynatması) aynı sayacı CustomerSyncSeq.Bump ile, yazma işleminin içinde kullanır;
-- MAX(SyncSeq)+1 hiçbir yerde YAZILMAZ (sayaçla aynı numarayı üretir). Numaralar arasında
-- boşluk olur (ör. ekleme iki numara tüketir — damga tetikleyicisinin iç UPDATE'i de
-- numara alır); ardışıklık varsayılmaz.
--
-- KURAL: Customer'a INSERT OR REPLACE / REPLACE INTO yazılmaz — çakışan satırı silme
-- tetikleyicisi çalıştırmadan siler ve 035'in harici içerikli FTS indeksini bozar.
--
-- IdentityKey: sunucudaki IdentityKeyOf'un aynası (CustomerIdentity.KeyOf). Burada
-- yalnız geri doldurulur; yeni satırlarda C# yazar (CustomerIdentity.KeyOrNull).
-- Username'i değiştiren her yazım IdentityKey'i AYNI ifadede yazar — kolonu kendiliğinden
-- güncel tutan bir şey yok. Boş ya da yalnız boşluk kullanıcı adının anahtarı NULL'dur:
-- boş anahtar platformun bütün boş adlı satırlarını tek kişi sayardı (kimlik araması;
-- mezar taşı eşleşmesiyle bir KVKK silmesi hepsini boşaltırdı). Tetikleyici YOK, çünkü
-- bu dosyadaki tetikleyiciler YALNIZ yerleşik SQL kullanmalı: uygulama fonksiyonu
-- çağıran bir tetikleyici, eski sürüme geri dönüşte (fonksiyonu kaydetmeyen ikili)
-- Customer'a bütün yazımları düşürürdü. Yerel tekil kural UX_Customer_Platform_Username
-- DEĞİŞMEZ (yerelde harf kopyası varsa yeniden kurulurken göç düşerdi).
--
-- İMLEÇLER: hiçbiri SİLİNMEZ (U15). Yeni sürüm biçim-2 gönderimini kendi imleciyle yapar
-- (customer-projection-out-v2; satırı yok → ilk açılışta tüm müşteriler biçim 2 ile) ve
-- akışı customer-changes-in ile çeker (satırı yok → baştan). Önceki sürüme dönüşte o sürüm
-- kendi imleçleriyle (customer-projection-out, shopper-ingest-in) kaldığı yerden sürer;
-- yeniden yükseltmede v2 imleci aradaki düzenlemeleri biçim 2 ile yeniden gönderir.
-- intake-form-in korunur (formlar yeniden oynatılmasın).
--
-- CustomerRedirect (U12): yerel yeniden anahtarlamanın ve miras satırı dönüştürmesinin
-- sildiği Id → güncel Id. Id ile yazan her yol Id'yi buradan, yazımla aynı ifadede çözer
-- (taşıma araya girerse etiket FK hatası / açık pencerenin kaydı 0 satır olurdu). Zincir
-- yazımda kısaltılır; canlı bir Id hiçbir zaman FromId değildir.
--
-- CustomerPurgeTombstone.IdentityKey (U16): NOCASE yalnız ASCII katlar ("ŞEYMA" ≠ "şeyma");
-- mezar taşı engeli kimlik anahtarıyla da eşler. Geri doldurma aynı ifadeyle (boş anahtar NULL).
--
-- CustomerFeedFailure (U10): uygulanamayan akış öğesinin kalıcı deneme sayacı; beş turdan
-- sonra öğe atlanır, satır kalır ve durum satırında uyarı gösterilir.

CREATE TABLE SyncApplyGuard (
    Id INTEGER PRIMARY KEY CHECK (Id = 1)
);

ALTER TABLE Customer ADD COLUMN FullNameChangedAt        INTEGER;
ALTER TABLE Customer ADD COLUMN DisplayNameChangedAt     INTEGER;
ALTER TABLE Customer ADD COLUMN GroupIdChangedAt         INTEGER;
ALTER TABLE Customer ADD COLUMN AddressChangedAt         INTEGER;
ALTER TABLE Customer ADD COLUMN RecipientPaysChangedAt   INTEGER;
ALTER TABLE Customer ADD COLUMN PhoneChangedAt           INTEGER;
ALTER TABLE Customer ADD COLUMN EmailChangedAt           INTEGER;
ALTER TABLE Customer ADD COLUMN TcknChangedAt            INTEGER;
ALTER TABLE Customer ADD COLUMN WhatsAppConsentChangedAt INTEGER;
ALTER TABLE Customer ADD COLUMN SmsConsentChangedAt      INTEGER;
ALTER TABLE Customer ADD COLUMN BlacklistChangedAt       INTEGER;
ALTER TABLE Customer ADD COLUMN NotesChangedAt           INTEGER;
-- Username'i değiştiren her yazım bu kolonu AYNI ifadede yazar: kendiliğinden güncel
-- tutan bir şey yok. Boş anahtar NULL.
ALTER TABLE Customer ADD COLUMN IdentityKey              TEXT;

UPDATE Customer SET IdentityKey = NULLIF(od_identity_key(Username), '');

-- Akışın kimlik sahibi araması ve FindByPlatformAndUsername'in yedek araması.
CREATE INDEX IX_Customer_Identity ON Customer(Platform COLLATE NOCASE, IdentityKey);

CREATE TABLE CustomerRedirect (
    FromId TEXT    NOT NULL PRIMARY KEY,
    ToId   TEXT    NOT NULL,
    At     INTEGER NOT NULL
);
CREATE INDEX IX_CustomerRedirect_ToId ON CustomerRedirect(ToId);

ALTER TABLE CustomerPurgeTombstone ADD COLUMN IdentityKey TEXT;
UPDATE CustomerPurgeTombstone SET IdentityKey = NULLIF(od_identity_key(Username), '');
CREATE INDEX IX_CustomerPurgeTombstone_Identity ON CustomerPurgeTombstone(Platform, IdentityKey);

CREATE TABLE CustomerFeedFailure (
    ItemId        TEXT    NOT NULL PRIMARY KEY,
    ChangeSeq     INTEGER NOT NULL,
    Attempts      INTEGER NOT NULL,
    LastError     TEXT,
    FirstFailedAt INTEGER NOT NULL,
    SkippedAt     INTEGER
);

-- SyncSeq sayacı (bkz. başlıktaki SYNCSEQ SAYACI): hiçbir tetikleyici kurulmadan ÖNCE
-- bugünkü en büyük numarayla tohumlanır.
CREATE TABLE SyncSeqCounter (
    Id    INTEGER PRIMARY KEY CHECK (Id = 1),
    Value INTEGER NOT NULL
);
INSERT INTO SyncSeqCounter (Id, Value) SELECT 1, COALESCE(MAX(SyncSeq), 0) FROM Customer;

CREATE TRIGGER Customer_stamp_ai AFTER INSERT ON Customer
WHEN NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET
        FullNameChangedAt = COALESCE(new.FullNameChangedAt,
            CASE WHEN NULLIF(TRIM(new.FullName), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        DisplayNameChangedAt = COALESCE(new.DisplayNameChangedAt,
            CASE WHEN NULLIF(TRIM(new.DisplayName), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        GroupIdChangedAt = COALESCE(new.GroupIdChangedAt,
            CASE WHEN NULLIF(TRIM(new.GroupId), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        AddressChangedAt = COALESCE(new.AddressChangedAt,
            CASE WHEN NULLIF(TRIM(new.Address), '') IS NOT NULL
                   OR NULLIF(TRIM(new.City), '') IS NOT NULL
                   OR NULLIF(TRIM(new.District), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        RecipientPaysChangedAt = COALESCE(new.RecipientPaysChangedAt,
            CASE WHEN new.RecipientPaysActive = 1
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        PhoneChangedAt = COALESCE(new.PhoneChangedAt,
            CASE WHEN NULLIF(TRIM(new.Phone), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        EmailChangedAt = COALESCE(new.EmailChangedAt,
            CASE WHEN NULLIF(TRIM(new.Email), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        TcknChangedAt = COALESCE(new.TcknChangedAt,
            CASE WHEN NULLIF(TRIM(new.Tckn), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        WhatsAppConsentChangedAt = COALESCE(new.WhatsAppConsentChangedAt,
            CASE WHEN new.WhatsAppConsent = 1
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        SmsConsentChangedAt = COALESCE(new.SmsConsentChangedAt,
            CASE WHEN new.SmsConsent = 1
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        BlacklistChangedAt = COALESCE(new.BlacklistChangedAt,
            CASE WHEN new.IsBlacklisted = 1
                   OR NULLIF(TRIM(new.BlacklistReason), '') IS NOT NULL
                   OR new.BlacklistedAt IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END),
        NotesChangedAt = COALESCE(new.NotesChangedAt,
            CASE WHEN NULLIF(TRIM(new.Notes), '') IS NOT NULL
                 THEN CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER) END)
     WHERE rowid = new.rowid;
END;

-- Güncelleme tetikleyicileri: metin birimleri NULLIF(TRIM(x), '') ile (boş = boş),
-- bayraklar ve tarih ham değerle karşılaştırılır.
CREATE TRIGGER Customer_stamp_fullname_au AFTER UPDATE OF FullName ON Customer
WHEN NULLIF(TRIM(old.FullName), '') IS NOT NULLIF(TRIM(new.FullName), '')
 AND new.FullNameChangedAt IS old.FullNameChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET FullNameChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.FullNameChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_displayname_au AFTER UPDATE OF DisplayName ON Customer
WHEN NULLIF(TRIM(old.DisplayName), '') IS NOT NULLIF(TRIM(new.DisplayName), '')
 AND new.DisplayNameChangedAt IS old.DisplayNameChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET DisplayNameChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.DisplayNameChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_groupid_au AFTER UPDATE OF GroupId ON Customer
WHEN NULLIF(TRIM(old.GroupId), '') IS NOT NULLIF(TRIM(new.GroupId), '')
 AND new.GroupIdChangedAt IS old.GroupIdChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET GroupIdChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.GroupIdChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_address_au AFTER UPDATE OF Address, City, District ON Customer
WHEN (NULLIF(TRIM(old.Address), '') IS NOT NULLIF(TRIM(new.Address), '')
      OR NULLIF(TRIM(old.City), '') IS NOT NULLIF(TRIM(new.City), '')
      OR NULLIF(TRIM(old.District), '') IS NOT NULLIF(TRIM(new.District), ''))
 AND new.AddressChangedAt IS old.AddressChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET AddressChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.AddressChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_recipientpays_au AFTER UPDATE OF RecipientPaysActive ON Customer
WHEN old.RecipientPaysActive IS NOT new.RecipientPaysActive
 AND new.RecipientPaysChangedAt IS old.RecipientPaysChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET RecipientPaysChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.RecipientPaysChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_phone_au AFTER UPDATE OF Phone ON Customer
WHEN NULLIF(TRIM(old.Phone), '') IS NOT NULLIF(TRIM(new.Phone), '')
 AND new.PhoneChangedAt IS old.PhoneChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET PhoneChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.PhoneChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_email_au AFTER UPDATE OF Email ON Customer
WHEN NULLIF(TRIM(old.Email), '') IS NOT NULLIF(TRIM(new.Email), '')
 AND new.EmailChangedAt IS old.EmailChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET EmailChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.EmailChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_tckn_au AFTER UPDATE OF Tckn ON Customer
WHEN NULLIF(TRIM(old.Tckn), '') IS NOT NULLIF(TRIM(new.Tckn), '')
 AND new.TcknChangedAt IS old.TcknChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET TcknChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.TcknChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_whatsappconsent_au AFTER UPDATE OF WhatsAppConsent ON Customer
WHEN old.WhatsAppConsent IS NOT new.WhatsAppConsent
 AND new.WhatsAppConsentChangedAt IS old.WhatsAppConsentChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET WhatsAppConsentChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.WhatsAppConsentChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_smsconsent_au AFTER UPDATE OF SmsConsent ON Customer
WHEN old.SmsConsent IS NOT new.SmsConsent
 AND new.SmsConsentChangedAt IS old.SmsConsentChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET SmsConsentChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.SmsConsentChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_blacklist_au AFTER UPDATE OF IsBlacklisted, BlacklistReason, BlacklistedAt ON Customer
WHEN (old.IsBlacklisted IS NOT new.IsBlacklisted
      OR NULLIF(TRIM(old.BlacklistReason), '') IS NOT NULLIF(TRIM(new.BlacklistReason), '')
      OR old.BlacklistedAt IS NOT new.BlacklistedAt)
 AND new.BlacklistChangedAt IS old.BlacklistChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET BlacklistChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.BlacklistChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_stamp_notes_au AFTER UPDATE OF Notes ON Customer
WHEN NULLIF(TRIM(old.Notes), '') IS NOT NULLIF(TRIM(new.Notes), '')
 AND new.NotesChangedAt IS old.NotesChangedAt
 AND NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE Customer SET NotesChangedAt = MAX(
        CAST(ROUND((julianday('now') - 2440587.5) * 86400000.0) AS INTEGER),
        COALESCE(old.NotesChangedAt, 0) + 1)
     WHERE rowid = new.rowid;
END;

-- SyncSeq tetikleyicileri 036'nınkilerin YERİNE: numara silinmeye dayanıklı sayaçtan
-- (bkz. başlıktaki SYNCSEQ SAYACI). Gövdeler yalnız yerleşik SQL — önceki sürüm şema 45'te
-- çalışır. Ekleme tetikleyicisi kilide UYMAZ: eklemede numara her zaman verilir.
DROP TRIGGER Customer_syncseq_ai;
CREATE TRIGGER Customer_syncseq_ai AFTER INSERT ON Customer
BEGIN
    UPDATE SyncSeqCounter SET Value = Value + 1 WHERE Id = 1;
    UPDATE Customer SET SyncSeq = (SELECT Value FROM SyncSeqCounter WHERE Id = 1)
     WHERE rowid = new.rowid;
END;

-- 036'nın gönderim sayacı artık sunucuya giden TÜM alanları ve damgaları izliyor
-- (eskiden il/ilçe, e-posta, TCKN, izinler, kara liste, notlar, alıcı ödemeli,
-- GroupId değişince satır hiç gönderilmiyordu). Damga kolonları da listede: formun
-- yalnız damgayı ilerleten yazımı (değer aynı, form daha yeni) da sunucuya gitmeli.
-- Kilit altında çalışmaz: sunucudan inen veri yankı olarak geri gönderilmez.
-- Özyineleme yok: SyncSeq hiçbir tetikleyicinin "UPDATE OF" listesinde değil.
DROP TRIGGER Customer_syncseq_au;
CREATE TRIGGER Customer_syncseq_au
AFTER UPDATE OF Platform, Username, DisplayName, FullName, GroupId,
                Address, City, District, RecipientPaysActive,
                Phone, Email, Tckn, WhatsAppConsent, SmsConsent,
                IsBlacklisted, BlacklistReason, BlacklistedAt, Notes,
                FullNameChangedAt, DisplayNameChangedAt, GroupIdChangedAt, AddressChangedAt,
                RecipientPaysChangedAt, PhoneChangedAt, EmailChangedAt, TcknChangedAt,
                WhatsAppConsentChangedAt, SmsConsentChangedAt, BlacklistChangedAt, NotesChangedAt
ON Customer
WHEN NOT EXISTS (SELECT 1 FROM SyncApplyGuard)
BEGIN
    UPDATE SyncSeqCounter SET Value = Value + 1 WHERE Id = 1;
    UPDATE Customer SET SyncSeq = (SELECT Value FROM SyncSeqCounter WHERE Id = 1)
     WHERE rowid = new.rowid;
END;

UPDATE _meta SET SchemaVersion = 45 WHERE Id = 1;
