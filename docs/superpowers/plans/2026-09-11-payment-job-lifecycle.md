# Uygulama Planı — Ödeme İşi Yaşam Döngüsü (PaymentJob)

> **Spec:** `docs/superpowers/specs/2026-09-11-payment-job-lifecycle-design.md` (commit 1e87c52).
> Bu plan spec'in birebir uygulanışıdır; çelişki hâlinde spec kazanır.

**Hedef:** R3 denetiminin R2-01..04 bulgularını kapatmak. `PendingBalanceApply`
(033) yerine kalıcı durum makineli `PaymentJob` gelir: anahtar diske iner →
sunucu cevabı kesinleşene kadar `apply_uncertain` → teslimatta iş kapanır.
Revizyon = eski düşümü geri al + yeni toplamla taze uygula. Belirsizlikte bir
kez replay, hâlâ belirsizse **mesaj gönderilmez** (`BalanceUncertain`).
`PendingApplyConflict` + `overridePendingConflict` tamamen kalkar.

**PR paketi ve YÜRÜTME SIRASI (spec §10'dan farklı — bağımlılık gereği):**

| Sıra | PR | Branch | İçerik |
|---|---|---|---|
| 1 | PR1 | `feat/payment-job-store` | Migration 034 + `PaymentJobRepository` + testler |
| 2 | PR4 | `feat/balance-reverse-endpoint` | Sunucu: A11 content-conflict + reverse ucu + `LicenseApiClient.ReverseBalanceTransactionAsync` |
| 3 | PR2 | `feat/payment-job-service` | `PaymentRequestService` durum makinesi + A1-A9 testleri + eski deponun silinmesi |
| 4 | PR3 | `feat/payment-job-ui` | `BalanceUncertain` diyaloğu (iki VM) |

Neden bu sıra: PR2'nin `TryReverseAsync`'i PR4'ün istemci metodunu çağırır.
PR4 merge = **otomatik prod deploy** — PR2'yi taşıyan Velopack sürümünden önce
sahada olmalı (eski istemci + yeni sunucu güvenli: yeni 409'ları eski genel
catch yutuyor, düşüm olmaz, çift tahsilat olmaz).

**Test komutları:**
- WPF/Core: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
- Sunucu: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` (Docker açık olmalı — Testcontainers alt kümesi)
- Tek test: `--filter "FullyQualifiedName~<TestAdı>"`

**Commit kuralı:** Türkçe, imperative, her commit'te
`Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.

---

## PR1 — Migration 034 + PaymentJobRepository

Branch: master'dan `feat/payment-job-store`.

### Task 1: Migration 034 — tablo + legacy taşıma

**Dosyalar:**
- Yeni: `OrderDeck.Core/Storage/Migrations/034_payment_job.sql` (csproj glob'u `Storage\Migrations\*.sql` otomatik gömer)
- Yeni: `OrderDeck.Tests/Storage/PaymentJobMigrationTests.cs`
- Sil: `OrderDeck.Tests/Storage/PendingBalanceApplyRepositoryTests.cs` (tablosu düşüyor; depo sınıfı PR2'ye kadar YAŞAR — servis hâlâ derleniyor)

**Adım 1 — başarısız testi yaz** (`PaymentJobMigrationTests.cs`):

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public sealed class PaymentJobMigrationTests
{
    /// <summary>Gömülü migration script'lerini <paramref name="maxVersion"/>'a
    /// KADAR yükler — 033 dünyası kurulup veri ekildikten sonra tam koşu 034'ün
    /// taşıma SQL'ini gerçek veri üstünde sınar.</summary>
    private static IReadOnlyList<(int Version, string Sql)> EmbeddedScriptsUpTo(int maxVersion)
    {
        var asm = typeof(MigrationRunner).Assembly;
        const string prefix = "OrderDeck.Core.Storage.Migrations.";
        var list = new List<(int Version, string Sql)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
                continue;
            var file = name.Substring(prefix.Length);
            var version = int.Parse(
                file.Substring(0, file.IndexOf('_')), CultureInfo.InvariantCulture);
            if (version > maxVersion) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((version, reader.ReadToEnd()));
        }
        return list.OrderBy(t => t.Version).ToList();
    }

    private static void SeedPending(
        InMemorySqlite fx, string customerId, Guid key, string total,
        long createdAt, long? resolvedAt)
    {
        using var conn = fx.Open();
        conn.Execute(
            @"INSERT INTO PendingBalanceApply
              (IdempotencyKey, CustomerId, ProductTotal, CreatedAt, ResolvedAt)
              VALUES (@k, @c, @t, @at, @r)",
            new { k = key.ToString("N"), c = customerId, t = total, at = createdAt, r = resolvedAt });
    }

    [Fact]
    public void Migration034_CozulmemisEnYeniKayit_LegacyIsineTasinir()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(33)).Run();

        var eskiKey = Guid.NewGuid();
        var yeniKey = Guid.NewGuid();
        var cozulmusKey = Guid.NewGuid();
        var digerKey = Guid.NewGuid();
        SeedPending(fx, "c1", eskiKey, "100.5", createdAt: 100, resolvedAt: null);
        SeedPending(fx, "c1", yeniKey, "250.75", createdAt: 200, resolvedAt: null); // en yeni → taşınır
        SeedPending(fx, "c1", cozulmusKey, "50", createdAt: 300, resolvedAt: 301);  // çözülmüş → atılır
        SeedPending(fx, "c2", digerKey, "10", createdAt: 150, resolvedAt: null);

        new MigrationRunner(fx).Run(); // kalan migration'lar (034 dahil)

        using var conn = fx.Open();
        var rows = conn.Query<(string CustomerId, string ScopeKey, string ProductTotal,
                string? ApplyKey, string State, long CreatedAt)>(
            @"SELECT CustomerId, ScopeKey, ProductTotal, ApplyKey, State, CreatedAt
              FROM PaymentJob ORDER BY CustomerId").ToList();

        rows.Should().HaveCount(2); // müşteri başına EN YENİ çözülmemiş satır
        var c1 = rows[0];
        c1.CustomerId.Should().Be("c1");
        c1.ScopeKey.Should().Be("legacy");
        c1.ProductTotal.Should().Be("250.75");
        c1.ApplyKey.Should().Be(yeniKey.ToString("N"));
        c1.State.Should().Be("apply_uncertain");
        c1.CreatedAt.Should().Be(200);
        rows[1].CustomerId.Should().Be("c2");
        rows[1].ApplyKey.Should().Be(digerKey.ToString("N"));

        // Eski tablo düşmüş olmalı.
        var eskiTablo = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingBalanceApply'");
        eskiTablo.Should().Be(0);

        var version = conn.ExecuteScalar<long>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(34);
    }

    [Fact]
    public void Migration034_UniqueScopeIndeksi_AyniKapsamdaIkinciSatiriReddeder()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();

        using var conn = fx.Open();
        const string insert =
            @"INSERT INTO PaymentJob
              (Id, CustomerId, ScopeKey, ProductTotal, Revision, State, CreatedAt, UpdatedAt)
              VALUES (@id, 'c1', 'session:s1', '10', 0, 'created', 1, 1)";
        conn.Execute(insert, new { id = Guid.NewGuid().ToString("N") });
        var act = () => conn.Execute(insert, new { id = Guid.NewGuid().ToString("N") });
        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>()
           .Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT
    }
}
```

Ayrıca `OrderDeck.Tests/Storage/PendingBalanceApplyRepositoryTests.cs` **silinir**
(aynı commit'te — tablo düşünce zaten kırmızıya döner).

**Adım 2 — koş, KIRMIZI bekle:**
```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~PaymentJobMigrationTests"
```
Beklenen: iki test de düşer (`no such table: PaymentJob` / 034 yok).

**Adım 3 — migration'ı yaz** (`034_payment_job.sql`):

```sql
-- R2-01..04 (2026-09-11 R3 denetimi): bakiye düşümünün KALICI durum makinesi.
--
-- 033'ün PendingBalanceApply'ı yalnız "çözülmemiş anahtar" tutuyordu; sonucun
-- ne olduğu (uygulandı mı, bakiye yok muydu, tutar neydi) bellekte kalıyordu.
-- PaymentJob bunları diske indirir:
--
--   created → apply_uncertain → applied | no_balance ; teslimatta ClosedAt dolar.
--
-- Kapsam (CustomerId, ScopeKey) UNIQUE: "session:{id}" | "cumulative" | "legacy".
-- INSERT OR IGNORE + bu indeks = atomik find-or-create (R2-04 TOCTOU ölür).
--
-- ProductTotal / AppliedAmount TEXT: invariant kültür ondalık — REAL, eşitlik
-- karşılaştırmasını (revizyon tespiti) bozardı. Id: Guid "N".

CREATE TABLE PaymentJob (
    Id            TEXT    NOT NULL PRIMARY KEY,
    CustomerId    TEXT    NOT NULL,
    ScopeKey      TEXT    NOT NULL,
    ProductTotal  TEXT    NOT NULL,
    Revision      INTEGER NOT NULL DEFAULT 0,
    ApplyKey      TEXT,
    AppliedAmount TEXT,
    State         TEXT    NOT NULL,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL,
    ClosedAt      INTEGER
);

CREATE UNIQUE INDEX UX_PaymentJob_Scope ON PaymentJob(CustomerId, ScopeKey);

-- Miras taşıma: her müşterinin EN YENİ çözülmemiş 033 kaydı 'legacy' işine
-- döner. Durum apply_uncertain: anahtar sunucuda kullanılmış olabilir; ilk
-- "Ödeme iste"de replay gerçeği öğrenir (bkz. servis, legacy devralma).
-- Daha eski çözülmemiş kayıtlar bilerek atılır: 033 akışı zaten müşteri başına
-- tek çözülmemiş kayıt vaat ediyordu; birden fazlası ancak yarım kalmış eski
-- denemedir ve anahtarları sunucuda ledger PK olarak duruyor — tekrar
-- kullanılmadıkça zararsız.
INSERT INTO PaymentJob
    (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey, AppliedAmount,
     State, CreatedAt, UpdatedAt, ClosedAt)
SELECT lower(hex(randomblob(16))), p.CustomerId, 'legacy', p.ProductTotal, 0,
       p.IdempotencyKey, NULL, 'apply_uncertain', p.CreatedAt, p.CreatedAt, NULL
FROM PendingBalanceApply p
WHERE p.ResolvedAt IS NULL
  AND p.rowid IN (
      SELECT p2.rowid FROM PendingBalanceApply p2
      WHERE p2.CustomerId = p.CustomerId AND p2.ResolvedAt IS NULL
      ORDER BY p2.CreatedAt DESC, p2.rowid DESC LIMIT 1);

DROP TABLE PendingBalanceApply;

UPDATE _meta SET SchemaVersion = 34 WHERE Id = 1;
```

**Adım 4 — koş, YEŞİL bekle** (aynı filter). Sonra tüm Core/WPF paketi:
`dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` — eski depo testleri
silindiği için başka kırmızı kalmamalı.

**Adım 5 — commit:**
```
feat(storage): migration 034 — PaymentJob tablosu + 033 legacy taşıma

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 2: PaymentJob modeli + depo

**Dosyalar:**
- Yeni: `OrderDeck.Core/Storage/Repositories/PaymentJobRepository.cs`
- Yeni: `OrderDeck.Tests/Storage/PaymentJobRepositoryTests.cs`

**Adım 1 — başarısız testleri yaz** (`PaymentJobRepositoryTests.cs`):

```csharp
using System;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public sealed class PaymentJobRepositoryTests
{
    private static (InMemorySqlite Fx, PaymentJobRepository Repo) Fx()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();
        return (fx, new PaymentJobRepository(fx));
    }

    [Fact]
    public void FindOrCreate_IlkCagri_CreatedIsYaratir()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250.75m);

        job.CustomerId.Should().Be("c1");
        job.ScopeKey.Should().Be("session:s1");
        job.ProductTotal.Should().Be(250.75m); // TEXT gidiş-dönüş kayıpsız
        job.Revision.Should().Be(0);
        job.ApplyKey.Should().BeNull();
        job.AppliedAmount.Should().BeNull();
        job.State.Should().Be(PaymentJobState.Created);
        job.ClosedAt.Should().BeNull();
    }

    [Fact]
    public void FindOrCreate_AyniKapsamIkinciCagri_AyniSatiriDondurur()
    {
        var (_, repo) = Fx();
        var ilk = repo.FindOrCreate("c1", "session:s1", 100m);
        var ikinci = repo.FindOrCreate("c1", "session:s1", 999m); // farklı toplam bile olsa

        ikinci.Id.Should().Be(ilk.Id);
        ikinci.ProductTotal.Should().Be(100m); // INSERT OR IGNORE — mevcut satır kazanır
    }

    [Fact]
    public void FindOrCreate_FarkliKapsam_AyriIsYaratir()
    {
        var (_, repo) = Fx();
        var oturum = repo.FindOrCreate("c1", "session:s1", 100m);
        var kumulatif = repo.FindOrCreate("c1", "cumulative", 100m);
        kumulatif.Id.Should().NotBe(oturum.Id);
    }

    [Fact]
    public void BeginApply_IlkYazan_KazanirVeBelirsizeGecer()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        var kazanan = Guid.NewGuid();
        var kaybeden = Guid.NewGuid();

        repo.BeginApply(job.Id, kazanan).Should().BeTrue();
        repo.BeginApply(job.Id, kaybeden).Should().BeFalse(); // anahtar zaten diskte

        var guncel = repo.Get(job.Id)!;
        guncel.ApplyKey.Should().Be(kazanan); // kaybedenin anahtarı ASLA yazılmaz
        guncel.State.Should().Be(PaymentJobState.ApplyUncertain); // anahtar diskte = belirsiz
    }

    [Fact]
    public void MarkApplied_SonucuVeTutariYazar()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.BeginApply(job.Id, Guid.NewGuid());
        repo.MarkApplied(job.Id, 42.5m);

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.AppliedAmount.Should().Be(42.5m);
    }

    [Fact]
    public void MarkNoBalance_SifirTutarlaKesinlesir()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.MarkNoBalance(job.Id);

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.NoBalance);
        guncel.AppliedAmount.Should().Be(0m);
    }

    [Fact]
    public void Close_Idempotent_IlkKapanisZamaniKorunur()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.Close(job.Id);
        var ilkKapanis = repo.Get(job.Id)!.ClosedAt;
        ilkKapanis.Should().NotBeNull();

        repo.Close(job.Id); // ikinci çağrı zamanı EZMEZ
        repo.Get(job.Id)!.ClosedAt.Should().Be(ilkKapanis);
    }

    [Fact]
    public void BeginRevision_BeklenenRevizyonTutuyorsa_YeniAnahtarVeToplamYazar()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.BeginApply(job.Id, Guid.NewGuid());
        repo.MarkApplied(job.Id, 40m);

        repo.Close(job.Id); // teslim edilmişti; tutar düzeltmesi işi yeniden açar (R2-02)

        var yeniKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 150m, yeniKey, expectedRevision: 0).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.Revision.Should().Be(1);
        guncel.ProductTotal.Should().Be(150m);
        guncel.ApplyKey.Should().Be(yeniKey);
        guncel.AppliedAmount.Should().BeNull();
        guncel.State.Should().Be(PaymentJobState.ApplyUncertain); // yeni anahtar diskte = belirsiz
        guncel.ClosedAt.Should().BeNull("revizyon kapanmış işi yeniden açar");
    }

    [Fact]
    public void BeginRevision_RevizyonKaymissa_ReddederVeHicbirSeyiDegistirmez()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        var kazananKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 150m, kazananKey, expectedRevision: 0).Should().BeTrue();

        repo.BeginRevision(job.Id, 200m, Guid.NewGuid(), expectedRevision: 0).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.Revision.Should().Be(1);
        guncel.ProductTotal.Should().Be(150m);
        guncel.ApplyKey.Should().Be(kazananKey);
    }

    [Fact]
    public void GetOpenLegacy_YalnizAcikLegacyIsiniDondurur()
    {
        var (_, repo) = Fx();
        repo.GetOpenLegacy("c1").Should().BeNull();

        var legacy = repo.FindOrCreate("c1", "legacy", 100m);
        repo.GetOpenLegacy("c1")!.Id.Should().Be(legacy.Id);

        repo.Close(legacy.Id);
        repo.GetOpenLegacy("c1").Should().BeNull(); // kapalı legacy görünmez
    }

    [Fact]
    public void AdoptLegacyResult_TazeHedefeSonucuKopyalarVeLegacyyiKapatir()
    {
        var (_, repo) = Fx();
        var legacy = repo.FindOrCreate("c1", "legacy", 250.75m);
        var legacyKey = Guid.NewGuid();
        repo.BeginApply(legacy.Id, legacyKey);
        repo.MarkApplied(legacy.Id, 50m);

        var hedef = repo.FindOrCreate("c1", "session:s1", 250.75m);
        repo.AdoptLegacyResult(hedef.Id, legacy.Id);

        var guncelHedef = repo.Get(hedef.Id)!;
        guncelHedef.ApplyKey.Should().Be(legacyKey);
        guncelHedef.AppliedAmount.Should().Be(50m);
        guncelHedef.State.Should().Be(PaymentJobState.Applied);
        guncelHedef.ProductTotal.Should().Be(250.75m);

        repo.Get(legacy.Id)!.ClosedAt.Should().NotBeNull();
        repo.GetOpenLegacy("c1").Should().BeNull();
    }

    [Fact]
    public void AdoptLegacyResult_HedefTazeDegilse_HedefiEzmezAmaLegacyyiKapatir()
    {
        var (_, repo) = Fx();
        var legacy = repo.FindOrCreate("c1", "legacy", 100m);
        repo.BeginApply(legacy.Id, Guid.NewGuid());

        var hedef = repo.FindOrCreate("c1", "session:s1", 100m);
        var hedefKey = Guid.NewGuid();
        repo.BeginApply(hedef.Id, hedefKey); // hedef kendi anahtarını yazmış

        repo.AdoptLegacyResult(hedef.Id, legacy.Id);

        repo.Get(hedef.Id)!.ApplyKey.Should().Be(hedefKey); // dokunulmadı
        repo.Get(legacy.Id)!.ClosedAt.Should().NotBeNull();
    }
}
```

**Adım 2 — koş, KIRMIZI bekle** (derlenmez: tipler yok):
```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~PaymentJobRepositoryTests"
```

**Adım 3 — depoyu yaz** (`PaymentJobRepository.cs`, tamamı):

```csharp
using System;
using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>PaymentJob durumları. String sabitler — SQLite'ta TEXT saklanır,
/// enum sıra değişikliği veri bozamasın.</summary>
public static class PaymentJobState
{
    /// <summary>İş var, henüz anahtar diske inmedi — sunucuya hiçbir şey gitmedi.</summary>
    public const string Created = "created";
    /// <summary>Anahtar diskte; sunucu cevabı KESİNLEŞMEDİ. Bu durumda mesaj gönderilmez.</summary>
    public const string ApplyUncertain = "apply_uncertain";
    /// <summary>Düşüm uygulandı; AppliedAmount dolu.</summary>
    public const string Applied = "applied";
    /// <summary>Sunucu kesin cevap verdi: bakiye yok / uygulanacak şey yok. AppliedAmount = 0.</summary>
    public const string NoBalance = "no_balance";
}

/// <summary>Bir "Ödeme iste" satışının kalıcı kimliği ve düşüm sonucu.
/// Kapsam: (CustomerId, ScopeKey) — "session:{id}" | "cumulative" | "legacy".
/// İşler ASLA silinmez; teslimatta ClosedAt dolar.</summary>
public sealed record PaymentJob(
    string Id,
    string CustomerId,
    string ScopeKey,
    decimal ProductTotal,
    int Revision,
    Guid? ApplyKey,
    decimal? AppliedAmount,
    string State,
    long CreatedAt,
    long UpdatedAt,
    long? ClosedAt);

/// <summary>R2-01..04: ödeme işi yaşam döngüsünün disk katmanı. Tüm geçişler
/// koşullu UPDATE'lerle yarışa dayanıklı; servis katmanı false dönüşünde
/// satırı yeniden okuyup kazananın yazdığını kullanır.</summary>
public interface IPaymentJobStore
{
    /// <summary>Atomik find-or-create: UNIQUE (CustomerId, ScopeKey) +
    /// INSERT OR IGNORE. İki eşzamanlı çağrı aynı satırı görür (R2-04).</summary>
    PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal);

    PaymentJob? Get(string id);

    /// <summary>033'ten taşınan, henüz kapanmamış 'legacy' işi; yoksa null.</summary>
    PaymentJob? GetOpenLegacy(string customerId);

    /// <summary>Anahtarı diske indirir ve işi apply_uncertain'e geçirir —
    /// yalnız anahtar HENÜZ yoksa. false = yarışı kaybettik; yeniden oku,
    /// kazananın anahtarıyla devam et.</summary>
    bool BeginApply(string id, Guid applyKey);

    void MarkApplied(string id, decimal appliedAmount);

    /// <summary>Kesin "bakiye yok" cevabı — AppliedAmount 0 yazılır.</summary>
    void MarkNoBalance(string id);

    /// <summary>Sonuç yeniden belirsizleşti (ör. geri alma ağda kayboldu).</summary>
    void MarkUncertain(string id);

    /// <summary>Mesaj müşteriye ulaştı — iş kapanır. Idempotent (ilk zaman korunur).</summary>
    void Close(string id);

    /// <summary>Revizyon: yeni toplam + yeni anahtar + Revision+1, eski sonuç
    /// sıfırlanır, durum apply_uncertain. Yalnız Revision == expectedRevision
    /// ise — false = eşzamanlı revizyon kazandı, yeniden oku.</summary>
    bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision);

    /// <summary>Legacy işin sonucunu (ApplyKey/AppliedAmount/State/ProductTotal)
    /// TAZE hedefe kopyalar ve legacy'yi kapatır — tek transaction. Hedef taze
    /// değilse (anahtarı varsa) hedefe dokunmaz, yine de legacy'yi kapatır.</summary>
    void AdoptLegacyResult(string targetId, string legacyId);
}

public sealed class PaymentJobRepository : IPaymentJobStore
{
    private readonly IDbConnectionFactory _factory;
    public PaymentJobRepository(IDbConnectionFactory factory) => _factory = factory;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
    {
        using var conn = _factory.Open();
        var now = Now();
        conn.Execute(
            @"INSERT OR IGNORE INTO PaymentJob
              (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
               AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt)
              VALUES (@id, @customerId, @scopeKey, @total, 0, NULL,
                      NULL, @state, @now, @now, NULL)",
            new
            {
                id = Guid.NewGuid().ToString("N"),
                customerId,
                scopeKey,
                total = Dec(productTotal),
                state = PaymentJobState.Created,
                now,
            });
        var row = conn.QuerySingle<Row>(
            SelectSql + " WHERE CustomerId=@customerId AND ScopeKey=@scopeKey",
            new { customerId, scopeKey });
        return Map(row);
    }

    public PaymentJob? Get(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(SelectSql + " WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public PaymentJob? GetOpenLegacy(string customerId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            SelectSql + " WHERE CustomerId=@customerId AND ScopeKey='legacy' AND ClosedAt IS NULL",
            new { customerId });
        return row is null ? null : Map(row);
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ApplyKey=@key, State=@state, UpdatedAt=@now
              WHERE Id=@id AND ApplyKey IS NULL",
            new
            {
                id,
                key = applyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
            }) == 1;
    }

    public void MarkApplied(string id, decimal appliedAmount)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, AppliedAmount=@amt, UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.Applied, amt = Dec(appliedAmount), now = Now() });
    }

    public void MarkNoBalance(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, AppliedAmount='0', UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.NoBalance, now = Now() });
    }

    public void MarkUncertain(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.ApplyUncertain, now = Now() });
    }

    public void Close(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@id",
            new { id, now = Now() });
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ProductTotal=@total, Revision=Revision+1, ApplyKey=@key,
                  AppliedAmount=NULL, State=@state, UpdatedAt=@now, ClosedAt=NULL
              WHERE Id=@id AND Revision=@expectedRevision",
            new
            {
                id,
                total = Dec(newProductTotal),
                key = newApplyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
                expectedRevision,
            }) == 1;
    }

    public void AdoptLegacyResult(string targetId, string legacyId)
    {
        using var connRaw = _factory.Open();
        var conn = (System.Data.Common.DbConnection)connRaw;
        using var tx = conn.BeginTransaction();
        var now = Now();
        conn.Execute(
            @"UPDATE PaymentJob SET
                  ProductTotal  = (SELECT ProductTotal  FROM PaymentJob WHERE Id=@legacyId),
                  ApplyKey      = (SELECT ApplyKey      FROM PaymentJob WHERE Id=@legacyId),
                  AppliedAmount = (SELECT AppliedAmount FROM PaymentJob WHERE Id=@legacyId),
                  State         = (SELECT State         FROM PaymentJob WHERE Id=@legacyId),
                  UpdatedAt     = @now
              WHERE Id=@targetId AND ApplyKey IS NULL AND State=@created",
            new { targetId, legacyId, now, created = PaymentJobState.Created },
            tx);
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@legacyId",
            new { legacyId, now },
            tx);
        tx.Commit();
    }

    private const string SelectSql =
        @"SELECT Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
                 AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt
          FROM PaymentJob";

    private static PaymentJob Map(Row r) => new(
        r.Id,
        r.CustomerId,
        r.ScopeKey,
        decimal.Parse(r.ProductTotal, CultureInfo.InvariantCulture),
        r.Revision,
        r.ApplyKey is null ? null : Guid.ParseExact(r.ApplyKey, "N"),
        r.AppliedAmount is null
            ? null
            : decimal.Parse(r.AppliedAmount, CultureInfo.InvariantCulture),
        r.State,
        r.CreatedAt,
        r.UpdatedAt,
        r.ClosedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string ScopeKey { get; init; } = "";
        public string ProductTotal { get; init; } = "0";
        public int Revision { get; init; }
        public string? ApplyKey { get; init; }
        public string? AppliedAmount { get; init; }
        public string State { get; init; } = "";
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; init; }
        public long? ClosedAt { get; init; }
    }
}
```

> Not: `AdoptLegacyResult` içindeki `DbConnection` cast'i `SqliteConnection`
> için güvenli (factory hep onu döner). `IDbConnection.BeginTransaction()`
> da vardır; Dapper'a `transaction:` geçebilmek için hangisi projede kullanılan
> kalıpsa ona uy (mevcut depolarında transaction örneği yoksa
> `conn.BeginTransaction()`'ı `IDbConnection` üzerinden çağır — cast gereksizse kaldır).

**Adım 4 — koş, YEŞİL bekle** (aynı filter, sonra tüm `OrderDeck.Tests`).

**Adım 5 — commit:**
```
feat(storage): PaymentJobRepository — koşullu BeginApply/BeginRevision ile yarışa dayanıklı durum makinesi

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 3: DI kaydı + PR1'i aç

**Dosya:** `OrderDeck.App/AppHost.cs`

**Adım 1:** Satır ~86'daki mevcut kaydın ALTINA yeni kayıt ekle (eskisi PR2'ye
kadar kalır — servis hâlâ ona bağlı):

```csharp
services.AddSingleton<IPendingBalanceApplyStore, PendingBalanceApplyRepository>();
services.AddSingleton<IPaymentJobStore, PaymentJobRepository>();
```

**Adım 2 — doğrula:**
```
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```

**Adım 3 — commit + PR:**
```
feat(storage): PaymentJob deposu DI kaydı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```
PR başlığı: `feat(payment-job): kalıcı ödeme işi deposu — migration 034 + PaymentJobRepository (R2 paket 1/4)`
Gövdede: spec linki, "davranış değişikliği YOK — servis hâlâ 033 akışında,
tablo taşındı ama eski depo sınıfı ölü kod olarak PR2'ye kadar duruyor" notu.
⚠️ Bu PR'da eski `PendingBalanceApplyRepository` çalışma zamanında KIRIK
(tablosu yok) — PR1 ile PR2 arasında Velopack sürümü YAYINLANMAZ.

---

## PR4 — Sunucu: A11 content-conflict + reverse ucu (⚠️ merge = prod deploy)

Branch: master'dan `feat/balance-reverse-endpoint`. **PR2'den ÖNCE merge edilmeli.**
Eski istemci + yeni sunucu güvenli: content-conflict 409'u eski istemcinin genel
catch'ine düşer → düşüm uygulanmaz, mesaj bakiyesiz gider (mevcut fail-silent davranış).

### Task 4: A11 — aynı anahtar + farklı içerik = content-conflict

**Dosyalar:**
- `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesCustomerBalanceApplyControllerTests.cs`

**Adım 1 — başarısız testleri yaz** (test sınıfının sonuna ekle; mevcut
`SetupWithBalanceAsync` + anonim gövde kalıbı aynen):

```csharp
    // ── A11: idempotency anahtarı + İÇERİK sözleşmesi ───────────────────────
    // Replay yalnız istek İLK isteğin aynısıysa meşru. Farklı müşteri/toplam
    // ile gelen aynı anahtar bir istemci hatasıdır; ilk sonucu oynatmak yanlış
    // satışa düşüm bağlar. 409 content-conflict + SIFIR yan etki.

    [Fact]
    public async Task Apply_same_key_different_product_total_returns_content_conflict()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var key = Guid.NewGuid();

        var ilk = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        ilk.StatusCode.Should().Be(HttpStatusCode.OK);

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 999m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("content-conflict");

        // Yan etki yok: bakiye ilk düşümden sonraki değerde kalmalı.
        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(400m);
    }

    [Fact]
    public async Task Apply_same_key_different_customer_returns_content_conflict()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var key = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = Guid.NewGuid(), Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("content-conflict");
    }

    [Fact]
    public async Task Apply_same_key_larger_amount_still_replays()
    {
        // Toleranslı yön: replay Amount >= ilk düşüm olduğu sürece meşru —
        // istemci replay'de Amount=ProductTotal gönderir (sunucu zaten kırpar).
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(100m);
        var key = Guid.NewGuid();

        var ilk = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 100m, ProductTotal = 2100m, IdempotencyKey = key });
        var ilkBody = await ilk.Content.ReadFromJsonAsync<ApplyResponse>();

        var ikinci = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = 2100m, ProductTotal = 2100m, IdempotencyKey = key });
        ikinci.StatusCode.Should().Be(HttpStatusCode.OK);
        var ikinciBody = await ikinci.Content.ReadFromJsonAsync<ApplyResponse>();
        ikinciBody!.AppliedAmount.Should().Be(ilkBody!.AppliedAmount);
    }
```

Sınıfa (yoksa) küçük bir problem DTO'su ekle:

```csharp
    private sealed record ProblemDetailsLite(string? Title, string? Detail, int? Status);
```

(Dosyada zaten ProblemDetails okuyan bir kalıp varsa onu kullan, yenisini ekleme.)

**Adım 2 — koş, KIRMIZI bekle:**
```
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests"
```
İlk iki test düşer (bugün replay dönüyor), üçüncü geçer (regresyon çıpası).

**Adım 3 — controller'ı değiştir.** `LookupAsync` istek içeriğini de doğrular:

İmza ve dönüş üçlüye çıkar:

```csharp
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid key, CancellationToken ct)
```

Gövdede `if (tx is null) return (null, false, false);` ve foreign dalı
`return (null, true, false);` olur. Foreign dalının HEMEN ARDINA içerik
kontrolü girer (A11):

```csharp
        // A11: anahtar bu lisansın düşüm satırı ama istek İLK isteğin aynısı
        // değil. Oynatmak yanlış satışa düşüm bağlar; hiçbir yan etki olmadan
        // reddet. Amount için tolerans: istemci replay'de Amount=ProductTotal
        // gönderir; ilk düşümden KÜÇÜK bir Amount ise gerçek bir çelişkidir.
        if (tx.WpfCustomerId != req.WpfCustomerId
            || tx.OriginalAmount != req.ProductTotal
            || -tx.Amount > req.Amount)
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı farklı içerikle yeniden kullanıldı (key={Key}, license={LicenseId})",
                key, licenseId);
            return (null, false, true);
        }
```

Son dönüş `return (new ApplyResponse(tx.Id, -tx.Amount, remaining), false, false);`.

İki çağıran güncellenir — ön kontrol (mevcut satır ~112):

```csharp
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, preKey, ct);
            if (foreign) return NotFound();
            if (conflict)
                return Problem(title: "content-conflict", statusCode: 409,
                    detail: "Idempotency anahtarı farklı bir istekle kullanılmış.");
            if (replay is not null) return Ok(replay);
        }
```

ve PK-yarışı dalı (mevcut satır ~188, `DbUpdateException` catch'i):

```csharp
            catch (DbUpdateException) when (req.IdempotencyKey is not null)
            {
                _db.ChangeTracker.Clear();
                var (winner, _, conflict) = await LookupAsync(licenseId, req, req.IdempotencyKey.Value, ct);
                if (conflict)
                    return Problem(title: "content-conflict", statusCode: 409,
                        detail: "Idempotency anahtarı farklı bir istekle kullanılmış.");
                if (winner is null) throw;
                _log.LogWarning(
                    "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                    req.IdempotencyKey, licenseId);
                return Ok(winner);
            }
```

**Adım 4 — koş, YEŞİL bekle** (aynı filter; mevcut idempotency testleri de
geçmeli — içerikleri birebir aynı olduğu için replay bozulmaz).

**Adım 5 — commit:**
```
feat(server): A11 — bakiye apply idempotency anahtarında içerik doğrulaması (409 content-conflict)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 5: WPF yüzeyine reverse ucu

**Dosyalar:** aynı controller + aynı test dosyası.

**Adım 1 — başarısız testleri yaz** (sınıf sonuna):

```csharp
    // ── Reverse (revizyon akışının sunucu yarısı) ───────────────────────────
    // WPF, aynı yayında toplam değişince eski düşümü geri alıp yeni toplamla
    // taze düşüm yapar (spec K2). Bu uç panel'deki reverse'in WPF-yüzeyi
    // ikizidir: yalnız kendi lisansının purchase-deduction satırını geri
    // alabilir, hakem N01 filtered-unique index'tir.

    private async Task<Guid> ApplyAndGetTransactionIdAsync(
        HttpClient client, Guid licenseId, Guid wpfCustomerId, decimal amount, decimal productTotal)
    {
        var key = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply",
            new { WpfCustomerId = wpfCustomerId, Amount = amount, ProductTotal = productTotal, IdempotencyKey = key });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApplyResponse>();
        return body!.TransactionId;
    }

    [Fact]
    public async Task Reverse_restores_balance_and_writes_reversal_row()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(client, licenseId, wpfCustomerId, 100m, 2100m);

        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(500m); // düşüm geri geldi
    }

    [Fact]
    public async Task Reverse_second_call_returns_already_reversed()
    {
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(client, licenseId, wpfCustomerId, 100m, 2100m);

        await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);
        var ikinci = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{txId}/reverse", null);

        ikinci.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ikinci.Content.ReadFromJsonAsync<ProblemDetailsLite>();
        problem!.Title.Should().Be("already-reversed");

        var preview = await client.GetFromJsonAsync<PreviewResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview?wpfCustomerId={wpfCustomerId}");
        preview!.Balance.Should().Be(500m); // ikinci geri alma para EKLEMEDİ
    }

    [Fact]
    public async Task Reverse_unknown_transaction_returns_404()
    {
        var (client, licenseId, _) = await SetupWithBalanceAsync(500m);
        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{Guid.NewGuid()}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reverse_non_deduction_transaction_returns_404()
    {
        // Seed'deki refund-full satırı bu ucun kapsamı dışında — müşteri yüzeyi
        // yalnız KENDİ purchase-deduction'ını geri alabilir.
        var (client, licenseId, wpfCustomerId) = await SetupWithBalanceAsync(500m);
        Guid refundTxId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            refundTxId = db.CustomerBalanceTransactions
                .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == wpfCustomerId
                    && t.Kind == "refund-full")
                .Select(t => t.Id)
                .Single();
        }

        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{refundTxId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reverse_foreign_license_returns_404()
    {
        var (clientA, licenseA, wpfCustomerA) = await SetupWithBalanceAsync(500m);
        var txId = await ApplyAndGetTransactionIdAsync(clientA, licenseA, wpfCustomerA, 100m, 2100m);

        var (clientB, licenseB, _) = await SetupWithBalanceAsync(100m);
        var resp = await clientB.PostAsync(
            $"/api/v1/licenses/{licenseB}/customer-balance/transactions/{txId}/reverse", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

(`System.Linq` using'i dosyada yoksa ekle.)

**Adım 2 — koş, KIRMIZI bekle** (aynı filter — reverse route'u yok, 404/405
düşer; `Reverse_unknown_transaction_returns_404` yanlışlıkla yeşilse bile
diğerleri kırmızı).

**Adım 3 — controller'a Reverse action'ı ekle** (`OwnsLicenseAsync`'in üstüne):

```csharp
    // ── POST reverse (revizyon: eski düşümü geri al) ────────────────────────

    /// <summary>Spec K2: aynı satışın toplamı değişince WPF eski düşümü geri
    /// alıp yeni toplamla taze apply yapar. Panel'deki reverse'in WPF-yüzeyi
    /// ikizi — ama kapsamı dar: yalnız BU lisansın purchase-deduction satırı.
    /// Hakem N01 filtered-unique index (ReversesTransactionId): yarışan ikinci
    /// reverse SaveChanges'te unique ihlali alır → 409 already-reversed.
    /// İstemci 409 already-reversed'i BAŞARI sayar (geri alma zaten olmuş).</summary>
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var original = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        if (original is null) return NotFound();

        // Hızlı yol ön kontrolü; asıl hakem N01 index'i (aşağıdaki catch).
        var alreadyReversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == transactionId, ct);
        if (alreadyReversed) return Problem(title: "already-reversed", statusCode: 409);

        var balance = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId
                && b.WpfCustomerId == original.WpfCustomerId, ct);
        // Apply bakiye satırı olmadan düşüm yazmaz; satır silinmiyor da.
        if (balance is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var reverseAmount = -original.Amount; // düşüm negatif → geri alma pozitif

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = original.WpfCustomerId,
            Amount = reverseAmount,
            Kind = "reversal",
            OriginalAmount = null,
            Reason = $"Reverse of {transactionId:N}",
            ReversesTransactionId = transactionId,
            CreatedByCustomerId = User.GetTenantCustomerId(),
            CreatedAt = now,
        });

        balance.Balance += reverseAmount;
        balance.UpdatedAt = now;

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return Ok();
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                // F02 kalıbı: bakiyeyi güncel değere çek, deltayı yeniden uygula.
                // Geri alma para EKLER — negatif kontrolü gerekmez.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                balance.Balance += reverseAmount;
                balance.UpdatedAt = DateTimeOffset.UtcNow;
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                // N01: eşzamanlı ikinci reverse yarışı kaybetti; transaction
                // geri alındı, bakiyeye hiçbir şey yazılmadı.
                _db.ChangeTracker.Clear();
                return Problem(title: "already-reversed", statusCode: 409);
            }
        }
    }

    /// <summary>N01 hakemi (PanelCustomerBalanceController'daki ile aynı):
    /// 2601/2627 = unique ihlali; index adı filtresi, başka unique yarışlarının
    /// aynı koda karışmasını önler.</summary>
    private static bool IsDuplicateReversal(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("ReversesTransactionId", StringComparison.Ordinal);
```

**Adım 4 — koş, YEŞİL bekle**; sonra TÜM sunucu paketi (Docker açık):
```
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

**Adım 5 — commit:**
```
feat(server): WPF yüzeyine bakiye düşümü geri alma ucu (revizyon akışı, K2)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 6: LicenseApiClient.ReverseBalanceTransactionAsync + PR4'ü aç

**Dosya:** `OrderDeck.Licensing/Api/LicenseApiClient.cs` — `ApplyBalanceAsync`'in
hemen altına (satır ~380):

```csharp
    /// <summary>Bir purchase-deduction satırını geri alır (revizyon akışı, K2).
    /// <paramref name="transactionId"/> = apply'da kullanılan idempotency
    /// anahtarı (ledger PK). Sunucu 409 already-reversed dönerse çağıran bunu
    /// BAŞARI saymalı — geri alma zaten yapılmış.</summary>
    public async Task ReverseBalanceTransactionAsync(
        Guid licenseId, Guid transactionId, CancellationToken ct = default)
    {
        var url = $"api/v1/licenses/{licenseId}/customer-balance/transactions/{transactionId}/reverse";
        using var resp = await SendJsonAsync(HttpMethod.Post, url, new { }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
    }
```

> `SendJsonAsync`/`ThrowMappedAsync` dosyadaki panel-yüzeyi metotların
> (`AddRefundFullAsync` vb.) kullandığı mevcut yardımcılar; imzaları oradakiyle
> birebir aynı kullanılmalı. 409 problem `title`'ı `ThrowMappedAsync`'te
> `ValidationException.Code`'a dönüşür — istemci `"already-reversed"` görür.

**Doğrulama:** `dotnet build OrderDeck.Licensing/OrderDeck.Licensing.csproj`
(bu metodun birim testi PR2'deki servis testlerinden gelir — stub handler
reverse route'unu da taklit edecek).

**Commit + PR:**
```
feat(licensing): ReverseBalanceTransactionAsync istemci metodu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```
PR başlığı: `feat(server): bakiye apply içerik doğrulaması + reverse ucu (R2 paket 2/4)`
Gövde: spec §A11 + K2 linki; "eski istemci güvenli — yeni 409'lar genel catch'e
düşer, düşümsüz mesaj = mevcut davranış" notu; **merge prod'a çıkar,
kullanıcı yayında değilken merge et.**

---

## PR2 — PaymentRequestService durum makinesi

Branch: master'dan `feat/payment-job-service` (**PR1 ve PR4 merge edildikten sonra**).

### Task 7: Test altyapısı — InMemoryPaymentJobStore + stub eklentileri

**Dosyalar:**
- Yeni: `OrderDeck.Tests/Fakes/InMemoryPaymentJobStore.cs`
- Sil: `OrderDeck.Tests/Fakes/InMemoryPendingBalanceApplyStore.cs` (Task 9'da, servisle birlikte)
- Değiştir: `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` içindeki `WhatsAppStubHandler`

**Fake** (`InMemoryPaymentJobStore.cs`, tamamı — gerçek deponun koşullu-UPDATE
sözleşmesini birebir taklit eder, kilitli çünkü A9 yarış testi paralel çağırır):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Tests.Fakes;

public sealed class InMemoryPaymentJobStore : IPaymentJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PaymentJob> _jobs = new();

    /// <summary>Testlerin durum iddiaları için anlık kopya.</summary>
    public IReadOnlyList<PaymentJob> Snapshot
    {
        get { lock (_gate) return _jobs.Values.ToList(); }
    }

    /// <summary>Test tohumu (ör. 034'ten taşınmış legacy iş).</summary>
    public PaymentJob Seed(PaymentJob job)
    {
        lock (_gate) { _jobs[job.Id] = job; return job; }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
    {
        lock (_gate)
        {
            var existing = _jobs.Values.FirstOrDefault(
                j => j.CustomerId == customerId && j.ScopeKey == scopeKey);
            if (existing is not null) return existing;
            var now = Now();
            var job = new PaymentJob(
                Guid.NewGuid().ToString("N"), customerId, scopeKey, productTotal,
                0, null, null, PaymentJobState.Created, now, now, null);
            _jobs[job.Id] = job;
            return job;
        }
    }

    public PaymentJob? Get(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public PaymentJob? GetOpenLegacy(string customerId)
    {
        lock (_gate)
            return _jobs.Values.FirstOrDefault(j =>
                j.CustomerId == customerId && j.ScopeKey == "legacy" && j.ClosedAt is null);
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.ApplyKey is not null) return false;
            _jobs[id] = j with
            {
                ApplyKey = applyKey,
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public void MarkApplied(string id, decimal appliedAmount)
    {
        lock (_gate)
            _jobs[id] = _jobs[id] with
            {
                State = PaymentJobState.Applied,
                AppliedAmount = appliedAmount,
                UpdatedAt = Now(),
            };
    }

    public void MarkNoBalance(string id)
    {
        lock (_gate)
            _jobs[id] = _jobs[id] with
            {
                State = PaymentJobState.NoBalance,
                AppliedAmount = 0m,
                UpdatedAt = Now(),
            };
    }

    public void MarkUncertain(string id)
    {
        lock (_gate)
            _jobs[id] = _jobs[id] with
            {
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
            };
    }

    public void Close(string id)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            _jobs[id] = j with { ClosedAt = j.ClosedAt ?? Now(), UpdatedAt = Now() };
        }
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.Revision != expectedRevision) return false;
            _jobs[id] = j with
            {
                ProductTotal = newProductTotal,
                Revision = j.Revision + 1,
                ApplyKey = newApplyKey,
                AppliedAmount = null,
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
                ClosedAt = null, // revizyon kapanmış işi yeniden açar (R2-02)
            };
            return true;
        }
    }

    public void AdoptLegacyResult(string targetId, string legacyId)
    {
        lock (_gate)
        {
            var target = _jobs[targetId];
            var legacy = _jobs[legacyId];
            if (target.ApplyKey is null && target.State == PaymentJobState.Created)
                _jobs[targetId] = target with
                {
                    ProductTotal = legacy.ProductTotal,
                    ApplyKey = legacy.ApplyKey,
                    AppliedAmount = legacy.AppliedAmount,
                    State = legacy.State,
                    UpdatedAt = Now(),
                };
            _jobs[legacyId] = legacy with { ClosedAt = legacy.ClosedAt ?? Now(), UpdatedAt = Now() };
        }
    }
}
```

**Stub eklentileri** (`WhatsAppStubHandler` içine — mevcut route yapısına uy):

1. Yeni ayar alanları:
```csharp
    /// <summary>Apply çağrısı bu problem title'ı ile 409 dönsün (null = normal).</summary>
    public string? ApplyProblemTitle { get; set; }
    /// <summary>Apply çağrısı ağ hatası fırlatsın (gövde YİNE kaydedilir — istek tele çıktı).</summary>
    public bool ThrowTimeoutOnApply { get; set; }
    /// <summary>Reverse çağrılarında yakalanan transactionId'ler.</summary>
    public List<Guid> ReverseCalls { get; } = new();
    public string? ReverseProblemTitle { get; set; }
    public bool ThrowTimeoutOnReverse { get; set; }
    /// <summary>A9: preview cevabından önce beklenir (yarış rendezvous'u).</summary>
    public Func<Task>? OnPreviewAsync { get; set; }
```
2. Route davranışları:
   - `*/customer-balance/preview`: cevaptan önce `if (OnPreviewAsync is not null) await OnPreviewAsync();`
   - `*/customer-balance/apply`: gövdeyi `AppliedBalanceBodies`'e **ekledikten sonra**
     `ThrowTimeoutOnApply` ise `throw new TaskCanceledException("stub timeout")`;
     `ApplyProblemTitle` doluysa `Problem(ApplyProblemTitle)` dön.
   - Yeni route `*/customer-balance/transactions/{id}/reverse` (POST): URL'den Guid'i
     ayıkla → `ReverseCalls.Add(id)`; `ThrowTimeoutOnReverse` ise fırlat;
     `ReverseProblemTitle` doluysa `Problem(...)`; değilse `200 {}`.
   - Problem yardımcısı:
```csharp
    private static HttpResponseMessage Problem(string title) => new((HttpStatusCode)409)
    {
        Content = new StringContent(
            $"{{\"title\":\"{title}\",\"status\":409}}",
            Encoding.UTF8, "application/problem+json"),
    };
```
3. A9 paralel çağırdığı için `SentBodies`/`AppliedBalanceBodies`/`ReverseCalls`
   yazımlarını `lock`'a al (küçük bir `private readonly object _sync = new();`).

> `Problem(...)`'ın döndüğü gövde `ThrowMappedAsync`'in title'ı
> `ValidationException.Code`'a çevirdiği biçimle uyumlu olmalı — sunucu
> testlerindeki gerçek ProblemDetails çıktısıyla karşılaştırıp gerekirse alan
> ekle (`type`/`detail` zorunlu değil).

**Doğrulama:** `dotnet build OrderDeck.Tests/OrderDeck.Tests.csproj` (yalnız
yeni dosya + stub derlenir; testler Task 8'de).

**Commit:**
```
test(payment): InMemoryPaymentJobStore + stub'a apply/reverse hata modları

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 8: A1-A8 + legacy servis testleri (KIRMIZI)

**Dosya:** `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs`

Hazırlık (mekanik):
- Sınıf alanı ekle: `private readonly InMemoryPaymentJobStore _jobs = new();`
  `MakeCloudSut` servisi kurarken `_pending` yerine `_jobs`'u geçecek (Task 9'da
  imza değişince); şimdilik alan dursun.
- `OpenWhatsAppAsync` YENİ imza alacak: `(customer, productTotal, streamDate,
  scopeKey, ct = default)` — **scopeKey zorunlu**. Dosyadaki TÜM mevcut
  çağrılara 4. argüman olarak `"cumulative"` ekle (bakiye yoluna girmeyen
  testlerde değer önemsiz, sadece derlesin).
- `// ── N02 ...` bölge başlığından dosya sonuna kadarki 5 test SİLİNİR,
  yerine aşağıdaki bölge gelir (`AppliedKey` yardımcısı KALIR).

```csharp
    // ── R2-01..04: ödeme işi yaşam döngüsü ──────────────────────────────────
    //
    // Anahtar diske iner (created→apply_uncertain), sunucu cevabı kesinleşince
    // applied/no_balance, mesaj müşteriye ulaşınca iş kapanır. Aynı kapsamda
    // (müşteri+oturum) tutar değişirse revizyon: eski düşüm geri alınır, yeni
    // toplamla taze düşüm. Belirsizlikte mesaj GÖNDERİLMEZ (BalanceUncertain).

    private static readonly DateTime T = new(2026, 9, 11);

    [Fact] // A1
    public async Task OpenWhatsAppAsync_taze_satis_dusum_uygulanir_teslimatta_is_kapanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle();
        var job = _jobs.Snapshot.Single();
        job.ScopeKey.Should().Be("session:s1");
        job.State.Should().Be(PaymentJobState.Applied);
        job.ApplyKey.Should().Be(AppliedKey(handler.AppliedBalanceBodies[0]));
        job.ClosedAt.Should().NotBeNull("mesaj müşteriye ulaştı");
    }

    [Fact] // A2 — eski N02 çekirdeği, iş anlambilimiyle
    public async Task OpenWhatsAppAsync_LaunchFailed_sonrasi_tekrar_ayni_anahtari_kullanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.LaunchFailed);
        _jobs.Snapshot.Single().ClosedAt.Should().BeNull("mesaj ulaşmadı — iş açık");

        _launcher.ThrowOnLaunch = null;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
            "aynı iş = aynı anahtar; sunucu ilk sonucu oynatır, bakiye ikinci kez düşmez");
        _jobs.Snapshot.Single().ClosedAt.Should().NotBeNull();
    }

    [Fact] // A3
    public async Task OpenWhatsAppAsync_bakiye_yoksa_no_balance_kesinlesir_mesaj_dusussuz_gider()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 0m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().BeEmpty("önizleme 0 — anahtar hiç yazılmaz");
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.NoBalance);
        job.AppliedAmount.Should().Be(0m);
        job.ClosedAt.Should().NotBeNull();
    }

    [Fact] // A4 — K3: belirsizlik mesajı BLOKLAR
    public async Task OpenWhatsAppAsync_apply_belirsiz_kalirsa_mesaj_gonderilmez_tekrar_ayni_anahtarla_cozulur()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        handler.ThrowTimeoutOnApply = true;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty("düşüm belirsizken mesaj gitmez");
        handler.SentBodies.Should().BeEmpty();
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.ApplyUncertain);
        job.ClosedAt.Should().BeNull();

        handler.ThrowTimeoutOnApply = false;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
            "çözüm aynı anahtarın replay'i — asla yeni anahtar üretilmez");
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // A5 — tekrar paylaşım: finansal çağrı YOK
    public async Task OpenWhatsAppAsync_ayni_kapsam_ayni_tutar_ikinci_cagri_finansal_cagri_yapmaz()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "aynı satışın tekrar paylaşımı — düşüm sonucu diskten okunur");
        handler.ReverseCalls.Should().BeEmpty();
        _launcher.LaunchedUrls.Should().HaveCount(2);
    }

    [Fact] // A5' — farklı oturum = yeni iş (eski "sonraki satış yeni anahtar")
    public async Task OpenWhatsAppAsync_farkli_oturum_yeni_is_yeni_anahtar_uretir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s2"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        AppliedKey(handler.AppliedBalanceBodies[1]).Should().NotBe(
            AppliedKey(handler.AppliedBalanceBodies[0]),
            "ayrı oturum ayrı satış — ayrı iş, ayrı düşüm");
        _jobs.Snapshot.Should().HaveCount(2);
    }

    [Fact] // A6 — K2: revizyon = geri al + taze uygula
    public async Task OpenWhatsAppAsync_ayni_kapsamda_tutar_degisirse_eski_dusum_geri_alinir_yenisi_uygulanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        var ilkKey = AppliedKey(handler.AppliedBalanceBodies[0]);

        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(
            ilkKey, "geri alınan, ilk düşümün ledger satırı (tx id = apply anahtarı)");
        handler.AppliedBalanceBodies.Should().HaveCount(2);
        AppliedKey(handler.AppliedBalanceBodies[1]).Should().NotBe(ilkKey,
            "revizyon YENİ anahtarla taze düşümdür");
        var job = _jobs.Snapshot.Single();
        job.ProductTotal.Should().Be(300m);
        job.Revision.Should().Be(1);
        job.State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // A7 — geri alma belirsizse revizyon İLERLEMEZ
    public async Task OpenWhatsAppAsync_geri_alma_belirsizse_BalanceUncertain_yeni_dusum_yapilmaz()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        var oncekiSentSayisi = _launcher.LaunchedUrls.Count;

        handler.ThrowTimeoutOnReverse = true;
        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "geri alma kesinleşmeden yeni düşüm çift tahsilat riskidir");
        _launcher.LaunchedUrls.Should().HaveCount(oncekiSentSayisi, "mesaj gitmedi");
        handler.ReverseCalls.Should().HaveCount(2, "bir deneme + bir anında tekrar (K3)");
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.ApplyUncertain);
    }

    [Fact] // A8 — already-reversed = idempotent başarı
    public async Task OpenWhatsAppAsync_geri_alma_already_reversed_donerse_basari_sayilir_revizyon_ilerler()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.ReverseProblemTitle = "already-reversed";
        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // Legacy (034 taşıması) — replay ile kesinleşir, sonucu satışa devrolur
    public async Task OpenWhatsAppAsync_acik_legacy_is_once_replay_ile_cozulur_sonuc_yeni_kapsama_devrolur()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        var legacyKey = Guid.NewGuid();
        _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customer.Id, "legacy", 250m, 0,
            legacyKey, null, PaymentJobState.ApplyUncertain, 1, 1, null));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "legacy replay'i aynı zamanda satışın düşümüdür — ikinci apply gerekmez");
        AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(legacyKey);
        _jobs.Snapshot.Single(j => j.ScopeKey == "legacy").ClosedAt.Should().NotBeNull();
        var hedef = _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1");
        hedef.ApplyKey.Should().Be(legacyKey);
        hedef.State.Should().Be(PaymentJobState.Applied);
    }
```

Ayrıca mevcut regresyon çıpası korunur:
`OpenWhatsAppAsync_LicenseUnresolvable_FallsBackToWaMeWithoutSending` —
lisans anahtarı YOKKEN (StubLicenseProvider null döner) bakiye yolu tümden
atlanır ve sonuç `Opened` kalır. Bu test yeni imzaya (`"cumulative"`) uyarlanır
ama iddiaları DEĞİŞMEZ.

**Koş, KIRMIZI bekle** (derlenmez — yeni imza/enum yok):
```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~PaymentRequestServiceTests"
```

**Commit** (kırmızı testlerle birlikte Task 9'da atılır — tek mantıksal adım).

### Task 9: PaymentRequestService yeniden yazımı (YEŞİL)

**Dosyalar:**
- `OrderDeck.App/Services/PaymentRequestService.cs`
- `OrderDeck.App/ViewModels/StreamReportViewModel.cs` (derleme için asgari)
- `OrderDeck.App/ViewModels/CustomerSearchViewModel.cs` (derleme için asgari)
- `OrderDeck.App/AppHost.cs`

**9a — enum:** `PendingApplyConflict` üyesi SİLİNİR, yerine:

```csharp
    /// <summary>Bakiye durumu doğrulanamadı (apply/geri alma belirsiz kaldı ya
    /// da lisans/önizleme erişilemedi). Mesaj GÖNDERİLMEDİ — operatör tekrar
    /// denemeli; deneme aynı anahtarla replay yapar, çift düşüm imkânsız.</summary>
    BalanceUncertain,
```

**9b — bağımlılık değişimi:** `IPendingBalanceApplyStore _pendingApplies` alanı
ve ctor parametresi `IPaymentJobStore _jobs` olur (using:
`OrderDeck.Core.Storage.Repositories` zaten var).

**9c — imza:** `overridePendingConflict` parametresi SİLİNİR, `scopeKey` gelir:

```csharp
    /// <param name="scopeKey">Satışın kalıcı kimlik kapsamı:
    /// "session:{id}" (yayın raporu) | "cumulative" (genel bakiye).
    /// Aynı kapsam + aynı müşteri = aynı satış; tutar değişirse revizyon.</param>
    public async Task<PaymentRequestResult> OpenWhatsAppAsync(
        Customer customer, decimal productTotal, DateTime streamDate,
        string scopeKey, CancellationToken ct = default)
```

**9d — bakiye bölgesi:** mevcut `// Bakiye uygulaması (best-effort).` bloğu
(satır ~176-258: `appliedBalance`/`totalBeforeBalance`/`pendingApplyKey`
tanımından catch bloğunun sonuna kadar) ŞU ile değişir:

```csharp
        // Bakiye uygulaması — PaymentJob durum makinesi (R2-01..04).
        // Eski fail-silent davranış BİLEREK terk edildi: sonuç belirsizse mesaj
        // gönderilmez (BalanceUncertain). "Bakiyesi düşmüş ama mesajı yanlış
        // tutarlı" ihtimali, "operatör bir kez daha tıklar" maliyetinden ağır.
        decimal appliedBalance = 0m;
        var totalBeforeBalance = totalAmount;
        PaymentJob? deliveryJob = null;   // mesaj müşteriye ulaşınca kapatılacak iş
        if (totalAmount > 0 && Guid.TryParseExact(customer.Id, "N", out var wpfCustomerId))
        {
            var outcome = await ResolveBalanceAsync(
                customer, wpfCustomerId, totalAmount, scopeKey, ct);
            if (outcome.Uncertain)
                return PaymentRequestResult.BalanceUncertain;
            deliveryJob = outcome.Job;
            appliedBalance = outcome.AppliedBalance;
            totalAmount -= appliedBalance;
        }
```

Teslimat noktalarında `ResolvePendingApply(pendingApplyKey)` çağrıları
`CloseJob(deliveryJob)` olur (üç yer: `CloudSendOutcome.Sent`, `Opened`,
ve `LaunchFailed`/`SendPending` dallarında ÇAĞRILMAZ — iş açık kalır, aynı).
`ResolvePendingApply` metodu silinir, yerine:

```csharp
    /// <summary>Mesaj müşteriye ulaştı — iş kapanır. Disk hatası akışı
    /// düşürmemeli: mesaj zaten gitti; en kötü iş açık kalır ve bir sonraki
    /// deneme sonucu diskten/replay'den yeniden bulur.</summary>
    private void CloseJob(PaymentJob? job)
    {
        if (job is null) return;
        try { _jobs.Close(job.Id); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Ödeme işi kapatılamadı (job={JobId})", job.Id);
        }
    }
```

**9e — durum makinesi çekirdeği** (sınıfa yeni private üyeler):

```csharp
    private sealed record BalanceOutcome(bool Uncertain, decimal AppliedBalance, PaymentJob? Job);

    /// <summary>Satışın bakiye sonucunu kesinleştirir. Dönüşte ya sonuç
    /// kesindir (Uncertain=false; AppliedBalance mesaja yazılabilir) ya da
    /// akış durmalıdır (Uncertain=true; çağıran BalanceUncertain döner).</summary>
    private async Task<BalanceOutcome> ResolveBalanceAsync(
        Customer customer, Guid wpfCustomerId, decimal totalAmount,
        string scopeKey, CancellationToken ct)
    {
        try
        {
            var licenseId = await ResolveLicenseIdAsync(ct);
            if (licenseId is null)
            {
                // Lisans anahtarı hiç yoksa bakiye özelliği kapalı — eski
                // davranış: düşümsüz devam. Anahtar var ama çözülemediyse
                // (ağ) belirsizlik: mesajı bloklamak gerekir.
                if (string.IsNullOrEmpty(_licenseProvider.CurrentLicenseKey))
                    return new(Uncertain: false, 0m, null);
                return new(Uncertain: true, 0m, null);
            }

            // 1) Miras (033→034) işi: önce kesinleştir, sonucu bu satışa devret.
            var legacy = _jobs.GetOpenLegacy(customer.Id);
            PaymentJob job;
            if (legacy is not null)
            {
                legacy = await ReplayAsync(licenseId.Value, wpfCustomerId, legacy, ct);
                if (legacy.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, legacy);

                job = _jobs.FindOrCreate(customer.Id, scopeKey, totalAmount);
                if (job.State == PaymentJobState.Created && job.ApplyKey is null)
                {
                    // Taze hedef: miras sonucu (anahtar+tutar+durum) devralır;
                    // tutar farklıysa aşağıdaki revizyon adımı düzeltir.
                    _jobs.AdoptLegacyResult(job.Id, legacy.Id);
                }
                else
                {
                    // Hedef iş kendi hayatını yaşıyor — miras düşümü artıksa
                    // geri al, işi kapat.
                    if (legacy.AppliedAmount is > 0m
                        && !await TryReverseAsync(licenseId.Value, legacy.ApplyKey!.Value, ct))
                    {
                        _jobs.MarkUncertain(legacy.Id);
                        return new(true, 0m, legacy);
                    }
                    _jobs.Close(legacy.Id);
                }
                job = _jobs.Get(job.Id)!;
            }
            else
            {
                job = _jobs.FindOrCreate(customer.Id, scopeKey, totalAmount);
            }

            // 2) Belirsiz iş: bir kez replay (K3) — hâlâ belirsizse blokla.
            if (job.State == PaymentJobState.ApplyUncertain)
            {
                job = await ReplayAsync(licenseId.Value, wpfCustomerId, job, ct);
                if (job.State == PaymentJobState.ApplyUncertain)
                    return new(true, 0m, job);
            }

            // 3) Revizyon (K2): kapsam aynı, tutar değişti — eski düşümü geri
            //    al, yeni toplam + YENİ anahtarla taze uygula. created işte de
            //    çalışır (geri alınacak şey yoktur, sadece tutar güncellenir).
            if (job.ProductTotal != totalAmount)
            {
                if (job.AppliedAmount is > 0m
                    && !await TryReverseAsync(licenseId.Value, job.ApplyKey!.Value, ct))
                {
                    _jobs.MarkUncertain(job.Id);
                    return new(true, 0m, job);
                }
                // false = eşzamanlı revizyon kazandı; onun anahtarıyla devam.
                _jobs.BeginRevision(job.Id, totalAmount, Guid.NewGuid(), job.Revision);
                job = _jobs.Get(job.Id)!;
                return await SettleAsync(licenseId.Value, wpfCustomerId, job, ct);
            }

            // 4) Tekrar paylaşım: sonuç kesin, tutar aynı — finansal çağrı YOK.
            if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
                return new(false, job.AppliedAmount ?? 0m, job);

            // 5) Taze iş: önizleme (bakiye yoksa anahtar hiç yazılmaz) →
            //    anahtar diske → apply.
            decimal previewBalance;
            try
            {
                var preview = await _api.GetBalancePreviewAsync(licenseId.Value, wpfCustomerId, ct);
                previewBalance = preview.Balance;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex,
                    "Bakiye önizlemesi alınamadı — mesaj engellendi (job={JobId})", job.Id);
                return new(true, 0m, job); // anahtar yazılmadı, iş created kaldı
            }
            if (previewBalance <= 0)
            {
                _jobs.MarkNoBalance(job.Id);
                return new(false, 0m, _jobs.Get(job.Id));
            }

            if (!_jobs.BeginApply(job.Id, Guid.NewGuid()))
            {
                // Yarışı kaybettik (R2-04/A9): kazananın anahtarı diskte.
                job = _jobs.Get(job.Id)!;
                if (job.State is PaymentJobState.Applied or PaymentJobState.NoBalance)
                    return new(false, job.AppliedAmount ?? 0m, job);
            }
            job = _jobs.Get(job.Id)!;
            return await SettleAsync(licenseId.Value, wpfCustomerId, job, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogError(ex,
                "Bakiye akışında beklenmeyen hata — mesaj engellendi (customer={CustomerId})",
                customer.Id);
            return new(true, 0m, null);
        }
    }

    /// <summary>Diskteki anahtarla apply — bir deneme + bir anında tekrar (K3).</summary>
    private async Task<BalanceOutcome> SettleAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        job = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        if (job.State == PaymentJobState.ApplyUncertain)
            job = await ReplayAsync(licenseId, wpfCustomerId, job, ct);
        return job.State == PaymentJobState.ApplyUncertain
            ? new(true, 0m, job)
            : new(false, job.AppliedAmount ?? 0m, job);
    }

    /// <summary>İşin diskteki anahtarıyla apply'ı (yeniden) dener, sonucu işe
    /// yazar. Amount=ProductTotal gönderilir — sunucu bakiyeye/toplama kırpar;
    /// replay'de birebir aynı gövde gittiği için A11 içerik kontrolünden geçer.</summary>
    private async Task<PaymentJob> ReplayAsync(
        Guid licenseId, Guid wpfCustomerId, PaymentJob job, CancellationToken ct)
    {
        if (job.ApplyKey is null)
            throw new InvalidOperationException($"Replay anahtarsız işte çağrıldı (job={job.Id})");
        try
        {
            var apply = await _api.ApplyBalanceAsync(
                licenseId,
                new CustomerBalanceApplyRequest(
                    wpfCustomerId, job.ProductTotal, job.ProductTotal, job.ApplyKey),
                ct);
            _jobs.MarkApplied(job.Id, apply.AppliedAmount);
        }
        catch (ValidationException ex) when (ex.Code is "no-balance" or "nothing-to-apply")
        {
            // Replay sunucuda balance kontrolünden ÖNCE çalışır: bu 409,
            // anahtarın hiç uygulanmadığının ve bakiye olmadığının kesin kanıtı.
            _jobs.MarkNoBalance(job.Id);
        }
        catch (ValidationException ex) when (ex.Code == "content-conflict")
        {
            // Olmamalı: anahtar sunucuda FARKLI içerikle kayıtlı (A11). Kesin
            // cevap sayamayız — belirsiz bırak, yüksek sesle logla.
            _log?.LogError(
                "Bakiye anahtarı sunucuda farklı içerikle kayıtlı (job={JobId}, key={Key})",
                job.Id, job.ApplyKey);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Bakiye apply belirsiz kaldı (job={JobId})", job.Id);
        }
        return _jobs.Get(job.Id)!;
    }

    /// <summary>Eski düşümü geri alır (K2). 409 already-reversed = idempotent
    /// başarı (A8). Geçici hatada bir kez daha dener (K3); yine olmazsa false.</summary>
    private async Task<bool> TryReverseAsync(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await _api.ReverseBalanceTransactionAsync(licenseId, transactionId, ct);
                return true;
            }
            catch (ValidationException ex) when (ex.Code == "already-reversed")
            {
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "Bakiye geri alma denemesi {Attempt} düştü (tx={TxId})", attempt, transactionId);
            }
        }
        return false;
    }
```

> Alan adları dosyadaki mevcutlara uyarlanır: `_api`, `_log`, `_jobs` (9b),
> lisans sağlayıcı alanı (`ResolveLicenseIdAsync`'in kullandığı
> `ICurrentLicenseProvider` alanı — dosyada nasıl adlandırıldıysa o).

**9f — VM asgari uyarlaması (derleme için):**

- `StreamReportViewModel.OpenWhatsAppAsync` (satır ~197-253): servis çağrısına
  `$"session:{_sessionId}"` kapsamı eklenir; `RequestPaymentAsync` içindeki
  `PendingApplyConflict` onay-diyaloğu + `overridePendingConflict: true`
  yeniden-deneme bloğu SİLİNİR; sonuç `switch`/`if` zincirine `BalanceUncertain`
  dalı eklenir — mevcut hata-diyaloğu yardımcısıyla:
  `"Bakiye doğrulanamadı — mesaj gönderilmedi. Tekrar deneyin."`
- `CustomerSearchViewModel.OpenWhatsAppAsync` (satır ~222-282): kapsam
  `streamSum > 0m && session is not null ? $"session:{session.Id}" : "cumulative"`;
  aynı silme + aynı `BalanceUncertain` dalı.

(İnce ayar/metin PR3'te; burada amaç derleme + doğru kapsam + uyarının kaybolmaması.)

**9g — AppHost + silme:**
- `AppHost.cs` satır ~86: `IPendingBalanceApplyStore` kaydı SİLİNİR
  (`IPaymentJobStore` kaydı Task 3'ten beri var).
- `OrderDeck.Core/Storage/Repositories/PendingBalanceApplyRepository.cs` SİLİNİR.
- `OrderDeck.Tests/Fakes/InMemoryPendingBalanceApplyStore.cs` SİLİNİR;
  testlerdeki `_pending` alanı ve `MakeCloudSut` içindeki kullanımı `_jobs` olur.

**Koş, YEŞİL bekle:**
```
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

**Commit:**
```
feat(payment): PaymentRequestService PaymentJob durum makinesine geçti — revizyon + belirsizlik bloğu (R2-01..03)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 10: A9 — eşzamanlılık yarışı GERÇEK depo ile

**Dosya:** `OrderDeck.Tests/Services/PaymentRequestServiceTests.cs` (bölge sonuna)

`MakeCloudSut`'a opsiyonel parametre ekle: `IPaymentJobStore? jobs = null` →
içeride `jobs ?? _jobs` kullanılır.

```csharp
    [Fact] // A9 — R2-04: iki eşzamanlı tıklama TEK anahtar üretir
    public async Task OpenWhatsAppAsync_es_zamanli_iki_cagri_tek_is_tek_anahtar()
    {
        // Fake yerine GERÇEK depo: yarışın hakemi SQLite'taki koşullu UPDATE.
        // Paylaşımlı bellek-DB eşzamanlı yazımda SQLITE_LOCKED verebildiği için
        // geçici DOSYA tabanlı veritabanı kullanılır (WAL — prod ile aynı).
        var dbPath = Path.Combine(Path.GetTempPath(), $"odjob-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(dbPath);
        try
        {
            new MigrationRunner(factory).Run();
            var repo = new PaymentJobRepository(factory);
            var (sut, handler) = MakeCloudSut(_store, _launcher, jobs: repo);
            handler.PreviewBalance = 100m;
            var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

            // Rendezvous: iki istek de preview'a VARANA kadar ikisi de bekler —
            // ikisinin de FindOrCreate'i geçip BeginApply'a yarışarak girmesi garanti.
            var arrived = 0;
            var bothArrived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnPreviewAsync = async () =>
            {
                if (Interlocked.Increment(ref arrived) == 2) bothArrived.TrySetResult();
                await bothArrived.Task;
            };

            var t1 = Task.Run(() => sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"));
            var t2 = Task.Run(() => sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"));
            var results = await Task.WhenAll(t1, t2);

            results.Should().AllBeEquivalentTo(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().NotBeEmpty();
            handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
                "kaybeden kazananın anahtarını yeniden kullanır — sunucuda tek düşüm");

            using var conn = factory.Open();
            // Dapper zaten test projesinde: satır sayısı iddiası
            Dapper.SqlMapper.ExecuteScalar<long>(conn,
                "SELECT COUNT(*) FROM PaymentJob").Should().Be(1, "UNIQUE kapsam tek iş");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
```

(Gerekli using'ler: `System.IO`, `System.Threading`, `OrderDeck.Core.Storage`.)

**Koş:** önce bu testi tek başına; SQLITE_BUSY/LOCKED görülürse teşhis et —
dosya-DB + WAL ile beklenmiyor (yazmalar milisaniyelik). Sonra tüm paket.

**Commit:**
```
test(payment): A9 — eşzamanlı iki "Ödeme iste" tek anahtar üretir (gerçek SQLite yarışı)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```

### Task 11: PR2'yi aç

PR başlığı: `feat(payment): ödeme işi yaşam döngüsü — servis durum makinesi (R2 paket 3/4)`
Gövde: spec linki; kapanan bulgular R2-01/02/03/04; davranış değişikliği özeti
(fail-silent → BalanceUncertain bloğu; PendingApplyConflict diyaloğu kalktı);
"Velopack sürümü PR3 merge'ünden sonra, PR4 sahada olduktan sonra" notu.

---

## PR3 — BalanceUncertain arayüzü + son doğrulama

Branch: master'dan `feat/payment-job-ui` (PR2 merge sonrası).

### Task 12: Diyalog metinleri + ayrım

**Dosyalar:** `StreamReportViewModel.cs`, `CustomerSearchViewModel.cs`

- `BalanceUncertain` dalı hata değil **uyarı** diyaloğu olur (iki VM'de aynı):
  başlık `"Bakiye doğrulanamadı"`, metin
  `"Sunucudan kesin cevap alınamadı; mesaj gönderilmedi. Bağlantıyı kontrol edip tekrar deneyin — çift düşüm olmaz."`
  (Mevcut diyalog yardımcısı hangi ayrımı sunuyorsa ona uy; ayrı uyarı türü
  yoksa mevcut bilgi diyaloğu yeterli.)
- İki VM'deki eski `PendingApplyConflict` izlerinin tamamen silindiğini doğrula:
  `grep -rn "PendingApplyConflict\|overridePendingConflict" OrderDeck.App/` → boş.

**Doğrulama:**
```
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```
El ile duman testi (yayın yokken): sahte lisansla "Ödeme iste" → ağ kapalıyken
BalanceUncertain diyaloğu; ağ açıkken normal akış.

**Commit + PR:**
```
feat(ui): BalanceUncertain uyarı diyaloğu — belirsiz bakiyede mesaj bloklandı bilgisi

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```
PR başlığı: `feat(ui): ödeme işi belirsizlik diyaloğu (R2 paket 4/4)`

---

## Sürüm / saha notları

1. PR4 prod'a çıkmadan PR2'li Velopack sürümü YAYINLANMAZ (reverse ucu yoksa
   revizyon akışı hep BalanceUncertain'e düşer).
2. PR1+PR2+PR3 tek Velopack sürümünde sahaya iner (aradaki commit'lerde eski
   depo çalışma zamanında kırık — sürüm checklist'ine not düş).
3. Migration 034 geri alınamaz (eski tablo düşer) — sürüm notuna yaz.
4. İlk sahada doğrulama: 034 sonrası açık legacy işi olan bir kurulumda
   "Ödeme iste" → tek düşüm, log'da replay satırı.

## Öz-denetim kaydı

- [x] Spec kapsaması: K1 (kalıcı kimlik+kapsam) T1/T2/A1-A5 · K2 (revizyon)
      T5/T6/A6-A8 · K3 (belirsizlik bloğu) A4/A7 · R2-04 (TOCTOU) T2/A9 ·
      A11 T4 · legacy taşıma T1/legacy testi — TAMAM
- [x] Placeholder taraması: "TODO/FIXME/..." plan gövdesinde yok — TAMAM
- [x] Tip tutarlılığı: `PaymentJob` alan sırası record ↔ fake ↔ SQL eşleşiyor;
      `ApplyKey` Guid? ↔ TEXT "N"; `ProductTotal/AppliedAmount` decimal ↔
      invariant TEXT; `BeginRevision` iki yerde de `ClosedAt=NULL` — TAMAM
- [x] Bilinen esneklikler (yürütücü dosyayı okuyarak uyarlar): stub route
      iskeleti, `MakeCloudSut` iç yapısı, VM diyalog yardımcı adları,
      lisans-sağlayıcı alan adı, `AdoptLegacyResult` transaction cast'i.




