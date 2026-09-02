# Panelden WhatsApp Şablonu Oluşturma — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncı, WhatsApp Manager'a hiç gitmeden panelden şablon oluşturabilsin, onay durumunu görebilsin, düzenleyip silebilsin.

**Architecture:** Sunucuda tek Graph istemcisi (`WhatsAppTemplateCatalog`) okuma + yazma uçlarını birlikte taşır; saf doğrulama `WhatsAppTemplateShape`'e taşınır ve genişler. Panel ucu (`api/panel/whatsapp-message-templates`) yazmadan önce şablonun bu WABA'ya ait olduğunu listeden doğrular — Meta'nın düzenle/sil uçları WABA kapsamlı değil. **Bağlayıcı değişmez:** formun ürettiği her taslak, Graph JSON'una çevrilip `ReadTemplate` ile geri okunduğunda `UnsupportedReason == null` vermek zorunda; yoksa panelde oluşturulup panelde gönderilemeyen şablon doğar.

**Tech Stack:** ASP.NET Core 10 (`OrderDeck.LicenseServer`), xUnit + `ApiFactory` (InMemory), React 18 + TanStack Query + Vitest + Testing Library (`OrderDeck-Mobile/apps/panel`).

**Spec:** `docs/superpowers/specs/2026-09-01-wa-sablon-olusturma-design.md`

---

## Dosya yapısı

### Sunucu — `C:\Users\burak\source\repos\LiveDeck`

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateShape.cs` (**YENİ**) | Saf şekil/doğrulama: yer tutucu sayımı, taslak kaydı, ad/kategori/bileşen doğrulayıcıları. HTTP yok. |
| `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs` (**DEĞİŞİR**) | Graph okuma **ve** yazma. `ApprovedTemplate` → `WabaTemplate` (Id/Status/RejectedReason kazanır). |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs` (**YENİ**) | `api/panel/whatsapp-message-templates` — liste/oluştur/düzenle/sil + sahiplik kontrolü. |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs` (**DEĞİŞİR**) | Yalnız tip adı düzeltmesi. |
| `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs` (**DEĞİŞİR**) | Yalnız tip adı düzeltmesi (WPF ayar ekranına bakıyor). |

Testler:

| Dosya | Kapsam |
|---|---|
| `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs` (**DEĞİŞİR**) | Mevcut okuma testleri + yeni alanlar. |
| `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateDraftTests.cs` (**YENİ**) | `WhatsAppTemplateShape` doğrulayıcıları. |
| `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs` (**YENİ**) | Giden istek gövdesi + gidiş-dönüş değişmezi. |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs` (**YENİ**) | Uç davranışı, sahiplik, yetki. |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs` (**DEĞİŞİR**) | `FakeTemplateCatalog` yeni üyeleri uygular. |

### Panel — `C:\Users\burak\source\repos\OrderDeck-Mobile`

| Dosya | Sorumluluk |
|---|---|
| `apps/panel/src/api/whatsappMessageTemplates.ts` (**YENİ**) | Tipler, hook'lar, `toTemplateName` slug yardımcısı. |
| `apps/panel/src/screens/WhatsAppMesajSablonlariScreen.tsx` (**YENİ**) | Liste + durum rozetleri + silme. |
| `apps/panel/src/screens/WhatsAppMesajSablonScreen.tsx` (**YENİ**) | Oluştur/düzenle formu + canlı önizleme. |
| `apps/panel/src/router.tsx` (**DEĞİŞİR**) | 3 rota. |
| `apps/panel/src/screens/DahaFazlaScreen.tsx` (**DEĞİŞİR**) | İletişim altında NavRow. |
| `apps/panel/src/screens/WhatsAppMesajSablonlariScreen.test.tsx` (**YENİ**) | Liste testleri. |
| `apps/panel/src/screens/WhatsAppMesajSablonScreen.test.tsx` (**YENİ**) | Form testleri. |

---

## Task 1: `WhatsAppTemplateShape`'i kendi dosyasına taşı

Saf davranış değişmiyor; bütün yeni doğrulama buraya geleceği için 385 satırlık katalog dosyasının "yalnız HTTP yapar" sözleşmesi korunmalı.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateShape.cs`
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs:44-112` (sınıfı sil)

- [ ] **Step 1: Yeni dosyayı oluştur**

`WhatsAppTemplateShape.cs` içeriği — katalog dosyasındaki 44-112. satırların birebir kopyası, kendi `using`'leriyle:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Şablon şekli: yer tutucuların çözümlenmesi ve taslak doğrulaması.
///
/// <para>HTTP'den ayrı duruyor çünkü asıl incelik burada: yanlış sayıda parametre
/// göndermek Meta'dan 132000 ile döner ve şablon <b>ücretli</b> olduğu için
/// yayıncı parasını denemelere yatırır.</para>
/// </summary>
public static class WhatsAppTemplateShape
{
    /// <summary>Yer tutucunun kendisi — içi rakam mı isim mi, ayrımı çağıran yapar.</summary>
    private static readonly Regex Placeholder =
        new(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant);

    public const string NamedParams =
        "Bu şablon isimli değişken kullanıyor; panel yalnız {{1}}, {{2}} biçimini gönderebiliyor.";

    public const string GappedParams =
        "Şablonun değişken numaraları 1'den başlayarak sırayla gitmiyor.";

    public const string HeaderMedia =
        "Şablonun başlığında görsel/belge var; panel yalnız metin başlıklı şablon gönderebiliyor.";

    public const string HeaderVariable =
        "Şablonun başlığında değişken var; panel yalnız gövde değişkenlerini doldurabiliyor.";

    public const string ButtonVariable =
        "Şablonun butonu değişken istiyor; panel bu tür şablonu gönderemiyor.";

    public const string AuthCategory =
        "Doğrulama (authentication) şablonları ayrı bir gönderim biçimi istiyor.";

    /// <summary>
    /// Gövdedeki konumsal parametre sayısı.
    /// </summary>
    /// <returns><c>Unsupported</c> doluysa gövde bizim gönderebileceğimiz
    /// biçimde değil ve <c>Count</c> anlamsızdır.</returns>
    public static (int Count, string? Unsupported) CountBodyParams(string bodyText)
    {
        var matches = Placeholder.Matches(bodyText);
        if (matches.Count == 0) return (0, null);

        var indexes = new SortedSet<int>();
        foreach (Match m in matches)
        {
            var inner = m.Groups[1].Value.Trim();
            // Meta 2024'ten beri {{musteri_adi}} gibi isimli değişkene de izin
            // veriyor. Gönderenimiz konumsal dizi yolluyor; isimli şablonda o
            // dizi sessizce yanlış yere oturmaz, Meta reddeder — ama ücretli
            // denemeye bırakmak yerine burada eliyoruz.
            if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return (0, NamedParams);
            }
            indexes.Add(n);
        }

        // 1..n bitişik olmalı. Meta bunu kendi de zorluyor ama liste bizim
        // verimiz değil: boşluklu bir dizide ({{1}}, {{3}}) yayıncının girdiği
        // değerler bir sıra kayar ve yanlış bilgi müşteriye gider.
        var expected = 1;
        foreach (var n in indexes)
        {
            if (n != expected++) return (0, GappedParams);
        }

        return (indexes.Count, null);
    }
}
```

- [ ] **Step 2: Katalog dosyasından sınıfı sil**

`WhatsAppTemplateCatalog.cs` içinde `/// <summary>` ile başlayıp `public static class WhatsAppTemplateShape { ... }` kapanış süslüsüyle biten blok (44-112) tamamen silinir. `using System.Text.RegularExpressions;` (satır 3) de silinir — dosyada başka Regex kullanımı yok. `using System.Net.Http.Headers;`, `System.Text.Json`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options` kalır.

- [ ] **Step 3: Derle ve testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplate"`
Expected: PASS — aynı namespace olduğu için hiçbir çağrı yeri değişmedi.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateShape.cs OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs
git commit -m "refactor(whatsapp): şablon şekil mantığını kendi dosyasına taşı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 2: `ApprovedTemplate` → `WabaTemplate`, Id/Status/RejectedReason

Düzenleme ve silme şablonun **id**'sini ister; onay durumu ve ret sebebi panelin asıl eksiği. Kayıt artık yalnız onaylıyı değil her durumu taşıyor, adı da onu söylemeli.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`WhatsAppTemplateCatalogTests.cs` sonuna (sınıf içine) ekle:

```csharp
    [Fact]
    public async Task Liste_sablonun_kimligini_ve_durumunu_tasir()
    {
        var handler = new StubHandler("""
        {"data":[{"id":"1200","name":"kargo","status":"APPROVED","category":"UTILITY",
                  "language":"tr","components":[{"type":"BODY","text":"Kargonuz yolda."}]}]}
        """);

        var result = await ListAsync(handler);

        Assert.True(result.Ok);
        var t = Assert.Single(result.Value!);
        Assert.Equal("1200", t.Id);
        Assert.Equal("APPROVED", t.Status);
        Assert.Null(t.RejectedReason);
    }
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Liste_sablonun_kimligini"`
Expected: derleme hatası — `ApprovedTemplate` içinde `Id` yok.

- [ ] **Step 3: Kaydı değiştir**

`WhatsAppTemplateCatalog.cs:21-31` yerine:

```csharp
/// <summary>
/// Meta'daki bir şablonun panelin ihtiyaç duyduğu hâli — <b>her durumdan</b>
/// (APPROVED, PENDING, REJECTED, PAUSED…).
///
/// <para><b>Gövde metni neden taşınıyor:</b> onaylı metin Meta'da duruyor ve biz
/// hiçbir yerde saklamıyoruz. Panel yayıncıya "hangi mesaj gidecek" sorusunu
/// ancak bu alanla cevaplayabiliyor — şablonu adından seçtirmek, içeriğini
/// görmeden ücretli mesaj göndertmek demekti.</para>
///
/// <para><paramref name="UnsupportedReason"/> doluysa şablon listede görünür ama
/// gönderilemez. Gizlemek yerine sebebini yazıyoruz: yayıncı Meta'da onaylattığı
/// şablonu panelde hiç göremezse eksikliği bize değil kendi hesabına yorar.</para>
///
/// <para><paramref name="RejectedReason"/> Meta'nın ham kodu (örn.
/// <c>INVALID_FORMAT</c>). Çevirmiyoruz: ret sebebini aramaya çıkan yayıncı ancak
/// bu dizgeyle Meta belgelerinde karşılık bulabiliyor.</para>
/// </summary>
public sealed record WabaTemplate(
    string Id,
    string Name,
    string Language,
    string Category,
    string Status,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons,
    int ParameterCount,
    IReadOnlyList<string> ParameterExamples,
    string? UnsupportedReason,
    string? RejectedReason);
```

- [ ] **Step 4: Tip adını dosya boyunca değiştir**

```bash
cd /c/Users/burak/source/repos/LiveDeck
sed -i 's/ApprovedTemplate/WabaTemplate/g' \
  OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs \
  OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
```

Sonra elle düzelt: `PanelWhatsAppApprovedTemplatesController.cs` ve `LicensesWhatsAppApprovedTemplatesController.cs` içinde `ApprovedTemplate` geçen satırlar (`sed` çalıştırılmadı, çünkü o dosyalarda `PanelWhatsAppApprovedTemplatesController` gibi **sınıf adları** da eşleşirdi). Grep'le bul, yalnız tip kullanımlarını değiştir:

```bash
grep -n "ApprovedTemplate" OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs
```

- [ ] **Step 5: Ayrıştırıcıyı düzelt**

`ReadTemplate` artık durum süzmüyor — süzme `ListApprovedAsync`'e taşınıyor. `WhatsAppTemplateCatalog.cs` içinde:

`ReadTemplate` başındaki blok:

```csharp
    /// <summary>Tek şablon satırı → <see cref="WabaTemplate"/>. Şekli tanınmayan
    /// satır için null (listeye girmez). Durum süzmesi burada DEĞİL: aynı
    /// ayrıştırıcı hem onaylı listeye hem yönetim listesine hizmet ediyor.</summary>
    private static WabaTemplate? ReadTemplate(JsonElement item)
    {
        var id = Str(item, "id");
        var name = Str(item, "name");
        var language = Str(item, "language");
        var status = Str(item, "status") ?? "";
        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(language)) return null;
```

(`if (!string.Equals(status, "APPROVED", ...)) return null;` satırı **silinir**.)

`ReadTemplate` sonundaki `return` yerine:

```csharp
        return new WabaTemplate(
            id!, name!, language!, category, status, headerText, bodyText!, footerText,
            buttons, count, examples, unsupported, Str(item, "rejected_reason"));
```

- [ ] **Step 6: Graph alan listesine `id` ve `rejected_reason` ekle**

`ListApprovedAsync` içindeki URL satırı:

```csharp
            $"?fields=id,name,status,category,language,components,rejected_reason&limit={PageLimit}";
```

- [ ] **Step 7: `ListApprovedAsync` durumu kendisi süzsün**

`list.Sort(...)` çağrısından **önce** ekle:

```csharp
        // Onay bekleyen ya da reddedilen şablon gönderilemez; gönderim listesinde
        // göstermek yayıncıya gönderebileceği izlenimi verirdi. Ayrıştırıcı artık
        // hepsini okuduğu için süzme burada.
        list.RemoveAll(t => !string.Equals(t.Status, "APPROVED", StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 8: Mevcut test kurucularını genişlet**

`PanelWhatsAppApprovedTemplatesControllerTests.cs:125` ve `:153` — pozisyonel kurucular yeni arite ile:

```csharp
            new WabaTemplate(
                "1001", "odeme_hatirlatma", "tr", "UTILITY", "APPROVED", "Sipariş bilgisi",
                "Merhaba {{1}}, {{2}} TL", "OrderDeck", ["Tamam"], 2, ["Ayşe", "250"], null, null),
```

```csharp
            new WabaTemplate(
                "1002", "kargo", "tr", "UTILITY", "APPROVED", null, "Kargonuz yolda.", null,
                [], 0, [], WhatsAppTemplateShape.HeaderMedia, null),
```

`WhatsAppTemplateCatalogTests.cs` içindeki JSON sabitlerinde `"id"` alanı olmayan satırlar artık **ayrıştırılamaz** (null döner). Her `"name":` öncesine `"id":"<sıra>",` ekle; testlerin beklediği sayılar değişmez.

- [ ] **Step 9: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplate|FullyQualifiedName~PanelWhatsAppApprovedTemplates"`
Expected: PASS

- [ ] **Step 10: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "refactor(whatsapp): şablon kaydına kimlik, durum ve ret sebebi ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 3: `ListAllAsync` — her durumdaki şablon

Panelin yönetim listesi PENDING ve REJECTED satırları da görmek zorunda; asıl eksik olan buydu.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

```csharp
    [Fact]
    public async Task ListAll_onaysiz_sablonlari_da_dondurur()
    {
        var handler = new StubHandler("""
        {"data":[
          {"id":"1","name":"a","status":"APPROVED","category":"UTILITY","language":"tr",
           "components":[{"type":"BODY","text":"Onaylı"}]},
          {"id":"2","name":"b","status":"REJECTED","category":"MARKETING","language":"tr",
           "rejected_reason":"INVALID_FORMAT",
           "components":[{"type":"BODY","text":"Reddedilen"}]}
        ]}
        """);

        var result = await Catalog(handler).ListAllAsync("WABA1", "TOKEN", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("INVALID_FORMAT", result.Value!.Single(t => t.Name == "b").RejectedReason);
    }
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ListAll_onaysiz"`
Expected: derleme hatası — `ListAllAsync` yok.

- [ ] **Step 3: Arayüze ekle**

`IWhatsAppTemplateCatalog` içine:

```csharp
    /// <summary>Durumdan bağımsız tüm şablonlar. Panelin yönetim ekranı onay
    /// bekleyeni ve reddedileni de göstermek zorunda; ayrıca yazma uçları
    /// sahipliği bu listeyle doğruluyor (Meta'nın düzenle/sil uçları WABA
    /// kapsamlı değil).</summary>
    Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
        string wabaId, string businessToken, CancellationToken ct);
```

- [ ] **Step 4: Gerçeklemede ortak yolu ayır**

Mevcut `ListApprovedAsync` gövdesi olduğu gibi `ListAllAsync`'e taşınır (yalnız `RemoveAll` satırı çıkarılır), `ListApprovedAsync` ince sarmalayıcı olur:

```csharp
    public async Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        // ...(eski ListApprovedAsync gövdesi, RemoveAll satırı OLMADAN)...
    }

    /// <summary>Yalnız <c>APPROVED</c> şablonlar — gönderim listesi.</summary>
    public async Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        var all = await ListAllAsync(wabaId, businessToken, ct);
        if (!all.Ok) return all;

        var approved = all.Value!
            .Where(t => string.Equals(t.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return GraphResult<IReadOnlyList<WabaTemplate>>.Success(approved);
    }
```

`using System.Linq;` gerekmiyor (implicit usings açık).

- [ ] **Step 5: `FakeTemplateCatalog`'u güncelle**

`PanelWhatsAppApprovedTemplatesControllerTests.cs` içindeki sahte, yeni üyeyi de uygulamak zorunda:

```csharp
        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
            string wabaId, string businessToken, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            SeenToken = businessToken;
            return Task.FromResult(Result);
        }
```

- [ ] **Step 6: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplate|FullyQualifiedName~PanelWhatsAppApprovedTemplates"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "feat(whatsapp): her durumdaki şablonu döndüren ListAllAsync ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 4: Taslak kaydı ve doğrulayıcılar

Meta'nın 132000 hatası okunmaz ve şablon **ücretli**; hatalı taslağı Graph'a hiç çıkarmadan eliyoruz. Ad ve kategori ayrı doğrulayıcılarda, çünkü düzenleme yolunda ikisi de gönderilmiyor — taslağa gömülselerdi güncellemede sahte değer uydurmak gerekirdi.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateShape.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateDraftTests.cs` (yeni)

- [ ] **Step 1: Başarısız testleri yaz**

`WhatsAppTemplateDraftTests.cs`:

```csharp
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public class WhatsAppTemplateDraftTests
{
    private static WhatsAppTemplateDraft Draft(
        string body = "Merhaba!",
        string? header = null,
        string? footer = null,
        IReadOnlyList<string>? examples = null,
        IReadOnlyList<WhatsAppTemplateButton>? buttons = null) =>
        new(header, body, footer, examples ?? [], buttons ?? []);

    [Theory]
    [InlineData("siparis_hatirlatma")]
    [InlineData("kargo2")]
    public void Gecerli_ad_kabul_edilir(string name) =>
        Assert.Null(WhatsAppTemplateShape.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("Sipariş")]      // büyük harf + Türkçe karakter
    [InlineData("kargo-bildirim")] // tire
    [InlineData("kargo bildirim")] // boşluk
    public void Gecersiz_ad_reddedilir(string name) =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateName(name));

    [Fact]
    public void Cok_uzun_ad_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateName(new string('a', 513)));

    [Theory]
    [InlineData("MARKETING")]
    [InlineData("UTILITY")]
    public void Gecerli_kategori_kabul_edilir(string c) =>
        Assert.Null(WhatsAppTemplateShape.ValidateCategory(c));

    // AUTHENTICATION şablonu OTP buton parametresi istiyor; gönderenimiz onu
    // yollamıyor, yani panelde oluşturulup panelde gönderilemezdi.
    [Theory]
    [InlineData("AUTHENTICATION")]
    [InlineData("")]
    [InlineData("marketing")] // küçük harf: Meta büyük harf bekliyor
    public void Gecersiz_kategori_reddedilir(string c) =>
        Assert.NotNull(WhatsAppTemplateShape.ValidateCategory(c));

    [Fact]
    public void Bos_govde_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(body: "   ")));

    [Fact]
    public void Uzun_govde_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(body: new string('a', 1025))));

    [Fact]
    public void Degiskenli_govde_ayni_sayida_ornek_ister()
    {
        var eksik = WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}, {{2}} TL", examples: ["Ayşe"]));
        Assert.NotNull(eksik);

        var tam = WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}, {{2}} TL", examples: ["Ayşe", "250"]));
        Assert.Null(tam);
    }

    // Meta örneksiz değişkenli şablonu reddediyor; boş dizgeyi örnek saymak
    // yayıncıya "gönderdim" deyip Meta'dan ret aldırırdı.
    [Fact]
    public void Bos_ornek_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{1}}", examples: ["  "])));

    [Fact]
    public void Isimli_degisken_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(
            Draft(body: "Merhaba {{ad}}", examples: ["Ayşe"])));

    [Fact]
    public void Baslikta_degisken_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(header: "Sipariş {{1}}")));

    [Fact]
    public void Uzun_baslik_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(header: new string('a', 61))));

    [Fact]
    public void Uzun_altbilgi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(footer: new string('a', 61))));

    [Fact]
    public void Gecerli_butonlar_kabul_edilir() =>
        Assert.Null(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", "Evet", null, null),
            new("QUICK_REPLY", "Hayır", null, null),
            new("URL", "Siteye git", "https://orderdeckapp.com", null),
            new("PHONE_NUMBER", "Ara", null, "+905321234567"),
        ])));

    [Fact]
    public void Degiskenli_buton_urlsi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("URL", "Takip", "https://orderdeckapp.com/{{1}}", null),
        ])));

    [Fact]
    public void Bilinmeyen_buton_turu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("COPY_CODE", "Kodu kopyala", null, null),
        ])));

    [Fact]
    public void Bos_buton_etiketi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", " ", null, null),
        ])));

    [Fact]
    public void Uzun_buton_etiketi_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", new string('a', 26), null, null),
        ])));

    [Fact]
    public void Ikiden_fazla_url_butonu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("URL", "1", "https://a.test", null),
            new("URL", "2", "https://b.test", null),
            new("URL", "3", "https://c.test", null),
        ])));

    [Fact]
    public void Birden_fazla_telefon_butonu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("PHONE_NUMBER", "Ara", null, "+905321234567"),
            new("PHONE_NUMBER", "Ara 2", null, "+905321234568"),
        ])));

    // Meta hızlı yanıt butonlarının bitişik olmasını şart koşuyor. Sessizce
    // yeniden sıralamak yayıncının tasarladığı düzeni değiştirmek olurdu.
    [Fact]
    public void Bolunmus_hizli_yanit_grubu_reddedilir() =>
        Assert.NotNull(WhatsAppTemplateShape.Validate(Draft(buttons: [
            new("QUICK_REPLY", "Evet", null, null),
            new("URL", "Site", "https://a.test", null),
            new("QUICK_REPLY", "Hayır", null, null),
        ])));
}
```

- [ ] **Step 2: Testlerin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplateDraftTests"`
Expected: derleme hatası — `WhatsAppTemplateDraft`, `WhatsAppTemplateButton`, `ValidateName`, `ValidateCategory`, `Validate` yok.

- [ ] **Step 3: Kayıtları ve doğrulayıcıları yaz**

`WhatsAppTemplateShape.cs` içine, `WhatsAppTemplateShape` sınıfının **üstüne**:

```csharp
/// <summary>Şablon butonu. <paramref name="Type"/> yalnız <c>QUICK_REPLY</c>,
/// <c>URL</c> ya da <c>PHONE_NUMBER</c> olabilir — gönderenimiz buton parametresi
/// yollamadığı için ötekiler oluşturulur ama gönderilemezdi.</summary>
public sealed record WhatsAppTemplateButton(string Type, string Text, string? Url, string? PhoneNumber);

/// <summary>
/// Şablonun <b>bileşenleri</b> — ad, kategori ve dil bilerek yok.
///
/// <para>Meta'nın düzenleme ucu yalnız bileşenleri güncelliyor; ad/kategori/dil
/// buraya konsaydı güncelleme yolunda uydurma değer taşımak gerekirdi.</para>
/// </summary>
public sealed record WhatsAppTemplateDraft(
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> BodyExamples,
    IReadOnlyList<WhatsAppTemplateButton> Buttons);
```

`WhatsAppTemplateShape` sınıfının içine (mevcut sabitlerin ve `CountBodyParams`'ın altına):

```csharp
    private static readonly Regex NamePattern =
        new("^[a-z0-9_]+$", RegexOptions.CultureInvariant);

    private const int MaxNameLength = 512;
    private const int MaxBodyLength = 1024;
    private const int MaxHeaderLength = 60;
    private const int MaxFooterLength = 60;
    private const int MaxButtonTextLength = 25;
    private const int MaxButtons = 10;
    private const int MaxUrlButtons = 2;
    private const int MaxPhoneButtons = 1;

    /// <returns>İlk hata metni (Türkçe, doğrudan yayıncıya gösterilir) ya da null.</returns>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Şablon adı boş olamaz.";
        if (name.Length > MaxNameLength) return $"Şablon adı en çok {MaxNameLength} karakter olabilir.";
        if (!NamePattern.IsMatch(name))
            return "Şablon adı yalnız küçük harf, rakam ve alt çizgi içerebilir (örn. siparis_hatirlatma).";
        return null;
    }

    /// <summary>Yalnız iki kategori. <c>AUTHENTICATION</c> OTP buton parametresi
    /// ister; gönderenimiz onu yollamıyor, yani oluşturulur ama gönderilemezdi.</summary>
    public static string? ValidateCategory(string category) =>
        category is "MARKETING" or "UTILITY"
            ? null
            : "Kategori yalnız MARKETING ya da UTILITY olabilir.";

    /// <summary>Bileşen doğrulaması. Meta'ya çıkmadan eliyoruz: 132000 hatası
    /// okunmaz ve şablon ücretli.</summary>
    public static string? Validate(WhatsAppTemplateDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.BodyText)) return "Mesaj metni boş olamaz.";
        if (draft.BodyText.Length > MaxBodyLength)
            return $"Mesaj metni en çok {MaxBodyLength} karakter olabilir.";

        var (count, unsupported) = CountBodyParams(draft.BodyText);
        if (unsupported is not null) return unsupported;

        if (draft.BodyExamples.Count != count)
            return $"Metinde {count} değişken var; {count} örnek değer girilmeli.";
        if (draft.BodyExamples.Any(string.IsNullOrWhiteSpace))
            return "Örnek değerler boş bırakılamaz; Meta örneksiz şablonu reddediyor.";

        if (draft.HeaderText is { } header)
        {
            if (header.Length > MaxHeaderLength)
                return $"Başlık en çok {MaxHeaderLength} karakter olabilir.";
            if (header.Contains("{{", StringComparison.Ordinal)) return HeaderVariable;
        }

        if (draft.FooterText is { Length: > MaxFooterLength })
            return $"Alt bilgi en çok {MaxFooterLength} karakter olabilir.";

        return ValidateButtons(draft.Buttons);
    }

    private static string? ValidateButtons(IReadOnlyList<WhatsAppTemplateButton> buttons)
    {
        if (buttons.Count == 0) return null;
        if (buttons.Count > MaxButtons) return $"En çok {MaxButtons} buton eklenebilir.";

        var urls = 0;
        var phones = 0;

        foreach (var b in buttons)
        {
            if (string.IsNullOrWhiteSpace(b.Text)) return "Buton yazısı boş olamaz.";
            if (b.Text.Length > MaxButtonTextLength)
                return $"Buton yazısı en çok {MaxButtonTextLength} karakter olabilir.";

            switch (b.Type)
            {
                case "QUICK_REPLY":
                    break;

                case "URL":
                    if (string.IsNullOrWhiteSpace(b.Url)) return "Bağlantı butonunda adres boş olamaz.";
                    if (b.Url.Contains("{{", StringComparison.Ordinal)) return ButtonVariable;
                    if (++urls > MaxUrlButtons)
                        return $"En çok {MaxUrlButtons} bağlantı butonu eklenebilir.";
                    break;

                case "PHONE_NUMBER":
                    if (string.IsNullOrWhiteSpace(b.PhoneNumber)) return "Arama butonunda numara boş olamaz.";
                    if (++phones > MaxPhoneButtons)
                        return $"En çok {MaxPhoneButtons} arama butonu eklenebilir.";
                    break;

                default:
                    return ButtonVariable;
            }
        }

        // Meta hızlı yanıt butonlarının bitişik durmasını şart koşuyor. Sessizce
        // yeniden sıralamak yayıncının tasarladığı düzeni değiştirmek olurdu.
        var seenOther = false;
        var reopened = false;
        var started = false;
        foreach (var b in buttons)
        {
            if (b.Type == "QUICK_REPLY")
            {
                if (started && seenOther) reopened = true;
                started = true;
            }
            else if (started)
            {
                seenOther = true;
            }
        }
        if (reopened) return "Hızlı yanıt butonları arka arkaya sıralanmalı.";

        return null;
    }
```

- [ ] **Step 4: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplateDraftTests"`
Expected: PASS (24 test)

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateShape.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateDraftTests.cs
git commit -m "feat(whatsapp): şablon taslağı ve doğrulayıcıları ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 5: Butonları tipli oku

Düzenleme formu butonu önceden doldurmak zorunda. Bugün katalog yalnız **etiketi** okuyor; adres ve telefon kayboluyordu — yayıncı düzenlemeye girip kaydettiğinde butonun adresi sessizce silinirdi.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs:84`
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs:89`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`WhatsAppTemplateCatalogTests.cs` sınıfına ekle:

```csharp
    [Fact]
    public async Task Butonlarin_adresi_ve_numarasi_okunuyor()
    {
        var handler = new StubHandler("""
        {"data":[{"id":"7","name":"kampanya","status":"APPROVED","category":"MARKETING",
          "language":"tr","components":[
            {"type":"BODY","text":"İndirim başladı."},
            {"type":"BUTTONS","buttons":[
              {"type":"URL","text":"Siteye git","url":"https://orderdeckapp.com"},
              {"type":"PHONE_NUMBER","text":"Ara","phone_number":"+905321234567"},
              {"type":"QUICK_REPLY","text":"Tamam"}]}]}]}
        """);

        var result = await ListAsync(handler);

        var b = Assert.Single(result.Value!).Buttons;
        Assert.Equal(["URL", "PHONE_NUMBER", "QUICK_REPLY"], b.Select(x => x.Type));
        Assert.Equal("https://orderdeckapp.com", b[0].Url);
        Assert.Equal("+905321234567", b[1].PhoneNumber);
        Assert.Null(b[2].Url);
    }
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Butonlarin_adresi"`
Expected: derleme hatası — `string` üzerinde `.Type` yok.

- [ ] **Step 3: Kaydı ve ayrıştırıcıyı değiştir**

`WabaTemplate` içinde:

```csharp
    IReadOnlyList<WhatsAppTemplateButton> Buttons,
```

`ReadTemplate` içinde `var buttons = new List<string>();` yerine:

```csharp
        var buttons = new List<WhatsAppTemplateButton>();
```

`ReadButtons` tamamen:

```csharp
    /// <summary>Butonları tipiyle birlikte okur — düzenleme formu adresi ve
    /// numarayı geri doldurmak zorunda; yalnız etiketi taşısaydık kaydeden
    /// yayıncı butonun adresini sessizce silerdi.</summary>
    private static void ReadButtons(
        JsonElement buttonsComponent, List<WhatsAppTemplateButton> into, ref string? unsupported)
    {
        if (!buttonsComponent.TryGetProperty("buttons", out var bs) || bs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var b in bs.EnumerateArray())
        {
            var type = (Str(b, "type") ?? "").ToUpperInvariant();
            var url = Str(b, "url");
            into.Add(new WhatsAppTemplateButton(type, Str(b, "text") ?? "", url, Str(b, "phone_number")));

            // Dinamik URL soneki ve kopyalanabilir kod, gövdeden AYRI bir
            // bileşen parametresi istiyor. Sabit butonlar (quick reply, düz URL,
            // telefon) parametresiz çalıştığı için sorun değil.
            if (type == "COPY_CODE" || (url?.Contains("{{", StringComparison.Ordinal) ?? false))
                unsupported ??= WhatsAppTemplateShape.ButtonVariable;
        }
    }
```

- [ ] **Step 4: İki mevcut denetleyiciyi etikete indir**

`PanelWhatsAppApprovedTemplatesController.cs:84` ve `LicensesWhatsAppApprovedTemplatesController.cs:89` — bu iki uç gönderim listesi; buton ayrıntısına ihtiyaçları yok, sözleşmeleri değişmesin:

```csharp
            t.Buttons.Select(b => b.Text).ToList(), t.ParameterCount, t.ParameterExamples, t.UnsupportedReason)));
```

- [ ] **Step 5: Mevcut testleri düzelt**

`WhatsAppTemplateCatalogTests.cs:96` ve `:182`:

```csharp
        t.Buttons.Select(b => b.Text).Should().Equal("Tamam");
```

```csharp
        t.Buttons.Select(b => b.Text).Should().Equal("Siteye git", "Ara");
```

`PanelWhatsAppApprovedTemplatesControllerTests.cs` içindeki `new WabaTemplate(...)` çağrılarında `["Tamam"]` yerine (denetleyici testi DTO üstünden baktığı için `:139` satırı değişmez):

```csharp
                [new WhatsAppTemplateButton("QUICK_REPLY", "Tamam", null, null)],
```

- [ ] **Step 6: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplate|FullyQualifiedName~PanelWhatsAppApprovedTemplates|FullyQualifiedName~LicensesWhatsApp"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppApprovedTemplatesController.cs OrderDeck.LicenseServer/Controllers/Licenses/LicensesWhatsAppApprovedTemplatesController.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateCatalogTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "feat(whatsapp): şablon butonlarını tipiyle birlikte oku

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 6: `CreateAsync` — şablon oluştur

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

`WhatsAppTemplateWriteTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public class WhatsAppTemplateWriteTests
{
    /// <summary>Giden isteğin gövdesini metin olarak saklar — istek elden
    /// çıktıktan sonra <c>Content</c> okunamıyor.</summary>
    private sealed class CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpMethod? Method;
        public string? Url;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri!.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static WhatsAppTemplateCatalog Catalog(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new WhatsAppOptions
            {
                GraphBaseUrl = "https://graph.test",
                GraphApiVersion = "v25.0",
            }),
            NullLogger<WhatsAppTemplateCatalog>.Instance);

    private static WhatsAppTemplateDraft Draft() => new(
        "Sipariş bilgisi",
        "Merhaba {{1}}, {{2}} TL tutarındaki siparişiniz hazır.",
        "OrderDeck",
        ["Ayşe", "250"],
        [new WhatsAppTemplateButton("QUICK_REPLY", "Tamam", null, null)]);

    [Fact]
    public async Task Create_dogru_uca_dogru_govdeyi_gonderiyor()
    {
        var handler = new CapturingHandler("""{"id":"9001","status":"PENDING","category":"UTILITY"}""");

        var result = await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "siparis_hazir", "UTILITY", "tr", Draft(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("9001", result.Value!.Id);
        Assert.Equal("PENDING", result.Value!.Status);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.test/v25.0/WABA1/message_templates", handler.Url);

        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;
        Assert.Equal("siparis_hazir", root.GetProperty("name").GetString());
        Assert.Equal("UTILITY", root.GetProperty("category").GetString());
        Assert.Equal("tr", root.GetProperty("language").GetString());

        var comps = root.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"],
            comps.Select(c => c.GetProperty("type").GetString()));

        var examples = comps[1].GetProperty("example").GetProperty("body_text")[0]
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["Ayşe", "250"], examples);
    }

    // Değişkensiz şablona boş bir example nesnesi eklemek Meta'dan ret getiriyor.
    [Fact]
    public async Task Degiskensiz_govdede_ornek_alani_gonderilmiyor()
    {
        var handler = new CapturingHandler("""{"id":"9002","status":"PENDING"}""");

        await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "kargo", "UTILITY", "tr",
            new WhatsAppTemplateDraft(null, "Kargonuz yolda.", null, [], []),
            CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Body!);
        var body = Assert.Single(sent.RootElement.GetProperty("components").EnumerateArray().ToList());
        Assert.Equal("BODY", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("example", out _));
    }

    [Fact]
    public async Task Create_meta_hatasini_veri_olarak_donduruyor()
    {
        var handler = new CapturingHandler(
            """{"error":{"code":100,"message":"Template name already exists"}}""",
            HttpStatusCode.BadRequest);

        var result = await Catalog(handler).CreateAsync(
            "WABA1", "TOKEN", "kargo", "UTILITY", "tr", Draft(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("100", result.ErrorCode);
        Assert.Contains("already exists", result.ErrorMessage);
    }
}
```

- [ ] **Step 2: Testlerin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplateWriteTests"`
Expected: derleme hatası — `CreateAsync` yok.

- [ ] **Step 3: Arayüze ve gerçeklemeye ekle**

`WhatsAppTemplateCatalog.cs` başına `using System.Text;` ve `using System.Text.Json.Serialization;` ekle.

`WabaTemplate` kaydının altına:

```csharp
/// <summary>Oluşturma yanıtı. <paramref name="Status"/> neredeyse her zaman
/// <c>PENDING</c>; Meta bazen anında onaylıyor, o yüzden sabitlemiyoruz.</summary>
public sealed record WhatsAppTemplateCreated(string Id, string Status);
```

`IWhatsAppTemplateCatalog` içine:

```csharp
    /// <summary>Yeni şablon oluşturur. Ad/kategori/dil ayrı parametre: Meta'nın
    /// düzenleme ucu bunları değiştiremediği için taslağın parçası değiller.</summary>
    Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
        string wabaId, string businessToken, string name, string category, string language,
        WhatsAppTemplateDraft draft, CancellationToken ct);
```

`WhatsAppTemplateCatalog` sınıfına:

```csharp
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Base() => $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}";

    public async Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
        string wabaId, string businessToken, string name, string category, string language,
        WhatsAppTemplateDraft draft, CancellationToken ct)
    {
        // parameter_format BİLEREK gönderilmiyor: Meta'nın belgelenmiş varsayılanı
        // konumsal ({{1}}) ve gönderenimiz de konumsal dizi yolluyor. Alanı yazmak
        // yalnız Graph sürümüne bağımlılık eklerdi.
        var payload = new
        {
            name,
            category,
            language,
            components = BuildComponents(draft),
        };

        var sent = await SendAsync(
            HttpMethod.Post, $"{Base()}/{wabaId}/message_templates", businessToken, payload, wabaId, ct);
        if (!sent.Ok) return GraphResult<WhatsAppTemplateCreated>.Failure(sent.ErrorCode, sent.ErrorMessage);

        using var doc = sent.Value!;
        var id = Str(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return GraphResult<WhatsAppTemplateCreated>.Failure(
                "unexpected", "Meta şablon kimliği döndürmedi");
        }

        return GraphResult<WhatsAppTemplateCreated>.Success(
            new WhatsAppTemplateCreated(id!, Str(doc.RootElement, "status") ?? "PENDING"));
    }

    /// <summary>Taslak → Graph bileşen dizisi. Boş başlık/alt bilgi/buton hiç
    /// yazılmıyor: Meta boş bileşeni ret sebebi sayıyor.</summary>
    private static List<object> BuildComponents(WhatsAppTemplateDraft draft)
    {
        var comps = new List<object>();

        if (!string.IsNullOrWhiteSpace(draft.HeaderText))
            comps.Add(new { type = "HEADER", format = "TEXT", text = draft.HeaderText });

        comps.Add(draft.BodyExamples.Count == 0
            ? new { type = "BODY", text = draft.BodyText }
            : (object)new
            {
                type = "BODY",
                text = draft.BodyText,
                example = new { body_text = new[] { draft.BodyExamples.ToArray() } },
            });

        if (!string.IsNullOrWhiteSpace(draft.FooterText))
            comps.Add(new { type = "FOOTER", text = draft.FooterText });

        if (draft.Buttons.Count > 0)
        {
            var buttons = draft.Buttons.Select(b => b.Type switch
            {
                "URL" => (object)new { type = "URL", text = b.Text, url = b.Url },
                "PHONE_NUMBER" => new { type = "PHONE_NUMBER", text = b.Text, phone_number = b.PhoneNumber },
                _ => new { type = "QUICK_REPLY", text = b.Text },
            }).ToArray();

            comps.Add(new { type = "BUTTONS", buttons });
        }

        return comps;
    }

    /// <summary>Yazma çağrıları için ortak gönderim. Okuma yolundaki
    /// <see cref="ReadPageAsync"/> ile aynı hata sözleşmesi: Graph hatası
    /// istisna değil <see cref="GraphResult{T}"/> verisi.</summary>
    private async Task<GraphResult<JsonDocument>> SendAsync(
        HttpMethod method, string url, string businessToken, object? payload, string context,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        if (payload is not null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, WriteOptions), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp şablon yazma ağ hatası ({Context})", context);
            return GraphResult<JsonDocument>.Failure("network", ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            _log.LogWarning(
                "WhatsApp şablon yazma yanıtı JSON değil (HTTP {Status}): {Body}",
                (int)resp.StatusCode, Truncate(body));
            return GraphResult<JsonDocument>.Failure(
                ((int)resp.StatusCode).ToString(), "beklenmedik yanıt");
        }

        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            doc.Dispose();
            _log.LogWarning("WhatsApp şablon yazma hatası ({Code}): {Msg}", code, msg);
            return GraphResult<JsonDocument>.Failure(code, msg);
        }

        if (!resp.IsSuccessStatusCode)
        {
            doc.Dispose();
            _log.LogWarning(
                "WhatsApp şablon yazma beklenmedik yanıt (HTTP {Status}): {Body}",
                (int)resp.StatusCode, Truncate(body));
            return GraphResult<JsonDocument>.Failure(
                ((int)resp.StatusCode).ToString(), "beklenmedik yanıt");
        }

        return GraphResult<JsonDocument>.Success(doc);
    }
```

- [ ] **Step 4: `FakeTemplateCatalog`'a gerçekleme ekle**

`PanelWhatsAppApprovedTemplatesControllerTests.cs` içindeki sahteye:

```csharp
        public Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
            string wabaId, string businessToken, string name, string category, string language,
            WhatsAppTemplateDraft draft, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");
```

- [ ] **Step 5: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplateWriteTests"`
Expected: PASS (3 test)

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "feat(whatsapp): Graph üzerinde şablon oluşturmayı ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 7: Gidiş-dönüş değişmezi

Bu planın **bağlayıcı kuralı**: formun ürettiği taslak, Graph JSON'una çevrilip katalogca geri okunduğunda `UnsupportedReason` **null** vermeli. Aksi hâlde panelde oluşturulup panelde gönderilemeyen şablon doğar ve yayıncı bunu ancak ücretli bir gönderim denemesinde öğrenir.

**Files:**
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs`

- [ ] **Step 1: Testi yaz**

Aynı sınıfa ekle:

```csharp
    /// <summary>Oluşturmada gönderdiğimiz bileşenleri Meta'nın liste yanıtına
    /// koyup katalogla geri okuyoruz. Gönderilemez çıkarsa panel kendi
    /// gönderemeyeceği şablonu üretiyor demektir.</summary>
    [Theory]
    [MemberData(nameof(GonderilebilirTaslaklar))]
    public async Task Olusturulan_sablon_katalogca_gonderilebilir_okunuyor(WhatsAppTemplateDraft draft)
    {
        Assert.Null(WhatsAppTemplateShape.Validate(draft));

        var create = new CapturingHandler("""{"id":"9100","status":"PENDING"}""");
        var created = await Catalog(create).CreateAsync(
            "WABA1", "TOKEN", "gidis_donus", "UTILITY", "tr", draft, CancellationToken.None);
        Assert.True(created.Ok);

        using var sent = JsonDocument.Parse(create.Body!);
        var components = sent.RootElement.GetProperty("components").GetRawText();

        var listJson = $$"""
        {"data":[{"id":"9100","name":"gidis_donus","status":"APPROVED","category":"UTILITY",
                  "language":"tr","components":{{components}}}]}
        """;

        var read = await Catalog(new CapturingHandler(listJson))
            .ListAllAsync("WABA1", "TOKEN", CancellationToken.None);

        Assert.True(read.Ok);
        var t = Assert.Single(read.Value!);
        Assert.Null(t.UnsupportedReason);
        Assert.Equal(draft.BodyExamples.Count, t.ParameterCount);
    }

    public static TheoryData<WhatsAppTemplateDraft> GonderilebilirTaslaklar() => new()
    {
        new WhatsAppTemplateDraft(null, "Kargonuz yolda.", null, [], []),
        new WhatsAppTemplateDraft("Sipariş bilgisi", "Merhaba {{1}}.", "OrderDeck", ["Ayşe"], []),
        new WhatsAppTemplateDraft(null, "Merhaba {{1}}, {{2}} TL.", null, ["Ayşe", "250"],
        [
            new WhatsAppTemplateButton("QUICK_REPLY", "Evet", null, null),
            new WhatsAppTemplateButton("QUICK_REPLY", "Hayır", null, null),
            new WhatsAppTemplateButton("URL", "Siteye git", "https://orderdeckapp.com", null),
            new WhatsAppTemplateButton("PHONE_NUMBER", "Ara", null, "+905321234567"),
        ]),
    };
```

- [ ] **Step 2: Koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Olusturulan_sablon_katalogca"`
Expected: PASS (3 vaka). Düşerse **doğrulayıcıyı sıkılaştır**, testi gevşetme — kural bu.

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs
git commit -m "test(whatsapp): oluşturulan şablonun gönderilebilirliğini bağla

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 8: `UpdateAsync` — bileşenleri düzenle

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
    [Fact]
    public async Task Update_sablon_kimligine_yalniz_bilesenleri_gonderiyor()
    {
        var handler = new CapturingHandler("""{"success":true}""");

        var result = await Catalog(handler).UpdateAsync("9001", "TOKEN", Draft(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.test/v25.0/9001", handler.Url);

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.False(sent.RootElement.TryGetProperty("name", out _));
        Assert.False(sent.RootElement.TryGetProperty("category", out _));
        Assert.False(sent.RootElement.TryGetProperty("language", out _));
        Assert.Equal(4, sent.RootElement.GetProperty("components").GetArrayLength());
    }

    // Meta 200 + {"success":false} dönebiliyor; başarı saymak yayıncıya
    // kaydedilmemiş bir düzenlemeyi kaydedildi diye gösterirdi.
    [Fact]
    public async Task Update_success_false_hata_sayiliyor()
    {
        var result = await Catalog(new CapturingHandler("""{"success":false}"""))
            .UpdateAsync("9001", "TOKEN", Draft(), CancellationToken.None);

        Assert.False(result.Ok);
    }
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Update_sablon_kimligine"`
Expected: derleme hatası — `UpdateAsync` yok.

- [ ] **Step 3: Arayüze ve gerçeklemeye ekle**

Arayüz:

```csharp
    /// <summary>Şablonun bileşenlerini günceller. Ad/kategori/dil gönderilmiyor:
    /// onaylı şablonda Meta zaten kabul etmiyor, panel de üçünü her durumda
    /// kilitliyor.</summary>
    Task<GraphResult<bool>> UpdateAsync(
        string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct);
```

Gerçekleme:

```csharp
    public async Task<GraphResult<bool>> UpdateAsync(
        string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct)
    {
        var sent = await SendAsync(
            HttpMethod.Post, $"{Base()}/{templateId}", businessToken,
            new { components = BuildComponents(draft) }, templateId, ct);

        return ReadSuccess(sent);
    }

    /// <summary>Meta yazma yanıtı <c>{"success":true}</c>. HTTP 200 tek başına
    /// yetmiyor: <c>success:false</c> gövdesi de 200 ile geliyor.</summary>
    private static GraphResult<bool> ReadSuccess(GraphResult<JsonDocument> sent)
    {
        if (!sent.Ok) return GraphResult<bool>.Failure(sent.ErrorCode, sent.ErrorMessage);

        using var doc = sent.Value!;
        var ok = doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

        return ok
            ? GraphResult<bool>.Success(true)
            : GraphResult<bool>.Failure("unexpected", "Meta işlemi onaylamadı");
    }
```

- [ ] **Step 4: `FakeTemplateCatalog`'a gerçekleme ekle**

```csharp
        public Task<GraphResult<bool>> UpdateAsync(
            string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");
```

- [ ] **Step 5: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsAppTemplateWriteTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "feat(whatsapp): şablon bileşenlerini güncellemeyi ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 9: `DeleteAsync` — şablonu sil

Meta'nın silme ucu hem `name` hem `hsm_id` alıyor ve kaynaklar hangisinin zorunlu olduğunda ayrışıyor. **İkisini birden** yolluyoruz: yalnız `name` göndermek aynı adın tüm dil sürümlerini silerdi.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

```csharp
    [Fact]
    public async Task Delete_hem_adi_hem_kimligi_gonderiyor()
    {
        var handler = new CapturingHandler("""{"success":true}""");

        var result = await Catalog(handler)
            .DeleteAsync("WABA1", "TOKEN", "9001", "siparis_hazir", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Contains("https://graph.test/v25.0/WABA1/message_templates?", handler.Url);
        Assert.Contains("name=siparis_hazir", handler.Url);
        Assert.Contains("hsm_id=9001", handler.Url);
        Assert.Null(handler.Body);
    }
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Delete_hem_adi"`
Expected: derleme hatası — `DeleteAsync` yok.

- [ ] **Step 3: Arayüze ve gerçeklemeye ekle**

Arayüz:

```csharp
    /// <summary>Şablonu siler. <paramref name="name"/> da isteniyor: Meta'nın
    /// ucu ada göre çalışıyor, <c>hsm_id</c> tek bir dil sürümüne daraltıyor.
    /// Yalnız ad gönderseydik aynı adın bütün dilleri silinirdi.</summary>
    Task<GraphResult<bool>> DeleteAsync(
        string wabaId, string businessToken, string templateId, string name, CancellationToken ct);
```

Gerçekleme:

```csharp
    public async Task<GraphResult<bool>> DeleteAsync(
        string wabaId, string businessToken, string templateId, string name, CancellationToken ct)
    {
        var url = $"{Base()}/{wabaId}/message_templates" +
                  $"?name={Uri.EscapeDataString(name)}&hsm_id={Uri.EscapeDataString(templateId)}";

        var sent = await SendAsync(HttpMethod.Delete, url, businessToken, null, wabaId, ct);
        return ReadSuccess(sent);
    }
```

- [ ] **Step 4: `FakeTemplateCatalog`'a gerçekleme ekle**

```csharp
        public Task<GraphResult<bool>> DeleteAsync(
            string wabaId, string businessToken, string templateId, string name, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");
```

- [ ] **Step 5: Sunucu WhatsApp takımını bütün koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WhatsApp"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppTemplateCatalog.cs OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppTemplateWriteTests.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppApprovedTemplatesControllerTests.cs
git commit -m "feat(whatsapp): şablon silmeyi ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 10: Panel ucu — liste

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`PanelWhatsAppMessageTemplatesControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Models;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.Helpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public sealed class PanelWhatsAppMessageTemplatesControllerTests : IDisposable
{
    private readonly List<TemplateApiFactory> _factories = [];

    /// <summary>Katalog sahtesi: hem döndüreceği listeyi hem gördüğü yazma
    /// çağrılarını tutuyor — sahiplik kontrolünün gerçekten listeye baktığını
    /// ancak böyle kanıtlayabiliyoruz.</summary>
    private sealed class FakeCatalog : IWhatsAppTemplateCatalog
    {
        public GraphResult<IReadOnlyList<WabaTemplate>> All =
            GraphResult<IReadOnlyList<WabaTemplate>>.Success([]);

        public GraphResult<WhatsAppTemplateCreated> CreateResult =
            GraphResult<WhatsAppTemplateCreated>.Success(new WhatsAppTemplateCreated("NEW", "PENDING"));

        public GraphResult<bool> WriteResult = GraphResult<bool>.Success(true);

        public string? SeenWabaId;
        public string? SeenToken;
        public (string Name, string Category, string Language, WhatsAppTemplateDraft Draft)? Created;
        public (string TemplateId, WhatsAppTemplateDraft Draft)? Updated;
        public (string TemplateId, string Name)? Deleted;

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
            string wabaId, string businessToken, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            SeenToken = businessToken;
            return Task.FromResult(All);
        }

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
            string wabaId, string businessToken, CancellationToken ct) =>
            ListAllAsync(wabaId, businessToken, ct);

        public Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
            string wabaId, string businessToken, string name, string category, string language,
            WhatsAppTemplateDraft draft, CancellationToken ct)
        {
            Created = (name, category, language, draft);
            return Task.FromResult(CreateResult);
        }

        public Task<GraphResult<bool>> UpdateAsync(
            string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct)
        {
            Updated = (templateId, draft);
            return Task.FromResult(WriteResult);
        }

        public Task<GraphResult<bool>> DeleteAsync(
            string wabaId, string businessToken, string templateId, string name, CancellationToken ct)
        {
            Deleted = (templateId, name);
            return Task.FromResult(WriteResult);
        }
    }

    private sealed class TemplateApiFactory : ApiFactory
    {
        public FakeCatalog Catalog { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IWhatsAppTemplateCatalog>(Catalog));
        }
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, TemplateApiFactory Factory)
    {
        public FakeCatalog Catalog => Factory.Catalog;
    }

    private async Task<Seed> SeedAsync()
    {
        var factory = new TemplateApiFactory();
        _factories.Add(factory);

        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-MTPL-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, factory);
    }

    /// <summary>Lisansa bir WhatsApp hesabı bağlar. Token şifreli saklandığı için
    /// satırı elle yazmak yetmiyor; koruyucu servisten geçmesi gerekiyor.</summary>
    private static async Task ConnectWhatsAppAsync(Seed s, string wabaId = "WABA_1")
    {
        using var scope = s.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();

        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = s.LicenseId,
            WabaId = wabaId,
            PhoneNumberId = "PNID_" + Guid.NewGuid().ToString("N")[..8],
            DisplayPhoneNumber = "+90 555 111 22 33",
            AccessTokenProtected = accounts.ProtectToken("BIZ_TOKEN"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static WabaTemplate Template(
        string id = "T1", string name = "kargo", string status = "APPROVED",
        string? rejected = null) =>
        new(id, name, "tr", "UTILITY", status, null, "Kargonuz yolda.", null,
            [], 0, [], null, rejected);

    private sealed record ButtonDto(string Type, string Text, string? Url, string? PhoneNumber);

    private sealed record TemplateDto(
        string Id, string Name, string Language, string Category, string Status,
        string? RejectedReason, string? HeaderText, string BodyText, string? FooterText,
        List<ButtonDto> Buttons, List<string> BodyExamples, string? UnsupportedReason);

    [Fact]
    public async Task Liste_onay_bekleyeni_ve_reddedileni_de_donduruyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, "WABA_42");
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo"),
            Template("T2", "kampanya", "REJECTED", "INVALID_FORMAT"),
        ]);

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<TemplateDto>>();
        Assert.Equal(2, list!.Count);
        Assert.Equal("REJECTED", list[1].Status);
        Assert.Equal("INVALID_FORMAT", list[1].RejectedReason);
        Assert.Equal("WABA_42", s.Catalog.SeenWabaId);
        Assert.Equal("BIZ_TOKEN", s.Catalog.SeenToken);
    }

    [Fact]
    public async Task Whatsapp_bagli_degilse_503()
    {
        var s = await SeedAsync();

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Meta_hatasi_502_olarak_doner()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Failure("190", "Session has expired.");

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }
}
```

- [ ] **Step 2: Testlerin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelWhatsAppMessageTemplatesControllerTests"`
Expected: FAIL — 404 (rota yok).

- [ ] **Step 3: Denetleyiciyi yaz**

`PanelWhatsAppMessageTemplatesController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının WhatsApp şablonlarını panelden yönetmesi.
///
/// <para><b>Onaylı listeden neden ayrı:</b> <c>whatsapp-approved-templates</c>
/// gönderim listesi — yalnız gönderilebilir olanı döndürüyor ve sözleşmesi
/// gönderim ekranına bağlı. Burası yönetim: onay bekleyeni ve reddedileni de
/// göstermek zorunda. İkisini birleştirmek, gönderim ekranını gönderilemez
/// şablonlarla doldurmak demekti.</para>
///
/// <para><b>Sahiplik neden elle doğrulanıyor:</b> Meta'nın düzenleme ucu
/// <c>POST /{TEMPLATE_ID}</c>, silme ucu da <c>hsm_id</c> alıyor — ikisi de
/// WABA kapsamlı DEĞİL. Kimliği doğrudan geçirseydik bir yayıncı, kimliğini
/// bildiği başka bir yayıncının şablonunu düzenleyebilir ya da silebilirdi.</para>
///
/// <para><b><c>[AllowStockStaff]</c> bilerek yok:</b> şablon oluşturmak marka
/// adına mesaj yazmak demek ve reddedilen şablon WABA'nın kalite notunu
/// düşürüyor. Stok elemanı bu bölümden dışarıda kalıyor.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-message-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppMessageTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppTemplateCatalog _catalog;

    public PanelWhatsAppMessageTemplatesController(
        LicenseDbContext db, WhatsAppAccountService accounts, IWhatsAppTemplateCatalog catalog)
    {
        _db = db;
        _accounts = accounts;
        _catalog = catalog;
    }

    /// <summary>Şablonların dili sabit <c>tr</c>. Panelde dil seçtirmek, aynı adın
    /// birden çok dil sürümünü doğurup silme/düzenleme yollarını çoğaltırdı;
    /// yayıncılarımızın hepsi Türkçe yazıyor.</summary>
    private const string Language = "tr";

    public sealed record ButtonDto(string Type, string Text, string? Url, string? PhoneNumber);

    public sealed record TemplateDto(
        string Id,
        string Name,
        string Language,
        string Category,
        string Status,
        string? RejectedReason,
        string? HeaderText,
        string BodyText,
        string? FooterText,
        IReadOnlyList<ButtonDto> Buttons,
        IReadOnlyList<string> BodyExamples,
        string? UnsupportedReason);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var result = await _catalog.ListAllAsync(scope!.WabaId, scope.AccessToken, ct);
        if (!result.Ok) return GraphProblem("whatsapp-templates-read-failed", result);

        return Ok(result.Value!.Select(ToDto));
    }

    private static TemplateDto ToDto(WabaTemplate t) => new(
        t.Id, t.Name, t.Language, t.Category, t.Status, t.RejectedReason,
        t.HeaderText, t.BodyText, t.FooterText,
        t.Buttons.Select(b => new ButtonDto(b.Type, b.Text, b.Url, b.PhoneNumber)).ToList(),
        t.ParameterExamples, t.UnsupportedReason);

    private sealed record WabaScope(string WabaId, string AccessToken);

    private async Task<(WabaScope? Scope, IActionResult? Error)> ResolveScopeAsync(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return (null, Problem(title: "no-active-license", statusCode: 400));

        var waba = await _accounts.ResolveWabaContextAsync(licenseId.Value, ct);
        if (waba is null)
        {
            return (null, Problem(
                title: "no-whatsapp-account", statusCode: 503,
                detail: "Bu lisansa bağlı aktif WhatsApp hesabı yok."));
        }

        return (new WabaScope(waba.WabaId, waba.AccessToken), null);
    }

    /// <summary>Meta hatası 502 ile geçiyor: sorun bizde değil, yukarı akışta.
    /// Kodu ve metni gövdeye yazıyoruz — "bir hata oluştu" diyen bir panel,
    /// yayıncıyı bize yazmaktan başka bir yere götürmüyor.</summary>
    private IActionResult GraphProblem<T>(string title, GraphResult<T> result) =>
        Problem(
            title: title, statusCode: 502,
            detail: string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.ErrorCode ?? "bilinmeyen hata"
                : $"{result.ErrorCode}: {result.ErrorMessage}");
}
```

- [ ] **Step 4: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelWhatsAppMessageTemplatesControllerTests"`
Expected: PASS (3 test)

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs
git commit -m "feat(whatsapp): panele şablon yönetim listesi ucu ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 11: Panel ucu — oluştur

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

Test sınıfına ekle:

```csharp
    private sealed record ButtonReq(string Type, string Text, string? Url, string? PhoneNumber);

    private sealed record DraftReq(
        string? HeaderText, string BodyText, string? FooterText,
        List<string>? BodyExamples, List<ButtonReq>? Buttons);

    private sealed record CreateReq(string Name, string Category, DraftReq Draft);

    private static CreateReq NewTemplate(
        string name = "siparis_hazir", string category = "UTILITY",
        string body = "Merhaba {{1}}, siparişiniz hazır.", List<string>? examples = null) =>
        new(name, category, new DraftReq(null, body, null, examples ?? ["Ayşe"], null));

    private static Task<HttpResponseMessage> CreateAsync(Seed s, CreateReq req) =>
        s.Client.PostAsJsonAsync("/api/panel/whatsapp-message-templates", req);

    [Fact]
    public async Task Olusturma_metaya_ad_kategori_ve_dili_geciriyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var created = s.Catalog.Created!.Value;
        Assert.Equal("siparis_hazir", created.Name);
        Assert.Equal("UTILITY", created.Category);
        Assert.Equal("tr", created.Language);
        Assert.Equal(["Ayşe"], created.Draft.BodyExamples);
    }

    // Doğrulama Graph'a çıkmadan yerelde: Meta'nın 132000 hatası okunmaz ve
    // reddedilen şablon WABA'nın kalite notunu düşürüyor.
    [Theory]
    [InlineData("Sipariş Hazır", "UTILITY")]      // geçersiz ad
    [InlineData("siparis_hazir", "AUTHENTICATION")] // geçersiz kategori
    public async Task Gecersiz_ad_veya_kategori_400_ve_metaya_gitmiyor(string name, string category)
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate(name, category));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Created);
    }

    [Fact]
    public async Task Eksik_ornek_degeri_400()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate(examples: []));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Created);
    }

    [Fact]
    public async Task Ayni_ad_hatasi_metadan_502_olarak_geciyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.CreateResult = GraphResult<WhatsAppTemplateCreated>.Failure(
            "100", "Template name already exists");

        var resp = await CreateAsync(s, NewTemplate());

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("already exists", await resp.Content.ReadAsStringAsync());
    }
```

- [ ] **Step 2: Testlerin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Olusturma_metaya_ad"`
Expected: FAIL — 405 (POST eylemi yok).

- [ ] **Step 3: Eylemi yaz**

Denetleyiciye ekle:

```csharp
    public sealed record ButtonRequest(string? Type, string? Text, string? Url, string? PhoneNumber);

    /// <summary>Şablonun bileşenleri — oluşturmada ve düzenlemede ortak.
    /// Ad/kategori/dil burada YOK: Meta'nın düzenleme ucu üçünü de değiştiremiyor,
    /// panel de üçünü her durumda kilitliyor.</summary>
    public sealed record DraftRequest(
        string? HeaderText,
        string? BodyText,
        string? FooterText,
        List<string>? BodyExamples,
        List<ButtonRequest>? Buttons);

    public sealed record CreateRequest(string? Name, string? Category, DraftRequest? Draft);

    public sealed record CreatedDto(string Id, string Status);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        var category = (req.Category ?? "").Trim().ToUpperInvariant();

        var invalid = WhatsAppTemplateShape.ValidateName(name)
                      ?? WhatsAppTemplateShape.ValidateCategory(category);
        if (invalid is not null) return Invalid(invalid);

        var draft = ToDraft(req.Draft);
        var draftError = WhatsAppTemplateShape.Validate(draft);
        if (draftError is not null) return Invalid(draftError);

        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var result = await _catalog.CreateAsync(
            scope!.WabaId, scope.AccessToken, name, category, Language, draft, ct);
        if (!result.Ok) return GraphProblem("whatsapp-template-create-failed", result);

        return Ok(new CreatedDto(result.Value!.Id, result.Value!.Status));
    }

    /// <summary>Doğrulama hatası 400 + Türkçe metin. Panel bu metni olduğu gibi
    /// gösteriyor; Meta'nın kendi hata metni yayıncıya hiçbir şey anlatmıyor.</summary>
    private IActionResult Invalid(string message) =>
        Problem(title: "invalid-template", statusCode: 400, detail: message);

    private static WhatsAppTemplateDraft ToDraft(DraftRequest? r)
    {
        r ??= new DraftRequest(null, null, null, null, null);

        return new WhatsAppTemplateDraft(
            Clean(r.HeaderText),
            (r.BodyText ?? "").Trim(),
            Clean(r.FooterText),
            (r.BodyExamples ?? []).Select(e => (e ?? "").Trim()).ToList(),
            (r.Buttons ?? []).Select(b => new WhatsAppTemplateButton(
                (b.Type ?? "").Trim().ToUpperInvariant(),
                (b.Text ?? "").Trim(),
                Clean(b.Url),
                Clean(b.PhoneNumber))).ToList());
    }

    /// <summary>Boş dizgeyi null'a indiriyor: panel dokunulmamış alanı boş dizge
    /// olarak yolluyor ve boş bir HEADER bileşeni Meta'dan ret getirirdi.</summary>
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
```

- [ ] **Step 4: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelWhatsAppMessageTemplatesControllerTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs
git commit -m "feat(whatsapp): panelden şablon oluşturma ucunu ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 12: Panel ucu — düzenle ve sil (sahiplik kontrolüyle)

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

```csharp
    private static Task<HttpResponseMessage> UpdateAsync(Seed s, string id, DraftReq draft) =>
        s.Client.PostAsJsonAsync($"/api/panel/whatsapp-message-templates/{id}", draft);

    private static DraftReq EditedDraft() =>
        new(null, "Kargonuz bugün çıktı.", null, [], null);

    [Fact]
    public async Task Duzenleme_yalniz_bilesenleri_metaya_geciriyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "T1", EditedDraft());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("T1", s.Catalog.Updated!.Value.TemplateId);
        Assert.Equal("Kargonuz bugün çıktı.", s.Catalog.Updated!.Value.Draft.BodyText);
    }

    // Meta'nın düzenleme ucu WABA kapsamlı değil: kimliği doğrudan geçirseydik
    // yayıncı, kimliğini bildiği BAŞKA bir yayıncının şablonunu düzenlerdi.
    [Fact]
    public async Task Baska_wabanin_sablonu_duzenlenemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "BASKASININ", EditedDraft());

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    // Meta onay bekleyen şablonu düzenlemeye hiç izin vermiyor; isteği yollamak
    // yayıncıya anlaşılmaz bir Graph hatası gösterirdi.
    [Fact]
    public async Task Onay_bekleyen_sablon_duzenlenemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo", "PENDING"),
        ]);

        var resp = await UpdateAsync(s, "T1", EditedDraft());

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    [Fact]
    public async Task Duzenlemede_de_dogrulama_calisiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "T1", new DraftReq(null, "   ", null, [], null));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    // Silme ucu adı da istiyor; adı istekten alsaydık yayıncı bir şablonun
    // kimliğiyle başka bir şablonun adını eşleştirip yanlış satırı sildirebilirdi.
    [Fact]
    public async Task Silmede_ad_istekten_degil_listeden_aliniyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo_bildirimi"),
        ]);

        var resp = await s.Client.DeleteAsync("/api/panel/whatsapp-message-templates/T1");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("T1", "kargo_bildirimi"), s.Catalog.Deleted);
    }

    [Fact]
    public async Task Baska_wabanin_sablonu_silinemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await s.Client.DeleteAsync("/api/panel/whatsapp-message-templates/BASKASININ");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(s.Catalog.Deleted);
    }
```

- [ ] **Step 2: Testlerin düştüğünü gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Duzenleme_yalniz"`
Expected: FAIL — 404/405 (eylemler yok).

- [ ] **Step 3: Eylemleri yaz**

Denetleyiciye ekle:

```csharp
    [HttpPost("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] DraftRequest req, CancellationToken ct)
    {
        var draft = ToDraft(req);
        var draftError = WhatsAppTemplateShape.Validate(draft);
        if (draftError is not null) return Invalid(draftError);

        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var (owned, ownError) = await FindOwnedAsync(scope!, id, ct);
        if (ownError is not null) return ownError;

        if (string.Equals(owned!.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                title: "template-pending", statusCode: 409,
                detail: "Onay bekleyen şablon düzenlenemiyor; Meta sonuçlanmasını bekliyor.");
        }

        var result = await _catalog.UpdateAsync(owned.Id, scope.AccessToken, draft, ct);
        if (!result.Ok) return GraphProblem("whatsapp-template-update-failed", result);

        return Ok(new { success = true });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var (owned, ownError) = await FindOwnedAsync(scope!, id, ct);
        if (ownError is not null) return ownError;

        // Ad listeden alınıyor, istekten DEĞİL: Meta'nın silme ucu ada göre
        // çalışıyor ve uydurma bir ad başka bir şablonu sildirebilirdi.
        var result = await _catalog.DeleteAsync(
            scope.WabaId, scope.AccessToken, owned!.Id, owned.Name, ct);
        if (!result.Ok) return GraphProblem("whatsapp-template-delete-failed", result);

        return Ok(new { success = true });
    }

    /// <summary>Şablonu bu WABA'nın listesinden bulur. Hem kiracı koruması hem
    /// silmenin ihtiyaç duyduğu adın kaynağı.</summary>
    private async Task<(WabaTemplate? Template, IActionResult? Error)> FindOwnedAsync(
        WabaScope scope, string id, CancellationToken ct)
    {
        var all = await _catalog.ListAllAsync(scope.WabaId, scope.AccessToken, ct);
        if (!all.Ok) return (null, GraphProblem("whatsapp-templates-read-failed", all));

        var found = all.Value!.FirstOrDefault(t => t.Id == id);
        if (found is null)
        {
            return (null, Problem(
                title: "template-not-found", statusCode: 404,
                detail: "Şablon bu WhatsApp hesabında bulunamadı."));
        }

        return (found, null);
    }
```

- [ ] **Step 4: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelWhatsAppMessageTemplatesControllerTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppMessageTemplatesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs
git commit -m "feat(whatsapp): şablon düzenleme ve silme uçlarını ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 13: Yetki testleri

Kural sunucuda zaten var (`StockStaffScopeFilter` varsayılan olarak reddediyor, `[Authorize]` sınıf düzeyinde). Testler o kuralı **yazılı** hâle getiriyor: `[AllowStockStaff]`'ı sonradan ekleyen biri kırmızı görsün.

**Files:**
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs`

- [ ] **Step 1: Testleri yaz**

```csharp
    [Fact]
    public async Task Kimliksiz_istek_401()
    {
        var factory = new TemplateApiFactory();
        _factories.Add(factory);

        var resp = await factory.CreateClient().GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Şablon oluşturmak marka adına mesaj yazmak demek ve reddedilen şablon
    // WABA'nın kalite notunu düşürüyor — stok elemanı bu bölümde işi yok.
    [Fact]
    public async Task Stok_elemani_403()
    {
        var factory = new TemplateApiFactory();
        _factories.Add(factory);

        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(
            factory, role: "stock");

        var resp = await client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
```

- [ ] **Step 2: `CustomerAuthHelper` imzasını doğrula**

Run: `grep -n "CreateAuthenticatedClientAsync" OrderDeck.LicenseServer.Tests/Helpers/CustomerAuthHelper.cs`

Stok rolü parametresinin gerçek adı farklıysa (örn. `principal` ya da ayrı bir `CreateStockStaffClientAsync`), testi o imzaya uydur — mevcut stok testlerinden birine bak:

Run: `grep -rn "stock-staff-forbidden\|role: \"stock\"" OrderDeck.LicenseServer.Tests | head -5`

- [ ] **Step 3: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelWhatsAppMessageTemplatesControllerTests"`
Expected: PASS

- [ ] **Step 4: Sunucu takımının tamamını koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS (Docker açık olmalı — birkaç test Testcontainers ile gerçek SQL Server başlatıyor)

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppMessageTemplatesControllerTests.cs
git commit -m "test(whatsapp): şablon yönetimi yetki kurallarını bağla

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

> **Buradan sonrası ayrı repo:** `C:\Users\burak\source\repos\OrderDeck-Mobile`.
> Sunucu tarafı merge edilmeden panel PR'ı açılmamalı — panelin çağırdığı uçlar
> prod'a inmeden ekran boş 404'e düşer.
>
> Panel komutları: `cd apps/panel && npm run test` (Vitest),
> `npm run lint`, `npm run build`.

## Task 14: Panel API modülü

**Files:**
- Create: `apps/panel/src/api/whatsappMessageTemplates.ts`
- Test: `apps/panel/src/api/whatsappMessageTemplates.test.ts`

- [ ] **Step 1: Başarısız testi yaz**

`whatsappMessageTemplates.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { countBodyParams, toTemplateName } from "./whatsappMessageTemplates";

describe("toTemplateName", () => {
  // Meta şablon adında yalnız küçük harf, rakam ve alt çizgi kabul ediyor.
  // Yayıncıya bu kuralı öğretmek yerine başlığından türetiyoruz.
  it("türkçe karakterleri ascii karşılığına indiriyor", () => {
    expect(toTemplateName("Sipariş Hazır")).toBe("siparis_hazir");
    expect(toTemplateName("ÇİĞDEM ÖĞÜT")).toBe("cigdem_ogut");
  });

  it("noktalama ve fazla boşluğu temizliyor", () => {
    expect(toTemplateName("  Kargo:  yolda!  ")).toBe("kargo_yolda");
  });

  it("baştaki ve sondaki alt çizgiyi atıyor", () => {
    expect(toTemplateName("-kargo-")).toBe("kargo");
  });

  // Ad tamamen elenirse boş dönüyor; ekran o zaman yayıncıdan ad istiyor.
  it("hiç geçerli karakter yoksa boş dönüyor", () => {
    expect(toTemplateName("!!!")).toBe("");
  });
});

describe("countBodyParams", () => {
  it("değişkensiz metinde sıfır", () => {
    expect(countBodyParams("Kargonuz yolda.")).toBe(0);
  });

  it("en büyük numarayı alan sayısı sayıyor", () => {
    expect(countBodyParams("Merhaba {{1}}, {{2}} TL")).toBe(2);
  });

  // Boşluklu numaralandırmada ({{1}}, {{3}}) sunucu zaten reddediyor; burada
  // 3 alan gösterip yayıncıya sorunu görünür kılmak, 2 gösterip sessizce
  // yanlış eşleştirmekten iyi.
  it("boşluklu numarada en büyüğü esas alıyor", () => {
    expect(countBodyParams("{{1}} ve {{3}}")).toBe(3);
  });
});
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `cd apps/panel && npx vitest run src/api/whatsappMessageTemplates.test.ts`
Expected: FAIL — modül yok.

- [ ] **Step 3: Modülü yaz**

`whatsappMessageTemplates.ts`:

```ts
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiClient } from "./client";

export type TemplateButton = {
  type: "QUICK_REPLY" | "URL" | "PHONE_NUMBER";
  text: string;
  url: string | null;
  phoneNumber: string | null;
};

/**
 * Yayıncının WhatsApp şablonu — **her durumdan**.
 *
 * `useApprovedTemplates` ile karıştırılmamalı: o uç gönderim listesi, yalnız
 * onaylıyı döndürüyor. Burası yönetim ekranı; onay bekleyeni ve reddedileni
 * göstermezse yayıncı şablonunun akıbetini WhatsApp Manager'dan öğrenmek
 * zorunda kalır — bu özelliğin var olma sebebi tam olarak o.
 */
export type MessageTemplate = {
  id: string;
  name: string;
  language: string;
  category: string;
  /** APPROVED | PENDING | REJECTED | PAUSED | DISABLED */
  status: string;
  /** Meta'nın ham ret kodu (örn. INVALID_FORMAT). Çevirmiyoruz: yayıncı ancak
   *  bu dizgeyle Meta belgelerinde karşılık bulabiliyor. */
  rejectedReason: string | null;
  headerText: string | null;
  bodyText: string;
  footerText: string | null;
  buttons: TemplateButton[];
  bodyExamples: string[];
  /** Doluysa şablon panelden gönderilemez; metin sebebi anlatıyor. */
  unsupportedReason: string | null;
};

export type TemplateDraft = {
  headerText: string | null;
  bodyText: string;
  footerText: string | null;
  bodyExamples: string[];
  buttons: TemplateButton[];
};

export type CreateTemplateInput = {
  name: string;
  category: "MARKETING" | "UTILITY";
  draft: TemplateDraft;
};

const KEY = ["whatsapp-message-templates"];

/**
 * Liste saklanmıyor, her seferinde Meta'ya soruluyor: onay durumu bizde değil
 * Meta'da değişiyor ve bayat kopya, yayıncıya "onaylandı" demiş olurdu.
 */
export function useMessageTemplates() {
  return useQuery({
    queryKey: KEY,
    queryFn: async (): Promise<MessageTemplate[]> => {
      const resp = await apiClient.get<MessageTemplate[]>(
        "/api/panel/whatsapp-message-templates",
      );
      return resp.data;
    },
    staleTime: 30_000,
    retry: false,
  });
}

export function useCreateTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: CreateTemplateInput) => {
      const resp = await apiClient.post<{ id: string; status: string }>(
        "/api/panel/whatsapp-message-templates",
        input,
      );
      return resp.data;
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useUpdateTemplate(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (draft: TemplateDraft) => {
      await apiClient.post(`/api/panel/whatsapp-message-templates/${id}`, draft);
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteTemplate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/api/panel/whatsapp-message-templates/${id}`);
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

const TR_ASCII: Record<string, string> = {
  ç: "c", ğ: "g", ı: "i", ö: "o", ş: "s", ü: "u",
  Ç: "c", Ğ: "g", İ: "i", I: "i", Ö: "o", Ş: "s", Ü: "u",
};

/**
 * Başlıktan Meta'nın kabul ettiği şablon adını türetir (küçük harf, rakam,
 * alt çizgi). Yayıncıya bu kuralı ezberletmek yerine adı biz üretiyoruz;
 * ad zaten müşteriye hiç görünmüyor.
 */
export function toTemplateName(title: string): string {
  return title
    .replace(/[çğıöşüÇĞİIÖŞÜ]/g, (c) => TR_ASCII[c] ?? c)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "");
}

/** Gövdedeki en büyük `{{n}}` — kaç örnek değer alanı çizileceği. */
export function countBodyParams(bodyText: string): number {
  const nums = [...bodyText.matchAll(/\{\{(\d+)\}\}/g)].map((m) => Number(m[1]));
  return nums.length === 0 ? 0 : Math.max(...nums);
}
```

- [ ] **Step 4: Testleri koştur**

Run: `cd apps/panel && npx vitest run src/api/whatsappMessageTemplates.test.ts`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/api/whatsappMessageTemplates.ts apps/panel/src/api/whatsappMessageTemplates.test.ts
git commit -m "feat(whatsapp): şablon yönetimi api modülünü ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 15: Liste ekranı

**Files:**
- Create: `apps/panel/src/screens/WhatsAppMesajSablonlariScreen.tsx`
- Test: `apps/panel/src/screens/WhatsAppMesajSablonlariScreen.test.tsx`

- [ ] **Step 1: Başarısız testi yaz**

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MessageTemplate } from "../api/whatsappMessageTemplates";
import { WhatsAppMesajSablonlariScreen } from "./WhatsAppMesajSablonlariScreen";

const api = vi.hoisted(() => ({
  list: {
    data: [] as MessageTemplate[],
    isLoading: false,
    isError: false,
    refetch: vi.fn(),
    isRefetching: false,
  },
  remove: { mutate: vi.fn(), isPending: false },
}));
vi.mock("../api/whatsappMessageTemplates", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/whatsappMessageTemplates")>()),
  useMessageTemplates: () => api.list,
  useDeleteTemplate: () => api.remove,
}));

function template(over: Partial<MessageTemplate> = {}): MessageTemplate {
  return {
    id: "T1",
    name: "kargo_bildirimi",
    language: "tr",
    category: "UTILITY",
    status: "APPROVED",
    rejectedReason: null,
    headerText: null,
    bodyText: "Merhaba {{1}}, kargonuz yolda.",
    footerText: null,
    buttons: [],
    bodyExamples: ["Ayşe"],
    unsupportedReason: null,
    ...over,
  };
}

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <WhatsAppMesajSablonlariScreen />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  api.list = {
    data: [],
    isLoading: false,
    isError: false,
    refetch: vi.fn(),
    isRefetching: false,
  };
  api.remove = { mutate: vi.fn(), isPending: false };
});

describe("WhatsAppMesajSablonlariScreen", () => {
  it("şablon yokken boş durumu gösterir", () => {
    renderScreen();
    expect(screen.getByText(/Henüz şablon yok/i)).toBeInTheDocument();
  });

  it("şablonu metniyle listeler", () => {
    api.list.data = [template()];
    renderScreen();

    expect(screen.getByText("kargo_bildirimi")).toBeInTheDocument();
    expect(screen.getByText(/kargonuz yolda/i)).toBeInTheDocument();
    expect(screen.getByText("Onaylandı")).toBeInTheDocument();
  });

  it("onay bekleyeni ayrı rozetle gösterir", () => {
    api.list.data = [template({ status: "PENDING" })];
    renderScreen();
    expect(screen.getByText("Onay bekliyor")).toBeInTheDocument();
  });

  // Ret sebebi Meta'nın ham kodu; çevirseydik yayıncı aradığında Meta
  // belgelerinde hiçbir karşılık bulamazdı.
  it("reddedilenin ham sebebini gösterir", () => {
    api.list.data = [template({ status: "REJECTED", rejectedReason: "INVALID_FORMAT" })];
    renderScreen();

    expect(screen.getByText("Reddedildi")).toBeInTheDocument();
    expect(screen.getByText(/INVALID_FORMAT/)).toBeInTheDocument();
  });

  it("düzenleme bağlantısı şablonun rotasına gidiyor", () => {
    api.list.data = [template()];
    renderScreen();
    expect(screen.getByRole("link", { name: /Düzenle/i })).toHaveAttribute(
      "href",
      "/whatsapp-mesaj-sablonlari/T1",
    );
  });

  // Meta onay bekleyen şablonu düzenlemeye izin vermiyor; düğmeyi açık
  // bırakmak yayıncıyı kaydedilemeyecek bir forma sokardı.
  it("onay bekleyende düzenleme bağlantısı yok", () => {
    api.list.data = [template({ status: "PENDING" })];
    renderScreen();
    expect(screen.queryByRole("link", { name: /Düzenle/i })).not.toBeInTheDocument();
  });

  it("gönderilemeyen şablonun sebebini yazıyor", () => {
    api.list.data = [template({ unsupportedReason: "Şablonun başlığında görsel/belge var" })];
    renderScreen();
    expect(screen.getByText(/başlığında görsel/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `cd apps/panel && npx vitest run src/screens/WhatsAppMesajSablonlariScreen.test.tsx`
Expected: FAIL — ekran yok.

- [ ] **Step 3: Ekranı yaz**

```tsx
import { useState } from "react";
import { Link } from "react-router-dom";
import { Plus, RefreshCw, Trash2 } from "lucide-react";
import { EmptyState, ErrorView, LoadingView } from "@orderdeck/shared-ui";
import {
  type MessageTemplate,
  useDeleteTemplate,
  useMessageTemplates,
} from "../api/whatsappMessageTemplates";

const STATUS_LABEL: Record<string, string> = {
  APPROVED: "Onaylandı",
  PENDING: "Onay bekliyor",
  REJECTED: "Reddedildi",
  PAUSED: "Duraklatıldı",
  DISABLED: "Kapatıldı",
};

const STATUS_CLASS: Record<string, string> = {
  APPROVED: "bg-success/15 text-success",
  PENDING: "bg-warning/15 text-warning",
  REJECTED: "bg-danger/15 text-danger",
  PAUSED: "bg-warning/15 text-warning",
  DISABLED: "bg-danger/15 text-danger",
};

/**
 * Yayıncının WhatsApp şablonları — oluşturma, onay takibi, düzenleme, silme.
 *
 * `/whatsapp-sablonlari` ile karıştırılmamalı: o ekran WPF'in wa.me serbest
 * metin kalıplarını önizliyor ve Meta'yla ilgisi yok.
 *
 * URL: /whatsapp-mesaj-sablonlari
 */
export function WhatsAppMesajSablonlariScreen() {
  const { data = [], isLoading, isError, refetch, isRefetching } = useMessageTemplates();
  const remove = useDeleteTemplate();
  const [confirmId, setConfirmId] = useState<string | null>(null);

  return (
    <main className="px-5 pt-6 pb-28">
      <header className="mb-4">
        <Link to="/daha-fazla" className="text-xs text-text-muted hover:text-text">
          ← Geri
        </Link>
        <div className="mt-2 flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold tracking-tight">WhatsApp Mesaj Şablonları</h1>
            <p className="mt-0.5 text-[13px] text-text-muted">
              Pencere kapalıyken gönderebildiğin onaylı mesajlar
            </p>
          </div>
          <button
            onClick={() => void refetch()}
            disabled={isRefetching}
            aria-label="Yenile"
            className="flex h-[38px] w-[38px] items-center justify-center rounded-xl border border-border bg-bg-surface text-text-muted transition-colors hover:text-text disabled:opacity-50"
          >
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
          </button>
        </div>
      </header>

      <Link
        to="/whatsapp-mesaj-sablonlari/yeni"
        className="mb-4 flex items-center justify-center gap-2 rounded-xl bg-accent px-4 py-3 font-semibold text-white"
      >
        <Plus className="h-4 w-4" aria-hidden="true" />
        Yeni şablon
      </Link>

      {isLoading ? (
        <LoadingView />
      ) : isError ? (
        <ErrorView onRetry={() => void refetch()} />
      ) : data.length === 0 ? (
        <EmptyState title="Henüz şablon yok" description="Yeni şablon oluşturup Meta'nın onayına gönderebilirsin." />
      ) : (
        <ul className="space-y-3">
          {data.map((t) => (
            <TemplateRow
              key={t.id}
              template={t}
              confirming={confirmId === t.id}
              onAskDelete={() => setConfirmId(t.id)}
              onCancelDelete={() => setConfirmId(null)}
              onConfirmDelete={() => {
                remove.mutate(t.id);
                setConfirmId(null);
              }}
            />
          ))}
        </ul>
      )}
    </main>
  );
}

function TemplateRow({
  template,
  confirming,
  onAskDelete,
  onCancelDelete,
  onConfirmDelete,
}: {
  template: MessageTemplate;
  confirming: boolean;
  onAskDelete: () => void;
  onCancelDelete: () => void;
  onConfirmDelete: () => void;
}) {
  const status = template.status.toUpperCase();
  // Meta onay bekleyen şablonu düzenlemeye hiç izin vermiyor.
  const editable = status !== "PENDING";

  return (
    <li className="rounded-xl border border-bg-elevated bg-bg-surface p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate font-semibold">{template.name}</p>
          <p className="mt-0.5 text-[11px] uppercase tracking-wide text-text-muted">
            {template.category}
          </p>
        </div>
        <span
          className={`shrink-0 rounded px-2 py-0.5 text-[10px] font-medium ${
            STATUS_CLASS[status] ?? "bg-bg-elevated text-text-muted"
          }`}
        >
          {STATUS_LABEL[status] ?? template.status}
        </span>
      </div>

      <p className="mt-2 whitespace-pre-wrap text-[13px] text-text-muted">{template.bodyText}</p>

      {template.rejectedReason && (
        <p className="mt-2 rounded-lg bg-danger/10 px-3 py-2 text-[12px] text-danger">
          Meta ret sebebi: {template.rejectedReason}
        </p>
      )}

      {template.unsupportedReason && (
        <p className="mt-2 rounded-lg bg-warning/10 px-3 py-2 text-[12px] text-warning">
          {template.unsupportedReason}
        </p>
      )}

      {confirming ? (
        <div className="mt-3 rounded-lg bg-danger/10 px-3 py-2">
          <p className="text-[12px] text-danger">
            Silinsin mi? Meta aynı adı <b>30 gün</b> boyunca tekrar kullandırmıyor.
          </p>
          <div className="mt-2 flex gap-2">
            <button
              onClick={onConfirmDelete}
              className="rounded-lg bg-danger px-3 py-1.5 text-[13px] font-semibold text-white"
            >
              Sil
            </button>
            <button onClick={onCancelDelete} className="px-3 py-1.5 text-[13px] text-text-muted">
              Vazgeç
            </button>
          </div>
        </div>
      ) : (
        <div className="mt-3 flex items-center gap-3">
          {editable && (
            <Link
              to={`/whatsapp-mesaj-sablonlari/${template.id}`}
              className="text-[13px] font-medium text-accent"
            >
              Düzenle
            </Link>
          )}
          <button
            onClick={onAskDelete}
            className="flex items-center gap-1 text-[13px] text-text-muted hover:text-danger"
          >
            <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
            Sil
          </button>
        </div>
      )}
    </li>
  );
}
```

- [ ] **Step 4: Testleri koştur**

Run: `cd apps/panel && npx vitest run src/screens/WhatsAppMesajSablonlariScreen.test.tsx`
Expected: PASS (7 test)

`EmptyState` / `ErrorView` / `LoadingView` prop adları farklıysa `WhatsAppSohbetlerScreen.tsx`'teki kullanımlarına bak ve ona uydur.

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/screens/WhatsAppMesajSablonlariScreen.tsx apps/panel/src/screens/WhatsAppMesajSablonlariScreen.test.tsx
git commit -m "feat(whatsapp): şablon yönetim listesi ekranını ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 16: Oluştur/düzenle formu

**Files:**
- Create: `apps/panel/src/screens/WhatsAppMesajSablonScreen.tsx`
- Test: `apps/panel/src/screens/WhatsAppMesajSablonScreen.test.tsx`

- [ ] **Step 1: Başarısız testi yaz**

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MessageTemplate } from "../api/whatsappMessageTemplates";
import { WhatsAppMesajSablonScreen } from "./WhatsAppMesajSablonScreen";

const api = vi.hoisted(() => ({
  list: { data: [] as MessageTemplate[], isLoading: false, isError: false },
  create: { mutate: vi.fn(), isPending: false, isSuccess: false, error: null as unknown },
  update: { mutate: vi.fn(), isPending: false, isSuccess: false, error: null as unknown },
}));
vi.mock("../api/whatsappMessageTemplates", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/whatsappMessageTemplates")>()),
  useMessageTemplates: () => api.list,
  useCreateTemplate: () => api.create,
  useUpdateTemplate: () => api.update,
}));

function renderAt(path: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/whatsapp-mesaj-sablonlari/yeni" element={<WhatsAppMesajSablonScreen />} />
          <Route path="/whatsapp-mesaj-sablonlari/:id" element={<WhatsAppMesajSablonScreen />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const EXISTING: MessageTemplate = {
  id: "T1",
  name: "kargo_bildirimi",
  language: "tr",
  category: "UTILITY",
  status: "APPROVED",
  rejectedReason: null,
  headerText: "Kargo",
  bodyText: "Merhaba {{1}}, kargonuz yolda.",
  footerText: "OrderDeck",
  buttons: [],
  bodyExamples: ["Ayşe"],
  unsupportedReason: null,
};

beforeEach(() => {
  api.list = { data: [EXISTING], isLoading: false, isError: false };
  api.create = { mutate: vi.fn(), isPending: false, isSuccess: false, error: null };
  api.update = { mutate: vi.fn(), isPending: false, isSuccess: false, error: null };
});

describe("yeni şablon", () => {
  it("başlıktan meta adını türetip gösteriyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/yeni");
    fireEvent.change(screen.getByLabelText(/Şablon başlığı/i), {
      target: { value: "Sipariş Hazır" },
    });
    expect(screen.getByText("siparis_hazir")).toBeInTheDocument();
  });

  // Değişken sayısı metinden türüyor; alanları elle saydırmak yayıncıya
  // Meta'nın ret sebebini ezberletmek olurdu.
  it("metindeki değişken kadar örnek alanı çiziyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/yeni");
    fireEvent.change(screen.getByLabelText(/Mesaj metni/i), {
      target: { value: "Merhaba {{1}}, {{2}} TL" },
    });
    expect(screen.getByLabelText("{{1}} örnek değeri")).toBeInTheDocument();
    expect(screen.getByLabelText("{{2}} örnek değeri")).toBeInTheDocument();
  });

  it("önizlemede örnek değerleri yerine koyuyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/yeni");
    fireEvent.change(screen.getByLabelText(/Mesaj metni/i), {
      target: { value: "Merhaba {{1}}." },
    });
    fireEvent.change(screen.getByLabelText("{{1}} örnek değeri"), {
      target: { value: "Ayşe" },
    });
    expect(screen.getByText("Merhaba Ayşe.")).toBeInTheDocument();
  });

  it("kaydet, adı ve taslağı gönderiyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/yeni");
    fireEvent.change(screen.getByLabelText(/Şablon başlığı/i), {
      target: { value: "Sipariş Hazır" },
    });
    fireEvent.change(screen.getByLabelText(/Mesaj metni/i), {
      target: { value: "Siparişiniz hazır." },
    });
    fireEvent.click(screen.getByRole("button", { name: /Onaya gönder/i }));

    // İkinci argüman `{ onSuccess }`: yönlendirme kaydın gerçekten
    // tamamlanmasına bağlı, çağrının yapılmasına değil.
    expect(api.create.mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        name: "siparis_hazir",
        category: "UTILITY",
        draft: expect.objectContaining({ bodyText: "Siparişiniz hazır." }),
      }),
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  // Meta ücretli bir kaynağı kirletiyor: aynı adı 30 gün geri vermiyor.
  // Metinsiz gönderimi engellemek, yayıncıyı boş bir ret'ten koruyor.
  it("metin boşken kaydedilemiyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/yeni");
    fireEvent.change(screen.getByLabelText(/Şablon başlığı/i), {
      target: { value: "Sipariş" },
    });
    expect(screen.getByRole("button", { name: /Onaya gönder/i })).toBeDisabled();
  });
});

describe("şablon düzenleme", () => {
  it("mevcut değerleri dolduruyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/T1");
    expect(screen.getByLabelText(/Mesaj metni/i)).toHaveValue(
      "Merhaba {{1}}, kargonuz yolda.",
    );
    expect(screen.getByLabelText("{{1}} örnek değeri")).toHaveValue("Ayşe");
  });

  // Meta onaylı şablonun adını/kategorisini/dilini değiştirmiyor. Kilidi
  // duruma göre oynatmak yayıncı için anlaşılmaz olurdu; her durumda kilitli.
  it("ad ve kategori değiştirilemiyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/T1");
    expect(screen.queryByLabelText(/Şablon başlığı/i)).not.toBeInTheDocument();
    expect(screen.getByText("kargo_bildirimi")).toBeInTheDocument();
    expect(screen.getByText(/Ad ve kategori değiştirilemiyor/i)).toBeInTheDocument();
  });

  it("kaydet yalnız taslağı gönderiyor", () => {
    renderAt("/whatsapp-mesaj-sablonlari/T1");
    fireEvent.change(screen.getByLabelText(/Mesaj metni/i), {
      target: { value: "Merhaba {{1}}, kargonuz çıktı." },
    });
    fireEvent.click(screen.getByRole("button", { name: /Kaydet/i }));

    expect(api.update.mutate).toHaveBeenCalledWith(
      expect.objectContaining({ bodyText: "Merhaba {{1}}, kargonuz çıktı." }),
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });
});
```

- [ ] **Step 2: Testin düştüğünü gör**

Run: `cd apps/panel && npx vitest run src/screens/WhatsAppMesajSablonScreen.test.tsx`
Expected: FAIL — ekran yok.

- [ ] **Step 3: Ekranı yaz**

```tsx
import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ErrorView, LoadingView } from "@orderdeck/shared-ui";
import {
  countBodyParams,
  type MessageTemplate,
  type TemplateButton,
  type TemplateDraft,
  toTemplateName,
  useCreateTemplate,
  useMessageTemplates,
  useUpdateTemplate,
} from "../api/whatsappMessageTemplates";
import { fillTemplateBody } from "../api/whatsappApprovedTemplates";

/**
 * Şablon oluşturma ve düzenleme.
 *
 * Ad Meta'nın kabul ettiği biçime (küçük harf/rakam/alt çizgi) başlıktan
 * türetiliyor; ad müşteriye hiç görünmüyor, kuralı yayıncıya ezberletmenin
 * anlamı yok.
 *
 * URL: /whatsapp-mesaj-sablonlari/yeni | /whatsapp-mesaj-sablonlari/:id
 */
export function WhatsAppMesajSablonScreen() {
  const { id } = useParams<{ id: string }>();
  const editing = id !== undefined && id !== "yeni";
  const { data = [], isLoading, isError } = useMessageTemplates();

  if (!editing) return <TemplateForm key="new" />;
  if (isLoading) return <LoadingView />;
  if (isError) return <ErrorView />;

  const existing = data.find((t) => t.id === id);
  if (!existing) {
    return (
      <main className="px-5 pt-6">
        <p className="text-text-muted">Şablon bulunamadı.</p>
        <Link to="/whatsapp-mesaj-sablonlari" className="mt-2 block text-accent">
          ← Şablonlara dön
        </Link>
      </main>
    );
  }

  // key: şablon değişince tüm form durumu sıfırlansın — kalan alanlar
  // yayıncıya öbür şablonun metnini kaydettirirdi.
  return <TemplateForm key={existing.id} existing={existing} />;
}

function TemplateForm({ existing }: { existing?: MessageTemplate }) {
  const navigate = useNavigate();
  const create = useCreateTemplate();
  const update = useUpdateTemplate(existing?.id ?? "");

  const [title, setTitle] = useState("");
  const [category, setCategory] = useState<"MARKETING" | "UTILITY">(
    (existing?.category as "MARKETING" | "UTILITY") ?? "UTILITY",
  );
  const [headerText, setHeaderText] = useState(existing?.headerText ?? "");
  const [bodyText, setBodyText] = useState(existing?.bodyText ?? "");
  const [footerText, setFooterText] = useState(existing?.footerText ?? "");
  const [examples, setExamples] = useState<string[]>(existing?.bodyExamples ?? []);
  const [buttons, setButtons] = useState<TemplateButton[]>(existing?.buttons ?? []);

  const paramCount = useMemo(() => countBodyParams(bodyText), [bodyText]);
  const name = existing?.name ?? toTemplateName(title);

  const filled = examples.slice(0, paramCount);
  while (filled.length < paramCount) filled.push("");

  const ready =
    name.length > 0 &&
    bodyText.trim().length > 0 &&
    filled.every((e) => e.trim().length > 0);

  const pending = create.isPending || update.isPending;
  const error = (create.error ?? update.error) as { message?: string } | null;

  function draft(): TemplateDraft {
    return {
      headerText: headerText.trim() || null,
      bodyText: bodyText.trim(),
      footerText: footerText.trim() || null,
      bodyExamples: filled.map((e) => e.trim()),
      buttons,
    };
  }

  // Listeye dönüş yalnız `onSuccess`'te: `mutate` ateşle-unut olduğu için
  // hemen sonra navigate edersek Meta'nın reddi (502 gövdesindeki metin) hiç
  // görünmez, yayıncı şablonu kaydedildi sanır.
  function submit() {
    if (!ready || pending) return;
    const done = { onSuccess: () => navigate("/whatsapp-mesaj-sablonlari") };
    if (existing) {
      update.mutate(draft(), done);
    } else {
      create.mutate({ name, category, draft: draft() }, done);
    }
  }

  return (
    <main className="px-5 pt-6 pb-28">
      <header className="mb-4">
        <Link to="/whatsapp-mesaj-sablonlari" className="text-xs text-text-muted hover:text-text">
          ← Geri
        </Link>
        <h1 className="mt-2 text-2xl font-bold tracking-tight">
          {existing ? "Şablonu düzenle" : "Yeni şablon"}
        </h1>
      </header>

      {existing ? (
        <div className="mb-4 rounded-xl border border-bg-elevated bg-bg-surface p-4">
          <p className="font-semibold">{existing.name}</p>
          <p className="mt-0.5 text-[12px] text-text-muted">
            {existing.category} · {existing.language}
          </p>
          <p className="mt-2 text-[12px] text-text-muted">
            Ad ve kategori değiştirilemiyor — Meta onaylı şablonda ikisini de kilitliyor.
            Farklı bir ad gerekiyorsa yeni şablon oluştur.
          </p>
        </div>
      ) : (
        <>
          <Field label="Şablon başlığı" hint="Yalnız sana görünür; müşteri görmüyor.">
            <input
              aria-label="Şablon başlığı"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
            />
          </Field>
          {name.length > 0 && (
            <p className="-mt-2 mb-4 text-[12px] text-text-muted">
              Meta'daki adı: <span className="font-mono text-text">{name}</span>
            </p>
          )}

          <Field label="Kategori" hint="Kampanya ve duyuru MARKETING; bilgilendirme UTILITY.">
            <select
              aria-label="Kategori"
              value={category}
              onChange={(e) => setCategory(e.target.value as "MARKETING" | "UTILITY")}
              className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
            >
              <option value="UTILITY">Bilgilendirme (UTILITY)</option>
              <option value="MARKETING">Kampanya (MARKETING)</option>
            </select>
          </Field>
        </>
      )}

      <Field label="Başlık metni (isteğe bağlı)" hint="En çok 60 karakter, değişken kullanılamaz.">
        <input
          aria-label="Başlık metni"
          value={headerText}
          maxLength={60}
          onChange={(e) => setHeaderText(e.target.value)}
          className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
        />
      </Field>

      <Field
        label="Mesaj metni"
        hint="Değişken için {{1}}, {{2}} yaz. En çok 1024 karakter."
      >
        <textarea
          aria-label="Mesaj metni"
          value={bodyText}
          maxLength={1024}
          rows={5}
          onChange={(e) => setBodyText(e.target.value)}
          className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
        />
      </Field>

      {paramCount > 0 && (
        <div className="mb-4 space-y-2">
          <p className="text-[13px] font-medium">Örnek değerler</p>
          <p className="text-[12px] text-text-muted">
            Meta onay için örnek istiyor; müşteriye bunlar gitmiyor.
          </p>
          {filled.map((value, i) => (
            <input
              key={i}
              aria-label={`{{${i + 1}}} örnek değeri`}
              value={value}
              onChange={(e) => {
                const next = [...filled];
                next[i] = e.target.value;
                setExamples(next);
              }}
              className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
            />
          ))}
        </div>
      )}

      <Field label="Alt bilgi (isteğe bağlı)" hint="En çok 60 karakter.">
        <input
          aria-label="Alt bilgi"
          value={footerText}
          maxLength={60}
          onChange={(e) => setFooterText(e.target.value)}
          className="w-full rounded-xl border border-bg-elevated bg-bg-surface px-3 py-2.5 outline-none focus:border-accent"
        />
      </Field>

      <ButtonEditor buttons={buttons} onChange={setButtons} />

      <section className="mb-4 rounded-xl border border-bg-elevated bg-bg-surface p-4">
        <p className="mb-2 text-[13px] font-medium">Önizleme</p>
        {headerText.trim() && <p className="font-semibold">{headerText}</p>}
        <p className="whitespace-pre-wrap text-[14px]">{fillTemplateBody(bodyText, filled)}</p>
        {footerText.trim() && <p className="mt-1 text-[12px] text-text-muted">{footerText}</p>}
        {buttons.length > 0 && (
          <ul className="mt-2 space-y-1">
            {buttons.map((b, i) => (
              <li key={i} className="rounded-lg bg-bg-elevated px-3 py-1.5 text-center text-[13px] text-accent">
                {b.text}
              </li>
            ))}
          </ul>
        )}
      </section>

      {error && (
        <p className="mb-3 rounded-lg bg-danger/10 px-3 py-2 text-[13px] text-danger">
          {error.message ?? "Şablon kaydedilemedi."}
        </p>
      )}

      <button
        onClick={submit}
        disabled={!ready || pending}
        className="w-full rounded-xl bg-accent px-4 py-3 font-semibold text-white disabled:opacity-50"
      >
        {existing ? "Kaydet" : "Onaya gönder"}
      </button>

      {!existing && (
        <p className="mt-2 text-center text-[12px] text-text-muted">
          Meta onayı genelde birkaç dakika sürüyor; durumu listeden izleyebilirsin.
        </p>
      )}
    </main>
  );
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="mb-4">
      <p className="mb-1 text-[13px] font-medium">{label}</p>
      {children}
      {hint && <p className="mt-1 text-[12px] text-text-muted">{hint}</p>}
    </div>
  );
}

/** Buton düzenleyici. Hızlı yanıtlar bitişik durmak zorunda (Meta kuralı), o
 *  yüzden yeni hızlı yanıt kendi grubunun sonuna ekleniyor. */
function ButtonEditor({
  buttons,
  onChange,
}: {
  buttons: TemplateButton[];
  onChange: (next: TemplateButton[]) => void;
}) {
  function add(type: TemplateButton["type"]) {
    const item: TemplateButton = { type, text: "", url: null, phoneNumber: null };
    if (type !== "QUICK_REPLY") {
      onChange([...buttons, item]);
      return;
    }
    const lastQuick = buttons.map((b) => b.type).lastIndexOf("QUICK_REPLY");
    const at = lastQuick === -1 ? buttons.length : lastQuick + 1;
    onChange([...buttons.slice(0, at), item, ...buttons.slice(at)]);
  }

  function patch(index: number, changes: Partial<TemplateButton>) {
    onChange(buttons.map((b, i) => (i === index ? { ...b, ...changes } : b)));
  }

  return (
    <div className="mb-4">
      <p className="mb-1 text-[13px] font-medium">Butonlar (isteğe bağlı)</p>

      {buttons.map((b, i) => (
        <div key={i} className="mb-2 rounded-xl border border-bg-elevated bg-bg-surface p-3">
          <div className="flex items-center justify-between">
            <span className="text-[12px] text-text-muted">
              {b.type === "QUICK_REPLY" ? "Hızlı yanıt" : b.type === "URL" ? "Bağlantı" : "Arama"}
            </span>
            <button
              onClick={() => onChange(buttons.filter((_, j) => j !== i))}
              className="text-[12px] text-danger"
            >
              Kaldır
            </button>
          </div>
          <input
            aria-label={`${i + 1}. buton yazısı`}
            value={b.text}
            maxLength={25}
            onChange={(e) => patch(i, { text: e.target.value })}
            className="mt-2 w-full rounded-lg border border-bg-elevated bg-bg px-3 py-2 outline-none focus:border-accent"
          />
          {b.type === "URL" && (
            <input
              aria-label={`${i + 1}. buton adresi`}
              value={b.url ?? ""}
              onChange={(e) => patch(i, { url: e.target.value })}
              placeholder="https://"
              className="mt-2 w-full rounded-lg border border-bg-elevated bg-bg px-3 py-2 outline-none focus:border-accent"
            />
          )}
          {b.type === "PHONE_NUMBER" && (
            <input
              aria-label={`${i + 1}. buton numarası`}
              value={b.phoneNumber ?? ""}
              onChange={(e) => patch(i, { phoneNumber: e.target.value })}
              placeholder="+905321234567"
              className="mt-2 w-full rounded-lg border border-bg-elevated bg-bg px-3 py-2 outline-none focus:border-accent"
            />
          )}
        </div>
      ))}

      <div className="flex flex-wrap gap-2">
        <AddButton onClick={() => add("QUICK_REPLY")}>+ Hızlı yanıt</AddButton>
        <AddButton onClick={() => add("URL")}>+ Bağlantı</AddButton>
        <AddButton onClick={() => add("PHONE_NUMBER")}>+ Arama</AddButton>
      </div>
    </div>
  );
}

function AddButton({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className="rounded-lg border border-bg-elevated bg-bg-surface px-3 py-1.5 text-[13px] text-text-muted hover:text-text"
    >
      {children}
    </button>
  );
}
```

- [ ] **Step 4: Testleri koştur**

Run: `cd apps/panel && npx vitest run src/screens/WhatsAppMesajSablonScreen.test.tsx`
Expected: PASS (8 test)

- [ ] **Step 5: Commit**

```bash
git add apps/panel/src/screens/WhatsAppMesajSablonScreen.tsx apps/panel/src/screens/WhatsAppMesajSablonScreen.test.tsx
git commit -m "feat(whatsapp): şablon oluşturma ve düzenleme formunu ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 17: Rota ve menü bağlantısı

**Files:**
- Modify: `apps/panel/src/router.tsx`
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx`

- [ ] **Step 1: Rotaları ekle**

`router.tsx` içinde `/whatsapp-sohbetler/:conversationId` girdisinin yanına, `AuthedGuard` → `AppShell` çocukları arasına:

```tsx
      { path: "whatsapp-mesaj-sablonlari", element: <WhatsAppMesajSablonlariScreen /> },
      { path: "whatsapp-mesaj-sablonlari/yeni", element: <WhatsAppMesajSablonScreen /> },
      { path: "whatsapp-mesaj-sablonlari/:id", element: <WhatsAppMesajSablonScreen /> },
```

Dosyanın mevcut biçimi `<Route>` öğeleriyse ona uy; import satırları:

```tsx
import { WhatsAppMesajSablonlariScreen } from "./screens/WhatsAppMesajSablonlariScreen";
import { WhatsAppMesajSablonScreen } from "./screens/WhatsAppMesajSablonScreen";
```

**Sıra önemli:** `yeni` rotası `:id`'den ÖNCE gelmeli, yoksa "yeni" bir şablon kimliği sanılır.

- [ ] **Step 2: Menü satırını ekle**

`DahaFazlaScreen.tsx` içinde `<Kicker>İletişim</Kicker>` bloğunda, mevcut `/whatsapp-sablonlari` satırının **üstüne**:

```tsx
        <NavRow
          to="/whatsapp-mesaj-sablonlari"
          label="WhatsApp Mesaj Şablonları"
          description="Onaylı şablonlarını oluştur ve takip et"
        />
```

`NavRow`'un gerçek prop adları farklıysa aynı bloktaki komşu satırlara bak ve ona uydur. **`/whatsapp-sablonlari` satırı silinmiyor** — o ekran WPF'in wa.me kalıplarını gösteriyor, ayrı bir iş.

- [ ] **Step 3: Derle ve tüm testleri koştur**

Run: `cd apps/panel && npm run lint && npm run test && npm run build`
Expected: hepsi PASS

- [ ] **Step 4: Commit**

```bash
git add apps/panel/src/router.tsx apps/panel/src/screens/DahaFazlaScreen.tsx
git commit -m "feat(whatsapp): şablon yönetimi rotalarını ve menü bağlantısını ekle

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 18: Doğrulama ve PR'lar

- [ ] **Step 1: Sunucu takımının tamamı**

Run (LiveDeck): `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS (Docker açık olmalı)

- [ ] **Step 2: Panel takımının tamamı**

Run (OrderDeck-Mobile): `cd apps/panel && npm run lint && npm run test && npm run build`
Expected: PASS

- [ ] **Step 3: Sunucu PR'ını aç**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git push -u origin feat/wa-sablon-olusturma
gh pr create --title "feat(whatsapp): panelden şablon oluşturma sunucu ucu" --body "$(cat <<'EOF'
## Özet
- `WhatsAppTemplateCatalog` artık okumanın yanında oluşturma/düzenleme/silme de yapıyor
- Şablon kaydı (`WabaTemplate`) kimlik, durum ve Meta'nın ret sebebini taşıyor
- Yeni uç: `api/panel/whatsapp-message-templates` (liste/oluştur/düzenle/sil)

## Neden
Yayıncı panelden onaylı şablon gönderebiliyordu ama oluşturamıyordu; yeni şablon
için WhatsApp Manager'a gitmesi ve onay durumunu da orada takip etmesi gerekiyordu.

## Bağlayıcı değişmez
Formun ürettiği taslak, Graph JSON'una çevrilip katalogca geri okunduğunda
`UnsupportedReason` **null** olmak zorunda (`Olusturulan_sablon_katalogca_gonderilebilir_okunuyor`).
Aksi hâlde panelde oluşturulup panelde gönderilemeyen şablon doğardı.

## Kiracı koruması
Meta'nın düzenle (`POST /{TEMPLATE_ID}`) ve sil (`hsm_id`) uçları WABA kapsamlı
değil; denetleyici her yazmadan önce şablonun bu WABA'ya ait olduğunu listeden
doğruluyor. Silmede ad da listeden alınıyor, istekten değil.

## Test planı
- [ ] `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
- [ ] Merge sonrası prod'da `GET /api/panel/whatsapp-message-templates` → 401 (rota ayakta)
- [ ] Gerçek WABA'da bir şablon oluştur, listede PENDING gör, onaylanınca gönderim listesine düştüğünü doğrula

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 4: Panel PR'ını aç** (sunucu PR'ı **merge edildikten ve deploy bittikten sonra**)

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile
git push -u origin feat/wa-sablon-olusturma
gh pr create --title "feat(whatsapp): panelden şablon oluşturma ekranları" --body "$(cat <<'EOF'
## Özet
- `/whatsapp-mesaj-sablonlari` — durum rozetli liste, Meta'nın ham ret sebebi, silme onayı
- `/whatsapp-mesaj-sablonlari/yeni` ve `/:id` — canlı önizlemeli oluştur/düzenle formu
- Meta şablon adı başlıktan türetiliyor (`toTemplateName`)

## Neden
Yayıncı şablon oluşturmak ve onay durumunu görmek için WhatsApp Manager'a
gitmek zorundaydı.

## Notlar
- Rota `/whatsapp-sablonlari`'ndan **ayrı**: o ekran WPF'in wa.me serbest metin
  kalıplarını önizliyor, Meta'yla ilgisi yok. İkisi de menüde duruyor.
- Ad/kategori/dil düzenlemede her durumda kilitli — Meta onaylı şablonda üçünü
  de kabul etmiyor, kilidi duruma göre oynatmak yayıncı için anlaşılmaz olurdu.
- Onay bekleyen şablonda "Düzenle" hiç çıkmıyor (Meta izin vermiyor).

## Test planı
- [ ] `cd apps/panel && npm run lint && npm run test && npm run build`
- [ ] Yayında: yeni şablon oluştur → listede "Onay bekliyor" → onaylanınca sohbet ekranındaki şablon seçicisinde görün

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Gerçek etkiyle doğrula**

CI yeşili kanıt değil. Sunucu deploy'undan sonra:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://license.orderdeckapp.com/api/panel/whatsapp-message-templates
```

Expected: `401` (rota var, kimlik yok). `404` görülürse yeni imaj ayakta değil demektir.

Panel yayınından sonra gerçek WABA'da uçtan uca: şablon oluştur → listede `PENDING` → Meta onaylayınca sohbet ekranındaki şablon seçicisinde görünüyor mu.

---

## Riskler

- **`parameter_format` gönderilmiyor.** Meta'nın belgelenmiş varsayılanı konumsal
  (`{{1}}`) ve gönderenimiz de konumsal. Varsayılan ileride isimliye dönerse
  oluşturulan şablon gönderilemez hâle gelir; belirti, yeni şablonların listede
  `NamedParams` sebebiyle gönderilemez görünmesi olur. Gidiş-dönüş testi bunu
  yakalamaz (bizim ürettiğimiz JSON'u okuyor, Meta'nın döndürdüğünü değil) —
  ilk gerçek şablon onaylandığında listeden kontrol edilmeli.
- **Silme ucunda `name` mi `hsm_id` mi zorunlu**, kaynaklar ayrışıyor. İkisi de
  gönderiliyor; Meta yalnız birini kabul eden bir sürüme geçerse silme 400 döner
  ve panelde "silinemedi" olarak görünür (sessiz kayıp yok).
- **Saatte 100 şablon oluşturma sınırı** var. Panelde tek tek oluşturulduğu için
  gerçekçi değil; aşılırsa Meta'nın hata metni 502 gövdesinde yayıncıya görünür.
- **Silinen adın 30 gün geri gelmemesi** Meta kuralı; silme onayında yazılı.
