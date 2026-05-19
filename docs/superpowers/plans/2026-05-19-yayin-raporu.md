# Yayın Excel Raporu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mobile panel'de tamamlanmış yayınların sipariş özetini CSV olarak indirip Capacitor Share API ile paylaşma (WhatsApp/Drive/Email/vs.).

**Architecture:** Client-side CSV (sıfır server kodu). Mevcut `useSessions()` + `useSessionOrders(id)` hook'larından gelen veriyi `lib/sessionExport.ts` CSV string'e çevirir, `@capacitor/filesystem` ile cache dizinine yazar, `@capacitor/share` ile native share sheet açar.

**Tech Stack:** React + Vite + TanStack Query + Capacitor 6 + `@capacitor/filesystem` (yeni) + `@capacitor/share` (mevcut).

**Spec:** `docs/superpowers/specs/2026-05-19-yayin-raporu-design.md`

**Branch:** `feat/yayin-raporu` in OrderDeck-Mobile (main'den). **Tek PR**.

**Server:** Değişiklik yok. Mevcut `/api/panel/sessions` + `/api/panel/sessions/{id}/orders` yeterli.

---

## Mobile tasks (OrderDeck-Mobile repo, branch `feat/yayin-raporu`)

### Task 1: Plugin install + lib/sessionExport.ts

**Files:**
- Modify: `apps/panel/package.json` (`@capacitor/filesystem` ekle)
- Modify: `package-lock.json` (root) + Android auto-gen gradle dosyaları (cap sync output)
- Create: `apps/panel/src/lib/sessionExport.ts`

- [ ] **Step 1: Branch aç + plugin install**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git checkout main && git pull origin main --ff-only
git checkout -b feat/yayin-raporu

cd apps/panel
npm install @capacitor/filesystem@^6
```

Capacitor 6 ile uyumlu sürüm — peer dep conflict olmamalı. Eğer olursa:
```bash
npm install @capacitor/filesystem@^6 --legacy-peer-deps
```

- [ ] **Step 2: Capacitor Android sync**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npx cap sync android
```

Bu Android tarafına plugin'i ekler. Manifest izni gerektirmez (Filesystem.Directory.Cache app-private).

- [ ] **Step 3: lib/sessionExport.ts oluştur**

`apps/panel/src/lib/sessionExport.ts` (yeni):

```typescript
import { Filesystem, Directory, Encoding } from "@capacitor/filesystem";
import { Share } from "@capacitor/share";
import { Order } from "../api/queries";

const PLATFORM_LABELS: Record<string, string> = {
  instagram: "Instagram",
  tiktok: "TikTok",
  facebook: "Facebook",
  youtube: "YouTube",
  web: "Web",
};

const TR_NORMALIZE: Record<string, string> = {
  "Ş": "S", "ş": "s",
  "İ": "I", "ı": "i",
  "Ğ": "G", "ğ": "g",
  "Ü": "U", "ü": "u",
  "Ö": "O", "ö": "o",
  "Ç": "C", "ç": "c",
};

/**
 * Orders listesinden CSV string oluşturur.
 * UTF-8 BOM + ; separator + TR locale uyumlu format.
 * Sıralama: AddedAt ASC.
 */
export function buildOrdersCsv(orders: Order[]): string {
  const sorted = [...orders].sort(
    (a, b) => new Date(a.addedAt).getTime() - new Date(b.addedAt).getTime(),
  );
  const headers = ["Tarih", "Müşteri", "Platform", "Sipariş Kodu", "Açıklama", "Tutar (₺)", "Durum"];
  const lines = [headers.map(quoteCell).join(";")];

  for (const o of sorted) {
    const row = [
      formatTarih(o.addedAt),
      o.displayName ?? o.username ?? "—",
      PLATFORM_LABELS[o.platform.toLowerCase()] ?? o.platform,
      o.code ?? "",
      truncate(o.messageText ?? "", 200),
      formatTutar(o.price),
      computeDurum(o),
    ];
    lines.push(row.map(quoteCell).join(";"));
  }

  return lines.join("\r\n");
}

/**
 * Yayın başlığını dosya adı için sanitize eder.
 * TR karakter normalize → lowercase → non-alnum → "-" → trim.
 */
export function buildFilename(sessionTitle: string | null | undefined, sessionId: string): string {
  const today = new Date();
  const yyyymmdd = `${today.getFullYear()}${pad2(today.getMonth() + 1)}${pad2(today.getDate())}`;

  let slug = (sessionTitle ?? "")
    .split("")
    .map((c) => TR_NORMALIZE[c] ?? c)
    .join("")
    .toLowerCase()
    .replace(/\s+/g, "-")
    .replace(/[^a-z0-9-]/g, "")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");

  if (!slug) {
    slug = sessionId.substring(0, 8);
  }

  return `yayin-rapor-${slug}-${yyyymmdd}.csv`;
}

/**
 * CSV'yi cache'a yazıp Capacitor Share ile native sheet açar.
 */
export async function shareSessionExport(
  sessionTitle: string | null,
  sessionId: string,
  orders: Order[],
): Promise<void> {
  const filename = buildFilename(sessionTitle, sessionId);
  const csv = "\uFEFF" + buildOrdersCsv(orders);

  const { uri } = await Filesystem.writeFile({
    path: filename,
    directory: Directory.Cache,
    data: csv,
    encoding: Encoding.UTF8,
  });

  await Share.share({
    title: `${sessionTitle ?? "Yayın"} - Yayın Raporu`,
    url: uri,
    dialogTitle: "Paylaş",
  });
}

// ─── Helpers ─────────────────────────────────────────────

function formatTarih(iso: string): string {
  const d = new Date(iso);
  return [
    pad2(d.getDate()),
    pad2(d.getMonth() + 1),
    d.getFullYear(),
  ].join(".") + " " + pad2(d.getHours()) + ":" + pad2(d.getMinutes());
}

function formatTutar(value: number): string {
  // TR locale: ondalık ayraç virgül. CSV separator ; olduğu için güvenli.
  return value.toFixed(2).replace(".", ",");
}

function computeDurum(o: Order): string {
  if (o.cancelledAt) return "İptal";
  if (o.isShippingFee) return "Kargo ücreti";
  if (o.isTentativeBackup) return "Yedek";
  if (o.printedAt) return "Yazıldı";
  return "Beklemede";
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s;
  return s.substring(0, max - 1) + "…";
}

function quoteCell(s: string | null | undefined): string {
  const v = String(s ?? "");
  const escaped = v.replace(/"/g, '""');
  return `"${escaped}"`;
}

function pad2(n: number): string {
  return n < 10 ? "0" + n : String(n);
}
```

**Önemli:** `Order` type'ının import'u `"../api/queries"`'tan geliyor. Mevcut `Order` type'ında bu alanlar olduğunu varsayıyoruz:
- `addedAt: string`
- `cancelledAt: string | null`
- `printedAt: string | null`
- `displayName: string | null`
- `username: string`
- `platform: string`
- `code: string | null`
- `messageText: string`
- `price: number`
- `isShippingFee: boolean`
- `isTentativeBackup: boolean`

Eğer field adları farklıysa (örn. `addedAt` yerine `added_at`), `api/queries.ts`'teki `Order` type definition'ına bakıp adapt edilir. (Mobile codebase'de TypeScript camelCase kullanıyor — server PascalCase'i JSON serialization'da camelCase'e çeviriyor.)

- [ ] **Step 4: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean. Eğer `Order` type field name conflict varsa, `api/queries.ts`'i oku ve `lib/sessionExport.ts`'i adapt et.

- [ ] **Step 5: Commit (DO NOT push)**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/package.json package-lock.json \
        apps/panel/src/lib/sessionExport.ts
git commit -m "feat(panel): CSV export library + @capacitor/filesystem plugin"
```

Eğer `cap sync` Android altında auto-gen dosyalarını değiştirdiyse (`capacitor.build.gradle`, `capacitor.settings.gradle`) onları da add et — broadcast posts ve hızlı istatistik task'larında olduğu gibi.

---

### Task 2: YayinRaporuScreen + route + DahaFazla NavRow + placeholder kaldırma

**Files:**
- Create: `apps/panel/src/screens/YayinRaporuScreen.tsx`
- Modify: `apps/panel/src/App.tsx` (route ekle)
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx` (NavRow ekle, placeholder kaldır)

- [ ] **Step 1: YayinRaporuScreen oluştur**

`apps/panel/src/screens/YayinRaporuScreen.tsx` (yeni):

```typescript
import { useState } from "react";
import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { Order, useSessions } from "../api/queries";
import { formatRelative, formatTl } from "../lib/format";
import { shareSessionExport } from "../lib/sessionExport";
import { apiClient } from "../api/client";

type SessionItem = {
  id: string;
  title: string | null;
  startedAt: string;
  endedAt: string | null;
  orderCount: number;
  totalAmount: number;
};

export function YayinRaporuScreen() {
  const { data: sessions = [], isLoading, isError, refetch } = useSessions();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [errorId, setErrorId] = useState<string | null>(null);
  const qc = useQueryClient();

  async function exportSession(s: SessionItem) {
    setBusyId(s.id);
    setErrorId(null);
    try {
      // Cache'i kullan ya da fresh fetch et
      const cached = qc.getQueryData<Order[]>(["session-orders", s.id]);
      const orders =
        cached ??
        (await apiClient
          .get<Order[]>(`/api/panel/sessions/${s.id}/orders`)
          .then((r) => r.data));
      qc.setQueryData(["session-orders", s.id], orders);
      await shareSessionExport(s.title ?? "Yayın", s.id, orders);
    } catch (err) {
      setErrorId(s.id);
      console.error("Export failed", err);
    } finally {
      setBusyId(null);
    }
  }

  return (
    <main className="px-5 pt-6 pb-24">
      <header className="mb-4">
        <Link to="/daha-fazla" className="text-text-muted text-xs hover:text-text">
          ← Geri
        </Link>
        <h1 className="text-2xl font-bold mt-1">Yayın raporu</h1>
        <p className="text-text-muted text-sm mt-0.5">
          Bir yayın seç, Excel olarak paylaş
        </p>
      </header>

      {isLoading ? (
        <div className="space-y-2">
          {[0, 1, 2].map((i) => (
            <div key={i} className="h-24 rounded-xl bg-bg-surface animate-pulse" />
          ))}
        </div>
      ) : isError ? (
        <div className="bg-danger/10 border border-danger/30 rounded-xl p-4 text-danger text-sm">
          Yüklenemedi.
          <button onClick={() => void refetch()} className="ml-2 underline">
            Tekrar dene
          </button>
        </div>
      ) : sessions.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-5xl mb-3">📭</p>
          <p className="text-text font-medium">Henüz yayın yok</p>
          <p className="text-text-muted text-xs mt-1">
            WPF App'te yayın başlatınca burada görünür.
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {sessions.map((s) => (
            <li
              key={s.id}
              className="bg-bg-surface rounded-xl border border-bg-elevated p-3"
            >
              <div className="flex justify-between items-start gap-3 mb-2">
                <div className="min-w-0 flex-1">
                  <p className="text-text font-medium truncate">
                    {s.title ?? "Yayın"}
                  </p>
                  <p className="text-text-muted text-xs mt-0.5">
                    {s.orderCount} sipariş ·{" "}
                    {s.endedAt
                      ? `${formatRelative(s.endedAt)} bitti`
                      : `${formatRelative(s.startedAt)} başladı`}
                  </p>
                </div>
                <p className="text-text font-semibold whitespace-nowrap">
                  {formatTl(s.totalAmount)}
                </p>
              </div>
              <button
                onClick={() => void exportSession(s)}
                disabled={busyId === s.id}
                className="w-full py-2 rounded-lg bg-accent text-white text-sm font-medium disabled:opacity-50"
              >
                {busyId === s.id ? "Hazırlanıyor..." : "📊 Excel paylaş"}
              </button>
              {errorId === s.id && (
                <p className="text-danger text-xs mt-2">
                  Paylaşılamadı, tekrar dene.
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
```

**Notlar:**
- `useSessions()` mevcut hook'tan dönen `SessionItem` shape'i `api/queries.ts`'tan alınır. Eğer field adları farklıysa (örn. `total` vs `totalAmount`) adapt edilir.
- `useQueryClient` mevcut TanStack pattern — broadcast posts'ta da kullanılıyor.
- `apiClient.get<Order[]>` doğrudan çağrı (lazy fetch için useQuery overhead'siz).
- `formatTl`, `formatRelative` mevcut `lib/format.ts`'te.
- "Excel paylaş" buton tek tıkta: lazy orders fetch → CSV → Filesystem.Cache → Share.share.

- [ ] **Step 2: App.tsx'a route ekle**

`apps/panel/src/App.tsx` — diğer screen import'larının yanına ekle (HizliIstatistikScreen import'unun altına):

```typescript
import { YayinRaporuScreen } from "./screens/YayinRaporuScreen";
```

Route'lar arasına ekle, `{ path: "/hizli-istatistik", element: <HizliIstatistikScreen /> },` satırının altına:

```typescript
          { path: "/yayin-raporu", element: <YayinRaporuScreen /> },
```

- [ ] **Step 3: DahaFazlaScreen güncelle**

`apps/panel/src/screens/DahaFazlaScreen.tsx`:

**3a. "İçerik" section'da, "Hızlı istatistik" NavRow'unun altına yeni NavRow ekle:**

```typescript
        <NavRow
          to="/yayin-raporu"
          label="Yayın raporu"
          hint="Bir yayını CSV olarak indir ve paylaş"
        />
```

**3b. "SONRAKİ SÜRÜM" section'ından şu satırı sil:**

```typescript
<DisabledRow label="Yayın Excel raporu" hint="Sonraki sürüm" />
```

**3c. Eğer "Sonraki sürüm" section'ında başka `<DisabledRow ...>` kalmadıysa (tüm 3 placeholder kaldırıldı), section başlığı + paragrafı tamamen sil:**

```typescript
<p className="text-text-muted text-xs uppercase tracking-wider px-1 pt-4">Sonraki sürüm</p>

<DisabledRow label="..." />
```

Bu bloğu sil. Önce dosyayı oku, içeride başka disabled row var mı kontrol et. Eğer "Müşteriler" placeholder hâlâ varsa (müşteriler feature merge edildi ama disabled row'u silinmedi varsayım) → onu da sil. Bu plan'da sadece "Yayın Excel raporu" silinir ama önceki merge edilmiş feature'ların disabled row'ları zaten kaldırılmış olmalı.

**Beklenen:** DahaFazla'da artık hiç "Sonraki sürüm" section'ı yok.

- [ ] **Step 4: Typecheck**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm run typecheck
```

Beklenen: clean.

- [ ] **Step 5: Commit (DO NOT push)**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git add apps/panel/src/screens/YayinRaporuScreen.tsx \
        apps/panel/src/App.tsx \
        apps/panel/src/screens/DahaFazlaScreen.tsx
git commit -m "feat(panel): YayinRaporuScreen + route + DahaFazla NavRow"
```

---

### Task 3: Mobile PR aç

- [ ] **Step 1: Push + PR**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git push -u origin feat/yayin-raporu

gh pr create --title "feat(panel): Yayın Excel raporu (client-side CSV + share)" --body "$(cat <<'EOF'
## Summary

DahaFazlaScreen'in son "Sonraki sürüm" placeholder'ı (\`Yayın Excel raporu\`) yerine gerçek bir export ekranı. **Sıfır server kodu** — mevcut \`/api/panel/sessions\` + \`/api/panel/sessions/{id}/orders\` endpoint'leri yeterli.

- **2 commit**:
  - CSV export library + \`@capacitor/filesystem\` plugin install
  - YayinRaporuScreen + route + DahaFazla NavRow (placeholder kaldır)
- Client-side CSV (UTF-8 BOM, \`;\` separator, TR locale uyumlu, 7 sütun)
- Capacitor Filesystem.Cache + Share API → native paylaşım sheet (WhatsApp/Drive/Email/vs.)
- Filename: \`yayin-rapor-{sanitized-title}-{YYYYMMDD}.csv\`

## Test plan

- [x] \`npm run typecheck\` clean
- [ ] Android emulator smoke:
  - DahaFazla > Yayın raporu → yayın listesi
  - "📊 Excel paylaş" → loading → native share sheet
  - Excel'de aç → 7 kolon, Türkçe karakterler doğru, tutar numeric
  - Boş yayın (0 sipariş) → CSV header-only paylaşılır

## DahaFazla "Sonraki sürüm" placeholder'ları

| Placeholder | Durum |
|---|---|
| Müşteriler | ✓ (PR #18 merged) |
| Hızlı istatistik | ✓ (PR #19 merged) |
| Yayın Excel raporu | ✓ (bu PR) |

3/3 placeholder bitti. "Sonraki sürüm" section'ı tamamen kaldırıldı.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

PR URL'ini not al.

---

## End-to-end smoke (PR merge sonrası)

- [ ] Mobile PR merge → APK rebuild + emulator install (önceki feature'lardaki pattern)
- [ ] Emulator'da:
  - DahaFazla > Yayın raporu → yayın listesi yüklenir
  - "📊 Excel paylaş" butonu → "Hazırlanıyor..." → native share sheet
  - WhatsApp veya başka uygulamaya gönder → dosya alınır
  - Excel/Sheets'te aç → 7 kolon görünür:
    - Tarih (gg.aa.yyyy ss:dd)
    - Müşteri (display name veya username)
    - Platform (Instagram/TikTok/...)
    - Sipariş Kodu
    - Açıklama (ilk 200 karakter)
    - Tutar (₺) (12,50 formatında, numeric)
    - Durum (Yazıldı / İptal / Yedek / Kargo ücreti / Beklemede)
  - Türkçe karakterler doğru (ş, ı, ğ, ç, ö, ü)
  - Boş yayın test: 0 sipariş'li yayın seç → CSV sadece header → şikayet etmeden paylaşılır

## Out of scope

- Server-side XLSX (ClosedXML) — client-side CSV yeterli
- Multi-sheet (payments + shipments) — orders only MVP
- iOS testi — Capacitor iOS pipeline kurulu değil
- Tarih aralığı / toplu rapor — per-session only
- Excel formatting (renkler, totals row, kalın header) — CSV format
