# OrderDeck

Live-stream e-commerce desktop tool — Instagram/TikTok/Facebook/YouTube canlı yayın chat'ini OBS overlay'i + sipariş kuyruğu + etiket yazıcısıyla birleştirir.

## Kullanıcı misin?

Kurulum rehberi: **[SETUP.md](SETUP.md)** (Türkçe)

İndirme: [orderdeckapp.com/indir](https://orderdeckapp.com/indir)

## Geliştirici misin?

### Stack
- **OrderDeck.App** — WPF .NET 10 desktop (operatör arayüzü)
- **OrderDeck.Core** — domain (sales / customers / sessions / giveaways / payments / shipments)
- **OrderDeck.Chat** — chat ingestion (Chrome extension WS bridge + YouTube scraper)
- **OrderDeck.Labeling** — thermal printer label rendering (PrintDocument)
- **OrderDeck.Overlay** — embedded ASP.NET Core HTTP server (`localhost:4747` + fallback 4757-4760) for OBS browser sources
- **OrderDeck.Licensing** — license activation + heartbeat + Payment/Shipment sync clients
- **OrderDeck.LicenseServer** — VPS-deployed ASP.NET Core API (license issuance + customer accounts + Panel API)
- **Extension/** — Chrome MV3 extension (DOM scraper for IG/TT/FB livestream chat) — [Chrome Web Store](https://chromewebstore.google.com/) yayında
- **web/** — Next.js marketing site ([orderdeckapp.com](https://orderdeckapp.com))
- **[OrderDeck-Mobile](https://github.com/Ulysses07/OrderDeck-Mobile)** (ayrı repo) — React + Vite Panel app (dekont onay/red, bekleyen kargo dosyaları)

### Domain feature'ları
- **Sales**: stream session bazlı label/sipariş kuyruğu
- **Customers**: müşteri kaydı + blacklist + intake form upsert
- **Payments**: PDF dekont parser (10+ banka format) + matcher + WPF↔server sync
- **Shipments** (kümülatif kargo eşik): cross-session kargo dosyası + threshold modal + WhatsApp "kazandın"
- **Giveaways**: keyword-based participant pool + overlay animation

### Bina
```bash
dotnet build       # tüm solution
dotnet test        # 560+ unit/integration test (OrderDeck.Tests + LicenseServer + Licensing)
dotnet run --project OrderDeck.App   # local dev launch
```

### Installer build
```powershell
installer\build.ps1 -Version 1.0.0
# Çıktı: dist\OrderDeck-1.0.0-setup.exe
```
Detay: [installer/build.ps1](installer/build.ps1) ve [installer/orderdeck.iss](installer/orderdeck.iss).

### Repo yapısı
```
OrderDeck.App/           # WPF (entry point)
OrderDeck.Core/          # domain (sales/customers/payments/shipments/giveaways)
OrderDeck.Chat/          # chat ingestion
OrderDeck.Labeling/      # thermal printer label rendering
OrderDeck.Overlay/       # OBS overlay HTTP server + animation plugins
OrderDeck.Licensing/     # license + sync clients (Payment + Shipment)
OrderDeck.LicenseServer/ # license server (VPS, ghcr.io image)
OrderDeck.Tests/         # WPF + Core tests
OrderDeck.Licensing.Tests/
OrderDeck.LicenseServer.Tests/
Extension/               # Chrome MV3 extension (Web Store yayında)
web/                     # Next.js marketing site
deploy/                  # VPS docker-compose + Caddyfile
installer/               # Inno Setup script + build.ps1
docs/superpowers/        # spec'ler + plan'lar (Phase 1-5 + kargo feature)
```

### Veri klasörleri (kullanıcı makinesinde)
- Settings + DB + auth: `%USERPROFILE%\Documents\OrderDeck\data\`
- Loglar: `%USERPROFILE%\Documents\OrderDeck\Logs\`
- WebView2 user-data: `%LOCALAPPDATA%\OrderDeck\WebView2\`

### CI
GitHub Actions: `.github/workflows/build-test.yml` (Windows runner + dotnet test).

### Lisans
Kapalı kaynak — `Ulysses07` özel mülk. Pull request kabul edilmiyor (henüz).
