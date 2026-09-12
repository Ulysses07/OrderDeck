-- N03-g (2026-09-12 denetimi): müşteri projeksiyonu delta imleci İŞ ZAMANINA
-- (LastSeenAt) bağlıydı. Sorun tek bir cümleyle: imleç GENEL, artış ise SATIRA
-- ÖZEL.
--
-- Yaşanan senaryo (denetimin kontrollü deneyi): bir kaydın LastSeenAt'i saat
-- kayması / ileri zamanlı veri yüzünden 60 sn ilerideydi. Sync o satırı gönderip
-- imleci oraya taşıdı. Ardından BAŞKA bir müşterinin telefonu güncellendi;
-- MAX(LastSeenAt+1, now) o satırı bir saniye ilerletti ama imleç hâlâ 59 saniye
-- öndeydi. Telefon sunucuya HİÇ gitmedi — ve bir daha denenmedi, çünkü satır
-- imlecin altında kalmaya devam ediyor. Kalıcı, sessiz kayıp.
--
-- N03 ve N03-k'nın "+1" düzeltmeleri bu sınıfı kapatamaz: hangi sayıya +1
-- yaparsan yap, karşılaştırdığın imleç başka bir satır yüzünden ileri gitmişse
-- yetişemezsin. Sorun aritmetikte değil, İKİ FARKLI ŞEYİ TEK KOLONA yüklemekte:
--   1) "müşteri en son ne zaman görüldü"  (iş zamanı, kullanıcıya gösterilir,
--      saatle birlikte geri de gidebilir)
--   2) "bu satırın senkronlanması gereken sırası" (yalnız artan olmak zorunda)
--
-- ÇÖZÜM: (2)'yi kendi kolonuna ayır. SyncSeq saatten tamamen bağımsız, GENEL
-- (satır başına değil, tablo genelinde) kesin artan bir sayaç. Yeni değer her
-- zaman tablodaki en büyükten büyük olduğu için güncellenen satır imlecin
-- ÖNÜNE geçmek zorunda — geçememesi imkânsız.
--
-- Bu aynı zamanda F07'nin (aynı saniyede BatchSize'dan fazla satır → sayfa
-- sınırında kalıcı atlama) sebebini de yapısal olarak ortadan kaldırıyor:
-- SyncSeq benzersiz olduğu için "aynı değere sahip iki satır" diye bir hâl yok,
-- eşitlik bozucu Id'ye gerek kalmıyor.
--
-- SAYACI KİM İLERLETİYOR: tetikleyiciler, repo metotları değil — gerekçesi 035
-- ile aynı. Customer'a yazan dokuz ayrı ifade var; her birine sayaç artışı
-- eklemek bugünü çözer, onuncu yazma yolu eklendiği gün o satır SESSİZCE
-- senkronlanmaz olur. Kaybın belirtisi de yok: kayıt yerli yerinde duruyor,
-- yalnız sunucuya gitmiyor. Tetikleyiciyle bu sınıf yapısal olarak imkânsız.
--
-- HANGİ KOLONLAR: yalnız sunucu projeksiyonuna GİDEN alanlar (bkz.
-- WpfCustomerSyncItem / LicensesWpfCustomersSyncController). LastSeenAt,
-- TotalAmount, TotalLabelsPrinted bilerek DIŞARIDA: etiket basımı sıcak yol ve
-- projeksiyonun içeriğini değiştirmiyor. Eskiden imleç LastSeenAt olduğu için
-- her chat görülmesi satırı yeniden gönderiyordu; artık yalnız gerçekten
-- değişen satır gidiyor.
--
-- ÖZYİNELEME YOK: tetikleyicinin kendi yazdığı kolon (SyncSeq) "UPDATE OF"
-- listesinde değil — 035'teki arama tetikleyicisiyle aynı desen. İki tetikleyici
-- birbirini de tetiklemiyor: 035 SearchKey/PhoneKey yazıyor, o kolonlar bu
-- listede yok; bu tetikleyici SyncSeq yazıyor, o da 035'in listesinde yok.

ALTER TABLE Customer ADD COLUMN SyncSeq INTEGER NOT NULL DEFAULT 0;

-- Mevcut satırlara (LastSeenAt, Id) sırasıyla artan numara. Sıra aslında
-- serbest — istemci imleci bu göçten sonra 0'dan başlıyor ve her şey bir kez
-- yeniden taranıyor (sunucu upsert'i idempotent) — ama deterministik olması
-- hata ayıklamayı kolaylaştırıyor.
WITH numbered AS (
    SELECT Id, ROW_NUMBER() OVER (ORDER BY LastSeenAt ASC, Id ASC) AS rn FROM Customer
)
UPDATE Customer
   SET SyncSeq = (SELECT rn FROM numbered WHERE numbered.Id = Customer.Id);

-- İmleç sorgusu: WHERE SyncSeq > @since ORDER BY SyncSeq ASC LIMIT @max.
CREATE INDEX IX_Customer_SyncSeq ON Customer(SyncSeq);

CREATE TRIGGER Customer_syncseq_ai AFTER INSERT ON Customer BEGIN
    UPDATE Customer
       SET SyncSeq = (SELECT COALESCE(MAX(SyncSeq), 0) + 1 FROM Customer)
     WHERE rowid = new.rowid;
END;

CREATE TRIGGER Customer_syncseq_au
AFTER UPDATE OF Platform, Username, DisplayName, FullName, Phone, Address ON Customer BEGIN
    UPDATE Customer
       SET SyncSeq = (SELECT COALESCE(MAX(SyncSeq), 0) + 1 FROM Customer)
     WHERE rowid = new.rowid;
END;

UPDATE _meta SET SchemaVersion = 36 WHERE Id = 1;
