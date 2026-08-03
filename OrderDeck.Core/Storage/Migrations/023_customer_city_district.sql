-- Adresi il/ilçe/kalan olarak ayır. e-Fatura toplu yükleme şablonu "Alıcı
-- Şehir", "Alıcı İlçe" ve "Alıcı Sokak" için ayrı kolonlar istiyor; tek parça
-- serbest metinden bunları güvenilir şekilde ayırmak mümkün değil (kısaltma,
-- yazım hatası, "Merkez"). Kayıt formu artık il/ilçeyi listeden seçtiriyor.
--
-- Eski satırlarda City/District NULL kalır ve tüm adres Address'te durur —
-- geriye dönük ayrıştırma bilerek YAPILMIYOR (yanlış fatura riski).
ALTER TABLE Customer ADD COLUMN City TEXT;
ALTER TABLE Customer ADD COLUMN District TEXT;

UPDATE _meta SET SchemaVersion = 23 WHERE Id = 1;
