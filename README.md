# OrderDeck

Live-stream e-commerce desktop tool — Instagram/TikTok/Facebook/YouTube canlı yayın chat'ini OBS overlay'i + sipariş kuyruğu + etiket yazıcısıyla birleştirir.

## Kullanıcı misin?

Kurulum rehberi: **[SETUP.md](SETUP.md)** (Türkçe)

İndirme: [orderdeckapp.com/indir](https://orderdeckapp.com/indir)

## Geliştirici misin?

### Stack
- **OrderDeck.App** — WPF .NET 10 desktop (operatör arayüzü)
- **OrderDeck.Core** — domain (sales / customers / sessions / giveaways)
- **OrderDeck.Chat** — chat ingestion (Chrome extension WS bridge + YouTube scraper)
- **OrderDeck.Overlay** — embedded ASP.NET Core HTTP server (`localhost:4747`) for OBS browser sources
- **OrderDeck.Licensing** — license activation + heartbeat
- **OrderDeck.LicenseServer** — VPS-deployed ASP.NET Core API (license issuance + customer accounts)
- **Extension/** — Chrome MV3 extension (DOM scraper for IG/TT/FB livestream chat)
- **web/** — Next.js marketing site

### Bina
```bash
dotnet build       # tüm solution
dotnet test        # 600+ unit/integration test
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
OrderDeck.Core/          # domain
OrderDeck.Chat/          # chat ingestion
OrderDeck.Overlay/       # OBS overlay HTTP server + animation plugins
OrderDeck.Licensing/     # license client
OrderDeck.LicenseServer/ # license server (VPS)
OrderDeck.Tests/         # WPF + Core tests
OrderDeck.Licensing.Tests/
OrderDeck.LicenseServer.Tests/
Extension/               # Chrome MV3 extension
web/                     # Next.js marketing site
deploy/                  # VPS docker-compose
installer/               # Inno Setup script + build.ps1
```

### Veri klasörleri (kullanıcı makinesinde)
- Settings + DB + auth: `%USERPROFILE%\Documents\OrderDeck\data\`
- Loglar: `%USERPROFILE%\Documents\OrderDeck\Logs\`
- WebView2 user-data: `%LOCALAPPDATA%\OrderDeck\WebView2\`

### CI
GitHub Actions: `.github/workflows/build-test.yml` (Windows runner + dotnet test).

### Lisans
Kapalı kaynak — `Ulysses07` özel mülk. Pull request kabul edilmiyor (henüz).
