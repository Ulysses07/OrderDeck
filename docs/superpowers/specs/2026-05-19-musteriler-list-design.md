# Müşteriler liste ekranı (mobile panel)

**Status**: design  
**Date**: 2026-05-19  
**Sub-project of**: DahaFazlaScreen "Sonraki sürüm" placeholder bitirme (3 feature'ın 1'i — diğerleri ayrı spec'ler)

## Problem

Mobile panel DahaFazlaScreen'de "Müşteriler — Sonraki sürüm" disabled placeholder var. Yayıncının kendi müşterilerinin (alıcılarının) tam bir envanterini gezecek, filtreleyecek, sıralayacak ekran yok. Sadece AramaScreen (search-by-name) ve CustomerDetailScreen (single fetch) var.

5K civarı müşteri tipik bir yayıncı için. Liste açıldığında:
- En son sipariş veren müşteriler üstte
- VIP'lere bakmak için sort değiştirilebiliyor
- Aktiflik/platform/harcama/sipariş sayısı filtrelerle daraltılabiliyor

## Solution

Mobile'a yeni bir liste ekranı (`/musteriler`). Server tarafında **var olan** `PanelCustomersController`'a `[HttpGet] List(...)` action eklenir (yan yana mevcut `Get(id)` ile).

Order verisi denormalize bir `Customers` tablosunda değil — `Orders.CustomerId` distinct değerlerinden runtime'da GROUP BY ile aggregate edilir (mevcut `Get(id)` pattern'iyle tutarlı).

## Architecture

```
mobile (React + Vite + Capacitor)         server (.NET LicenseServer)
─────────────────────────────              ───────────────────────────
[Daha Fazla] → "Müşteriler" NavRow         PanelCustomersController
                ↓                            ├─ GET /api/panel/customers (yeni)
[MusterilerScreen.tsx] (yeni)                │   ?cursor=...&sort=...&filters...
  ├─ Sort dropdown (4 seçenek)               │
  ├─ Filter modal                            │   Order aggregate (GROUP BY)
  ├─ Liste (cursor pagination)               │
  └─ Row tap → /musteriler/:customerId       └─ GET /{customerId} (zaten var)
              (mevcut CustomerDetailScreen)
```

**Yeni dosyalar**:
- `apps/panel/src/screens/MusterilerScreen.tsx` (~280 satır)
- `apps/panel/src/api/queries.ts` — `useCustomers(params)` infinite query hook + types ekle
- `apps/panel/src/App.tsx` — `/musteriler` route
- `apps/panel/src/screens/DahaFazlaScreen.tsx` — "Müşteriler" placeholder kaldır + "İçerik" section'a NavRow ekle (Duyurular'ın yanına)
- `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomersController.cs` — `[HttpGet] List(...)` action + DTO'lar
- `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCustomersControllerTests.cs` — yeni test dosyası (~9 test)

**Eklenebilecek DB index'leri** (Phase 1 ile birlikte):
- `Orders(LicenseId, CustomerId, AddedAt DESC)` composite — tenant + group + sort
- Mevcut index'lere bakılıp gerekli ise EF migration ile eklenir

## Server endpoint contract

### `GET /api/panel/customers`

**Query params**:

| param | tip | default | açıklama |
|---|---|---|---|
| `cursor` | string? | null | Composite cursor: `{sortValue}\|{customerId}` |
| `sort` | enum | `lastOrder` | `lastOrder` / `totalSpent` / `orderCount` / `name` |
| `activeWithinDays` | int? | null | Set ise `LastOrderAt > now - N days` filtresi |
| `platforms` | string? | null | Comma-separated: "instagram,tiktok,facebook,youtube,web" |
| `minSpent` | decimal? | null | TotalSpent >= |
| `maxSpent` | decimal? | null | TotalSpent <= |
| `minOrders` | int? | null | OrderCount >= |
| `maxOrders` | int? | null | OrderCount <= |
| `limit` | int | 50 | 1-100 clamp |

**Response** (`200 OK`):

```json
{
  "customers": [
    {
      "id": "burakdemir-instagram",
      "displayName": "Burak Demir",
      "username": "@burakdemir",
      "platform": "instagram",
      "totalSpent": 12450.00,
      "orderCount": 8,
      "lastOrderAt": "2026-05-18T10:30:00Z",
      "isActive": true
    }
  ],
  "nextCursor": "2026-05-18T10:30:00Z|burakdemir-instagram"
}
```

**Cursor format**: `{sortValue}|{customerId}` composite. Sort field'a göre `sortValue`:
- `lastOrder` → ISO 8601 timestamp
- `totalSpent` → decimal (örn. "12450.00")
- `orderCount` → int (örn. "8")
- `name` → lowercase display name

**Page 2+ filter**:
```
WHERE (sortValue < @cursorSortValue)
   OR (sortValue = @cursorSortValue AND CustomerId < @cursorCustomerId)
```

Tie-breaker `CustomerId` (broadcast posts pattern).

**Identity (displayName/username/platform)**: Her customer için `Orders` tablosundan en son `UpdatedAt`'li satırın bu üç field'ı alınır. Subquery veya `OUTER APPLY`.

**Order aggregate kuralı**:
- `OrderCount` ve `TotalSpent` "gerçek satış" sayar: `PrintedAt != NULL && CancelledAt == NULL && !IsShippingFee && !IsTentativeBackup`
- `LastOrderAt` ham `MAX(AddedAt)` — iptal de dahil (müşterinin "son aktivite" göstergesi)
- `IsActive` = `LastOrderAt > NOW - 30 days`

**Tenant isolation**: `Where(o => licenseIds.Contains(o.LicenseId))` — broadcaster'ın tüm lisansları (genelde 1, nadiren çoğul).

**Error states**:
- `limit` < 1 veya > 100 → silently 50'ye clamp (broadcast posts'la tutarlı)
- Geçersiz `sort` → `Problem(title: "invalid-sort", 400)`
- Geçersiz `cursor` (parse fail) → silent ignore, ilk sayfa dön

## Mobile UI — `MusterilerScreen.tsx`

**Layout**:

```
┌─────────────────────────────────────┐
│ ← Geri                              │  header
│ Müşteriler                          │
│ 247 müşteri                         │  active filter sonuç sayısı
├─────────────────────────────────────┤
│ [Sırala: Son sipariş ▼]  [Filtre 🎚]│  filter bar
├─────────────────────────────────────┤
│ ┌─Burak Demir─────────── ₺12.450 ─┐│  dense row, 2 satır
│ │ 8 sipariş • 3g önce • IG       ││
│ └─────────────────────────────────┘│
│ ... (50 row + infinite scroll)     │
└─────────────────────────────────────┘
```

**Etkileşimler**:

1. **Sort dropdown** — inline (filter bar'da). Tıklanınca:
   - Son sipariş
   - Toplam harcama
   - Sipariş sayısı
   - Ad (A-Z)

   Seçim → liste yeniden fetch, cursor sıfırla.

2. **Filter modal** — `🎚` butonu → bottom sheet (mobile için full-screen). İçinde:
   - **Aktiflik**: toggle "Sadece aktif (son 30 gün)" — default kapalı
   - **Platform**: 5 chip — IG / TT / FB / YT / Web — multi-select. Hiçbiri seçilmezse "tümü"
   - **Harcama aralığı**: iki input field (min ₺ — max ₺), boş bırakılırsa filtre yok
   - **Sipariş sayısı aralığı**: iki input field (min — max), boş bırakılırsa filtre yok
   - Alt sabit bar: "Sıfırla" + "Uygula" butonu
   - Aktif filtre sayısı header rozeti (örn. "Filtre 🎚 ②")

3. **Liste row** — dense layout:
   - Üst satır: ad (sola) + harcama (sağa, ₺ formatlı, kalın)
   - Alt satır: "8 sipariş • 3g önce • IG" (text-muted, küçük font)
   - Platform: 2-letter abbreviation
   - Tap → `useNavigate("/musteriler/" + customerId)` → mevcut CustomerDetailScreen

4. **Pagination** — infinite scroll: liste'nin sonuna yaklaştıkça (IntersectionObserver) `nextCursor` ile fetch. Manuel "Daha fazla yükle" butonu yok.

5. **States**:
   - Loading initial: skeleton 3 placeholder row
   - Loading more: alt'ta spinner
   - Empty (filtre sonucu yok): `🔍 Eşleşen müşteri yok • Filtreleri sıfırla` butonu
   - Empty (hiç müşteri yok): `📭 Henüz müşterin yok • İlk satıştan sonra burada görünür`
   - Error: `Yüklenemedi • Tekrar dene` butonu

**API hook** (queries.ts):

```typescript
export type CustomerListItem = {
  id: string;
  displayName: string | null;
  username: string;
  platform: string;
  totalSpent: number;
  orderCount: number;
  lastOrderAt: string;
  isActive: boolean;
};

export type CustomerListResponse = {
  customers: CustomerListItem[];
  nextCursor: string | null;
};

export type CustomerListParams = {
  sort?: "lastOrder" | "totalSpent" | "orderCount" | "name";
  activeWithinDays?: number;
  platforms?: string[];
  minSpent?: number;
  maxSpent?: number;
  minOrders?: number;
  maxOrders?: number;
};

export function useCustomersInfinite(params: CustomerListParams) {
  return useInfiniteQuery({
    queryKey: ["customers", params],
    queryFn: async ({ pageParam }) => { /* ... */ },
    getNextPageParam: (last) => last.nextCursor,
    initialPageParam: null as string | null,
    staleTime: 30_000,
  });
}
```

## Performance

**JOIN+GROUP BY yaklaşımı** (tek seçenek — denormalize Customer tablo yok):

```sql
WITH FilteredOrders AS (
  SELECT o.* FROM Orders o
  JOIN Licenses l ON o.LicenseId = l.Id
  WHERE l.CustomerId = @authCustomerId
    -- platform filter (varsa)
),
CustomerAgg AS (
  SELECT
    CustomerId,
    MAX(AddedAt) AS LastOrderAt,
    SUM(CASE WHEN PrintedAt IS NOT NULL AND CancelledAt IS NULL
                  AND IsShippingFee = 0 AND IsTentativeBackup = 0
             THEN Price ELSE 0 END) AS TotalSpent,
    COUNT(CASE WHEN PrintedAt IS NOT NULL AND CancelledAt IS NULL
                    AND IsShippingFee = 0 AND IsTentativeBackup = 0
               THEN 1 END) AS OrderCount
  FROM FilteredOrders
  GROUP BY CustomerId
)
SELECT
  ca.CustomerId,
  -- identity from latest order
  (SELECT TOP 1 DisplayName FROM FilteredOrders fo
    WHERE fo.CustomerId = ca.CustomerId ORDER BY UpdatedAt DESC) AS DisplayName,
  -- ... Username, Platform aynı şekilde
  ca.LastOrderAt, ca.TotalSpent, ca.OrderCount,
  CASE WHEN ca.LastOrderAt > DATEADD(day, -30, SYSUTCDATETIME())
       THEN 1 ELSE 0 END AS IsActive
FROM CustomerAgg ca
-- filtre WHERE
WHERE (@activeWithinDays IS NULL OR ca.LastOrderAt > DATEADD(day, -@activeWithinDays, SYSUTCDATETIME()))
  AND (@minSpent IS NULL OR ca.TotalSpent >= @minSpent)
  AND (@maxSpent IS NULL OR ca.TotalSpent <= @maxSpent)
  AND (@minOrders IS NULL OR ca.OrderCount >= @minOrders)
  AND (@maxOrders IS NULL OR ca.OrderCount <= @maxOrders)
-- cursor
  AND (<sortField> < @cursorSortValue
       OR (<sortField> = @cursorSortValue AND ca.CustomerId < @cursorCustomerId))
ORDER BY <sortField> DESC, ca.CustomerId DESC
OFFSET 0 ROWS FETCH NEXT 51 ROWS ONLY  -- peek next cursor
```

**EF Core LINQ implementation hint**: `Where → GroupBy → Select projection → Where (filters) → OrderBy → Take(limit+1)`. Identity subquery için `Select(g => new { ..., Identity = orders.OrderByDescending(o => o.UpdatedAt).Select(o => new { o.DisplayName, ... }).First() })` veya `LATERAL`-vari pattern.

**Indexler**:
- `Orders(LicenseId, CustomerId, AddedAt)` composite — tenant + grouping + sort
- `Orders(LicenseId, CustomerId, UpdatedAt DESC)` — identity subquery için
- Mevcut indexlere bakılır, eksik olanlar EF migration ile eklenir

**Tahmini performans** (5K müşteri / 50K order):
- p95 hedef: < 500ms
- GROUP BY indexli: ~100-300ms
- Identity subquery: tek pass — ekstra ~50-100ms

**Phase 2** (eğer yavaşlarsa — bu plan dışı):
- Pre-aggregated `CustomerSnapshot` tablosu (order insert/update'te trigger veya app-level)
- SQL Server materialized indexed view

YAGNI — önce JOIN ile çalışsın, ölçer geçeriz.

## Testing

**Server tests** (`PanelCustomersControllerTests.cs`, ~9 yeni test):

| Test | Doğrulanan |
|---|---|
| `List_returns_empty_when_no_orders` | Boş broadcaster için empty list + nextCursor null |
| `List_returns_customers_with_aggregate_counts` | Sipariş eklenmiş müşteriler aggregate doğru |
| `List_sort_by_lastOrder_desc_default` | Default sort working |
| `List_sort_by_totalSpent_desc` | Alt sort path |
| `List_filter_active_within_30_days` | Aktiflik filtresi 30 günlük cut-off |
| `List_filter_platform_multi` | Comma-separated platform filtresi |
| `List_filter_spent_range` | min/max spent eşikleri |
| `List_pagination_composite_cursor` | Page 1 + cursor + page 2, duplicate yok |
| `List_cross_tenant_returns_empty` | Tenant A müşterilerini tenant B çekemez |

ApiFactory + helper: `SeedCustomerOrders(licenseId, customerId, count, totalSpent, lastOrderAt)`.

**Mobile**: typecheck only — `npm run typecheck`. Manuel smoke broadcaster panelinden test edilir.

## Out of scope

- Müşteri oluşturma/düzenleme (WPF authoritative)
- Müşteri silme/birleştirme (idem)
- Favori müşteri / not ekleme (YAGNI — başka feature)
- Bulk actions (toplu silme/etiketleme — YAGNI)
- WhatsApp gönderme listeden (zaten detay sayfasında var)
- Pre-aggregated `CustomerSnapshot` cache tablosu (Phase 2'ye)
- DahaFazlaScreen'in diğer 2 placeholder'ı (Hızlı istatistik, Yayın Excel raporu) — ayrı spec'ler
