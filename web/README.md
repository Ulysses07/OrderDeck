# OrderDeck Marketing Site

Production: <https://orderdeckapp.com>

`Next.js 15 (App Router)` + `Tailwind CSS v4` + `TypeScript`. İki dilli (TR default, EN `/en/`). MDX blog. Statik export → Caddy `file_server` ile servis ediliyor (Node.js runtime yok).

## Geliştirme

```bash
cd web
npm install
npm run dev      # http://localhost:3000
```

## Build

```bash
npm run build    # → web/out/  (statik HTML + assets)
```

## Deploy (manuel)

```bash
# Lokal makineden:
rsync -avz --delete web/out/ user@vps:/opt/orderdeck/web-out/
```

Caddy `file_server` klasör değişimini anında görür; restart gerekmez.

## Deploy (CI)

`.github/workflows/web-deploy.yml` → main'e push olduğunda otomatik build + rsync deploy. Secrets:
- `DEPLOY_SSH_KEY` — private key
- `DEPLOY_HOST` — VPS hostname/IP
- `DEPLOY_USER` — SSH user (genelde `deploy` veya `ubuntu`)

## Klasör yapısı

```
web/
  app/
    (tr)/           — TR sayfalar, html lang="tr"
      page.tsx           /
      ozellikler/        /ozellikler/
      fiyatlandirma/     /fiyatlandirma/
      sss/               /sss/
      blog/              /blog/, /blog/[slug]/
      gizlilik-politikasi/
      kullanim-kosullari/
      iletisim/
    (en)/           — EN sayfalar, html lang="en"
      en/page.tsx        /en/
      en/features/       /en/features/
      en/pricing/        /en/pricing/
      en/faq/            /en/faq/
      en/blog/           /en/blog/, /en/blog/[slug]/
      en/privacy-policy/
      en/terms-of-service/
      en/contact/
  components/       — Shared UI (Nav, Footer, Hero, FeatureCard, ...)
  content/blog/{tr,en}/*.mdx   — Blog yazıları
  lib/              — i18n + blog helpers
  messages/{tr,en}.ts          — UI string sözlükleri
  public/           — robots.txt, og-image, vs.
```

## Yeni blog yazısı eklemek

1. `content/blog/{tr,en}/YYYY-MM-DD-slug.mdx` oluştur.
2. Frontmatter:
   ```mdx
   ---
   title: "Başlık"
   description: "Kısa açıklama (SEO için)"
   date: "2026-05-01"
   author: "OrderDeck Ekibi"
   tags: ["etiket1", "etiket2"]
   ---
   ```
3. `npm run build` → otomatik `/blog/<slug>/` rotası üretilir.

## Privacy / Terms güncellenmesi

Yasal sayfalar `app/(tr)/gizlilik-politikasi/page.tsx` ve `app/(en)/en/privacy-policy/page.tsx` (terms için karşılıkları). Her güncelleme sonrası `EFFECTIVE_DATE` sabitini değiştir; önemli içerik değişiminde kayıtlı kullanıcılara e-posta git.

## YouTube API audit hazırlık

OAuth verification için Google'a aşağıdaki URL'ler gönderiliyor:
- Homepage: `https://orderdeckapp.com/en/`
- Privacy Policy: `https://orderdeckapp.com/en/privacy-policy/`
- Terms of Service: `https://orderdeckapp.com/en/terms-of-service/`

Privacy sayfasında istenen şu maddeler **mutlaka** olmalı (silme!):
- "Google API Services User Data Policy" + "Limited Use" beyanı
- İstenen OAuth scope listesi (`youtube`, `youtube.force-ssl`)
- Veri saklamadığımıza dair açık beyan
- Kullanıcının nasıl izin iptal edebileceği

## Tasarım sistemi

Renkler `app/globals.css`'te `@theme` bloğunda tanımlı. Desktop app `OrderDeck.App/Themes/DarkControls.xaml`'daki paletle uyumlu (marka tutarlılığı). Yeni renk eklerken oradan da senkronize tut.
