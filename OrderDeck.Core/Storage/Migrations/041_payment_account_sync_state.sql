-- R10-D04 (2026-09-15 denetimi): ödeme hesabı temizleme niyeti yeniden
-- başlatmayı atlatamıyordu. Operatör IBAN+hesap sahibini boşaltır, 5 dakikalık
-- senkron turu gelmeden uygulama yeniden başlarsa taze servis örneğinin
-- süreç-içi önbelleği null/null'dı; ayarlar da null/null → "değişiklik yok"
-- sayılıp POST atılmıyor, uzaktaki hesap dolu kalıyordu.
--
-- ÇÖZÜM: "sunucuyla hiç karşılaştırılmadı" ile "boş değer başarıyla
-- gönderildi" ayrımı süreç belleğinde yaşayamaz — son başarılı gönderimin
-- kendisi kalıcılaşır (R9-D01 dersi: kalıcı senkron durumu settings'te değil
-- veritabanında yaşar). Satır YOK = sunucu durumu bilinmiyor → yerel değer de
-- boşsa DOKUNMA (AC46: taze profil meşru uzak hesabı körlemesine silemez).
-- Satır VAR = sunucunun bildiği değerler bunlar → fark varsa (boşaltma dahil)
-- gönder. Lisans anahtarı başına satır: hedef değişimi de doğal olarak
-- "bilinmiyor"a düşer (D03'ün ödeme ayağını yapısal olarak kapsar).
--
-- Geriye dönük doldurma YOK: 041 öncesi ne gönderildiği kayıtlı değil.
-- Değerli kurulumlar ilk turda bir kez gereksiz POST atar (idempotent),
-- boş kurulumlar hiç dokunmaz — iki yön de güvenli taraf.

CREATE TABLE PaymentAccountSyncState (
    LicenseKey    TEXT NOT NULL PRIMARY KEY,
    Iban          TEXT NULL,
    AccountHolder TEXT NULL,
    SyncedAt      TEXT NOT NULL
);

UPDATE _meta SET SchemaVersion = 41 WHERE Id = 1;
