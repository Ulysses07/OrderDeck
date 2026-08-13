# Panel Stok Ekranları (Faz 1b — panel ayağı) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stok elemanının (ve sahibin) panelden stok girişi yapabildiği, sayım
yapabildiği ve hareket geçmişini görebildiği iki ekranı yazmak.

**Architecture:** PR #254 + PR #258 ile sunucuda hazır olan `api/panel/stock`
uçlarının önüne ince bir React katmanı. Bakiye **hiçbir yerde saklanmaz** —
sunucu her istekte defterden toplar, panel sadece gösterir. Panel katalog
ekranıyla aynı desenleri kullanır: `api/*.ts` içinde TanStack Query kancaları,
`components/stock/*` içinde aptal bileşenler, `screens/*` içinde ekran birleşimi.

**Tech Stack:** React 18.3, TypeScript, React Router v7, TanStack Query v5,
Tailwind 3.4, lucide-react, axios (`@orderdeck/shared-api` üzerinden), Vitest 4 +
Testing Library.

---

## Bu iş nerede yapılıyor

**Depo:** `C:\Users\burak\source\repos\OrderDeck-Mobile` (LiveDeck DEĞİL).
Ana dal `main`, şu an temiz. Bu plan LiveDeck'te duruyor çünkü tüm planlar orada.

**Dal:** `main`'den `feat/stok-panel-ekranlari`.

**Paket yöneticisi:** npm workspaces. Komutlar depo kökünden:
- `npm run test --workspace apps/panel`
- `npm run typecheck --workspace apps/panel`
- `npm run lint` (kökten, tüm workspace'ler)
- `npm run build --workspace apps/panel`

## Sunucu sözleşmesi (değiştirilemez — panel buna uyar)

Hepsi `[AllowStockStaff]`, taban yol `api/panel/stock`:

| Uç | İstek | Yanıt |
|---|---|---|
| `GET /balances` | `productId` **tekrarlanabilir** query param; hiç yoksa tüm lisans | `StockBalanceDto[]` |
| `GET /movements` | `productId` (zorunlu), `productVariantId?`, `take` (1..500, varsayılan 100), `CreatedAt` sonra `Id` **azalan** | `StockMovementDto[]` |
| `POST /entries` | `{ productId, productVariantId?, quantity (1..100000), note? (≤200) }` | `StockBalanceDto` |
| `POST /counts` | `{ productId, productVariantId?, countedQuantity (0..1000000), note? (≤200) }` | `StockBalanceDto` |

```
StockBalanceDto  { productId, productVariantId|null, quantity }
StockMovementDto { id, productId, productVariantId|null, quantity, reason,
                   orderId|null, note|null, occurredAt, createdAt }
```

`reason`: `1 = Sale`, `2 = CancelReturn`, `3 = Entry`, `4 = CountAdjustment`.

Ürün listesi katalogla aynı uçtan gelir:
`GET /api/panel/products?q=&page=1&pageSize=50` → `{ items: ProductRow[], total }`.
Bunun kancası **zaten var**: `apps/panel/src/api/catalog.ts` → `useProducts`.
Yeniden yazma, içe aktar.

## ⚠️ Bu işin tek gerçek tuzağı: axios dizi serileştirmesi

axios `{ params: { productId: ["a","b"] } }` verildiğinde sorguyu
`productId[]=a&productId[]=b` diye üretir. ASP.NET Core'un model bağlayıcısı bu
biçimi `Guid[] productId` parametresine **bağlamaz** → dizi boş kalır → sunucu
"hiç id verilmemiş" sanıp **tüm lisansın defterini** döndürür. Hata vermez,
sessizce yanlış çalışır.

Bu yüzden bakiye sorgusunun query string'i `URLSearchParams` ile **elle**
kurulacak (`productId=a&productId=b`) ve bu davranış Task 1'de bir sözleşme
testiyle çivilenecek. Bu testi silme.

## Dosya yapısı

**Yeni:**
- `apps/panel/src/api/stock.ts` — 4 kanca + tipler
- `apps/panel/src/api/stock.test.tsx` — sunucu sözleşmesi testleri
- `apps/panel/src/lib/stockReason.ts` — sebep kodu → Türkçe etiket
- `apps/panel/src/lib/stockReason.test.ts`
- `apps/panel/src/components/stock/StockList.tsx` — ürün + bakiye listesi
- `apps/panel/src/components/stock/StockList.test.tsx`
- `apps/panel/src/components/stock/StockMovementList.tsx` — hareket geçmişi
- `apps/panel/src/components/stock/StockMovementList.test.tsx`
- `apps/panel/src/components/stock/StockEntryForm.tsx` — tek satırlık giriş/sayım formu
- `apps/panel/src/components/stock/StockEntryForm.test.tsx`
- `apps/panel/src/screens/StokScreen.tsx` — `/stok`
- `apps/panel/src/screens/StokUrunScreen.tsx` — `/stok/urun/:productId`

**Değişecek:**
- `apps/panel/src/lib/nav.ts` — stok rolüne "Stok" sekmesi, ev yolu `/stok`
- `apps/panel/src/lib/nav.test.ts` — beklenen yollar güncellenir
- `apps/panel/src/router.tsx` — iki yeni rota
- `apps/panel/src/screens/DahaFazlaScreen.tsx` — sahip/staff için "Stok" satırı

**Kapsam dışı:** barkod okutma (Faz 1c), stok ekranında kategori ağacı, global
hareket akışı, maliyet gösterimi, sayfalama arayüzü (katalogda da yok — daraltma
aramayla yapılır).

---

### Task 1: `api/stock.ts` — sunucu sözleşmesi katmanı

**Files:**
- Create: `apps/panel/src/api/stock.ts`
- Test: `apps/panel/src/api/stock.test.tsx`

- [ ] **Step 1: Testi yaz (önce başarısız olacak)**

`apps/panel/src/api/stock.test.tsx`:

```tsx
/**
 * Bu dosya kancaların react-query davranışını değil, SUNUCU SÖZLEŞMESİNİ
 * koruyor: hangi yola gidiliyor, gövdede hangi alanlar var, dizi parametresi
 * hangi biçimde serileşiyor.
 */
import type { ReactNode } from "react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const http = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
}));
vi.mock("./client", () => ({ apiClient: http }));

import {
  useStockBalances,
  useStockMovements,
  useCreateStockEntry,
  useCreateStockCount,
} from "./stock";

function wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  http.get.mockReset();
  http.post.mockReset();
});

describe("useStockBalances", () => {
  it("id'leri TEKRARLANAN productId olarak serileştirir (productId[] DEĞİL)", async () => {
    http.get.mockResolvedValue({ data: [] });
    renderHook(() => useStockBalances(["a", "b"]), { wrapper });

    await waitFor(() => expect(http.get).toHaveBeenCalled());
    const url = http.get.mock.calls[0][0] as string;
    expect(url).toBe("/api/panel/stock/balances?productId=a&productId=b");
    // axios'a params VERİLMEMELİ; verilirse dizi productId[]= diye serileşir
    // ve sunucu tüm lisansın defterini döndürür.
    expect(http.get.mock.calls[0][1]).toBeUndefined();
  });

  it("liste boşken sunucuya HİÇ gitmez (yoksa tüm defter gelirdi)", () => {
    renderHook(() => useStockBalances([]), { wrapper });
    expect(http.get).not.toHaveBeenCalled();
  });
});

describe("useStockMovements", () => {
  it("productId ve take gönderir", async () => {
    http.get.mockResolvedValue({ data: [] });
    renderHook(() => useStockMovements("p1"), { wrapper });

    await waitFor(() => expect(http.get).toHaveBeenCalled());
    expect(http.get).toHaveBeenCalledWith("/api/panel/stock/movements", {
      params: { productId: "p1", take: "100" },
    });
  });

  it("productId yokken sorgu atmaz", () => {
    renderHook(() => useStockMovements(null), { wrapper });
    expect(http.get).not.toHaveBeenCalled();
  });
});

describe("mutasyonlar", () => {
  it("giriş /entries'e gövdeyi olduğu gibi yollar", async () => {
    http.post.mockResolvedValue({ data: { productId: "p1", productVariantId: null, quantity: 5 } });
    const { result } = renderHook(() => useCreateStockEntry(), { wrapper });

    result.current.mutate({ productId: "p1", productVariantId: null, quantity: 5, note: "kasa" });

    await waitFor(() => expect(http.post).toHaveBeenCalled());
    expect(http.post).toHaveBeenCalledWith("/api/panel/stock/entries", {
      productId: "p1",
      productVariantId: null,
      quantity: 5,
      note: "kasa",
    });
  });

  it("sayım /counts'a countedQuantity yollar — fark SUNUCUDA hesaplanır", async () => {
    http.post.mockResolvedValue({ data: { productId: "p1", productVariantId: "v1", quantity: 3 } });
    const { result } = renderHook(() => useCreateStockCount(), { wrapper });

    result.current.mutate({
      productId: "p1",
      productVariantId: "v1",
      countedQuantity: 3,
      note: null,
    });

    await waitFor(() => expect(http.post).toHaveBeenCalled());
    expect(http.post).toHaveBeenCalledWith("/api/panel/stock/counts", {
      productId: "p1",
      productVariantId: "v1",
      countedQuantity: 3,
      note: null,
    });
  });
});
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- stock.test`
Beklenen: FAIL — `Failed to resolve import "./stock"`.

- [ ] **Step 3: `api/stock.ts`'i yaz**

```ts
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiClient } from "./client";

// ─── Sunucu sözleşmesi (PR #258 — PanelStockController) ───────────────────
// Alan adları sunucudaki record'larla BİREBİR aynı; ASP.NET camelCase
// serileştiriyor. Bir alanı burada yeniden adlandırma — sözleşme kayar.

/** Bir anahtarın (ürün + varyant) güncel bakiyesi. NEGATİF OLABİLİR. */
export type StockBalance = {
  productId: string;
  productVariantId: string | null;
  quantity: number;
};

/** Defterdeki tek satır. Hiçbir zaman silinmez, yalnız eklenir. */
export type StockMovement = {
  id: string;
  productId: string;
  productVariantId: string | null;
  /** İşaretli: satış negatif, giriş/iade pozitif. */
  quantity: number;
  /** StockMovementReason: 1 Satış, 2 İptal/İade, 3 Giriş, 4 Sayım. */
  reason: number;
  orderId: string | null;
  note: string | null;
  /** İş zamanı — geçmişe dönük olabilir. */
  occurredAt: string;
  /** Sunucuya yazılma anı. */
  createdAt: string;
};

export type CreateEntryArgs = {
  productId: string;
  productVariantId: string | null;
  quantity: number;
  note?: string | null;
};

export type CreateCountArgs = {
  productId: string;
  productVariantId: string | null;
  countedQuantity: number;
  note?: string | null;
};

/**
 * Verilen ürünlerin bakiyeleri.
 *
 * Sorgu dizesi ELLE kuruluyor: axios bir diziyi `productId[]=a` diye
 * serileştirir, ASP.NET Core'un model bağlayıcısı bu biçimi `Guid[]`'e
 * BAĞLAMAZ ve sunucu parametre hiç verilmemiş sayıp TÜM lisansın defterini
 * döndürür. Sessiz bir hata olurdu; `URLSearchParams` bunu kesiyor.
 *
 * Liste boşken sorgu hiç atılmaz — boş dizi de "hepsini ver" demektir.
 */
export function useStockBalances(productIds: string[]) {
  // Sıralı kopya: çağıranın sırası değişince önbellek anahtarı kaymasın.
  const ids = [...productIds].sort();
  return useQuery({
    queryKey: ["stock", "balances", ids],
    queryFn: async () => {
      const qs = new URLSearchParams();
      for (const id of ids) qs.append("productId", id);
      const resp = await apiClient.get<StockBalance[]>(
        `/api/panel/stock/balances?${qs.toString()}`,
      );
      return resp.data;
    },
    enabled: ids.length > 0,
    staleTime: 10_000,
  });
}

/**
 * Bir ürünün son hareketleri (varyantlar dahil), yeniden eskiye.
 * `take` sunucuda 1..500 arasına kırpılıyor.
 */
export function useStockMovements(productId: string | null, take = 100) {
  return useQuery({
    queryKey: ["stock", "movements", productId, take],
    queryFn: async () => {
      const resp = await apiClient.get<StockMovement[]>(
        "/api/panel/stock/movements",
        { params: { productId: productId as string, take: String(take) } },
      );
      return resp.data;
    },
    enabled: productId !== null,
    staleTime: 10_000,
  });
}

export function useCreateStockEntry() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: CreateEntryArgs) => {
      const resp = await apiClient.post<StockBalance>(
        "/api/panel/stock/entries",
        args,
      );
      return resp.data;
    },
    // Bakiye ve hareket listelerinin tamamı bayatlar; ["stock"] önekini
    // topluca geçersiz kılmak yeterli.
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["stock"] });
    },
  });
}

/**
 * Sayım. Panel FARKI HESAPLAMAZ — sayılan adedi yollar, sunucu mevcut
 * bakiyeyle karşılaştırıp düzeltme hareketini kendisi üretir. Farkı panelde
 * hesaplamak, okuma ile yazma arasında geçen sürede yayından düşen bir satışı
 * görmezden gelmek olurdu.
 */
export function useCreateStockCount() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: CreateCountArgs) => {
      const resp = await apiClient.post<StockBalance>(
        "/api/panel/stock/counts",
        args,
      );
      return resp.data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["stock"] });
    },
  });
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- stock.test`
Beklenen: PASS — 6 test.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/api/stock.ts apps/panel/src/api/stock.test.tsx
git commit -m "feat(stok): panel stok API kancaları — bakiye, hareket, giriş, sayım

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: `lib/stockReason.ts` — sebep kodu → Türkçe etiket

**Files:**
- Create: `apps/panel/src/lib/stockReason.ts`
- Test: `apps/panel/src/lib/stockReason.test.ts`

- [ ] **Step 1: Testi yaz**

`apps/panel/src/lib/stockReason.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { stockReasonLabel } from "./stockReason";

describe("stockReasonLabel", () => {
  it("bilinen sebepleri Türkçeye çevirir", () => {
    expect(stockReasonLabel(1)).toBe("Satış");
    expect(stockReasonLabel(2)).toBe("İptal/İade");
    expect(stockReasonLabel(3)).toBe("Giriş");
    expect(stockReasonLabel(4)).toBe("Sayım");
  });

  it("bilinmeyen sebepte çökmez, kodu gösterir", () => {
    // Sunucu ileride yeni bir sebep eklerse panel eski sürümde de açılmalı.
    expect(stockReasonLabel(9)).toBe("Hareket (9)");
  });
});
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- stockReason`
Beklenen: FAIL — `Failed to resolve import "./stockReason"`.

- [ ] **Step 3: Uygulamayı yaz**

`apps/panel/src/lib/stockReason.ts`:

```ts
/**
 * Sunucudaki StockMovementReason enum'unun sayı karşılıkları.
 * Değerler sunucuda sabit; burada yeniden numaralandırma.
 */
const LABELS: Record<number, string> = {
  1: "Satış",
  2: "İptal/İade",
  3: "Giriş",
  4: "Sayım",
};

/** Bilinmeyen kodda çökmek yerine kodu gösterir — panel sunucudan eski olabilir. */
export function stockReasonLabel(reason: number): string {
  return LABELS[reason] ?? `Hareket (${reason})`;
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- stockReason`
Beklenen: PASS — 2 test.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/lib/stockReason.ts apps/panel/src/lib/stockReason.test.ts
git commit -m "feat(stok): hareket sebebi etiketleri

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: `StockList` — ürün + bakiye listesi

Katalogdaki `ProductGrid`'in stok muadili. Fark: ızgara değil **liste**
(bakiyeler alt alta taransın diye) ve her satırda toplam bakiye var.

**Files:**
- Create: `apps/panel/src/components/stock/StockList.tsx`
- Test: `apps/panel/src/components/stock/StockList.test.tsx`

- [ ] **Step 1: Testi yaz**

`apps/panel/src/components/stock/StockList.test.tsx`:

```tsx
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { ProductPage } from "../../api/catalog";
import type { StockBalance } from "../../api/stock";

const state = vi.hoisted(() => ({
  page: { items: [], total: 0 } as ProductPage,
  balances: [] as StockBalance[],
}));

vi.mock("../../api/catalog", () => ({
  useProducts: () => ({
    data: state.page,
    isLoading: false,
    isError: false,
    isPlaceholderData: false,
  }),
}));
vi.mock("../../api/stock", () => ({
  useStockBalances: () => ({ data: state.balances, isLoading: false }),
}));

import { StockList } from "./StockList";

function row(id: string, name: string, variantCount = 0) {
  return {
    id,
    categoryId: null,
    code: name.toUpperCase(),
    name,
    defaultPrice: 100,
    isArchived: false,
    coverUrl: null,
    variantCount,
    updatedAt: "2026-08-13T10:00:00Z",
  };
}

beforeEach(() => {
  state.page = { items: [], total: 0 };
  state.balances = [];
});

describe("StockList", () => {
  it("ürünün TÜM varyant bakiyelerini toplayıp tek sayı gösterir", () => {
    state.page = { items: [row("p1", "Gömlek", 2)], total: 1 };
    state.balances = [
      { productId: "p1", productVariantId: "v1", quantity: 4 },
      { productId: "p1", productVariantId: "v2", quantity: 3 },
      { productId: "p1", productVariantId: null, quantity: 1 },
    ];

    render(
      <MemoryRouter>
        <StockList q="" />
      </MemoryRouter>,
    );

    expect(screen.getByText("8")).toBeInTheDocument();
  });

  it("hiç hareketi olmayan ürünü 0 gösterir — listeden düşürmez", () => {
    // Stok girilmemiş ürün de listede DURMALI; zaten giriş yapılacak yer burası.
    state.page = { items: [row("p2", "Pantolon")], total: 1 };
    state.balances = [];

    render(
      <MemoryRouter>
        <StockList q="" />
      </MemoryRouter>,
    );

    expect(screen.getByText("Pantolon")).toBeInTheDocument();
    expect(screen.getByText("0")).toBeInTheDocument();
  });

  it("tükenmiş ürünü uyarı rengiyle işaretler", () => {
    state.page = { items: [row("p3", "Ceket")], total: 1 };
    state.balances = [{ productId: "p3", productVariantId: null, quantity: 0 }];

    render(
      <MemoryRouter>
        <StockList q="" />
      </MemoryRouter>,
    );

    expect(screen.getByText("0")).toHaveClass("text-danger");
  });

  it("satır ürünün stok detayına götürür", () => {
    state.page = { items: [row("p4", "Etek")], total: 1 };

    render(
      <MemoryRouter>
        <StockList q="" />
      </MemoryRouter>,
    );

    expect(screen.getByRole("link", { name: /Etek/ })).toHaveAttribute(
      "href",
      "/stok/urun/p4",
    );
  });

  it("aramaya uyan ürün yoksa arama diliyle söyler", () => {
    state.page = { items: [], total: 0 };

    render(
      <MemoryRouter>
        <StockList q="zzz" />
      </MemoryRouter>,
    );

    expect(screen.getByText("Aramaya uyan ürün yok.")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- StockList`
Beklenen: FAIL — `Failed to resolve import "./StockList"`.

- [ ] **Step 3: Bileşeni yaz**

`apps/panel/src/components/stock/StockList.tsx`:

```tsx
import { useMemo } from "react";
import { Link } from "react-router-dom";
import { ImageOff } from "lucide-react";
import { useProducts, type ProductRow } from "../../api/catalog";
import { useStockBalances } from "../../api/stock";

type Props = { q: string };

/**
 * Stok listesi KATALOĞUN TAMAMINI gösterir, yalnız hareketi olanları değil:
 * stok girişi yapılacak ürün, tanımı gereği henüz hareketi olmayan üründür.
 *
 * Bakiyeler yalnız EKRANDAKİ 50 ürün için isteniyor. Parametresiz çağrı tüm
 * lisansın defterini toplardı; ürün id'lerini geçmek sorguyu sayfaya sabitler.
 */
export function StockList({ q }: Props) {
  const { data, isLoading, isError, isPlaceholderData } = useProducts({
    categoryId: null,
    q,
    page: 1,
    pageSize: 50,
  });

  const items = useMemo(() => data?.items ?? [], [data]);
  const ids = useMemo(() => items.map((p) => p.id), [items]);
  const { data: balances = [] } = useStockBalances(ids);

  // Ürün toplamı = o ürünün tüm anahtarlarının (varyantlar + varyantsız)
  // bakiyeleri. Sunucu anahtar bazında döndürüyor, toplama burada.
  const totals = useMemo(() => {
    const m = new Map<string, number>();
    for (const b of balances) {
      m.set(b.productId, (m.get(b.productId) ?? 0) + b.quantity);
    }
    return m;
  }, [balances]);

  if (isLoading) return <p className="p-4 text-sm text-text-muted">Yükleniyor…</p>;
  if (isError) return <p className="p-4 text-sm text-danger">Ürünler getirilemedi.</p>;

  if (items.length === 0) {
    return (
      <div className="p-6 text-center">
        <p className="text-sm text-text-muted">
          {q ? "Aramaya uyan ürün yok." : "Katalogda henüz ürün yok."}
        </p>
      </div>
    );
  }

  return (
    <>
      <ul
        aria-busy={isPlaceholderData}
        className={`flex flex-col gap-2 p-3 ${isPlaceholderData ? "opacity-50" : ""}`}
      >
        {items.map((p) => (
          <StockRow key={p.id} product={p} total={totals.get(p.id) ?? 0} />
        ))}
      </ul>
      {data && data.total > items.length && (
        <p className="px-3 pb-3 text-xs text-text-muted">
          {items.length} / {data.total} ürün gösteriliyor. Daraltmak için arama kullan.
        </p>
      )}
    </>
  );
}

function StockRow({ product, total }: { product: ProductRow; total: number }) {
  return (
    <li>
      <Link
        to={`/stok/urun/${product.id}`}
        className="flex items-center gap-3 rounded-xl border border-bg-elevated bg-bg-surface p-2 hover:border-accent/40"
      >
        <div className="h-12 w-12 shrink-0 overflow-hidden rounded-lg bg-bg-elevated">
          {product.coverUrl ? (
            <img
              src={product.coverUrl}
              alt=""
              loading="lazy"
              className="h-full w-full object-cover"
            />
          ) : (
            <div className="flex h-full items-center justify-center text-text-muted">
              <ImageOff size={18} />
            </div>
          )}
        </div>

        <div className="min-w-0 flex-1">
          <p className="truncate text-sm text-text">{product.name}</p>
          <p className="mt-0.5 text-xs text-text-muted">
            {product.code}
            {product.variantCount > 0 && ` · ${product.variantCount} varyant`}
          </p>
        </div>

        <span
          className={`shrink-0 text-lg font-semibold tabular-nums ${
            total <= 0 ? "text-danger" : "text-text"
          }`}
        >
          {total}
        </span>
      </Link>
    </li>
  );
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- StockList`
Beklenen: PASS — 5 test.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/components/stock/StockList.tsx apps/panel/src/components/stock/StockList.test.tsx
git commit -m "feat(stok): panel stok listesi bileşeni

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: `/stok` ekranı

**Files:**
- Create: `apps/panel/src/screens/StokScreen.tsx`

Bu ekran yalnız arama kutusu + `StockList` birleşimi; kendi mantığı yok, testi
Task 3 ve Task 8 kapsıyor.

- [ ] **Step 1: Ekranı yaz**

`apps/panel/src/screens/StokScreen.tsx`:

```tsx
import { useState } from "react";
import { Search } from "lucide-react";
import { StockList } from "../components/stock/StockList";
import { useDebounced } from "../lib/useDebounced";

/**
 * Stok ana ekranı. Katalogdaki kategori ağacı BİLEREK yok: stok elemanı ürünü
 * elindeki etiketten arar, ağaçta gezmez.
 */
export function StokScreen() {
  const [q, setQ] = useState("");
  const debouncedQ = useDebounced(q, 300);

  return (
    <main className="pb-4">
      <header className="p-3">
        <h1 className="mb-3 px-1 text-lg font-semibold text-text">Stok</h1>
        <div className="relative">
          <Search
            size={16}
            className="absolute left-2.5 top-1/2 -translate-y-1/2 text-text-muted"
          />
          <input
            type="search"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Ürün adı veya kodu"
            aria-label="Ürün ara"
            className="w-full rounded-xl border border-bg-elevated bg-bg-surface py-2 pl-8 pr-3 text-sm text-text placeholder:text-text-muted"
          />
        </div>
      </header>

      <StockList q={debouncedQ} />
    </main>
  );
}
```

- [ ] **Step 2: Tip kontrolü**

Çalıştır: `npm run typecheck --workspace apps/panel`
Beklenen: hata yok.

- [ ] **Step 3: Commit**

```bash
git add apps/panel/src/screens/StokScreen.tsx
git commit -m "feat(stok): /stok ekranı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: `StockMovementList` — hareket geçmişi

**Files:**
- Create: `apps/panel/src/components/stock/StockMovementList.tsx`
- Test: `apps/panel/src/components/stock/StockMovementList.test.tsx`

- [ ] **Step 1: Testi yaz**

`apps/panel/src/components/stock/StockMovementList.test.tsx`:

```tsx
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import type { StockMovement } from "../../api/stock";

const state = vi.hoisted(() => ({ movements: [] as StockMovement[] }));

vi.mock("../../api/stock", () => ({
  useStockMovements: () => ({
    data: state.movements,
    isLoading: false,
    isError: false,
  }),
}));

import { StockMovementList } from "./StockMovementList";

function mv(over: Partial<StockMovement>): StockMovement {
  return {
    id: "m1",
    productId: "p1",
    productVariantId: null,
    quantity: -1,
    reason: 1,
    orderId: null,
    note: null,
    occurredAt: "2026-08-13T09:00:00Z",
    createdAt: "2026-08-13T09:00:05Z",
    ...over,
  };
}

beforeEach(() => {
  state.movements = [];
});

describe("StockMovementList", () => {
  it("sebebi Türkçe etiketler ve miktarı işaretiyle gösterir", () => {
    state.movements = [
      mv({ id: "m1", quantity: -1, reason: 1 }),
      mv({ id: "m2", quantity: 10, reason: 3 }),
    ];

    render(<StockMovementList productId="p1" variantLabels={{}} />);

    expect(screen.getByText("Satış")).toBeInTheDocument();
    expect(screen.getByText("Giriş")).toBeInTheDocument();
    expect(screen.getByText("−1")).toBeInTheDocument();
    expect(screen.getByText("+10")).toBeInTheDocument();
  });

  it("varyant adını çözer, çözemezse ürün geneli der", () => {
    state.movements = [
      mv({ id: "m1", productVariantId: "v1" }),
      mv({ id: "m2", productVariantId: null }),
    ];

    render(<StockMovementList productId="p1" variantLabels={{ v1: "M · Kırmızı" }} />);

    expect(screen.getByText("M · Kırmızı")).toBeInTheDocument();
    expect(screen.getByText("Ürün geneli")).toBeInTheDocument();
  });

  it("hiç hareket yoksa boş durum gösterir", () => {
    render(<StockMovementList productId="p1" variantLabels={{}} />);
    expect(screen.getByText("Henüz hareket yok.")).toBeInTheDocument();
  });

  it("notu varsa gösterir", () => {
    state.movements = [mv({ note: "depo sayımı" })];
    render(<StockMovementList productId="p1" variantLabels={{}} />);
    expect(screen.getByText("depo sayımı")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- StockMovementList`
Beklenen: FAIL — `Failed to resolve import "./StockMovementList"`.

- [ ] **Step 3: Bileşeni yaz**

`apps/panel/src/components/stock/StockMovementList.tsx`:

```tsx
import { useStockMovements } from "../../api/stock";
import { stockReasonLabel } from "../../lib/stockReason";
import { formatDateTime } from "../../lib/format";

type Props = {
  productId: string;
  /** Varyant id → okunabilir ad. Ekran ürün kartından hazırlayıp geçiyor. */
  variantLabels: Record<string, string>;
};

/**
 * Ürünün son hareketleri (sunucu yeniden eskiye sıralıyor).
 *
 * Gösterilen zaman `occurredAt`, yani İŞ ZAMANI: 1 Ağustos'ta satılıp 5
 * Ağustos'ta iptal edilen siparişin düşümü 1 Ağustos'ta, telafisi 5 Ağustos'ta
 * durur. `createdAt` sunucuya yazılma anıdır ve kullanıcıyı ilgilendirmez.
 */
export function StockMovementList({ productId, variantLabels }: Props) {
  const { data, isLoading, isError } = useStockMovements(productId);

  if (isLoading) return <p className="p-4 text-sm text-text-muted">Yükleniyor…</p>;
  if (isError) return <p className="p-4 text-sm text-danger">Hareketler getirilemedi.</p>;

  const items = data ?? [];
  if (items.length === 0) {
    return <p className="p-4 text-sm text-text-muted">Henüz hareket yok.</p>;
  }

  return (
    <ul className="flex flex-col divide-y divide-bg-elevated">
      {items.map((m) => (
        <li key={m.id} className="flex items-start gap-3 py-2.5">
          <div className="min-w-0 flex-1">
            <p className="text-sm text-text">{stockReasonLabel(m.reason)}</p>
            <p className="mt-0.5 truncate text-xs text-text-muted">
              {m.productVariantId
                ? (variantLabels[m.productVariantId] ?? "Varyant")
                : "Ürün geneli"}
              {" · "}
              {formatDateTime(m.occurredAt)}
            </p>
            {m.note && <p className="mt-0.5 truncate text-xs text-text-muted">{m.note}</p>}
          </div>
          <span
            className={`shrink-0 text-sm font-semibold tabular-nums ${
              m.quantity < 0 ? "text-danger" : "text-success"
            }`}
          >
            {m.quantity < 0 ? `−${Math.abs(m.quantity)}` : `+${m.quantity}`}
          </span>
        </li>
      ))}
    </ul>
  );
}
```

Not: eksi işareti U+2212 (`−`), ASCII tire değil — test dosyasındaki dize de
aynı karakteri kullanıyor, kopyalarken bozma.

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- StockMovementList`
Beklenen: PASS — 4 test.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/components/stock/StockMovementList.tsx apps/panel/src/components/stock/StockMovementList.test.tsx
git commit -m "feat(stok): hareket geçmişi listesi

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: `StockEntryForm` — tek anahtarın giriş/sayım satırı

**Files:**
- Create: `apps/panel/src/components/stock/StockEntryForm.tsx`
- Test: `apps/panel/src/components/stock/StockEntryForm.test.tsx`

- [ ] **Step 1: Testi yaz**

`apps/panel/src/components/stock/StockEntryForm.test.tsx`:

```tsx
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";

const calls = vi.hoisted(() => ({ entry: vi.fn(), count: vi.fn() }));

vi.mock("../../api/stock", () => ({
  useCreateStockEntry: () => ({ mutate: calls.entry, isPending: false }),
  useCreateStockCount: () => ({ mutate: calls.count, isPending: false }),
}));

import { StockEntryForm } from "./StockEntryForm";

beforeEach(() => {
  calls.entry.mockReset();
  calls.count.mockReset();
});

function setup(mode: "entry" | "count") {
  render(
    <StockEntryForm
      mode={mode}
      productId="p1"
      variantId="v1"
      label="M · Kırmızı"
      current={7}
      note="depo"
    />,
  );
}

describe("StockEntryForm", () => {
  it("giriş kipinde adedi quantity olarak yollar", () => {
    setup("entry");

    fireEvent.change(screen.getByLabelText("M · Kırmızı adet"), {
      target: { value: "5" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(calls.entry).toHaveBeenCalledWith({
      productId: "p1",
      productVariantId: "v1",
      quantity: 5,
      note: "depo",
    });
    expect(calls.count).not.toHaveBeenCalled();
  });

  it("sayım kipinde adedi countedQuantity olarak yollar", () => {
    setup("count");

    fireEvent.change(screen.getByLabelText("M · Kırmızı adet"), {
      target: { value: "0" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(calls.count).toHaveBeenCalledWith({
      productId: "p1",
      productVariantId: "v1",
      countedQuantity: 0,
      note: "depo",
    });
    expect(calls.entry).not.toHaveBeenCalled();
  });

  it("giriş kipinde 0 kabul etmez — sunucu Range(1,..) ile reddederdi", () => {
    setup("entry");

    fireEvent.change(screen.getByLabelText("M · Kırmızı adet"), {
      target: { value: "0" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Kaydet" }));

    expect(calls.entry).not.toHaveBeenCalled();
    expect(screen.getByText("Adet en az 1 olmalı.")).toBeInTheDocument();
  });

  it("boş alanla kaydetmez", () => {
    setup("entry");
    fireEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    expect(calls.entry).not.toHaveBeenCalled();
  });

  it("mevcut bakiyeyi gösterir", () => {
    setup("entry");
    expect(screen.getByText("7")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- StockEntryForm`
Beklenen: FAIL — `Failed to resolve import "./StockEntryForm"`.

- [ ] **Step 3: Bileşeni yaz**

`apps/panel/src/components/stock/StockEntryForm.tsx`:

```tsx
import { useState } from "react";
import { useCreateStockEntry, useCreateStockCount } from "../../api/stock";

type Props = {
  mode: "entry" | "count";
  productId: string;
  /** null = ürün geneli (varyantsız düşüm/giriş). */
  variantId: string | null;
  label: string;
  current: number;
  note: string | null;
};

/**
 * Tek anahtarın (ürün + varyant) giriş/sayım satırı.
 *
 * Kip yalnız hangi uca gidileceğini belirler; PANEL FARK HESAPLAMAZ. Sayımda
 * sayılan adet olduğu gibi gider, düzeltme hareketini sunucu üretir.
 */
export function StockEntryForm({
  mode,
  productId,
  variantId,
  label,
  current,
  note,
}: Props) {
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);
  const entry = useCreateStockEntry();
  const count = useCreateStockCount();
  const pending = entry.isPending || count.isPending;

  function submit() {
    const n = Number(value);
    if (value.trim() === "" || !Number.isInteger(n) || n < 0) {
      setError("Geçerli bir adet gir.");
      return;
    }
    if (mode === "entry" && n < 1) {
      setError("Adet en az 1 olmalı.");
      return;
    }
    setError(null);
    const trimmed = note?.trim() ? note.trim() : null;
    if (mode === "entry") {
      entry.mutate({ productId, productVariantId: variantId, quantity: n, note: trimmed });
    } else {
      count.mutate({
        productId,
        productVariantId: variantId,
        countedQuantity: n,
        note: trimmed,
      });
    }
    setValue("");
  }

  return (
    <div className="flex items-center gap-2 py-2">
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm text-text">{label}</p>
        {error && <p className="mt-0.5 text-xs text-danger">{error}</p>}
      </div>

      <span
        className={`w-10 shrink-0 text-right text-sm font-semibold tabular-nums ${
          current <= 0 ? "text-danger" : "text-text-muted"
        }`}
      >
        {current}
      </span>

      <input
        type="number"
        inputMode="numeric"
        min={mode === "entry" ? 1 : 0}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        aria-label={`${label} adet`}
        placeholder={mode === "entry" ? "+adet" : "sayılan"}
        className="w-20 shrink-0 rounded-lg border border-bg-elevated bg-bg-surface px-2 py-1.5 text-right text-sm text-text placeholder:text-text-muted"
      />

      <button
        type="button"
        onClick={submit}
        disabled={pending}
        className="shrink-0 rounded-lg bg-accent px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
      >
        Kaydet
      </button>
    </div>
  );
}
```

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- StockEntryForm`
Beklenen: PASS — 5 test.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/components/stock/StockEntryForm.tsx apps/panel/src/components/stock/StockEntryForm.test.tsx
git commit -m "feat(stok): giriş/sayım satır formu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: `/stok/urun/:productId` ekranı

**Files:**
- Create: `apps/panel/src/screens/StokUrunScreen.tsx`

- [ ] **Step 1: Ekranı yaz**

`apps/panel/src/screens/StokUrunScreen.tsx`:

```tsx
import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { useProduct, type Variant } from "../api/catalog";
import { useStockBalances } from "../api/stock";
import { StockEntryForm } from "../components/stock/StockEntryForm";
import { StockMovementList } from "../components/stock/StockMovementList";

/** "M · Kırmızı" — iki eksen de boşsa varyant koduna düşer. */
function variantLabel(v: Variant): string {
  const parts = [v.axis1Value, v.axis2Value].filter(Boolean) as string[];
  return parts.length > 0 ? parts.join(" · ") : v.variantCode;
}

/**
 * Ürünün stok detayı: her anahtar için bakiye + giriş/sayım satırı, altında
 * hareket geçmişi.
 *
 * "Ürün geneli" satırı varyantlı üründe de duruyor: yayında varyant seçilmeden
 * satılan siparişler ürün seviyesine düşüyor, o bakiyenin de düzeltilebilmesi
 * gerek.
 */
export function StokUrunScreen() {
  const { productId } = useParams<{ productId: string }>();
  const [mode, setMode] = useState<"entry" | "count">("entry");
  const [note, setNote] = useState("");

  const { data: product, isLoading, isError } = useProduct(productId ?? null);
  const ids = useMemo(() => (productId ? [productId] : []), [productId]);
  const { data: balances = [] } = useStockBalances(ids);

  const byKey = useMemo(() => {
    const m = new Map<string, number>();
    for (const b of balances) m.set(b.productVariantId ?? "", b.quantity);
    return m;
  }, [balances]);

  const total = useMemo(
    () => balances.reduce((sum, b) => sum + b.quantity, 0),
    [balances],
  );

  const variantLabels = useMemo(() => {
    const m: Record<string, string> = {};
    for (const v of product?.variants ?? []) m[v.id] = variantLabel(v);
    return m;
  }, [product]);

  if (isLoading) return <p className="p-4 text-sm text-text-muted">Yükleniyor…</p>;
  if (isError || !product || !productId) {
    return <p className="p-4 text-sm text-danger">Ürün getirilemedi.</p>;
  }

  return (
    <main className="pb-6">
      <header className="flex items-center gap-2 p-3">
        <Link
          to="/stok"
          aria-label="Stok listesine dön"
          className="rounded-lg p-1.5 text-text-muted hover:bg-bg-elevated"
        >
          <ArrowLeft size={18} />
        </Link>
        <div className="min-w-0 flex-1">
          <p className="truncate text-base font-semibold text-text">{product.name}</p>
          <p className="text-xs text-text-muted">{product.code}</p>
        </div>
        <span
          className={`shrink-0 text-2xl font-semibold tabular-nums ${
            total <= 0 ? "text-danger" : "text-text"
          }`}
        >
          {total}
        </span>
      </header>

      <div className="px-3">
        <div
          role="radiogroup"
          aria-label="İşlem kipi"
          className="flex gap-1 rounded-xl bg-bg-elevated p-1"
        >
          <ModeButton active={mode === "entry"} onClick={() => setMode("entry")}>
            Giriş
          </ModeButton>
          <ModeButton active={mode === "count"} onClick={() => setMode("count")}>
            Sayım
          </ModeButton>
        </div>
        <p className="mt-1.5 px-1 text-xs text-text-muted">
          {mode === "entry"
            ? "Yazdığın adet mevcut stoğa EKLENİR."
            : "Yazdığın adet mevcut stoğun YERİNE geçer; farkı sunucu hesaplar."}
        </p>

        <input
          type="text"
          value={note}
          maxLength={200}
          onChange={(e) => setNote(e.target.value)}
          placeholder="Not (isteğe bağlı)"
          aria-label="Not"
          className="mt-3 w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2 text-sm text-text placeholder:text-text-muted"
        />

        <div className="mt-3 divide-y divide-bg-elevated rounded-xl border border-bg-elevated bg-bg-surface px-3">
          <StockEntryForm
            mode={mode}
            productId={productId}
            variantId={null}
            label="Ürün geneli"
            current={byKey.get("") ?? 0}
            note={note}
          />
          {product.variants.map((v) => (
            <StockEntryForm
              key={v.id}
              mode={mode}
              productId={productId}
              variantId={v.id}
              label={variantLabel(v)}
              current={byKey.get(v.id) ?? 0}
              note={note}
            />
          ))}
        </div>

        <h2 className="mb-1 mt-6 px-1 text-[11px] font-semibold uppercase tracking-[0.08em] text-text-muted">
          Hareketler
        </h2>
        <div className="rounded-xl border border-bg-elevated bg-bg-surface px-3">
          <StockMovementList productId={productId} variantLabels={variantLabels} />
        </div>
      </div>
    </main>
  );
}

function ModeButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      onClick={onClick}
      className={`flex-1 rounded-lg py-1.5 text-sm font-medium transition-colors ${
        active ? "bg-accent text-white" : "text-text-muted"
      }`}
    >
      {children}
    </button>
  );
}
```

- [ ] **Step 2: Tip kontrolü**

Çalıştır: `npm run typecheck --workspace apps/panel`
Beklenen: hata yok.

- [ ] **Step 3: Commit**

```bash
git add apps/panel/src/screens/StokUrunScreen.tsx
git commit -m "feat(stok): ürün stok detay ekranı — giriş, sayım, hareketler

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: Menü ve rotalar

Stok elemanının menüsü şu an `["/katalog", "/cikis"]`. Stok girişi asıl işi
olduğu için ev yolu `/stok` olmalı. Sahip/staff'ın alt menüsü **5 sekmede
kalacak** (altıncı sekme dar telefonda taşar), onlar "Daha" ekranından girer.

**Files:**
- Modify: `apps/panel/src/lib/nav.ts`
- Modify: `apps/panel/src/lib/nav.test.ts`
- Modify: `apps/panel/src/router.tsx`
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx`

- [ ] **Step 1: Testi güncelle (önce başarısız olacak)**

`apps/panel/src/lib/nav.test.ts` — iki testi değiştir, diğer dördüne dokunma.

Bunu (14-17. satırlar):

```ts
  it("stok elemanı katalogu ve çıkışı görür", () => {
    const paths = navItemsForRole("stock").map((i) => i.to);
    expect(paths).toEqual(["/katalog", "/cikis"]);
  });
```

şununla değiştir:

```ts
  it("stok elemanı stoğu, katalogu ve çıkışı görür", () => {
    const paths = navItemsForRole("stock").map((i) => i.to);
    expect(paths).toEqual(["/stok", "/katalog", "/cikis"]);
  });
```

Bunu (25-31. satırlar):

```ts
  it("stok elemanının açılışı katalog, diğerlerininki ana ekran", () => {
    // Stok elemanı "Ana"ya düşerse ekran boş kalır ve AnaScreen'in üç sorgusu
    // sunucudan üç ayrı 403 alır.
    expect(homePathForRole("stock")).toBe("/katalog");
    expect(homePathForRole("owner")).toBe("/");
    expect(homePathForRole("staff")).toBe("/");
  });
```

şununla değiştir:

```ts
  it("stok elemanının açılışı stok ekranı, diğerlerininki ana ekran", () => {
    // Stok elemanı "Ana"ya düşerse ekran boş kalır ve AnaScreen'in üç sorgusu
    // sunucudan üç ayrı 403 alır. Asıl işi stok girişi olduğu için katalog
    // değil stok ekranı açılıyor.
    expect(homePathForRole("stock")).toBe("/stok");
    expect(homePathForRole("owner")).toBe("/");
    expect(homePathForRole("staff")).toBe("/");
  });
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Çalıştır: `npm run test --workspace apps/panel -- nav`
Beklenen: FAIL — `expected [ '/katalog', '/cikis' ] to deeply equal [ '/stok', '/katalog', '/cikis' ]`.

- [ ] **Step 3: `nav.ts`'i güncelle**

`ClipboardList` ikonunu lucide-react içe aktarımına ekle, sonra
`navItemsForRole`'ü şöyle yap:

```ts
/**
 * Alt menü BEŞ sekmede kalıyor — altıncısı dar telefonda taşıyor. Bu yüzden
 * "/stok" ALL'a EKLENMEDİ: sahip/staff oraya "Daha" ekranından girer, stok
 * elemanının menüsüne ise ilk sıraya konur (ev yolu ilk öğeden türüyor).
 */
export function navItemsForRole(role: OperatorRole): NavItem[] {
  if (role === "stock") {
    return [
      { to: "/stok", label: "Stok", Icon: ClipboardList },
      ...ALL.filter((i) => i.to === "/katalog"),
      { to: "/cikis", label: "Çıkış", Icon: LogOut },
    ];
  }
  return ALL;
}
```

`ALL` dizisine ve `homePathForRole`'e dokunma — ev yolu zaten
`navItemsForRole(role)[0].to`.

- [ ] **Step 4: Testi çalıştır, geçtiğini gör**

Çalıştır: `npm run test --workspace apps/panel -- nav`
Beklenen: PASS.

- [ ] **Step 5: Rotaları ekle**

`apps/panel/src/router.tsx` — içe aktarımlara ekle (`UrunScreen` satırının altına):

```tsx
import { StokScreen } from "./screens/StokScreen";
import { StokUrunScreen } from "./screens/StokUrunScreen";
```

`{ path: "/katalog/urun/:productId", ... }` satırının hemen altına ekle:

```tsx
          { path: "/stok", element: <StokScreen /> },
          { path: "/stok/urun/:productId", element: <StokUrunScreen /> },
```

- [ ] **Step 6: "Daha" ekranına giriş noktası ekle**

`apps/panel/src/screens/DahaFazlaScreen.tsx`:

lucide-react içe aktarımına `ClipboardList` ekle, sonra `İçerik` bölümünün
üstüne (yani `{isNative() && ...}` bloğunun altına) yeni bir bölüm koy:

```tsx
      <Kicker>Stok</Kicker>
      <NavRow
        to="/stok"
        label="Stok"
        hint="Stok girişi, sayım ve hareket geçmişi"
        Icon={ClipboardList}
      />
```

- [ ] **Step 7: Tüm testler + tip kontrolü**

Çalıştır: `npm run test --workspace apps/panel`
Beklenen: tüm paketler geçer.

Çalıştır: `npm run typecheck --workspace apps/panel`
Beklenen: hata yok.

- [ ] **Step 8: Commit**

```bash
git add apps/panel/src/lib/nav.ts apps/panel/src/lib/nav.test.ts apps/panel/src/router.tsx apps/panel/src/screens/DahaFazlaScreen.tsx
git commit -m "feat(stok): stok rotaları ve menü girişleri

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 9: Kapanış doğrulaması

**Files:** yok — yalnız komutlar.

- [ ] **Step 1: Lint**

Çalıştır (depo kökünden): `npm run lint`
Beklenen: 0 hata, 0 uyarı.

- [ ] **Step 2: Tüm testler**

Çalıştır: `npm run test --workspaces --if-present`
Beklenen: hepsi geçer.

- [ ] **Step 3: Üretim derlemesi**

Çalıştır: `npm run build --workspace apps/panel`
Beklenen: `tsc --noEmit` temiz, vite derlemesi başarılı.

- [ ] **Step 4: Elle duman testi (kullanıcı)**

`npm run dev:panel` → sahip hesabıyla gir:
1. "Daha" → "Stok" → liste geliyor, bakiyeler görünüyor.
2. Ağ sekmesinde `balances` isteğinin sorgusu `productId=…&productId=…` biçiminde
   (köşeli parantez YOK) ve yalnız ekrandaki ürünleri içeriyor.
3. Bir ürüne gir → Giriş kipinde 5 yaz → Kaydet → bakiye ve hareket listesi
   anında güncelleniyor.
4. Sayım kipinde 2 yaz → Kaydet → hareket listesinde "Sayım −3" beliriyor.
5. Stok elemanı hesabıyla gir → uygulama doğrudan `/stok`'a düşüyor, alt menüde
   Stok · Katalog · Çıkış var.

---

## Notlar

- **Yetki:** panel taraflı rol kontrolü tamamen kozmetik. Asıl kapı sunucudaki
  `StockStaffScopeFilter`; stok elemanı yalnız `[AllowStockStaff]` işaretli
  uçlara girebiliyor. Panelde ek bir gizleme mantığı yazma.
- **Maliyet:** stok ekranlarında maliyet hiç gösterilmiyor, `canSeeCost` çağrısı
  gerekmiyor. Sunucu zaten stok rolüne `cost: null` yolluyor.
- **Bakiye negatif olabilir** ve bu bir hata değil: stok girilmeden satış yapılan
  ürün eksiye düşer. Ekranlar bunu kırmızıyla gösterir, engellemez.
