# WhatsApp Etiket Altyapısı — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sunucu tarafında, yayıncının kendi tanımladığı dinamik WhatsApp sohbet etiketlerini sabit olaylara (ödeme onay/ret, sipariş, kargo, müşteri belge gönderdi) otomatik yapıştıran altyapıyı kurmak.

**Architecture:** Dört yeni tablo (`WaLabel`, `WaLabelRule`, `WaConversationLabel`, `WaDekontExtraction`) + tek giriş noktası `LabelRuleApplier` servisi. Olay üreten controller/job'lar yalnız bu servisi çağırır; servis kuralı bulur, telefonu normalize eder, sohbeti bulur ve etiketi ekler. Etiketleme iş kaydından SONRA ve `try/catch` içinde çalışır — bir ödeme onayı asla etiket yüzünden geri alınmaz. WhatsApp'tan gelen PDF dekontlar mevcut `PdfDekontParser` ile ayrıştırılıp `WaDekontExtraction` satırına yazılır.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test), xUnit + FluentAssertions, PdfPig (`OrderDeck.PdfParsing`), Hangfire (mevcut inbound job).

**Kaynak spec:** `docs/superpowers/specs/2026-08-18-wa-etiket-altyapisi-design.md`

**Dal:** Task 1'e başlamadan önce `git switch -c feat/wa-etiket-altyapisi`.
Tasarım belgesi `docs/wa-etiket-altyapisi-spec` dalında duruyor; kod repo
konvansiyonu gereği `feat/...` dalına gider.

---

## Spec'ten sapmalar (kod okunarak doğrulandı)

Bu beş nokta spec yazılırken bilinmiyordu; plan koddaki gerçeğe göre yazıldı.

| # | Spec ne diyor | Kod ne diyor | Planın kararı |
|---|---|---|---|
| 1 | Telefon eşleşmesi `Services/Auth/PhoneNormalizer.cs` (`+90…`) ile | `WaConversation.CustomerPhone` rakam-only `wa_id` (`905321234567`), `WaPhone.Canonical()` bunun için var | İki adım: `PhoneNormalizer.TryNormalize` → `WaPhone.Canonical` |
| 2 | Olayın telefonu var | `Order.CustomerId` / `Shipment.CustomerId` WPF-lokal GUID hex string, telefon değil | Applier'a `TryApplyAndSaveByWpfCustomersAsync` yolu: `WpfCustomerProjection.Phone` üzerinden çöz |
| 3 | "PDF'in byte'ları o anda elimizde" | `WhatsAppMediaDownloader.FetchAsync` yalnız `WhatsAppMediaRef(ObjectKey, MimeType, SizeBytes)` dönüyor; `IWhatsAppMediaStore`'da okuma metodu yok | `WhatsAppMediaRef`'e `byte[]? Bytes` eklendi, YALNIZ `application/pdf` için doldurulur |
| 4 | Route `/api/panel/wa/labels` | Repo kuralı `api/panel/whatsapp-templates` | `api/panel/whatsapp-labels`, `…-label-rules`, `…-conversations` |
| 5 | `WaConversationLabel(ConversationId, WaLabelId, Source, CreatedAt)` + "Cascade from WaLabel" | Sohbet **ve** etiket ikisi de License'tan cascade → ara tablo birine cascade bağlanırsa SQL Server "multiple cascade paths" verir (aynı sorun `LicenseDbContext.cs:539`'da not edilmiş) | Ara tabloya denormalize `LicenseId` (aynı kalıp `WaMessage`'ta zaten var), cascade YALNIZ License'tan; `WaLabelRule` de aynı şekilde. Etiket silme temizliği controller'da açıkça |

---

## File Structure

**Yeni dosyalar (sunucu):**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Domain/WaLabel.cs` | Yayıncıya ait etiket tanımı |
| `OrderDeck.LicenseServer/Domain/WaLabelRule.cs` | Sabit olay → etiket eşlemesi |
| `OrderDeck.LicenseServer/Domain/WaConversationLabel.cs` | Sohbet ↔ etiket ara tablosu |
| `OrderDeck.LicenseServer/Domain/WaDekontExtraction.cs` | Gelen PDF dekontun ayrıştırılmış alanları |
| `OrderDeck.LicenseServer/Services/WhatsApp/WaLabelEvent.cs` | Olay enum'u + sabit renk paleti |
| `OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs` | Etiket yapıştırmanın TEK giriş noktası |
| `OrderDeck.LicenseServer/Services/WhatsApp/WaDekontExtractor.cs` | PDF byte → `WaDekontExtraction` satırı |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelLicenseScope.cs` | Üç yeni panel controller'ın ortak lisans çözümü |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelsController.cs` | Etiket CRUD |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelRulesController.cs` | Kural okuma/yazma |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppConversationsController.cs` | Sohbet listesi + etiket filtresi + elle etiket ekle/kaldır |

**Değişen dosyalar:**

| Dosya | Değişiklik |
|---|---|
| `Data/LicenseDbContext.cs` | 4 `DbSet` + `OnModelCreating` blokları |
| `Program.cs` | `LabelRuleApplier` ve `WaDekontExtractor` DI kaydı |
| `Services/WhatsApp/WhatsAppMediaDownloader.cs` | `WhatsAppMediaRef`'e `Bytes` alanı |
| `Services/WhatsApp/WhatsAppInboundJob.cs` | Belge/görsel olayı + PDF ayrıştırma çağrısı |
| `Controllers/Panel/PanelPaymentsController.cs` | Approve/Reject sonrası etiket |
| `Controllers/Licenses/LicensesSessionsSyncController.cs` | Yeni basılmış sipariş sonrası etiket |
| `Controllers/Licenses/LicensesShipmentsSyncController.cs` | Kargo durumu değişince etiket |

**Yeni testler:**

| Dosya | Kapsam |
|---|---|
| `Tests/Data/WaLabelSchemaTests.cs` | Eşlemeler, unique index, cascade davranışı |
| `Tests/Services/WhatsApp/LabelRuleApplierTests.cs` | Uygulayıcının dört giriş yolu |
| `Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs` | Webhook → etiket + PDF ayrıştırma |
| `Tests/Services/WhatsApp/WaDekontExtractorTests.cs` | Ayrıştırıcı sarmalayıcı |
| `Tests/Controllers/Panel/PanelPaymentsLabelTests.cs` | Ödeme onay/ret olayı |
| `Tests/Controllers/Licenses/ShipmentSyncLabelTests.cs` | Kargo durumu olayı |
| `Tests/Controllers/Licenses/OrderSyncLabelTests.cs` | Sipariş olayı |
| `Tests/Controllers/Panel/PanelWhatsAppLabelsControllerTests.cs` | Etiket CRUD |
| `Tests/Controllers/Panel/PanelWhatsAppLabelRulesControllerTests.cs` | Kural okuma/yazma |
| `Tests/Controllers/Panel/PanelWhatsAppConversationsControllerTests.cs` | Liste, filtre, elle etiket |

Ayrıca mevcut `Tests/Services/WhatsApp/WhatsAppInboundJobTests.cs` ve
`…/WhatsAppMediaDownloaderTests.cs` yapıcı imzası değiştiği için güncellenir.

---

### Task 1: Veri modeli — dört entity

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/WaLabel.cs`
- Create: `OrderDeck.LicenseServer/Domain/WaLabelRule.cs`
- Create: `OrderDeck.LicenseServer/Domain/WaConversationLabel.cs`
- Create: `OrderDeck.LicenseServer/Domain/WaDekontExtraction.cs`
- Create: `OrderDeck.LicenseServer/Services/WhatsApp/WaLabelEvent.cs`

- [ ] **Step 1: Olay enum'unu ve renk paletini yaz**

`OrderDeck.LicenseServer/Services/WhatsApp/WaLabelEvent.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Otomatik etiket kuralının bağlanabileceği SABİT olaylar.
///
/// <para>Etiketin kendisi dinamik (yayıncı yazar), olay listesi değil: kural
/// "şu olduğunda şu etiketi yapıştır" diyor ve "şu olduğunda" kısmını kod
/// üretiyor. Bu yüzden enum, DB'de int olarak saklanır — yeni olay eklemek
/// kod değişikliği gerektirir, kasten.</para>
///
/// <para>Değerler AÇIKÇA yazıldı: DB'de int duruyorlar, araya yeni bir üye
/// eklenmesi mevcut satırların anlamını kaydırmasın.</para>
/// </summary>
public enum WaLabelEvent
{
    /// <summary>Yayıncı dekontu onayladı (panel).</summary>
    PaymentApproved = 0,

    /// <summary>Yayıncı dekontu reddetti (panel).</summary>
    PaymentRejected = 1,

    /// <summary>WPF'ten yeni basılmış (iptal olmayan, kargo ücreti olmayan) sipariş geldi.</summary>
    OrderReceived = 2,

    /// <summary>Kargo dosyasının durumu değişti (beklet / alıcı ödemeli / kargolandı).</summary>
    ShipmentStatusChanged = 3,

    /// <summary>
    /// Müşteri WhatsApp'tan belge ya da görsel gönderdi. Tek olay: dekontu kimi
    /// PDF kimi ekran görüntüsü yolluyor ve gelenin gerçekten dekont olduğu
    /// bilinemez. Yanlış etiketin bedeli bir tık, kaçırmanın bedeli kayıp para.
    /// </summary>
    CustomerSentDocument = 4,
}

/// <summary>
/// Etiket rengi serbest metin değil: panel ve WPF aynı rengi aynı görsün diye
/// sabit palet. Değerler küçük harf hex, '#' ile.
/// </summary>
public static class WaLabelColors
{
    public static readonly string[] Palette =
    {
        "#ef4444", // kırmızı
        "#f97316", // turuncu
        "#eab308", // sarı
        "#22c55e", // yeşil
        "#14b8a6", // turkuaz
        "#3b82f6", // mavi
        "#8b5cf6", // mor
        "#6b7280", // gri
    };

    /// <summary>Büyük/küçük harf duyarsız — panel <c>#EF4444</c> gönderdiğinde
    /// reddetmek kullanıcıya hiçbir şey anlatmayan bir hata olurdu.</summary>
    public static bool IsValid(string? color) => Normalize(color) is not null;

    /// <summary>Paletteki kanonik (küçük harfli) hâli, yoksa <c>null</c>.
    /// Kaydedilen değer daima buradan geçer ki panelde renkler karşılaştırılabilsin.</summary>
    public static string? Normalize(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var lower = color.Trim().ToLowerInvariant();
        return Array.IndexOf(Palette, lower) >= 0 ? lower : null;
    }
}
```

- [ ] **Step 2: Entity'leri yaz**

`OrderDeck.LicenseServer/Domain/WaLabel.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının kendi tanımladığı WhatsApp sohbet etiketi. Meta'da sohbet
/// etiketi API'si YOK — etiket tamamen bizim tarafımızda yaşıyor.
///
/// <para>Sistem hiçbir etiketi önceden tanımlamaz: her yayıncı kendi işine
/// göre ("Dekont geldi", "Kargoya verilecek", "İnsan baksın") yazar.</para>
/// </summary>
public sealed class WaLabel
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Yayıncının yazdığı ad. (LicenseId, Name) benzersiz.</summary>
    public string Name { get; set; } = "";

    /// <summary>Sabit paletten hex renk — <c>WaLabelColors.Palette</c>.</summary>
    public string Color { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/WaLabelRule.cs`:

```csharp
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// "Şu olay olduğunda şu etiketi yapıştır" kuralı. Olay sabit
/// (<see cref="WaLabelEvent"/>), etiket dinamik.
///
/// <para>(LicenseId, EventKey) benzersiz: bir olay en fazla bir etikete
/// bağlanır. Çoklu eşleme istenirse yayıncı olayı değil etiketi çoğaltır —
/// aksi hâlde tek bir ödeme onayı sohbete üç etiket birden yapıştırırdı.</para>
/// </summary>
public sealed class WaLabelRule
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public WaLabelEvent EventKey { get; set; }

    public Guid WaLabelId { get; set; }
    public WaLabel WaLabel { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/WaConversationLabel.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Sohbete yapıştırılmış etiket. Bir sohbet BİRDEN ÇOK etiket taşıyabilir.
///
/// <para>Etiketler yalnız ELLE kaldırılır — sunucu hiçbirini otomatik
/// düşürmez. Ödeme onayı "Dekont geldi" etiketini silmez; yayıncı işi
/// bitirdiğinde kendisi kaldırır. Bunun sonucu "iş var" etiketlerinin
/// birikebilmesidir, o yüzden panelde kaldırma tek tık olmalı.</para>
/// </summary>
public sealed class WaConversationLabel
{
    public Guid Id { get; set; }

    /// <summary>
    /// Denormalize — <c>WaMessage.LicenseId</c> ile aynı gerekçe artı bir tane
    /// daha: silme yolu. Sohbet de etiket de License'tan cascade siliniyor;
    /// ara tablo ikisinden birine cascade bağlansaydı License silinirken SQL
    /// Server'a iki cascade yolu çıkardı. Cascade YALNIZ buradan (License) —
    /// diğer iki FK <c>NoAction</c>, temizlik açıkça yapılıyor.
    /// </summary>
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid ConversationId { get; set; }
    public WaConversation Conversation { get; set; } = null!;

    public Guid WaLabelId { get; set; }
    public WaLabel WaLabel { get; set; } = null!;

    /// <summary>"auto" (kural yapıştırdı) | "manual" (yayıncı yapıştırdı).</summary>
    public string Source { get; set; } = "auto";

    public DateTimeOffset CreatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/WaDekontExtraction.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WhatsApp'tan gelen PDF dekontun <c>PdfDekontParser</c> ile çıkarılmış
/// alanları. Panelde etiketin yanında gönderen/tutar/tarih/referans görünsün
/// diye tutulur — yayıncı PDF'i açmadan karar verebilsin.
///
/// <para>Bu satır bir <c>Payment</c> DEĞİL ve otomatik ödeme kaydı üretmez:
/// gelenin gerçekten dekont olduğu bilinmiyor, karar insanın.</para>
///
/// <para>Görsel dekontlar kapsam dışı (AI gerektirir, ayrı faz).</para>
/// </summary>
public sealed class WaDekontExtraction
{
    /// <summary>PK ve FK aynı: bir mesajın en fazla bir ayrıştırması olur.</summary>
    public Guid WaMessageId { get; set; }
    public WaMessage WaMessage { get; set; } = null!;

    public Guid LicenseId { get; set; }

    public string? PayerName { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? ReferansNo { get; set; }

    /// <summary>PDF'in SHA-256'sı. Bugün yalnız teşhis için tutuluyor;
    /// mükerrer dekont tespiti KAPSAM DIŞI.</summary>
    public string PdfHash { get; set; } = "";

    /// <summary>"High" | "Medium" | "Low" — <c>ParserConfidenceCalculator</c>.</summary>
    public string ParserConfidence { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 3: Derlemeyi doğrula**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: Build succeeded (entity'ler henüz `DbContext`'te değil, bu normal).

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/WaLabel.cs \
        OrderDeck.LicenseServer/Domain/WaLabelRule.cs \
        OrderDeck.LicenseServer/Domain/WaConversationLabel.cs \
        OrderDeck.LicenseServer/Domain/WaDekontExtraction.cs \
        OrderDeck.LicenseServer/Services/WhatsApp/WaLabelEvent.cs
git commit -m "feat(wa-etiket): etiket altyapısı entity'leri ve olay enum'u"
```

---

### Task 2: Şema — DbContext eşlemeleri ve göç

**Files:**
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet bloğu ~satır 50-52; `OnModelCreating` WhatsApp bloğu ~satır 622-666)
- Create (otomatik): `OrderDeck.LicenseServer/Data/Migrations/{timestamp}_AddWaLabels.cs`
- Test: `OrderDeck.LicenseServer.Tests/Data/WaLabelSchemaTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Data/WaLabelSchemaTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public sealed class WaLabelSchemaTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"walabel-schema-{Guid.NewGuid():N}").Options);

    [Fact]
    public void Model_exposes_the_four_label_tables()
    {
        using var db = NewDb();
        var model = db.Model;

        model.FindEntityType(typeof(WaLabel)).Should().NotBeNull();
        model.FindEntityType(typeof(WaLabelRule)).Should().NotBeNull();
        model.FindEntityType(typeof(WaConversationLabel)).Should().NotBeNull();
        model.FindEntityType(typeof(WaDekontExtraction)).Should().NotBeNull();
    }

    [Fact]
    public void Label_name_is_unique_per_license()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaLabel))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("LicenseId,Name");
    }

    [Fact]
    public void One_event_maps_to_at_most_one_label_per_license()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaLabelRule))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("LicenseId,EventKey");
    }

    [Fact]
    public void The_same_label_cannot_be_attached_twice_to_one_conversation()
    {
        using var db = NewDb();
        var idx = db.Model.FindEntityType(typeof(WaConversationLabel))!
            .GetIndexes()
            .Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))
            .ToList();

        idx.Should().Contain("ConversationId,WaLabelId");
    }

    /// <summary>
    /// License silinirken SQL Server'a tek bir cascade yolu görünmeli. İki yol
    /// (sohbet üzerinden + etiket üzerinden) şema oluşturmayı patlatır.
    /// </summary>
    [Fact]
    public void Join_rows_cascade_only_from_license()
    {
        using var db = NewDb();
        var fks = db.Model.FindEntityType(typeof(WaConversationLabel))!
            .GetForeignKeys()
            .ToDictionary(
                fk => fk.PrincipalEntityType.ClrType.Name,
                fk => fk.DeleteBehavior);

        fks[nameof(License)].Should().Be(DeleteBehavior.Cascade);
        fks[nameof(WaConversation)].Should().Be(DeleteBehavior.NoAction);
        fks[nameof(WaLabel)].Should().Be(DeleteBehavior.NoAction);
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WaLabelSchemaTests
```
Expected: FAIL — `Model_exposes_the_four_label_tables` üzerinde "Expected … not to be <null>" (entity'ler modele girmemiş).

- [ ] **Step 3: DbSet'leri ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — `WaSendAttempts` satırının hemen altına:

```csharp
    public DbSet<WaSendAttempt> WaSendAttempts => Set<WaSendAttempt>();
    public DbSet<WaLabel> WaLabels => Set<WaLabel>();
    public DbSet<WaLabelRule> WaLabelRules => Set<WaLabelRule>();
    public DbSet<WaConversationLabel> WaConversationLabels => Set<WaConversationLabel>();
    public DbSet<WaDekontExtraction> WaDekontExtractions => Set<WaDekontExtraction>();
```

- [ ] **Step 4: `OnModelCreating` bloklarını ekle**

Aynı dosyada, `mb.Entity<WaSendAttempt>(…)` bloğunun kapanışından (`});`) hemen sonra:

```csharp
        mb.Entity<WaLabel>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasOne(l => l.License).WithMany().HasForeignKey(l => l.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(l => l.Name).HasMaxLength(40).IsRequired();
            b.Property(l => l.Color).HasMaxLength(9).IsRequired();
            // Aynı ada iki etiket, panelde ayırt edilemez bir liste demek.
            b.HasIndex(l => new { l.LicenseId, l.Name }).IsUnique();
        });

        mb.Entity<WaLabelRule>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasOne(r => r.License).WithMany().HasForeignKey(r => r.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            // NoAction: License silinirken kural satırı zaten License cascade'i
            // ile gidiyor. Buradan da cascade verilseydi SQL Server iki yol
            // görüp şemayı reddederdi. Etiket silmede temizlik controller'da.
            b.HasOne(r => r.WaLabel).WithMany().HasForeignKey(r => r.WaLabelId)
             .OnDelete(DeleteBehavior.NoAction);
            b.Property(r => r.EventKey).HasConversion<int>();
            // Bir olay en fazla bir etiket üretir.
            b.HasIndex(r => new { r.LicenseId, r.EventKey }).IsUnique();
        });

        mb.Entity<WaConversationLabel>(b =>
        {
            b.HasKey(cl => cl.Id);
            b.HasOne(cl => cl.License).WithMany().HasForeignKey(cl => cl.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(cl => cl.Conversation).WithMany().HasForeignKey(cl => cl.ConversationId)
             .OnDelete(DeleteBehavior.NoAction);
            b.HasOne(cl => cl.WaLabel).WithMany().HasForeignKey(cl => cl.WaLabelId)
             .OnDelete(DeleteBehavior.NoAction);
            b.Property(cl => cl.Source).HasMaxLength(8).IsRequired();
            // Kural iki kez tetiklenirse (webhook tekrarı, çift onay) sohbette
            // aynı etiket iki kez görünmesin.
            b.HasIndex(cl => new { cl.ConversationId, cl.WaLabelId }).IsUnique();
            b.HasIndex(cl => new { cl.LicenseId, cl.WaLabelId });
        });

        mb.Entity<WaDekontExtraction>(b =>
        {
            // PK = FK: bir mesajın en fazla bir ayrıştırması olur.
            b.HasKey(d => d.WaMessageId);
            b.HasOne(d => d.WaMessage).WithOne().HasForeignKey<WaDekontExtraction>(d => d.WaMessageId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(d => d.PayerName).HasMaxLength(200);
            b.Property(d => d.Amount).HasPrecision(18, 2);
            b.Property(d => d.ReferansNo).HasMaxLength(100);
            b.Property(d => d.PdfHash).HasMaxLength(64).IsRequired();
            b.Property(d => d.ParserConfidence).HasMaxLength(16).IsRequired();
            b.HasIndex(d => d.LicenseId);
        });
```

- [ ] **Step 5: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WaLabelSchemaTests
```
Expected: PASS (5 test).

- [ ] **Step 6: Göçü üret**

Run (repo kökünden):
```bash
cd OrderDeck.LicenseServer
dotnet ef migrations add AddWaLabels --output-dir Data/Migrations
cd ..
```
Expected: `Data/Migrations/{timestamp}_AddWaLabels.cs` + `.Designer.cs` + güncellenmiş `LicenseDbContextModelSnapshot.cs`.

- [ ] **Step 7: Göçün YIKICI OLMADIĞINI gözle doğrula**

Üretilen `{timestamp}_AddWaLabels.cs` dosyasını aç. `Up(...)` içinde YALNIZ `CreateTable` ve `CreateIndex` çağrıları olmalı.
Beklenen: dört `CreateTable` (`WaLabels`, `WaLabelRules`, `WaConversationLabels`, `WaDekontExtractions`).
**Kırmızı bayrak:** herhangi bir `DropColumn`, `DropTable`, `AlterColumn` ya da mevcut tablolara `AddColumn`. Bunlardan biri varsa DURDUR — plan mevcut hiçbir tabloya dokunmuyor, çıkmışsa entity eşlemesinde hata var.

- [ ] **Step 8: Tüm sunucu testlerini çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS (mevcut testler dahil; şema değişikliği hiçbirini kırmamalı).

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer.Tests/Data/WaLabelSchemaTests.cs
git commit -m "feat(wa-etiket): etiket tabloları için şema ve eklemeli göç"
```

---

### Task 3: `LabelRuleApplier` — çekirdek (telefonla eşleşme)

Etiket yapıştırmanın TEK giriş noktası. Kural yoksa, telefon çözülemiyorsa ya da sohbet yoksa **sessizce çıkar** — hiçbiri hata değil.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/LabelRuleApplierTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Services/WhatsApp/LabelRuleApplierTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class LabelRuleApplierTests
{
    private static (LicenseDbContext Db, LabelRuleApplier Applier, Guid LicenseId) Build()
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"labelrule-{Guid.NewGuid():N}").Options);
        return (db, new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance), Guid.NewGuid());
    }

    private static Guid SeedLabelAndRule(
        LicenseDbContext db, Guid licenseId, WaLabelEvent ev, string name = "Dekont geldi")
    {
        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Name = name,
            Color = "#22c55e",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            EventKey = ev,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return label.Id;
    }

    private static Guid SeedConversation(
        LicenseDbContext db, Guid licenseId, string canonicalPhone = "905321234567")
    {
        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = canonicalPhone,
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);
        db.SaveChanges();
        return convo.Id;
    }

    [Theory]
    [InlineData("+905321234567")]
    [InlineData("05321234567")]
    [InlineData("905321234567")]
    [InlineData("0532 123 45 67")]
    public async Task Attaches_the_rule_label_whatever_shape_the_phone_arrives_in(string phone)
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        var conversationId = SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, phone, default);
        await db.SaveChangesAsync();

        var row = db.WaConversationLabels.Single();
        row.ConversationId.Should().Be(conversationId);
        row.WaLabelId.Should().Be(labelId);
        row.LicenseId.Should().Be(licenseId);
        row.Source.Should().Be("auto");
    }

    [Fact]
    public async Task Does_nothing_when_the_license_has_no_rule_for_the_event()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId);

        // Kural PaymentApproved için tanımlı; gelen olay PaymentRejected.
        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentRejected, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Customer_who_never_wrote_on_whatsapp_is_skipped_silently()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        // Sohbet YOK.

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_turkish_number_is_skipped_silently()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId, canonicalPhone: "14155552671");

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+14155552671", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_licenses_conversation_is_never_touched()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, Guid.NewGuid());   // BAŞKA yayıncının sohbeti, aynı numara

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeated_event_does_not_duplicate_the_label()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();
        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().ContainSingle();
    }

    /// <summary>
    /// Tek bir SaveChanges'ten önce aynı olay iki kez işlenirse (webhook
    /// paketinde iki mesaj) DB'de henüz satır YOK — kontrol yalnız sorguya
    /// dayansaydı iki satır eklenirdi ve unique index SaveChanges'i patlatırdı.
    /// </summary>
    [Fact]
    public async Task Two_events_in_the_same_unit_of_work_do_not_duplicate_the_label()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.CustomerSentDocument);
        SeedConversation(db, licenseId);

        await applier.ApplyAsync(licenseId, WaLabelEvent.CustomerSentDocument, "+905321234567", default);
        await applier.ApplyAsync(licenseId, WaLabelEvent.CustomerSentDocument, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Should().ContainSingle();
    }

    /// <summary>
    /// Yayıncı etiketi elle yapıştırmışsa kural onu "auto"ya çevirmemeli:
    /// kaynak bilgisi panelde "bunu ben mi koydum, sistem mi" sorusunun cevabı.
    /// </summary>
    [Fact]
    public async Task Existing_manual_label_is_left_alone()
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.PaymentApproved);
        var conversationId = SeedConversation(db, licenseId);
        db.WaConversationLabels.Add(new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        await applier.ApplyAsync(licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Single().Source.Should().Be("manual");
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~LabelRuleApplierTests
```
Expected: derleme hatası — `CS0246: The type or namespace name 'LabelRuleApplier' could not be found`.

- [ ] **Step 3: Servisi yaz**

`OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Otomatik etiket kurallarının TEK uygulama noktası.
///
/// <para><b>Neden tek servis:</b> olayı üreten beş ayrı yer var (ödeme onay,
/// ödeme ret, sipariş sync, kargo sync, gelen medya). Kural arama + telefon
/// normalize + sohbet bulma mantığı beş yere dağılsaydı, biri düzeltilip
/// diğeri unutulurdu.</para>
///
/// <para><b>Sessiz çıkış tasarımı:</b> kural tanımlı değilse, telefon TR
/// formatına oturmuyorsa ya da müşteri hiç WhatsApp'tan yazmamışsa yapılacak
/// bir şey yoktur. Bunların hiçbiri hata değil — istisna atmak, çağıran
/// tarafta iş akışını kesme riski demek olurdu.</para>
///
/// <para><b>Kaydetmez:</b> metotlar satırı yalnız DbContext'e ekler; commit
/// çağırana ait. Bir ödeme onayının etiket yüzünden geri alınmaması için
/// kural şu: iş kaydı önce commit edilir, etiket sonra
/// (<see cref="TryApplyAndSaveAsync"/> — Task 4).</para>
/// </summary>
public sealed class LabelRuleApplier
{
    private readonly LicenseDbContext _db;
    private readonly ILogger<LabelRuleApplier> _log;

    public LabelRuleApplier(LicenseDbContext db, ILogger<LabelRuleApplier> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Telefonla eşleştirir. <paramref name="phone"/> serbest formatta
    /// gelebilir (WPF alanı, shopper profili, panel girişi).
    /// </summary>
    public async Task ApplyAsync(
        Guid licenseId, WaLabelEvent eventKey, string? phone, CancellationToken ct)
    {
        var labelId = await FindRuleLabelIdAsync(licenseId, eventKey, ct);
        if (labelId is null) return;

        var canonical = ToConversationPhone(phone);
        if (canonical is null) return;

        var conversationId = await _db.WaConversations
            .Where(c => c.LicenseId == licenseId && c.CustomerPhone == canonical)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (conversationId is null) return;

        await StageAsync(licenseId, conversationId.Value, labelId.Value, "auto", ct);
    }

    /// <summary>
    /// Serbest formattaki numarayı <see cref="WaConversation.CustomerPhone"/>
    /// ile karşılaştırılabilir hâle getirir.
    ///
    /// <para>İKİ ADIM şart: <see cref="PhoneNormalizer"/> "0532…" / "532…" gibi
    /// yerel yazımları E.164'e ("+90532…") çeker ve TR dışını reddeder;
    /// <see cref="WaPhone.Canonical"/> ise '+' işaretini atarak Meta'nın
    /// <c>wa_id</c> formatına indirir — sohbet tablosundaki unique index bu
    /// forma dayanıyor. Tek adım yapılsaydı ya yerel yazımlar kaçardı ya da
    /// başındaki '+' yüzünden hiçbir sohbet eşleşmezdi.</para>
    /// </summary>
    public static string? ToConversationPhone(string? phone)
        => PhoneNormalizer.TryNormalize(phone, out var e164)
            ? WaPhone.Canonical(e164)
            : null;

    private Task<Guid?> FindRuleLabelIdAsync(
        Guid licenseId, WaLabelEvent eventKey, CancellationToken ct)
        => _db.WaLabelRules
            .Where(r => r.LicenseId == licenseId && r.EventKey == eventKey)
            .Select(r => (Guid?)r.WaLabelId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Etiketi sohbete ekler; zaten varsa dokunmaz.
    ///
    /// <para>Mükerrer kontrolü hem DB'ye hem değişiklik izleyicisine bakar:
    /// tek bir <c>SaveChanges</c> öncesinde aynı olay iki kez işlenirse (bir
    /// webhook paketinde iki belge) satır henüz DB'de olmaz ve yalnız sorguya
    /// güvenen bir kontrol unique index'i ihlal ederdi.</para>
    ///
    /// <para><paramref name="conversation"/> yalnız çağıran elinde <b>henüz
    /// kaydedilmemiş</b> bir sohbet varlığı tutuyorsa verilir; gezinme
    /// özelliğini doldurmak EF'e "önce sohbeti ekle" demenin tek güvenilir
    /// yoludur. Kaydedilmiş sohbetlerde <c>null</c> kalır.</para>
    /// </summary>
    private async Task StageAsync(
        Guid licenseId, Guid conversationId, Guid labelId, string source,
        CancellationToken ct, WaConversation? conversation = null)
    {
        var pending = _db.WaConversationLabels.Local
            .Any(x => x.ConversationId == conversationId && x.WaLabelId == labelId);
        if (pending) return;

        var exists = await _db.WaConversationLabels
            .AnyAsync(x => x.ConversationId == conversationId && x.WaLabelId == labelId, ct);
        if (exists) return;

        var row = new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (conversation is not null) row.Conversation = conversation;

        _db.WaConversationLabels.Add(row);
    }
}
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~LabelRuleApplierTests
```
Expected: PASS (11 test — `[Theory]` 4 varyantla).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/LabelRuleApplierTests.cs
git commit -m "feat(wa-etiket): kural uygulayıcı servis (telefonla eşleşme)"
```

---

### Task 4: `LabelRuleApplier` — diğer iki giriş yolu ve güvenli kaydetme

Üç farklı çağrı yeri üç farklı şeye sahip: panel ödemesinde **telefon**, gelen mesajda **sohbetin kendisi**, sipariş/kargo sync'inde ise **WPF müşteri GUID'i** var (telefon YOK — `Order.CustomerId` bir GUID hex string).

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/LabelRuleApplierTests.cs` (aynı sınıfa ekleme)

- [ ] **Step 1: Başarısız testleri yaz**

`LabelRuleApplierTests.cs` içindeki son `}` işaretinden ÖNCE, `Existing_manual_label_is_left_alone` testinin altına ekle:

```csharp
    [Fact]
    public async Task Applies_to_a_conversation_that_is_already_in_hand()
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.CustomerSentDocument);
        var conversationId = SeedConversation(db, licenseId);
        var conversation = db.WaConversations.Single(c => c.Id == conversationId);

        await applier.ApplyToConversationAsync(
            licenseId, WaLabelEvent.CustomerSentDocument, conversation, default);
        await db.SaveChangesAsync();

        var row = db.WaConversationLabels.Single();
        row.ConversationId.Should().Be(conversationId);
        row.WaLabelId.Should().Be(labelId);
        row.Source.Should().Be("auto");
    }

    /// <summary>
    /// Gelen mesaj işlenirken sohbet HENÜZ KAYDEDİLMEMİŞ olabilir (müşteri ilk
    /// kez yazıyor). Etiket satırı aynı <c>SaveChanges</c>'te yazılacağı için
    /// EF'in sohbeti önce eklediğini bilmesi şart — yoksa yabancı anahtar
    /// ihlali. Bu yüzden yol Guid değil, varlığın kendisini alıyor.
    /// </summary>
    [Fact]
    public async Task Applies_to_a_conversation_that_is_not_saved_yet()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.CustomerSentDocument);

        var fresh = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(fresh);

        await applier.ApplyToConversationAsync(
            licenseId, WaLabelEvent.CustomerSentDocument, fresh, default);
        await db.SaveChangesAsync();

        db.WaConversationLabels.Single().ConversationId.Should().Be(fresh.Id);
    }

    private static void SeedWpfCustomer(
        LicenseDbContext db, Guid licenseId, Guid customerId, string? phone)
    {
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = customerId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "musteri",
            Phone = phone,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Resolves_the_phone_from_the_wpf_customer_projection()
    {
        var (db, applier, licenseId) = Build();
        var labelId = SeedLabelAndRule(db, licenseId, WaLabelEvent.OrderReceived);
        var conversationId = SeedConversation(db, licenseId);
        var customerId = Guid.NewGuid();
        SeedWpfCustomer(db, licenseId, customerId, "0532 123 45 67");

        await applier.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.OrderReceived, new[] { customerId.ToString("N") }, default);

        var row = db.WaConversationLabels.Single();
        row.ConversationId.Should().Be(conversationId);
        row.WaLabelId.Should().Be(labelId);
    }

    /// <summary>
    /// Yayıncının WPF'te telefonunu girmediği müşteri — kanıt yok, atlanır.
    /// Bu meşru bir durum: sohbetten gelip form doldurmamış müşteriler.
    /// </summary>
    [Fact]
    public async Task Wpf_customer_without_a_phone_is_skipped()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.OrderReceived);
        SeedConversation(db, licenseId);
        var customerId = Guid.NewGuid();
        SeedWpfCustomer(db, licenseId, customerId, phone: null);

        await applier.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.OrderReceived, new[] { customerId.ToString("N") }, default);

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Unparsable_customer_id_does_not_throw()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.OrderReceived);
        SeedConversation(db, licenseId);

        var act = async () => await applier.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.OrderReceived, new[] { "", "not-a-guid" }, default);

        await act.Should().NotThrowAsync();
        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_labels_every_matching_customer_once()
    {
        var (db, applier, licenseId) = Build();
        SeedLabelAndRule(db, licenseId, WaLabelEvent.ShipmentStatusChanged);
        SeedConversation(db, licenseId, "905321234567");
        SeedConversation(db, licenseId, "905339876543");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SeedWpfCustomer(db, licenseId, a, "+905321234567");
        SeedWpfCustomer(db, licenseId, b, "+905339876543");

        // Aynı müşteri pakette iki kez → yine tek etiket.
        await applier.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.ShipmentStatusChanged,
            new[] { a.ToString("N"), b.ToString("N"), a.ToString("N") }, default);

        db.WaConversationLabels.Should().HaveCount(2);
    }

    /// <summary>
    /// Etiketleme iş kaydından SONRA çalışır ve onu asla geri almaz. Kural
    /// yoksa bile çağrı sessiz kalmalı — hiçbir controller bu yüzden 500
    /// dönmemeli.
    /// </summary>
    [Fact]
    public async Task Save_variant_never_throws_when_there_is_nothing_to_do()
    {
        var (db, applier, licenseId) = Build();

        var act = async () => await applier.TryApplyAndSaveAsync(
            licenseId, WaLabelEvent.PaymentApproved, "+905321234567", default);

        await act.Should().NotThrowAsync();
        db.WaConversationLabels.Should().BeEmpty();
    }
```

Dosyanın başındaki `using` bloğuna ekleme gerekmiyor — `WpfCustomerProjection` zaten `OrderDeck.LicenseServer.Domain` altında ve o using mevcut.

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~LabelRuleApplierTests
```
Expected: derleme hatası — `CS1061: 'LabelRuleApplier' does not contain a definition for 'ApplyToConversationAsync'`.

- [ ] **Step 3: Üç metodu ekle**

`LabelRuleApplier.cs` içinde, `ApplyAsync`'in hemen altına:

```csharp
    /// <summary>
    /// Sohbet zaten elimizdeyken kullanılır (gelen mesaj işleme). Telefon
    /// çözmeye gerek yok — bu yol eşleştirmenin en güvenilir hâli.
    ///
    /// <para>Parametre Guid değil VARLIĞIN KENDİSİ: müşteri ilk kez yazdığında
    /// sohbet henüz kaydedilmemiş olur ve etiket satırı aynı
    /// <c>SaveChanges</c>'te yazılır. Gezinme özelliğini doldurmak, EF'e
    /// "önce sohbeti ekle" demenin tek güvenilir yolu.</para>
    /// </summary>
    public async Task ApplyToConversationAsync(
        Guid licenseId, WaLabelEvent eventKey, WaConversation conversation, CancellationToken ct)
    {
        var labelId = await FindRuleLabelIdAsync(licenseId, eventKey, ct);
        if (labelId is null) return;

        await StageAsync(licenseId, conversation.Id, labelId.Value, "auto", ct, conversation);
    }

    /// <summary>
    /// İş kaydı ZATEN commit edilmiş çağrı yerleri için: etiketi ekler,
    /// kaydeder ve her türlü hatayı yutup loglar.
    ///
    /// <para>Yutmak bilinçli: bir dekont onayı, etiket yazılamadı diye
    /// başarısız sayılamaz. Etiketin kaybı bir tıkla telafi edilir, onayın
    /// kaybı müşteriyi bekletir.</para>
    /// </summary>
    public async Task TryApplyAndSaveAsync(
        Guid licenseId, WaLabelEvent eventKey, string? phone, CancellationToken ct)
    {
        try
        {
            await ApplyAsync(licenseId, eventKey, phone, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Otomatik etiket uygulanamadı: lisans {LicenseId}, olay {Event}",
                licenseId, eventKey);
        }
    }

    /// <summary>
    /// Sipariş/kargo sync'i için toplu yol. Bu olaylarda telefon YOK: elimizde
    /// yalnız <c>Order.CustomerId</c> / <c>Shipment.CustomerId</c> var ve bu
    /// alan WPF'in lokal müşteri GUID'inin hex yazımı. Telefona ancak
    /// <see cref="WpfCustomerProjection"/> üzerinden ulaşılır.
    ///
    /// <para>Toplu çalışır çünkü iki sync uç noktası da paket hâlinde (≤200)
    /// geliyor; satır başına sorgu açmak yayın sırasında gereksiz yük olurdu.</para>
    /// </summary>
    public async Task TryApplyAndSaveByWpfCustomersAsync(
        Guid licenseId,
        WaLabelEvent eventKey,
        IReadOnlyCollection<string> wpfCustomerIds,
        CancellationToken ct)
    {
        try
        {
            if (wpfCustomerIds.Count == 0) return;

            var labelId = await FindRuleLabelIdAsync(licenseId, eventKey, ct);
            if (labelId is null) return;

            var ids = wpfCustomerIds
                .Select(raw => Guid.TryParse(raw, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return;

            var phones = await _db.WpfCustomerProjections
                .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id) && p.Phone != null)
                .Select(p => p.Phone!)
                .ToListAsync(ct);

            var canonical = phones
                .Select(ToConversationPhone)
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct()
                .ToList();
            if (canonical.Count == 0) return;

            var conversationIds = await _db.WaConversations
                .Where(c => c.LicenseId == licenseId && canonical.Contains(c.CustomerPhone))
                .Select(c => c.Id)
                .ToListAsync(ct);

            foreach (var conversationId in conversationIds)
                await StageAsync(licenseId, conversationId, labelId.Value, "auto", ct);

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Otomatik etiket (WPF müşteri yolu) uygulanamadı: lisans {LicenseId}, olay {Event}",
                licenseId, eventKey);
        }
    }
```

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~LabelRuleApplierTests
```
Expected: PASS (17 test).

- [ ] **Step 5: DI kaydını ekle**

`OrderDeck.LicenseServer/Program.cs` — mevcut `WhatsAppInboundJob` kaydının hemen altına (~satır 150):

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WhatsAppInboundJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.LabelRuleApplier>();
```

- [ ] **Step 6: Tüm sunucu testlerini çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/LabelRuleApplier.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/LabelRuleApplierTests.cs
git commit -m "feat(wa-etiket): sohbet ve WPF müşteri giriş yolları + güvenli kaydetme"
```

---

### Task 5: Ödeme onay/ret olaylarını bağla

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelPaymentsLabelTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelPaymentsLabelTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelPaymentsLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelPaymentsLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(HttpClient Client, Guid LicenseId, Guid PaymentId, Guid ConversationId, Guid LabelId);

    private async Task<Seeded> SeedAsync(WaLabelEvent eventKey)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-WAL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Ayşe K.",
            Phone = "+905321234567",
            PasswordHash = "x",
            Address = "adres",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = shopper.Id,
            PayerName = "Ayşe K.",
            Amount = 1450m,
            PaidAt = DateTimeOffset.UtcNow,
            ReferansNo = "4471X",
            Status = PaymentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payments.Add(payment);

        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);

        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Name = eventKey == WaLabelEvent.PaymentApproved ? "Ödeme onaylandı" : "Ödeme reddedildi",
            Color = "#22c55e",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = eventKey,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, payment.Id, convo.Id, label.Id);
    }

    private async Task<List<WaConversationLabel>> LabelsAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync();
    }

    [Fact]
    public async Task Approving_a_payment_labels_the_customers_conversation()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentApproved);

        var resp = await s.Client.PostAsync($"/api/panel/payments/{s.PaymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var labels = await LabelsAsync(s.ConversationId);
        labels.Should().ContainSingle().Which.WaLabelId.Should().Be(s.LabelId);
    }

    [Fact]
    public async Task Rejecting_a_payment_labels_the_customers_conversation()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentRejected);

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/payments/{s.PaymentId}/reject", new { reason = "Tutar eksik" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var labels = await LabelsAsync(s.ConversationId);
        labels.Should().ContainSingle().Which.WaLabelId.Should().Be(s.LabelId);
    }

    /// <summary>
    /// Etiketleme onayı GERİ ALMAZ. Kural tanımlı değilken de onay geçmeli;
    /// bu test, etiket yolunun iş akışına bağlanmadığının kanıtı.
    /// </summary>
    [Fact]
    public async Task Approval_still_succeeds_when_no_rule_is_defined()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentRejected);   // kural RET için, onay için değil

        var resp = await s.Client.PostAsync($"/api/panel/payments/{s.PaymentId}/approve", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LabelsAsync(s.ConversationId)).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelPaymentsLabelTests
```
Expected: `Approving_a_payment_labels_the_customers_conversation` FAIL — "Expected labels to contain a single item, but the collection is empty". (Üçüncü test zaten geçer, o bir regresyon kalkanı.)

- [ ] **Step 3: Controller'ı bağla**

`PanelPaymentsController.cs` — using bloğuna ekle:

```csharp
using OrderDeck.LicenseServer.Services.WhatsApp;
```

Alanı ve kurucuyu genişlet:

```csharp
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly LabelRuleApplier _labels;
    private readonly ILogger<PanelPaymentsController> _log;

    public PanelPaymentsController(
        LicenseDbContext db,
        INotificationSender push,
        LabelRuleApplier labels,
        ILogger<PanelPaymentsController> log)
    {
        _db = db;
        _push = push;
        _labels = labels;
        _log = log;
    }
```

`Approve` içinde, `await _db.SaveChangesAsync(ct);` satırının HEMEN ALTINA:

```csharp
        await ApplyConversationLabelAsync(payment, WaLabelEvent.PaymentApproved, ct);
```

`Reject` içinde, aynı şekilde `await _db.SaveChangesAsync(ct);` satırının hemen altına:

```csharp
        await ApplyConversationLabelAsync(payment, WaLabelEvent.PaymentRejected, ct);
```

Ve `NotifyShopperPaymentDecisionAsync` metodunun hemen üstüne yeni yardımcıyı ekle:

```csharp
    /// <summary>
    /// Karar sonrası müşterinin WhatsApp sohbetine otomatik etiket.
    ///
    /// <para>Ödeme SaveChanges'inden SONRA çağrılır: etiket yazılamasa bile
    /// onay kaydı yerinde kalır. Telefon kaynağı <c>Shopper.Phone</c> —
    /// ShopperId null ise (eski WhatsApp akışından gelen dekont) elimizde
    /// telefon yoktur, sessizce atlanır.</para>
    /// </summary>
    private async Task ApplyConversationLabelAsync(
        Payment payment, WaLabelEvent eventKey, CancellationToken ct)
    {
        if (payment.ShopperId is null) return;

        var phone = await _db.Shoppers
            .Where(s => s.Id == payment.ShopperId.Value)
            .Select(s => s.Phone)
            .FirstOrDefaultAsync(ct);

        await _labels.TryApplyAndSaveAsync(payment.LicenseId, eventKey, phone, ct);
    }
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelPaymentsLabelTests
```
Expected: PASS (3 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelPaymentsLabelTests.cs
git commit -m "feat(wa-etiket): ödeme onay/ret olaylarını etiket kuralına bağla"
```

---

### Task 6: Kargo durumu değişikliği olayını bağla

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesShipmentsSyncController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/ShipmentSyncLabelTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/ShipmentSyncLabelTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class ShipmentSyncLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShipmentSyncLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(
        HttpClient Client, Guid LicenseId, Guid ConversationId, Guid LabelId, Guid WpfCustomerId);

    private async Task<Seeded> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-SHPL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = license.Id,
            Platform = "youtube",
            Username = "ayse",
            Phone = "+905321234567",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);

        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Name = "Kargo durumu değişti",
            Color = "#3b82f6",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = WaLabelEvent.ShipmentStatusChanged,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, convo.Id, label.Id, wpfCustomerId);
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    private static object Body(Guid shipmentId, Guid wpfCustomerId, string status) => new
    {
        shipments = new[]
        {
            new
            {
                id = shipmentId,
                customerId = wpfCustomerId.ToString("N"),
                status,
                cumulativeAmount = 250m,
                createdAt = DateTimeOffset.UtcNow,
                heldAt = (DateTimeOffset?)null,
                shippedAt = (DateTimeOffset?)null,
            }
        }
    };

    [Fact]
    public async Task Status_change_labels_the_conversation()
    {
        var s = await SeedAsync();
        var shipmentId = Guid.NewGuid();

        // İlk sync: Pending — henüz karar yok, etiket beklenmiyor.
        var first = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "pending"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LabelCountAsync(s.ConversationId)).Should().Be(0);

        // İkinci sync: durum değişti → etiket.
        var second = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "shipped"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }

    /// <summary>
    /// WPF outbox aynı satırı değişmeden tekrar gönderebilir. Durum aynıysa
    /// olay yoktur — yoksa her sync turu sohbeti yeniden etiketlemeye çalışırdı.
    /// </summary>
    [Fact]
    public async Task Resending_the_same_status_is_not_an_event()
    {
        var s = await SeedAsync();
        var shipmentId = Guid.NewGuid();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "held"));
        var countAfterFirst = await LabelCountAsync(s.ConversationId);

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "held"));

        countAfterFirst.Should().Be(1);
        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~ShipmentSyncLabelTests
```
Expected: `Status_change_labels_the_conversation` FAIL — "Expected … to be 1, but found 0".

- [ ] **Step 3: Controller'ı bağla**

`LicensesShipmentsSyncController.cs` — using bloğuna ekle:

```csharp
using OrderDeck.LicenseServer.Services.WhatsApp;
```

Alanı ve kurucuyu genişlet:

```csharp
    private readonly LicenseDbContext _db;
    private readonly LabelRuleApplier _labels;

    public LicensesShipmentsSyncController(LicenseDbContext db, LabelRuleApplier labels)
    {
        _db = db;
        _labels = labels;
    }
```

`Sync` içinde, `var now = DateTimeOffset.UtcNow;` satırından sonra toplayıcıyı ekle:

```csharp
        // Durumu GERÇEKTEN değişen kargoların müşterileri. WPF outbox aynı
        // satırı değişmeden tekrar gönderebiliyor; "her sync = olay" deseydik
        // sohbet her turda yeniden etiketlenmeye çalışılırdı.
        var statusChangedCustomers = new List<string>();
```

Upsert döngüsündeki iki dalı şöyle güncelle:

```csharp
            if (existing.TryGetValue(item.Id, out var current))
            {
                if (current.Status != status && !string.IsNullOrWhiteSpace(item.CustomerId))
                    statusChangedCustomers.Add(item.CustomerId);

                // WPF authoritative — tüm mutable alanları update
                current.CustomerId = item.CustomerId;
                current.Status = status;
                current.CumulativeAmount = item.CumulativeAmount;
                current.HeldAt = item.HeldAt;
                current.ShippedAt = item.ShippedAt;
                current.UpdatedAt = now;
                // CreatedAt değişmez (WPF tarafında oluşturulduğu an)
            }
            else
            {
                // Yeni dosya "Pending" ile açılıyor — bu bir karar değil, yalnız
                // kaydın doğuşu. Yayıncı beklet/alıcı ödemeli/kargolandı dediyse
                // ilk sync'te bile olay sayılır.
                if (status != ShipmentStatus.Pending && !string.IsNullOrWhiteSpace(item.CustomerId))
                    statusChangedCustomers.Add(item.CustomerId);

                _db.Shipments.Add(new Shipment
                {
```

`await _db.SaveChangesAsync(ct);` satırının HEMEN ALTINA:

```csharp
        await _labels.TryApplyAndSaveByWpfCustomersAsync(
            licenseId, WaLabelEvent.ShipmentStatusChanged, statusChangedCustomers, ct);
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~ShipmentSyncLabelTests
```
Expected: PASS (2 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesShipmentsSyncController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/ShipmentSyncLabelTests.cs
git commit -m "feat(wa-etiket): kargo durumu değişikliğini etiket kuralına bağla"
```

---

### Task 7: Sipariş olayını bağla

Controller zaten "yeni + basılmış + iptal değil + kargo ücreti değil + geçici yedek değil" siparişleri `newOrdersForShopperPush` listesinde topluyor. Etiket olayı **aynı** tanımı kullanır — ikinci bir "gerçek satış" tanımı üretmiyoruz.

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderSyncLabelTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderSyncLabelTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class OrderSyncLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OrderSyncLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(HttpClient Client, Guid LicenseId, Guid ConversationId, Guid WpfCustomerId);

    private async Task<Seeded> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-ORDL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = license.Id,
            Platform = "youtube",
            Username = "ayse",
            Phone = "+905321234567",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);

        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Name = "Yeni sipariş",
            Color = "#eab308",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = WaLabelEvent.OrderReceived,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, convo.Id, wpfCustomerId);
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    private static object Body(Guid wpfCustomerId, DateTimeOffset? printedAt, bool isShippingFee = false) => new
    {
        catalogAware = false,
        orders = new[]
        {
            new
            {
                id = Guid.NewGuid(),
                sessionId = (Guid?)null,
                customerId = wpfCustomerId.ToString("N"),
                platform = "youtube",
                username = "ayse",
                displayName = "Ayşe K.",
                messageText = "A12",
                code = "A12",
                price = 250m,
                addedAt = DateTimeOffset.UtcNow,
                printedAt,
                cancelledAt = (DateTimeOffset?)null,
                cancelReason = (string?)null,
                isShippingFee,
                isBackupPromoted = false,
                isTentativeBackup = false,
                productId = (Guid?)null,
                productVariantId = (Guid?)null,
            }
        }
    };

    [Fact]
    public async Task A_new_printed_order_labels_the_conversation()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: DateTimeOffset.UtcNow));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }

    /// <summary>
    /// Kargo ücreti satırı sipariş değildir — push bildirimi de onu saymıyor.
    /// Etiket ikinci bir "gerçek satış" tanımı üretmemeli.
    /// </summary>
    [Fact]
    public async Task A_shipping_fee_row_is_not_an_order()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: DateTimeOffset.UtcNow, isShippingFee: true));

        (await LabelCountAsync(s.ConversationId)).Should().Be(0);
    }

    [Fact]
    public async Task An_unprinted_order_is_not_an_event_yet()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: null));

        (await LabelCountAsync(s.ConversationId)).Should().Be(0);
    }
}
```

> **Not:** Sipariş sync rotası `POST /api/v1/licenses/{licenseId}/orders/sync`. Uygulamadan önce `LicensesSessionsSyncController` üzerindeki `[HttpPost("…")]` özniteliğinden doğrula; farklıysa testteki üç URL'i de düzelt.

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~OrderSyncLabelTests
```
Expected: `A_new_printed_order_labels_the_conversation` FAIL — "Expected … to be 1, but found 0".

- [ ] **Step 3: Controller'ı bağla**

`LicensesSessionsSyncController.cs` — using bloğuna ekle:

```csharp
using OrderDeck.LicenseServer.Services.WhatsApp;
```

Alanı ve kurucuyu genişlet:

```csharp
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly Services.Stock.StockLedgerWriter _ledger;
    private readonly LabelRuleApplier _labels;
    private readonly ILogger<LicensesSessionsSyncController> _log;

    public LicensesSessionsSyncController(
        LicenseDbContext db,
        INotificationSender push,
        Services.Stock.StockLedgerWriter ledger,
        LabelRuleApplier labels,
        ILogger<LicensesSessionsSyncController> log)
    {
        _db = db;
        _push = push;
        _ledger = ledger;
        _labels = labels;
        _log = log;
    }
```

`await _db.SaveChangesAsync(ct);` satırından sonra, `if (newPrintedOrders.Count > 0)` bloğunun HEMEN ÜSTÜNE:

```csharp
        // Etiket olayı, push bildiriminin kullandığı listeyi paylaşıyor:
        // "yeni + basılmış + iptal değil + kargo ücreti değil + geçici yedek
        // değil". İkinci bir gerçek-satış tanımı yazmıyoruz ki ikisi zamanla
        // ayrışmasın.
        await _labels.TryApplyAndSaveByWpfCustomersAsync(
            licenseId,
            WaLabelEvent.OrderReceived,
            newOrdersForShopperPush.Select(o => o.CustomerIdHex).ToList(),
            ct);
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~OrderSyncLabelTests
```
Expected: PASS (3 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderSyncLabelTests.cs
git commit -m "feat(wa-etiket): yeni sipariş olayını etiket kuralına bağla"
```

---

### Task 8: Müşteri belge/görsel gönderdi olayı (webhook)

Beşinci ve son olay. Diğer dördünden farkı: tetikleyici bir panel/senkron
isteği değil, gelen webhook'un kendisi. Bu yüzden burada `TryApplyAndSave*`
kullanılmaz — `WhatsAppInboundJob.ProcessAsync` zaten sonunda tek bir
`SaveChangesAsync` çağırıyor ve etiket satırı o kayda katılır.

**Neden `document` VE `image` tek olay:** dekontu kimi PDF kimi ekran
görüntüsü olarak yolluyor; gelenin gerçekten dekont olduğu bilinemez. Yanlış
etiketin bedeli bir tık, kaçırmanın bedeli kayıp para.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppInboundJob.cs:23-38` (alanlar + yapıcı), `:94-102` (echo dışı blok)
- Modify: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundJobTests.cs:37` (Build yardımcısı)
- Modify: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppMediaDownloaderTests.cs:119-120` (Build yardımcısı)
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

Create `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppInboundLabelTests
{
    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId, Guid LabelId) Build()
    {
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wainlbl-{Guid.NewGuid():N}").Options);

        var accounts = new WhatsAppAccountService(
            db, new EphemeralDataProtectionProvider(), Options.Create(new WhatsAppOptions()));

        var licenseId = Guid.NewGuid();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = "PNID_1",
            DisplayPhoneNumber = "+905550000000",
            AccessTokenProtected = accounts.ProtectToken("t"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });

        var labelId = Guid.NewGuid();
        db.WaLabels.Add(new WaLabel
        {
            Id = labelId,
            LicenseId = licenseId,
            Name = "Dekont geldi",
            Color = "#eab308",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            EventKey = WaLabelEvent.CustomerSentDocument,
            WaLabelId = labelId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance));

        return (db, job, licenseId, labelId);
    }

    /// <summary>Tek medya mesajı içeren webhook gövdesi.</summary>
    private static string MediaPayload(string wamId, string type, string from = "905321234567")
        => $$"""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "{{from}}" }],
            "messages": [{ "from": "{{from}}", "id": "{{wamId}}", "timestamp": "1753440000",
                           "type": "{{type}}",
                           "{{type}}": { "id": "MEDIA_1", "mime_type": "application/pdf" } }]
          }}]}]
        }
        """;

    [Theory]
    [InlineData("document")]
    [InlineData("image")]
    public async Task Document_and_image_both_raise_the_label(string type)
    {
        var (db, job, _, labelId) = Build();

        await job.ProcessAsync(MediaPayload("wamid.1", type));

        var link = await db.WaConversationLabels.SingleAsync();
        link.WaLabelId.Should().Be(labelId);
        link.Source.Should().Be("auto");
    }

    [Fact]
    public async Task Text_message_does_not_raise_the_label()
    {
        var (db, job, _, _) = Build();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "905321234567" }],
            "messages": [{ "from": "905321234567", "id": "wamid.t", "timestamp": "1753440000",
                           "type": "text", "text": { "body": "merhaba" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_documents_in_one_batch_produce_one_link()
    {
        var (db, job, _, _) = Build();

        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "905321234567" }],
            "messages": [
              { "from": "905321234567", "id": "wamid.a", "timestamp": "1753440000",
                "type": "document", "document": { "id": "M1", "mime_type": "application/pdf" } },
              { "from": "905321234567", "id": "wamid.b", "timestamp": "1753440001",
                "type": "document", "document": { "id": "M2", "mime_type": "application/pdf" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().ContainSingle();
    }

    [Fact]
    public async Task Label_is_written_in_the_same_save_as_a_brand_new_conversation()
    {
        var (db, job, _, _) = Build();

        // Bu numaradan daha önce hiç mesaj yok → sohbet aynı SaveChanges'te oluşur.
        db.WaConversations.Should().BeEmpty();

        await job.ProcessAsync(MediaPayload("wamid.new", "document", from: "905339998877"));

        var convo = await db.WaConversations.SingleAsync();
        var link = await db.WaConversationLabels.SingleAsync();
        link.ConversationId.Should().Be(convo.Id);
    }

    [Fact]
    public async Task Echo_of_our_own_document_does_not_raise_the_label()
    {
        var (db, job, _, _) = Build();

        // "context" alanı ayrıştırıcının bizim gönderdiğimiz mesajı tanıma yolu.
        await job.ProcessAsync("""
        {
          "entry": [{ "changes": [{ "field": "messages", "value": {
            "metadata": { "phone_number_id": "PNID_1" },
            "contacts": [{ "profile": { "name": "Ayşe" }, "wa_id": "905321234567" }],
            "messages": [{ "from": "905550000000", "id": "wamid.echo", "timestamp": "1753440000",
                           "type": "document",
                           "document": { "id": "M9", "mime_type": "application/pdf" },
                           "context": { "from": "905550000000" } }]
          }}]}]
        }
        """);

        db.WaConversationLabels.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_rule_no_label_is_written()
    {
        var (db, job, _, _) = Build();
        db.WaLabelRules.RemoveRange(db.WaLabelRules);
        db.SaveChanges();

        await job.ProcessAsync(MediaPayload("wamid.norule", "document"));

        db.WaConversationLabels.Should().BeEmpty();
    }
}
```

> **Echo testine dikkat:** `IsEcho` kararını `WhatsAppWebhookParser` veriyor.
> Yukarıdaki gövde mevcut ayrıştırıcının echo saydığı şekle göre yazıldı;
> test kırmızı kalırsa ayrıştırıcıyı **değiştirme**, `WhatsAppInboundJobTests`
> içindeki mevcut echo testinin gövdesini kopyalayıp medya tipine çevir.

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WhatsAppInboundLabelTests
```
Expected: FAIL — derleme hatası: `WhatsAppInboundJob` yapıcısı bu argümanları almıyor.

- [ ] **Step 3: `WhatsAppInboundJob`'a uygulayıcıyı ekle**

`WhatsAppInboundJob.cs` — alanlar + yapıcı (mevcut 23-38 satırlarının yerine):

```csharp
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly ILogger<WhatsAppInboundJob> _log;
    private readonly LabelRuleApplier _labels;
    private readonly WhatsAppMediaDownloader? _media;

    public WhatsAppInboundJob(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        ILogger<WhatsAppInboundJob> log,
        LabelRuleApplier labels,
        WhatsAppMediaDownloader? media = null)
    {
        _db = db;
        _accounts = accounts;
        _log = log;
        _labels = labels;
        _media = media;
    }
```

> **Neden zorunlu, `media` gibi opsiyonel değil:** medya indirici olmadan iş
> bozulmadan çalışıyor (mesaj metadata'sıyla kaydediliyor); uygulayıcı olmadan
> ise olay sessizce kaybolur. Sessiz kayıp, açık derleme hatasından beterdir.

`ProcessMessagesAsync` içindeki echo-dışı blok (mevcut 94-102 satırları) şu
hâle gelir:

```csharp
            if (!m.IsEcho)
            {
                // Pencereyi YALNIZ müşteriden gelen mesaj açar.
                if (convo.LastInboundAt is null || m.Timestamp > convo.LastInboundAt)
                    convo.LastInboundAt = m.Timestamp;
                convo.UnreadCount++;
                // Operatör kapatmış olsa bile yeni mesaj sohbeti geri açar.
                convo.Status = "open";

                // Dekont olabilecek her şey tek olay: gelenin gerçekten dekont
                // olduğu bilinemez, yanlış etiketin bedeli bir tık.
                if (m.Type is "document" or "image")
                {
                    await _labels.ApplyToConversationAsync(
                        account.LicenseId, WaLabelEvent.CustomerSentDocument, convo, ct);
                }
            }
```

> `ApplyToConversationAsync` çağrılır, `ApplyAsync` değil: sohbet varlığı
> zaten elimizde ve **henüz kaydedilmemiş olabilir**. Telefonla arayan yol
> onu bulamazdı.

- [ ] **Step 4: İki mevcut test yardımcısını genişlet**

`WhatsAppInboundJobTests.cs` — 37. satırın yerine:

```csharp
        return (db, new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance)), licenseId);
```

`WhatsAppMediaDownloaderTests.cs` — 119-120. satırların yerine:

```csharp
        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance), downloader);
```

- [ ] **Step 5: Testlerin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~WhatsAppInbound|FullyQualifiedName~WhatsAppMediaDownloader"
```
Expected: PASS — yeni dosyada 7 test (`[Theory]` 2 varyantla) + iki mevcut
sınıfın tamamı.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppInboundJob.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundJobTests.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppMediaDownloaderTests.cs
git commit -m "feat(wa-etiket): gelen belge/görselde dekont etiketi yapıştır"
```

---

### Task 9: PDF baytlarını elde tut + `WaDekontExtractor`

Tasarımdaki "PDF dekontlar mevcut `PdfDekontParser`'dan geçirilecek" kapsamı.
İki parça: (a) indirilen baytlara erişim, (b) ayrıştırıcı sarmalayıcı.

**Sapma #3'ün gerekçesi:** `WhatsAppMediaDownloader.FetchAsync` bugün yalnız
`(ObjectKey, MimeType, SizeBytes)` döndürüyor ve `IWhatsAppMediaStore`'da
geri-okuma yok. R2'ye yeni bir `GetAsync` eklemek yerine baytları zaten
bellekte olduğu anda taşımak hem daha az kod hem daha az ağ turu.
**Yalnız `application/pdf` için doldurulur** — belge limiti 100 MB, her türü
tutmak belleği patlatır.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppMediaDownloader.cs:16` (record), `:107-113` (başarı dönüşü)
- Create: `OrderDeck.LicenseServer/Services/WhatsApp/WaDekontExtractor.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WaDekontExtractorTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

Create `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WaDekontExtractorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.PdfParsing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WaDekontExtractorTests
{
    /// <summary>Gerçek PdfPig'e girmeden ayrıştırıcı sözleşmesini taklit eder.</summary>
    private sealed class FakeParser : IPdfDekontParser
    {
        private readonly PdfDekontParser.ParseResult? _result;
        private readonly Exception? _throw;

        public FakeParser(PdfDekontParser.ParseResult result) => _result = result;
        public FakeParser(Exception ex) => _throw = ex;

        public int Calls { get; private set; }

        public PdfDekontParser.ParseResult Parse(byte[] pdfBytes)
        {
            Calls++;
            if (_throw is not null) throw _throw;
            return _result!;
        }
    }

    private static PdfDekontParser.ParseResult FullResult() => new(
        PayerName: "AYŞE YILMAZ",
        Amount: 1250.50m,
        PaidAt: new DateTime(2026, 8, 18, 14, 30, 0),
        ReferansNo: "REF123456",
        PdfHash: "abc123",
        RawText: "ham metin",
        RecipientIban: "TR330006100519786457841326",
        RecipientName: "EMAR GLOBAL");

    private static WaDekontExtractor Build(IPdfDekontParser parser)
        => new(parser, NullLogger<WaDekontExtractor>.Instance);

    [Fact]
    public void Extracts_all_four_fields_from_a_readable_dekont()
    {
        var licenseId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var row = Build(new FakeParser(FullResult()))
            .TryExtract(licenseId, messageId, [1, 2, 3]);

        row.Should().NotBeNull();
        row!.LicenseId.Should().Be(licenseId);
        row.WaMessageId.Should().Be(messageId);
        row.PayerName.Should().Be("AYŞE YILMAZ");
        row.Amount.Should().Be(1250.50m);
        row.ReferansNo.Should().Be("REF123456");
        row.PdfHash.Should().Be("abc123");
    }

    [Fact]
    public void Dekont_date_is_read_as_Turkish_local_time()
    {
        var row = Build(new FakeParser(FullResult()))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);

        // Dekontta yazan saat yerel saattir; Türkiye 2016'dan beri sabit UTC+3.
        row!.PaidAt.Should().Be(new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Confidence_is_computed_from_the_parse_result()
    {
        var full = Build(new FakeParser(FullResult()))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);
        full!.ParserConfidence.Should().Be("High");

        var empty = new PdfDekontParser.ParseResult(
            PayerName: null, Amount: null, PaidAt: null, ReferansNo: null,
            PdfHash: "h", RawText: "", RecipientIban: null, RecipientName: null);

        var low = Build(new FakeParser(empty))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);
        low!.ParserConfidence.Should().Be("Low");
    }

    [Fact]
    public void A_broken_pdf_returns_null_instead_of_throwing()
    {
        var row = Build(new FakeParser(new InvalidOperationException("bozuk PDF")))
            .TryExtract(Guid.NewGuid(), Guid.NewGuid(), [1]);

        row.Should().BeNull();
    }

    [Fact]
    public void Empty_bytes_never_reach_the_parser()
    {
        var parser = new FakeParser(FullResult());

        Build(parser).TryExtract(Guid.NewGuid(), Guid.NewGuid(), []).Should().BeNull();
        Build(parser).TryExtract(Guid.NewGuid(), Guid.NewGuid(), null).Should().BeNull();

        parser.Calls.Should().Be(0);
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WaDekontExtractorTests
```
Expected: FAIL — `WaDekontExtractor` tipi bulunamıyor.

- [ ] **Step 3: `WaDekontExtractor`'ı yaz**

Create `OrderDeck.LicenseServer/Services/WhatsApp/WaDekontExtractor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.PdfParsing;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp'tan gelen PDF dekontu mevcut <see cref="IPdfDekontParser"/>'dan
/// geçirip panelde etiketin yanında gösterilecek satırı üretir.
///
/// <para><b>Neden hiç fırlatmıyor:</b> çağıran <c>WhatsAppInboundJob</c> —
/// bir Hangfire job'ı. Bozuk/şifreli/taranmış bir PDF exception atarsa job
/// retry'a girer ve <i>mesajın kendisi</i> tekrar tekrar işlenir. Ayrıştırma
/// ikincil veri: etiket zaten yapıştı, operatör sohbeti açıp PDF'i kendi
/// okuyabilir.</para>
///
/// <para><b>Kapsam dışı:</b> görsel dekontlar (AI gerektirir, ayrı faz) ve
/// mükerrer dekont tespiti. <c>PdfHash</c> bugün yalnız teşhis için saklanır.</para>
/// </summary>
public sealed class WaDekontExtractor
{
    /// <summary>Türkiye 2016'dan beri kalıcı UTC+3, yaz saati uygulaması yok —
    /// dolayısıyla dekonttaki yerel saati sabit offset ile çevirmek güvenli.</summary>
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    private readonly IPdfDekontParser _parser;
    private readonly ILogger<WaDekontExtractor> _log;

    public WaDekontExtractor(IPdfDekontParser parser, ILogger<WaDekontExtractor> log)
    {
        _parser = parser;
        _log = log;
    }

    /// <summary>Ayrıştırılamayan her durumda <c>null</c> döner.</summary>
    public WaDekontExtraction? TryExtract(Guid licenseId, Guid waMessageId, byte[]? pdfBytes)
    {
        if (pdfBytes is null || pdfBytes.Length == 0) return null;

        PdfDekontParser.ParseResult result;
        try
        {
            result = _parser.Parse(pdfBytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "WhatsApp dekontu ayrıştırılamadı: mesaj {MessageId}", waMessageId);
            return null;
        }

        return new WaDekontExtraction
        {
            WaMessageId = waMessageId,
            LicenseId = licenseId,
            PayerName = result.PayerName,
            Amount = result.Amount,
            PaidAt = ToTurkeyOffset(result.PaidAt),
            ReferansNo = result.ReferansNo,
            PdfHash = result.PdfHash,
            ParserConfidence = ParserConfidenceCalculator.Compute(result),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static DateTimeOffset? ToTurkeyOffset(DateTime? value)
        => value is null
            ? null
            : new DateTimeOffset(
                DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), TurkeyOffset);
}
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WaDekontExtractorTests
```
Expected: PASS (5 test).

- [ ] **Step 5: PDF baytlarını `WhatsAppMediaRef`'e taşı**

`WhatsAppMediaDownloader.cs` — 16. satırdaki record ve üstündeki özet:

```csharp
/// <summary>Medyanın kalıcı hale getirilmiş hali. <see cref="ObjectKey"/> null ise
/// bayt indirilmedi (boyut limiti aşıldı ya da indirme başarısız) — metadata yine
/// de saklanır ki operatör mesajın bir görsel/belge olduğunu görsün.
///
/// <para><see cref="Bytes"/> YALNIZ <c>application/pdf</c> için doldurulur:
/// dekont ayrıştırıcısı baytları hemen istiyor ve depoda geri-okuma yok.
/// Her türü taşımak 100 MB'lık belge limitiyle belleği patlatırdı.</para></summary>
public sealed record WhatsAppMediaRef(
    string? ObjectKey, string? MimeType, long? SizeBytes, byte[]? Bytes = null);
```

Aynı dosyada başarı dönüşü (mevcut 107-113 satırları):

```csharp
        _log.LogInformation(
            "WhatsApp media saklandı: {MediaId} → {Key} ({Size} bayt)", mediaId, key, bytes.LongLength);

        var isPdf = string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase);
        return new WhatsAppMediaRef(key, mime, bytes.LongLength, isPdf ? bytes : null);
```

> Diğer üç `return new WhatsAppMediaRef(null, mime, size)` satırına **dokunma**:
> baytlar zaten yok, opsiyonel parametre null kalır.

- [ ] **Step 6: Mevcut medya testlerinin bozulmadığını doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WhatsAppMediaDownloaderTests
```
Expected: PASS — record'a opsiyonel parametre eklemek mevcut çağrıları bozmaz.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WaDekontExtractor.cs \
        OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppMediaDownloader.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WaDekontExtractorTests.cs
git commit -m "feat(wa-etiket): WhatsApp PDF dekontu için ayrıştırıcı sarmalayıcı"
```

---

### Task 10: Ayrıştırıcıyı webhook akışına bağla

Task 8 etiketi yapıştırdı, Task 9 ayrıştırıcıyı yazdı; burada ikisi birleşiyor.
Ayrıştırma satırı `WaMessage` ile **aynı `SaveChanges`'te** yazılır (PK=FK
ilişkisi var, gezinme özelliği doldurulur).

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppInboundJob.cs` (yapıcı + `ProcessMessagesAsync`)
- Modify: `OrderDeck.LicenseServer/Program.cs:150` civarı (DI)
- Modify: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs` (Build yardımcısı)
- Modify: `OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundJobTests.cs:37`, `.../WhatsAppMediaDownloaderTests.cs:119-121`

- [ ] **Step 1: Başarısız testi yaz**

`WhatsAppInboundLabelTests.cs` içine — önce `Build` yardımcısını genişlet
(dönüş demetine sahte ayrıştırıcı eklenir):

```csharp
    /// <summary>Testin kontrol edebildiği sahte PDF ayrıştırıcısı.</summary>
    private sealed class StubParser : IPdfDekontParser
    {
        public int Calls { get; private set; }

        public PdfDekontParser.ParseResult Parse(byte[] pdfBytes)
        {
            Calls++;
            return new PdfDekontParser.ParseResult(
                PayerName: "AYŞE YILMAZ",
                Amount: 1250.50m,
                PaidAt: new DateTime(2026, 8, 18, 14, 30, 0),
                ReferansNo: "REF123456",
                PdfHash: "abc123",
                RawText: "ham metin",
                RecipientIban: "TR330006100519786457841326",
                RecipientName: "EMAR GLOBAL");
        }
    }
```

`Build` imzası ve son satırları şu hâle gelir:

```csharp
    private static (LicenseDbContext Db, WhatsAppInboundJob Job, Guid LicenseId, Guid LabelId, StubParser Parser)
        Build(WhatsAppMediaDownloader? media = null)
    {
        // ... gövdenin geri kalanı Task 8'deki gibi, değişmez ...

        var parser = new StubParser();
        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(parser, NullLogger<WaDekontExtractor>.Instance),
            media);

        return (db, job, licenseId, labelId, parser);
    }
```

> Task 8'deki beş testin destructuring satırlarına da bir alan eklenir:
> `var (db, job, _, labelId) = Build();` → `var (db, job, _, labelId, _) = Build();`
> (son testte `var (db, job, _, _, _) = Build();`).

Yeni testler — aynı dosyanın sonuna:

```csharp
    [Fact]
    public async Task Pdf_dekont_is_parsed_and_stored_next_to_the_message()
    {
        var (db, job, licenseId, _, parser) = Build(FakeMedia.ReturningPdf([9, 9, 9]));

        await job.ProcessAsync(MediaPayload("wamid.pdf", "document"));

        parser.Calls.Should().Be(1);

        var msg = await db.WaMessages.SingleAsync();
        var row = await db.WaDekontExtractions.SingleAsync();
        row.WaMessageId.Should().Be(msg.Id);
        row.LicenseId.Should().Be(licenseId);
        row.PayerName.Should().Be("AYŞE YILMAZ");
        row.Amount.Should().Be(1250.50m);
        row.ParserConfidence.Should().Be("High");
    }

    [Fact]
    public async Task Image_dekont_is_labeled_but_not_parsed()
    {
        // Görsel dekont AI gerektirir — ayrı faz. Etiket yine de yapışır.
        var (db, job, _, _, parser) = Build(FakeMedia.ReturningImage());

        await job.ProcessAsync(MediaPayload("wamid.img", "image"));

        parser.Calls.Should().Be(0);
        db.WaConversationLabels.Should().ContainSingle();
        db.WaDekontExtractions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_without_pdf_bytes_is_still_saved_and_labeled()
    {
        // Medya indirici kayıtlı değil ya da belge PDF değil → bayt yok.
        // Mesaj yine kaydedilmeli, etiket yine yapışmalı; yalnız özet çıkmaz.
        var (db, job, _, _, parser) = Build();

        await job.ProcessAsync(MediaPayload("wamid.nomedia", "document"));

        parser.Calls.Should().Be(0);
        db.WaMessages.Should().ContainSingle();
        db.WaConversationLabels.Should().ContainSingle();
        db.WaDekontExtractions.Should().BeEmpty();
    }
```

Ve testin ihtiyaç duyduğu sahte indirici — aynı dosyada:

```csharp
    /// <summary>Graph'a çıkmadan sabit bir <see cref="WhatsAppMediaRef"/> döndüren
    /// indirici. <c>WhatsAppMediaDownloader</c> mühürlü olmadığı için
    /// <c>FetchAsync</c> sanal yapılamaz; bunun yerine HTTP katmanını taklit
    /// etmek yerine job'a hazır bir alt sınıf veriyoruz.</summary>
    private static class FakeMedia
    {
        public static WhatsAppMediaDownloader ReturningPdf(byte[] bytes)
            => new StubDownloader(new WhatsAppMediaRef("k.pdf", "application/pdf", bytes.Length, bytes));

        public static WhatsAppMediaDownloader ReturningImage()
            => new StubDownloader(new WhatsAppMediaRef("k.jpg", "image/jpeg", 10, null));

        private sealed class StubDownloader : WhatsAppMediaDownloader
        {
            private readonly WhatsAppMediaRef _ref;

            public StubDownloader(WhatsAppMediaRef mediaRef)
                : base(new HttpClient(), new InMemoryWhatsAppMediaStore(),
                       Options.Create(new WhatsAppOptions()),
                       NullLogger<WhatsAppMediaDownloader>.Instance)
                => _ref = mediaRef;

            public override Task<WhatsAppMediaRef?> FetchAsync(
                string mediaId, string messageType, WhatsAppSendContext ctx,
                Guid licenseId, CancellationToken ct = default)
                => Task.FromResult<WhatsAppMediaRef?>(_ref);
        }
    }
```

Dosyanın `using` bloğuna eklenecekler:

```csharp
using OrderDeck.PdfParsing;
```

> `InMemoryWhatsAppMediaStore` zaten `WhatsAppMediaDownloaderTests.cs` içinde
> var; testler aynı derlemede olduğu için doğrudan kullanılabilir.

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~WhatsAppInboundLabelTests
```
Expected: FAIL — derleme hataları: `WhatsAppInboundJob` yapıcısı 6 argüman
almıyor, `WhatsAppMediaDownloader` `sealed`, `FetchAsync` `virtual` değil,
`WaDekontExtractions` DbSet'i çağrılamıyor değil (o Task 2'de eklendi).

- [ ] **Step 3: `WhatsAppMediaDownloader`'ı türetilebilir yap**

`WhatsAppMediaDownloader.cs` — sınıf bildirimi ve `FetchAsync`:

```csharp
public class WhatsAppMediaDownloader
```

```csharp
    public virtual async Task<WhatsAppMediaRef?> FetchAsync(
```

> **Neden `sealed` kalkıyor:** medya indirme HTTP'ye ve gerçek Graph
> sözleşmesine bağlı; ayrıştırma davranışını doğrulamak için o katmanı taklit
> etmenin başka yolu yok. Alternatif bir `IWhatsAppMediaDownloader` arayüzü
> çıkarmaktı — tek uygulaması olan arayüz, YAGNI.

- [ ] **Step 4: Job'a ayrıştırıcıyı ekle**

`WhatsAppInboundJob.cs` — alanlar + yapıcı:

```csharp
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly ILogger<WhatsAppInboundJob> _log;
    private readonly LabelRuleApplier _labels;
    private readonly WaDekontExtractor _dekonts;
    private readonly WhatsAppMediaDownloader? _media;

    public WhatsAppInboundJob(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        ILogger<WhatsAppInboundJob> log,
        LabelRuleApplier labels,
        WaDekontExtractor dekonts,
        WhatsAppMediaDownloader? media = null)
    {
        _db = db;
        _accounts = accounts;
        _log = log;
        _labels = labels;
        _dekonts = dekonts;
        _media = media;
    }
```

`ProcessMessagesAsync` — mesaj satırının eklendiği yer (Task 8 sonrası hâli)
şöyle değişir: `_db.WaMessages.Add(new WaMessage { ... })` çağrısı bir
değişkene alınır ve hemen ardından ayrıştırma denenir.

```csharp
            var message = new WaMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = convo.Id,
                Conversation = convo,
                LicenseId = account.LicenseId,
                WamId = m.WamId,
                Direction = m.IsEcho ? "out" : "in",
                Origin = m.IsEcho ? "echo" : null,
                Type = m.Type,
                Body = m.Body,
                MediaR2Key = media?.ObjectKey,
                MediaMimeType = media?.MimeType ?? m.MediaMimeType,
                MediaSizeBytes = media?.SizeBytes,
                Status = m.IsEcho ? "sent" : "received",
                Timestamp = m.Timestamp,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.WaMessages.Add(message);

            // Baytlar YALNIZ PDF için dolu gelir (bkz. WhatsAppMediaRef.Bytes).
            // Görsel dekontlar AI gerektirir, ayrı faz.
            if (!m.IsEcho && media?.Bytes is { Length: > 0 })
            {
                var extraction = _dekonts.TryExtract(account.LicenseId, message.Id, media.Bytes);
                if (extraction is not null)
                {
                    // Gezinme özelliği: mesaj da bu SaveChanges'te yazılıyor,
                    // EF'e ekleme sırasını başka türlü anlatamayız (PK = FK).
                    extraction.WaMessage = message;
                    _db.WaDekontExtractions.Add(extraction);
                }
            }
```

- [ ] **Step 5: DI kaydını ekle**

`OrderDeck.LicenseServer/Program.cs` — Task 4'te eklenen `LabelRuleApplier`
satırının hemen altına:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.WhatsApp.WaDekontExtractor>();
```

> `IPdfDekontParser` zaten 75-76. satırlarda singleton kayıtlı, ek iş yok.

- [ ] **Step 6: Diğer iki test yardımcısını güncelle**

`WhatsAppInboundJobTests.cs` — 37. satır:

```csharp
        return (db, new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(new PdfDekontParser(), NullLogger<WaDekontExtractor>.Instance)),
            licenseId);
```

`WhatsAppMediaDownloaderTests.cs` — job kurulumu:

```csharp
        var job = new WhatsAppInboundJob(
            db, accounts, NullLogger<WhatsAppInboundJob>.Instance,
            new LabelRuleApplier(db, NullLogger<LabelRuleApplier>.Instance),
            new WaDekontExtractor(new PdfDekontParser(), NullLogger<WaDekontExtractor>.Instance),
            downloader);
```

Her iki dosyaya `using OrderDeck.PdfParsing;` eklenir. Gerçek `PdfDekontParser`
kullanılabilir çünkü bu testlerdeki baytlar PDF değil → `Bytes` null kalır →
ayrıştırıcıya hiç girilmez.

- [ ] **Step 7: Testlerin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~WhatsApp|FullyQualifiedName~WaDekont"
```
Expected: PASS — `WhatsAppInboundLabelTests` 10 test, diğer WhatsApp sınıfları
bozulmamış.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppInboundJob.cs \
        OrderDeck.LicenseServer/Services/WhatsApp/WhatsAppMediaDownloader.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundLabelTests.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppInboundJobTests.cs \
        OrderDeck.LicenseServer.Tests/Services/WhatsApp/WhatsAppMediaDownloaderTests.cs
git commit -m "feat(wa-etiket): gelen PDF dekontu ayrıştırıp mesajın yanına yaz"
```

---

### Task 11: Etiket CRUD ucu

Panelin (OrderDeck-Mobile reposu) etiket tanımlarını yönettiği uç. Bu repoda
yalnız API var, ekran yok.

**Rota adı sapması (#4):** tasarım `/api/panel/wa/labels` diyordu; repo
konvansiyonu `api/panel/whatsapp-templates` → `api/panel/whatsapp-labels`.

**Silme neden elle temizliyor:** Task 2'de `WaLabelRule` ve
`WaConversationLabel` FK'leri `NoAction` (SQL Server çoklu cascade yolu kabul
etmiyor). Yani etiketi silmeden önce ona bağlı satırlar bizim tarafımızdan
kaldırılmazsa DB FK hatası verir.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelLicenseScope.cs`
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelsControllerTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

Create `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppLabelsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppLabelsControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);

    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-WALBL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<LabelDto> CreateAsync(HttpClient client, string name, string color = "#eab308")
    {
        var resp = await client.PostAsJsonAsync("/api/panel/whatsapp-labels", new { name, color });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<LabelDto>())!;
    }

    [Fact]
    public async Task Creates_and_lists_labels_alphabetically()
    {
        var (client, _) = await SeedAsync();

        await CreateAsync(client, "Ödeme bekliyor");
        await CreateAsync(client, "Dekont geldi");

        var list = await client.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");

        list!.Select(l => l.Name).Should().ContainInOrder("Dekont geldi", "Ödeme bekliyor");
    }

    [Fact]
    public async Task Rejects_a_color_outside_the_palette()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Test", color = "#123456" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_same_license()
    {
        var (client, _) = await SeedAsync();
        await CreateAsync(client, "Dekont geldi");

        var resp = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Renames_a_label()
    {
        var (client, _) = await SeedAsync();
        var label = await CreateAsync(client, "Dekont geldi");

        var resp = await client.PatchAsJsonAsync(
            $"/api/panel/whatsapp-labels/{label.Id}", new { name = "Dekont var", color = "#EF4444" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<LabelDto>())!;
        updated.Name.Should().Be("Dekont var");
        // Büyük harfle gönderildi, kanonik küçük harfle saklanır.
        updated.Color.Should().Be("#ef4444");
    }

    [Fact]
    public async Task Deleting_a_label_also_removes_its_rule_and_conversation_links()
    {
        var (client, licenseId) = await SeedAsync();
        var label = await CreateAsync(client, "Dekont geldi");

        Guid conversationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            conversationId = Guid.NewGuid();
            db.WaConversations.Add(new WaConversation
            {
                Id = conversationId, LicenseId = licenseId,
                CustomerPhone = "905321234567", PhoneNumberId = "PNID_1",
                Status = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                EventKey = WaLabelEvent.CustomerSentDocument, WaLabelId = label.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.WaConversationLabels.Add(new WaConversationLabel
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                ConversationId = conversationId, WaLabelId = label.Id,
                Source = "auto", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.DeleteAsync($"/api/panel/whatsapp-labels/{label.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WaLabels.AnyAsync(l => l.Id == label.Id)).Should().BeFalse();
            (await db.WaLabelRules.AnyAsync(r => r.WaLabelId == label.Id)).Should().BeFalse();
            (await db.WaConversationLabels.AnyAsync(x => x.WaLabelId == label.Id)).Should().BeFalse();
            // Sohbetin kendisi silinmez — etiket düşer, konuşma kalır.
            (await db.WaConversations.AnyAsync(c => c.Id == conversationId)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Another_broadcasters_label_is_not_reachable()
    {
        var (mine, _) = await SeedAsync();
        var (theirs, _) = await SeedAsync();

        var label = await CreateAsync(theirs, "Onların etiketi");

        var get = await mine.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");
        get!.Should().NotContain(l => l.Id == label.Id);

        var del = await mine.DeleteAsync($"/api/panel/whatsapp-labels/{label.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_an_active_license_the_list_is_empty()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var list = await client.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");

        list.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelWhatsAppLabelsControllerTests
```
Expected: FAIL — tüm istekler 404 (rota yok).

- [ ] **Step 3: Ortak lisans çözümünü yaz**

Create `OrderDeck.LicenseServer/Controllers/Panel/PanelLicenseScope.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panel isteğindeki tenant müşterisinden aktif lisansı çözer.
///
/// <para>Repoda bu sorgu her controller'da özel bir metot olarak tekrarlanıyor
/// (<c>PanelOperatorsController.ResolveLicenseAsync</c> vb.). Bu iş üç yeni
/// controller getiriyor; aynı gövdeyi üç kez daha kopyalamak yerine buraya
/// alındı. <b>Mevcut controller'lar bilinçli olarak ellenmiyor</b> — bu iş
/// etiket altyapısı, genel bir yeniden düzenleme değil.</para>
/// </summary>
internal static class PanelLicenseScope
{
    /// <summary>Tipik kullanımda müşterinin tek lisansı var; birden fazlaysa
    /// ilk aktif olan seçilir.</summary>
    public static Task<Guid?> ResolveAsync(
        LicenseDbContext db, Guid customerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Licenses
            .Where(l => l.CustomerId == customerId
                && l.RevokedAt == null
                && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
```

- [ ] **Step 4: Controller'ı yaz**

Create `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelsController.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının WhatsApp sohbet etiketleri. Etiketler tamamen dinamik: biz hiç
/// etiket tanımlamıyoruz, her yayıncı kendi listesini yazar.
///
/// <para>Meta'nın Cloud API'sinde sohbet etiketi yok; bu tablo tamamen bize
/// ait. Yani şablon onayı beklerken de çalışır.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-labels")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppLabelsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelWhatsAppLabelsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);

    public sealed class LabelRequest
    {
        [Required, MaxLength(60)]
        public string Name { get; set; } = "";

        [Required, MaxLength(7)]
        public string Color { get; set; } = "";
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Ok(Array.Empty<LabelDto>());

        var rows = await _db.WaLabels
            .Where(l => l.LicenseId == licenseId.Value)
            .OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color, l.CreatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LabelRequest req, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var name = req.Name.Trim();
        if (name.Length == 0) return Problem(title: "empty-name", statusCode: 400);

        var color = WaLabelColors.Normalize(req.Color);
        if (color is null) return Problem(title: "invalid-color", statusCode: 400);

        var taken = await _db.WaLabels
            .AnyAsync(l => l.LicenseId == licenseId.Value && l.Name == name, ct);
        if (taken) return Problem(title: "duplicate-name", statusCode: 409);

        var row = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            Name = name,
            Color = color,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WaLabels.Add(row);
        await _db.SaveChangesAsync(ct);

        var dto = new LabelDto(row.Id, row.Name, row.Color, row.CreatedAt);
        return Created($"/api/panel/whatsapp-labels/{row.Id}", dto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LabelRequest req, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var row = await _db.WaLabels
            .FirstOrDefaultAsync(l => l.Id == id && l.LicenseId == licenseId.Value, ct);
        if (row is null) return NotFound();

        var name = req.Name.Trim();
        if (name.Length == 0) return Problem(title: "empty-name", statusCode: 400);

        var color = WaLabelColors.Normalize(req.Color);
        if (color is null) return Problem(title: "invalid-color", statusCode: 400);

        var taken = await _db.WaLabels
            .AnyAsync(l => l.LicenseId == licenseId.Value && l.Name == name && l.Id != id, ct);
        if (taken) return Problem(title: "duplicate-name", statusCode: 409);

        row.Name = name;
        row.Color = color;
        await _db.SaveChangesAsync(ct);

        return Ok(new LabelDto(row.Id, row.Name, row.Color, row.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var row = await _db.WaLabels
            .FirstOrDefaultAsync(l => l.Id == id && l.LicenseId == licenseId.Value, ct);
        if (row is null) return NotFound();

        // Bağlı satırlar ELLE temizlenir: her iki FK de NoAction — SQL Server
        // License'tan iki cascade yolu olan şemayı kabul etmiyor (bkz. Task 2).
        var rules = await _db.WaLabelRules.Where(r => r.WaLabelId == id).ToListAsync(ct);
        _db.WaLabelRules.RemoveRange(rules);

        var links = await _db.WaConversationLabels.Where(x => x.WaLabelId == id).ToListAsync(ct);
        _db.WaConversationLabels.RemoveRange(links);

        _db.WaLabels.Remove(row);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
```

- [ ] **Step 5: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelWhatsAppLabelsControllerTests|FullyQualifiedName~PanelControllerConventionTests"
```
Expected: PASS (7 + konvansiyon testi). Konvansiyon testi de koşulur çünkü
yeni Panel controller'ı `[ApiController]` + `[Authorize(Bearer-Customer)]`
taşımazsa CI'yı o kırar.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelLicenseScope.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelsControllerTests.cs
git commit -m "feat(wa-etiket): etiket CRUD ucu"
```

---

### Task 12: Kural okuma/yazma ucu

Panelin "hangi olay hangi etiketi yapıştırsın" ekranını besleyen uç.

**Neden `GET` beş olayı da döndürüyor:** olay listesi sabit ve dinamik değil;
panel bunu kendi içine sabitlerse sunucuya bir olay eklendiğinde ekran sessizce
eksik kalır. Kural tanımlı olmayan olay için `waLabelId` null döner.

**Neden tel formatı enum ADI, sayı değil:** `3` gördüğünde panelin ne olduğunu
anlaması için sunucu enum'unu kopyalaması gerekir; `ShipmentStatusChanged`
kendi kendini anlatır. DB'de yine int saklanır (Task 2).

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelRulesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelRulesControllerTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

Create `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelRulesControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppLabelRulesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppLabelRulesControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);
    private sealed record RuleDto(string EventKey, string Description, Guid? WaLabelId);

    private async Task<(HttpClient Client, Guid LabelId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(), CustomerId = customerId,
                LicenseKey = "LDK-WARUL-" + Guid.NewGuid().ToString("N"),
                SkuCode = "STD", ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });
        var label = (await created.Content.ReadFromJsonAsync<LabelDto>())!;
        return (client, label.Id);
    }

    [Fact]
    public async Task Lists_every_event_even_when_no_rule_exists()
    {
        var (client, _) = await SeedAsync();

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");

        rules!.Should().HaveCount(5);
        rules.Select(r => r.EventKey).Should().BeEquivalentTo(new[]
        {
            "PaymentApproved", "PaymentRejected", "OrderReceived",
            "ShipmentStatusChanged", "CustomerSentDocument",
        });
        rules.Should().OnlyContain(r => r.WaLabelId == null);
        rules.Should().OnlyContain(r => r.Description.Length > 0);
    }

    [Fact]
    public async Task Assigning_a_label_to_an_event_is_readable_back()
    {
        var (client, labelId) = await SeedAsync();

        var put = await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/CustomerSentDocument", new { waLabelId = labelId });
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "CustomerSentDocument").WaLabelId.Should().Be(labelId);
    }

    [Fact]
    public async Task Assigning_twice_replaces_instead_of_duplicating()
    {
        var (client, first) = await SeedAsync();
        var second = (await (await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "İnsan baksın", color = "#ef4444" }))
            .Content.ReadFromJsonAsync<LabelDto>())!;

        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/OrderReceived", new { waLabelId = first });
        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/OrderReceived", new { waLabelId = second.Id });

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "OrderReceived").WaLabelId.Should().Be(second.Id);

        // Sayım bu iki etiketle sınırlanır: ApiFactory veritabanı sınıftaki
        // bütün testlerde ortak, filtresiz sayım komşu testlere bağımlı olurdu.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaLabelRules.Count(r =>
            r.EventKey == WaLabelEvent.OrderReceived
            && (r.WaLabelId == first || r.WaLabelId == second.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_null_label_clears_the_rule()
    {
        var (client, labelId) = await SeedAsync();
        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = labelId });

        var clear = await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = (Guid?)null });
        clear.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "PaymentApproved").WaLabelId.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_event_key_is_rejected()
    {
        var (client, labelId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/SomethingElse", new { waLabelId = labelId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Another_broadcasters_label_cannot_be_bound()
    {
        var (mine, _) = await SeedAsync();
        var (_, theirLabelId) = await SeedAsync();

        var resp = await mine.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = theirLabelId });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

> **`using OrderDeck.LicenseServer.Services.WhatsApp;`** de gerekir —
> `WaLabelEvent` oradan geliyor.

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelWhatsAppLabelRulesControllerTests
```
Expected: FAIL — 404 (rota yok).

- [ ] **Step 3: Controller'ı yaz**

Create `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelRulesController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Otomatik etiket kuralları: SABİT bir olay → yayıncının DİNAMİK etiketi.
///
/// <para>Olay listesi genişleyebilir ama panel tarafından tanımlanamaz —
/// her olayın sunucuda onu tetikleyen gerçek bir kod yolu var.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-label-rules")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppLabelRulesController : ControllerBase
{
    /// <summary>Panelde olayın yanında görünen Türkçe açıklama. Sunucuda
    /// duruyor ki yeni bir olay eklendiğinde panel güncellenmeden de anlamlı
    /// bir metin görünsün.</summary>
    private static readonly IReadOnlyDictionary<WaLabelEvent, string> Descriptions =
        new Dictionary<WaLabelEvent, string>
        {
            [WaLabelEvent.PaymentApproved] = "Ödeme onaylandı",
            [WaLabelEvent.PaymentRejected] = "Ödeme reddedildi",
            [WaLabelEvent.OrderReceived] = "Yeni sipariş geldi",
            [WaLabelEvent.ShipmentStatusChanged] = "Kargo durumu değişti",
            [WaLabelEvent.CustomerSentDocument] = "Müşteri belge/görsel gönderdi",
        };

    private readonly LicenseDbContext _db;

    public PanelWhatsAppLabelRulesController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record RuleDto(string EventKey, string Description, Guid? WaLabelId);

    public sealed class RuleRequest
    {
        /// <summary>null → kuralı kaldır.</summary>
        public Guid? WaLabelId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);

        var assigned = licenseId is null
            ? new Dictionary<WaLabelEvent, Guid>()
            : await _db.WaLabelRules
                .Where(r => r.LicenseId == licenseId.Value)
                .ToDictionaryAsync(r => r.EventKey, r => r.WaLabelId, ct);

        var rows = Descriptions
            .Select(kv => new RuleDto(
                kv.Key.ToString(),
                kv.Value,
                assigned.TryGetValue(kv.Key, out var id) ? id : null))
            .ToList();

        return Ok(rows);
    }

    [HttpPut("{eventKey}")]
    public async Task<IActionResult> Put(
        string eventKey, [FromBody] RuleRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<WaLabelEvent>(eventKey, ignoreCase: false, out var parsed)
            || !Descriptions.ContainsKey(parsed))
        {
            // ignoreCase: false bilerek — "paymentapproved" kabul edilirse
            // panelde yazım hatası sessizce çalışır, sonra düzeltilemez.
            return Problem(title: "unknown-event", statusCode: 400);
        }

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var existing = await _db.WaLabelRules
            .FirstOrDefaultAsync(r => r.LicenseId == licenseId.Value && r.EventKey == parsed, ct);

        if (req.WaLabelId is null)
        {
            if (existing is not null) _db.WaLabelRules.Remove(existing);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // Etiketin BU yayıncıya ait olduğunu doğrula: aksi hâlde başka bir
        // yayıncının etiketi bizim sohbetlerimize yapıştırılabilirdi.
        var owned = await _db.WaLabels.AnyAsync(
            l => l.Id == req.WaLabelId.Value && l.LicenseId == licenseId.Value, ct);
        if (!owned) return NotFound();

        if (existing is null)
        {
            _db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId.Value,
                EventKey = parsed,
                WaLabelId = req.WaLabelId.Value,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.WaLabelId = req.WaLabelId.Value;
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelWhatsAppLabelRulesControllerTests
```
Expected: PASS (6 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppLabelRulesController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppLabelRulesControllerTests.cs
git commit -m "feat(wa-etiket): otomatik kural okuma/yazma ucu"
```

---

### Task 13: Sohbet listesi, etiket filtresi ve elle etiketleme

Son uç. Üç iş yapar: sohbetleri etiketleriyle listeler, etikete göre filtreler,
elle etiket takıp söker.

**Kilitli karar — etiketler YALNIZ elle kaldırılır.** Sunucu hiçbir etiketi
otomatik düşürmez (ödeme onayı `Dekont geldi`yi silmez). Sonucu: "iş var"
etiketleri birikir → kaldırma tek istek olmalı, bu yüzden ayrı bir DELETE ucu var.

**Dekont alanları neden burada:** operatör panelde etiketi görüp sohbeti
açmadan gönderen/tutar/tarih/referansı görsün diye. Sohbetin **en son**
ayrıştırılmış dekontu döner — birden çok dekont gelmişse güncel olan işe yarar.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppConversationsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppConversationsControllerTests.cs` (yeni)

- [ ] **Step 1: Başarısız testi yaz**

Create `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppConversationsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppConversationsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppConversationsControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);
    private sealed record DekontDto(
        string? PayerName, decimal? Amount, DateTimeOffset? PaidAt,
        string? ReferansNo, string ParserConfidence);
    private sealed record ConversationLabelDto(Guid WaLabelId, string Name, string Color, string Source);
    private sealed record ConversationDto(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        int UnreadCount, DateTimeOffset? LastMessageAt,
        List<ConversationLabelDto> Labels, DekontDto? LatestDekont);

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid LabelId, Guid ConversationId);

    private async Task<Seed> SeedAsync(string phone = "905321234567")
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId, CustomerId = customerId,
                LicenseKey = "LDK-WACNV-" + Guid.NewGuid().ToString("N"),
                SkuCode = "STD", ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            db.WaConversations.Add(new WaConversation
            {
                Id = conversationId, LicenseId = licenseId,
                CustomerPhone = phone, PhoneNumberId = "PNID_1",
                ProfileName = "Ayşe", Status = "open", UnreadCount = 2,
                LastMessageAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });
        var label = (await created.Content.ReadFromJsonAsync<LabelDto>())!;

        return new Seed(client, licenseId, label.Id, conversationId);
    }

    [Fact]
    public async Task Lists_conversations_with_no_labels_initially()
    {
        var s = await SeedAsync();

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var row = list!.Single(c => c.Id == s.ConversationId);
        row.CustomerPhone.Should().Be("905321234567");
        row.ProfileName.Should().Be("Ayşe");
        row.UnreadCount.Should().Be(2);
        row.Labels.Should().BeEmpty();
        row.LatestDekont.Should().BeNull();
    }

    [Fact]
    public async Task Attaching_a_label_by_hand_marks_it_manual()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var label = list!.Single(c => c.Id == s.ConversationId).Labels.Single();
        label.WaLabelId.Should().Be(s.LabelId);
        label.Name.Should().Be("Dekont geldi");
        label.Color.Should().Be("#eab308");
        label.Source.Should().Be("manual");
    }

    [Fact]
    public async Task Attaching_the_same_label_twice_is_harmless()
    {
        var s = await SeedAsync();
        var url = $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}";

        (await s.Client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await s.Client.PostAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).Labels.Should().ContainSingle();
    }

    [Fact]
    public async Task Removing_a_label_works_even_when_the_server_attached_it()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WaConversationLabels.Add(new WaConversationLabel
            {
                Id = Guid.NewGuid(), LicenseId = s.LicenseId,
                ConversationId = s.ConversationId, WaLabelId = s.LabelId,
                Source = "auto", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await s.Client.DeleteAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Single(c => c.Id == s.ConversationId).Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task Filtering_by_label_returns_only_matching_conversations()
    {
        var s = await SeedAsync();

        Guid otherConversationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            otherConversationId = Guid.NewGuid();
            db.WaConversations.Add(new WaConversation
            {
                Id = otherConversationId, LicenseId = s.LicenseId,
                CustomerPhone = "905339998877", PhoneNumberId = "PNID_1",
                Status = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await s.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{s.ConversationId}/labels/{s.LabelId}", null);

        var filtered = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            $"/api/panel/whatsapp-conversations?labelId={s.LabelId}");

        filtered!.Should().ContainSingle().Which.Id.Should().Be(s.ConversationId);
        filtered.Should().NotContain(c => c.Id == otherConversationId);
    }

    [Fact]
    public async Task The_latest_parsed_dekont_is_surfaced_next_to_the_labels()
    {
        var s = await SeedAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            // İki dekont: eski ve yeni. Panelde güncel olan işe yarar.
            foreach (var (wamId, payer, amount, minutesAgo) in new[]
                     {
                         ("wamid.old", "ESKİ GÖNDEREN", 100m, 60),
                         ("wamid.new", "AYŞE YILMAZ", 1250.50m, 1),
                     })
            {
                var messageId = Guid.NewGuid();
                db.WaMessages.Add(new WaMessage
                {
                    Id = messageId, ConversationId = s.ConversationId, LicenseId = s.LicenseId,
                    WamId = wamId, Direction = "in", Type = "document",
                    Status = "received",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                });
                db.WaDekontExtractions.Add(new WaDekontExtraction
                {
                    WaMessageId = messageId, LicenseId = s.LicenseId,
                    PayerName = payer, Amount = amount,
                    PaidAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                    ReferansNo = "REF" + wamId, PdfHash = "h" + wamId,
                    ParserConfidence = "High",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
                });
            }
            await db.SaveChangesAsync();
        }

        var list = await s.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");

        var dekont = list!.Single(c => c.Id == s.ConversationId).LatestDekont;
        dekont.Should().NotBeNull();
        dekont!.PayerName.Should().Be("AYŞE YILMAZ");
        dekont.Amount.Should().Be(1250.50m);
        dekont.ReferansNo.Should().Be("REFwamid.new");
        dekont.ParserConfidence.Should().Be("High");
    }

    [Fact]
    public async Task Another_broadcasters_conversation_is_not_reachable()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        var list = await mine.Client.GetFromJsonAsync<List<ConversationDto>>(
            "/api/panel/whatsapp-conversations");
        list!.Should().NotContain(c => c.Id == theirs.ConversationId);

        var resp = await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{theirs.ConversationId}/labels/{mine.LabelId}", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_label_from_another_broadcaster_cannot_be_attached()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync("905441112233");

        var resp = await mine.Client.PostAsync(
            $"/api/panel/whatsapp-conversations/{mine.ConversationId}/labels/{theirs.LabelId}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelWhatsAppConversationsControllerTests
```
Expected: FAIL — 404 (rota yok).

- [ ] **Step 3: Controller'ı yaz**

Create `OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppConversationsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin sohbet listesi: etiketler, etiket filtresi ve elle etiketleme.
///
/// <para><b>Etiketler otomatik DÜŞMEZ.</b> Sunucu hiçbir etiketi kaldırmaz —
/// ödeme onaylansa bile "Dekont geldi" durur. Bu bilinçli: etiket "iş var"
/// demek, işin bittiğine operatör karar verir. Bu yüzden kaldırma tek
/// istektir.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-conversations")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppConversationsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelWhatsAppConversationsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record DekontDto(
        string? PayerName, decimal? Amount, DateTimeOffset? PaidAt,
        string? ReferansNo, string ParserConfidence);

    public sealed record ConversationLabelDto(Guid WaLabelId, string Name, string Color, string Source);

    public sealed record ConversationDto(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        int UnreadCount, DateTimeOffset? LastMessageAt,
        List<ConversationLabelDto> Labels, DekontDto? LatestDekont);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? labelId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Ok(Array.Empty<ConversationDto>());

        var q = _db.WaConversations.Where(c => c.LicenseId == licenseId.Value);

        if (labelId is not null)
        {
            q = q.Where(c => _db.WaConversationLabels
                .Any(x => x.ConversationId == c.Id && x.WaLabelId == labelId.Value));
        }

        var conversations = await q
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => new
            {
                c.Id, c.CustomerPhone, c.ProfileName, c.Status, c.UnreadCount, c.LastMessageAt,
            })
            .ToListAsync(ct);

        var ids = conversations.Select(c => c.Id).ToList();

        // Etiketler tek sorguda: sohbet başına ayrı sorgu 200 satırda 200 tur eder.
        var labels = await (
            from link in _db.WaConversationLabels
            join label in _db.WaLabels on link.WaLabelId equals label.Id
            where ids.Contains(link.ConversationId)
            select new
            {
                link.ConversationId,
                Dto = new ConversationLabelDto(label.Id, label.Name, label.Color, link.Source),
            })
            .ToListAsync(ct);

        var labelsByConversation = labels
            .GroupBy(x => x.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Dto.Name).Select(x => x.Dto).ToList());

        // Sohbetin EN SON ayrıştırılmış dekontu. Mesaj zaman damgasına göre,
        // çünkü webhook'lar sırasız gelebilir ama damga müşterinin gönderdiği andır.
        var dekonts = await (
            from d in _db.WaDekontExtractions
            join m in _db.WaMessages on d.WaMessageId equals m.Id
            where ids.Contains(m.ConversationId)
            select new { m.ConversationId, m.Timestamp, D = d })
            .ToListAsync(ct);

        var latestDekont = dekonts
            .GroupBy(x => x.ConversationId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var newest = g.OrderByDescending(x => x.Timestamp).First().D;
                    return new DekontDto(
                        newest.PayerName, newest.Amount, newest.PaidAt,
                        newest.ReferansNo, newest.ParserConfidence);
                });

        var rows = conversations
            .Select(c => new ConversationDto(
                c.Id, c.CustomerPhone, c.ProfileName, c.Status, c.UnreadCount, c.LastMessageAt,
                labelsByConversation.TryGetValue(c.Id, out var ls) ? ls : new List<ConversationLabelDto>(),
                latestDekont.TryGetValue(c.Id, out var d) ? d : null))
            .ToList();

        return Ok(rows);
    }

    [HttpPost("{conversationId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Attach(
        Guid conversationId, Guid labelId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        // Hem sohbet hem etiket BU yayıncıya ait olmalı; ikisinden biri
        // başkasınınsa 404 — varlığını da sızdırmayalım.
        var ownsConversation = await _db.WaConversations.AnyAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (!ownsConversation) return NotFound();

        var ownsLabel = await _db.WaLabels.AnyAsync(
            l => l.Id == labelId && l.LicenseId == licenseId.Value, ct);
        if (!ownsLabel) return NotFound();

        var exists = await _db.WaConversationLabels.AnyAsync(
            x => x.ConversationId == conversationId && x.WaLabelId == labelId, ct);
        if (exists) return NoContent();   // idempotent: iki kez tıklamak hata değil

        _db.WaConversationLabels.Add(new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpDelete("{conversationId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Detach(
        Guid conversationId, Guid labelId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var link = await _db.WaConversationLabels.FirstOrDefaultAsync(
            x => x.ConversationId == conversationId
                 && x.WaLabelId == labelId
                 && x.LicenseId == licenseId.Value, ct);

        // Zaten yoksa da NoContent: kaldırma idempotent, panel iki kez
        // tıklarsa kullanıcıya anlamsız bir hata göstermeyelim.
        if (link is null) return NoContent();

        // Kaynağı ("auto"/"manual") sormuyoruz: sunucunun yapıştırdığı etiketi
        // de operatör kaldırabilir — kilitli karar.
        _db.WaConversationLabels.Remove(link);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelWhatsAppConversationsControllerTests
```
Expected: PASS (8 test).

- [ ] **Step 5: Tüm sunucu paketini çalıştır**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: PASS. `PanelControllerConventionTests` üç yeni controller'ı da
kapsar — biri `[Authorize(Bearer-Customer)]` unutursa burada patlar.

> **Docker açık olmalı:** birkaç test `SqlServerContainerFixture` ile gerçek
> SQL Server başlatıyor.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelWhatsAppConversationsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelWhatsAppConversationsControllerTests.cs
git commit -m "feat(wa-etiket): sohbet listesi, etiket filtresi ve elle etiketleme"
```

---

### Task 14: Yayına hazırlık

Kod bitti; bu görev PR'ı temiz çıkarmakla ilgili.

- [ ] **Step 1: Sadece bu işe ait dosyaların sahnede olduğunu doğrula**

Run: `git status --short`

Bu depoda **commit'siz duran ve bu PR'a ASLA karışmaması gereken** dosyalar var:

```
.claude/launch.json
.gitignore
.codex/
AGENTS.md
docs/proje-analiz-raporu-2026-07-16.md
docs/superpowers/plans/2026-07-28-whatsapp-odeme-hatirlatma-cloud-api.md
docs/superpowers/plans/2026-08-15-wpf-yorum-eslestirme-cekmece.md
docs/superpowers/specs/2026-07-28-whatsapp-otomasyon-design.md
```

Bunlardan biri `git log --stat` çıktısında görünüyorsa commit'i düzelt.
`git add -A` / `git add .` **kullanma** — her adımda dosyalar tek tek eklendi,
son adımda kural değişmiyor.

- [ ] **Step 2: Her iki test paketini de çalıştır**

Run:
```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```
Expected: ikisi de PASS. WPF/Chat paketine dokunulmadı ama sunucu `Domain`
tipleri paylaşılıyor; kırılmadığını görmek ucuz.

- [ ] **Step 3: Göçün eklemeli olduğunu son kez doğrula**

Run:
```bash
git diff master --stat -- OrderDeck.LicenseServer/Data/Migrations/
```
Beklenen: yalnız `AddWaLabels` göçünün üç dosyası + snapshot. Göç gövdesinde
`DropColumn` / `DropTable` / `AlterColumn` **olmamalı** — prod veritabanı canlı,
göç yıkıcı olamaz.

- [ ] **Step 4: PR aç**

```bash
git push -u origin feat/wa-etiket-altyapisi
gh pr create --title "feat(wa-etiket): WhatsApp sohbet etiketi altyapısı" --body "$(cat <<'EOF'
## Özet
- Yayıncıya ait **dinamik** WhatsApp sohbet etiketleri (Meta'da böyle bir API yok)
- 5 sabit olay → etiket kuralı; tek giriş noktası `LabelRuleApplier`
- Gelen PDF dekontlar mevcut `PdfDekontParser`'dan geçip panelde özetleniyor
- 3 yeni panel ucu: etiketler, kurallar, sohbetler (+ etiket filtresi)

Tasarım: `docs/superpowers/specs/2026-08-18-wa-etiket-altyapisi-design.md`

## Test planı
- [ ] `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
- [ ] Göç prod'da eklemeli (yeni 4 tablo, mevcut tabloya kolon yok)
- [ ] Panel ekranları **bu PR'da yok** — OrderDeck-Mobile reposunda ayrı iş

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

> **Yayın sırası (tasarım Bölüm 4):** göç → sunucu uçları → panel ekranları.
> Sunucu tek başına yayına girebilir; etiketler o an yapışmaya başlar, panel
> geldiğinde geçmiş dolu gelir.

---

## Bu planın KAPSAMADIĞI işler

Tasarımın "Kapsam dışı" bölümüyle birebir; buraya yazılı ki uygulama sırasında
kimse kapsamı genişletmesin:

- **Panel ekranları** — OrderDeck-Mobile (`apps/panel`) reposunda, ayrı iş.
- **Yedek bildirimi** — ayrı spec, WPF sürüm yayınına kilitli. (Not: o spec
  webhook'ta `button_reply.title` yerine `id` kullanmalı —
  `WhatsAppWebhookPayload.cs:181`; iki eşzamanlı "Evet" aksi hâlde ayırt edilemez.)
- **Görsel dekontların AI ile okunması** — ayrı faz.
- **AI mesajlaşma otomasyonu** — ayrı spec. İleride "İnsan baksın" etiketi
  AI'nin devretme çıkışı olacak; bugün eklenecek bir şey yok.
- **Mükerrer dekont tespiti** — `PdfHash` saklanıyor, kontrol kurulmuyor.
- **`reaction` emoji ile telefonda görünür iz** — opsiyonel, sonra.
- **WhatsApp Business App içi "liste"ye programatik atama** — Cloud API'de yok.

---
