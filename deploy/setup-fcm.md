# FCM Push Notifications — Deploy Setup

Mobile Panel push notification'larını gerçek olarak yollamak için (stub değil)
Firebase projesi + service account JSON gerekir. Adımlar:

## 1. Firebase Project oluştur

1. https://console.firebase.google.com/ → **Add project**
2. Project name: `orderdeck-prod` (test ortamı için ayrı: `orderdeck-test`)
3. Google Analytics: opsiyonel, push için gerek yok
4. Project ID'yi bir kenara yaz (örn: `orderdeck-prod-xxxxx`)

## 2. iOS + Android app'lerini Firebase'e ekle

OrderDeck-Mobile/apps/panel — `capacitor.config.ts` içinde `appId`:

- iOS: Project → **Add app** → iOS, bundle ID = capacitor.config.ts'teki appId
- Android: Project → **Add app** → Android, package name = aynı appId

Her ikisinde de `google-services.json` (Android) ve `GoogleService-Info.plist` (iOS)
indir, OrderDeck-Mobile/apps/panel/{android,ios} altına yerleştir. Bunlar
client config — secret değil, repo'ya commit edilebilir.

## 3. Service Account JSON (server-side)

1. Firebase Console → Project settings (⚙) → **Service accounts** sekmesi
2. **Generate new private key** → JSON indir
3. Bu JSON **secret** — repo'ya KOYMA. VPS'e güvenli aktar:

```bash
# Lokal → VPS
scp -i ~/.ssh/orderdeck.pem firebase-service-account.json \
    deploy@72.62.53.86:/tmp/

# VPS içinde
ssh -i ~/.ssh/orderdeck.pem deploy@72.62.53.86
sudo mkdir -p /etc/orderdeck
sudo mv /tmp/firebase-service-account.json /etc/orderdeck/
sudo chmod 600 /etc/orderdeck/firebase-service-account.json
sudo chown root:root /etc/orderdeck/firebase-service-account.json
```

## 4. LicenseServer config

`docker-compose.yml` LicenseServer service'ine ENV ekle:

```yaml
services:
  licenseserver:
    environment:
      OrderDeck__Push__Provider: "fcm"
      OrderDeck__Push__Fcm__ServiceAccountJsonPath: "/etc/orderdeck/firebase-service-account.json"
    volumes:
      - /etc/orderdeck/firebase-service-account.json:/etc/orderdeck/firebase-service-account.json:ro
```

`appsettings.Production.json` üzerinden de set edilebilir ama ENV daha
explicit + secret rotation kolay.

## 5. Restart & verify

```bash
cd /opt/orderdeck
docker compose up -d licenseserver
docker compose logs -f licenseserver | grep -i "push\|fcm"
```

Boot başarılıysa `OrderDeck:Push:Provider=fcm` log'da, hata yoksa init OK.

JSON path yanlışsa boot fail-fast: `FileNotFoundException` ile çıkar.

## 6. Smoke test

1. Mobile app aç → DahaFazla → "Bildirim" toggle = **Açık**
2. WPF app'ten yeni bir dekont oluştur, sync push edilir
3. Mobile cihazda bildirim gelmeli

LicenseServer log'unda:
```
Push[FCM] customer=... success=1 failure=0 stale=0
```

Gelmiyorsa:
- `failure>0`: log'a bak, transient/permanent ayır
- `stale>0`: token eski, cihaz yeniden register etmeli (uygulama açılışta initPush çağırıyor)
- Hiç log yok: provider hala "stub" → ENV yanlış

## 7. Rotation / iptal

Service account compromise olursa:
1. Firebase Console → Service accounts → eski key'i **Delete**
2. Yeni key generate et → VPS'e aktar → `docker compose restart licenseserver`

PushDevice tablosu otomatik temizlenmez — sadece kullanıcı uygulamayı silerse
FCM "Unregistered" döner, sender o devicey silmek için ExecuteDeleteAsync
çağırır. Manuel temizlik için:

```sql
DELETE FROM PushDevices WHERE LastSeenAt < DATEADD(day, -90, GETUTCDATE());
```
