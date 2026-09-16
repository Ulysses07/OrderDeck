-- R12-D03 (2026-09-16 denetimi): yedekten dönen kayıt, kurulmamış bir ayar
-- dosyasının boşluğunu "boşaltıldı" diye okutuyordu.
--
-- BackupService yalnız orderdeck.db'yi zipliyor; settings.json yedeğe GİRMİYOR.
-- Yedek yeni cihazda açıldığında PaymentAccountSyncState satırı (doğrulanmış,
-- dolu IBAN + hesap sahibi) geliyor, ayar bloğu ise varsayılan boş açılıyor.
-- 041/043 mantığı bunu "satır var + değer farklı" diye okuyup null POST'luyor
-- ve shopper dekont IBAN kontrolünün dayandığı uzak hesabı SİLİYOR. Aynı şey
-- ayar dosyası bozulup karantinaya alındığında da oluyor.
--
-- Kayıt sunucunun NE BİLDİĞİNİ söyler ve lisans anahtarı başına tutulduğu için
-- yedekle taşınması doğrudur — eksik olan o değil: yerel boşluğun NİYET mi
-- yoksa YOKLUK mu olduğunun kanıtı. O kanıt ayar dosyasında yaşar.
--
-- ÇÖZÜM: satır, kendisini yazan ayar dosyasının kimliğiyle damgalanır.
--   damga EŞLEŞİR      → 041/043 üç-durum mantığı aynen geçerli
--   damga TUTMAZ/YOK   → bu ayar dosyası bu lisans için hiçbir şey doğrulamadı;
--                        yerel değer de boşsa DOKUNMA (R10-D04/AC46 dalının
--                        genelleştirilmiş hâli: "satır yok" da bunun özel hâli)
--                        yerel değer doluysa ve satırla ÖRTÜŞÜYORSA yalnız
--                        damgala (gönderme), farklıysa normal gönderim.
--
-- Mevcut satırlar damgasız kalır (NULL). Bir satırın hangi ayar dosyasınca
-- yazıldığını geriye dönük kanıtlayamayız; damgasız = "tutmuyor" saymak
-- zararların asimetrisine göre doğru taraftır: en kötü ihtimalle yükseltmeden
-- hemen önceki bir boşaltma niyeti bir tur gecikir ve operatör tekrarlar —
-- diğer yönde ise meşru bir uzak hesap sessizce silinir. Sağlıklı kurulumda
-- pencere kendiliğinden kapanır: yerel değer satırla örtüştüğü için ilk turda
-- gönderimsiz damgalanır.

ALTER TABLE PaymentAccountSyncState ADD COLUMN InstallationId TEXT NULL;

UPDATE _meta SET SchemaVersion = 44 WHERE Id = 1;
