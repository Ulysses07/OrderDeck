-- Kargo PR F: Customer entity'ye RecipientPaysActive bayrağı.
-- Vendor DekontEkleDialog'da ShipmentDirective=RecipientPays seçtiğinde
-- müşterinin bu bayrağı 1'e döner. Print template flag'i okuyup etikette
-- "ALICI ÖDEMELİ" kırmızı yazı + tutar render eder.
--
-- Per-customer state (per-shipment değil) çünkü mevcut domain'de Customer↔Payment
-- linkage yok. MVP compromise: müşteri başına aktif state. Vendor manuel olarak
-- Customer detail dialog'tan (gelecek) veya direkt SQL ile clear edebilir.
-- Gelecek "Shipment" entity feature'ı bu sticky flag'i deprecate edecek.

ALTER TABLE Customer ADD COLUMN RecipientPaysActive INTEGER NOT NULL DEFAULT 0;

UPDATE _meta SET SchemaVersion = 17 WHERE Id = 1;
