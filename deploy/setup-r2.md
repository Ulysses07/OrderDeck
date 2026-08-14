# Cloudflare R2 — Broadcast Posts Media Storage

OrderDeck broadcast post media (foto/video) Cloudflare R2'de tutulur.
Mevcut FCM kurulumu gibi tek seferlik bir setup, sonra otomatik.

**Aynı bucket'ı katalog ürün fotoğrafları da kullanıyor** (Faz 1a, `PanelProductPhotoController`)
— dosya adı hâlâ "broadcast-posts" ama içerik iki akıştan geliyor. CORS
listesini daraltırken bunu unutma.

## 1. Cloudflare hesabı + R2

1. https://dash.cloudflare.com → R2 sekmesi → **Enable R2**
2. Bucket oluştur: **orderdeck-broadcast-posts** (region: auto, EU)
3. Bucket → **Settings → CORS**:
   ```json
   [{
     "AllowedOrigins": ["https://localhost", "capacitor://localhost",
                        "http://localhost",
                        "https://license.orderdeckapp.com",
                        "https://panel.orderdeckapp.com"],
     "AllowedMethods": ["GET", "PUT", "HEAD"],
     "AllowedHeaders": ["*"],
     "ExposeHeaders": ["ETag"],
     "MaxAgeSeconds": 3600
   }]
   ```

   > **Tarayıcıdan yükleyen HER yeni istemci bu listeye eklenmeli.** Yükleme
   > presigned URL ile doğrudan R2'ye gittiği için araya sunucu girmiyor; CORS
   > tek kapı. Listede olmayan origin `OPTIONS` preflight'ında **403** alır ve
   > tarayıcıda yalnız `Failed to fetch` görünür — sunucu log'una hiçbir şey
   > düşmez.
   >
   > Bu liste 2026-05'te yalnız mobil için yazılmıştı. Panel
   > (`panel.orderdeckapp.com`) 2026-08'de web'e alınırken origin eklenmedi ve
   > panelden fotoğraf yükleme —hem ürün hem yayın gönderisi— sessizce kırık
   > kaldı; mobil çalışmaya devam ettiği için kimse fark etmedi. 2026-08-14'te
   > düzeltildi.
   >
   > Doğrulama (yazma yapmaz; `<account>` ve origin'i değiştir):
   > ```bash
   > curl -s -o /dev/null -D - -X OPTIONS \
   >   "https://<account>.r2.cloudflarestorage.com/orderdeck-broadcast-posts/probe.img" \
   >   -H "Origin: https://panel.orderdeckapp.com" \
   >   -H "Access-Control-Request-Method: PUT" \
   >   -H "Access-Control-Request-Headers: content-type"
   > ```
   > İzinli origin `204` + `Access-Control-Allow-Origin` döner; izinsiz origin
   > `403`. İkisini de dene — yalnız izinliyi denemek politikanın ardına kadar
   > açık olmadığını göstermez.

## 2. API token

1. R2 → **Manage R2 API Tokens** → **Create API Token**
2. Permissions: **Object Read & Write**
3. Specify bucket: `orderdeck-broadcast-posts`
4. TTL: opsiyonel (boş bırak = no expiry)
5. **Create** → çıkan **Access Key ID** + **Secret Access Key**'i kopyala

## 3. Account ID

Cloudflare dashboard → sağ üst → Account ID kopyala.

## 4. VPS .env güncelle

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86
nano /opt/orderdeck/.env
```

Aşağıdakini ekle:

```env
OrderDeck__BroadcastMedia__Provider=r2
R2__AccountId=<account-id>
R2__AccessKeyId=<access-key>
R2__SecretAccessKey=<secret>
R2__BucketName=orderdeck-broadcast-posts
```

## 5. docker-compose.yml environment

`/opt/orderdeck/docker-compose.yml` license-server service environment'ına ekle:

```yaml
OrderDeck__BroadcastMedia__Provider: "${OrderDeck__BroadcastMedia__Provider:-stub}"
R2__AccountId: "${R2__AccountId:-}"
R2__AccessKeyId: "${R2__AccessKeyId:-}"
R2__SecretAccessKey: "${R2__SecretAccessKey:-}"
R2__BucketName: "${R2__BucketName:-}"
```

## 6. Restart + verify

```bash
cd /opt/orderdeck
docker compose up -d license-server
docker compose logs -f license-server | grep -iE "broadcast|r2"
```

Boot başarılıysa log'da "BroadcastMedia provider=r2" görünür (yoksa eski log seviyesinde olabilir, INFO veya DEBUG'a çek).

## 7. Smoke

Mobile Panel'den text post → POST 201. Photo post için:
1. POST /api/panel/posts/upload-url → URL al
2. Direkt R2'ye PUT (mobile in-app)
3. POST /api/panel/posts media bilgisiyle → 201
4. Cloudflare dashboard → bucket → objeyi gör.
