-- Intake form çoklu-platform: kişi kimlik grubu + iletişim/izin alanları.
-- Aynı kişinin farklı platform Customer satırları GroupId ile bağlanır.
-- IG/TikTok/FB'de Username = chat handle → doğal birleşir; YouTube'da form
-- @handle ile durur, chat channelId satırı DisplayName eşleşmesiyle gruba
-- adopte edilir (Faz 3). Kara liste/çekiliş grup-bazlı kontrol edecek.
ALTER TABLE Customer ADD COLUMN GroupId TEXT;
ALTER TABLE Customer ADD COLUMN Email TEXT;
ALTER TABLE Customer ADD COLUMN Tckn TEXT;
ALTER TABLE Customer ADD COLUMN WhatsAppConsent INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Customer ADD COLUMN SmsConsent INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS IX_Customer_GroupId ON Customer(GroupId);

UPDATE _meta SET SchemaVersion = 21 WHERE Id = 1;
