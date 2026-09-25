# Obifin Banka Hareketi Çekimi + Gölge Eşleştirme (Faz 1) — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncının (pilot: EMAR GLOBAL, QNB) banka hareketlerini Obifin API'sinden imleçli çekip saklamak; her gelen hareket için müşteri önerisi üretip insan kararıyla karşılaştırarak doğruluğu ölçmek. Otomatik onay YOK.

**Architecture:** LicenseServer'da yeni `Services/Bank` alanı: `IObifinClient` (typed HttpClient, form-urlencoded, header kimlik), lisans başına `ObifinConnection` (şifreli kimlik + imleç), `ObifinPollJob` (Hangfire */5, 31 günlük pencere + `BaslangicHareketId`, 1000/sayfa, idempotent `BankTransaction`), `PaymentMatcher` (kullanıcı adı → IBAN hafızası → ad; yalnız `PaymentMatch` önerisi), `PaymentMatchReconciler` (dekont onayı/reddi ile karşılaştırma, IBAN öğrenme), iki admin Razor sayfası. Spec: `docs/superpowers/specs/2026-09-25-obifin-banka-hareketi-golge-eslestirme-design.md`.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test), Hangfire, DataProtection, Razor Pages (AdminCookie), xunit + FluentAssertions.

---

## Genel kurallar

- Dal: LiveDeck `feat/obifin-banka-cekimi` (PR-1, Görev 1–7) ve `feat/obifin-golge-eslestirme` (PR-2, Görev 8–13; PR-1 merge edilince ondan dallanır). Worktree: `.claude/worktrees/obifin-banka-eslestirme` (spec dalından `git checkout -b feat/obifin-banka-cekimi origin/master` — spec/plan commit'leri de master'a girecek, docs branch ayrıca PR olur).
- Repo **public**: testlerde literal kimlik/IBAN/VKN YOK — üret: `$"pw-{Guid.NewGuid():N}"`, IBAN için `TestIban()` yardımcıları (aşağıda), VKN için `Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)`.
- Türkçe yorum/test adı; commit Türkçe emir kipi + `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- Komutlar LiveDeck kökünden. Filtreli test: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`. Sunucu derlemesi `TreatWarningsAsErrors` — 0 uyarı.
- Göç: `cd OrderDeck.LicenseServer && dotnet ef migrations add <Ad> --output-dir Data/Migrations` (dotnet-ef 10 global kurulu). Göç dosyası + Designer + Snapshot birlikte commit'lenir.
- Gerçek Obifin'e yalnız VPS'ten erişilebilir (IP beyaz liste); yerelde ve testte `IObifinClient` stub'lanır.
- Yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) içinde master merge YOK. Merge Burak'ta.

## Dosya haritası

| Görev | Dosya |
|---|---|
| 1 | `OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs`, `BankConnection.cs`, `BankAccount.cs`, `BankTransaction.cs`, `PaymentMatch.cs`, `CustomerIbanMemory.cs`, `PaymentMatchGap.cs` (YENİ); `Services/Bank/BankOptions.cs`, `BankHasher.cs` (YENİ); `Data/LicenseDbContext.cs` (DbSet + config); `Data/Migrations/*_ObifinBankTransactions.cs` (YENİ); test `Services/Bank/BankHasherTests.cs`, `Data/BankModelTests.cs` |
| 2 | `Services/Bank/ObifinOptions.cs`, `IObifinClient.cs`, `ObifinClient.cs`, `NullObifinClient.cs` (YENİ); test `Services/Bank/ObifinClientTests.cs` |
| 3 | `Services/Bank/ObifinConnectionService.cs` (YENİ); test `ObifinConnectionServiceTests.cs` |
| 4 | `Services/Bank/ObifinPollJob.cs`, `ObifinAccountRefreshJob.cs`, `BankDataRetentionJob.cs` (YENİ); testler |
| 5 | `Program.cs` (options, DI, HttpClient, recurring); test `Services/Bank/BankJobsDiTests.cs` |
| 6 | `Pages/Admin/Obifin/Index.cshtml(.cs)` (YENİ); `Pages/Shared/_AdminLayout.cshtml` (menü); `Services/Audit/AuditEvents.cs`; test `Pages/Admin/AdminObifinPageTests.cs` |
| 7 | kod yok — PR-1 tam paket + PR |
| 8 | `Services/Bank/BankTextNormalizer.cs`; test |
| 9 | `Services/Bank/PaymentMatcher.cs`; test |
| 10 | `Services/Bank/MatchingBankTransactionSink.cs` (YENİ); `Program.cs` (sink değişimi); test `Services/Bank/MatchingSinkTests.cs` |
| 11 | `Services/Bank/PaymentMatchReconciler.cs` (YENİ); `Controllers/Panel/PanelPaymentsController.cs` (kanca); `Services/Bank/MatchingBankTransactionSink.cs` (gap çözümü); `Program.cs`; test `PaymentMatchReconcilerTests.cs` + `MatchingSinkTests.cs` güncellemesi |
| 12 | `Pages/Admin/BankaEslestirme/Index.cshtml(.cs)`, `Services/Bank/PaymentMatchMetrics.cs` (YENİ); `_AdminLayout.cshtml`, `AuditEvents.cs`, `Program.cs`; test `PaymentMatchMetricsTests.cs`, `Pages/Admin/AdminBankaEslestirmePageTests.cs` |
| 13 | spec §6 sapma notu; PR-2 tam paket + PR |

---

## PR-1 — Çekim ve saklama

### Görev 1: Veri modeli, hash yardımcısı, göç

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Domain/Bank/*.cs` (7 entity), `OrderDeck.LicenseServer/Services/Bank/BankOptions.cs`, `OrderDeck.LicenseServer/Services/Bank/BankHasher.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs`, `OrderDeck.LicenseServer.Tests/Data/BankModelTests.cs`

- [ ] **Adım 1: Hash testini yaz (kırmızı — derlenmez)**

`OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>IBAN/VKN düz saklanmaz; HMAC anahtarı olmadan hash'ten geri dönülemez,
/// aynı değer aynı hash'i verir (hafıza araması), maskeleme yalnız görüntü içindir.</summary>
public sealed class BankHasherTests
{
    private static BankHasher NewHasher(string? key = null)
        => new(Options.Create(new BankOptions { HashKey = key ?? $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    /// <summary>Test IBAN'ı üretir — repo public, gerçek IBAN yazılmaz.</summary>
    public static string TestIban()
        => "TR" + Random.Shared.NextInt64(10_000_000_000_000_000, 99_999_999_999_999_999).ToString() + "0000000";

    [Fact]
    public void Ayni_iban_ayni_anahtarla_ayni_hashi_verir_bosluk_ve_kucuk_harf_farki_yok()
    {
        var h = NewHasher();
        var iban = TestIban();
        var spaced = string.Join(" ", Enumerable.Range(0, iban.Length / 4 + 1)
            .Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));

        h.HashIban(iban).Should().Be(h.HashIban(spaced.ToLowerInvariant()));
        h.HashIban(iban).Should().HaveLength(64, "SHA-256 hex");
    }

    [Fact]
    public void Farkli_anahtar_farkli_hash_verir()
    {
        var iban = TestIban();
        NewHasher().HashIban(iban).Should().NotBe(NewHasher().HashIban(iban));
    }

    [Fact]
    public void Maske_yalniz_ilk_dort_ve_son_uc_karakteri_gosterir()
    {
        var iban = TestIban();
        var masked = BankHasher.MaskIban(iban);
        masked.Should().StartWith(iban[..4]).And.EndWith(iban[^3..]).And.Contain("…");
        masked.Length.Should().BeLessThan(iban.Length);
    }

    [Fact]
    public void Bos_veya_kisa_deger_hash_ve_maske_uretmez()
    {
        var h = NewHasher();
        h.HashIban("").Should().BeNull();
        h.HashIban("  ").Should().BeNull();
        BankHasher.MaskIban("TR12").Should().Be("TR12");
    }

    [Fact]
    public void Anahtar_bos_ise_hasher_kurulamaz()
    {
        var act = () => new BankHasher(Options.Create(new BankOptions { HashKey = "" }));
        act.Should().Throw<InvalidOperationException>("anahtarsız HMAC = düz SHA, IBAN sözlük saldırısına açık");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

`--filter "FullyQualifiedName~BankHasherTests"` → FAIL: `BankHasher`/`BankOptions` yok.

- [ ] **Adım 3: Options + hasher**

`OrderDeck.LicenseServer/Services/Bank/BankOptions.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`OrderDeck:Bank` bölümü. <see cref="HashKey"/> .env'den gelir (Bitwarden'da yedek);
/// IBAN/VKN hash'leri bu anahtarla HMAC'lenir — anahtar değişirse eski hash'ler eşleşmez.</summary>
public sealed class BankOptions
{
    public string HashKey { get; set; } = "";

    /// <summary>Eşleştirme dışı bırakılan işlem kodları (POS tahsilatı vb.). Başlangıç: CCP (QNB demo).</summary>
    public string[] ExcludedTransactionCodes { get; set; } = ["CCP"];

    public int RawJsonRetentionDays { get; set; } = 90;
    public int DescriptionRetentionDays { get; set; } = 180;
}
```

`OrderDeck.LicenseServer/Services/Bank/BankHasher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// IBAN/VKN için HMAC-SHA256 (spec §7). Düz SHA yetersiz: IBAN'ın entropisi düşük,
/// sızan tablo sözlükle çözülür. Anahtar yalnız sunucuda; hash yalnız "aynı mı" sorusuna
/// cevap verir (hafıza araması), geri dönüş yoktur.
/// </summary>
public sealed class BankHasher
{
    private readonly byte[] _key;

    public BankHasher(IOptions<BankOptions> opt)
    {
        var key = opt.Value.HashKey;
        if (string.IsNullOrWhiteSpace(key) || key.Length < 16)
            throw new InvalidOperationException(
                "OrderDeck:Bank:HashKey boş ya da 16 karakterden kısa — IBAN hash'i güvensiz olur.");
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>Boşluk/küçük harf farkı hash'i değiştirmez; boş değer null döner.</summary>
    public string? HashIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        return norm is null ? null : Hmac(norm);
    }

    public string? HashTaxId(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = new string(taxId.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : Hmac(digits);
    }

    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = new string(iban.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length < 8 ? null : compact;
    }

    /// <summary>`TR12…345` — yalnız görüntü; 8 karakterden kısa değer olduğu gibi döner.</summary>
    public static string MaskIban(string? iban)
    {
        var norm = NormalizeIban(iban);
        if (norm is null) return iban ?? "";
        return norm[..4] + "…" + norm[^3..];
    }

    private string Hmac(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(mac);
    }
}
```

- [ ] **Adım 4: Koştur — YEŞİL** (`BankHasherTests` 5/5).

- [ ] **Adım 5: Model testini yaz (kırmızı — derlenmez)**

`OrderDeck.LicenseServer.Tests/Data/BankModelTests.cs` — InMemory index'i uygulamaz; model
metadata'sından tekil index'lerin ve uzunlukların TANIMLI olduğunu doğrular:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public sealed class BankModelTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"bank-model-{Guid.NewGuid():N}").Options);

    [Fact]
    public void BankTransaction_lisans_icinde_ObifinId_tekil()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(BankTransaction))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "ObifinId" }),
            "aynı hareket iki kez yazılamaz — imleç geri sarsa bile");
    }

    [Fact]
    public void ObifinConnection_lisans_basina_tek()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(ObifinConnection))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId" }));
    }

    [Fact]
    public void CustomerIbanMemory_lisans_icinde_iban_hash_tekil()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(CustomerIbanMemory))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "LicenseId", "IbanHash" }),
            "bir IBAN aynı anda tek müşteriye ait olabilir");
    }

    [Fact]
    public void Ham_iban_alani_yok_yalniz_hash_ve_maske()
    {
        using var db = NewDb();
        var props = db.Model.FindEntityType(typeof(BankTransaction))!.GetProperties().Select(p => p.Name).ToList();
        props.Should().Contain("CounterpartyIbanHash").And.Contain("CounterpartyIbanMasked");
        props.Should().NotContain("CounterpartyIban", "spec §7: IBAN düz saklanmaz");
    }

    [Fact]
    public void PaymentMatch_hareket_basina_tek()
    {
        using var db = NewDb();
        var et = db.Model.FindEntityType(typeof(PaymentMatch))!;
        et.GetIndexes().Should().Contain(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "BankTransactionId" }));
    }

    [Fact]
    public void Status_alanlari_string_saklanir()
    {
        using var db = NewDb();
        var p = db.Model.FindEntityType(typeof(PaymentMatch))!.FindProperty("Status")!;
        p.GetValueConverter().Should().NotBeNull("admin SQL'inde Proposed okunur, 0 değil");
    }
}
```

- [ ] **Adım 6: Entity'leri yaz**

`OrderDeck.LicenseServer/Domain/Bank/ObifinConnection.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum ObifinConnectionStatus { Unverified = 0, Verified = 1, Failed = 2, Disabled = 3 }

/// <summary>Lisans başına Obifin web servis kimliği + çekim imleci (spec §3).
/// Şifre ve API key DataProtection ile şifreli; görünüme yalnız "kayıtlı" bayrağı çıkar.</summary>
public sealed class ObifinConnection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string BaseUrl { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    public ObifinConnectionStatus Status { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    /// <summary>Görülen en büyük Obifin hareket Id'si; yalnız tam başarılı turda ilerler.</summary>
    public long? LastObifinTransactionId { get; set; }
    public DateTimeOffset? BackfillCompletedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/BankConnection.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankConnectionStatus { Active = 0, Failed = 1, Removed = 2 }

/// <summary>Obifin'de açılmış banka API kaydı. Banka web servis kimlikleri SAKLANMAZ —
/// admin girer, sunucu Obifin'e iletir, unutur (spec §3).</summary>
public sealed class BankConnection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid ObifinConnectionId { get; set; }
    public ObifinConnection ObifinConnection { get; set; } = null!;
    public string BankaKodu { get; set; } = "";
    public long BankaApiId { get; set; }
    public string Label { get; set; } = "";
    public BankConnectionStatus Status { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/BankAccount.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public sealed class BankAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid? BankConnectionId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public string IbanMasked { get; set; } = "";
    public string? IbanHash { get; set; }
    public string Currency { get; set; } = "TL";
    public bool Active { get; set; }
    /// <summary>Obifin `GuncellemeTarihi` — bankadan son çekim; gecikme ölçümü.</summary>
    public DateTimeOffset? LastBankSyncAt { get; set; }
    public string? NotificationNote { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/BankTransaction.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankTransactionDirection { Incoming = 0, Outgoing = 1 }

/// <summary>Obifin'den çekilen tek hareket. (LicenseId, ObifinId) tekil → idempotent yazım.
/// Karşı IBAN/VKN yalnız hash + maske (spec §7); ham JSON ve açıklama süreli (§5).</summary>
public sealed class BankTransaction
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public long ObifinId { get; set; }
    public Guid? BankAccountId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public BankTransactionDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TL";
    public DateTimeOffset OccurredAt { get; set; }
    public string? Description { get; set; }
    public string? TransactionCode { get; set; }
    public string? CommonType { get; set; }
    public string? BankReference { get; set; }
    public string? CounterpartyIbanHash { get; set; }
    public string? CounterpartyIbanMasked { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyTaxIdHash { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset? DescriptionPurgedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/PaymentMatch.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum PaymentMatchLayer { None = 0, UsernameInDescription = 1, IbanMemory = 2, NameAmount = 3 }

public enum PaymentMatchStatus
{
    Proposed = 0,
    NoProposal = 1,
    ConfirmedByHuman = 2,
    Contradicted = 3,
    ManualOnly = 4
}

/// <summary>Gölge mod önerisi (spec §3/§6). Hiçbir zaman Payment yaratmaz/değiştirmez.</summary>
public sealed class PaymentMatch
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid BankTransactionId { get; set; }
    public BankTransaction BankTransaction { get; set; } = null!;
    public Guid? ProposedWpfCustomerId { get; set; }
    public PaymentMatchLayer Layer { get; set; }
    public decimal Confidence { get; set; }
    public string? Evidence { get; set; }
    public PaymentMatchStatus Status { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? ActualWpfCustomerId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/CustomerIbanMemory.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum IbanMemorySource { ManualMatch = 0, HumanApproval = 1 }

/// <summary>Müşteri ↔ gönderen IBAN hafızası. Geri alma satırı SİLER (spec §3): yanlış öğrenip
/// sessizce tekrarlamayı önlemek için aktif tabloda iptal bayrağı tutulmaz.</summary>
public sealed class CustomerIbanMemory
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid WpfCustomerId { get; set; }
    public string IbanHash { get; set; } = "";
    public string IbanMasked { get; set; } = "";
    public IbanMemorySource LearnedFrom { get; set; }
    public Guid? SourceBankTransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Bank/PaymentMatchGap.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain.Bank;

public enum PaymentMatchGapReason { NoCandidate = 0, AmbiguousCandidates = 1 }

/// <summary>İnsan kararı var, banka hareketi bulunamadı (gecikme / başka hesap) — ölçüm (spec §3).</summary>
public sealed class PaymentMatchGap
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid PaymentId { get; set; }
    public PaymentMatchGapReason Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ResolvedBankTransactionId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
```

- [ ] **Adım 7: DbContext**

`LicenseDbContext.cs` — DbSet'ler (`IysConsentEvents` satırının altına):

```csharp
    public DbSet<Domain.Bank.ObifinConnection> ObifinConnections => Set<Domain.Bank.ObifinConnection>();
    public DbSet<Domain.Bank.BankConnection> BankConnections => Set<Domain.Bank.BankConnection>();
    public DbSet<Domain.Bank.BankAccount> BankAccounts => Set<Domain.Bank.BankAccount>();
    public DbSet<Domain.Bank.BankTransaction> BankTransactions => Set<Domain.Bank.BankTransaction>();
    public DbSet<Domain.Bank.PaymentMatch> PaymentMatches => Set<Domain.Bank.PaymentMatch>();
    public DbSet<Domain.Bank.CustomerIbanMemory> CustomerIbanMemories => Set<Domain.Bank.CustomerIbanMemory>();
    public DbSet<Domain.Bank.PaymentMatchGap> PaymentMatchGaps => Set<Domain.Bank.PaymentMatchGap>();
```

`OnModelCreating` — `mb.Entity<NetgsmAccount>` bloğunun altına (`using OrderDeck.LicenseServer.Domain.Bank;` ekle):

```csharp
        mb.Entity<ObifinConnection>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => c.LicenseId).IsUnique();
            b.Property(c => c.BaseUrl).HasMaxLength(200).IsRequired();
            b.Property(c => c.UserCode).HasMaxLength(200).IsRequired();
            b.Property(c => c.PasswordProtected).HasMaxLength(4000).IsRequired();
            b.Property(c => c.ApiKeyProtected).HasMaxLength(4000).IsRequired();
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(c => c.LastError).HasMaxLength(500);
        });

        mb.Entity<BankConnection>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.ObifinConnection).WithMany().HasForeignKey(c => c.ObifinConnectionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => new { c.LicenseId, c.BankaApiId }).IsUnique();
            b.Property(c => c.BankaKodu).HasMaxLength(32).IsRequired();
            b.Property(c => c.Label).HasMaxLength(80).IsRequired();
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(c => c.LastError).HasMaxLength(500);
        });

        mb.Entity<BankAccount>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => new { a.LicenseId, a.ObifinAccountId }).IsUnique();
            b.Property(a => a.BankaKodu).HasMaxLength(32).IsRequired();
            b.Property(a => a.IbanMasked).HasMaxLength(40).IsRequired();
            b.Property(a => a.IbanHash).HasMaxLength(64);
            b.Property(a => a.Currency).HasMaxLength(3).IsRequired();
            b.Property(a => a.NotificationNote).HasMaxLength(500);
        });

        mb.Entity<BankTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.LicenseId, t.ObifinId }).IsUnique();
            b.HasIndex(t => new { t.LicenseId, t.Direction, t.OccurredAt });
            b.Property(t => t.BankaKodu).HasMaxLength(32).IsRequired();
            b.Property(t => t.Direction).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(t => t.Amount).HasPrecision(18, 2);
            b.Property(t => t.Currency).HasMaxLength(3).IsRequired();
            b.Property(t => t.Description).HasMaxLength(512);
            b.Property(t => t.TransactionCode).HasMaxLength(32);
            b.Property(t => t.CommonType).HasMaxLength(64);
            b.Property(t => t.BankReference).HasMaxLength(64);
            b.Property(t => t.CounterpartyIbanHash).HasMaxLength(64);
            b.Property(t => t.CounterpartyIbanMasked).HasMaxLength(40);
            b.Property(t => t.CounterpartyName).HasMaxLength(160);
            b.Property(t => t.CounterpartyTaxIdHash).HasMaxLength(64);
        });

        mb.Entity<PaymentMatch>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasOne(m => m.BankTransaction).WithMany().HasForeignKey(m => m.BankTransactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(m => m.BankTransactionId).IsUnique();
            b.HasIndex(m => new { m.LicenseId, m.Status });
            b.Property(m => m.Layer).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            b.Property(m => m.Confidence).HasPrecision(4, 3);
            b.Property(m => m.Evidence).HasMaxLength(500);
        });

        mb.Entity<CustomerIbanMemory>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.LicenseId, m.IbanHash }).IsUnique();
            b.HasIndex(m => new { m.LicenseId, m.WpfCustomerId });
            b.Property(m => m.IbanHash).HasMaxLength(64).IsRequired();
            b.Property(m => m.IbanMasked).HasMaxLength(40).IsRequired();
            b.Property(m => m.LearnedFrom).HasConversion<string>().HasMaxLength(16).IsRequired();
        });

        mb.Entity<PaymentMatchGap>(b =>
        {
            b.HasKey(g => g.Id);
            b.HasIndex(g => g.PaymentId).IsUnique();
            b.Property(g => g.Reason).HasConversion<string>().HasMaxLength(24).IsRequired();
        });
```

- [ ] **Adım 8: Koştur — YEŞİL** (`BankModelTests` 6/6, `BankHasherTests` 5/5).

- [ ] **Adım 9: Göç**

`cd OrderDeck.LicenseServer && dotnet ef migrations add ObifinBankTransactions --output-dir Data/Migrations && cd ..`
Beklenen: `*_ObifinBankTransactions.cs` yedi tablo oluşturur; `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` 0 uyarı. Göç dosyasında CHECK/index adları otomatik.

- [ ] **Adım 10: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Bank OrderDeck.LicenseServer/Services/Bank/BankOptions.cs OrderDeck.LicenseServer/Services/Bank/BankHasher.cs OrderDeck.LicenseServer/Data OrderDeck.LicenseServer.Tests/Services/Bank/BankHasherTests.cs OrderDeck.LicenseServer.Tests/Data/BankModelTests.cs
git commit -F - <<'MSG'
feat(banka): Obifin bağlantısı, banka hareketi, eşleştirme ve IBAN hafızası veri modeli + HMAC hash

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
### Görev 2: Obifin istemcisi (`IObifinClient`)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/ObifinOptions.cs`, `IObifinClient.cs`, `ObifinClient.cs`, `NullObifinClient.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinClientTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Obifin sözleşmesi: header kimlik, form gövde, `Hata:[]` = başarı, sayısal alanlar
/// STRING gelir, tarih aralığı ≤ 31 gün, ham gövde kesmesi ayrıştırmayı etkilemez.</summary>
public sealed class ObifinClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public HttpRequestMessage? Last { get; private set; }
        public string? LastForm { get; private set; }
        public CapturingHandler(string body) => _body = body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastForm = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ObifinCredentials Creds() => new(
        "https://example.invalid", $"u-{Guid.NewGuid():N}@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}");

    private static (ObifinClient Client, CapturingHandler Handler) Build(string body)
    {
        var h = new CapturingHandler(body);
        var c = new ObifinClient(new HttpClient(h), Options.Create(new ObifinOptions()),
            NullLogger<ObifinClient>.Instance);
        return (c, h);
    }

    [Fact]
    public async Task Kimlik_uc_header_ile_gider_govde_form_urlencoded()
    {
        var creds = Creds();
        var (client, h) = Build("""{"Hata":[],"KayitSayisi":0,"Liste":[]}""");

        await client.ListAccountsAsync(creds);

        h.Last!.Headers.GetValues("KullaniciAdi").Single().Should().Be(creds.UserCode);
        h.Last.Headers.GetValues("Sifre").Single().Should().Be(creds.Password);
        h.Last.Headers.GetValues("APIKey").Single().Should().Be(creds.ApiKey);
        h.Last.RequestUri!.ToString().Should().Be("https://example.invalid/webservis/hesaplar/hesaplistesi/");
        h.Last.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task Hata_listesi_doluysa_istisna_mesajlari_tasir()
    {
        var (client, _) = Build("""{"Hata":["Kullanici Bilgileri Hatali!"]}""");

        var act = () => client.ListAccountsAsync(Creds());

        (await act.Should().ThrowAsync<ObifinApiException>()).Which.Messages
            .Should().ContainSingle().Which.Should().Be("Kullanici Bilgileri Hatali!");
    }

    [Fact]
    public async Task Json_degilse_protokol_istisnasi()
    {
        var (client, _) = Build("<html>502 Bad Gateway</html>");
        var act = () => client.ListAccountsAsync(Creds());
        await act.Should().ThrowAsync<ObifinProtocolException>();
    }

    [Fact]
    public async Task Hesap_listesi_string_sayilari_ve_tarihi_ayristirir()
    {
        var (client, _) = Build("""
        {"Hata":[],"KayitSayisi":1,"Liste":[{"Id":"9298","BankaKodu":"qnb","BankaApiId":"77","HesapNo":"123",
          "IBAN":"TR000000000000000000000001","ParaBirimi":"TL","Bakiye":"42736392.00","Durum":"1",
          "GuncellemeTarihi":"2022-10-17 10:32:08","BildirimNotu":""}]}
        """);

        var list = await client.ListAccountsAsync(Creds());

        var a = list.Should().ContainSingle().Subject;
        a.Id.Should().Be(9298); a.BankaApiId.Should().Be(77); a.Active.Should().BeTrue();
        a.Iban.Should().Be("TR000000000000000000000001");
        a.UpdatedAtTr.Should().Be(new DateTime(2022, 10, 17, 10, 32, 8));
    }

    [Fact]
    public async Task Hareket_listesi_sayfa_meta_ve_isaretli_tutar()
    {
        var (client, h) = Build("""
        {"SayfaBasinaKayitSayisi":1000,"ToplamKayitSayisi":2,"ToplamSayfaSayisi":1,"SayfaNo":1,"Hata":[],"Liste":[
          {"Id":"326404","HesapId":"6","IslemNo":"X1","IslemZamaniDT":"2022-10-10 12:07:30","Aciklama":"HAVALE test kodu",
           "IslemKodu":"FT37","OrtakIslemTipi":"EFT","Tutar":"10.00","TutarEksiArti":"10.00","ParaBirimi":"TL",
           "KarsiHesapIBAN":"TR000000000000000000000002","GonderenAdi":null,"BorcluVKN":"","BankaKodu":"garanti"},
          {"Id":"326405","HesapId":"6","IslemNo":"X2","IslemZamaniDT":"2022-10-10 13:25:08","Aciklama":"giden",
           "IslemKodu":"WPSO","Tutar":"-10.00","TutarEksiArti":"-10.00","ParaBirimi":"TL","BankaKodu":"garanti"}]}
        """);

        var page = await client.ListTransactionsAsync(Creds(),
            new DateOnly(2022, 10, 1), new DateOnly(2022, 10, 17), sinceId: 326000, pageNo: 1, pageSize: 1000);

        h.LastForm.Should().Contain("BaslangicTarihi=2022-10-01").And.Contain("BitisTarihi=2022-10-17")
            .And.Contain("BaslangicHareketId=326000").And.Contain("SayfaBasinaKayitSayisi=1000").And.Contain("SayfaNo=1");
        page.TotalPages.Should().Be(1);
        page.Items.Should().HaveCount(2);
        page.Items[0].Id.Should().Be(326404);
        page.Items[0].SignedAmount.Should().Be(10.00m);
        page.Items[1].SignedAmount.Should().Be(-10.00m);
        page.Items[0].OccurredAtTr.Should().Be(new DateTime(2022, 10, 10, 12, 7, 30));
        page.Items[0].RawJson.Should().Contain("\"Id\":\"326404\"");
    }

    [Fact]
    public async Task Otuz_bir_gunden_uzun_aralik_istemcide_reddedilir()
    {
        var (client, h) = Build("""{"Hata":[],"Liste":[]}""");
        var act = () => client.ListTransactionsAsync(Creds(), new DateOnly(2022, 1, 1), new DateOnly(2022, 2, 5), null, 1, 1000);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        h.Last.Should().BeNull("sunucuya hiç gidilmedi");
    }

    [Fact]
    public async Task Banka_baglantisi_ekle_form_alanlarini_bankaya_gore_iletir()
    {
        var (client, h) = Build("""{"Hata":[]}""");
        var form = new Dictionary<string, string>
        {
            ["BankaApiAdi"] = "OrderDeck-test", ["KullaniciAdi"] = $"u-{Guid.NewGuid():N}",
            ["Sifre"] = $"pw-{Guid.NewGuid():N}", ["Url"] = "https://example.invalid/wsdl",
        };

        await client.AddBankConnectionAsync(Creds(), "qnb", form);

        h.Last!.RequestUri!.ToString().Should().EndWith("/webservis/bankaapi/ekle/qnb/");
        h.LastForm.Should().Contain("BankaApiAdi=OrderDeck-test").And.Contain("Url=");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle** (`--filter "FullyQualifiedName~ObifinClientTests"`).

- [ ] **Adım 3: Options, sözleşme, istemci**

`OrderDeck.LicenseServer/Services/Bank/ObifinOptions.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>`Obifin` bölümü. Kimlikler burada DEĞİL (lisans başına DB'de, şifreli).</summary>
public sealed class ObifinOptions
{
    /// <summary>Bağlantı kaydında BaseUrl boşsa kullanılır.</summary>
    public string DefaultBaseUrl { get; set; } = "https://prodapio2.obifin.com";
    public int TimeoutSeconds { get; set; } = 40;
    /// <summary>Hareket sorgusunda API tavanı (25.09 ölçümü: 2000 istenince 1000 döndü).</summary>
    public int PageSize { get; set; } = 1000;
}
```

`OrderDeck.LicenseServer/Services/Bank/IObifinClient.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çağrı başına çözülen kimlik; loglanmaz, saklanmaz.</summary>
public sealed record ObifinCredentials(string BaseUrl, string UserCode, string Password, string ApiKey);

public sealed record ObifinAccountDto(
    long Id, string BankaKodu, long? BankaApiId, string? HesapNo, string? Iban, string Currency,
    decimal? Balance, DateTime? UpdatedAtTr, bool Active, string? NotificationNote);

public sealed record ObifinBankConnectionDto(long BankaApiId, string BankaKodu, string? Name, bool Active);

/// <summary>Tek hareket. Tarih TR yerel (dönüşüm çağıranda); tutar işaretli.</summary>
public sealed record ObifinTransactionDto(
    long Id, long AccountId, string BankaKodu, DateTime OccurredAtTr, decimal SignedAmount, string Currency,
    string? Description, string? TransactionCode, string? CommonType, string? BankReference,
    string? CounterpartyIban, string? CounterpartyName, string? CounterpartyTaxId, string RawJson);

public sealed record ObifinPage<T>(IReadOnlyList<T> Items, int PageNo, int? TotalPages, int? TotalCount, int PageSize);

/// <summary>Obifin `Hata[]` dolu döndü — mesajlar Obifin'in Türkçe metinleri.</summary>
public sealed class ObifinApiException : Exception
{
    public IReadOnlyList<string> Messages { get; }
    public ObifinApiException(IReadOnlyList<string> messages)
        : base("Obifin: " + string.Join(" | ", messages)) => Messages = messages;
}

/// <summary>JSON değil / beklenen şekil değil (ör. vekil 502 HTML).</summary>
public sealed class ObifinProtocolException : Exception
{
    public ObifinProtocolException(string message) : base(message) { }
}

public interface IObifinClient
{
    Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials creds, CancellationToken ct = default);
    Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials creds, CancellationToken ct = default);
    /// <summary>`bankaapi/ekle/{bankaKodu}/`; form = bankaya özel alanlar (+ BankaApiAdi). Başarı = Hata boş.</summary>
    Task AddBankConnectionAsync(ObifinCredentials creds, string bankaKodu, IReadOnlyDictionary<string, string> form, CancellationToken ct = default);
    Task RemoveBankConnectionAsync(ObifinCredentials creds, long bankaApiId, CancellationToken ct = default);
    /// <summary>Aralık ≤ 31 gün (aksi ArgumentOutOfRange); `sinceId` = yalnız Id &gt; değer.</summary>
    Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(
        ObifinCredentials creds, DateOnly fromTr, DateOnly toTr, long? sinceId, int pageNo, int pageSize,
        CancellationToken ct = default);
}
```

`OrderDeck.LicenseServer/Services/Bank/ObifinClient.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Obifin web servisi (doküman v1.03.04). Kimlik header'da, gövde form-urlencoded, HTTP 200 +
/// `Hata:[]` başarı. Sayısal alanlar STRING ("10.00"), tarih "yyyy-MM-dd HH:mm:ss" TR yerel.
/// Ham gövde yalnız tanı kopyası olarak kesilir — ayrıştırma TAM gövdeden (İYS 2026-09-22 dersi).
/// </summary>
public sealed class ObifinClient : IObifinClient
{
    public const int MaxRangeDays = 31;
    private const int DiagnosticCap = 2000;

    private readonly HttpClient _http;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinClient> _log;

    public ObifinClient(HttpClient http, IOptions<ObifinOptions> opt, ILogger<ObifinClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials creds, CancellationToken ct = default)
    {
        using var doc = await PostAsync(creds, "/webservis/hesaplar/hesaplistesi/", null, ct);
        return ReadList(doc, row => new ObifinAccountDto(
            Id: Long(row, "Id") ?? throw new ObifinProtocolException("hesap Id yok"),
            BankaKodu: Str(row, "BankaKodu") ?? "",
            BankaApiId: Long(row, "BankaApiId"),
            HesapNo: Str(row, "HesapNo"),
            Iban: Str(row, "IBAN"),
            Currency: Str(row, "ParaBirimi") ?? "TL",
            Balance: Dec(row, "Bakiye"),
            UpdatedAtTr: DateTr(row, "GuncellemeTarihi"),
            Active: Str(row, "Durum") == "1",
            NotificationNote: Str(row, "BildirimNotu")));
    }

    public async Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials creds, CancellationToken ct = default)
    {
        using var doc = await PostAsync(creds, "/webservis/bankaapi/liste/", null, ct);
        return ReadList(doc, row => new ObifinBankConnectionDto(
            BankaApiId: Long(row, "BankaApiId") ?? 0,
            BankaKodu: Str(row, "BankaKodu") ?? "",
            Name: Str(row, "BankaApiAdi"),
            Active: Str(row, "Durum") is null or "1"));
    }

    public async Task AddBankConnectionAsync(ObifinCredentials creds, string bankaKodu, IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bankaKodu) || bankaKodu.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Banka kodu yalnız harf/rakam olabilir.", nameof(bankaKodu));
        using var _ = await PostAsync(creds, $"/webservis/bankaapi/ekle/{bankaKodu}/", form, ct);
    }

    public async Task RemoveBankConnectionAsync(ObifinCredentials creds, long bankaApiId, CancellationToken ct = default)
    {
        using var _ = await PostAsync(creds, "/webservis/bankaapi/sil/",
            new Dictionary<string, string> { ["BankaApiId"] = bankaApiId.ToString(CultureInfo.InvariantCulture) }, ct);
    }

    public async Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(
        ObifinCredentials creds, DateOnly fromTr, DateOnly toTr, long? sinceId, int pageNo, int pageSize,
        CancellationToken ct = default)
    {
        if (toTr < fromTr) throw new ArgumentOutOfRangeException(nameof(toTr), "Bitiş başlangıçtan önce.");
        if (toTr.DayNumber - fromTr.DayNumber + 1 > MaxRangeDays)
            throw new ArgumentOutOfRangeException(nameof(toTr), "Obifin tarih aralığı 31 günü aşamaz.");
        if (pageNo < 1) throw new ArgumentOutOfRangeException(nameof(pageNo));

        var form = new Dictionary<string, string>
        {
            ["BaslangicTarihi"] = fromTr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["BitisTarihi"] = toTr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["SayfaBasinaKayitSayisi"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["SayfaNo"] = pageNo.ToString(CultureInfo.InvariantCulture),
        };
        if (sinceId is { } s) form["BaslangicHareketId"] = s.ToString(CultureInfo.InvariantCulture);

        using var doc = await PostAsync(creds, "/webservis/hesaphareketleri/hesaphareketleriliste/", form, ct);
        var root = doc.RootElement;
        var items = ReadList(doc, row =>
        {
            var signed = Dec(row, "TutarEksiArti") ?? Dec(row, "Tutar")
                ?? throw new ObifinProtocolException("hareket tutarı yok");
            return new ObifinTransactionDto(
                Id: Long(row, "Id") ?? throw new ObifinProtocolException("hareket Id yok"),
                AccountId: Long(row, "HesapId") ?? 0,
                BankaKodu: Str(row, "BankaKodu") ?? "",
                OccurredAtTr: DateTr(row, "IslemZamaniDT") ?? throw new ObifinProtocolException("IslemZamaniDT yok"),
                SignedAmount: signed,
                Currency: Str(row, "ParaBirimi") ?? "TL",
                Description: Str(row, "Aciklama") ?? Str(row, "IslemAciklama"),
                TransactionCode: Str(row, "IslemKodu"),
                CommonType: Str(row, "OrtakIslemTipi"),
                BankReference: Str(row, "IslemNo"),
                CounterpartyIban: Str(row, "KarsiHesapIBAN"),
                CounterpartyName: Str(row, "GonderenAdi"),
                CounterpartyTaxId: Str(row, "BorcluVKN") ?? Str(row, "AmirVKN") ?? Str(row, "LehdarTCKN"),
                RawJson: row.GetRawText());
        });
        return new ObifinPage<ObifinTransactionDto>(items,
            PageNo: Int(root, "SayfaNo") ?? pageNo,
            TotalPages: Int(root, "ToplamSayfaSayisi"),
            TotalCount: Int(root, "ToplamKayitSayisi"),
            PageSize: Int(root, "SayfaBasinaKayitSayisi") ?? pageSize);
    }

    // ---- ortak ----

    private async Task<JsonDocument> PostAsync(ObifinCredentials creds, string path,
        IReadOnlyDictionary<string, string>? form, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(creds.BaseUrl) ? _opt.DefaultBaseUrl : creds.BaseUrl;
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + path)
        {
            Content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>()),
        };
        req.Headers.TryAddWithoutValidation("KullaniciAdi", creds.UserCode);
        req.Headers.TryAddWithoutValidation("Sifre", creds.Password);
        req.Headers.TryAddWithoutValidation("APIKey", creds.ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            _log.LogWarning("Obifin JSON olmayan yanıt ({Status}) {Path}: {Head}", (int)resp.StatusCode, path, Diagnostic(body));
            throw new ObifinProtocolException($"Obifin JSON olmayan yanıt ({(int)resp.StatusCode}) {path}");
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new ObifinProtocolException($"Obifin beklenmeyen JSON kökü {path}");
        }
        if (doc.RootElement.TryGetProperty("Hata", out var hata) && hata.ValueKind == JsonValueKind.Array && hata.GetArrayLength() > 0)
        {
            var msgs = hata.EnumerateArray().Select(e => e.ToString()).ToList();
            doc.Dispose();
            throw new ObifinApiException(msgs);
        }
        return doc;
    }

    private static IReadOnlyList<T> ReadList<T>(JsonDocument doc, Func<JsonElement, T> map)
    {
        if (!doc.RootElement.TryGetProperty("Liste", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<T>();
        return list.EnumerateArray().Select(map).ToList();
    }

    private static string? Str(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Long(JsonElement row, string name)
        => long.TryParse(Str(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? Int(JsonElement row, string name)
        => int.TryParse(Str(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static decimal? Dec(JsonElement row, string name)
        => decimal.TryParse(Str(row, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime? DateTr(JsonElement row, string name)
        => DateTime.TryParseExact(Str(row, name), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    private static string Diagnostic(string body) => body.Length > DiagnosticCap ? body[..DiagnosticCap] : body;
}
```

`OrderDeck.LicenseServer/Services/Bank/NullObifinClient.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Test/dev ortamı: Obifin yok. Her çağrı yapılandırma hatası döner ki iş
/// "sessizce boş" değil "açıkça kapalı" görünsün.</summary>
public sealed class NullObifinClient : IObifinClient
{
    private static ObifinApiException Off() => new(new[] { "obifin-not-configured" });
    public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Off();
    public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Off();
    public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => throw Off();
    public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => throw Off();
    public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default) => throw Off();
}
```

- [ ] **Adım 4: Koştur — YEŞİL** (`ObifinClientTests` 7/7); `dotnet build` 0 uyarı.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/ObifinOptions.cs OrderDeck.LicenseServer/Services/Bank/IObifinClient.cs OrderDeck.LicenseServer/Services/Bank/ObifinClient.cs OrderDeck.LicenseServer/Services/Bank/NullObifinClient.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinClientTests.cs
git commit -F - <<'MSG'
feat(banka): Obifin web servis istemcisi — header kimlik, form gövde, Hata sözleşmesi, 31 gün sınırı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
### Görev 3: `ObifinConnectionService` — kimlik saklama, doğrulama, banka bağlantısı, hesap yenileme

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinConnectionServiceTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class ObifinConnectionServiceTests
{
    /// <summary>Betikli sahte istemci: hesap listesi ve banka bağlantı listesi sabit, çağrılar kaydedilir.</summary>
    private sealed class StubObifin : IObifinClient
    {
        public List<ObifinAccountDto> Accounts { get; } = new();
        public List<ObifinBankConnectionDto> Connections { get; } = new();
        public List<(string Banka, IReadOnlyDictionary<string, string> Form)> Added { get; } = new();
        public ObifinCredentials? LastCreds { get; private set; }
        public Exception? ListAccountsError { get; set; }

        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            LastCreds = c;
            if (ListAccountsError is not null) throw ListAccountsError;
            return Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Accounts);
        }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(Connections);
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            Added.Add((b, f));
            // Obifin gerçekte Id döndürmüyor (doküman sessiz): listede etiketle bulunur.
            Connections.Add(new ObifinBankConnectionDto(4242, b, f["BankaApiAdi"], true));
            return Task.CompletedTask;
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => Task.FromResult(new ObifinPage<ObifinTransactionDto>(Array.Empty<ObifinTransactionDto>(), p, 0, 0, ps));
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"obifin-conn-{Guid.NewGuid():N}").Options);

    private static Guid SeedLicense(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"c-{Guid.NewGuid():N}@x", Name = "C",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.SaveChanges();
        return licenseId;
    }

    private static ObifinConnectionService Svc(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection, new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);

    [Fact]
    public async Task Kimlik_sifreli_saklanir_ve_cozulup_istemciye_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var pw = $"pw-{Guid.NewGuid():N}"; var key = $"k-{Guid.NewGuid():N}";

        var conn = await svc.UpsertAsync(lic, "https://example.invalid", "api@x", pw, key, CancellationToken.None);
        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        conn.PasswordProtected.Should().NotContain(pw);
        conn.ApiKeyProtected.Should().NotContain(key);
        stub.LastCreds!.Password.Should().Be(pw);
        stub.LastCreds.ApiKey.Should().Be(key);
        result.Ok.Should().BeTrue();
        (await db.ObifinConnections.SingleAsync()).Status.Should().Be(ObifinConnectionStatus.Verified);
    }

    [Fact]
    public async Task Dogrulama_Obifin_hatasinda_Failed_ve_mesaj_saklanir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { ListAccountsError = new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        result.Ok.Should().BeFalse();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Contain("Kullanici Bilgileri Hatali");
    }

    [Fact]
    public async Task Yeniden_kayitta_bos_sifre_eskisini_korur()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var pw = $"pw-{Guid.NewGuid():N}";
        await svc.UpsertAsync(lic, "", "api@x", pw, $"k-{Guid.NewGuid():N}", CancellationToken.None);

        await svc.UpsertAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None);
        await svc.VerifyAsync(lic, CancellationToken.None);

        stub.LastCreds!.UserCode.Should().Be("api2@x");
        stub.LastCreds.Password.Should().Be(pw, "boş şifre = değiştirme");
        (await db.ObifinConnections.CountAsync()).Should().Be(1, "lisans başına tek bağlantı");
    }

    [Fact]
    public async Task Banka_baglantisi_eklenince_BankaApiId_listeden_etiketle_bulunur_kimlik_saklanmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        var bankPw = $"pw-{Guid.NewGuid():N}";

        var bc = await svc.AddBankConnectionAsync(lic, "qnb", "QNB ana hesap",
            new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = bankPw, ["Url"] = "https://example.invalid/wsdl" },
            CancellationToken.None);

        bc.BankaApiId.Should().Be(4242);
        bc.BankaKodu.Should().Be("qnb");
        stub.Added.Single().Form["BankaApiAdi"].Should().Be(bc.Label, "Obifin'de etiket = bizim üretilmiş ad");
        var json = System.Text.Json.JsonSerializer.Serialize(await db.BankConnections.SingleAsync());
        json.Should().NotContain(bankPw, "banka web servis kimliği saklanmaz");
    }

    [Fact]
    public async Task Hesap_yenileme_maskeli_iban_ve_banka_senkron_zamanini_yazar()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var iban = BankHasherTests.TestIban();
        stub.Accounts.Add(new ObifinAccountDto(9298, "qnb", 77, "123", iban, "TL", 10m,
            new DateTime(2026, 9, 25, 10, 0, 0), true, ""));
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);
        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        var acc = (await db.BankAccounts.ToListAsync()).Should().ContainSingle("ikinci yenileme upsert").Subject;
        acc.IbanMasked.Should().Be(BankHasher.MaskIban(iban));
        acc.IbanHash.Should().NotBeNullOrEmpty().And.NotContain(iban[4..10]);
        acc.LastBankSyncAt.Should().Be(new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero), "TR 10:00 = UTC 07:00");
        acc.Active.Should().BeTrue();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle** (`--filter "FullyQualifiedName~ObifinConnectionServiceTests"`).

- [ ] **Adım 3: Servisi yaz**

`OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

public sealed record ObifinVerifyResult(bool Ok, string? Error, int AccountCount);

/// <summary>
/// Lisans başına Obifin bağlantısı: kimlikleri şifreli saklar (DataProtection, Netgsm kalıbı),
/// doğrular (`hesaplistesi`), banka bağlantısı ekler (banka kimliği SAKLANMAZ — spec §3) ve
/// hesap listesini yeniler. Çekim işi (<see cref="ObifinPollJob"/>) kimliği buradan çözer.
/// </summary>
public sealed class ObifinConnectionService
{
    private const string Purpose = "OrderDeck.Obifin.Credentials.v1";
    public const string UndecryptableMessage = "Saklı Obifin kimliği çözülemedi. Kimlik bilgilerini yeniden girin.";

    // Windows "Turkey Standard Time", Linux "Europe/Istanbul" (PanelStatsController kalıbı).
    public static readonly TimeZoneInfo TrZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly IDataProtector _protector;
    private readonly BankHasher _hasher;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinConnectionService> _log;

    public ObifinConnectionService(LicenseDbContext db, IObifinClient client, IDataProtectionProvider protection,
        BankHasher hasher, IOptions<ObifinOptions> opt, ILogger<ObifinConnectionService> log)
    {
        _db = db; _client = client; _protector = protection.CreateProtector(Purpose);
        _hasher = hasher; _opt = opt.Value; _log = log;
    }

    public static DateTimeOffset TrToUtc(DateTime trLocal)
        => new DateTimeOffset(DateTime.SpecifyKind(trLocal, DateTimeKind.Unspecified), TrZone.GetUtcOffset(trLocal)).ToUniversalTime();

    /// <summary>Boş <paramref name="password"/>/<paramref name="apiKey"/> = mevcut değeri koru.</summary>
    public async Task<ObifinConnection> UpsertAsync(Guid licenseId, string baseUrl, string userCode,
        string? password, string? apiKey, CancellationToken ct)
    {
        userCode = (userCode ?? "").Trim();
        if (userCode.Length == 0) throw new ArgumentException("Kullanıcı adı boş olamaz.", nameof(userCode));
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;
        if (conn is null)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("İlk kayıtta şifre ve API anahtarı zorunlu.");
            conn = new ObifinConnection { Id = Guid.NewGuid(), LicenseId = licenseId, CreatedAt = now, Status = ObifinConnectionStatus.Unverified };
            _db.ObifinConnections.Add(conn);
        }
        conn.BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? _opt.DefaultBaseUrl : baseUrl.Trim();
        conn.UserCode = userCode;
        if (!string.IsNullOrWhiteSpace(password)) conn.PasswordProtected = _protector.Protect(password);
        if (!string.IsNullOrWhiteSpace(apiKey)) conn.ApiKeyProtected = _protector.Protect(apiKey);
        conn.Status = conn.Status == ObifinConnectionStatus.Disabled ? ObifinConnectionStatus.Disabled : ObifinConnectionStatus.Unverified;
        conn.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return conn;
    }

    /// <summary>Şifre çözülemezse null (anahtar halkası kaybı) — çağıran Failed'a çeker.</summary>
    public ObifinCredentials? TryResolveCredentials(ObifinConnection conn)
    {
        try
        {
            return new ObifinCredentials(conn.BaseUrl, conn.UserCode,
                _protector.Unprotect(conn.PasswordProtected), _protector.Unprotect(conn.ApiKeyProtected));
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task<ObifinVerifyResult> VerifyAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn);
        var now = DateTimeOffset.UtcNow;
        if (creds is null)
        {
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = UndecryptableMessage; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return new ObifinVerifyResult(false, UndecryptableMessage, 0);
        }
        try
        {
            var accounts = await _client.ListAccountsAsync(creds, ct);
            conn.Status = ObifinConnectionStatus.Verified; conn.LastError = null; conn.LastVerifiedAt = now; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await UpsertAccountsAsync(conn, accounts, now, ct);
            return new ObifinVerifyResult(true, null, accounts.Count);
        }
        catch (Exception ex) when (ex is ObifinApiException or ObifinProtocolException or HttpRequestException or TaskCanceledException)
        {
            // Kimlik bilgisi loga girmez; Obifin'in mesajı yeter.
            var msg = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = msg; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return new ObifinVerifyResult(false, msg, 0);
        }
    }

    /// <summary>Banka kimliklerini Obifin'e iletir, listeden etiketle `BankaApiId`'yi bulur; kimlikleri saklamaz.</summary>
    public async Task<BankConnection> AddBankConnectionAsync(Guid licenseId, string bankaKodu, string label,
        IReadOnlyDictionary<string, string> bankForm, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Önce Obifin bağlantısı kaydedilmeli.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        bankaKodu = bankaKodu.Trim().ToLowerInvariant();
        // Obifin tarafında tekil, tahmin edilemez etiket: liste dönüşünde bunu ararız.
        var obifinLabel = $"OrderDeck-{licenseId.ToString("N")[..8]}-{bankaKodu}-{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
        var form = new Dictionary<string, string>(bankForm) { ["BankaApiAdi"] = obifinLabel };
        await _client.AddBankConnectionAsync(creds, bankaKodu, form, ct);
        var listed = await _client.ListBankConnectionsAsync(creds, ct);
        var match = listed.FirstOrDefault(x => string.Equals(x.Name, obifinLabel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Banka bağlantısı Obifin'de görünmedi; listeyi kontrol edin.");
        var bc = new BankConnection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, ObifinConnectionId = conn.Id, BankaKodu = bankaKodu,
            BankaApiId = match.BankaApiId, Label = string.IsNullOrWhiteSpace(label) ? obifinLabel : label.Trim(),
            Status = BankConnectionStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.BankConnections.Add(bc);
        await _db.SaveChangesAsync(ct);
        return bc;
    }

    public async Task<int> RefreshAccountsAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        var accounts = await _client.ListAccountsAsync(creds, ct);
        await UpsertAccountsAsync(conn, accounts, DateTimeOffset.UtcNow, ct);
        return accounts.Count;
    }

    private async Task UpsertAccountsAsync(ObifinConnection conn, IReadOnlyList<ObifinAccountDto> accounts,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _db.BankAccounts.Where(a => a.LicenseId == conn.LicenseId).ToListAsync(ct);
        var connections = await _db.BankConnections.Where(b => b.LicenseId == conn.LicenseId).ToListAsync(ct);
        foreach (var dto in accounts)
        {
            var row = existing.FirstOrDefault(a => a.ObifinAccountId == dto.Id);
            if (row is null)
            {
                row = new BankAccount { Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinAccountId = dto.Id };
                _db.BankAccounts.Add(row);
                existing.Add(row);
            }
            row.BankConnectionId = connections.FirstOrDefault(b => b.BankaApiId == dto.BankaApiId)?.Id;
            row.BankaKodu = dto.BankaKodu;
            row.IbanMasked = BankHasher.MaskIban(dto.Iban);
            row.IbanHash = _hasher.HashIban(dto.Iban);
            row.Currency = dto.Currency;
            row.Active = dto.Active;
            row.LastBankSyncAt = dto.UpdatedAtTr is { } tr ? TrToUtc(tr) : null;
            row.NotificationNote = dto.NotificationNote;
            row.RefreshedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Adım 4: Koştur — YEŞİL** (`ObifinConnectionServiceTests` 5/5). Not: `BankHasherTests.TestIban()` public static — Görev 1'de öyle yazıldı.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/ObifinConnectionService.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinConnectionServiceTests.cs
git commit -F - <<'MSG'
feat(banka): Obifin bağlantı servisi — şifreli kimlik, doğrulama, banka bağlantısı (kimlik saklanmaz), hesap yenileme

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
### Görev 4: Çekim, hesap yenileme ve saklama işleri

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs`, `ObifinAccountRefreshJob.cs`, `BankDataRetentionJob.cs`, `IBankTransactionSink.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs`, `BankDataRetentionJobTests.cs`

Tasarım notu: `ObifinPollJob` yeni GELEN hareketleri bir `IBankTransactionSink`'e verir; PR-1'de
`NoopBankTransactionSink`, PR-2'de eşleştirici bunu uygular (Görev 10). Böylece eşleştirme çekim
işine sonradan takılır, çekim testleri değişmez.

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

`OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class ObifinPollJobTests
{
    /// <summary>Betikli istemci: hareketler bellekte; sorgu tarih penceresi + sinceId + sayfa ile
    /// gerçek API gibi filtrelenir (Id artan). 31 gün üstü aralık gerçek istemci gibi fırlatır.</summary>
    private sealed class ScriptedObifin : IObifinClient
    {
        public List<ObifinTransactionDto> Transactions { get; } = new();
        public List<(DateOnly From, DateOnly To, long? Since, int Page)> Calls { get; } = new();
        public int FailOnCall { get; set; } = -1;
        public int PageCap { get; set; } = 1000;

        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Transactions.Select(t => t.AccountId).Distinct()
                .Select(id => new ObifinAccountDto(id, "qnb", 1, "1", BankHasherTests.TestIban(), "TL", 0, null, true, null)).ToList());
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(Array.Empty<ObifinBankConnectionDto>());
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
        {
            Calls.Add((f, t, s, p));
            if (t.DayNumber - f.DayNumber + 1 > 31) throw new ArgumentOutOfRangeException(nameof(t));
            if (Calls.Count == FailOnCall) throw new ObifinApiException(new[] { "gecici-hata" });
            var size = Math.Min(ps, PageCap);
            var all = Transactions
                .Where(x => DateOnly.FromDateTime(x.OccurredAtTr) >= f && DateOnly.FromDateTime(x.OccurredAtTr) <= t)
                .Where(x => s is null || x.Id > s)
                .OrderBy(x => x.Id).ToList();
            var items = all.Skip((p - 1) * size).Take(size).ToList();
            var pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)size));
            return Task.FromResult(new ObifinPage<ObifinTransactionDto>(items, p, pages, all.Count, size));
        }
    }

    private sealed class RecordingSink : IBankTransactionSink
    {
        public List<BankTransaction> Received { get; } = new();
        public Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct) { Received.Add(tx); return Task.CompletedTask; }
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"obifin-poll-{Guid.NewGuid():N}").Options);

    private static async Task<Guid> SeedVerifiedAsync(LicenseDbContext db, ObifinConnectionService svc, long? cursor = null, bool backfilled = false)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"c-{Guid.NewGuid():N}@x", Name = "C",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        var conn = await svc.UpsertAsync(licenseId, "", "api@x", $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}", CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        conn.LastObifinTransactionId = cursor;
        conn.BackfillCompletedAt = backfilled ? DateTimeOffset.UtcNow.AddDays(-1) : null;
        await db.SaveChangesAsync();
        return licenseId;
    }

    private static ObifinTransactionDto Tx(long id, DateTime whenTr, decimal signed, string desc = "HAVALE test", string? iban = null)
        => new(id, 6, "qnb", whenTr, signed, "TL", desc, "FT", "EFT", $"ref-{id}", iban, null, null, $"{{\"Id\":\"{id}\"}}");

    private static (ObifinPollJob Job, ObifinConnectionService Svc, RecordingSink Sink) Build(LicenseDbContext db, ScriptedObifin client, DateOnly todayTr)
    {
        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var svc = new ObifinConnectionService(db, client, Protection, hasher, Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);
        var sink = new RecordingSink();
        var job = new ObifinPollJob(db, client, svc, hasher, sink, Options.Create(new ObifinOptions()), NullLogger<ObifinPollJob>.Instance)
        { TodayTr = () => todayTr };
        return (job, svc, sink);
    }

    [Fact]
    public async Task Ilk_cekim_90_gunu_31_gunluk_dilimlerle_alir_ve_imleci_kurar()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        client.Transactions.Add(Tx(100, new DateTime(2026, 7, 1, 10, 0, 0), 50m));   // 86 gün önce
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 24, 10, 0, 0), 75m));

        await job.RunAsync(CancellationToken.None);

        client.Calls.Select(c => (c.From, c.To)).Distinct().Should().HaveCount(3, "90 gün = 3 dilim");
        client.Calls.Should().OnlyContain(c => c.To.DayNumber - c.From.DayNumber + 1 <= 31);
        (await db.BankTransactions.CountAsync()).Should().Be(2);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.BackfillCompletedAt.Should().NotBeNull();
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastPolledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Artimli_cekim_31_gunluk_pencere_ve_imlecle_yalniz_yeni_hareketleri_yazar()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, sink) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(200, new DateTime(2026, 9, 24, 10, 0, 0), 75m));   // zaten görüldü
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));
        client.Transactions.Add(Tx(202, new DateTime(2026, 9, 25, 9, 5, 0), -30m));  // giden

        await job.RunAsync(CancellationToken.None);

        var call = client.Calls.Should().ContainSingle().Subject;
        call.Since.Should().Be(200);
        (call.To.DayNumber - call.From.DayNumber + 1).Should().Be(31);
        call.To.Should().Be(today);
        (await db.BankTransactions.CountAsync()).Should().Be(2);
        sink.Received.Should().ContainSingle(t => t.ObifinId == 201, "yalnız GELEN hareket eşleştiriciye gider");
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic)).LastObifinTransactionId.Should().Be(202);
    }

    [Fact]
    public async Task Ayni_hareket_iki_kez_gelirse_tek_satir_kalir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: null, backfilled: true);
        client.Transactions.Add(Tx(300, new DateTime(2026, 9, 20, 10, 0, 0), 10m));

        await job.RunAsync(CancellationToken.None);
        var conn = await db.ObifinConnections.SingleAsync(); conn.LastObifinTransactionId = null; await db.SaveChangesAsync(); // imleç geri sarıldı
        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Hata_olursa_imlec_ilerlemez_ve_LastError_yazilir_kosu_fail_olur()
    {
        using var db = NewDb(); var client = new ScriptedObifin { FailOnCall = 1 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc, cursor: 200, backfilled: true);
        client.Transactions.Add(Tx(201, new DateTime(2026, 9, 25, 9, 0, 0), 100m));

        var act = () => job.RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ObifinApiException>("Hangfire panosunda Failed görünmeli");
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.LastObifinTransactionId.Should().Be(200);
        conn.LastError.Should().Contain("gecici-hata");
        (await db.BankTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sayfa_dolu_donerse_ayni_kosuda_drenaj_yapilir()
    {
        using var db = NewDb(); var client = new ScriptedObifin { PageCap = 2 }; var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        for (var i = 1; i <= 5; i++) client.Transactions.Add(Tx(i, new DateTime(2026, 9, 25, 8, i, 0), 10m));

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.CountAsync()).Should().Be(5);
        (await db.ObifinConnections.SingleAsync()).LastObifinTransactionId.Should().Be(5);
    }

    [Fact]
    public async Task Tr_saat_utc_ye_cevrilir_yon_isaretten_iban_yalniz_hash_ve_maske()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        await SeedVerifiedAsync(db, svc, cursor: 0, backfilled: true);
        var iban = BankHasherTests.TestIban();
        client.Transactions.Add(Tx(7, new DateTime(2026, 9, 25, 12, 0, 0), 250m, "EFT GELEN kod123", iban));
        client.Transactions.Add(Tx(8, new DateTime(2026, 9, 25, 12, 1, 0), -40m));

        await job.RunAsync(CancellationToken.None);

        var inc = await db.BankTransactions.SingleAsync(t => t.ObifinId == 7);
        inc.Direction.Should().Be(BankTransactionDirection.Incoming);
        inc.Amount.Should().Be(250m);
        inc.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        inc.CounterpartyIbanHash.Should().HaveLength(64);
        inc.CounterpartyIbanMasked.Should().Be(BankHasher.MaskIban(iban));
        System.Text.Json.JsonSerializer.Serialize(inc).Should().NotContain(iban[4..12], "ham IBAN hiçbir alanda yok");
        (await db.BankTransactions.SingleAsync(t => t.ObifinId == 8)).Direction.Should().Be(BankTransactionDirection.Outgoing);
    }

    [Fact]
    public async Task Verified_olmayan_baglanti_atlanir()
    {
        using var db = NewDb(); var client = new ScriptedObifin(); var today = new DateOnly(2026, 9, 25);
        var (job, svc, _) = Build(db, client, today);
        var lic = await SeedVerifiedAsync(db, svc);
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == lic);
        conn.Status = ObifinConnectionStatus.Failed; await db.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        client.Calls.Should().BeEmpty();
    }
}
```

`OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankDataRetentionJobTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-ret-{Guid.NewGuid():N}").Options);

    private static BankTransaction Row(int ageDays) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), ObifinId = Random.Shared.NextInt64(1, 1_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow.AddDays(-ageDays), FetchedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
    };

    [Fact]
    public async Task Ham_json_90_gun_aciklama_180_gun_sonra_bosaltilir_tutar_ve_hash_kalir()
    {
        using var db = NewDb();
        var fresh = Row(10); var mid = Row(100); var old = Row(200);
        db.BankTransactions.AddRange(fresh, mid, old);
        await db.SaveChangesAsync();
        var job = new BankDataRetentionJob(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), NullLogger<BankDataRetentionJob>.Instance);

        await job.RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(fresh.Id))!.RawJson.Should().NotBeNull();
        var m = (await db.BankTransactions.FindAsync(mid.Id))!;
        m.RawJson.Should().BeNull(); m.Description.Should().Be("aciklama");
        var o = (await db.BankTransactions.FindAsync(old.Id))!;
        o.RawJson.Should().BeNull(); o.Description.Should().BeNull(); o.CounterpartyName.Should().BeNull();
        o.DescriptionPurgedAt.Should().NotBeNull(); o.Amount.Should().Be(10m);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**.

- [ ] **Adım 3: İşleri yaz**

`OrderDeck.LicenseServer/Services/Bank/IBankTransactionSink.cs`:

```csharp
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çekim işi her yeni GELEN hareketi (kaydedildikten sonra) buraya verir. PR-2 eşleştiriciyi takar.</summary>
public interface IBankTransactionSink
{
    Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct);
}

public sealed class NoopBankTransactionSink : IBankTransactionSink
{
    public Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct) => Task.CompletedTask;
}
```

`OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Obifin'den hareket çekimi (spec §5). Bağlantı başına: ilk çekimde 90 gün 31'lik dilimlerle,
/// sonra `[bugün−30, bugün]` + `BaslangicHareketId` imleci. Yazım idempotent
/// ((LicenseId, ObifinId) tekil); imleç yalnız tam başarılı turda ilerler; hata LastError'a
/// yazılır ve DIŞARI çıkar (Hangfire panosunda Failed). Webhook yok — bu iş tek kaynak.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class ObifinPollJob
{
    public const int BackfillDays = 90;
    public const int WindowDays = 31;
    public const int MaxDrainRounds = 10;

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly ObifinConnectionService _connections;
    private readonly BankHasher _hasher;
    private readonly IBankTransactionSink _sink;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinPollJob> _log;

    /// <summary>Test için: TR takvim günü.</summary>
    public Func<DateOnly> TodayTr { get; set; } = () =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ObifinConnectionService.TrZone));

    public ObifinPollJob(LicenseDbContext db, IObifinClient client, ObifinConnectionService connections,
        BankHasher hasher, IBankTransactionSink sink, IOptions<ObifinOptions> opt, ILogger<ObifinPollJob> log)
    {
        _db = db; _client = client; _connections = connections; _hasher = hasher; _sink = sink; _opt = opt.Value; _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var ids = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Verified)
            .Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids)
            await PollConnectionAsync(id, ct);
    }

    /// <summary>Tek bağlantı; admin "Şimdi çek" de bunu kuyruğa atar.</summary>
    public async Task PollConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn is null || conn.Status != ObifinConnectionStatus.Verified) return;
        var creds = _connections.TryResolveCredentials(conn);
        if (creds is null)
        {
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = ObifinConnectionService.UndecryptableMessage;
            conn.UpdatedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct);
            return;
        }
        var today = TodayTr();
        try
        {
            if (conn.BackfillCompletedAt is null)
            {
                long? maxId = conn.LastObifinTransactionId;
                var start = today.AddDays(-(BackfillDays - 1));
                for (var from = start; from <= today; from = from.AddDays(WindowDays))
                {
                    var to = from.AddDays(WindowDays - 1) > today ? today : from.AddDays(WindowDays - 1);
                    var seen = await FetchWindowAsync(conn, creds, from, to, sinceId: null, ct);
                    if (seen is { } s && (maxId is null || s > maxId)) maxId = s;
                }
                conn.LastObifinTransactionId = maxId;
                conn.BackfillCompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var from = today.AddDays(-(WindowDays - 1));
                for (var round = 0; round < MaxDrainRounds; round++)
                {
                    var before = conn.LastObifinTransactionId;
                    var (seen, lastPageFull) = await FetchWindowWithMetaAsync(conn, creds, from, today, before, ct);
                    if (seen is { } s && (before is null || s > before)) conn.LastObifinTransactionId = s;
                    if (!lastPageFull || conn.LastObifinTransactionId == before) break;
                }
            }
            conn.LastPolledAt = DateTimeOffset.UtcNow;
            conn.LastError = null;
            conn.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is ObifinApiException or ObifinProtocolException or HttpRequestException or TaskCanceledException)
        {
            // İmleç DEĞİŞMEDİ (yalnız yukarıdaki başarılı yolda ilerler); sonraki koşu aynı yerden dener.
            _db.ChangeTracker.Clear();
            var fresh = await _db.ObifinConnections.FirstAsync(c => c.Id == connectionId, ct);
            fresh.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            fresh.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "Obifin çekimi düştü (bağlantı {ConnectionId})", connectionId);
            throw;
        }
    }

    private async Task<long?> FetchWindowAsync(ObifinConnection conn, ObifinCredentials creds, DateOnly from, DateOnly to, long? sinceId, CancellationToken ct)
        => (await FetchWindowWithMetaAsync(conn, creds, from, to, sinceId, ct)).MaxId;

    /// <summary>Pencereyi sayfa sayfa çeker, yazar; (görülen en büyük Id, son sayfa dolu muydu) döner.</summary>
    private async Task<(long? MaxId, bool LastPageFull)> FetchWindowWithMetaAsync(ObifinConnection conn, ObifinCredentials creds,
        DateOnly from, DateOnly to, long? sinceId, CancellationToken ct)
    {
        long? maxId = null; var lastPageFull = false;
        for (var page = 1; ; page++)
        {
            var result = await _client.ListTransactionsAsync(creds, from, to, sinceId, page, _opt.PageSize, ct);
            var ordered = result.Items.OrderBy(t => t.Id).ToList();
            await UpsertAsync(conn, ordered, ct);
            if (ordered.Count > 0) maxId = Math.Max(maxId ?? 0, ordered[^1].Id);
            lastPageFull = result.Items.Count >= result.PageSize;
            var totalPages = result.TotalPages ?? 1;
            if (page >= totalPages || result.Items.Count == 0) break;
        }
        return (maxId, lastPageFull);
    }

    private async Task UpsertAsync(ObifinConnection conn, IReadOnlyList<ObifinTransactionDto> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var ids = items.Select(i => i.Id).ToList();
        var existing = await _db.BankTransactions
            .Where(t => t.LicenseId == conn.LicenseId && ids.Contains(t.ObifinId))
            .Select(t => t.ObifinId).ToHashSetAsync(ct);
        var accounts = await _db.BankAccounts.Where(a => a.LicenseId == conn.LicenseId)
            .ToDictionaryAsync(a => a.ObifinAccountId, ct);
        var now = DateTimeOffset.UtcNow;
        var fresh = new List<BankTransaction>();
        foreach (var dto in items)
        {
            if (existing.Contains(dto.Id)) continue;
            if (!accounts.TryGetValue(dto.AccountId, out var account))
            {
                // Bilinmeyen hesap: yer tutucu; saatlik yenileme (ObifinAccountRefreshJob) tamamlar.
                account = new BankAccount { Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinAccountId = dto.AccountId,
                    BankaKodu = dto.BankaKodu, IbanMasked = "?", Currency = dto.Currency, Active = false, RefreshedAt = now };
                _db.BankAccounts.Add(account);
                accounts[dto.AccountId] = account;
            }
            var tx = new BankTransaction
            {
                Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinId = dto.Id, BankAccountId = account.Id,
                ObifinAccountId = dto.AccountId, BankaKodu = dto.BankaKodu,
                Direction = dto.SignedAmount >= 0 ? BankTransactionDirection.Incoming : BankTransactionDirection.Outgoing,
                Amount = Math.Abs(dto.SignedAmount), Currency = dto.Currency,
                OccurredAt = ObifinConnectionService.TrToUtc(dto.OccurredAtTr),
                Description = Trim(dto.Description, 512), TransactionCode = Trim(dto.TransactionCode, 32),
                CommonType = Trim(dto.CommonType, 64), BankReference = Trim(dto.BankReference, 64),
                CounterpartyIbanHash = _hasher.HashIban(dto.CounterpartyIban),
                CounterpartyIbanMasked = dto.CounterpartyIban is null ? null : BankHasher.MaskIban(dto.CounterpartyIban),
                CounterpartyName = Trim(dto.CounterpartyName, 160),
                CounterpartyTaxIdHash = _hasher.HashTaxId(dto.CounterpartyTaxId),
                RawJson = dto.RawJson, FetchedAt = now,
            };
            _db.BankTransactions.Add(tx);
            fresh.Add(tx);
        }
        await _db.SaveChangesAsync(ct);
        foreach (var tx in fresh.Where(t => t.Direction == BankTransactionDirection.Incoming))
            await _sink.OnNewIncomingAsync(tx, ct);
    }

    private static string? Trim(string? s, int max) => s is null ? null : (s.Length > max ? s[..max] : s);
}
```

`OrderDeck.LicenseServer/Services/Bank/ObifinAccountRefreshJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Saatte bir hesap listesi (Durum, BildirimNotu, GuncellemeTarihi). Bir bağlantının hatası
/// diğerini durdurmaz; hata LastError'a yazılır, koşu yine de başarılı sayılır (çekim işi asıl sinyal).</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class ObifinAccountRefreshJob
{
    private readonly LicenseDbContext _db;
    private readonly ObifinConnectionService _connections;
    private readonly ILogger<ObifinAccountRefreshJob> _log;

    public ObifinAccountRefreshJob(LicenseDbContext db, ObifinConnectionService connections, ILogger<ObifinAccountRefreshJob> log)
    { _db = db; _connections = connections; _log = log; }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var licenseIds = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Verified).Select(c => c.LicenseId).ToListAsync(ct);
        var ok = 0;
        foreach (var licenseId in licenseIds)
        {
            try { await _connections.RefreshAccountsAsync(licenseId, ct); ok++; }
            catch (Exception ex) when (ex is ObifinApiException or ObifinProtocolException or HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex, "Obifin hesap yenileme düştü (lisans {LicenseId})", licenseId);
                var conn = await _db.ObifinConnections.FirstAsync(c => c.LicenseId == licenseId, ct);
                conn.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                conn.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        return ok;
    }
}
```

`OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs`:

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Spec §5/§7: ham JSON 90 gün, açıklama/gönderen adı 180 gün; tutar, tarih, hash'ler kalır.
/// Faz 1'de kanıt metni (PaymentMatch.Evidence) de 180 günde boşaltılır.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankDataRetentionJob
{
    private readonly LicenseDbContext _db;
    private readonly BankOptions _opt;
    private readonly ILogger<BankDataRetentionJob> _log;

    public BankDataRetentionJob(LicenseDbContext db, IOptions<BankOptions> opt, ILogger<BankDataRetentionJob> log)
    { _db = db; _opt = opt.Value; _log = log; }

    public async Task<(int RawPurged, int DescriptionPurged)> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rawCutoff = now.AddDays(-_opt.RawJsonRetentionDays);
        var descCutoff = now.AddDays(-_opt.DescriptionRetentionDays);

        // InMemory sağlayıcı ExecuteUpdate desteklemez; satır sayısı küçük (günlük artış), döngü yeterli.
        var raw = await _db.BankTransactions.Where(t => t.RawJson != null && t.FetchedAt < rawCutoff).ToListAsync(ct);
        foreach (var t in raw) t.RawJson = null;
        var desc = await _db.BankTransactions
            .Where(t => t.DescriptionPurgedAt == null && t.FetchedAt < descCutoff).ToListAsync(ct);
        foreach (var t in desc) { t.Description = null; t.CounterpartyName = null; t.DescriptionPurgedAt = now; }
        var evidence = await _db.PaymentMatches.Where(m => m.Evidence != null && m.CreatedAt < descCutoff).ToListAsync(ct);
        foreach (var m in evidence) m.Evidence = null;
        await _db.SaveChangesAsync(ct);
        if (raw.Count + desc.Count > 0)
            _log.LogInformation("Banka veri saklama: {Raw} ham JSON, {Desc} açıklama boşaltıldı", raw.Count, desc.Count);
        return (raw.Count, desc.Count);
    }
}
```

- [ ] **Adım 4: Koştur — YEŞİL** (`ObifinPollJobTests` 7/7, `BankDataRetentionJobTests` 1/1); build 0 uyarı.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/IBankTransactionSink.cs OrderDeck.LicenseServer/Services/Bank/ObifinPollJob.cs OrderDeck.LicenseServer/Services/Bank/ObifinAccountRefreshJob.cs OrderDeck.LicenseServer/Services/Bank/BankDataRetentionJob.cs OrderDeck.LicenseServer.Tests/Services/Bank/ObifinPollJobTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankDataRetentionJobTests.cs
git commit -F - <<'MSG'
feat(banka): Obifin çekim işi (31 gün pencere + imleç, idempotent), hesap yenileme ve saklama işleri

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
### Görev 5: Program.cs — options, DI, HttpClient, zamanlama

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Program.cs`, `OrderDeck.LicenseServer/appsettings.json`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankJobsDiTests.cs`

- [ ] **Adım 1: DI testini yaz (kırmızı)**

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankJobsDiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BankJobsDiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Banka_isleri_ve_servisleri_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ObifinPollJob>().Should().NotBeNull();
        sp.GetRequiredService<ObifinAccountRefreshJob>().Should().NotBeNull();
        sp.GetRequiredService<BankDataRetentionJob>().Should().NotBeNull();
        sp.GetRequiredService<ObifinConnectionService>().Should().NotBeNull();
        sp.GetRequiredService<BankHasher>().Should().NotBeNull("Testing'de HashKey appsettings'ten gelir");
        sp.GetRequiredService<IObifinClient>().Should().BeOfType<NullObifinClient>("test ortamı Obifin'e bağlanmaz");
        sp.GetRequiredService<IBankTransactionSink>().Should().NotBeNull();
    }
}
```

- [ ] **Adım 2: Koştur — KIRMIZI** (`No service for type 'ObifinPollJob'`).

- [ ] **Adım 3: Program.cs + appsettings**

`appsettings.json` — `"Netgsm": {` bloğunun üstüne:

```json
  "Obifin": {
    "DefaultBaseUrl": "https://prodapio2.obifin.com",
    "TimeoutSeconds": 40,
    "PageSize": 1000
  },
  "OrderDeck": {
    "Bank": {
      "HashKey": "",
      "ExcludedTransactionCodes": [ "CCP" ],
      "RawJsonRetentionDays": 90,
      "DescriptionRetentionDays": 180
    }
  },
```

(Var olan bir `"OrderDeck"` kökü varsa `"Bank"`i onun içine ekle — iki kök JSON'da geçersiz. Prod
`.env`: `OrderDeck__Bank__HashKey=<32+ rastgele karakter>` — Bitwarden'a da yaz. **Testing:** `ApiFactory`
`UseSetting("OrderDeck:Bank:HashKey", "test-key-" + Guid)` ile doldurur — `ApiFactory.cs`'te
`builder.UseEnvironment("Testing")` satırının altına: `builder.UseSetting("OrderDeck:Bank:HashKey", $"test-{Guid.NewGuid():N}{Guid.NewGuid():N}");`)

`Program.cs` — `builder.Services.Configure<...NetgsmOptions>` satırının (≈58) altına:

```csharp
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Bank.ObifinOptions>(
            builder.Configuration.GetSection("Obifin"));
        builder.Services.Configure<OrderDeck.LicenseServer.Services.Bank.BankOptions>(
            builder.Configuration.GetSection("OrderDeck:Bank"));
```

`IysMirrorSyncJob` AddScoped satırının (≈212) altına:

```csharp
        // Banka hareketi çekimi (Obifin) — spec docs/superpowers/specs/2026-09-25-obifin-*.md.
        // Obifin'e yalnız beyaz listedeki VPS IP'sinden ulaşılır; Testing ortamı ve yerel geliştirme
        // NullObifinClient ile "obifin-not-configured" görür, sessizce boş dönmez.
        if (builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Bank.IObifinClient,
                OrderDeck.LicenseServer.Services.Bank.NullObifinClient>();
        }
        else
        {
            var obifinTimeout = builder.Configuration.GetValue("Obifin:TimeoutSeconds", 40);
            builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Bank.IObifinClient,
                    OrderDeck.LicenseServer.Services.Bank.ObifinClient>(
                    c => c.Timeout = TimeSpan.FromSeconds(obifinTimeout <= 0 ? 40 : obifinTimeout));
        }
        builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Bank.BankHasher>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.ObifinConnectionService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.IBankTransactionSink,
            OrderDeck.LicenseServer.Services.Bank.NoopBankTransactionSink>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.ObifinPollJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.ObifinAccountRefreshJob>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.BankDataRetentionJob>();
```

Recurring — `iys-mirror-sync` bloğunun altına, aynı non-Testing blok içinde
(önce `grep -n '"\*/5 \* \* \* \*"\|"20 \* \* \* \*"\|"55 4 \* \* \*"\|obifin' Program.cs` ile id/slot çakışması olmadığını doğrula):

```csharp
            // Obifin banka hareketi çekimi: 5 dakikada bir, bağlantı başına imleçli (spec §5).
            // Webhook yok; gecikme = 5 dk + Obifin'in bankadan çekme aralığı (hesap listesinde ölçülür).
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Bank.ObifinPollJob>(
                "obifin-poll",
                j => j.RunAsync(CancellationToken.None),
                "*/5 * * * *");
            // Hesap listesi (Durum/BildirimNotu/GuncellemeTarihi) saatte bir; :20, */5 ile çakışsa da hafif.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Bank.ObifinAccountRefreshJob>(
                "obifin-accounts",
                j => j.RunAsync(CancellationToken.None),
                "20 * * * *");
            // Ham JSON 90 gün, açıklama 180 gün (spec §5). 04:55 UTC: 04:52 eşitlemeden sonra, boş slot.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Bank.BankDataRetentionJob>(
                "bank-data-retention",
                j => j.RunAsync(CancellationToken.None),
                "55 4 * * *");
```

- [ ] **Adım 4: Koştur — YEŞİL** (`BankJobsDiTests` + tüm `Services/Bank` testleri); build 0 uyarı.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer/appsettings.json OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankJobsDiTests.cs
git commit -F - <<'MSG'
feat(banka): Obifin istemcisi, servisler ve işler DI'da; obifin-poll */5, obifin-accounts saatlik, saklama 04:55 UTC

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 6: Admin sayfası `Admin/Obifin` — kimlik, doğrulama, banka bağlantısı, hesaplar, "Şimdi çek"

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Pages/Admin/Obifin/Index.cshtml`, `Index.cshtml.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs` (+`AuditTargets`)
- Test: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminObifinPageTests.cs`

Kalıp: `Pages/Admin/Netgsm/Index.cshtml(.cs)` — `[Authorize]` yok (Program.cs `AuthorizeFolder("/Admin")`),
`TempData["Success"/"Error"]` şeritleri layout'tan, `IAuditService.LogAsync`. Testler
`AdminNetgsmPageTests` kalıbı: `HookedApiFactory`/`ApiFactory` + `AdminLoginHelper` ile giriş, formlar
`FormUrlEncodedContent` + antiforgery (yardımcıda nasıl alınıyorsa aynı).

- [ ] **Adım 1: Testleri yaz (kırmızı — 404)**

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public sealed class AdminObifinPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminObifinPageTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"ob-{Guid.NewGuid():N}@x", Name = "Ob",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-OB-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        return licenseId;
    }

    /// <summary>Sayfayı GET'leyip anti-forgery jetonunu forma ekler (AdminNetgsmPageTests kalıbı).</summary>
    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    [Fact]
    public async Task Girissiz_istek_login_e_yonlenir()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/admin/obifin");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Kimlik_kaydi_sifreli_saklanir_ve_sayfada_gorunmez()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var pw = $"pw-{Guid.NewGuid():N}"; var key = $"k-{Guid.NewGuid():N}";

        var post = await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BaseUrl"] = "https://example.invalid",
            ["UserCode"] = "api@x", ["Password"] = pw, ["ApiKey"] = key,
        }));
        post.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.OK);

        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().Contain("api@x").And.NotContain(pw).And.NotContain(key);
        using var scope = _factory.Services.CreateScope();
        var conn = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().ObifinConnections.SingleAsync(c => c.LicenseId == licenseId);
        conn.PasswordProtected.Should().NotContain(pw);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
    }

    [Fact]
    public async Task Dogrula_dugmesi_Null_istemcide_Failed_ve_hata_metni_gosterir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));

        await client.PostAsync("/admin/obifin?handler=Verify", await FormAsync(client, "/admin/obifin",
            new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() }));

        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().Contain("Failed").And.Contain("obifin-not-configured");
    }

    [Fact]
    public async Task Banka_baglantisi_formu_kimligi_saklamaz()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));
        var bankPw = $"pw-{Guid.NewGuid():N}";

        var resp = await client.PostAsync("/admin/obifin?handler=AddBank", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BankaKodu"] = "qnb", ["Label"] = "QNB",
            ["Field_KullaniciAdi"] = "ws-user", ["Field_Sifre"] = bankPw, ["Field_Url"] = "https://example.invalid/wsdl",
        }));

        // NullObifinClient fırlatır → TempData["Error"], kayıt yok; ama kimlik hiçbir yere yazılmamış olmalı.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankConnections.CountAsync(b => b.LicenseId == licenseId)).Should().Be(0);
        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().NotContain(bankPw);
    }
}
```

Test yardımcıları: `_factory.CreateLoggedInAdminClientAsync()` (ApiFactory uzantısı) ve sınıf içi `FormAsync` (aşağıda; `AdminLoginHelper.ExtractAntiForgeryToken` kullanır) — adlar repo'da doğrulandı.

- [ ] **Adım 2: Koştur — KIRMIZI** (404 / derleme).

- [ ] **Adım 3: Audit sabitleri**

`AuditEvents.cs` — `NetgsmAccountDisable` satırının yakınına:

```csharp
    public const string ObifinConnectionSave = "obifin.connection.save";
    public const string ObifinConnectionVerify = "obifin.connection.verify";
    public const string ObifinBankAdd = "obifin.bank.add";
    public const string ObifinPollNow = "obifin.poll.now";
```

`AuditTargets` — `NetgsmAccount` satırının altına: `public const string ObifinConnection = "obifin-connection";`

- [ ] **Adım 4: Sayfa modeli** — `Pages/Admin/Obifin/Index.cshtml.cs`:

```csharp
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Pages.Admin.Obifin;

/// <summary>Pilot yönetimi (spec §8): Obifin kimliği, doğrulama, banka bağlantısı, hesaplar, "Şimdi çek".
/// [Authorize] yok — Program.cs `AuthorizeFolder("/Admin")`. Şifre/API key asla görünüme çıkmaz.</summary>
public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly ObifinConnectionService _connections;
    private readonly IBackgroundJobClient _jobs;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, ObifinConnectionService connections, IBackgroundJobClient jobs, IAuditService audit)
    { _db = db; _connections = connections; _jobs = jobs; _audit = audit; }

    public sealed record ConnectionRow(Guid LicenseId, string CustomerEmail, string BaseUrl, string UserCode,
        ObifinConnectionStatus Status, string? LastError, DateTimeOffset? LastVerifiedAt, DateTimeOffset? LastPolledAt,
        long? Cursor, DateTimeOffset? BackfillCompletedAt, int BankConnections, int Accounts, int Transactions);
    public sealed record AccountRow(string BankaKodu, string IbanMasked, string Currency, bool Active,
        DateTimeOffset? LastBankSyncAt, string? NotificationNote);
    public sealed record LicenseOption(Guid Id, string Label);

    /// <summary>Banka kodu → Obifin'in `bankaapi/ekle` formunda beklediği alanlar (Postman v1.03.06).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> BankFields = new Dictionary<string, string[]>
    {
        ["qnb"] = ["KullaniciAdi", "Sifre", "Url"],
        ["qnbapi"] = ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"],
        ["garanti"] = ["KullaniciAdi", "Sifre", "FirmaKodu"],
        ["garantibbvaapi"] = ["TanimNumarasi"],
        ["isbank"] = ["KullaniciAdi", "Sifre"],
        ["yapikredi"] = ["KullaniciAdi", "Sifre"],
        ["ziraat"] = ["KullaniciAdi", "Sifre"],
        ["akbank"] = ["FirmaAnahtar", "KullaniciAdi", "Sifre"],
        ["papara"] = ["APIKey", "APISecret"],
    };

    [BindProperty] public Guid LicenseId { get; set; }
    [BindProperty] public string? BaseUrl { get; set; }
    [BindProperty] public string? UserCode { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? ApiKey { get; set; }
    [BindProperty] public string? BankaKodu { get; set; }
    [BindProperty] public string? Label { get; set; }

    public List<ConnectionRow> Rows { get; private set; } = new();
    public Dictionary<Guid, List<AccountRow>> AccountsByLicense { get; private set; } = new();
    public List<LicenseOption> Licenses { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rows = await _db.ObifinConnections.AsNoTracking().OrderBy(c => c.CreatedAt)
            .Select(c => new ConnectionRow(c.LicenseId,
                _db.Licenses.Where(l => l.Id == c.LicenseId).Select(l => l.Customer.Email).FirstOrDefault() ?? "(bilinmiyor)",
                c.BaseUrl, c.UserCode, c.Status, c.LastError, c.LastVerifiedAt, c.LastPolledAt, c.LastObifinTransactionId,
                c.BackfillCompletedAt,
                _db.BankConnections.Count(b => b.LicenseId == c.LicenseId && b.Status == BankConnectionStatus.Active),
                _db.BankAccounts.Count(a => a.LicenseId == c.LicenseId),
                _db.BankTransactions.Count(t => t.LicenseId == c.LicenseId)))
            .ToListAsync(ct);
        var licenseIds = Rows.Select(r => r.LicenseId).ToList();
        AccountsByLicense = (await _db.BankAccounts.AsNoTracking().Where(a => licenseIds.Contains(a.LicenseId)).ToListAsync(ct))
            .GroupBy(a => a.LicenseId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountRow(a.BankaKodu, a.IbanMasked, a.Currency, a.Active, a.LastBankSyncAt, a.NotificationNote)).ToList());
        Licenses = await _db.Licenses.AsNoTracking().OrderBy(l => l.Customer.Email)
            .Select(l => new LicenseOption(l.Id, l.Customer.Email + " · " + l.LicenseKey)).Take(200).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        try
        {
            await _connections.UpsertAsync(LicenseId, BaseUrl ?? "", UserCode ?? "", Password, ApiKey, ct);
            await _audit.LogAsync(AuditEvents.ObifinConnectionSave, AuditTargets.ObifinConnection, LicenseId.ToString(), new { UserCode }, ct);
            TempData["Success"] = "Obifin kimliği kaydedildi. Şimdi doğrulayın.";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        var result = await _connections.VerifyAsync(LicenseId, ct);
        await _audit.LogAsync(AuditEvents.ObifinConnectionVerify, AuditTargets.ObifinConnection, LicenseId.ToString(), new { result.Ok, result.AccountCount }, ct);
        if (result.Ok) TempData["Success"] = $"Doğrulandı: {result.AccountCount} hesap.";
        else TempData["Error"] = "Doğrulanamadı: " + result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddBankAsync(CancellationToken ct)
    {
        var banka = (BankaKodu ?? "").Trim().ToLowerInvariant();
        if (!BankFields.TryGetValue(banka, out var fields)) { TempData["Error"] = "Bilinmeyen banka kodu."; return RedirectToPage(); }
        // Alanlar Field_<Ad> olarak gelir; yalnız bu isteğin belleğinde yaşar, loglanmaz, saklanmaz.
        var form = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            var v = Request.Form["Field_" + f].ToString();
            if (!string.IsNullOrWhiteSpace(v)) form[f] = v.Trim();
        }
        try
        {
            var bc = await _connections.AddBankConnectionAsync(LicenseId, banka, Label ?? "", form, ct);
            await _audit.LogAsync(AuditEvents.ObifinBankAdd, AuditTargets.ObifinConnection, LicenseId.ToString(), new { banka, bc.BankaApiId }, ct);
            await _connections.RefreshAccountsAsync(LicenseId, ct);
            TempData["Success"] = $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}).";
        }
        catch (Exception ex) when (ex is ObifinApiException or ObifinProtocolException or InvalidOperationException or HttpRequestException)
        {
            TempData["Error"] = "Banka bağlantısı eklenemedi: " + ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPollNowAsync(CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.AsNoTracking().FirstOrDefaultAsync(c => c.LicenseId == LicenseId, ct);
        if (conn is null) return NotFound();
        _jobs.Enqueue<ObifinPollJob>(j => j.PollConnectionAsync(conn.Id, CancellationToken.None));
        await _audit.LogAsync(AuditEvents.ObifinPollNow, AuditTargets.ObifinConnection, LicenseId.ToString(), null, ct);
        TempData["Success"] = "Çekim kuyruğa alındı; birkaç saniye içinde tablo güncellenir.";
        return RedirectToPage();
    }
}
```

- [ ] **Adım 5: Görünüm** — `Pages/Admin/Obifin/Index.cshtml`:

```cshtml
@page "/admin/obifin"
@model IndexModel
@{
    ViewData["Title"] = "Obifin Banka Bağlantıları";
}
<h1 class="h3 mb-4">Obifin Banka Bağlantıları (pilot)</h1>

<table class="table table-sm align-middle">
    <thead><tr><th>Müşteri</th><th>Kullanıcı</th><th>Durum</th><th>Son doğrulama</th><th>Son çekim</th><th>İmleç</th><th>Banka</th><th>Hesap</th><th>Hareket</th><th>Son hata</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Rows)
    {
        <tr data-license="@r.LicenseId">
            <td>@r.CustomerEmail</td><td>@r.UserCode</td>
            <td data-cell="status">@r.Status</td>
            <td>@(r.LastVerifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")</td>
            <td>@(r.LastPolledAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")@(r.BackfillCompletedAt is null ? " (ilk çekim bekliyor)" : "")</td>
            <td>@(r.Cursor?.ToString() ?? "—")</td>
            <td>@r.BankConnections</td><td>@r.Accounts</td><td>@r.Transactions</td>
            <td class="text-danger small">@r.LastError</td>
            <td>
                <form method="post" class="d-inline">
                    <input type="hidden" name="LicenseId" value="@r.LicenseId" />
                    <button class="btn btn-sm btn-outline-primary" asp-page-handler="Verify">Doğrula</button>
                    @if (r.Status == ObifinConnectionStatus.Verified)
                    {
                        <button class="btn btn-sm btn-outline-secondary" asp-page-handler="PollNow">Şimdi çek</button>
                    }
                </form>
            </td>
        </tr>
        @if (Model.AccountsByLicense.TryGetValue(r.LicenseId, out var accounts) && accounts.Count > 0)
        {
            <tr><td colspan="11" class="small text-muted">
                @foreach (var a in accounts)
                {
                    <span class="me-3">@a.BankaKodu @a.IbanMasked (@a.Currency) @(a.Active ? "aktif" : "pasif") · banka senk. @(a.LastBankSyncAt?.ToString("yyyy-MM-dd HH:mm") ?? "—") @(string.IsNullOrEmpty(a.NotificationNote) ? "" : " ⚠ " + a.NotificationNote)</span>
                }
            </td></tr>
        }
    }
    </tbody>
</table>

<h2 class="h5 mt-4">Kimlik kaydet / güncelle</h2>
<form method="post" asp-page-handler="Save" class="row g-2" autocomplete="off">
    <div class="col-md-4"><label class="form-label">Lisans</label>
        <select name="LicenseId" class="form-select">@foreach (var l in Model.Licenses) { <option value="@l.Id">@l.Label</option> }</select></div>
    <div class="col-md-4"><label class="form-label">BaseUrl (boş = varsayılan)</label><input name="BaseUrl" class="form-control" /></div>
    <div class="col-md-4"><label class="form-label">Kullanıcı (e-posta)</label><input name="UserCode" class="form-control" required /></div>
    <div class="col-md-4"><label class="form-label">Şifre (boş = değiştirme)</label><input name="Password" type="password" class="form-control" autocomplete="new-password" /></div>
    <div class="col-md-4"><label class="form-label">API anahtarı (boş = değiştirme)</label><input name="ApiKey" type="password" class="form-control" autocomplete="new-password" /></div>
    <div class="col-md-4 align-self-end"><button class="btn btn-primary">Kaydet</button></div>
</form>

<h2 class="h5 mt-4">Banka bağlantısı ekle</h2>
<p class="small text-muted">Bankanın kurumsal web servis kimliği Obifin'e iletilir ve <strong>saklanmaz</strong>. Alanlar bankaya göre: @string.Join(" · ", IndexModel.BankFields.Select(kv => kv.Key + ": " + string.Join("/", kv.Value)))</p>
<form method="post" asp-page-handler="AddBank" class="row g-2" autocomplete="off">
    <div class="col-md-3"><label class="form-label">Lisans</label>
        <select name="LicenseId" class="form-select">@foreach (var l in Model.Licenses) { <option value="@l.Id">@l.Label</option> }</select></div>
    <div class="col-md-2"><label class="form-label">Banka kodu</label>
        <select name="BankaKodu" class="form-select">@foreach (var b in IndexModel.BankFields.Keys) { <option value="@b">@b</option> }</select></div>
    <div class="col-md-3"><label class="form-label">Etiket</label><input name="Label" class="form-control" placeholder="QNB ana hesap" /></div>
    @foreach (var f in new[] { "KullaniciAdi", "Sifre", "Url", "FirmaKodu", "FirmaAnahtar", "TanimNumarasi", "ClientId", "ClientSecret", "AccessToken", "RefreshToken", "APIKey", "APISecret" })
    {
        <div class="col-md-3"><label class="form-label">@f</label><input name="Field_@f" class="form-control" type="@(f is "Sifre" or "ClientSecret" or "APISecret" ? "password" : "text")" autocomplete="new-password" /></div>
    }
    <div class="col-md-3 align-self-end"><button class="btn btn-outline-primary">Obifin'e ekle</button></div>
</form>
```

Admin menüsüne bağlantı: `Pages/Shared/_AdminLayout.cshtml`'de `Audit Log` satırının altına `<li class="nav-item"><a class="nav-link" asp-page="/Admin/Obifin/Index">Obifin</a></li>`.

- [ ] **Adım 6: Koştur — YEŞİL** (`AdminObifinPageTests` 4/4).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Obifin OrderDeck.LicenseServer/Pages/Shared/_AdminLayout.cshtml OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminObifinPageTests.cs
git commit -F - <<'MSG'
feat(banka): admin Obifin sayfası — kimlik, doğrulama, banka bağlantısı (kimlik saklanmaz), hesaplar, şimdi çek

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 7: PR-1 tam paket + PR

- [ ] **Adım 1:** Docker açıkken tam paket: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` → tümü PASS; `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` 0 uyarı.
- [ ] **Adım 2:** Push + `gh pr create --base master` — gövde: spec linki, veri modeli, çekim algoritması (31 gün + imleç, 1000/sayfa), admin sayfası, **deploy sonrası** adımlar: `.env`'e `OrderDeck__Bank__HashKey` (Burak, Bitwarden), göç otomatik; admin → Obifin kimliği kaydet → Doğrula → QNB bağlantısı ekle → "Şimdi çek" → 90 gün backfill; Hangfire panosunda `obifin-poll` görünür. Merge Burak'ta, yayın penceresi dışında.

---
## PR-2 — Gölge eşleştirme

Dal: `feat/obifin-golge-eslestirme` (PR-1 merge edildikten sonra `origin/master`'dan).

### Görev 8: `BankTextNormalizer` — Türkçe normalizasyon ve token'lama

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/BankTextNormalizer.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/BankTextNormalizerTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Açıklama ve kullanıcı adı aynı normalizasyondan geçer: küçük harf (tr-TR), Türkçe harf
/// → ASCII, ayırıcılar boşluk. Bitişik yazım için boşluksuz birleşik metin de üretilir.</summary>
public sealed class BankTextNormalizerTests
{
    [Theory]
    [InlineData("Işıl ŞENGÜL", "isil sengul")]
    [InlineData("@Ayse_Gül.34", "ayse gul 34")]
    [InlineData("EFT-GELEN/İSTANBUL:ÖDEME", "eft gelen istanbul odeme")]
    [InlineData("  çok   boşluk ", "cok bosluk")]
    public void Normalize_turkce_harf_ve_ayiricilari_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Tokenlar_ve_bitisik_metin()
    {
        var t = BankTextNormalizer.Tokenize("HAVALE ayse_gul34 acıklama");
        t.Tokens.Should().Equal("havale", "ayse", "gul34", "aciklama");
        t.Joined.Should().Be("havaleaysegul34aciklama");
    }

    [Fact]
    public void Kullanici_adi_anahtari_ayni_kurallarla_uretilir()
    {
        BankTextNormalizer.UsernameKey("@Ayse_Gül.34").Should().Be("aysegul34", "bitişik anahtar: ayırıcısız");
        BankTextNormalizer.UsernameTokens("Ayse_Gül.34").Should().Equal("ayse", "gul", "34");
    }

    [Fact]
    public void Bos_ve_null_guvenli()
    {
        BankTextNormalizer.Normalize(null).Should().BeEmpty();
        BankTextNormalizer.Tokenize("").Tokens.Should().BeEmpty();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası.**

- [ ] **Adım 3: Yaz**

```csharp
using System.Globalization;
using System.Text;

namespace OrderDeck.LicenseServer.Services.Bank;

public readonly record struct TokenizedText(IReadOnlyList<string> Tokens, string Joined);

/// <summary>Spec §6 normalizasyonu. Deterministik ve kültür-bağımsız: bankadan gelen açıklama ile
/// yayıncının kaydettiği kullanıcı adı aynı fonksiyondan geçer, aksi halde "İ/ı" gibi farklar eşleşmeyi kırar.</summary>
public static class BankTextNormalizer
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var lower = input.ToLower(Tr);
        var sb = new StringBuilder(lower.Length);
        var lastSpace = true;
        foreach (var ch in lower)
        {
            var c = ch switch
            {
                'ı' => 'i', 'i' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch,
            };
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                sb.Append(c); lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' '); lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    public static TokenizedText Tokenize(string? input)
    {
        var norm = Normalize(input);
        var tokens = norm.Length == 0 ? Array.Empty<string>() : norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new TokenizedText(tokens, string.Concat(tokens));
    }

    /// <summary>Kullanıcı adının bitişik anahtarı ("aysegul34") — alt dize araması için.</summary>
    public static string UsernameKey(string? username) => Tokenize(username).Joined;

    public static IReadOnlyList<string> UsernameTokens(string? username)
    {
        // "gul34" değil "gul","34": harf/rakam sınırında da böl ki açıklamadaki "gul 34" ile eşleşsin.
        var result = new List<string>();
        foreach (var tok in Tokenize(username).Tokens)
        {
            var start = 0;
            for (var i = 1; i <= tok.Length; i++)
            {
                if (i == tok.Length || char.IsDigit(tok[i]) != char.IsDigit(tok[i - 1]))
                { result.Add(tok[start..i]); start = i; }
            }
        }
        return result;
    }
}
```

- [ ] **Adım 4: Koştur — YEŞİL.** Not: `Tokenize("HAVALE ayse_gul34 acıklama")` beklentisi `"gul34"` tek token (ayırıcı yok); `UsernameTokens` harf/rakam sınırında böler — iki fonksiyonun farkı bilinçli, testte ikisi de var.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/BankTextNormalizer.cs OrderDeck.LicenseServer.Tests/Services/Bank/BankTextNormalizerTests.cs
git commit -F - <<'MSG'
feat(banka): açıklama/kullanıcı adı için Türkçe normalizasyon ve token'lama

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 9: `PaymentMatcher` — üç katman, dışlama, belirsizlik

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/PaymentMatcher.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatcherTests.cs`

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Sentetik açıklamalar demo'daki maskeli kalıplardan türetildi; gerçek ad/IBAN yok.</summary>
public sealed class PaymentMatcherTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"matcher-{Guid.NewGuid():N}").Options);

    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = new string('k', 32) }));

    private static PaymentMatcher Matcher(LicenseDbContext db, params string[] excluded)
        => new(db, Hasher, Options.Create(new BankOptions { HashKey = new string('k', 32), ExcludedTransactionCodes = excluded.Length == 0 ? ["CCP"] : excluded }),
            NullLogger<PaymentMatcher>.Instance);

    private static WpfCustomerProjection Customer(LicenseDbContext db, Guid lic, string username, string? fullName = null)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, FullName = fullName, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    private static BankTransaction Incoming(LicenseDbContext db, Guid lic, string description, string? ibanHash = null, string code = "FT")
    {
        var t = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 500m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow,
            Description = description, TransactionCode = code, CounterpartyIbanHash = ibanHash, FetchedAt = DateTimeOffset.UtcNow,
        };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    [Fact]
    public async Task Aciklamadaki_kullanici_adi_tek_adayi_bulur()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var ayse = Customer(db, lic, "ayse_gul34"); Customer(db, lic, "mehmet_k");
        var tx = Incoming(db, lic, "HAVALE - AYSE GUL34 - siparis");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.Proposed);
        m.ProposedWpfCustomerId.Should().Be(ayse.Id);
        m.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription);
        m.Confidence.Should().Be(0.90m);
        m.Evidence.Should().Contain("aysegul34");
    }

    [Fact]
    public async Task Bitisik_yazim_da_eslesir_alt_dize_ile()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "burak.yildiz");
        var tx = Incoming(db, lic, "EFT GELEN burakyildiz odeme");
        (await Matcher(db).MatchAsync(tx, CancellationToken.None)).ProposedWpfCustomerId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Kisa_kullanici_adi_yalniz_tam_token_ile_eslesir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "ali");
        var tx1 = Incoming(db, lic, "HAVALE ali odeme");
        var tx2 = Incoming(db, lic, "HAVALE alim satim");
        (await Matcher(db).MatchAsync(tx1, CancellationToken.None)).ProposedWpfCustomerId.Should().Be(c.Id);
        (await Matcher(db).MatchAsync(tx2, CancellationToken.None)).Status.Should().Be(PaymentMatchStatus.NoProposal, "'alim' içinde 'ali' alt dize sayılmaz");
    }

    [Fact]
    public async Task Birden_fazla_aday_oneri_uretmez()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul"); Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse gul 34");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().StartWith("ambiguous:");
    }

    [Fact]
    public async Task Iban_hafizasi_ikinci_katman_ve_ikisi_birlikte_yuksek_guven()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "zeynep_d");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = c.Id, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();

        var onlyIban = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN aciklamasiz", hash), CancellationToken.None);
        var both = await Matcher(db).MatchAsync(Incoming(db, lic, "EFT GELEN zeynep_d", hash), CancellationToken.None);

        onlyIban.Layer.Should().Be(PaymentMatchLayer.IbanMemory); onlyIban.Confidence.Should().Be(0.85m);
        both.Layer.Should().Be(PaymentMatchLayer.UsernameInDescription); both.Confidence.Should().Be(0.98m);
    }

    [Fact]
    public async Task Iban_hafizasi_kullanici_adiyla_celisirse_oneri_yok()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var a = Customer(db, lic, "ayse_gul34"); var b = Customer(db, lic, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = b.Id, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();

        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "HAVALE ayse_gul34", hash), CancellationToken.None);

        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Contain("conflict");
    }

    [Fact]
    public async Task Ad_benzerligi_yalniz_dusuk_guvenli_oneri()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var c = Customer(db, lic, "xk_77", fullName: "Selin Kaya");
        var tx = Incoming(db, lic, "T.GARANTI BANKASI /IBAN MERKEZ SUBESI gonderilen havale SELIN KAYA odeme");

        var m = await Matcher(db).MatchAsync(tx, CancellationToken.None);

        m.Layer.Should().Be(PaymentMatchLayer.NameAmount);
        m.ProposedWpfCustomerId.Should().Be(c.Id);
        m.Confidence.Should().Be(0.50m);
    }

    [Fact]
    public async Task Pos_kodu_dislanir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34");
        var m = await Matcher(db).MatchAsync(Incoming(db, lic, "Firma:1234 (ayse_gul34) POS", code: "CCP"), CancellationToken.None);
        m.Status.Should().Be(PaymentMatchStatus.NoProposal);
        m.Evidence.Should().Be("excluded:CCP");
    }

    [Fact]
    public async Task Ayni_hareket_icin_ikinci_cagri_ayni_satiri_gunceller()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        Customer(db, lic, "ayse_gul34");
        var tx = Incoming(db, lic, "HAVALE ayse_gul34");
        await Matcher(db).MatchAsync(tx, CancellationToken.None);
        await Matcher(db).MatchAsync(tx, CancellationToken.None);
        (await db.PaymentMatches.CountAsync(m => m.BankTransactionId == tx.Id)).Should().Be(1);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası.**

- [ ] **Adım 3: Eşleştiriciyi yaz**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Gölge eşleştirme (spec §6). Yalnız <see cref="PaymentMatch"/> yazar; Payment'a dokunmaz.
/// Katmanlar: (1) açıklamada kullanıcı adı — tek aday 0.90; (2) IBAN hafızası 0.85; ikisi aynı müşteriyi
/// gösterirse 0.98; çelişirse öneri yok; (3) ad benzerliği yalnız 0.50 (17.09 kararı: tutar ayırt edici değil).
/// Belirsizlik (çoklu aday) her zaman "öneri yok" — gölge modda yanlış öneri ucuz ama ölçümü kirletir.
/// </summary>
public sealed class PaymentMatcher
{
    public const decimal UsernameConfidence = 0.90m;
    public const decimal IbanConfidence = 0.85m;
    public const decimal BothConfidence = 0.98m;
    public const decimal NameConfidence = 0.50m;
    private const int SubstringMinLength = 6;
    private const int ExactOnlyMaxLength = 3;

    private readonly LicenseDbContext _db;
    private readonly BankHasher _hasher;
    private readonly BankOptions _opt;
    private readonly ILogger<PaymentMatcher> _log;

    public PaymentMatcher(LicenseDbContext db, BankHasher hasher, IOptions<BankOptions> opt, ILogger<PaymentMatcher> log)
    { _db = db; _hasher = hasher; _opt = opt.Value; _log = log; }

    public async Task<PaymentMatch> MatchAsync(BankTransaction tx, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct)
            ?? new PaymentMatch { Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, CreatedAt = DateTimeOffset.UtcNow };
        // İnsan kararı bağlanmış satır yeniden hesaplanmaz.
        if (match.Status is PaymentMatchStatus.ConfirmedByHuman or PaymentMatchStatus.Contradicted or PaymentMatchStatus.ManualOnly)
            return match;

        var code = (tx.TransactionCode ?? "").Trim();
        if (tx.Direction != BankTransactionDirection.Incoming || _opt.ExcludedTransactionCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"excluded:{code}", PaymentMatchStatus.NoProposal);
            return await SaveAsync(match, ct);
        }

        var customers = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(c => c.LicenseId == tx.LicenseId && c.PurgedAt == null)
            .Select(c => new { c.Id, c.Username, c.FullName }).ToListAsync(ct);
        var text = BankTextNormalizer.Tokenize(tx.Description);

        // Katman 1 — kullanıcı adı.
        var byUsername = new List<(Guid Id, string Key)>();
        foreach (var c in customers)
        {
            var key = BankTextNormalizer.UsernameKey(c.Username);
            if (key.Length == 0) continue;
            var tokens = BankTextNormalizer.UsernameTokens(c.Username);
            var exactSeq = tokens.Count > 0 && ContainsSequence(text.Tokens, tokens);
            var exactToken = text.Tokens.Contains(key);
            var substring = key.Length >= SubstringMinLength && text.Joined.Contains(key, StringComparison.Ordinal);
            var hit = key.Length <= ExactOnlyMaxLength ? exactToken : (exactToken || exactSeq || substring);
            if (hit) byUsername.Add((c.Id, key));
        }

        // Katman 2 — IBAN hafızası.
        Guid? byIban = null;
        if (tx.CounterpartyIbanHash is not null)
            byIban = await _db.CustomerIbanMemories.AsNoTracking()
                .Where(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash)
                .Select(m => (Guid?)m.WpfCustomerId).FirstOrDefaultAsync(ct);

        if (byUsername.Count > 1)
        {
            Set(match, null, PaymentMatchLayer.None, 0m, $"ambiguous:{byUsername.Count}", PaymentMatchStatus.NoProposal);
            return await SaveAsync(match, ct);
        }
        if (byUsername.Count == 1)
        {
            var (id, key) = byUsername[0];
            if (byIban is { } ib && ib != id)
            {
                Set(match, null, PaymentMatchLayer.None, 0m, $"conflict:username={key},iban-memory", PaymentMatchStatus.NoProposal);
                return await SaveAsync(match, ct);
            }
            var both = byIban == id;
            Set(match, id, PaymentMatchLayer.UsernameInDescription, both ? BothConfidence : UsernameConfidence,
                both ? $"username={key};iban-memory" : $"username={key}", PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }
        if (byIban is { } ibanCustomer)
        {
            Set(match, ibanCustomer, PaymentMatchLayer.IbanMemory, IbanConfidence, "iban-memory", PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }

        // Katman 3 — ad benzerliği (yalnız öneri).
        var nameHits = customers
            .Where(c => !string.IsNullOrWhiteSpace(c.FullName))
            .Select(c => (c.Id, Name: BankTextNormalizer.Tokenize(c.FullName).Tokens))
            .Where(c => c.Name.Count >= 2 && ContainsSequence(text.Tokens, c.Name))
            .ToList();
        if (nameHits.Count == 1)
        {
            Set(match, nameHits[0].Id, PaymentMatchLayer.NameAmount, NameConfidence,
                "name=" + string.Join(' ', nameHits[0].Name), PaymentMatchStatus.Proposed);
            return await SaveAsync(match, ct);
        }
        Set(match, null, PaymentMatchLayer.None, 0m, nameHits.Count > 1 ? $"ambiguous-name:{nameHits.Count}" : "no-signal", PaymentMatchStatus.NoProposal);
        return await SaveAsync(match, ct);
    }

    private static bool ContainsSequence(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || haystack.Count < needle.Count) return false;
        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++) if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    private static void Set(PaymentMatch m, Guid? customer, PaymentMatchLayer layer, decimal confidence, string evidence, PaymentMatchStatus status)
    {
        m.ProposedWpfCustomerId = customer; m.Layer = layer; m.Confidence = confidence;
        m.Evidence = evidence.Length > 500 ? evidence[..500] : evidence; m.Status = status; m.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<PaymentMatch> SaveAsync(PaymentMatch m, CancellationToken ct)
    {
        if (_db.Entry(m).State == EntityState.Detached) _db.PaymentMatches.Add(m);
        await _db.SaveChangesAsync(ct);
        return m;
    }
}
```

Ad benzerliği için Jaro-Winkler yerine
**token dizisi içerme** seçildi: sentetik testte deterministik, harici kütüphane yok; spec §6'daki
"≥ 0.92" eşiği Faz 2'de gerçek veriye göre gerekirse eklenir (planda bilinçli sapma, spec'e not düşülecek).

- [ ] **Adım 4: Koştur — YEŞİL** (`PaymentMatcherTests` 9/9).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/PaymentMatcher.cs OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatcherTests.cs
git commit -F - <<'MSG'
feat(banka): gölge eşleştirici — kullanıcı adı, IBAN hafızası, ad katmanları; belirsizlikte öneri yok

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 10: Eşleştiriciyi çekim işine tak

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/MatchingBankTransactionSink.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (sink kaydı)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/MatchingSinkTests.cs`

- [ ] **Adım 1: Test (kırmızı)**

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class MatchingSinkTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MatchingSinkTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void DI_de_sink_eslestirici_olani()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBankTransactionSink>().Should().BeOfType<MatchingBankTransactionSink>();
    }

    [Fact]
    public async Task Sink_gelen_hareket_icin_PaymentMatch_yazar_ve_hatayi_yutar()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"sink-{Guid.NewGuid():N}").Options);
        var lic = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow });
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 1, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming,
            Amount = 100m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow, Description = "HAVALE ayse_gul34", FetchedAt = DateTimeOffset.UtcNow };
        db.BankTransactions.Add(tx); await db.SaveChangesAsync();
        var matcher = new PaymentMatcher(db, new BankHasher(Options.Create(new BankOptions { HashKey = new string('k', 32) })),
            Options.Create(new BankOptions { HashKey = new string('k', 32) }), NullLogger<PaymentMatcher>.Instance);
        var sink = new MatchingBankTransactionSink(matcher, NullLogger<MatchingBankTransactionSink>.Instance);

        await sink.OnNewIncomingAsync(tx, CancellationToken.None);

        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.Proposed);
    }
}
```

- [ ] **Adım 2: Koştur — KIRMIZI.**

- [ ] **Adım 3: Sink + kayıt**

```csharp
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çekim işinden gelen her yeni GELEN hareketi eşleştiriciye verir. Eşleştirme hatası çekimi
/// düşürmez (hareket zaten kaydedildi; öneri sonradan admin "yeniden eşle" ile üretilebilir).</summary>
public sealed class MatchingBankTransactionSink : IBankTransactionSink
{
    private readonly PaymentMatcher _matcher;
    private readonly ILogger<MatchingBankTransactionSink> _log;
    public MatchingBankTransactionSink(PaymentMatcher matcher, ILogger<MatchingBankTransactionSink> log) { _matcher = matcher; _log = log; }

    public async Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct)
    {
        try { await _matcher.MatchAsync(tx, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Gölge eşleştirme düştü (hareket {TransactionId})", tx.Id);
        }
    }
}
```

`Program.cs`: `NoopBankTransactionSink` kaydını `MatchingBankTransactionSink` ile değiştir; üstüne
`builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.PaymentMatcher>();`. `NoopBankTransactionSink`
sınıfı kalır (testlerde kullanılabilir).

- [ ] **Adım 4: Koştur — YEŞİL** (`MatchingSinkTests` + `ObifinPollJobTests` değişmeden geçer).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/MatchingBankTransactionSink.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/MatchingSinkTests.cs
git commit -F - <<'MSG'
feat(banka): çekim işi yeni gelen hareketi gölge eşleştiriciye verir

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 11: `PaymentMatchReconciler` — insan kararıyla karşılaştırma, IBAN öğrenme, elle eşleme, gap çözümü

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconciler.cs`
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs` (ctor + `Approve` kancası),
  `OrderDeck.LicenseServer/Services/Bank/MatchingBankTransactionSink.cs` (gap çözümü), `Program.cs` (DI),
  `OrderDeck.LicenseServer.Tests/Services/Bank/MatchingSinkTests.cs` (ctor)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatchReconcilerTests.cs`

Yalnız **onay** kararı bağlanır; ret öğretmez ve bağlanmaz (spec §6 "ret öğretmez"; reddedilen dekontun
"gerçek müşterisi" yoktur — bilinçli daraltma, spec'e not düşülür, bkz. Görev 13). `PanelPaymentsController`
başka yerde `new` ile kurulmuyor (grep doğrulandı), ctor'a servis eklenebilir. Mevcut entity alanları
doğrulandı: `Shopper` (FullName, Phone, PasswordHash, Address — CreatedAt YOK), `ShopperBroadcasterLink`
(ShopperId, LicenseId, Platform, Username, WpfCustomerId?, JoinedAt), `Payment` (ShopperId?, PayerName, Amount,
PaidAt, ReferansNo, Status, ApprovedAt, CreatedAt, UpdatedAt).

- [ ] **Adım 1: Testi yaz (kırmızı — derlenmez)**

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class PaymentMatchReconcilerTests
{
    private static readonly BankHasher Hasher = new(Options.Create(new BankOptions { HashKey = new string('k', 32) }));

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"recon-{Guid.NewGuid():N}").Options);

    private static PaymentMatchReconciler Recon(LicenseDbContext db)
        => new(db, new PaymentMatcher(db, Hasher, Options.Create(new BankOptions { HashKey = new string('k', 32) }), NullLogger<PaymentMatcher>.Instance),
            NullLogger<PaymentMatchReconciler>.Instance);

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid WpfCustomerId);

    private static Seed SeedShopper(LicenseDbContext db, string username = "ayse_gul34")
    {
        var lic = Guid.NewGuid(); var shopperId = Guid.NewGuid();
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, FullName = "Ayse Gul", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        db.Shoppers.Add(new Shopper { Id = shopperId, FullName = "Ayse Gul", Phone = $"+9050{Random.Shared.Next(10000000, 99999999)}", PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-" });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink { Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = lic, Platform = "youtube", Username = username, WpfCustomerId = wpf.Id, JoinedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        return new Seed(lic, shopperId, wpf.Id);
    }

    private static WpfCustomerProjection OtherCustomer(LicenseDbContext db, Guid lic, string username)
    {
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = username, UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(c); db.SaveChanges();
        return c;
    }

    private static BankTransaction Tx(LicenseDbContext db, Guid lic, decimal amount, DateTimeOffset when, string desc, string? ibanHash = null)
    {
        var t = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = amount, Currency = "TL", OccurredAt = when, Description = desc, CounterpartyIbanHash = ibanHash, FetchedAt = when };
        db.BankTransactions.Add(t); db.SaveChanges();
        return t;
    }

    private static Payment Approved(LicenseDbContext db, Guid lic, Guid shopperId, decimal amount, DateTimeOffset paidAt, string payer = "AYSE GUL")
    {
        var p = new Payment { Id = Guid.NewGuid(), LicenseId = lic, ShopperId = shopperId, PayerName = payer, Amount = amount, PaidAt = paidAt, ReferansNo = $"r-{Guid.NewGuid():N}",
            Status = PaymentStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Payments.Add(p); db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task Oneri_insan_karariyla_ayni_musteriyse_ConfirmedByHuman_ve_iban_ogrenilir()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        var tx = Tx(db, s.LicenseId, 500m, when, "HAVALE ayse_gul34", hash);
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 500m, when.AddMinutes(10));

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var m = await db.PaymentMatches.SingleAsync();
        m.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
        m.PaymentId.Should().Be(payment.Id); m.ActualWpfCustomerId.Should().Be(s.WpfCustomerId); m.DecidedAt.Should().NotBeNull();
        var mem = await db.CustomerIbanMemories.SingleAsync();
        mem.LearnedFrom.Should().Be(IbanMemorySource.HumanApproval); mem.SourceBankTransactionId.Should().Be(tx.Id);
    }

    [Fact]
    public async Task Oneri_farkli_musteriyse_Contradicted()
    {
        using var db = NewDb(); var s = SeedShopper(db, "ayse_gul34");
        OtherCustomer(db, s.LicenseId, "mehmet_k");
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 250m, when, "HAVALE mehmet_k");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 250m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.Contradicted);
    }

    [Fact]
    public async Task Oneri_yokken_insan_eslerse_ManualOnly()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        var tx = Tx(db, s.LicenseId, 300m, when, "EFT GELEN aciklamasiz");
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 300m, when);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.ManualOnly);
    }

    [Fact]
    public async Task Aday_hareket_yoksa_gap_yazilir_ve_tekrar_cagri_ikinci_gap_yazmaz()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 999m, DateTimeOffset.UtcNow);
        var recon = Recon(db);

        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.SingleAsync();
        gap.PaymentId.Should().Be(payment.Id); gap.Reason.Should().Be(PaymentMatchGapReason.NoCandidate); gap.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task Ayni_tutarli_iki_aday_gonderen_adiyla_ayrilir_ayrilamazsa_gap()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var when = DateTimeOffset.UtcNow.AddHours(-2);
        var a = Tx(db, s.LicenseId, 100m, when, "HAVALE AYSE GUL odeme");
        var b = Tx(db, s.LicenseId, 100m, when.AddMinutes(5), "HAVALE MEHMET KAYA odeme");
        var recon = Recon(db);
        await recon.Matcher.MatchAsync(a, CancellationToken.None); await recon.Matcher.MatchAsync(b, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 100m, when, payer: "AYSE GUL"), CancellationToken.None);
        (await db.PaymentMatches.SingleAsync(m => m.BankTransactionId == a.Id)).PaymentId.Should().NotBeNull();
        (await db.PaymentMatches.SingleAsync(m => m.BankTransactionId == b.Id)).PaymentId.Should().BeNull();

        var c = Tx(db, s.LicenseId, 200m, when, "EFT 1"); var d = Tx(db, s.LicenseId, 200m, when, "EFT 2");
        await recon.Matcher.MatchAsync(c, CancellationToken.None); await recon.Matcher.MatchAsync(d, CancellationToken.None);
        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 200m, when, payer: "BILINMEYEN"), CancellationToken.None);
        (await db.PaymentMatchGaps.SingleAsync()).Reason.Should().Be(PaymentMatchGapReason.AmbiguousCandidates);
    }

    [Fact]
    public async Task Gecikmeli_gelen_hareket_acik_gap_i_cozer()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var paidAt = DateTimeOffset.UtcNow.AddHours(-5);
        var payment = Approved(db, s.LicenseId, s.ShopperId, 750m, paidAt);
        var recon = Recon(db);
        await recon.ReconcileApprovalAsync(payment, CancellationToken.None);
        (await db.PaymentMatchGaps.SingleAsync()).ResolvedAt.Should().BeNull();

        var late = Tx(db, s.LicenseId, 750m, paidAt.AddHours(4), "HAVALE ayse_gul34");
        await recon.Matcher.MatchAsync(late, CancellationToken.None);
        await recon.TryResolveGapAsync(late, CancellationToken.None);

        var gap = await db.PaymentMatchGaps.SingleAsync();
        gap.ResolvedAt.Should().NotBeNull(); gap.ResolvedBankTransactionId.Should().Be(late.Id);
        var m = await db.PaymentMatches.SingleAsync(x => x.BankTransactionId == late.Id);
        m.PaymentId.Should().Be(payment.Id); m.Status.Should().Be(PaymentMatchStatus.ConfirmedByHuman);
    }

    [Fact]
    public async Task Elle_esleme_ogretir_kaldirma_hafiza_satirini_siler()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        var tx = Tx(db, s.LicenseId, 400m, DateTimeOffset.UtcNow, "EFT GELEN", hash);
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);
        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.ManualOnly);
        (await db.CustomerIbanMemories.SingleAsync()).LearnedFrom.Should().Be(IbanMemorySource.ManualMatch);

        await recon.UnmatchAsync(s.LicenseId, tx.Id, CancellationToken.None);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0, "geri alma satırı SİLER");
        var m = await db.PaymentMatches.SingleAsync();
        m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ActualWpfCustomerId.Should().BeNull(); m.DecidedAt.Should().BeNull();
    }

    [Fact]
    public async Task Baska_musteriye_ait_iban_hafizasi_onayla_ezilmez()
    {
        using var db = NewDb(); var s = SeedShopper(db);
        var other = OtherCustomer(db, s.LicenseId, "mehmet_k");
        var hash = Hasher.HashIban(BankHasherTests.TestIban())!;
        db.CustomerIbanMemories.Add(new CustomerIbanMemory { Id = Guid.NewGuid(), LicenseId = s.LicenseId, WpfCustomerId = other.Id, IbanHash = hash, IbanMasked = "TR..", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        var when = DateTimeOffset.UtcNow;
        var tx = Tx(db, s.LicenseId, 50m, when, "EFT GELEN", hash);
        var recon = Recon(db); await recon.Matcher.MatchAsync(tx, CancellationToken.None);

        await recon.ReconcileApprovalAsync(Approved(db, s.LicenseId, s.ShopperId, 50m, when), CancellationToken.None);

        (await db.CustomerIbanMemories.SingleAsync()).WpfCustomerId.Should().Be(other.Id, "çelişki loglanır, sessizce ezilmez");
        (await db.PaymentMatches.SingleAsync()).Status.Should().Be(PaymentMatchStatus.Contradicted);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası.**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentMatchReconcilerTests"`
Expected: `PaymentMatchReconciler` bulunamadı (CS0246).

- [ ] **Adım 3: Servisi yaz**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İnsan kararını gölge önerisine bağlar (spec §6). Aday = aynı lisans, gelen, tutar eşit,
/// ±2 gün, henüz bağlanmamış. Tek aday → bağla; çoklu → gönderen adının tüm tokenları açıklamada
/// geçen TEK aday; hâlâ çoklu/yok → PaymentMatchGap. Onay IBAN öğretir; ret hiçbir şey yapmaz.
/// Elle eşleme öğretir; kaldırma hafıza satırını SİLER ve öneriyi yeniden hesaplar.
/// Sonradan gelen hareket açık gap'i çözer (spec §3 ResolvedBankTransactionId).
/// </summary>
public sealed class PaymentMatchReconciler
{
    public static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(2);

    private readonly LicenseDbContext _db;
    private readonly ILogger<PaymentMatchReconciler> _log;
    public PaymentMatcher Matcher { get; }

    public PaymentMatchReconciler(LicenseDbContext db, PaymentMatcher matcher, ILogger<PaymentMatchReconciler> log)
    { _db = db; Matcher = matcher; _log = log; }

    public async Task ReconcileApprovalAsync(Payment payment, CancellationToken ct)
    {
        var wpfCustomerId = await ResolveWpfCustomerAsync(payment, ct);
        if (wpfCustomerId is null) { _log.LogInformation("Reconcile: shopper→WPF müşteri bağı yok (ödeme {PaymentId})", payment.Id); return; }
        if (await _db.PaymentMatchGaps.AnyAsync(g => g.PaymentId == payment.Id, ct)
            || await _db.PaymentMatches.AnyAsync(m => m.PaymentId == payment.Id, ct)) return;

        var from = payment.PaidAt - CandidateWindow; var to = payment.PaidAt + CandidateWindow;
        var linked = _db.PaymentMatches.Where(m => m.LicenseId == payment.LicenseId && m.PaymentId != null).Select(m => m.BankTransactionId);
        var candidates = await _db.BankTransactions
            .Where(t => t.LicenseId == payment.LicenseId && t.Direction == BankTransactionDirection.Incoming
                        && t.Amount == payment.Amount && t.OccurredAt >= from && t.OccurredAt <= to && !linked.Contains(t.Id))
            .ToListAsync(ct);

        if (candidates.Count > 1)
        {
            var payer = BankTextNormalizer.Tokenize(payment.PayerName).Tokens.Where(t => t.Length >= 3).ToList();
            if (payer.Count > 0)
            {
                var narrowed = candidates.Where(t => { var d = BankTextNormalizer.Tokenize(t.Description).Tokens; return payer.All(p => d.Contains(p)); }).ToList();
                if (narrowed.Count == 1) candidates = narrowed;
            }
        }
        if (candidates.Count != 1)
        {
            _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = payment.LicenseId, PaymentId = payment.Id,
                Reason = candidates.Count == 0 ? PaymentMatchGapReason.NoCandidate : PaymentMatchGapReason.AmbiguousCandidates, CreatedAt = DateTimeOffset.UtcNow });
            await _db.SaveChangesAsync(ct);
            return;
        }

        await LinkAsync(candidates[0], payment, wpfCustomerId.Value, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Yeni gelen hareket: aynı tutar ±2 gün, tek AÇIK gap → bağla ve gap'i çöz. Çoklu → bekle.</summary>
    public async Task TryResolveGapAsync(BankTransaction tx, CancellationToken ct)
    {
        if (tx.Direction != BankTransactionDirection.Incoming) return;
        if (await _db.PaymentMatches.AnyAsync(m => m.BankTransactionId == tx.Id && m.PaymentId != null, ct)) return;
        var from = tx.OccurredAt - CandidateWindow; var to = tx.OccurredAt + CandidateWindow;
        var open = await (from g in _db.PaymentMatchGaps
                          join p in _db.Payments on g.PaymentId equals p.Id
                          where g.LicenseId == tx.LicenseId && g.ResolvedAt == null && p.Status == PaymentStatus.Approved
                                && p.Amount == tx.Amount && p.PaidAt >= from && p.PaidAt <= to
                          select new { Gap = g, Payment = p }).ToListAsync(ct);
        if (open.Count != 1) return;
        var wpfCustomerId = await ResolveWpfCustomerAsync(open[0].Payment, ct);
        if (wpfCustomerId is null) return;
        await LinkAsync(tx, open[0].Payment, wpfCustomerId.Value, ct);
        open[0].Gap.ResolvedBankTransactionId = tx.Id; open[0].Gap.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ManualMatchAsync(Guid licenseId, Guid transactionId, Guid wpfCustomerId, CancellationToken ct)
    {
        var tx = await _db.BankTransactions.FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Hareket bulunamadı.");
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct) ?? await Matcher.MatchAsync(tx, ct);
        var now = DateTimeOffset.UtcNow;
        match.ActualWpfCustomerId = wpfCustomerId; match.DecidedAt = now; match.UpdatedAt = now;
        match.Status = match.ProposedWpfCustomerId == wpfCustomerId ? PaymentMatchStatus.ConfirmedByHuman : PaymentMatchStatus.ManualOnly;
        await LearnIbanAsync(tx, wpfCustomerId, IbanMemorySource.ManualMatch, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UnmatchAsync(Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        var tx = await _db.BankTransactions.FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Hareket bulunamadı.");
        var learned = await _db.CustomerIbanMemories.Where(m => m.SourceBankTransactionId == tx.Id).ToListAsync(ct);
        _db.CustomerIbanMemories.RemoveRange(learned); // spec §3: iptal bayrağı değil, silme
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct);
        if (match is not null)
        {
            match.PaymentId = null; match.ActualWpfCustomerId = null; match.DecidedAt = null;
            match.Status = PaymentMatchStatus.NoProposal; // insan-kararı kilidi kalkar, Matcher yeniden hesaplar
        }
        await _db.SaveChangesAsync(ct);
        if (match is not null) await Matcher.MatchAsync(tx, ct);
    }

    private async Task<Guid?> ResolveWpfCustomerAsync(Payment payment, CancellationToken ct)
    {
        if (payment.ShopperId is null) return null;
        return await _db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.ShopperId == payment.ShopperId && l.LicenseId == payment.LicenseId && l.WpfCustomerId != null)
            .OrderByDescending(l => l.JoinedAt).Select(l => l.WpfCustomerId).FirstOrDefaultAsync(ct);
    }

    private async Task LinkAsync(BankTransaction tx, Payment payment, Guid wpfCustomerId, CancellationToken ct)
    {
        var match = await _db.PaymentMatches.FirstOrDefaultAsync(m => m.BankTransactionId == tx.Id, ct) ?? await Matcher.MatchAsync(tx, ct);
        var now = DateTimeOffset.UtcNow;
        match.PaymentId = payment.Id; match.ActualWpfCustomerId = wpfCustomerId; match.DecidedAt = now; match.UpdatedAt = now;
        match.Status = match.ProposedWpfCustomerId is null ? PaymentMatchStatus.ManualOnly
            : match.ProposedWpfCustomerId == wpfCustomerId ? PaymentMatchStatus.ConfirmedByHuman : PaymentMatchStatus.Contradicted;
        await LearnIbanAsync(tx, wpfCustomerId, IbanMemorySource.HumanApproval, ct);
    }

    private async Task LearnIbanAsync(BankTransaction tx, Guid wpfCustomerId, IbanMemorySource source, CancellationToken ct)
    {
        if (tx.CounterpartyIbanHash is null) return;
        var existing = await _db.CustomerIbanMemories.FirstOrDefaultAsync(m => m.LicenseId == tx.LicenseId && m.IbanHash == tx.CounterpartyIbanHash, ct);
        if (existing is not null)
        {
            if (existing.WpfCustomerId != wpfCustomerId)
                _log.LogWarning("IBAN hafızası çelişkisi: hareket {TransactionId} başka müşteriye kayıtlı IBAN'dan geldi", tx.Id);
            return;
        }
        _db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = tx.LicenseId, WpfCustomerId = wpfCustomerId, IbanHash = tx.CounterpartyIbanHash,
            IbanMasked = tx.CounterpartyIbanMasked ?? "?", LearnedFrom = source, SourceBankTransactionId = tx.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
```

`PanelPaymentsController` — ctor (mevcut `_log` alanının altına `private readonly PaymentMatchReconciler _reconciler;`):

```csharp
    public PanelPaymentsController(
        LicenseDbContext db,
        INotificationSender push,
        LabelRuleApplier labels,
        ILogger<PanelPaymentsController> log,
        PaymentMatchReconciler reconciler)
    {
        _db = db;
        _push = push;
        _labels = labels;
        _log = log;
        _reconciler = reconciler;
    }
```

`Approve`'da `await NotifyShopperPaymentDecisionAsync(payment, approved: true, reason: null, ct);` satırından
SONRA, `return NoContent();` öncesine:

```csharp
        // Gölge eşleştirme ölçümü — best-effort: hata onayı asla düşürmez (spec §6).
        try { await _reconciler.ReconcileApprovalAsync(payment, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Gölge eşleştirme bağlama düştü (ödeme {PaymentId})", payment.Id);
        }
```

`using OrderDeck.LicenseServer.Services.Bank;` ekle. `Reject` DOKUNULMAZ.

`MatchingBankTransactionSink` (Görev 10'daki dosyanın yeni hali — gap çözümü eklendi):

```csharp
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Çekim işinden gelen her yeni GELEN hareketi eşleştiriciye verir, sonra açık gap'i çözmeyi dener.
/// Hata çekimi düşürmez (hareket zaten kaydedildi).</summary>
public sealed class MatchingBankTransactionSink : IBankTransactionSink
{
    private readonly PaymentMatcher _matcher;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly ILogger<MatchingBankTransactionSink> _log;

    public MatchingBankTransactionSink(PaymentMatcher matcher, PaymentMatchReconciler reconciler, ILogger<MatchingBankTransactionSink> log)
    { _matcher = matcher; _reconciler = reconciler; _log = log; }

    public async Task OnNewIncomingAsync(BankTransaction tx, CancellationToken ct)
    {
        try
        {
            await _matcher.MatchAsync(tx, ct);
            await _reconciler.TryResolveGapAsync(tx, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Gölge eşleştirme düştü (hareket {TransactionId})", tx.Id);
        }
    }
}
```

`MatchingSinkTests.Sink_gelen_hareket_icin_PaymentMatch_yazar_ve_hatayi_yutar` içindeki kurucu satırı:

```csharp
        var sink = new MatchingBankTransactionSink(matcher, new PaymentMatchReconciler(db, matcher, NullLogger<PaymentMatchReconciler>.Instance),
            NullLogger<MatchingBankTransactionSink>.Instance);
```

`Program.cs` — `PaymentMatcher` kaydının altına:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.PaymentMatchReconciler>();
```

- [ ] **Adım 4: Koştur — YEŞİL**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentMatchReconcilerTests|FullyQualifiedName~MatchingSinkTests|FullyQualifiedName~PanelPayments"`
Expected: `PaymentMatchReconcilerTests` 8/8, `MatchingSinkTests` 2/2, mevcut panel ödeme testleri değişmeden PASS; build 0 uyarı.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/PaymentMatchReconciler.cs OrderDeck.LicenseServer/Services/Bank/MatchingBankTransactionSink.cs OrderDeck.LicenseServer/Controllers/Panel/PanelPaymentsController.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatchReconcilerTests.cs OrderDeck.LicenseServer.Tests/Services/Bank/MatchingSinkTests.cs
git commit -F - <<'MSG'
feat(banka): dekont onayını gölge öneriyle bağla — doğrulandı/çelişti/gap, IBAN öğrenme, elle eşleme

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 12: Admin sayfası `Admin/BankaEslestirme` + ölçüm

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/Bank/PaymentMatchMetrics.cs`,
  `OrderDeck.LicenseServer/Pages/Admin/BankaEslestirme/Index.cshtml`, `.../Index.cshtml.cs`
- Değiştir: `OrderDeck.LicenseServer/Pages/Shared/_AdminLayout.cshtml` (menü),
  `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`, `Program.cs` (DI)
- Test: `OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatchMetricsTests.cs`,
  `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBankaEslestirmePageTests.cs`

Spec §8: son 30 gün gelen hareketler, öneri, gerçek karar, elle eşle / kaldır, ölçüm kutusu, **sayfalama 50**.
Spec §9 sayımları: gelen; dışlanan; öneri üretilen; Confirmed; Contradicted; ManualOnly; NoProposal + kararsız;
gap; Obifin→banka gecikmesi (medyan/maks); katman bazında isabet. TempData şeritleri `_AdminLayout` →
`_ToastPartial` basar (sayfada tekrar YAZMA — Netgsm sayfasındaki not). `_ViewImports` sayesinde `@model IndexModel`
klasör ad alanından çözülür (Netgsm sayfasıyla aynı).

- [ ] **Adım 1: Ölçüm testi (kırmızı)**

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class PaymentMatchMetricsTests
{
    [Fact]
    public async Task Son_30_gun_sayimlari_celiski_orani_gecikme_ve_katman()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}").Options);
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        BankTransaction T(int daysAgo, BankTransactionDirection dir = BankTransactionDirection.Incoming) => new()
        { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb", Direction = dir,
          Amount = 10m, Currency = "TL", OccurredAt = now.AddDays(-daysAgo), FetchedAt = now };
        PaymentMatch M(BankTransaction t, PaymentMatchStatus s, PaymentMatchLayer layer = PaymentMatchLayer.None, string? evidence = null) => new()
        { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = t.Id, Status = s, Layer = layer, Evidence = evidence, CreatedAt = t.OccurredAt, UpdatedAt = t.OccurredAt };
        var t1 = T(1); var t2 = T(2); var t3 = T(3); var t4 = T(4); var t5 = T(40); var t6 = T(5, BankTransactionDirection.Outgoing); var t7 = T(6);
        db.BankTransactions.AddRange(t1, t2, t3, t4, t5, t6, t7);
        db.PaymentMatches.AddRange(
            M(t1, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription),
            M(t2, PaymentMatchStatus.Contradicted, PaymentMatchLayer.IbanMemory),
            M(t3, PaymentMatchStatus.ManualOnly),
            M(t4, PaymentMatchStatus.Proposed, PaymentMatchLayer.UsernameInDescription),
            M(t5, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription),
            M(t7, PaymentMatchStatus.NoProposal, evidence: "excluded:CCP"));
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now.AddDays(-1) });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now.AddDays(-2), ResolvedAt = now });
        await db.SaveChangesAsync();

        var m = await new PaymentMatchMetrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Incoming.Should().Be(5, "40 gün önceki ve giden sayılmaz");
        m.Excluded.Should().Be(1);
        m.Proposed.Should().Be(3, "Confirmed + Contradicted + bekleyen Proposed");
        m.Confirmed.Should().Be(1); m.Contradicted.Should().Be(1); m.ManualOnly.Should().Be(1); m.PendingProposals.Should().Be(1);
        m.NoProposal.Should().Be(0, "dışlanan NoProposal'dan düşülür");
        m.OpenGaps.Should().Be(1, "çözülen gap sayılmaz");
        m.ContradictionRate.Should().Be(0.5m, "1 / (1 + 1)");
        m.LagMedian.Should().Be(TimeSpan.FromDays(3), "1,2,3,4,6 gün → medyan 3");
        m.LagMax.Should().Be(TimeSpan.FromDays(6));
        m.Layers.Single(l => l.Layer == PaymentMatchLayer.UsernameInDescription).Confirmed.Should().Be(1);
        m.Layers.Single(l => l.Layer == PaymentMatchLayer.IbanMemory).Contradicted.Should().Be(1);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası (`PaymentMatchMetrics` yok).**

- [ ] **Adım 3: Ölçüm servisi**

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

public sealed record LayerStat(PaymentMatchLayer Layer, int Confirmed, int Contradicted);

/// <summary>Spec §9 sayımları (kayan pencere). Faz 2 eşiği: ≥ 200 bağlanmış, çelişki ≤ %2.</summary>
public sealed record PaymentMatchSummary(
    int Incoming, int Excluded, int Proposed, int Confirmed, int Contradicted, int ManualOnly,
    int PendingProposals, int NoProposal, int OpenGaps, decimal? ContradictionRate,
    TimeSpan? LagMedian, TimeSpan? LagMax, IReadOnlyList<LayerStat> Layers)
{
    public int Linked => Confirmed + Contradicted + ManualOnly;
    public bool MeetsPhase2Threshold => Linked >= 200 && ContradictionRate is { } r && r <= 0.02m;
}

public sealed class PaymentMatchMetrics
{
    /// <summary>Gecikme ölçümünde 7 günü aşan farklar (ilk geri doldurma) sayım dışı.</summary>
    public const int LagCapDays = 7;

    private readonly LicenseDbContext _db;
    public PaymentMatchMetrics(LicenseDbContext db) => _db = db;

    public async Task<PaymentMatchSummary> ComputeAsync(Guid licenseId, int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var incoming = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming && t.OccurredAt >= since)
            .Select(t => new { t.OccurredAt, t.FetchedAt }).ToListAsync(ct);
        var matches = await _db.PaymentMatches.AsNoTracking()
            .Where(m => m.LicenseId == licenseId && m.CreatedAt >= since)
            .Select(m => new { m.Status, m.Layer, m.Evidence }).ToListAsync(ct);
        var openGaps = await _db.PaymentMatchGaps.CountAsync(g => g.LicenseId == licenseId && g.CreatedAt >= since && g.ResolvedAt == null, ct);

        int Of(PaymentMatchStatus s) => matches.Count(m => m.Status == s);
        var excluded = matches.Count(m => m.Evidence != null && m.Evidence.StartsWith("excluded:", StringComparison.Ordinal));
        var confirmed = Of(PaymentMatchStatus.ConfirmedByHuman); var contradicted = Of(PaymentMatchStatus.Contradicted);
        var decided = confirmed + contradicted;

        var lagTicks = incoming.Select(x => (x.FetchedAt - x.OccurredAt).Ticks)
            .Where(l => l >= 0 && l <= TimeSpan.FromDays(LagCapDays).Ticks).OrderBy(l => l).ToList();
        TimeSpan? lagMedian = lagTicks.Count == 0 ? null
            : TimeSpan.FromTicks(lagTicks.Count % 2 == 1 ? lagTicks[lagTicks.Count / 2] : (lagTicks[lagTicks.Count / 2 - 1] + lagTicks[lagTicks.Count / 2]) / 2);
        TimeSpan? lagMax = lagTicks.Count == 0 ? null : TimeSpan.FromTicks(lagTicks[^1]);

        var layers = matches
            .Where(m => m.Status is PaymentMatchStatus.ConfirmedByHuman or PaymentMatchStatus.Contradicted)
            .GroupBy(m => m.Layer)
            .Select(g => new LayerStat(g.Key, g.Count(x => x.Status == PaymentMatchStatus.ConfirmedByHuman), g.Count(x => x.Status == PaymentMatchStatus.Contradicted)))
            .OrderBy(l => l.Layer).ToList();

        return new PaymentMatchSummary(
            Incoming: incoming.Count, Excluded: excluded,
            Proposed: decided + Of(PaymentMatchStatus.Proposed), Confirmed: confirmed, Contradicted: contradicted,
            ManualOnly: Of(PaymentMatchStatus.ManualOnly), PendingProposals: Of(PaymentMatchStatus.Proposed),
            NoProposal: Of(PaymentMatchStatus.NoProposal) - excluded, OpenGaps: openGaps,
            ContradictionRate: decided == 0 ? null : Math.Round((decimal)contradicted / decided, 3),
            LagMedian: lagMedian, LagMax: lagMax, Layers: layers);
    }
}
```

- [ ] **Adım 4: Koştur — `PaymentMatchMetricsTests` 1/1 YEŞİL.**

- [ ] **Adım 5: Sayfa testi (kırmızı — 404)**

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public sealed class AdminBankaEslestirmePageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBankaEslestirmePageTests(ApiFactory factory) => _factory = factory;

    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    private async Task<(Guid LicenseId, Guid TxId, Guid WpfId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"be-{Guid.NewGuid():N}@x", Name = "Be", PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-BE-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 120m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow, Description = "EFT GELEN aciklamasiz", FetchedAt = DateTimeOffset.UtcNow };
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return (lic, tx.Id, wpf.Id);
    }

    [Fact]
    public async Task Girissiz_login_e_yonlenir()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/admin/banka-eslestirme");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Liste_gosterir_elle_esler_ve_kaldirir()
    {
        var (lic, txId, wpfId) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = $"/admin/banka-eslestirme?licenseId={lic}";

        var html = await client.GetStringAsync(path);
        html.Should().Contain("120").And.Contain("EFT GELEN").And.Contain("Faz 2");

        var post = await client.PostAsync("/admin/banka-eslestirme?handler=ManualMatch", await FormAsync(client, path,
            new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["TransactionId"] = txId.ToString(), ["Username"] = "ayse_gul34" }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using (var scope = _factory.Services.CreateScope())
        {
            var m = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.SingleAsync(x => x.BankTransactionId == txId);
            m.Status.Should().Be(PaymentMatchStatus.ManualOnly); m.ActualWpfCustomerId.Should().Be(wpfId);
        }
        (await client.GetStringAsync(path)).Should().Contain("ayse_gul34");

        await client.PostAsync("/admin/banka-eslestirme?handler=Unmatch", await FormAsync(client, path,
            new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["TransactionId"] = txId.ToString() }));
        using (var scope = _factory.Services.CreateScope())
        {
            var m = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.SingleAsync(x => x.BankTransactionId == txId);
            m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ActualWpfCustomerId.Should().BeNull();
        }
    }

    [Fact]
    public async Task Bilinmeyen_kullanici_adi_hata_seridi_verir_eslemez()
    {
        var (lic, txId, _) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = $"/admin/banka-eslestirme?licenseId={lic}";

        await client.PostAsync("/admin/banka-eslestirme?handler=ManualMatch", await FormAsync(client, path,
            new Dictionary<string, string> { ["LicenseId"] = lic.ToString(), ["TransactionId"] = txId.ToString(), ["Username"] = "yok_boyle_biri" }));

        (await client.GetStringAsync(path)).Should().Contain("bulunamadı");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.CountAsync(x => x.BankTransactionId == txId && x.ActualWpfCustomerId != null)).Should().Be(0);
    }
}
```

- [ ] **Adım 6: Sayfa** — `Pages/Admin/BankaEslestirme/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Pages.Admin.BankaEslestirme;

/// <summary>Gölge mod görünürlüğü (spec §8/§9): son 30 gün gelen hareketler, öneri, insan kararı,
/// elle eşle / kaldır, ölçüm kutusu. Otomatik onay yok; bu sayfa Payment'a dokunmaz.</summary>
public class IndexModel : PageModel
{
    public const int PageSize = 50;
    public const int WindowDays = 30;

    private readonly LicenseDbContext _db;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly PaymentMatchMetrics _metrics;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, PaymentMatchReconciler reconciler, PaymentMatchMetrics metrics, IAuditService audit)
    { _db = db; _reconciler = reconciler; _metrics = metrics; _audit = audit; }

    public sealed record LicenseOption(Guid Id, string Email);
    public sealed record Row(Guid TransactionId, DateTimeOffset OccurredAt, decimal Amount, string Currency, string BankaKodu,
        string? Description, string? CounterpartyIbanMasked, string? ProposedUsername, PaymentMatchLayer Layer, decimal Confidence,
        string? Evidence, PaymentMatchStatus? Status, string? ActualUsername, Guid? PaymentId)
    {
        public bool IsLinked => ActualUsername is not null || PaymentId is not null;
    }

    [BindProperty(SupportsGet = true)] public Guid LicenseId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;
    [BindProperty] public Guid TransactionId { get; set; }
    [BindProperty] public string? Username { get; set; }

    public List<LicenseOption> Licenses { get; private set; } = new();
    public PaymentMatchSummary? Summary { get; private set; }
    public List<Row> Rows { get; private set; } = new();
    public bool HasNext { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var connLicenseIds = await _db.ObifinConnections.AsNoTracking().Select(c => c.LicenseId).ToListAsync(ct);
        Licenses = await _db.Licenses.AsNoTracking().Where(l => connLicenseIds.Contains(l.Id))
            .Select(l => new LicenseOption(l.Id, l.Customer.Email)).ToListAsync(ct);
        if (LicenseId == Guid.Empty) LicenseId = Licenses.FirstOrDefault()?.Id ?? Guid.Empty;
        if (LicenseId == Guid.Empty) return;
        if (PageNo < 1) PageNo = 1;

        Summary = await _metrics.ComputeAsync(LicenseId, WindowDays, ct);
        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);
        var txs = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId && t.Direction == BankTransactionDirection.Incoming && t.OccurredAt >= since)
            .OrderByDescending(t => t.OccurredAt).Skip((PageNo - 1) * PageSize).Take(PageSize + 1).ToListAsync(ct);
        HasNext = txs.Count > PageSize;
        if (HasNext) txs.RemoveAt(txs.Count - 1);
        var txIds = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.AsNoTracking().Where(m => txIds.Contains(m.BankTransactionId)).ToDictionaryAsync(m => m.BankTransactionId, ct);
        var names = await _db.WpfCustomerProjections.AsNoTracking().Where(c => c.LicenseId == LicenseId).ToDictionaryAsync(c => c.Id, c => c.Username, ct);
        string? NameOf(Guid? id) => id is { } g && names.TryGetValue(g, out var u) ? u : null;
        Rows = txs.Select(t =>
        {
            matches.TryGetValue(t.Id, out var m);
            return new Row(t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.Description, t.CounterpartyIbanMasked,
                NameOf(m?.ProposedWpfCustomerId), m?.Layer ?? PaymentMatchLayer.None, m?.Confidence ?? 0m, m?.Evidence, m?.Status,
                NameOf(m?.ActualWpfCustomerId), m?.PaymentId);
        }).ToList();
    }

    public async Task<IActionResult> OnPostManualMatchAsync(CancellationToken ct)
    {
        var username = (Username ?? "").Trim();
        var customer = await _db.WpfCustomerProjections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.LicenseId == LicenseId && c.Username == username && c.PurgedAt == null, ct);
        if (customer is null)
        {
            TempData["Error"] = "Kullanıcı adı bu lisansta bulunamadı.";
            return RedirectToPage(new { licenseId = LicenseId, pageNo = PageNo });
        }
        await _reconciler.ManualMatchAsync(LicenseId, TransactionId, customer.Id, ct);
        await _audit.LogAsync(AuditEvents.BankMatchManual, AuditTargets.BankTransaction, TransactionId.ToString(), new { username }, ct);
        TempData["Success"] = "Eşlendi; karşı IBAN varsa hafızaya alındı.";
        return RedirectToPage(new { licenseId = LicenseId, pageNo = PageNo });
    }

    public async Task<IActionResult> OnPostUnmatchAsync(CancellationToken ct)
    {
        await _reconciler.UnmatchAsync(LicenseId, TransactionId, ct);
        await _audit.LogAsync(AuditEvents.BankMatchUnmatch, AuditTargets.BankTransaction, TransactionId.ToString(), null, ct);
        TempData["Success"] = "Eşleme kaldırıldı; öğrenilen IBAN silindi, öneri yeniden hesaplandı.";
        return RedirectToPage(new { licenseId = LicenseId, pageNo = PageNo });
    }
}
```

`Pages/Admin/BankaEslestirme/Index.cshtml`:

```cshtml
@page "/admin/banka-eslestirme"
@model IndexModel
@{
    ViewData["Title"] = "Banka Eşleştirme (gölge)";
}
@* TempData şeritleri _AdminLayout → _ToastPartial'da basılır; burada tekrar yazma. *@
<h2>Banka Eşleştirme <small class="text-muted">gölge mod — otomatik onay yok</small></h2>

<form method="get" class="row g-2 mb-3">
    <div class="col-auto">
        <select name="licenseId" class="form-select" onchange="this.form.submit()">
            @foreach (var l in Model.Licenses)
            {
                <option value="@l.Id" selected="@(l.Id == Model.LicenseId)">@l.Email</option>
            }
        </select>
    </div>
</form>

@if (Model.Summary is { } s)
{
    <div class="card mb-3"><div class="card-body">
        <h5 class="card-title">Son @IndexModel.WindowDays gün</h5>
        <div class="row small">
            <div class="col-md-3">Gelen: <b>@s.Incoming</b><br />Dışlanan: @s.Excluded<br />Öneri üretilen: @s.Proposed</div>
            <div class="col-md-3">Doğrulandı: <b>@s.Confirmed</b><br />Çelişti: <b>@s.Contradicted</b><br />Elle (öneri yoktu): @s.ManualOnly</div>
            <div class="col-md-3">Bekleyen öneri: @s.PendingProposals<br />Öneri yok: @s.NoProposal<br />Açık gap (dekont var, hareket yok): @s.OpenGaps</div>
            <div class="col-md-3">Çelişki oranı: <b>@(s.ContradictionRate is { } r ? r.ToString("P1") : "—")</b><br />
                Gecikme medyan/maks: @(s.LagMedian?.ToString(@"d\.hh\:mm") ?? "—") / @(s.LagMax?.ToString(@"d\.hh\:mm") ?? "—")
                <span class="text-muted">(7 günü aşanlar sayım dışı)</span></div>
        </div>
        <div class="mt-2 small">Katman isabeti:
            @foreach (var l in s.Layers) { <span class="badge bg-secondary me-1">@l.Layer ✓@l.Confirmed ✗@l.Contradicted</span> }
        </div>
        <div class="mt-2 small @(s.MeetsPhase2Threshold ? "text-success" : "text-muted")">
            Faz 2 eşiği: ≥ 200 bağlı, çelişki ≤ %2 — şu an @s.Linked bağlı@(s.MeetsPhase2Threshold ? " ✓ eşik sağlandı" : "")
        </div>
    </div></div>
}

<table class="table table-sm table-striped">
    <thead><tr><th>Tarih</th><th>Tutar</th><th>Banka</th><th>Açıklama</th><th>Karşı IBAN</th><th>Öneri</th><th>Durum</th><th>Gerçek</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.Rows)
    {
        <tr>
            <td>@r.OccurredAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm")</td>
            <td>@r.Amount.ToString("N2") @r.Currency</td>
            <td>@r.BankaKodu</td>
            <td class="small">@r.Description</td>
            <td class="small">@r.CounterpartyIbanMasked</td>
            <td class="small">
                @if (r.ProposedUsername is not null) { <b>@r.ProposedUsername</b> <span class="text-muted">@r.Layer @r.Confidence.ToString("0.00")</span> }
                <div class="text-muted">@r.Evidence</div>
            </td>
            <td>@(r.Status?.ToString() ?? "—")</td>
            <td>@r.ActualUsername</td>
            <td>
                <form method="post" class="d-flex gap-1">
                    <input type="hidden" name="LicenseId" value="@Model.LicenseId" />
                    <input type="hidden" name="TransactionId" value="@r.TransactionId" />
                    @if (r.IsLinked)
                    {
                        <button class="btn btn-sm btn-outline-danger" asp-page-handler="Unmatch">Kaldır</button>
                    }
                    else
                    {
                        <input name="Username" class="form-control form-control-sm" placeholder="kullanıcı adı" />
                        <button class="btn btn-sm btn-outline-primary" asp-page-handler="ManualMatch">Elle eşle</button>
                    }
                </form>
            </td>
        </tr>
    }
    </tbody>
</table>

<nav>
    @if (Model.PageNo > 1) { <a class="btn btn-sm btn-light" asp-page="/Admin/BankaEslestirme/Index" asp-route-licenseId="@Model.LicenseId" asp-route-pageNo="@(Model.PageNo - 1)">‹ Önceki</a> }
    @if (Model.HasNext) { <a class="btn btn-sm btn-light" asp-page="/Admin/BankaEslestirme/Index" asp-route-licenseId="@Model.LicenseId" asp-route-pageNo="@(Model.PageNo + 1)">Sonraki ›</a> }
</nav>
```

`AuditEvents.cs` — Görev 6'da eklenen `ObifinPollNow` satırının altına:

```csharp
    public const string BankMatchManual = "bank.match.manual";
    public const string BankMatchUnmatch = "bank.match.unmatch";
```

`AuditTargets` — `ObifinConnection` satırının altına `public const string BankTransaction = "bank-transaction";`.

`Pages/Shared/_AdminLayout.cshtml` — `Obifin` satırının altına:

```html
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/BankaEslestirme/Index">Banka Eşleştirme</a></li>
```

`Program.cs` — `PaymentMatchReconciler` kaydının altına:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Bank.PaymentMatchMetrics>();
```

- [ ] **Adım 7: Koştur — YEŞİL**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminBankaEslestirmePageTests|FullyQualifiedName~PaymentMatchMetricsTests"`
Expected: 3/3 + 1/1 PASS; build 0 uyarı.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Bank/PaymentMatchMetrics.cs OrderDeck.LicenseServer/Pages/Admin/BankaEslestirme OrderDeck.LicenseServer/Pages/Shared/_AdminLayout.cshtml OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Bank/PaymentMatchMetricsTests.cs OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBankaEslestirmePageTests.cs
git commit -F - <<'MSG'
feat(banka): admin Banka Eşleştirme sayfası — öneri/karar listesi, elle eşle/kaldır, 30 günlük ölçüm

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

### Görev 13: PR-2 tam paket + spec notu + PR

- [ ] **Adım 1: Spec'e sapma notları** — `docs/superpowers/specs/2026-09-25-obifin-banka-hareketi-golge-eslestirme-design.md`
  §6 sonuna şu paragrafı ekle:

```markdown
**Faz 1 uygulama notları (plan 2026-09-25):** (1) Katman 3 ad benzerliği Jaro-Winkler yerine
normalize `FullName` token dizisinin açıklamada geçmesiyle ölçülür (deterministik, kütüphanesiz);
Jaro-Winkler gerekirse Faz 2. (2) Çoklu adayda "en yüksek benzerlik" yerine gönderen adının tüm
tokenlarını içeren TEK aday alınır; yoksa `AmbiguousCandidates` gap'i. (3) Bağlama yalnız ONAY'da
koşar; ret hiçbir şeye bağlanmaz (reddedilen dekontun gerçek müşterisi yoktur).
```

- [ ] **Adım 2: Tam paket** (Docker açık):

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: tümü PASS; `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj -warnaserror` 0 uyarı.

- [ ] **Adım 3: Commit + PR**

```bash
git add docs/superpowers/specs/2026-09-25-obifin-banka-hareketi-golge-eslestirme-design.md
git commit -F - <<'MSG'
docs(banka): spec §6 Faz 1 uygulama notları

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
git push -u origin HEAD
gh pr create --base master --title "feat(banka): gölge eşleştirme — öneri, insan kararı bağlama, admin sayfası" --body-file - <<'BODY'
## Özet
- `BankTextNormalizer` + `PaymentMatcher`: kullanıcı adı / IBAN hafızası / ad katmanları, dışlama (`CCP`), belirsizlikte öneri yok
- `PaymentMatchReconciler`: dekont ONAYI → aday hareket (tutar, ±2 gün) → doğrulandı/çelişti/elle; IBAN öğrenme; gap + gecikmeli çözüm; elle eşle / kaldır (hafıza satırı silinir)
- Admin `Banka Eşleştirme`: son 30 gün liste (50/sayfa), öneri + gerçek karar, ölçüm kutusu (çelişki oranı, gecikme, katman isabeti, Faz 2 eşiği)
- `Payment`'a DOKUNULMADI; otomatik onay yok (gölge mod)

## Ölçüm planı
2–4 hafta gölge; Faz 2 eşiği: ≥ 200 bağlı, çelişki ≤ %2, gap oranı açıklanmış.

## Test
- `PaymentMatcherTests`, `PaymentMatchReconcilerTests`, `PaymentMatchMetricsTests`, `AdminBankaEslestirmePageTests`, `MatchingSinkTests`
- Tam paket yeşil (Docker/Testcontainers dahil)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
BODY
```

Merge Burak'ta; yayın penceresi (Paz/Pzt/Çar/Per 20:00–01:00 TR) dışında.

---

## Öz-inceleme (plan yazıldıktan sonra yapıldı)

**Spec kapsamı:** §3 veri modeli → Görev 1; §4 istemci → Görev 2; §5 çekim/yenileme/saklama → Görev 4–5;
§8 `Admin/Obifin` → Görev 6; §6 normalizasyon + katmanlar → Görev 8–9; §6 bağlama + elle eşle + §3 gap çözümü
(`ResolvedBankTransactionId`) → Görev 11; §8 `Admin/BankaEslestirme` (sayfalama 50) + §9 ölçüm (gecikme
medyan/maks, katman isabeti, Faz 2 eşiği) → Görev 12; §7 gizlilik → Görev 1 (hash/maske), 3 (banka kimliği
saklanmaz), 4 (saklama işi), 6 (görünümde şifre yok); §11 test listesi görev testlerine dağıtıldı; §12 teslim
sırası → Görev 7 (PR-1) / 13 (PR-2). §10 açık sorular plan dışı (Burak/Obifin). Boşluk yok.

**Yer tutucu taraması:** "TBD/TODO/sonra doldur/uygun hata yönetimi ekle" yok. Test yardımcıları gerçek adlarıyla
(`ApiFactory.CreateLoggedInAdminClientAsync()` uzantısı, `AdminLoginHelper.ExtractAntiForgeryToken`) kullanıldı;
her sayfa testi kendi `FormAsync` yardımcısını taşır.

**Tip tutarlılığı:** `ObifinCredentials(BaseUrl, UserCode, Password, ApiKey)` Görev 2/3/4/6'da aynı;
`IBankTransactionSink.OnNewIncomingAsync(BankTransaction, CancellationToken)` Görev 4/10/11'de aynı;
`PaymentMatcher.MatchAsync(BankTransaction, ct)` Görev 9/10/11'de aynı; `MatchingBankTransactionSink` kurucusu
Görev 11'de `(PaymentMatcher, PaymentMatchReconciler, ILogger)` olarak değişti ve Görev 10 testi aynı görevde
güncellendi; `ObifinPollJob.PollConnectionAsync(Guid connectionId, ct)` Görev 4/6'da aynı;
`PaymentMatchMetrics.ComputeAsync(licenseId, days, ct)` tek; DbSet adları `ObifinConnections`, `BankConnections`,
`BankAccounts`, `BankTransactions`, `PaymentMatches`, `CustomerIbanMemories`, `PaymentMatchGaps` (Görev 1);
mevcut `WpfCustomerProjections`, `ShopperBroadcasterLinks`, `Payments`, `Shoppers`, `Licenses`, `Customers`
(repo'da doğrulandı). `PaymentMatchStatus` değerleri (Proposed/NoProposal/ConfirmedByHuman/Contradicted/ManualOnly)
ve `PaymentMatchLayer` (None/UsernameInDescription/IbanMemory/NameAmount) Görev 1 tanımıyla aynı.

**Bilinçli sapmalar (spec'e Görev 13'te işlenir):** (1) ad benzerliği token dizisi; (2) çoklu adayda tüm-token
içerme; (3) yalnız onay bağlanır. Ayrıca spec §5 "kısmi hata → imleç eski kalır" Görev 4'te sayfa bazlı commit +
idempotent yazım + imleç yalnız tam başarıda ilerler biçiminde uygulanır.
