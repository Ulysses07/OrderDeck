# OrderDeck License Server — VPS Deployment

## Architecture
- **SQL Server 2022 Express** (Docker) — DB, internal port 1433
- **OrderDeck.LicenseServer** (.NET 10 ASP.NET Core, Docker) — `ghcr.io/ulysses07/orderdeck-license-server:<tag>` image, internal port 8080
- **Caddy 2** (Docker) — reverse proxy, ports 80/443, automatic Let's Encrypt TLS
- All on a private Docker network `web`

License-server build artık CI tarafından (`.github/workflows/`) ghcr.io'a push'lanıyor; VPS `docker compose pull` ile yeni image'i alıp restart eder. Local source tree (`app/`) artık VPS'te gerekmiyor.

### docker-compose.yml'ın sahibi CI (2026-08-22'den beri)

`deploy/docker-compose.yml` her deploy'da VPS'e kopyalanır ve `/opt/orderdeck/docker-compose.yml` dosyasını **ezer**. VPS'te elle yapılan düzenlemeler bir sonraki master merge'inde kaybolur — değişiklik repo'da yapılmalı.

Bu kuraldan önce dosya hiç kopyalanmıyordu ve iki kopya sessizce ayrışmıştı:
canlı dosyada `Jwt__SecretKey`'in `:?` koruması yoktu, sağlayıcılar
`:-log`/`:-stub`'a düşüyordu ve repo'da yapılan yükleme tavanı değişikliği hiç
uygulanmamıştı. Aşağıdaki "compose bunlar olmadan başlamaz" cümlesi o dönemde
**canlı için doğru değildi**.

`.env` senkronize **edilmez** — sırları taşır, yalnız VPS'te durur. Bu yüzden
yeni bir `:?` zorunlu değişkeni eklerken önce `.env`'e satırı koy: eksikse CI'ın
`docker compose config -q` doğrulaması deploy'u durdurur (canlı dosyaya
dokunmadan).

## Layout on VPS

```
/opt/orderdeck/
├── docker-compose.yml
├── Caddyfile
├── .env                  # secrets (gitignored, file mode 600)
├── web-out/              # marketing site static export (Caddy bind-mount /srv/web)
│   └── downloads/        # OrderDeck-X.Y.Z-setup.exe public download
├── keys/                 # ASP.NET Core DataProtection keys (Docker volume mount)
├── tmp/                  # license-server /tmp (yedek zarfı açma alanı)
├── sql-data/             # SQL Server data files (Docker volume mount)
├── backups/              # encrypted customer backup blobs (per-customer dir)
└── caddy_data            # Docker named volume — Let's Encrypt certs
```

## Konteyner root koşmuyor — mount'lar uid 1654'e ait OLMALI (denetim O-11)

License-server imajı `USER $APP_UID` ile, yani **uid/gid 1654 (`app`)** olarak
koşuyor ve kök dosya sistemi `read_only: true`. Yazılabilir tek yerler bind
mount edilen üç dizin. Docker bind mount'ları host sahipliğini aynen aktarır ve
bu dizinler bugün `root:root` — **chown yapılmadan konteyner açılmaz veya daha
kötüsü sessizce bozulur.**

Bu adımlar **deploy'dan ÖNCE** çalıştırılmalıdır. Güvenlidir: hâlâ root koşan
mevcut konteyner dosya sahipliğini yok sayar, yani çalışan sisteme dokunmaz.

```bash
cd /opt/orderdeck
mkdir -p tmp
chown -R 1654:1654 keys backups tmp
# FCM servis hesabı anahtarı 0600 — sahibi değişmezse konteyner okuyamaz ve
# push bildirimleri sessizce ölür.
chown 1654:1654 /etc/orderdeck/firebase-service-account.json
```

Doğrulama (deploy sonrası):

```bash
docker exec orderdeck-license id                     # uid=1654(app)
docker exec orderdeck-license ls -la /app/keys       # key-*.xml, sahibi 1654
docker exec orderdeck-license touch /app/probe       # Read-only file system
```

## Uygulamanın SQL login'i `sa` değil (denetim O-11 Faz 3a)

`deploy/setup-app-sql-login.sh` `orderdeck_app` login'ini kurar: yalnız
`OrderDeckLicense` üzerinde `db_owner`, sunucu düzeyinde sıfır yetki. Script
idempotent, `.env`'deki parolayı korur ve sonunda **yeni login'le bağlanıp**
doğrular.

`db_owner` (datareader/writer değil) çünkü uygulama açılışta `Migrate()` ve
Hangfire `PrepareSchemaIfNecessary` çalıştırıyor — ikisi de DDL istiyor. Bunları
deploy zamanına taşımak (raporun önerdiği iki-kimlik kurgusu) her migration'ı bir
önceki uygulama sürümüyle geriye dönük uyumlu olmaya mecbur bırakırdı; kazancı
"uygulama kendi tablosunu düşüremesin" ile sınırlıyken bedeli her PR'a yayılıyordu.
Bilinçli olarak yapılmadı.

`sa` kaldırılmadı: healthcheck, `scripts/backup-sql-to-r2.sh`, `bootstrap-admin.sh`
ve `smoke-jwt-refresh.sh` host tarafında root koşan operatör aletleri ve `sa`'da
kalıyorlar. Değişen tek şey internete bakan process.

**Parola rotasyonu:** script'i yeniden çalıştırmadan önce `.env`'den
`APP_SQL_PASSWORD` satırını sil; script yeni parola üretip login'i `ALTER LOGIN`
ile günceller, sonra `docker compose up -d license-server`.

### `.bak` restore'undan sonra login'i MUTLAKA onar

Restore veritabanı **kullanıcısını** getirir ama **login'i getirmez** — login
`master`'da yaşar. Yeni instance'ta kullanıcının SID'i hiçbir login'e uymaz,
uygulama `Login failed` ile açılmaz ve hata yanıltıcıdır (parola doğrudur).
`setup-app-sql-login.sh` bu durumu `ALTER USER ... WITH LOGIN` ile onarır — restore
sonrası çalıştırmak zorunludur. Bkz. HA-PLAYBOOK.md cutover adım 1b.

### DataProtection anahtar yolu neden `.env`/compose'da açıkça yazılı

`DataProtection__KeysPath: "/app/keys"` compose'da **zorunlu**. Yoksa ASP.NET
Core anahtarları `$HOME/.aspnet/DataProtection-Keys` altında arar; root iken bu
`/root/...` idi ve compose oraya mount ediyordu. Kullanıcı değişince `$HOME`
`/home/app` olur — o dizin imajda yoktur, uygulama **yeni bir anahtar üretir ve
açılış başarılı olur**. `WhatsAppAccounts` satırındaki erişim token'ı ve iki
adımlı doğrulama PIN'i o anahtarla şifreli; çözülemez hâle gelirler ve
`WhatsAppAccountService.TryUnprotect` istisnayı yuttuğu için **hiçbir yerde hata
görünmez**. Kurtarma yolu yayıncının Embedded Signup'ı baştan yapmasıdır.

Host tarafındaki `./keys` dizini DEĞİŞMEDİ; yalnız konteyner içindeki hedefi
`/root/.aspnet/DataProtection-Keys` → `/app/keys` oldu. Aynı dosyalar, aynı
anahtarlar. `WORKDIR /app` de değişmemeli: DataProtection'ın uygulama ayracı
`ContentRootPath`'tir ve amaç zincirinin parçasıdır.

## Initial deploy

1. Provision .env (see template below)
2. `docker compose pull` (ghcr.io'dan image'leri çek)
3. `docker compose up -d`
4. EF migrations otomatik uygulanır (license-server `Database.Migrate()` startup'ta çalıştırır; eski deploy için bootstrap-migration-history.sql var — aşağıda)

## .env template

Copy to `/opt/orderdeck/.env` (file mode 600, do NOT commit):

```env
SQL_PASSWORD=ReplaceWithStrong32CharPassword!
# Uygulamanın SQL login'i — `sa` DEĞİL. setup-app-sql-login.sh üretir, elle yazma.
APP_SQL_PASSWORD=GenerateWith_setup-app-sql-login.sh
JWT_SECRET=ReplaceWith64CharRandomBase64String_GenerateWithOpenSSL
ADMIN_USERNAME=admin
ADMIN_PASSWORD_HASH=ReplaceWithBCryptHash

# Sağlayıcı seçimleri — ZORUNLU, compose bunlar olmadan başlamaz.
#
# Her birinin bir de "hiçbir iş yapmayan" karşılığı var (log / stub) ve o
# varsayılan olsaydı eksik bir satır üretimde sessiz arızaya dönüşürdü:
# sunucu açılır, sağlık kontrolü yeşil yanar, gönderimler "başarılı" döner
# ama SMS gitmez, WhatsApp gitmez, yüklenen dekont hiçbir yere yazılmaz.
# Bu yüzden hem compose hem sunucu eksik değerde açılışı durduruyor.
# Geliştirme makinesinde log/stub yazmak serbest — kural yalnız üretimde.
SMS_PROVIDER=netgsm
OrderDeck__WhatsApp__Provider=cloud
OrderDeck__BroadcastMedia__Provider=r2

# Optional: SMTP (set to real values when email features needed)
SMTP_HOST=smtp.example.com
SMTP_PORT=587
SMTP_USESSL=true
SMTP_USERNAME=
SMTP_PASSWORD=
SMTP_FROM=noreply@orderdeckapp.com

# Phase 5a — Cloud backup (set via setup-backup-key.sh)
BACKUP_MASTER_KEY=GenerateWith_setup-backup-key.sh
```

Generate strong values:
```bash
SQL_PASSWORD: openssl rand -base64 24 | tr -d '/+=' | head -c 32
JWT_SECRET:   openssl rand -base64 48
ADMIN_PASSWORD_HASH: see Phase 4a admin bootstrap docs (BCrypt-Net)
```

## Operations

- **Start**: `docker compose up -d`
- **Stop**: `docker compose down`
- **Update license-server** (after code merge to master, CI publishes new image):
  ```bash
  cd /opt/orderdeck && docker compose pull license-server && docker compose up -d license-server
  ```
- **Logs (live)**: `docker compose logs -f license-server`
- **DB backup**: `docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PASSWORD" -Q "BACKUP DATABASE OrderDeckLicense TO DISK = '/var/opt/mssql/backup/orderdeck-$(date +%F).bak'"`

## SQL Server sürüm yükseltme (artık elle)

`sqlserver` imajı **digest'e pinli** (`2022-latest@sha256:2dca9ee5…`). Sebep:
kayan etiket, host yeniden kurulduğunda üretimdekinden başka bir derleme
getiriyordu ve yeni bir CU veri dosyalarını **geri alınamaz** biçimde yükseltir.

Bunun bedeli: CU güncellemeleri artık kendiliğinden gelmiyor. Yükseltmek
istendiğinde sıra şu:

```bash
# 1) Taze yedek al ve R2'ye gittiğini DOĞRULA (yükseltme geri alınamaz).
/opt/orderdeck/scripts/backup-sql-to-r2.sh

# 2) Etiketin bugün neye çözüldüğüne bak.
curl -sI -H 'Accept: application/vnd.docker.distribution.manifest.list.v2+json' \
  https://mcr.microsoft.com/v2/mssql/server/manifests/2022-latest | grep -i docker-content-digest

# 3) deploy/docker-compose.yml'daki digest'i güncelleyip PR aç (dosyanın sahibi CI).
```

Yükseltme deploy'u `sqlserver` konteynerini yeniden yaratır → veritabanı kısa
süre düşer → smoke testi düşerse #321'in geri alması tetiklenir ama **veri
dosyası yükseltmesini geri almaz**. Bu yüzden izlenen bir pencerede yapılmalı,
sıradan bir merge'ün yan etkisi olarak değil.

## Deploy geri alma (rollback)

**Normalde elle bir şey yapman gerekmez.** 2026-08-22'den beri deploy workflow'u
kendi geri alıyor: `.env`'e dokunmadan önce mevcut `LICENSE_SERVER_TAG` +
`.deployed_sha` VPS'te `/opt/orderdeck/.rollback` dosyasına yazılır; SSH deploy'u
kalıcı olarak düşerse **veya** smoke testi (`/ready`) 60 sn içinde 200 vermezse
önceki sürüme dönülür ve runner `/ready`'yi tekrar yoklar. Geri alma da tutmazsa
iş `ELLE MÜDAHALE` hatasıyla kırmızı kalır.

Script'i CI her deploy'da kopyalar (`deploy/scripts/rollback-license-server.sh` →
`/opt/orderdeck/scripts/`), yani elle kurulum yok ve dosya hep repo'daki sürümle
aynı.

Elle geri alma (CI dışında, gece 3'te):

```bash
cd /opt/orderdeck
scripts/rollback-license-server.sh --apply     # --run-id VERME: CI dışı çalıştırmada
                                               # koşu eşleşmesi aranmaz
```

`--apply` şunları yapar: `.env`'deki etiketi kayıtlı önceki `master-<sha>`'ya
sed'ler, compose bu deploy'da değiştiyse `docker-compose.yml.prev`'i geri koyar
(bozuk hâli `docker-compose.yml.failed` olarak saklar), `up -d` eder,
`.deployed_sha`'yı geri yazar ve konteynerin `running` olduğunu doğrular.

Kayıtlı durum yoksa (`.rollback` silinmişse — başarılı deploy sonrası silinir)
klasik yol: `.env`'de `LICENSE_SERVER_TAG`'i bir önceki `master-<short-sha>`'ya
çevir, `docker compose up -d license-server`. Tüm sha etiketleri değişmez;
imaj prune 72 saatlik geçmişi tutar, daha eskisi GHCR'dan çekilir.

## EF migration history bootstrap (one-time, before first Migrate() deploy)

The original deploy used `EnsureCreated()` so the DB has all the schema but no
`__EFMigrationsHistory` table. The app now calls `Database.Migrate()` on
startup; without the history table EF would try to re-apply every migration
and fail with "table already exists".

Apply once before the next deploy:

```bash
scp deploy/bootstrap-migration-history.sql root@72.62.53.86:/tmp/
ssh root@72.62.53.86
docker cp /tmp/bootstrap-migration-history.sql orderdeck-sqlserver:/tmp/
docker exec -i orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SQL_PASSWORD" -d OrderDeckLicense -C -No \
  -i /tmp/bootstrap-migration-history.sql
```

Idempotent — re-running is a no-op.

## Cloud backup setup (Phase 5a)

After initial deploy, bootstrap the AES master key:

```bash
ssh root@72.62.53.86
/opt/orderdeck/setup-backup-key.sh
```

This generates a 64-hex (32-byte) random key, writes it to `/opt/orderdeck/.env`,
and restarts the license-server. Backups are stored at `/opt/orderdeck/backups/{customerId}/`.

**Rotation (Phase 5b — versioned key ring):** Master keys are now a versioned
ring. Rotating no longer breaks history — old keys stay in the ring and decrypt
the blobs they originally wrote. To rotate:

1. Generate a fresh 64-hex key (`openssl rand -hex 32`).
2. Add it to `.env` at the next free version slot, keeping all existing keys
   in place. For example, going from active=v0 to active=v1:

   ```env
   # Existing legacy field — leave it; it's the v0 key the historical blobs
   # were encrypted with. Removing it would brick those blobs.
   BACKUP_MASTER_KEY=<existing 64-hex>

   # New ring entries
   BACKUP_MASTERKEYS_1=<new 64-hex>
   BACKUP_ACTIVEKEYVERSION=1
   ```
3. Restart the license-server. From now on every new upload writes a v1
   envelope; v0 blobs continue to decrypt with the v0 key.
4. Eventually (when no v0 blobs remain — check via SQL
   `SELECT KeyVersion, COUNT(*) FROM CustomerBackups GROUP BY KeyVersion;`),
   you can remove the v0 key from `.env`. Keep ALL versions referenced by
   any live row.

**Compromise scenario:** if a key version is suspected leaked, bump active
to a fresh version, then audit + delete affected blobs (they're encrypted
under the leaked key, retention can't help). Customers will upload fresh
backups under the new active version.

### Upload size + concurrency budget

`Backup__MaxBlobSizeMb` (64), `Backup__MaxConcurrentUploads` (2) and the
license-server `mem_limit` (1g) share one budget and must be changed together.
The upload path buffers rather than streams — AES-GCM's one-shot API needs the
plaintext and the envelope in memory at the same time — so a request holds
roughly **2 × blob**. Peak is therefore `MaxBlobSizeMb × 2 × MaxConcurrentUploads`
≈ 256 MB.

The per-customer rate limit (`backup-upload`, 6/hour) does **not** bound this:
it is partitioned by customer id, so it caps how often one customer uploads,
not how many upload at once. That is what `MaxConcurrentUploads` is for; when
the gate is full a request gets 503 + `Retry-After` after
`Backup__UploadQueueWaitSeconds`.

> Historical note: this value used to read 200 and had no effect whatsoever.
> The server's default request-body cap (30,000,000 bytes ≈ 28.6 MB) bound
> first, so the app's own 413 branch was unreachable and raising the setting
> changed nothing. The limit is now applied per request, which means the
> configured number is finally the real one.

Sizing reality check: a real installation's `orderdeck.db` was 4.9 MB, 1.6 MB
zipped. Product photos are **not** in the backup (the DB stores only the R2
object key; the bytes sit in a separate on-disk cache), and the stock/catalog
replica is rewritten each sync so it does not grow over time. The terms that
do grow are history: `Label`, `Customer`, `GiveawayParticipant`, `Payment`,
`Shipment`.

### Off-host replication (gecelik cron → R2)

Saha dışı kopyalama **uygulamada değil**, VPS'te cron ile yapılır. Hepsi tek
bucket'ta: `s3://orderdeck-prod-backups/`.

| İçerik | Script | Cron | Uzak nesne |
|---|---|---|---|
| SQL `.bak` | `backup-sql-to-r2.sh` | `0 3 * * *` | `sql-bak/orderdeck-<tarih>.bak.gz.gpg` |
| DataProtection anahtarları | `backup-keys-to-r2.sh` | `30 3 * * *` | `keys/keys-<tarih>.tar.gz.gpg` |
| `.env` | `backup-env-to-r2.sh` | `45 3 * * *` | `env/env-<tarih>.gpg` |
| Müşteri yedek blob'ları | `backup-blobs-to-r2.sh` | `0 4 * * *` | `customer-blobs/…` (AES-256-GCM) |

Dördü birlikte anlamlı: blob'lar şifreli, çözmek için `.env`'deki ana anahtar
gerekli, hangi blob'un hangi anahtar sürümüyle yazıldığı ise SQL'deki
`CustomerBackups.KeyVersion` satırında. Biri eksikse geri dönüş yapılamaz.

**Dördü de uzakta şifreli**, ilk üçü aynı GPG parolasıyla
(`/opt/orderdeck/.env-backup-pass`; asıl kopya parola yöneticisinde). Bir süre
`.bak` ve anahtarlar **düz** gidiyordu — o hâlde `.env`'i şifrelemek tiyatroydu:
R2'yi okuyabilen birinin `.env`'e ihtiyacı yok, müşteri verisi zaten `.bak`'ın
içinde, imzalama anahtarları da düz XML olarak yanında. 2026-08-22 restore
tatbikatı bunu somutlaştırdı: tek bir *salt-okunur* R2 token'ıyla tüm prod
veritabanı temiz bir makinede restore edilebildi.

Tehdit modeli: bu şifreleme VPS'i ele geçirene karşı hiçbir şey yapmaz (adam
zaten canlı DB'yi okuyor), **R2'nin veya bir token'ın sızmasına** karşı yapar.
Yereldeki `.bak` kopyaları bilerek şifresiz; host'a erişen zaten DB'ye erişiyor,
şifrelemek yalnızca restore'u zorlaştırırdı.

Anahtarlar `aws s3 sync` yerine **tarihli tarball** olarak gidiyor: sync dosya
başına şifreleme yapamaz. Anahtarlar KB boyutunda ve nadiren değişiyor, her
gece tam anlık görüntü almak bedava — üstelik restore ederken yarı senkronize
bir dizin yerine tutarlı bir halka veriyor.

**Neden uygulama içi replikasyon yok:** eski `Backup:S3` yolu silindi. İki
sebeple: R2'de hiç çalışmazdı (`AuthenticationRegion` ve `DisablePayloadSigning`
eksikti) ve `Task.Run` ile ateşle-unut olduğu için hangi blob'un kopyalandığı
kayda geçmiyordu — süreç yeniden başlayınca (her deploy) uçuştaki iş sessizce
kayboluyordu. `aws s3 sync` yapı gereği artımlı ve tekrar güvenli: bir gece
kaçan dosya ertesi gece gider. Bedeli, kopyanın anlık değil en fazla ~24 saat
gecikmeli olması; blob o sürede zaten yerelde duruyor.

Blob senkronu `--delete` kullanır: retention ve KVKK silme talebi off-site
kopyada da uygulanmalı. R2'de nesne sürümleme **yok** (yalnız prefix bazlı
bucket lock var), o yüzden script'te toplu silme tel tuzağı var — yerel blob
sayısı uzaktakinin yarısının altına düşerse senkron çalışmaz. Kasıtlıysa
`ALLOW_MASS_DELETE=1` ile çalıştır.

#### GPG parolası — kurulum (bir kez, üç script için ortak)

Script'ler parolayı **üretmez**. Üretselerdi parola yalnız bu host'ta yaşayan
bir dosya olurdu ve host'u kaybettiğimizde R2'deki şifreli yedekler de
kullanılamaz hâle gelirdi — yani tam korumaya çalıştığımız senaryoda işe
yaramazdı.

```bash
# 1) Parolayı ÜRET ve ÖNCE parola yöneticine kaydet:
openssl rand -base64 48

# 2) Sonra VPS'e yaz:
ssh root@72.62.53.86
umask 077 && printf '%s' '<parola>' > /opt/orderdeck/.env-backup-pass

# 3) Elle bir kez çalıştır ve log'a bak:
/opt/orderdeck/scripts/backup-env-to-r2.sh
```

Script'ler yüklemeden **önce** şifre-çöz turu yapar (şifrele–çöz–`cmp`); tur
başarısızsa yükleme yapılmaz. Şifrelenmiş ama açılamayan bir yedek, hiç yedek
almamaktan kötüdür — felaket anına kadar yedeğin olduğunu sanırsın. Bozuk bir
parola dosyası böylece bozulduğu gece patlar, ihtiyaç duyulduğu gece değil.

**Parolayı döndürürsen eskisini parola yöneticisinden silme.** R2'deki geçmiş
`.gpg` nesneleri eski parolayla şifreli kalır; retention penceresi (30 gün)
dolana kadar iki parola da geçerlidir.

#### Geri yükleme

Parola yalnız parola yöneticisinde olduğu için hepsi elle çözülür:

```bash
gpg --decrypt env-YYYY-MM-DD.gpg          > .env
gpg --decrypt orderdeck-YYYY-MM-DD.bak.gz.gpg | gunzip > orderdeck.bak
gpg --decrypt keys-YYYY-MM-DD.tar.gz.gpg  | tar -xz -C /opt/orderdeck/keys
```

R2 kimlik bilgileri **ölen host'ta** (`/root/.aws/credentials`) ve `.env`'in
içinde (`R2__SecretAccessKey`) — ama `.env` R2'nin içinde olduğu için ondan
bootstrap edilemez. Kurtarma **Cloudflare hesabına giriş** gerektirir: panelden
yeni bir *Object Read only* R2 token'ı üretilir. Parola yöneticisinde saklanması
gereken şey uzun ömürlü bir token değil (bayatlar, kimse fark etmez), Cloudflare
hesap erişimidir.

#### Bir kereye mahsus: eski şifresiz nesneleri sil

Şifrelemeye geçmeden önce R2'ye **düz** yüklenmiş nesneler retention penceresi
boyunca orada durmaya devam eder ve otomatik temizlik onlara dokunmaz (tarih
eşleşmesi yalnız yeni adlandırmayı tanır — tanımadığı bir şeyi silmesini
istemiyoruz). Şifreli ilk tur başarılı olduktan **sonra** elle:

```bash
aws s3 rm s3://orderdeck-prod-backups/sql-bak/ --recursive --exclude '*' --include '*.bak.gz' --profile r2
aws s3 rm s3://orderdeck-prod-backups/keys/    --recursive --exclude '*' --include '*.xml'    --profile r2
```

## DNS

A record: `license.orderdeckapp.com` → VPS IP (72.62.53.86), TTL 300, NOT proxied.
Caddy will automatically obtain the Let's Encrypt cert on first request once DNS resolves.
