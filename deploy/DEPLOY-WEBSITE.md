# orderdeckapp.com — İlk Deploy Rehberi

İlk yayına alma için adım adım kılavuz. Marketing sitesini canlıya çıkarır, mevcut `license.orderdeckapp.com` akışını bozmaz.

> **Önce localde**: `web/` değişiklikleri commit'lenmiş ve master'a push'lanmış olmalı. VPS `git pull` ile en son hali çekecek.

## 1. VPS IP'sini öğren

Hostinger hPanel → VPS → "Sunucu Bilgileri" / "Server Info" altında **IPv4** adresi yazıyor. Aşağıda `<VPS_IP>` olarak referans veriyorum (örn. `185.x.x.x`).

```bash
# Lokalden test:
ping orderdeck-vps  # ya da doğrudan IP
```

## 2. DNS — Hostinger hPanel

hPanel → **Domains** → `orderdeckapp.com` → **DNS / Nameservers** → **DNS Records** sekmesine git.

Aşağıdaki kayıtları ekle (varsa eskileri sil):

| Type  | Name              | Value         | TTL     |
|-------|-------------------|---------------|---------|
| A     | `@`               | `<VPS_IP>`    | 14400   |
| A     | `www`             | `<VPS_IP>`    | 14400   |
| A     | `license`         | `<VPS_IP>`    | 14400   |
| TXT   | `@`               | `v=spf1 -all` | 14400   |

> `@` = apex (`orderdeckapp.com` kendisi). `www` ve `license` = subdomain'ler.
> TXT/SPF: marketing site mail göndermiyor; bu kayıt phishing'i engeller. **`support@orderdeckapp.com` mail kullanmak istersen sonradan ekleriz.**

DNS yayılması Hostinger'da genelde 5-30 dakika. Test:
```bash
dig +short orderdeckapp.com
dig +short www.orderdeckapp.com
dig +short license.orderdeckapp.com
# Üçü de <VPS_IP> dönmeli
```

## 3. Lokalde build hazırla

Zaten yapıldı ama deploy öncesi yenile:

```bash
cd C:/Users/burak/source/repos/LiveDeck/web
npm run build
# → web/out/ üretiliyor
```

`web/out/index.html` ve `web/out/en/index.html` dosyalarının olduğunu doğrula.

## 4. Repo değişikliklerini push et

```bash
cd C:/Users/burak/source/repos/LiveDeck
git add web/ deploy/Caddyfile deploy/docker-compose.yml .github/workflows/web-deploy.yml
git commit -m "feat(web): orderdeckapp.com marketing site (Next.js static export)"
git push
```

## 5. VPS'te repo'yu güncelle

SSH ile bağlan (Hostinger'ın verdiği user, genelde `root` ya da kendi belirlediğin):

```bash
ssh root@<VPS_IP>
cd /opt/orderdeck
git pull
```

Eğer `/opt/orderdeck` git repo değilse (sadece deploy dosyaları kopyalandıysa), bu durumda repo'yu klonlamak gerekir:

```bash
# yedek al sonra klonla
mv /opt/orderdeck /opt/orderdeck.bak
git clone <REPO_URL> /opt/orderdeck
cp /opt/orderdeck.bak/deploy/.env /opt/orderdeck/deploy/.env  # env'i geri al
cp -r /opt/orderdeck.bak/deploy/sql-data /opt/orderdeck/deploy/  # SQL data
cp -r /opt/orderdeck.bak/deploy/keys /opt/orderdeck/deploy/      # data-protection keys
cp -r /opt/orderdeck.bak/deploy/backups /opt/orderdeck/deploy/   # backup'lar
```

> **Dikkat:** SQL Server data ve key klasörlerini KAYBETME — bunlar production state. Şüphedeysen önce `tar -czf /root/orderdeck-backup.tgz /opt/orderdeck` yedeği al.

## 6. web-out klasörünü oluştur

```bash
mkdir -p /opt/orderdeck/deploy/web-out
```

Bu klasör `docker-compose.yml`'de Caddy container'ına `:/srv/web:ro` olarak bind-mount edilmiş; içine statik HTML kopyalayacağız.

## 7. Lokalden VPS'e build çıktısını rsync ile gönder

Windows'tan rsync için **Git Bash** veya **WSL** gerekir. Git Bash'te:

```bash
cd C:/Users/burak/source/repos/LiveDeck

rsync -avz --delete-after \
  -e "ssh" \
  web/out/ \
  root@<VPS_IP>:/opt/orderdeck/deploy/web-out/
```

Rsync yoksa (saf Windows PowerShell), alternatif olarak `scp` ile tar paketi gönder:

```powershell
# Lokal:
cd C:\Users\burak\source\repos\LiveDeck\web
tar -czf out.tgz -C out .
scp out.tgz root@<VPS_IP>:/tmp/

# VPS:
ssh root@<VPS_IP>
rm -rf /opt/orderdeck/deploy/web-out/*
tar -xzf /tmp/out.tgz -C /opt/orderdeck/deploy/web-out/
rm /tmp/out.tgz
```

Doğrulama (VPS'te):
```bash
ls /opt/orderdeck/deploy/web-out/
# index.html, en/, blog/, gizlilik-politikasi/, ... görünmeli
cat /opt/orderdeck/deploy/web-out/index.html | head -20
# Next.js'in HTML çıktısı olmalı
```

## 8. Caddy'i yeniden başlat (yeni Caddyfile + bind mount yüklensin)

```bash
cd /opt/orderdeck/deploy
docker compose up -d caddy --force-recreate
docker compose logs --tail 50 caddy
```

Logda `obtained certificate for orderdeckapp.com` benzeri satırlar görmen lazım. İlk Let's Encrypt sertifikası 10-30 saniyede gelir.

## 9. Sıkıntı olursa

```bash
# Caddy logu canlı izle:
docker compose logs -f caddy

# Cert alma başarısızsa: DNS henüz yayılmamış demektir.
# Bekle (~5 dk), sonra:
docker compose restart caddy
```

## 10. Smoke testleri

```bash
# Ana sayfa:
curl -sI https://orderdeckapp.com/ | head -1
# HTTP/2 200

# www → apex 301:
curl -sI https://www.orderdeckapp.com/ | head -3
# HTTP/2 301
# location: https://orderdeckapp.com/

# Privacy + Terms (audit-kritik):
curl -sI https://orderdeckapp.com/en/privacy-policy/ | head -1
curl -sI https://orderdeckapp.com/en/terms-of-service/ | head -1

# Mevcut license akışı bozulmamış olmalı:
curl -sI https://license.orderdeckapp.com/health | head -1
# HTTP/2 200
```

Browser'da:
- https://orderdeckapp.com/ → Ana sayfa, mavi gradient logo
- https://orderdeckapp.com/en/ → İngilizce versiyon
- https://orderdeckapp.com/blog/ → 3 yazı listeli
- https://orderdeckapp.com/fiyatlandirma/ → 100k ₺ tek lisans + 10k ₺/yıl güncelleme paketi

## 11. (Bonus) GitHub Actions ile sonraki deploy'ları otomatikleştir

Bir kerelik ilk deploy elden yapıldıktan sonra `web/**` değişikliklerinde otomatik deploy için:

GitHub repo → **Settings → Secrets and variables → Actions** → şu üç secret'ı ekle:
- `DEPLOY_HOST` = `<VPS_IP>` ya da hostname
- `DEPLOY_USER` = `root` (ya da SSH user)
- `DEPLOY_SSH_KEY` = lokalden ürettiğin private key (`ssh-keygen -t ed25519 -f orderdeck-deploy`)
  - Public key'i (`orderdeck-deploy.pub`) VPS'te `~/.ssh/authorized_keys`'e ekle

Workflow `.github/workflows/web-deploy.yml` zaten hazır; sonraki `web/` push'unda otomatik build + rsync + smoke test yapacak.

## 12. (Sonraki adım) YouTube API audit başvurusu

Site yayında olduktan sonra:

1. <https://support.google.com/youtube/contact/yt_api_form> formunu aç
2. URL alanlarına yaz:
   - **App homepage:** `https://orderdeckapp.com/en/`
   - **Privacy Policy:** `https://orderdeckapp.com/en/privacy-policy/`
   - **Terms of Service:** `https://orderdeckapp.com/en/terms-of-service/`
3. Use case açıklaması (Türkçe + İngilizce):
   > OrderDeck is a desktop application for live auction streamers in Türkiye that
   > aggregates chat from Instagram, TikTok, Facebook and YouTube to enable real-time
   > order capture and label printing. We currently have 10+ active broadcasters and
   > are scaling to 100+. The application uses YouTube Data API v3 only to read
   > live-chat messages and, on explicit operator click, to delete individual chat
   > messages or ban abusive users via the moderation endpoints. We adhere to the
   > Limited Use requirements: data is processed only on the user's local machine
   > and never transmitted to OrderDeck servers.
4. Quota request: 1.000.000 units/day
5. Confirm compliance with all linked policies
