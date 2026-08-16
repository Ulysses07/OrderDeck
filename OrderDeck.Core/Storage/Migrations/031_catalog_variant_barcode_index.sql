-- Barkod okutma yolu için indeks. KOLON YENİ DEĞİL: CatalogVariant.Barcode
-- göç 025'ten beri var (replika sunucunun alanlarını birebir taşıyor) ve
-- CatalogSyncService onu zaten yazıyor. Eksik olan tek şey indeksti.
--
-- Neden gerekli: okutma yolu (BroadcastCodeResolver) barkodu tek satır
-- aramasıyla çözüyor. İndekssiz her okutma tam tarama demek — yayın
-- sırasında binlerce satırlık replikada operatörün hissedeceği bir gecikme.
--
-- Neden UNIQUE DEĞİL: benzersizliğin sahibi sunucu ((LicenseId, Barcode)
-- indeksi). Replikada UNIQUE olsaydı, senkron sırasında geçici bir çakışma
-- (bir varyantın barkodu diğerine devredilirken sıra meselesi) INSERT'i
-- düşürür, işlem geri alınır ve katalog senkronu SESSİZCE ölürdü — göç 026
-- döneminde VariantCode NOT NULL yüzünden başımıza gelen tam olarak buydu.
CREATE INDEX IF NOT EXISTS IX_CatalogVariant_Barcode ON CatalogVariant(Barcode);

UPDATE _meta SET SchemaVersion = 31 WHERE Id = 1;
