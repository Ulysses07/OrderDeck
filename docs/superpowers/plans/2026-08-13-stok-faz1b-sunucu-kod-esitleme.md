# Faz 1b Sunucu Ön Koşulu — Ürün Kodu Katlaması ve Eski İstemci Koruması

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ürün kodunu büyük/küçük harf ve Türkçe harf farkından bağımsız hâle getirmek, ve katalog bilgisi taşımayan (güncellenmemiş) WPF istemcilerinin stok defterini geri sarmasını engellemek.

**Architecture:** Sunucu ürün kodunu **zaten normalleştirilmiş hâlde saklıyor** (`"  a5 "` → `"A5"`); tek eksik, normalleştiricinin Türkçe'de bozuk olması. Normalleştirme `ToUpperInvariant`'tan projenin ortak `SearchNormalizer`'ına geçiyor — böylece benzersizlik, panel araması, varyant kodu, barkod ve WPF'in yorum eşleştirmesi **tek kuralı** paylaşıyor. Ayrıca sipariş senkron isteğine `CatalogAware` bayrağı ekleniyor; bayrak yoksa (eski istemci) o paket için stok mutabakatı hiç çalışmıyor.

**Tech Stack:** ASP.NET Core 10, EF Core 10, SQL Server (prod) / InMemory (test), xUnit + FluentAssertions + `ApiFactory` uçtan uca HTTP testleri.

---

## Neden bu plan Faz 1b'nin WPF ayağından ÖNCE

1. **Eşleştirme sözleşmesi burada tanımlanıyor.** WPF, yorum metnini yerel katalog replikasına karşı eşleştirecek. Sunucu ile masaüstü aynı normalleştirmeyi kullanmazsa `güzel elbise` yazan izleyici ürünü bulamaz. Kuralın tek sahibi sunucu olmalı; WPF onu taşır, yeniden icat etmez.

2. **Bu bir *veri* değişikliği.** Katalog sahada neredeyse boş (panel 2026-08-12'de canlıya çıktı). Bugün maliyeti sıfır; yayıncılar ürün girmeye başladıktan sonra aynı iş, çakışan kodların elle ayıklanması demek.

3. **Eski istemci koruması bir veri bozulması riski.** Aşağıda ayrıntısı var; katalog kullanımı yayılmadan kapatılmalı.

---

## Çözülen iki sorun

### Sorun 1 — Kod normalleştirmesi Türkçe'de bozuk

Ürün kodu bugün **ham saklanmıyor**; yazma anında normalleştiriliyor (`PanelProductsController.cs:682`):

```csharp
private static string NormalizeCode(string? code)
    => (code ?? string.Empty).Trim().ToUpperInvariant();
```

Bu davranış test tarafından da sabitlenmiş (`PanelProductsControllerTests.cs:202-213`): `"  a5 "` girilir, `"A5"` saklanır. Yani kod **kimlik**, ad gibi serbest metin değil — kanonikleştirilmesi mevcut ve kabul edilmiş tasarım.

Tek sorun, kanonikleştiricinin Türkçe'de yanlış olması. `ToUpperInvariant` iki yerde bozuk (`OrderDeck.Shared/Text/TurkishAscii.cs:27-28` bunu zaten belgeliyor):

- `ı` (U+0131) **küçük kalır** — büyük harfe hiç çevrilmez.
- `İ` (U+0130) korunur, `I`'ya inmez.

Somut sonuç: `Işık 1` girildiğinde saklanan kod `IŞıK 1` oluyor (ı küçük kalmış, Ş duruyor). Bunun üç ayrı etkisi var:

| Nereye dokunuyor | Bozukluk |
|---|---|
| Benzersizlik (`CodeTakenAsync`, `:617-630`) | `ışık1` ile `IŞIK1` **iki ayrı ürün** olabilir; "lisans başına kod benzersiz" sessizce delinir |
| Panel araması (`:150-163`) | İğne `SearchNormalizer`'dan geçiyor (`GUZEL`), saklanan kod geçmiyor (`GÜZEL`) → arama tutmaz |
| Varyant kodu / barkod (`VariantCodeBuilder.cs:27-35`) | `VariantCode = ürünKodu + "-" + eksenKodu`; eksen parçası ASCII'ye katlanıyor (`AxisCodeDeriver`) ama **ürün kodu katlanmıyor** → `IŞıK 1-M` gibi bir barkod yükü |

Ayrıca `:156-158`'deki şu yorum artık **yanlış**:

> `Code` zaten ASCII büyük harf üretiliyor (NextCode / AxisCodeDeriver), normalleştirilmiş iğne de büyük harf.

Bu yalnız **otomatik üretilen** kodlar için doğru (`A1`, `A2`). Panelde kod elle yazılabiliyor ve sahada kodlar `güzel elbise` gibi çok kelimeli Türkçe ifadeler olacak.

**Çözüm:** `NormalizeCode` gövdesini `SearchNormalizer.Normalize`'a bağla. Tek satır; üç bozukluk da kapanır çünkü hepsi aynı alanı okuyor.

#### Reddedilen alternatif: ayrı `CodeSearch` kolonu

`Name`/`NameSearch` ikilisine bakıp koda da ikinci bir kolon eklemek ilk akla gelen çözümdü. **Reddedildi**, gerekçeler:

- **Ad ile kod aynı cins şey değil.** Ad serbest metin (ham hâli korunmalı), kod bir kimlik — sistem onu bugün de kanonikleştiriyor. İkinci kolon, var olmayan bir soruna çözüm.
- **İkiye bölünen değer ayrışır.** `Code` ile `CodeSearch` arasındaki tutarlılığı hiçbir şey zorlamaz; yazma yollarından birinin unutulması sessiz bir hata olurdu.
- **Barkod hâlâ bozuk kalırdı.** `VariantCodeBuilder` ürün kodunu okuyor; ham `Code`'u korumak Türkçe harfi barkod yüküne taşımaya devam ederdi.
- **Maliyeti göç.** Kolon + geri doldurma + benzersiz indeks = prod'da bir EF göçü. Tek satırlık düzeltmeyle aynı sonucu almak varken.

**Kabul edilen bedel:** operatör `Güzel Elbise` yazdığında kartta `GUZEL ELBISE` görünür. Bu bugünkü `a5` → `A5` davranışının aynısının Türkçe'ye uzanmış hâli; üstelik operatöre **izleyicinin yazacağı metnin karşılaştırılacağı hâli** gösterdiği için yayında okurken de doğru olan bu.

**Kabul edilen ikinci bedel:** `ŞIK1` ile `SIK1` aynı koda katlanır, bir arada var olamaz (409). İstenen davranış — izleyici ikisini zaten ayırt edemezdi.

### Sorun 2 — Eski istemci stok defterini geri sarıyor

`StockLedgerReconciler.cs:80` bilinçli olarak şunu yapıyor: siparişin `ProductId`'si yoksa stok hareketi **yazılmaz**. Bu doğru.

Tehlike geri sarmada (`StockLedgerReconciler.cs:61-65`):

```csharp
foreach (var (key, have) in existing)
{
    if (desired.ContainsKey(key)) continue;
    if (have != 0) deltas.Add(new LedgerDelta(key, -have));
}
```

Bir etiket **önce** katalog bilgisiyle senkronlanır, `-1` hareketi yazılır. Sonra aynı sipariş **güncellenmemiş** bir WPF'ten tekrar gönderilir; o istemci `ProductId` alanını hiç doldurmadığı için `Desired` boş döner, mutabakat "bu siparişin artık stok etkisi yok" sonucuna varır ve **`+1` ters hareket** yazar. Stok sessizce geri eklenir.

Bu kurgusal değil: sipariş senkronu senkronlanmamış etiketleri tekrar gönderebiliyor (`OrderProductLinkSyncTests.Repushing_the_same_order_can_rebind_the_variant` tam bu yolu test ediyor) ve bir yayıncının iki operatör bilgisayarından biri güncellenmemiş olabilir.

Kök sebep, `ProductId = null`'ın **iki farklı şey** demesi:

- "Operatör ürünü belirleyemedi" → meşru, hareket yazılmaz.
- "Bu istemci katalog diye bir şey bilmiyor" → sunucu hiçbir şeye dokunmamalı.

**Çözüm:** İsteğe `CatalogAware` bayrağı. Eski istemciler alanı hiç göndermez → `false` → o paket için `StockLedgerWriter` **hiç çağrılmaz**. Sipariş senkronu her zamanki gibi işler; yalnız defter dokunulmadan kalır.

---

## Dosya yapısı

**Değişecek:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs` | `NormalizeCode` ortak normalleştiriciye bağlanır; `:156-158` yanlış yorumu düzelir |
| `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs` | `SyncOrdersRequest.CatalogAware`; bayrak yoksa defter yazıcısı atlanır |
| `OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs` | İstemci tarafındaki `SyncOrdersRequest` aynı alanı taşısın (iki tel modeli ayrışmasın) |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs` | Katlama + çakışma testleri |
| `OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs` | Geri sarma testi |

**Yeni dosya yok. Göç yok.**

**Not:** `SearchNormalizer` ve `TurkishAscii` **zaten var** (`OrderDeck.Shared/Text/`), yeniden yazılmayacak. `PanelProductsController` dosyasında `using OrderDeck.Shared.Text;` **zaten mevcut** (`:159` `SearchNormalizer.Normalize` çağırıyor) — yeni using gerekmez.

---

## Task 1: Ürün kodu ortak normalleştiriciden geçsin

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs:682-683`, `:150-163`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

`PanelProductsControllerTests.cs` içinde, mevcut `Create_normalizes_the_manual_code_and_rejects_the_duplicate` testinin (`:201-213`) hemen ardına ekle. Dosyanın kendi yardımcıları kullanılıyor: `SeedAsync()`, `CreateProductAsync(client, name, code:)`, `PostProductAsync(client, name, code:)`, `TitleAsync(resp)`.

```csharp
    /// <summary>
    /// "Işık 1" → ToUpperInvariant "IŞıK 1" üretir: ı (U+0131) küçük kalır,
    /// Ş yerinde durur. Kod bir kimlik ve izleyici yorumu ona karşı
    /// eşleştirilecek — Türkçe klavyesi olmayan biri "isik 1" yazdığında da
    /// aynı ürüne düşmeli.
    /// </summary>
    [Fact]
    public async Task Create_folds_turkish_letters_in_the_code()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Işıklı Elbise", code: "  Işık 1 ");

        product.Code.Should().Be("ISIK 1");
    }

    [Fact]
    public async Task Create_409_when_the_code_only_differs_by_turkish_letters()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Şık Elbise", code: "ŞIK1");

        // "sik1" katlandığında "SIK1"; "ŞIK1" de "SIK1". İzleyici ikisini
        // ayırt edemez, o yüzden bir arada var olamazlar.
        var resp = await PostProductAsync(client, "Sıkı Elbise", code: "sik1");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-code");
    }

    /// <summary>
    /// Çok kelimeli Türkçe kod sahadaki gerçek kullanım ("güzel elbise").
    /// Panel araması iğneyi SearchNormalizer'dan geçiriyor; saklanan kod da
    /// aynı normalleştiriciden geçmezse arama sessizce boş döner.
    /// </summary>
    [Fact]
    public async Task List_finds_a_multiword_turkish_code_typed_without_turkish_letters()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Elbise", code: "güzel elbise");

        var page = await client.GetFromJsonAsync<ProductPage>(
            "/api/panel/products?q=guzel%20elbise&page=1&pageSize=20");

        page!.Items.Should().ContainSingle()
            .Which.Code.Should().Be("GUZEL ELBISE");
    }
```

- [ ] **Step 2: Testleri çalıştır, başarısız olduklarını gör**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelProductsControllerTests.Create_folds_turkish_letters_in_the_code|FullyQualifiedName~PanelProductsControllerTests.Create_409_when_the_code_only_differs_by_turkish_letters|FullyQualifiedName~PanelProductsControllerTests.List_finds_a_multiword_turkish_code_typed_without_turkish_letters"
```
Expected: 3 FAILED.
- 1.: `"IŞıK 1"` beklenirken `"ISIK 1"` istendi.
- 2.: 409 yerine 201 Created.
- 3.: sayfa boş — `"GÜZEL ELBISE"` içinde `"GUZEL ELBISE"` aranmaz.

- [ ] **Step 3: `NormalizeCode`'u ortak normalleştiriciye bağla**

`PanelProductsController.cs:682-683`'ü şununla değiştir:

```csharp
    /// <summary>
    /// Kodun kanonik hâli. Kod bir <b>kimlik</b>: sistem onu ham saklamıyor,
    /// yazma anında tek bir biçime indirgiyor (bugün de öyle — "  a5 " → "A5").
    ///
    /// <c>ToUpperInvariant</c> TEK BAŞINA yetmiyor: Türkçe'de <c>ı</c>'yı küçük
    /// bırakır, <c>İ</c>'yi korur. Ürün adında kullanılan normalleştiricinin
    /// aynısı kullanılıyor ki dört tüketici de aynı kuralı paylaşsın —
    /// benzersizlik, panel araması, <c>VariantCodeBuilder</c> üstünden barkod
    /// yükü ve WPF'in izleyici yorumunu eşleştirmesi.
    ///
    /// Yan etki bilinçli: "ŞIK1" ile "SIK1" aynı koda iner, bir arada var
    /// olamaz. İzleyici ikisini zaten ayırt edemezdi.
    /// </summary>
    private static string NormalizeCode(string? code)
        => SearchNormalizer.Normalize(code);
```

Başka hiçbir çağrı yeri değişmiyor: `Create` (`:313`), `Update` (`:399`) ve `CodeTakenAsync` (`:617`) zaten bu metodun çıktısıyla çalışıyor.

- [ ] **Step 4: Aramadaki yanlış yorumu düzelt**

`PanelProductsController.cs:150-163` bloğundaki yorumu değiştir (kod aynı kalıyor — `p.Code` artık doğru kolon):

```csharp
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Aranan iğne de saklanan değer de AYNI normalleştiriciden geçiyor →
            // eşleşme veritabanının collation'ından bağımsız (SQL Server duyarsız,
            // PostgreSQL duyarlı; göçte davranış değişmesin).
            //
            // Kod için ayrı bir iğne gerekmiyor çünkü `Code` de yazma anında aynı
            // normalleştiriciden geçiyor (NormalizeCode). Eskiden burada "Code
            // zaten ASCII büyük harf" yazıyordu — bu yalnız OTOMATİK üretilen
            // kodlar (A1, A2) için doğruydu; kod elle yazılabiliyor ve sahada
            // "güzel elbise" gibi çok kelimeli Türkçe ifadeler oluyor.
            var needle = SearchNormalizer.Normalize(q);
            if (needle.Length > 0)
                query = query.Where(
                    p => p.NameSearch.Contains(needle) || p.Code.Contains(needle));
        }
```

- [ ] **Step 5: Testleri çalıştır, geçtiklerini gör**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelProductsControllerTests"
```
Expected: PASSED, 0 failed. Mevcut `Create_normalizes_the_manual_code_and_rejects_the_duplicate` (`"  a5 "` → `"A5"`) de geçmeye devam etmeli — `SearchNormalizer.Normalize("  a5 ")` yine `"A5"` üretir.

- [ ] **Step 6: Varyant/barkod testlerinin kırılmadığını doğrula**

Ürün kodu `VariantCodeBuilder`'ın girdisi; kod biçimi değiştiği için varyant kodu üreten testler de koşmalı.

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~Variant|FullyQualifiedName~Catalog"
```
Expected: PASSED, 0 failed.

**Beklenen bir davranış kayması (kod değişikliği gerektirmez, sadece bilinçli olsun):** `CatalogCodeSequence.Next` mevcut kodları `^([A-Z]+)([0-9]{1,3})$` deseniyle tarıyor. Katlamadan sonra `Işık1` gibi bir kod `ISIK1` olarak saklanacağı için artık desene **uyar** ve bir sonraki otomatik kod `ISIK2` olur. Bu, sınıfın belgelenmiş niyetiyle tutarlı ("yayıncı elle bir kod yazdığında sayaç oradan devam eder", `CatalogCodeSequence.cs:12-14`) — ASCII kodlarda bugün de böyle davranıyor.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs
git commit -m "fix(katalog): ürün kodu Türkçe harf ve harf boyutundan bağımsız eşleşsin"
```

---

## Task 2: `CatalogAware` bayrağı — eski istemci stok defterine dokunmasın

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs:171`, `:285-300`
- Modify: `OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs:41`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs`

**Dikkat — iki ayrı `SyncOrdersRequest` var:**
- `LicensesSessionsSyncController.cs:171` — sunucunun okuduğu **tel modeli** (`List<SyncOrderItem>`).
- `OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs:41` — WPF'in gönderdiği istemci DTO'su (`IReadOnlyList<SyncOrderItem>`).

Aynı JSON'un iki ucu; ortak tip yok. İkisi de değişecek — birini bırakmak tam olarak Dockerfile olayındaki ayrışma türü.

- [ ] **Step 1: Başarısız testi yaz**

`OrderProductLinkSyncTests.cs` sınıfının sonuna ekle. Dosyanın kendi `SeedAsync()` ve `OrderPayload(orderId, productId, variantId)` yardımcıları kullanılıyor; yeni yardımcı gerekmiyor.

```csharp
    /// <summary>
    /// ProductId = null İKİ ayrı şey demek olabilir: "operatör ürünü
    /// belirleyemedi" (meşru) ve "bu istemci katalog diye bir şey bilmiyor".
    /// İkisi ayırt edilmezse, güncellenmemiş bir WPF aynı siparişi tekrar
    /// gönderdiğinde mutabakat "artık stok etkisi yok" diye okur ve daha önce
    /// yazılmış satış hareketini +1 ile GERİ SARAR.
    /// </summary>
    [Fact]
    public async Task Catalog_unaware_client_does_not_unwind_the_stock_movement()
    {
        var (client, licenseId, productId, variantId) = await SeedAsync();
        var orderId = Guid.NewGuid();

        // 1) Güncel istemci: katalog kimlikleriyle gönderiyor → -1 hareket.
        (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[] { OrderPayload(orderId, productId, variantId) },
                catalogAware = true,
            })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.StockMovements.CountAsync(m => m.OrderId == orderId))
                .Should().Be(1, "güncel istemcinin satışı deftere girmeli");
        }

        // 2) Güncellenmemiş istemci: catalogAware alanını hiç göndermiyor.
        (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, null, null) } }))
            .EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.StockMovements
                .Where(m => m.OrderId == orderId)
                .ToListAsync();

            rows.Should().HaveCount(1, "eski istemcinin paketi deftere hiç girmemeli");
            rows.Sum(m => m.Quantity).Should().Be(-1);
        }
    }
```

- [ ] **Step 2: Testi çalıştır, başarısız olduğunu gör**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~OrderProductLinkSyncTests.Catalog_unaware_client_does_not_unwind_the_stock_movement"
```
Expected: FAILED — ikinci blokta 1 yerine 2 hareket satırı bulunur (`-1` ve onu geri sarmış `+1`), toplam 0.

- [ ] **Step 3: Sunucu tel modeline bayrağı ekle**

`LicensesSessionsSyncController.cs:171`:

```csharp
    /// <param name="CatalogAware">
    /// İstemcinin katalog kimliklerini (<c>ProductId</c>/<c>ProductVariantId</c>)
    /// doldurabildiğini bildirir. VARSAYILAN false, çünkü güncellenmemiş WPF
    /// sürümleri bu alanı hiç göndermez ve göndermemeleri hata değil.
    ///
    /// Bayrak olmadan <c>ProductId = null</c> iki ayrı durumu birbirine karıştırır:
    /// "operatör ürünü belirleyemedi" (meşru — hareket yazılmaz) ile "bu istemci
    /// katalog bilmiyor". İkincisinde sunucu deftere HİÇ dokunmamalı; aksi hâlde
    /// StockLedgerReconciler'ın "istenmeyen anahtarı sıfırla" dalı daha önce
    /// yazılmış satışı geri sarar ve stok sessizce şişer.
    /// </param>
    public sealed record SyncOrdersRequest(List<SyncOrderItem> Orders, bool CatalogAware = false);
```

- [ ] **Step 4: Defter yazıcısını bayrağa bağla**

`LicensesSessionsSyncController.cs:285-300`. Mevcut çağrıyı sarmalar hâle getir; `ledgerNow` ve yorumlar korunur:

```csharp
        // Katalog bilmeyen istemcinin paketi deftere HİÇ girmez: siparişler her
        // zamanki gibi kaydediliyor, yalnız mutabakat atlanıyor. Ayrım şart,
        // çünkü mutabakat "gönderilmeyen anahtar = artık geçersiz" varsayıyor
        // ve bu varsayım yalnız katalog bilen istemci için doğru.
        if (req.CatalogAware)
        {
            await _ledger.ApplyAsync(
                licenseId,
                orders.Select(o => new Services.Stock.LedgerOrderInput(
                    new Services.Stock.LedgerOrderState(
                        o.Id,
                        o.ProductId,
                        o.ProductVariantId,
                        o.IsShippingFee,
                        o.CancelledAt is not null,
                        o.IsTentativeBackup),
                    o.AddedAt,
                    o.CancelledAt)).ToList(),
                ledgerNow,
                ct);
        }
```

- [ ] **Step 5: İstemci DTO'sunu aynı hizaya getir**

`OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs:41`:

```csharp
/// <param name="CatalogAware">
/// Bu istemcinin <c>SyncOrderItem.ProductId</c>/<c>ProductVariantId</c>
/// alanlarını doldurabildiğini bildirir. Sunucu bayrağı yoksa o paket için
/// stok mutabakatını hiç çalıştırmaz. WPF katalog replikasını kurana kadar
/// varsayılan false kalıyor — Faz 1b WPF planında true'ya çevrilecek.
/// </param>
public sealed record SyncOrdersRequest(
    System.Collections.Generic.IReadOnlyList<SyncOrderItem> Orders,
    bool CatalogAware = false);
```

- [ ] **Step 6: Testi çalıştır, geçtiğini gör**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~OrderProductLinkSyncTests"
```
Expected: PASSED, 0 failed. Dosyadaki diğer üç test `catalogAware` göndermiyor ama yalnız `Orders` tablosunu doğruluyor — etkilenmezler.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs \
        OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs
git commit -m "fix(stok): katalog bilmeyen istemci stok defterini geri sarmasın"
```

---

## Task 3: Tam takım doğrulama

- [ ] **Step 1: Sunucu testlerinin tamamı**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: 0 failed. (Taban ~747 test; bu plan 4 test ekliyor.)

- [ ] **Step 2: WPF/Chat tarafı kırılmadı**

`SyncOrdersRequest` `OrderDeck.Licensing` içinde ve WPF onu kuruyor. Yeni parametre varsayılanlı olduğu için derleme kırılmamalı — doğrula:

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: 0 failed.

- [ ] **Step 3: Çözümü derle**

Run: `dotnet build OrderDeck.sln --configuration Release --nologo`
Expected: `Build succeeded`, 0 error.

- [ ] **Step 4: Mevcut prod verisini gözden geçir (deploy SONRASI, elle)**

Katlama yalnız **yazma anında** uygulanıyor; deploy'dan önce kaydedilmiş kodlar olduğu gibi kalır. Katalog sahada neredeyse boş olduğu için bu bir göç değil, bir **kontrol**:

`panel.orderdeckapp.com` → Ürünler listesi. Kodunda Türkçe harf (`ç ğ ı İ ö ş ü`) ya da küçük harf içeren ürün varsa, o ürünü aç ve kodu **değiştirmeden Kaydet** — yazma yolu kodu kanonik hâline indirir.

Bu adım atlanırsa hata sessizdir: WPF `GUZEL ELBISE` arar, veritabanında `GÜZEL ELBİSE` durur, eşleşme olmaz.

- [ ] **Step 5: Uçtan doğrulama (deploy sonrası)**

Panelde kodu `güzel elbise` olan bir ürün oluştur → listede kodun `GUZEL ELBISE` göründüğünü, arama kutusuna `guzel` yazınca bulunduğunu doğrula.

---

## Bu planın BİLEREK dışında bıraktıkları

- **WPF'in `CatalogAware = true` göndermesi** — Faz 1b WPF planına ait. Bu plan yalnız sunucuyu hazırlıyor; WPF bayrağı göndermediği sürece davranış bugünküyle aynı (stok mutabakatı çalışmaz), yani **tek başına deploy edilmesi güvenli**.
- **Ürün koduna karakter kısıtı** — konuşuldu ve **reddedildi**. Barkod ayrı alan (`ProductVariant.Barcode`), Türkçe klavye Türkiye'de yaygın, katlama zaten eşleştirmeyi çözüyor.
- **Ayrı `CodeSearch` kolonu** — değerlendirildi ve reddedildi; gerekçesi yukarıda "Reddedilen alternatif" başlığında.
- **`CatalogCodeSequence`'ın `Code` yerine başka bir kolondan sayması** — gerekmiyor; `Next` girdiyi kendi içinde büyük harfe çeviriyor ve `Code` artık zaten kanonik.
- **Yorum → ürün/varyant eşleştirmesi** — Faz 1b WPF planına ait.

---

## Sonraki planlar (bu iş bittikten sonra yazılacak)

Faz 1b'nin WPF ayağı tek plana sığmıyor; her biri tek başına çalışır durumda teslim edilebilir:

1. **WPF katalog replikası** — `025_catalog_replica.sql`, replika depoları, `CatalogSyncService` (hosted service), yerel `Product`/`ProductSize` tablolarının düşürülmesi (sahada boş olduğu doğrulandı), ürün kartının katalogdan beslenmesi.
2. **Yorum eşleştirme ve varyant seçimi** — kodu bulup metinden çıkarma, kalan metinde eksen değeri arama, varyant seçici, `Label`'ın `ProductId`/`ProductVariantId` taşıması, `CatalogAware = true`.
3. **WPF'te stok gösterimi** — `LicensesWpfStockPullController`'dan bakiye çekme, ürün kartında varyant başına bakiye.
