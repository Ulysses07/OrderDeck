# Eşleşmeyen kayıt ölçümü — 2026-09-02

Kayıt formundan gelen kullanıcı adlarının ne kadarı sohbetteki kişiyle hiç
eşleşmemiş? Faz 1 öncesi taban ölçüm.

**Bu bir DÜZELTME değil.** Eski kayıtlarda doğru handle'ın ne olduğunu bilmiyoruz;
toplu düzeltme yanlış tahminleri veriye yazardı. Yalnız sayıyoruz.

## Nasıl çalıştırılır

Yayıncı PC'sinde, uygulama KAPALIYKEN, veritabanının **kopyası** üzerinde:

```bash
cp "$USERPROFILE/Documents/OrderDeck/data/orderdeck.db" /tmp/olcum.db
sqlite3 /tmp/olcum.db
```

Yol `OrderDeck.Core/AppPaths.cs` içinde tanımlı; `data/` alt klasörü şart.

Kopya üzerinde çalışmanın sebebi: canlı dosyaya açılan okuma bile WAL kilidi
tutabiliyor ve uygulama yeniden açıldığında yazma hatası veriyor.

## Sorgular

### 1. Hiç etkileşime girmemiş kayıtlar

Forma kaydolmuş ama sohbette bir kez bile görünmemiş kişiler. Kullanıcı adı
yanlışsa beklenen iz tam olarak bu: kayıt var, hareket yok.

```sql
SELECT Platform,
       COUNT(*) AS Toplam,
       SUM(CASE WHEN TotalLabelsPrinted = 0
                 AND TotalAmount = 0
                 AND LastSeenAt = FirstSeenAt THEN 1 ELSE 0 END) AS HicHareketYok
FROM Customer
GROUP BY Platform
ORDER BY Toplam DESC;
```

`HicHareketYok / Toplam` oranı, platform bazında sorunun büyüklüğü.

Not: `Platform` sütununda eski kayıtlardan gelen `'form'` değeri de çıkabilir —
platform ayrımı yapılmadan önce yazılan satırlar. Onları ayrı değerlendirin.

### 2. YouTube: handle satırı var, channelId satırı ayrı

YouTube sohbet satırları `Username = channelId` (UC…), `DisplayName = sohbette
görünen ad` olarak düşüyor. Formdan `@handle` girilip kaydedilen satır
channelId'li satırla birleşemediyse aynı kişi iki kayıt olarak duruyor.

```sql
SELECT COUNT(*) AS AyriKalanHandleSatiri
FROM Customer AS f
WHERE f.Platform = 'youtube'
  AND f.Username NOT LIKE 'UC%'
  AND NOT EXISTS (
      SELECT 1 FROM Customer AS c
      WHERE c.Platform = 'youtube'
        AND c.Username LIKE 'UC%'
        AND LTRIM(c.DisplayName, '@') = LTRIM(f.Username, '@') COLLATE NOCASE
  );
```

**Bu sorgu bir yaklaşıklık, kesin sayı değil.** İki sebeple:

- `LTRIM(DisplayName,'@')` karşılaştırması üretimdeki birleştirme mantığının
  aynısı (`CustomerRepository.FindExistingForIntake`), ama YouTube API'sinin
  verdiği `displayName` teknik olarak kanal **başlığı**; handle ile aynı olması
  garanti değil. Yani "ayrı kalan" sayılan bir satır aslında eşleşmiş de olabilir.
- Faz 1 sonrası formdan gelen satırlar `Username = channelId` yazılıyor
  (`IntakeFormSyncService.cs`), yani `NOT LIKE 'UC%'` filtresi zaten yeni
  kayıtları kapsam dışı bırakıyor. Bu, ölçümü tam da istediğimiz şeye —
  **eski** kayıtlara — daraltıyor.

### 3. Şüpheli kısa/uzun handle çiftleri

"test1234 yerine test" hatasının izi: aynı platformda bir kaydın kullanıcı adı,
başka bir kaydın kullanıcı adının ön eki.

```sql
SELECT a.Platform, a.Username AS Kisa, b.Username AS Uzun
FROM Customer AS a
JOIN Customer AS b
  ON b.Platform = a.Platform
 AND b.Username <> a.Username
 AND b.Username LIKE a.Username || '%' COLLATE NOCASE
WHERE LENGTH(a.Username) >= 4
ORDER BY a.Platform, a.Username;
```

Bu liste **kanıt değil, ipucu**: `ayse` ve `ayse_moda` gerçekten iki ayrı kişi
olabilir. Elle bakılır.

## Sonuç

| Tarih | Platform | Toplam | Hareketsiz | Oran |
|---|---|---|---|---|
| 2026-09-03 | instagram | 1195 | 590 | 49.4% |
| 2026-09-03 | youtube | 709 | 326 | 46.0% |
| 2026-09-03 | facebook | 96 | 76 | 79.2% |
| 2026-09-03 | tiktok | 23 | 22 | 95.7% |

Sorgu 2 (YouTube handle/channelId ayrışması): **41** satır ayrı kaldı.

Sorgu 3 (şüpheli kısa/uzun handle çifti): **72** eşleşen çift, tamamı Instagram.
Büyük çoğunluğu büyük/küçük harf varyasyonu (`Ahmet` / `ahmet`) — gerçek
"test1234 yerine test" hatasına uyan az sayıda satır var (`Musa`→`musaa.sevinc`,
`Test`→`testkullanici`, `Yakup`→`yakupmusellim`, `ahmet`→üç farklı hesap).
Liste kanıt değil ipucu; elle bakılmadı.

Ölçüm alındığında tablo doldurulur; Faz 1 yayına girdikten bir süre sonra aynı
sorgular tekrar çalıştırılıp karşılaştırılır.
