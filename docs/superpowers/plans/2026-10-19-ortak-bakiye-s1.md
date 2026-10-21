# Ortak Bakiye (Kişi Havuzu) — S1 Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ortak bakiye spec'inin (`docs/superpowers/specs/2026-10-11-ortak-bakiye-design.md`) §12 **S1** satırını sunucuya getirmek: tek EF göçü, `PersonBalance` okuyucuları, grup geri alması, toplanmış listeler, `GroupId` kancası (silinmiş üyenin numara izlemesi dahil), `person-balance verify` ve günlük denetim. **Para davranışı değişmez**: hiçbir çekim çok bacaklı yazılmaz, kip bayrağı okunmaz.

**Architecture:** Defter satır başına kalır; S1 yalnız okumada grupları (`DrawGroupId ?? Id`) toplar ve çok bacaklı geri almayı bilir — S2'nin yazacağı satır biçimini önceden okuyabilen "genişlet" adımı (genişlet/daralt). Kişi `S(a)`, ödeme isteği kirasının kullandığı aynı sorgudan (`PersonMembership`) gelir. `LicenseDbContext.SaveChanges` kancası `GroupId` değişiminde bakiye jetonunu ilerletir ve boşalan numaradaki silinmiş üyeleri yeni numaraya taşır. Denetim (`PersonBalanceVerifier`) saf bir analizdir; CLI ve Hangfire işi onu çağırır.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test), Hangfire, xunit + FluentAssertions, Testcontainers (SQL Server 2022, Developer + Express).

---

## Spec ↔ kod farkları (master `e7f505fe`, 2026-10-19)

Spec `7f8d1efa` kesitine göre yazıldı; arada satış senkronu S3 (#520) ve öncül özeti (#523) aynı denetleyiciyi değiştirdi. Aşağıdaki her madde için önerilen çözüm planda uygulanmıştır; Burak farklı karar verirse ilgili görev değişir.

1. **Satır numaraları kaydı.** `LicensesCustomerBalanceApplyController`: preview `:102-122` (spec `:74-91`), scope `:160-186` + yüklem `LatestScopeDeductionAsync` `:194-206` (spec `:123-154`), transactions `:232-257` (spec `:180-205`), apply artık üç parça — `Apply` `:276-331`, `ApplyOncePerScopeAsync` `:359-404`, `WriteDeductionAsync` `:413-505` (spec `:224-362`), `LookupAsync` `:532-587` (spec `:385-440`; `sameCustomer` `:565-566`, tutar karşılaştırması `:569`), reverse `:602-674` (spec `:450-519`). `Program.cs`: `Migrate()` `:929` (spec `:868`), tekrarlayan işler bloğu `:946-1138` (spec `:889-1031`), CLI dağıtımı `:38-67`. `LicenseDbContext`: `CustomerBalance` yapılandırması `:1013-1032` (spec `:851-854`), `CustomerBalanceTransaction` `:1034-1063` (spec `:862-891`, N01 indeksi `:1053-1055`), projeksiyon `GroupId` `:870`, `IdentityKey` BIN2 emsali `:864-865`; `SaveChanges` `:198-213` ve `StampVersions` `:174-192` değişmemiş. Öbür dosyalar (`PanelCustomerBalanceController`, `CustomerIdentityMerger`, `CustomerGroupUnion`, `CustomerFieldMerge`, `CustomerIdentityMergeJob`) spec'teki satırlarda.
2. **Kapsam kuralı artık apply'da (S3, #520).** Spec §4.2 "satış senkronunun S3'üyle gelir" diyor; geldi: `ApplyOncePerScopeAsync` + `LatestScopeDeductionAsync`. **Çözüm:** S1 apply'ın yazım yoluna dokunmaz. `/scope` ve 409 `scope-has-deduction` aynı yüklemi paylaştığı için, yüklemin `AppliedAmount`'u `−Σ(grup)` olunca 409'un tutarı da kendiliğinden `−Σ(grup)` olur (§4.2 son madde; satır kipinde `= −Amount`, davranış aynı). Yükleme "yalnız çapa" süzgeci eklenir (bacaklar zaten kapsam taşımıyor; savunma).
3. **§15-6 sürüm kapısı.** Mod On'da `preview`/`scope`/`reverse`/`apply` için `client-update-required` kapısı (`:110-111`, `:177-178`, `:308-309`, `:608-609`) sahiplik denetiminin hemen ardından ve her okumadan önce durur. **Çözüm:** S1'in değiştirdiği `scope` ve `reverse`'te kapı yerinde kalır (yeni kod kapıdan SONRA başlar), `transactions/{id}` kapısız kalır. Var olan `CustomerBalanceScopeRuleTests` (InMemory) ve `CustomerBalanceScopeRuleRelationalTests` her ilgili görevde koşturulur.
4. **§15-5 üyelik kaynağı.** `payment-requests/begin` kişiyi `PaymentPersonResolver.ResolveAsync` + `ServerGroupAsync` (`PaymentPersonResolver.cs:85-99`) ile kurar ve "gruplu" kuralı `PaymentPersonKey.IsGrouped` = `!string.IsNullOrWhiteSpace(GroupId)`'dir. Spec §2 "`GroupId` boşsa `S = {a}`" diyor; yalnız boşluktan oluşan numarayı söylemiyor. **Çözüm:** sorgu yeni `Services/CustomerSync/PersonMembership.cs`'e taşınır; `ServerGroupAsync` ve `PersonBalance.MembersAsync` İKİSİ de onu çağırır, `PaymentPersonKey.IsGrouped` de `PersonMembership.IsGrouped`'a devreder. Böylece "aynı kaynak" bir test değil, tek fonksiyon (Görev 3'te ayrıca bir eşitlik testi).
5. **Kanca: "ayrılma" tanımı (§4.11).** Spec silinmiş üyenin numara izlemesini yalnız `GroupId` değişimiyle tarif ediyor; ama `--at-twins` ve kimlik birleşmesi işinde bir numara, son canlı üyesinin **kopyaya dönmesiyle** (GroupId değişmeden `MergedIntoId` dolarak) boşalabiliyor — §4.12 ve §8.2-13 bu işleri açıkça kapsamda sayıyor. **Çözüm:** ayrılma = canlı asıl üyenin (a) `GroupId`'sinin ordinal değişmesi (hedef = yeni numara) ya da (b) kopyaya dönmesi (hedef = asıl kaydın kayıttaki numarası). KVKK silmesi ayrılma değildir (satır numarasında silinmiş üye olarak kalır). Bakiye jetonu dokunuşu ise spec'teki gibi yalnız `GroupId` değişiminde (kopyaya dönüşte birleştirici iki bakiye satırını zaten yazar/damgalar).
6. **`UpdatedAt` (M9) S1'de de gerekli.** S1'in kancası ve birleştiricinin sıfır damgası bakiye satırının `UpdatedAt`'ini satır kipinde de ilerletir; panel ve Shopper "güncellendi" zamanı kilit dokunuşuyla oynardı. **Çözüm:** S1'de yeniden yazılan iki liste ucu (panel GET, Shopper GET) `UpdatedAt` = son para hareketinin `CreatedAt`'i (yoksa satırın `UpdatedAt`'i) döner. WPF preview satır kipinde bugünkü gibi kalır (spec §4.1 "bugünkü gibi"; S2 yeniden yazar).
7. **Taban çizgisinin "lisansın tabanı var mı" sorusu tanımsız.** §8.1 "lisansın tabanı henüz yoksa uyarı yok" diyor, ama yakalama anında hiç eksi/D1/kopya satırı olmayan bir lisans tabloya hiç satır yazmaz → sonsuza dek "taban yok" → hiç uyarmaz. Yakalamadan sonra açılan yeni lisans da öyle. **Çözüm (şema icadı yok, §3.1'in tablosu):** yakalama her lisans için `WpfCustomerId = Guid.Empty` bir **işaret satırı** yazar; bir lisansın tabanı var ⇔ işaret satırı var **ya da** lisans ilk yakalamadan sonra verilmiş (`IssuedAt` > en erken işaret `CapturedAt`, S1 öncesi verisi olamaz). Tablonun projeksiyona FK'sı yok (yalnız `Licenses`'a cascade). Alternatif: `Licenses.PersonBalanceBaselineAt` kolonu — Burak'a sorulacaklar listesinde.
8. **D3 uyarısı (havuz kipi) S2'de.** §8.1'in son uyarı kalemi "havuz kipindeki lisansta kişi havuzu < 0". S1 bayrağı hiç okumaz (§3.1). **Çözüm:** S1'in `verify`'ı 6. maddeyi (havuzu < 0 kişi, tabanla açıklananlar ayrı) yalnız sayı olarak basar; uyarıya çevirmesi S2'nin kip okumasıyla gelir.
9. **Aktif PC penceresi.** §7.3-9 sürüm dağılımı istiyor; `PersonBalance:ActivePcWindowDays` ayarı `enable` (S2) ile tanımlanıyor. **Çözüm:** S1 `SalesModeGate.RecentWindow` (14 gün, satış senkronu kapısıyla aynı ölçüt) sabitini kullanır; ayar S2'de gelir.
10. **Shopper `Reason` maskesi ve asıl kayda çözme S1'de.** §12 S2 satırı maskeyi S2'ye koyuyor, §4.10'un ilk maddesi (asıl kayda çözme) kipten bağımsız. S1 listeyi grup olarak topladığında açıklama **çapadan** gelir; çapa başka üyenin satırındaysa (S2 biçimli veri, ör. S2 → S1 elle dönüşte) onun serbest metni Shopper'a sızardı. **Çözüm:** maske ve asıl kayda çözme S1'de (birkaç satır, K4'e uygun); satır kipinde bugünkü verinin çıktısı değişmez.
11. **WPF reverse, satır yokken.** Bugün bakiye satırı yoksa 404 (`:626-627`); §4.5 "satır yoksa açılır (artı tutar her zaman güvenli)". **Çözüm:** spec kazanır. Ayrıca üç çakışmadan sonra bugünkü 500 yerine 503 `balance-busy` (WPF'in dayanıklılık katmanı aynı isteği yeniden dener; ikinci deneme `already-reversed` görüp başarı sayar).
12. **Panel reverse, eski (`DrawGroupId NULL`) hedef.** §4.9 "düşüm → grup geri alması" diyor; eski tek satırlık düşümün grup geri alması bugünkü satırla birebir aynı. **Çözüm:** `DrawGroupId NULL` hedef bugünkü kod yolunda kalır (satır kipi = bugünkü davranış, en küçük risk); yalnız `DrawGroupId` dolu hedefler yeni yoldan geçer.
13. **Göç: istatistik düşürme kapsamı.** §3.1 "otomatik istatistik (`_WA_Sys_*`) varsa göç düşürür". **Çözüm:** göç `GroupId`'ye bağlı, indekse ait OLMAYAN bütün istatistikleri (otomatik ve kullanıcı) düşürür — ikisi de `ALTER COLUMN`'u engeller, motor gerektiğinde yeniden oluşturur. Göç testi kullanıcı istatistiğini kesin, otomatik istatistiği motor isterse oluşturur.
14. **Ordinal süzgeç testi gerçek SQL'de.** §8.3 "`g1` / `g1 ` / `G1` ayrı kişi"yi InMemory'ye koyuyor; InMemory zaten ordinal karşılaştırır, iddiayı kanıtlamaz. **Çözüm:** aynı test Testcontainers'ta da (Görev 3) koşar; göç testi BIN2'nin harf farkını ayırıp sondaki boşluğu yuttuğunu ayrıca gösterir.

## Bilerek S1 dışında bırakılanlar

- Kip bayrağının okunması, `ResolveDrawSetAsync`, `PlanDraw`, `DrawAsync`, havuzlu preview/apply, K5 (panelde çok bacaklı eksi ayar / iade iptali), havuz kipinde eski `reversal` → `not-reversible`, `person-balance enable/disable`, `PersonBalance:MinClientVersion`, metrikler (§8.6), Testcontainers 1, 2, 6–10, 14, 15 → **S2**.
- W-havuz (WPF) ve Wh sürümü → ayrı PR.
- §8.1'in D3 uyarısı → S2 (fark 8).
- Spec §8.3'teki "Örnek 1 ve 4'ün yazımı" → S2 (yazan kod S2). S1 testleri Örnek 1'in **sonucunu** (S2 biçimli satırları) elle kurar; Örnek 2 (geri alma) ve 3 (`Members[]`) S1'de birebir sınanır.
- `verify`'ın CLI dışı bir yüzü (admin sayfası) yok; spec istemiyor.

## Ön koşullar ve sıra (spec §7.1)

- **Adım 0 (Burak):** prod'da salt okunur `SELECT COUNT(*)` — `WpfCustomerProjections` ve `CustomerBalanceTransactions`. Göç Express'te `ONLINE`'sız iki indeks kurar ve bir kolonun harmanını değiştirir; Görev 1'in göç testi süreyi günlüğe yazar. Sayılar sınıra yakınsa iki indeks ayrı bir göçe bölünür (bu plan tek göçü varsayar).
- Merge = otomatik prod deploy: **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil), Burak yapar.
- S1'den sonra: `person-balance verify` → `--capture-baseline --apply` (Burak), ≥ 1 gün gözlem, taban incelemesi (S2'den önce).

---

## Genel kurallar

- Worktree: `C:/Users/burak/source/repos/LiveDeck/.claude/worktrees/ortak-bakiye-s1`, dal `feat/ortak-bakiye-s1` (`origin/master`'dan; upstream yok, **push yok**). Bütün komutlar bu kökten. Ana checkout'a (docs dalı) dokunma.
- Repo **public**: testlerde literal kimlik bilgisi YOK. Parola/özet/anahtar üret: `$"g-{Guid.NewGuid():N}"`, değişkene **nötr ad** ver (`giris`, `ozet`; `password`/`secret` gibi adlar tarayıcıyı tetikler). Telefon `TestPhone.NewE164()`. Kullanıcı adları ve grup numaraları `Guid`'den üretilir; gerçek ad/telefon/IBAN yok.
- Türkçe yorum ve test adı. Commit Türkçe emir kipi, gövde sonu `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- Sunucu derlemesi `TreatWarningsAsErrors`: 0 uyarı. Kullanılmayan özel üye bırakma.
- Filtreli test (git-bash): `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~<Sınıf>"`. PowerShell'de: `$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test ...`.
- Testcontainers testleri Docker ister. Yerelde "Docker kapalı" gibi düşerlerse: `docker context ls`, sonra **PowerShell'den** `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (İKİ eğik çizgi; boru adı Docker Desktop sürümüne göre `docker_engine` da olabilir — `docker version` hangisinde yanıt veriyorsa). git-bash bu değeri bozar.
- Kaynak dosyaları **yalnız Edit/Write aracıyla** düzenle. PowerShell 5.1 `Get-Content`/`Set-Content` BOM'suz UTF-8'i bozar (Türkçe mojibake).
- Göç: `cd OrderDeck.LicenseServer && dotnet ef migrations add <Ad> --output-dir Data/Migrations` (dotnet-ef 10 global). Göç + Designer + Snapshot birlikte commit'lenir.
- InMemory'de eşzamanlılık jetonu **denetlenir** ama `SaveChanges` atomik **değildir** (çakışmada önceki girdiler yazılmış kalabilir); çakışma senaryoları yalnız Testcontainers'ta sınanır.

## Dosya haritası

| Görev | Dosya |
|---|---|
| 1 | Değiştir: `Domain/CustomerBalanceTransaction.cs` (`DrawGroupId`), `Domain/License.cs` (`PersonBalancePoolSince`), `Data/LicenseDbContext.cs` (DbSet + yapılandırma). Oluştur: `Domain/PersonBalanceBaseline.cs`, `Data/Migrations/*_PersonBalanceS1.cs` (+Designer, Snapshot). Test: `Data/PersonBalanceS1MigrationTests.cs` |
| 2 | Değiştir: `Data/LicenseDbContext.cs` (`StampCustomerBalanceVersions`), `Services/CustomerSync/CustomerIdentityMerger.cs`. Test: `Data/CustomerBalanceVersionStampTests.cs` (yeni), `Services/CustomerSync/CustomerIdentityMergerTests.cs` (güncelle) |
| 3 | Oluştur: `Services/CustomerSync/PersonMembership.cs`, `Services/CustomerSync/PersonBalance.cs`. Değiştir: `Services/Sync/PaymentRequests/PaymentPersonResolver.cs`, `PaymentPersonKey.cs`, `Program.cs` (DI). Test: `Controllers/Licenses/PersonBalanceTestKit.cs` (yeni), `Services/CustomerSync/PersonBalanceMembersTests.cs` (yeni, InMemory + Testcontainers) |
| 4 | Değiştir: `Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` (preview, scope, transactions, Lookup). Test: `Controllers/Licenses/PersonBalanceWpfReadersTests.cs` |
| 5 | Değiştir: `Services/CustomerSync/PersonBalance.cs` (`ReverseGroupAsync`), `LicensesCustomerBalanceApplyController.cs` (reverse). Test: `Controllers/Licenses/PersonBalanceGroupReversalTests.cs` |
| 6 | Değiştir: `Controllers/Panel/PanelCustomerBalanceController.cs` (reverse), `PersonBalance.cs` (`IsGroupReversible`). Test: `Controllers/Panel/PersonBalancePanelReverseTests.cs` |
| 7 | Değiştir: `PersonBalance.cs` (`AggregateHistory`, `HistoryAsync`, `LastMovementAtAsync`), `PanelCustomerBalanceController.cs` (GET), `Controllers/Shopper/ShopperBalanceController.cs`. Test: `Services/CustomerSync/PersonBalanceHistoryTests.cs`, `Controllers/Panel/PersonBalancePanelListTests.cs`, `Controllers/Shopper/PersonBalanceShopperListTests.cs` |
| 8 | Oluştur: `TestHelpers/RaceSaveHook.cs`. Test: `Controllers/Licenses/PersonBalanceReversalRaceTests.cs` (Testcontainers 3, 4, 5 + 503) |
| 9 | Oluştur: `Services/CustomerSync/PersonGroupSaveHook.cs`. Değiştir: `Data/LicenseDbContext.cs`. Test: `Services/CustomerSync/PersonGroupHookTests.cs`, `Services/CustomerSync/GroupIdWriteConventionTests.cs`, `Services/CustomerSync/PersonGroupHookRelationalTests.cs` (Testcontainers 12) |
| 10 | Değiştir: `Services/CustomerSync/PersonGroupSaveHook.cs`. Test: `Services/CustomerSync/PurgedMemberFollowTests.cs` (InMemory), `Services/CustomerSync/PurgedMemberFollowRelationalTests.cs` (Testcontainers 13) |
| 11 | Oluştur: `Services/CustomerSync/PersonBalanceVerifier.cs`. Değiştir: `Program.cs` (DI). Test: `Services/CustomerSync/PersonBalanceVerifierTests.cs` |
| 12 | Oluştur: `Tools/PersonBalanceCommand.cs`. Değiştir: `Program.cs` (CLI dağıtımı). Test: `Tools/PersonBalanceCommandTests.cs` |
| 13 | Oluştur: `Services/CustomerSync/PersonBalanceVerifyJob.cs`. Değiştir: `Program.cs` (DI + `SchedulePersonBalanceJobs`). Test: `Services/CustomerSync/PersonBalanceVerifyJobTests.cs` |
| 14 | Değiştir: `deploy/README.md` (UYARI). Tam paket. |

Bütün yollar `OrderDeck.LicenseServer/` ya da `OrderDeck.LicenseServer.Tests/` altında (tablodaki göreli yollar ona göre).

## Görev özeti

| # | Görev | Kabaca efor |
|---|---|---|
| 1 | Şema + tek EF göçü + göç testleri (betik, Developer, Express) | 2,5 sa |
| 2 | `CustomerBalance` jetonu `StampVersions`'a; birleştirici sıfırda da damgalar | 1 sa |
| 3 | `PersonMembership` (S'nin tek kaynağı) + `PersonBalance` iskeleti + test kiti | 2,5 sa |
| 4 | WPF okuyucuları: scope / transactions / Lookup `−Σ`, `PersonPool = false` | 2 sa |
| 5 | Grup geri alması (`ReverseGroupAsync`) + WPF reverse | 2,5 sa |
| 6 | Panel reverse: `DrawGroupId` dolu hedefler | 2 sa |
| 7 | Toplanmış listeler: panel GET (`Members[]`), Shopper GET | 3 sa |
| 8 | Testcontainers 3, 4, 5 + 503 (grup geri alması yarışları) | 2,5 sa |
| 9 | `GroupId` kancası I: jeton dokunuşu + convention testi + Testcontainers 12 | 2,5 sa |
| 10 | `GroupId` kancası II: silinmiş üyenin numara izlemesi + Testcontainers 13 | 3 sa |
| 11 | `PersonBalanceVerifier` (§7.3 1–10, §8.1 sapma kuralları) | 3 sa |
| 12 | `person-balance verify` CLI (taban, inceleme işareti, `--details`) | 2,5 sa |
| 13 | Günlük iş `person-balance-verify` + uyarı e-postası | 2 sa |
| 14 | `deploy/README` uyarısı + tam paket | 1 sa (+ ~15 dk koşu) |

Toplam ≈ 33 sa (spec tahmini 3,75 gün).

---
## Görev 1: Şema ve tek EF göçü (spec §3.1)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Domain/CustomerBalanceTransaction.cs`, `OrderDeck.LicenseServer/Domain/License.cs`, `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (`:85` DbSet bloğu, `:870` GroupId, `:1034-1063` hareket yapılandırması)
- Oluştur: `OrderDeck.LicenseServer/Domain/PersonBalanceBaseline.cs`, `OrderDeck.LicenseServer/Data/Migrations/<zaman>_PersonBalanceS1.cs` (+ `.Designer.cs`, `LicenseDbContextModelSnapshot.cs` güncellenir)
- Test: `OrderDeck.LicenseServer.Tests/Data/PersonBalanceS1MigrationTests.cs`

Harman değişimi var olan okuyucuların sonucunu değiştirmez: `GroupId`'ye SQL'de bakan üç yer (`CustomerGroupUnion.cs:51-52`, `CustomerGroupingJob.cs:158`, `PaymentPersonResolver.cs:89`) SQL'de yalnız aday daraltır ve kesin eşlemeyi bellekte ordinal yapar; BIN2 adayları daraltır ama ordinal eşleşeni dışarıda bırakmaz.

- [ ] **Adım 1: Göç testlerini yaz (kırmızı — göç yok)**

`OrderDeck.LicenseServer.Tests/Data/PersonBalanceS1MigrationTests.cs`:

```csharp
using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// PersonBalanceS1 göçü (ortak bakiye S1, spec 2026-10-11 §3.1, §8.2-11) gerçek SQL Server'da, DOLU tablolara karşı:
/// hedeften bir önceki göçe kadar kur → eski şemaya satır yaz (+ GroupId'de istatistik) → yalnız hedefi uygula.
/// Deploy'un otomatik geri alması göçü geri ALMAZ; S1 öncesi imajın yazdığı kolon listeleri yeni şemada çalışmalı.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceS1MigrationTests
{
    private readonly SqlServerContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public PersonBalanceS1MigrationTests(SqlServerContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Gocu_istatistik_varken_uygular_harman_indeks_ve_tablo_kurar_eski_imaj_yazmayi_surdurur()
        => await PersonBalanceS1MigrationScenario.RunAsync(await _sql.CreateDatabaseAsync(), _output);
}

/// <summary>Aynı senaryo prod'un sürümünde: SQL Server Express (ONLINE indeks işlemi yok, spec §3.1, §8.2-11).</summary>
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceS1MigrationExpressTests : IClassFixture<SqlServerExpressContainerFixture>
{
    private readonly SqlServerExpressContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public PersonBalanceS1MigrationExpressTests(SqlServerExpressContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Goc_Express_surumunde_kosar()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))";
            ((string)(await cmd.ExecuteScalarAsync())!).Should().StartWith("Express",
                "test prod'un sürümünde koşmalı, yoksa kanıt değildir");
        }

        await PersonBalanceS1MigrationScenario.RunAsync(connectionString, _output);
    }
}

/// <summary>Göç betiğinin biçimi — bağlantı açmaz, Docker'sız koşar.</summary>
public sealed class PersonBalanceS1MigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_ve_veri_yazimi_icermez_istatistigi_harmandan_once_duser()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = PersonBalanceS1MigrationScenario.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1)");
        script.Split('\n')
            .Where(line => Regex.IsMatch(line, @"^\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            .Should().BeEmpty("S1 göçü veri yazmaz, geri doldurma yok (spec §3.1)");

        var drop = script.IndexOf("DROP STATISTICS", StringComparison.Ordinal);
        var alter = Regex.Match(script,
            @"ALTER COLUMN \[GroupId\] nvarchar\(64\) COLLATE " + PersonBalanceS1MigrationScenario.Bin2);
        drop.Should().BeGreaterThan(-1, "GroupId'ye bağlı istatistik ALTER COLUMN'u engelleyebilir");
        alter.Success.Should().BeTrue("GroupId BIN2'ye alınır (M5)");
        alter.Index.Should().BeGreaterThan(drop, "istatistik harman değişiminden ÖNCE düşer");

        script.Should().Contain($"CREATE INDEX [{PersonBalanceS1MigrationScenario.GroupIndex}]");
        script.Should().Contain($"CREATE INDEX [{PersonBalanceS1MigrationScenario.DrawIndex}]");
        script.Should().NotContain($"DROP INDEX [{PersonBalanceS1MigrationScenario.GroupIndex}]",
            "henüz olmayan indeks harman değişiminde düşürülmeye çalışılmamalı");
        Regex.IsMatch(script, $@"CREATE\s+UNIQUE\s+INDEX\s+\[{PersonBalanceS1MigrationScenario.DrawIndex}\]")
            .Should().BeFalse("bacak indeksi tekil olamaz: birleştirme aynı grubun iki bacağını aynı satıra taşıyabilir");
        script.Should().Contain("CREATE TABLE [PersonBalanceBaselines]");
        script.Should().Contain("[PersonBalancePoolSince] datetimeoffset NULL");
    }
}

/// <summary>Senaryo testten ayrı: aynı adımlar Express'e karşı da koşar.</summary>
internal static class PersonBalanceS1MigrationScenario
{
    public const string Bin2 = "Latin1_General_100_BIN2";
    public const string GroupIndex = "IX_WpfCustomerProjections_LicenseId_GroupId";
    public const string DrawIndex = "IX_CustomerBalanceTransactions_DrawGroupId";

    /// <summary>S1 öncesi imajın <c>CustomerBalanceTransactions</c>'a yazdığı TAM kolon listesi. Senaryo bunu göç
    /// öncesi şemayla karşılaştırır: liste eksik kalırsa "eski imaj yazabiliyor" iddiası boşa düşerdi.</summary>
    internal static readonly string[] PreS1TransactionColumns =
    [
        "Id", "LicenseId", "WpfCustomerId", "Amount", "Kind", "OriginalAmount", "ShippingDeducted", "Reason",
        "ReversesTransactionId", "SaleScope", "CreatedByCustomerId", "CreatedAt",
    ];

    private const string StatsOnGroupIdSql = """
        SELECT COUNT(*) FROM sys.stats AS s
        WHERE s.object_id = OBJECT_ID(N'[dbo].[WpfCustomerProjections]')
          AND (s.auto_created = 1 OR s.user_created = 1)
          AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                      JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                      WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND c.name = N'GroupId')
        """;

    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_PersonBalanceS1", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    public static async Task RunAsync(string connectionString, ITestOutputHelper output)
    {
        await using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);
        await migrator.MigrateAsync(previous);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        (await ColumnsAsync(conn, "CustomerBalanceTransactions")).Should().BeEquivalentTo(PreS1TransactionColumns);
        var (customerId, licenseId) = await NewLicenseAsync(conn);

        // Projeksiyonlar bugünkü modelle yazılabilir: S1 bu tabloya kolon eklemez (yalnız harman ve indeks).
        var lower = Projection(licenseId, "g1");
        var upper = Projection(licenseId, "G1");
        var spaced = Projection(licenseId, "g1 ");
        db.WpfCustomerProjections.AddRange(lower, upper, spaced);
        await db.SaveChangesAsync();
        await InsertPreS1TransactionAsync(conn, licenseId, lower.Id, customerId, 50m);

        // GroupId'de istatistik. Kullanıcı istatistiği kesin oluşur; GROUP BY sorgusu motor isterse otomatik
        // istatistik de açar. İkisi de ALTER COLUMN'u engeller — göç ikisini de düşürmeli (spec §3.1).
        await ExecAsync(conn, "CREATE STATISTICS st_GroupId_test ON dbo.WpfCustomerProjections (GroupId);");
        await ExecAsync(conn,
            "SELECT GroupId, COUNT(*) FROM dbo.WpfCustomerProjections WHERE GroupId > N'' GROUP BY GroupId;");
        var statsBefore = await ScalarAsync<int>(conn, StatsOnGroupIdSql);
        statsBefore.Should().BeGreaterThan(0);
        output.WriteLine($"GroupId istatistikleri (göç öncesi): {statsBefore}");

        var watch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        output.WriteLine($"PersonBalanceS1 göçü: {watch.ElapsedMilliseconds} ms");

        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(target);

        // §3.1 satır 1–2: DrawGroupId + filtreli, tekil olmayan indeks.
        (await ColumnAsync(conn, "CustomerBalanceTransactions", "DrawGroupId"))
            .Should().Be(("uniqueidentifier", true, (string?)null));
        (await IndexAsync(conn, "CustomerBalanceTransactions", DrawIndex))
            .Should().Be((false, "([DrawGroupId] IS NOT NULL)"));

        // §3.1 satır 3–4: GroupId BIN2, tür/uzunluk/boşluk aynı; (LicenseId, GroupId) filtreli indeks.
        (await ColumnAsync(conn, "WpfCustomerProjections", "GroupId"))
            .Should().Be(("nvarchar", true, Bin2));
        (await ScalarAsync<short>(conn,
                "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID(N'WpfCustomerProjections') AND name = N'GroupId'"))
            .Should().Be(128, "nvarchar(64) = 128 bayt");
        (await IndexAsync(conn, "WpfCustomerProjections", GroupIndex))
            .Should().Be((false, "([MergedIntoId] IS NULL AND [GroupId] IS NOT NULL)"));
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM sys.stats WHERE name = N'st_GroupId_test'"))
            .Should().Be(0, "göç istatistiği düşürdü");

        // §3.1 satır 5–6: bayrak kolonu ve taban tablosu.
        (await ColumnAsync(conn, "Licenses", "PersonBalancePoolSince"))
            .Should().Be(("datetimeoffset", true, (string?)null));
        (await ColumnsAsync(conn, "PersonBalanceBaselines")).Should().BeEquivalentTo(
            ["LicenseId", "WpfCustomerId", "Balance", "LedgerSum", "CapturedAt", "ReviewedAt"]);
        (await ScalarAsync<int>(conn, """
            SELECT COUNT(*) FROM sys.key_constraints k
            JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
            WHERE k.parent_object_id = OBJECT_ID(N'PersonBalanceBaselines') AND k.type = 'PK'
            """)).Should().Be(2, "PK (LicenseId, WpfCustomerId)");

        // Veri yerinde; BIN2 harf farkını ayırır, sondaki boşluğu SQL '=' yine yutar (bellekteki süzgecin gerekçesi).
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM WpfCustomerProjections WHERE LicenseId = '{licenseId}' AND GroupId = N'g1'"))
            .Should().Be(2, "'g1' ve 'g1 ' eşit (ANSI dolgu), 'G1' BIN2'de ayrı");
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM WpfCustomerProjections WHERE LicenseId = '{licenseId}' AND GroupId = N'G1'"))
            .Should().Be(1);

        // Otomatik geri alma: S1 öncesi imaj yeni şemada yazmayı sürdürür (DrawGroupId NULL kalır).
        var oldRow = await InsertPreS1TransactionAsync(conn, licenseId, upper.Id, customerId, 20m);
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE Id = '{oldRow}' AND DrawGroupId IS NULL"))
            .Should().Be(1);
        await NewLicenseAsync(conn); // eski kolon listesiyle Licenses INSERT'i (PersonBalancePoolSince NULL'a düşer)
    }

    private static WpfCustomerProjection Projection(Guid licenseId, string groupId) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "instagram",
        Username = $"p{Guid.NewGuid():N}", GroupId = groupId, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Müşteri ve lisans, S1 öncesi imajın kolonlarıyla (CustomerProjectionUniqueIdentityMigrationTests yolu).</summary>
    private static async Task<(Guid CustomerId, Guid LicenseId)> NewLicenseAsync(SqlConnection conn)
    {
        var customerId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@customerId, @email, N'Ortak Bakiye Göç Testi', @ozet, 0, @now, 0);
            INSERT INTO Licenses (Id, LicenseKey, CustomerId, SkuCode, ActivationSlots, IssuedAt, ExpiresAt, ShopperAppEnabled)
            VALUES (@licenseId, @licenseKey, @customerId, 'STD', 1, @now, @expiresAt, 0);
            """;
        cmd.Parameters.AddWithValue("@customerId", customerId);
        cmd.Parameters.AddWithValue("@email", $"musteri-{Guid.NewGuid():N}@example.test");
        cmd.Parameters.AddWithValue("@ozet", $"h-{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@licenseId", licenseId);
        cmd.Parameters.AddWithValue("@licenseKey", "pb1-" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("@expiresAt", now.AddYears(1));
        await cmd.ExecuteNonQueryAsync();
        return (customerId, licenseId);
    }

    private static async Task<Guid> InsertPreS1TransactionAsync(
        SqlConnection conn, Guid licenseId, Guid wpfCustomerId, Guid customerId, decimal amount)
    {
        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO CustomerBalanceTransactions ({string.Join(", ", PreS1TransactionColumns)})
            VALUES (@id, @licenseId, @wpf, @amount, N'refund-full', @amount, NULL, NULL, NULL, NULL, @customerId, @now);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@licenseId", licenseId);
        cmd.Parameters.AddWithValue("@wpf", wpfCustomerId);
        cmd.Parameters.AddWithValue("@amount", amount);
        cmd.Parameters.AddWithValue("@customerId", customerId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<List<string>> ColumnsAsync(SqlConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t";
        cmd.Parameters.AddWithValue("@t", table);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<(string Type, bool Nullable, string? Collation)> ColumnAsync(
        SqlConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DATA_TYPE, IS_NULLABLE, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @t AND COLUMN_NAME = @c
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"{table}.{column} olmalı");
        return (reader.GetString(0), reader.GetString(1) == "YES", reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<(bool IsUnique, string Filter)?> IndexAsync(SqlConnection conn, string table, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_unique, ISNULL(filter_definition, N'') FROM sys.indexes
            WHERE object_id = OBJECT_ID(@t) AND name = @n
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@n", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetBoolean(0), reader.GetString(1));
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Adım 2: Betik testini koştur — derleme/çalışma hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceS1MigrationScriptTests"`
Expected: FAIL — `Sequence contains no matching element` (`_PersonBalanceS1` göçü yok).

- [ ] **Adım 3: Varlıklar**

`OrderDeck.LicenseServer/Domain/CustomerBalanceTransaction.cs` — `SaleScope`'tan sonra ekle:

```csharp
    /// <summary>
    /// Ortak bakiye (spec 2026-10-11 §2, §3.2): çok bacaklı bir çekimin bacaklarını çapaya bağlar. Değer çapanın
    /// <see cref="Id"/>'si; çapada kendi <see cref="Id"/>'si. <c>null</c> = eski, tek bacaklı satır — kendi başına
    /// bir grup. Çapa ⇔ <c>DrawGroupId == null || DrawGroupId == Id</c> (<c>PersonBalance.IsAnchor</c>).
    ///
    /// <para>Çekimleri (havuz kipindeki düşüm, elle eksi, çekim türü iptal) S2 yazar; S1 bu biçimi yalnız OKUR ve
    /// çok bacaklı bir çekimin grup geri almasını yazar (geri alma satırları geri alma çapasına bağlanır). Havuz kipi
    /// hiç açılmamış bir lisansta dolu satır yoktur. Bacaklarda <see cref="SaleScope"/>, <see cref="OriginalAmount"/>
    /// ve <see cref="Reason"/> boştur; açıklama çapada durur.</para>
    /// </summary>
    public Guid? DrawGroupId { get; set; }
```

`OrderDeck.LicenseServer/Domain/License.cs` — `SalesSyncModeChangedAt`'ten sonra ekle:

```csharp
    /// <summary>
    /// Ortak bakiye havuz kipi bayrağı (spec 2026-10-11 §3.1, §7.2): doluysa lisans havuz kipindedir. S1'de hiçbir
    /// kod okumaz ve yazmaz; S2 okur, yalnız <c>person-balance enable|disable</c> CLI'si yazar.
    /// </summary>
    public DateTimeOffset? PersonBalancePoolSince { get; set; }
```

`OrderDeck.LicenseServer/Domain/PersonBalanceBaseline.cs` (yeni):

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ortak bakiye denetiminin taban çizgisi (spec 2026-10-11 §3.1, §7.3, §8.1): S1 öncesinden kalan eksi satırlar,
/// D1 bozulmaları (<c>Balance ≠ Σ Amount</c>) ve kopya satırlar, yakalandıkları andaki <see cref="Balance"/> ve
/// <see cref="LedgerSum"/> ile. Günlük denetim yalnız tabana göre YENİ sapmada uyarır. Yalnız
/// <c>person-balance verify --capture-baseline --apply</c> ve <c>--mark-reviewed --apply</c> yazar.
///
/// <para><see cref="WpfCustomerId"/> = <see cref="Guid.Empty"/> satırı lisansın "taban alındı" işaretidir: yakalama
/// anında hiç sapması olmayan lisans da işaret alır, uyarılar onun için de açılır (plan "Spec ↔ kod farkları" 7).
/// Projeksiyona FK yok — işaret satırı ve silinen projeksiyonlar için.</para>
/// </summary>
public sealed class PersonBalanceBaseline
{
    public Guid LicenseId { get; set; }

    /// <summary>Satırın asıl ya da kopya kaydı; <see cref="Guid.Empty"/> = lisans işareti.</summary>
    public Guid WpfCustomerId { get; set; }

    /// <summary>Yakalama anındaki <c>CustomerBalance.Balance</c> (satır yoksa 0).</summary>
    public decimal Balance { get; set; }

    /// <summary>Yakalama anında satıra bağlı hareketlerin toplamı.</summary>
    public decimal LedgerSum { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Sahip satırı inceledi ("düzelt" ya da "olduğu gibi kalsın"); S2'nin <c>enable</c>'ı hepsini dolu ister.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }
}
```

- [ ] **Adım 4: `LicenseDbContext` yapılandırması**

`:85` DbSet bloğuna (`CustomerBalanceTransactions`'tan sonra):

```csharp
    public DbSet<PersonBalanceBaseline> PersonBalanceBaselines => Set<PersonBalanceBaseline>();
```

`WpfCustomerProjection` yapılandırmasında `:870`'deki satırı değiştir:

```csharp
            // BIN2 (ortak bakiye M5, spec 2026-10-11 §3.1): kişi eşitliği WPF'teki gibi ordinal. BIN2 harf farkını
            // SQL'de ayırır; sondaki boşluğu SQL '=' hiçbir harmanda ayırmaz — onu bellekteki süzgeç ayırır
            // (PersonMembership). Emsal IdentityKey.
            b.Property(c => c.GroupId).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
```

Aynı bloğa, `b.HasIndex(c => new { c.LicenseId, c.ChangeSeq });` satırından sonra:

```csharp
            // Kişi sorgusu (PersonMembership): lisansın asıl, gruplu kayıtları. Bugüne kadar GroupId'de indeks yoktu
            // ve kişi sorgusu lisansın bütün kayıtlarını tarıyordu (spec §3.1).
            b.HasIndex(c => new { c.LicenseId, c.GroupId })
             .HasFilter("[MergedIntoId] IS NULL AND [GroupId] IS NOT NULL");
```

`CustomerBalanceTransaction` yapılandırmasının sonuna (`SaleScope` indeksinden sonra):

```csharp
            // Ortak bakiye (spec §3.1): grup okuması ve geri alma. TEKİL DEĞİL — kimlik birleşmesi aynı çekimin iki
            // bacağını aynı satıra taşıyabilir (CustomerIdentityMerger), geri alma bacak başına yazılır.
            b.HasIndex(t => t.DrawGroupId)
             .HasFilter("[DrawGroupId] IS NOT NULL")
             .HasDatabaseName("IX_CustomerBalanceTransactions_DrawGroupId");
```

`CustomerBalanceTransaction` bloğundan sonra yeni varlık:

```csharp
        // Ortak bakiye denetiminin taban çizgisi (spec §3.1, §7.3). Projeksiyona FK yok: lisans işareti satırı
        // (WpfCustomerId = Guid.Empty) ve S1 öncesi kopyalar için. Lisans silinince cascade.
        mb.Entity<PersonBalanceBaseline>(b =>
        {
            b.HasKey(x => new { x.LicenseId, x.WpfCustomerId });
            b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Balance).HasPrecision(18, 2);
            b.Property(x => x.LedgerSum).HasPrecision(18, 2);
        });
```

- [ ] **Adım 5: Göçü üret ve istatistik düşürmeyi ekle**

Run: `cd OrderDeck.LicenseServer && dotnet ef migrations add PersonBalanceS1 --output-dir Data/Migrations && cd ..`
Expected: `Done.` Üç dosya: `<zaman>_PersonBalanceS1.cs`, `.Designer.cs`, `LicenseDbContextModelSnapshot.cs` değişikliği.

Üretilen `Up`'ı aşağıdakiyle karşılaştır; EF'in ürettiği işlemler aynı olmalı (sıra farklıysa düzelt — `Sql(DropGroupIdStatistics)` mutlaka `AlterColumn`'dan ÖNCE). Dosyanın tamamı şu biçimde olmalı (Designer'a dokunma):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderDeck.LicenseServer.Data.Migrations
{
    /// <summary>
    /// Ortak bakiye S1 (spec 2026-10-11 §3.1). Yalnız ekleyici: <c>CustomerBalanceTransactions.DrawGroupId</c> +
    /// filtreli tekil OLMAYAN indeks; <c>WpfCustomerProjections.GroupId</c> BIN2 harmanı + (LicenseId, GroupId)
    /// filtreli indeks; <c>Licenses.PersonBalancePoolSince</c>; <c>PersonBalanceBaselines</c> tablosu.
    ///
    /// <para><b>ONLINE yok, veri yazımı yok</b> (prod SQL Server Express; geri doldurma yok — eski satırların
    /// DrawGroupId'si NULL kalır). <b>İstatistik:</b> GroupId'ye bağlı, indekse ait olmayan istatistik (otomatik
    /// <c>_WA_Sys_*</c> ya da kullanıcı) harman değişimini engeller; önce düşürülür, motor gerekirse yeniden açar.
    /// PersonBalanceS1MigrationTests sabitler (Developer + Express).</para>
    /// </summary>
    public partial class PersonBalanceS1 : Migration
    {
        /// <summary>GroupId'ye bağlı, indekse ait olmayan bütün istatistikleri düşürür (yoksa hiçbir şey yapmaz).</summary>
        internal const string DropGroupIdStatistics = """
            DECLARE @drop nvarchar(max);
            SELECT @drop = STRING_AGG(CONVERT(nvarchar(max),
                       N'DROP STATISTICS [dbo].[WpfCustomerProjections].' + QUOTENAME(s.name)), N'; ')
            FROM sys.stats AS s
            WHERE s.object_id = OBJECT_ID(N'[dbo].[WpfCustomerProjections]')
              AND (s.auto_created = 1 OR s.user_created = 1)
              AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                          JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                          WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND c.name = N'GroupId');
            IF @drop IS NOT NULL EXEC sp_executesql @drop;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonBalancePoolSince",
                table: "Licenses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DrawGroupId",
                table: "CustomerBalanceTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(DropGroupIdStatistics);

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "PersonBalanceBaselines",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WpfCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LedgerSum = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonBalanceBaselines", x => new { x.LicenseId, x.WpfCustomerId });
                    table.ForeignKey(
                        name: "FK_PersonBalanceBaselines_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WpfCustomerProjections_LicenseId_GroupId",
                table: "WpfCustomerProjections",
                columns: new[] { "LicenseId", "GroupId" },
                filter: "[MergedIntoId] IS NULL AND [GroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBalanceTransactions_DrawGroupId",
                table: "CustomerBalanceTransactions",
                column: "DrawGroupId",
                filter: "[DrawGroupId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonBalanceBaselines");

            migrationBuilder.DropIndex(
                name: "IX_WpfCustomerProjections_LicenseId_GroupId",
                table: "WpfCustomerProjections");

            migrationBuilder.DropIndex(
                name: "IX_CustomerBalanceTransactions_DrawGroupId",
                table: "CustomerBalanceTransactions");

            migrationBuilder.DropColumn(
                name: "PersonBalancePoolSince",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "DrawGroupId",
                table: "CustomerBalanceTransactions");

            migrationBuilder.Sql(DropGroupIdStatistics);

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "WpfCustomerProjections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
```

Not: EF `AlterColumn`'u, hedef modeldeki `(LicenseId, GroupId)` indeksini "yeniden kurulacak" sayıp önce `DROP INDEX` üretirse (betik testi yakalar), indeksi bu göçten ayrı tutmak yerine `CreateIndex`'in `AlterColumn`'dan SONRA geldiğinden emin ol; EF 10 aynı göçte oluşturulan indeksi yeniden kurmaz — betik testi bunun kanıtıdır.

- [ ] **Adım 6: Testleri koştur**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceS1Migration"`
Expected: 3 test PASS (betik; Developer; Express). Çıktıda göç süresi (ms) — PR açıklamasına yazılacak.

Ardından şemaya dokunan var olan göç testleri ve bakiye testleri:

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~CustomerBalance|FullyQualifiedName~PanelCustomerBalance"`
Expected: hepsi PASS. (Eski göç testleri "hedeften bir öncekine kadar kur" desenini kullanır; yeni göç zincirin sonuna eklendiği için etkilenmez. `CustomerProjectionFullSyncMigrationTests` gibi sonraki göçleri de uygulayan bir test GroupId harmanı yüzünden düşerse, eski şemaya projeksiyon yazan kısmı inceleyip gerekçeyle güncelle.)

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/CustomerBalanceTransaction.cs OrderDeck.LicenseServer/Domain/License.cs \
  OrderDeck.LicenseServer/Domain/PersonBalanceBaseline.cs OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
  OrderDeck.LicenseServer/Data/Migrations/ OrderDeck.LicenseServer.Tests/Data/PersonBalanceS1MigrationTests.cs
git commit -F - <<'MSG'
feat(balance): ortak bakiye S1 göçü — DrawGroupId, GroupId BIN2, taban tablosu

Tek ekleyici göç (spec 2026-10-11 §3.1): CustomerBalanceTransactions.DrawGroupId
ve filtreli tekil olmayan indeksi, WpfCustomerProjections.GroupId BIN2 harmanı
ve (LicenseId, GroupId) indeksi, Licenses.PersonBalancePoolSince,
PersonBalanceBaselines. ONLINE ve veri yazımı yok; GroupId'deki istatistikler
harman değişiminden önce düşürülür. Göç testi Developer ve Express'te koşar.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 2: `CustomerBalance` jetonu `StampVersions`'a; birleştirici sıfırda da damgalar (spec §4.11, §4.12, M12)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs:154-213`, `OrderDeck.LicenseServer/Services/CustomerSync/CustomerIdentityMerger.cs:20-37`, `:92-116`
- Test: `OrderDeck.LicenseServer.Tests/Data/CustomerBalanceVersionStampTests.cs` (yeni), `OrderDeck.LicenseServer.Tests/Services/CustomerSync/CustomerIdentityMergerTests.cs:104-131` (güncelle)

- [ ] **Adım 1: Damga testini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Data/CustomerBalanceVersionStampTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// <see cref="CustomerBalance.UpdatedAt"/> kişi kilidinin jetonudur (spec 2026-10-11 §4.11, M6): yalnız "değişti"
/// işaretlenen satırın jetonu da <c>max(UtcNow, özgün + 1 tick)</c> ilerler; bir dokunuş kaybolmaz.
/// </summary>
public sealed class CustomerBalanceVersionStampTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerBalanceVersionStampTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Yalniz_degisti_isaretlenen_satirin_jetonu_ozgunun_bir_tik_otesine_ilerler()
    {
        var kit = await SalesCommandTestKit.CreateAsync(_factory);
        var wpfCustomerId = Guid.NewGuid();
        await kit.SeedProjectionAsync(wpfCustomerId);
        // Saati ileri bir yazar: özgün değer şimdiden büyük — UtcNow ataması jetonu GERİ alırdı.
        var future = DateTimeOffset.UtcNow.AddDays(1);
        await kit.WithDbAsync(db =>
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = wpfCustomerId,
                Balance = 10m, UpdatedAt = future,
            });
            return Task.CompletedTask;
        });

        await kit.WithDbAsync(async db =>
        {
            var row = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == wpfCustomerId);
            db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
        });

        var after = await kit.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .SingleAsync(b => b.WpfCustomerId == wpfCustomerId));
        after.UpdatedAt.Should().Be(future.AddTicks(1));
        after.Balance.Should().Be(10m);
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerBalanceVersionStampTests"`
Expected: FAIL — `UpdatedAt` değişmemiş (`future`), çünkü `CustomerBalance` damgalanmıyor.

- [ ] **Adım 3: `StampCustomerBalanceVersions`**

`LicenseDbContext.cs` — `StampPaymentMatchVersions`'ın altına:

```csharp
    /// <summary>
    /// <see cref="CustomerBalance.UpdatedAt"/> (F02 jetonu) ortak bakiyenin kişi kilidinin de jetonudur (spec
    /// 2026-10-11 §4.11, M6): grup değişimi kancası ve birleştiricinin sıfır bakiye damgası satırı yalnız "değişti"
    /// işaretler, değeri burada ilerler. <c>UtcNow</c> atamasına güvenen bugünkü yazarlar saat geri giderse aynı
    /// değeri yazabilirdi; <c>max(UtcNow, özgün + 1 tick)</c> ile bir dokunuş hiçbir zaman kaybolmaz.
    /// </summary>
    private void StampCustomerBalanceVersions() => StampVersions<CustomerBalance>(b => b.UpdatedAt);
```

İki `SaveChanges` override'ında `StampPaymentMatchVersions();` satırından sonra:

```csharp
        StampCustomerBalanceVersions();
```

- [ ] **Adım 4: Koştur — PASS**

Run: aynı filtre. Expected: PASS.

- [ ] **Adım 5: Birleştirici testini spec'e göre güncelle (kırmızı)**

`CustomerIdentityMergerTests.cs`'te `Kopyanin_bakiyesi_sifirsa_asil_kayda_hicbir_sey_eklenmez_sayilmaz` (`:104-131`) testini şu hâle getir (ad ve son iddia değişir; kalan iddialar aynı):

```csharp
    [Fact]
    public async Task Kopyanin_bakiyesi_sifirsa_asil_kayda_hicbir_sey_eklenmez_sayilmaz_satiri_yalniz_damgalanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var balanceId = Guid.NewGuid();
        var originalUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.CustomerBalances.Add(new CustomerBalance { Id = balanceId, LicenseId = license.Id, WpfCustomerId = copy.Id, Balance = 0m, UpdatedAt = originalUpdatedAt });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.Balances.Should().Be(0, "kopyanın bakiyesi zaten 0 — sayılmaz");
        var balances = await db.CustomerBalances.AsNoTracking().Where(b => b.LicenseId == license.Id).ToListAsync();
        balances.Should().ContainSingle("asıl kayıt için satır AÇILMAMALI — eklenecek tutar yok");
        var copyBal = balances.Single();
        copyBal.Id.Should().Be(balanceId);
        copyBal.WpfCustomerId.Should().Be(copy.Id);
        copyBal.Balance.Should().Be(0m);
        // Ortak bakiye M12 (spec 2026-10-11 §4.12): sıfır bakiyeli kopya satırı da damgalanır. Damgasız, bu satırı
        // okuyan eşzamanlı bir grup geri alması çakışmadan commit eder ve parayı kopyaya (görünmez) yazardı.
        copyBal.UpdatedAt.Should().BeAfter(originalUpdatedAt, "jeton ilerlemeli — eşzamanlı geri alma çakışsın");
    }
```

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerIdentityMergerTests"`
Expected: yeni test FAIL (`UpdatedAt` değişmemiş), öbürleri PASS.

- [ ] **Adım 6: Birleştiriciyi değiştir**

`CustomerIdentityMerger.cs` `:92-116` bloğunu şöyle yap (yalnız `else if` kolu eklenir):

```csharp
        var balances = 0;
        var fromBal = await _db.CustomerBalances
            .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == fromId, ct);
        if (fromBal is not null && fromBal.Balance != 0m)
        {
            // DbSet.Local Deleted durumdakileri içermez.
            var toBal = _db.CustomerBalances.Local
                            .SingleOrDefault(b => b.LicenseId == licenseId && b.WpfCustomerId == toId)
                        ?? await _db.CustomerBalances
                            .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == toId, ct);
            if (toBal is null)
            {
                toBal = new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = toId,
                    Balance = 0m, UpdatedAt = now,
                };
                _db.CustomerBalances.Add(toBal);
            }
            toBal.Balance += fromBal.Balance;
            toBal.UpdatedAt = now;
            fromBal.Balance = 0m;
            fromBal.UpdatedAt = now;
            balances = 1;
        }
        else if (fromBal is not null)
        {
            // Ortak bakiye M12 (spec 2026-10-11 §4.12): tutarı 0 olsa da kopyanın bakiye satırı damgalanır (jetonu
            // LicenseDbContext.StampVersions ilerletir). Çok bacaklı düşümden sonra bacak satırı tipik olarak 0'da
            // kalır; damgasız, o satırı okuyan eşzamanlı bir grup geri alması çakışmadan commit eder ve parayı kopya
            // satıra yazardı — bütün okumalar asıl kayda çözüldüğü için o para görünmez olurdu. Damgayla geri alma
            // çakışır ve bacağın güncel (asıl) satırıyla yeniden kurulur.
            fromBal.UpdatedAt = now;
            _db.Entry(fromBal).Property(b => b.UpdatedAt).IsModified = true;
        }
```

`RepointCounts` belge yorumunu (`:30-34`) güncelle:

```csharp
    /// <param name="Balances">
    /// 1 yalnız kopyanın SIFIR OLMAYAN bir bakiyesi asıl kayda eklendiğinde; kopyanın bakiye satırı zaten 0'sa
    /// sayılmaz — satır yalnız damgalanır (ortak bakiye M12: jetonu ilerler, tutarı değişmez). A7'nin günlüklediği
    /// sayaç budur.
    /// </param>
```

Sınıf yorumundaki (`:20-22`) "kopyanınki 0'lanır ama satır kalır" cümlesinin sonuna ekle: `Sıfır bakiyeli kopya satırı da damgalanır (ortak bakiye M12).`

- [ ] **Adım 7: Koştur — PASS + bakiye regresyonu**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~CustomerIdentityMerger|FullyQualifiedName~CustomerBalance|FullyQualifiedName~PanelCustomerBalance|FullyQualifiedName~ShopperBalance"`
Expected: hepsi PASS (InMemory; `CustomerBalanceConcurrencyTests` ve `CustomerBalanceScopeRuleRelationalTests` Docker ister — açıksa onlar da PASS).

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Data/LicenseDbContext.cs OrderDeck.LicenseServer/Services/CustomerSync/CustomerIdentityMerger.cs \
  OrderDeck.LicenseServer.Tests/Data/CustomerBalanceVersionStampTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/CustomerIdentityMergerTests.cs
git commit -F - <<'MSG'
feat(balance): bakiye jetonunu StampVersions'a al, sıfır bakiyeli kopyayı da damgala

CustomerBalance.UpdatedAt kişi kilidinin jetonu (ortak bakiye M6): "değişti"
işaretlenen satır max(UtcNow, özgün + 1 tick) ile ilerler. Birleştirici
kopyanın bakiye satırını tutarı 0 olsa da damgalar (M12) — eşzamanlı grup
geri alması çakışsın, para görünmez kopyada kalmasın.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 3: `PersonMembership` (S'nin tek kaynağı) + `PersonBalance` iskeleti + test kiti (spec §2, §4.0, §4.13, §15-5)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonMembership.cs`, `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs`
- Değiştir: `OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentPersonResolver.cs:79-99`, `OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentPersonKey.cs:32-33`, `OrderDeck.LicenseServer/Program.cs:155` (DI)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceTestKit.cs` (yeni), `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceMembersTests.cs` (yeni)

- [ ] **Adım 1: Test kitini yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceTestKit.cs` (sonraki görevlerin hepsi kullanır; istek yardımcıları da şimdi yazılır):

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye (spec 2026-10-11) testlerinin ortak kurulumu — InMemory ve gerçek SQL Server. Lisans ve kimliği
/// doğrulanmış istemci <see cref="SalesCommandTestKit"/>'ten. Üyeler, bakiyeler ve S2 biçimindeki çekim grupları
/// doğrudan veritabanına yazılır (D1 korunur: her bakiye bir defter satırından). Kişisel alan yok: kullanıcı adları
/// ve grup numaraları üretilir.
/// </summary>
public sealed class PersonBalanceTestKit
{
    public const string Session42 = "session:42";
    public const string KindDeduction = "purchase-deduction";
    public const string KindManual = "manual-adjustment";
    public const string KindReversal = "reversal";

    private readonly Dictionary<Guid, (string Platform, string Username)> _identities = new();

    private PersonBalanceTestKit(SalesCommandTestKit sales) => Sales = sales;

    public SalesCommandTestKit Sales { get; }
    public Guid LicenseId => Sales.LicenseId;
    public Guid CustomerId => Sales.CustomerId;
    public HttpClient Client => Sales.Client;
    private string WpfUrl => $"/api/v1/licenses/{LicenseId}/customer-balance";

    public static async Task<PersonBalanceTestKit> CreateAsync(ApiFactory factory, SalesSyncMode mode = SalesSyncMode.Off) =>
        new(await SalesCommandTestKit.CreateAsync(factory, mode));

    public static string NewGroup() => $"g{Guid.NewGuid():N}";

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Kişinin bir üyesi (asıl kayıt). <paramref name="balance"/> ≠ 0 ise bir defter satırı (artıda iade, eksi
    /// "eski" bir elle ayar) ve aynı tutarlı bakiye satırı; 0 ise <paramref name="balanceRow"/> true iken 0'lık satır.
    /// </summary>
    public async Task<Guid> MemberAsync(
        string? groupId, decimal balance = 0m, bool purged = false, bool balanceRow = true,
        string platform = "youtube", string? username = null)
    {
        var id = Guid.NewGuid();
        var name = username ?? $"p{Guid.NewGuid():N}";
        _identities[id] = (platform, name);
        var now = DateTimeOffset.UtcNow;
        await Sales.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = LicenseId, Platform = platform, Username = name, GroupId = groupId,
                UpdatedAt = now, PurgedAt = purged ? now.AddMinutes(-30) : null,
            });
            if (balance != 0m || balanceRow)
                db.CustomerBalances.Add(new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = id,
                    Balance = balance, UpdatedAt = now.AddMinutes(-10),
                });
            if (balance != 0m)
                db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = id, Amount = balance,
                    Kind = balance > 0 ? "refund-full" : KindManual,
                    OriginalAmount = balance > 0 ? balance : null,
                    CreatedByCustomerId = CustomerId, CreatedAt = now.AddMinutes(-10),
                });
            return Task.CompletedTask;
        });
        return id;
    }

    /// <summary>Var olan bir satıra iade (+): defter satırı ve bakiye (satır yoksa açılır). Döner: hareketin Id'si.</summary>
    public async Task<Guid> FundAsync(Guid row, decimal amount, string? reason = null, DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid();
        var createdAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-10);
        await Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = id, LicenseId = LicenseId, WpfCustomerId = row, Amount = amount, Kind = "refund-full",
                OriginalAmount = amount, Reason = reason, CreatedByCustomerId = CustomerId, CreatedAt = createdAt,
            });
            var balance = await db.CustomerBalances.SingleOrDefaultAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
            if (balance is null)
                db.CustomerBalances.Add(new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = LicenseId, WpfCustomerId = row, Balance = amount, UpdatedAt = createdAt,
                });
            else
                balance.Balance += amount;
        });
        return id;
    }

    /// <summary>
    /// S2 biçiminde (elle) bir çekim grubu — spec §3.2. İlk bacak çapadır (Id = DrawGroupId; OriginalAmount,
    /// SaleScope, Reason, ReversesTransactionId yalnız onda); hepsi aynı CreatedAt. Bakiye satırları bacak tutarı
    /// kadar düşer (D1). Döner: bacak Id'leri, çapa ilk.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SeedDrawAsync(
        string kind, IReadOnlyList<(Guid Row, decimal Amount)> legs, decimal? originalAmount = null,
        string? saleScope = null, string? reason = null, Guid? reverses = null, DateTimeOffset? at = null)
    {
        var ids = legs.Select(_ => Guid.NewGuid()).ToList();
        var anchor = ids[0];
        var createdAt = at ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        await Sales.WithDbAsync(async db =>
        {
            for (var i = 0; i < legs.Count; i++)
            {
                var (row, amount) = legs[i];
                db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
                {
                    Id = ids[i], LicenseId = LicenseId, WpfCustomerId = row, Amount = -amount, Kind = kind,
                    OriginalAmount = i == 0 ? originalAmount : null,
                    SaleScope = i == 0 ? saleScope : null,
                    Reason = i == 0 ? reason : null,
                    ReversesTransactionId = i == 0 ? reverses : null,
                    DrawGroupId = anchor, CreatedByCustomerId = CustomerId, CreatedAt = createdAt,
                });
                if (amount == 0m) continue;
                var balance = await db.CustomerBalances.SingleAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
                balance.Balance -= amount;
            }
        });
        return ids;
    }

    /// <summary>Eski, tek bacaklı düşüm (DrawGroupId NULL) — bugünkü apply'ın yazdığı biçim.</summary>
    public async Task<Guid> SeedLegacyDeductionAsync(Guid row, decimal amount, decimal productTotal, string? saleScope = null)
    {
        var id = Guid.NewGuid();
        await Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = id, LicenseId = LicenseId, WpfCustomerId = row, Amount = -amount, Kind = KindDeduction,
                OriginalAmount = productTotal, SaleScope = saleScope,
                CreatedByCustomerId = CustomerId, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            var balance = await db.CustomerBalances.SingleAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row);
            balance.Balance -= amount;
        });
        return id;
    }

    /// <summary>Spec Örnek 1'in sonu.</summary>
    public sealed record Example1(Guid T, Guid I, Guid Y, Guid K, Guid L1, Guid L2, string Group);

    /// <summary>
    /// Spec Örnek 1'in SONUCU (S2'nin yazacağı satırlar, elle): kişi {T (sahip, 0), I (Instagram, 100), Y (30)};
    /// çekim K (T, 0,00, OriginalAmount 120, session:42), L1 (I, −100), L2 (Y, −20) → T 0, I 0, Y 10.
    /// </summary>
    public async Task<Example1> Example1Async(string? group = null, string? iUsername = null)
    {
        var g = group ?? NewGroup();
        var t = await MemberAsync(g);
        var i = await MemberAsync(g, 100m, platform: "instagram", username: iUsername);
        var y = await MemberAsync(g, 30m);
        var ids = await SeedDrawAsync(KindDeduction, [(t, 0m), (i, 100m), (y, 20m)], originalAmount: 120m, saleScope: Session42);
        return new Example1(t, i, y, ids[0], ids[1], ids[2], g);
    }

    // ── Okuma ─────────────────────────────────────────────────────────────────

    public Task<decimal> BalanceAsync(Guid row) =>
        Sales.ReadDbAsync(async db => await db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == LicenseId && b.WpfCustomerId == row)
            .Select(b => (decimal?)b.Balance).SingleOrDefaultAsync() ?? 0m);

    public Task<CustomerBalance?> BalanceRowAsync(Guid row) =>
        Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .SingleOrDefaultAsync(b => b.LicenseId == LicenseId && b.WpfCustomerId == row));

    public Task<decimal> LedgerSumAsync(Guid row) =>
        Sales.ReadDbAsync(async db => (await db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId && t.WpfCustomerId == row)
            .Select(t => t.Amount).ToListAsync()).Sum());

    /// <summary>D1: satırın bakiyesi defter toplamına eşit mi.</summary>
    public async Task<bool> LedgerHoldsAsync(Guid row) => await BalanceAsync(row) == await LedgerSumAsync(row);

    /// <summary>Verilen hareketleri geri alan satırlar.</summary>
    public Task<List<CustomerBalanceTransaction>> ReversalsOfAsync(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        return Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.ReversesTransactionId != null && list.Contains(t.ReversesTransactionId.Value))
            .ToListAsync());
    }

    public Task<WpfCustomerProjection> ProjectionAsync(Guid id) =>
        Sales.ReadDbAsync(db => db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id));

    // ── İstek ─────────────────────────────────────────────────────────────────

    public Task<HttpResponseMessage> PreviewAsync(Guid row) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/preview?wpfCustomerId={row}");

    public Task<HttpResponseMessage> ScopeAsync(Guid row, string saleScope) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/scope?wpfCustomerId={row}&saleScope={Uri.EscapeDataString(saleScope)}");

    public Task<HttpResponseMessage> TransactionAsync(Guid id) =>
        SendAsync(HttpMethod.Get, $"{WpfUrl}/transactions/{id}");

    public Task<HttpResponseMessage> ReverseAsync(Guid id) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/transactions/{id}/reverse");

    public Task<HttpResponseMessage> ApplyAsync(Guid row, decimal amount, decimal productTotal, Guid? key, string? saleScope) =>
        SendAsync(HttpMethod.Post, $"{WpfUrl}/apply",
            new { wpfCustomerId = row, amount, productTotal, idempotencyKey = key, saleScope });

    public Task<HttpResponseMessage> PanelGetAsync(Guid row, int take = 50) =>
        SendAsync(HttpMethod.Get, $"/api/panel/customers/{row}/balance?take={take}", userAgent: null);

    public Task<HttpResponseMessage> PanelReverseAsync(Guid row, Guid transactionId) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/transactions/{transactionId}/reverse", userAgent: null);

    public Task<HttpResponseMessage> PanelRefundAsync(Guid row, decimal amount) =>
        SendAsync(HttpMethod.Post, $"/api/panel/customers/{row}/balance/refund-full",
            new { amount, reason = (string?)null }, userAgent: null);

    /// <summary>WPF senkronu (Format 2) ile üyenin grup numarasını damgalı yazar.</summary>
    public Task<HttpResponseMessage> SyncGroupAsync(Guid member, string? groupId, DateTimeOffset? stamp = null)
    {
        var (platform, username) = _identities[member];
        var at = stamp ?? DateTimeOffset.UtcNow;
        return SendAsync(HttpMethod.Post, $"/api/v1/licenses/{LicenseId}/wpf-customers/sync",
            new { customers = new[] { new { id = member, platform, username, updatedAt = at, format = 2, groupId, groupIdChangedAt = at } } });
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? userAgent = SalesCommandTestKit.WpfUserAgent)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return await Client.SendAsync(request);
    }

    public static async Task<string?> TitleAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }
}
```

- [ ] **Adım 2: Üyelik testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceMembersTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Sunucu kişisi S(a) — spec 2026-10-11 §2, §4.0, §15-5: istekteki Id'nin asıl kaydı gruplu değilse yalnız kendisi;
/// gruplu ise lisansta numarası ORDİNAL eşit bütün asıl kayıtlar (silinmişler dahil, kopyalar hariç). Ödeme isteği
/// kirasıyla (<c>payment-requests/begin</c>) aynı kaynak.
/// </summary>
public sealed class PersonBalanceMembersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceMembersTests(ApiFactory factory) => _factory = factory;

    private async Task<PersonBalance.Person> MembersAsync(PersonBalanceTestKit kit, Guid a)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>().MembersAsync(kit.LicenseId, a, default);
    }

    [Fact]
    public async Task Gruplu_satirin_kisisi_ayni_numarali_asil_kayitlar_silinmisler_dahil_kopyalar_haric()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var g = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, balanceRow: false);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.MemberAsync(PersonBalanceTestKit.NewGroup(), 99m);              // başka kişi
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);                 // kopya: kişide değil
        await kit.Sales.WithDbAsync(async db =>
            (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == copy)).GroupId = g);

        var person = await MembersAsync(kit, a);

        person.GroupId.Should().Be(g);
        person.Members.Should().BeEquivalentTo(new[]
        {
            new PersonBalance.Member(a, 10m, false),
            new PersonBalance.Member(b, 0m, false),
            new PersonBalance.Member(p, 40m, true),
        });
    }

    [Fact]
    public async Task Grupsuz_satir_tek_basina_kisidir()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 5m);
        await kit.MemberAsync(null, 7m);

        (await MembersAsync(kit, a)).Members.Should().Equal(new PersonBalance.Member(a, 5m, false));
    }

    [Fact]
    public async Task Yalniz_bosluktan_olusan_numara_grupsuz_sayilir_kira_ile_ayni_kural()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var a = await kit.MemberAsync("   ", 5m);
        await kit.MemberAsync("   ", 7m);

        (await MembersAsync(kit, a)).Members.Select(m => m.WpfCustomerId).Should().Equal(a);
        PaymentPersonKey.IsGrouped("   ").Should().Be(PersonMembership.IsGrouped("   "));
    }

    [Fact]
    public async Task Odeme_istegi_kirasinin_sunucu_grubu_ile_ayni_uye_kumesi()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var g = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g);
        await kit.MemberAsync(g, 3m, purged: true);

        using var scope = _factory.Services.CreateScope();
        var lease = await scope.ServiceProvider.GetRequiredService<PaymentPersonResolver>()
            .ServerGroupAsync(kit.LicenseId, g, default);
        var person = await MembersAsync(kit, a);

        person.Members.Select(m => m.WpfCustomerId.ToString("N")).Should().BeEquivalentTo(lease);
    }
}

/// <summary>
/// Ordinal süzgecin gerçek SQL Server kanıtı (spec §4.13, M5): BIN2 harf farkını ayırır ama SQL '=' sondaki boşluğu
/// yine yutar; kişi sorgusu sonucu bellekte ordinal süzmeli. InMemory zaten ordinal karşılaştırdığı için iddiayı
/// kanıtlamaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceMembersRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PersonBalanceMembersRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task g1_g1bosluk_ve_G1_ayri_kisilerdir()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var stem = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(stem, 10m);
        var d = await kit.MemberAsync(stem, 1m);
        var spaced = await kit.MemberAsync(stem + " ", 20m);
        var upper = await kit.MemberAsync(stem.ToUpperInvariant(), 30m);

        using var scope = _factory.Services.CreateScope();
        var person = scope.ServiceProvider.GetRequiredService<PersonBalance>();
        (await person.MembersAsync(kit.LicenseId, a, default)).Members.Select(m => m.WpfCustomerId)
            .Should().BeEquivalentTo(new[] { a, d });
        (await person.MembersAsync(kit.LicenseId, spaced, default)).Members.Select(m => m.WpfCustomerId)
            .Should().Equal(spaced);
        (await person.MembersAsync(kit.LicenseId, upper, default)).Members.Select(m => m.WpfCustomerId)
            .Should().Equal(upper);
        (await scope.ServiceProvider.GetRequiredService<PaymentPersonResolver>().ServerGroupAsync(kit.LicenseId, stem, default))
            .Should().BeEquivalentTo(a.ToString("N"), d.ToString("N"));
    }
}
```

- [ ] **Adım 3: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembers"`
Expected: FAIL — `PersonBalance`, `PersonMembership` yok.

- [ ] **Adım 4: `PersonMembership`**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonMembership.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Sunucu kişisinin üye kümesinin TEK kaynağı (ortak bakiye spec 2026-10-11 §2, §4.13; §15-5): ödeme isteği kirası
/// (<c>PaymentPersonResolver.ServerGroupAsync</c>) ve ortak bakiye (<see cref="PersonBalance.MembersAsync"/>) aynı
/// fonksiyonu çağırır — satış senkronunun PersonKey'i bu spec'in S(a) kişisidir.
///
/// <para><b>Gruplu kuralı</b> masaüstündeki <c>CustomerGroup.IsGrouped</c> ile aynı: boş ya da yalnız boşluktan
/// oluşan numara gruplanmamış demektir. <b>Eşitlik ordinal, ham değer üzerinde</b> (kırpma ve harf katlaması yok):
/// SQL aday daraltır (GroupId BIN2; '=' sondaki boşluğu yine yutar), kesin eşleme bellekte.</para>
/// </summary>
public static class PersonMembership
{
    /// <summary>Kişinin bir üyesi: asıl kayıt Id'si ve KVKK ile silinmiş mi.</summary>
    public sealed record MemberRow(Guid Id, bool Purged);

    public static bool IsGrouped(string? groupId) => !string.IsNullOrWhiteSpace(groupId);

    /// <summary>Lisansın, numarası <paramref name="groupId"/> ile ordinal eşit asıl kayıtları (silinmişler dahil,
    /// kopyalar hariç — varsayılan sorgu süzgeci).</summary>
    public static async Task<IReadOnlyList<MemberRow>> GroupMembersAsync(
        LicenseDbContext db, Guid licenseId, string groupId, CancellationToken ct)
    {
        var rows = await db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && p.GroupId == groupId)
            .Select(p => new { p.Id, p.GroupId, p.PurgedAt })
            .ToListAsync(ct);
        return rows
            .Where(r => string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .Select(r => new MemberRow(r.Id, r.PurgedAt is not null))
            .ToList();
    }
}
```

`PaymentPersonResolver.ServerGroupAsync` (`:85-99`) gövdesini değiştir (belge yorumunun son cümlesine "Sorgu <see cref="PersonMembership"/>'te; ortak bakiye aynı kaynağı kullanır (§15-5)." ekle):

```csharp
    public async Task<IReadOnlyList<string>> ServerGroupAsync(Guid licenseId, string? groupId, CancellationToken ct)
    {
        if (groupId is null) return [];
        var rows = await PersonMembership.GroupMembersAsync(_db, licenseId, groupId, ct);
        return rows
            .Select(r => r.Id.ToString("N"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
```

`PaymentPersonKey.cs:32-33`:

```csharp
    /// <summary>Satır bir gruba bağlı mı (<c>CustomerGroup.IsGrouped</c> ile aynı kural; tek tanım
    /// <see cref="CustomerSync.PersonMembership.IsGrouped"/>).</summary>
    public static bool IsGrouped(string? groupId) => CustomerSync.PersonMembership.IsGrouped(groupId);
```

(`PaymentPersonKey.cs` başına `using OrderDeck.LicenseServer.Services.CustomerSync;` ekleyip `PersonMembership.IsGrouped` da yazılabilir.)

- [ ] **Adım 5: `PersonBalance` iskeleti**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiye (kişi havuzu) — spec 2026-10-11 §4.0. Defter satır başına kalır; havuz okumada toplanır, çekim çok
/// bacaklı yazılır (S2). Bu dosyanın VARLIĞI deploy README'nin "S1 tabanı" kontrolüdür.
///
/// <para><b>S1 (bu sınıf):</b> kişi (<see cref="MembersAsync"/>), çekim grubu okuyucuları (<see cref="IsAnchor"/>,
/// <see cref="AnchorOfAsync"/>, <see cref="GroupSumAsync"/>), grup geri alması ve toplanmış liste. S1 kip bayrağını
/// OKUMAZ ve çok bacaklı çekim YAZMAZ — para davranışı değişmez; S2'nin biçimini yalnız okur ve geri alır
/// (genişlet/daralt). <b>S2:</b> <c>ResolveDrawSetAsync</c>, <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
/// </summary>
public sealed class PersonBalance
{
    public const string KindPurchaseDeduction = "purchase-deduction";
    public const string KindManualAdjustment = "manual-adjustment";
    public const string KindReversal = "reversal";

    /// <summary>Jeton çakışmasında baştan kurma denemesi (spec §4.5, §4.13); sonra 503 <c>balance-busy</c>.</summary>
    public const int MaxAttempts = 3;

    private const string BalanceRowIndex = "IX_CustomerBalances_LicenseId_WpfCustomerId";

    private readonly LicenseDbContext _db;
    private readonly ILogger<PersonBalance> _log;

    public PersonBalance(LicenseDbContext db, ILogger<PersonBalance> log)
    {
        _db = db;
        _log = log;
    }

    // ── Kişi (§2, §4.0) ──────────────────────────────────────────────────────

    /// <summary>Kişinin bir üyesi: satır bakiyesi (satır yoksa 0) ve silinmişlik.</summary>
    public sealed record Member(Guid WpfCustomerId, decimal Balance, bool Purged);

    /// <param name="CanonicalId">İstekteki Id'nin asıl kaydı (a).</param>
    /// <param name="GroupId">a'nın ham numarası; gruplu değilse de olduğu gibi.</param>
    /// <param name="Members">S(a), "N" metnine göre ordinal sıralı.</param>
    public sealed record Person(Guid CanonicalId, string? GroupId, IReadOnlyList<Member> Members);

    /// <summary>
    /// S(a) ve üye başına bakiye. <paramref name="canonicalId"/> asıl kayıt olmalı (çağıran
    /// <see cref="CustomerIdResolver"/>'dan geçirir). Projeksiyon yoksa kişi yalnız a'dır.
    /// </summary>
    public async Task<Person> MembersAsync(Guid licenseId, Guid canonicalId, CancellationToken ct)
    {
        var head = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.Id == canonicalId)
            .Select(p => new { p.GroupId, p.PurgedAt })
            .FirstOrDefaultAsync(ct);
        IReadOnlyList<PersonMembership.MemberRow> rows = head is not null && PersonMembership.IsGrouped(head.GroupId)
            ? await PersonMembership.GroupMembersAsync(_db, licenseId, head.GroupId!, ct)
            : [new PersonMembership.MemberRow(canonicalId, head?.PurgedAt is not null)];

        var ids = rows.Select(r => r.Id).ToList();
        var balances = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
            .ToDictionaryAsync(b => b.WpfCustomerId, b => b.Balance, ct);
        var members = rows
            .Select(r => new Member(r.Id, balances.GetValueOrDefault(r.Id), r.Purged))
            .OrderBy(m => m.WpfCustomerId.ToString("N"), StringComparer.Ordinal)
            .ToList();
        return new Person(canonicalId, head?.GroupId, members);
    }

    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────

    /// <summary>Çapa ⇔ <c>DrawGroupId IS NULL OR DrawGroupId = Id</c>. Eski tek bacaklı satır kendi çapasıdır.</summary>
    public static bool IsAnchor(CustomerBalanceTransaction t) => t.DrawGroupId is null || t.DrawGroupId == t.Id;

    /// <summary>Satırın grubu: çapanın Id'si (<c>DrawGroupId ?? Id</c>).</summary>
    public static Guid GroupKeyOf(CustomerBalanceTransaction t) => t.DrawGroupId ?? t.Id;

    /// <summary>Satırın çapası (çapaysa kendisi). Bacağın çapası yoksa (yetim bacak — <c>verify</c> sayar) null.</summary>
    public async Task<CustomerBalanceTransaction?> AnchorOfAsync(CustomerBalanceTransaction row, CancellationToken ct)
    {
        if (IsAnchor(row)) return row;
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == row.DrawGroupId && t.LicenseId == row.LicenseId, ct);
    }

    /// <summary>Grubun bütün satırlarının toplamı (çekimde ≤ 0). Eski tek bacaklı satırda kendi tutarı (sorgu yok).</summary>
    public async Task<decimal> GroupSumAsync(CustomerBalanceTransaction anchor, CancellationToken ct)
    {
        if (anchor.DrawGroupId is null) return anchor.Amount;
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == anchor.LicenseId && t.DrawGroupId == anchor.Id)
            .SumAsync(t => t.Amount, ct);
    }

    // ── Hata tanıma ──────────────────────────────────────────────────────────

    /// <summary>N01 hakemi: 2601/2627 + <c>ReversesTransactionId</c> indeksinin adı (öbür tekil yarışlar karışmasın).</summary>
    public static bool IsDuplicateReversal(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("ReversesTransactionId", StringComparison.Ordinal);

    /// <summary>Bakiye satırı açma yarışı: tekil <c>(LicenseId, WpfCustomerId)</c> indeksi.</summary>
    public static bool IsDuplicateBalanceRow(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(BalanceRowIndex, StringComparison.Ordinal);
}
```

`Program.cs:155`'in altına:

```csharp
        // Ortak bakiye (spec 2026-10-11): kişi, çekim grubu okuyucuları, grup geri alması, toplanmış liste.
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance>();
```

Not: `_log` bu görevde kullanılmıyor; derleyici kullanılmayan alan için uyarı vermez (atanıyor). Görev 5 kullanır.

- [ ] **Adım 6: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceMembers|FullyQualifiedName~PaymentRequest"`
Expected: PASS (InMemory 4 + Testcontainers 1; `PaymentRequest*` testleri `ServerGroupAsync`'in davranışını sabitler, değişmeden yeşil).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonMembership.cs OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentPersonResolver.cs OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentPersonKey.cs \
  OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceTestKit.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceMembersTests.cs
git commit -F - <<'MSG'
feat(balance): kişi S(a)'yı ödeme isteği kirasıyla tek kaynaktan kur

PersonMembership, begin'in ServerGroupAsync sorgusunun ve gruplu kuralının
tek tanımı; PersonBalance.MembersAsync aynı fonksiyonu çağırır (ortak bakiye
§15-5). Ordinal süzgeç gerçek SQL Server'da sınanır ("g1", "g1 ", "G1" ayrı
kişi). PersonBalance iskeleti: çapa/grup okuyucuları ve hata tanıma.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 4: WPF okuyucuları — scope / transactions / Lookup `−Σ(grup)`, `PersonPool = false` (spec §4.1, §4.2 A11, §4.3, §4.4)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` (kurucu `:77-91`, preview `:93-122`, scope `:126-206`, transactions `:210-257`, `LookupAsync` `:532-587`)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceWpfReadersTests.cs`

S1'de bu uçlara gelen her satır ya eski tek bacaklıdır (çıktı bugünküyle aynı) ya da S2 biçimidir (testler elle kurar). Kapı (§15-6) değişmez: yeni kod sahiplik + kapıdan SONRA başlar.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceWpfReadersTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S1, WPF yüzeyinin okuyucuları (spec 2026-10-11 §4.1–§4.4): çok bacaklı (S2 biçimli, elle kurulmuş)
/// bir düşüm yalnız çapada bulunur ve tutarı grubun toplamıdır; bacak Id'si çapaya çözülür ama idempotency anahtarı
/// olamaz; preview ve scope <c>PersonPool = false</c> döner. Satır kipi (mod Off).
/// </summary>
public sealed class PersonBalanceWpfReadersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceWpfReadersTests(ApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record TxDto(Guid TransactionId, decimal AppliedAmount, bool Reversed);
    private sealed record PreviewDto(Guid WpfCustomerId, decimal Balance, DateTimeOffset UpdatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    [Fact]
    public async Task Ornek_1_scope_yalniz_capada_bulunur_tutari_grup_toplamidir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onAnchor = await kit.ScopeAsync(ex.T, Kit.Session42);
        onAnchor.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onAnchor.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = false,
        });

        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "bacak kapsam taşımaz (§4.3, Örnek 1)");
    }

    [Fact]
    public async Task Transactions_bacak_Id_si_capaya_cozulur_tutar_grup_toplami()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        foreach (var id in new[] { ex.K, ex.L1, ex.L2 })
        {
            var resp = await kit.TransactionAsync(id);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync<TxDto>()).Should().Be(new TxDto(ex.K, 120m, false));
        }
    }

    [Fact]
    public async Task Transactions_capanin_geri_alma_satiri_varsa_Reversed_true()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.Sales.WithDbAsync(db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = ex.T, Amount = 0m,
                Kind = Kit.KindReversal, ReversesTransactionId = ex.K, CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });

        (await (await kit.TransactionAsync(ex.L1)).Content.ReadFromJsonAsync<TxDto>())!.Reversed.Should().BeTrue();
    }

    [Fact]
    public async Task Eski_tek_bacakli_dusumde_scope_ve_transactions_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 80m);
        var d = await kit.SeedLegacyDeductionAsync(a, 30m, 50m, Kit.Session42);

        (await (await kit.ScopeAsync(a, Kit.Session42)).Content.ReadFromJsonAsync<ScopeDto>())
            .Should().BeEquivalentTo(new { TransactionId = d, AppliedAmount = 30m, ProductTotal = 50m, PersonPool = false });
        (await (await kit.TransactionAsync(d)).Content.ReadFromJsonAsync<TxDto>()).Should().Be(new TxDto(d, 30m, false));
    }

    [Fact]
    public async Task Bacak_Id_si_idempotency_anahtari_olarak_gelirse_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.ApplyAsync(ex.I, 20m, 120m, key: ex.L1, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "bacak anahtar olamaz (§4.2 A11)");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }

    [Fact]
    public async Task Capa_anahtarinin_tekrari_grup_toplamini_oynatir_kucuk_tutar_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var replay = await kit.ApplyAsync(ex.T, 120m, 120m, key: ex.K, Kit.Session42);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await replay.Content.ReadFromJsonAsync<ApplyDto>();
        body!.TransactionId.Should().Be(ex.K);
        body.AppliedAmount.Should().Be(120m, "çapanın kendi payı 0 — oynatma grubun toplamını döner");

        var smaller = await kit.ApplyAsync(ex.T, 100m, 120m, key: ex.K, Kit.Session42);
        smaller.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(smaller)).Should().Be("content-conflict", "−Σ(grup) = 120 > istenen 100 (A11)");
    }

    [Fact]
    public async Task Preview_PersonPool_false_ve_bakiye_satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var preview = await (await kit.PreviewAsync(ex.Y)).Content.ReadFromJsonAsync<PreviewDto>();

        preview!.Balance.Should().Be(10m, "satır kipi: yalnız istenen satır");
        preview.PersonPool.Should().BeFalse();
        preview.WpfCustomerId.Should().Be(ex.Y);
    }
}

/// <summary>
/// Satış senkronu mod On (S3'ün kapsam kuralı, §4.2): 409 <c>scope-has-deduction</c>'ın tutarı çapanın değil grubun
/// toplamıdır — çapanın payı 0 olan planda WPF'in benimsediği düşüm 0 ₺ görünüp parayı ikinci kez isterdi.
/// </summary>
public sealed class PersonBalanceScopeRuleSumTests : IClassFixture<SalesSyncConfiguredApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;

    public PersonBalanceScopeRuleSumTests(SalesSyncConfiguredApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Mod_On_kapsamda_cok_bacakli_dusum_varken_409_tutari_grup_toplami()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var ex = await kit.Example1Async();

        var resp = await kit.ApplyAsync(ex.T, 50m, 120m, key: Guid.NewGuid(), Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await SalesCommandTestKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(ex.K);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(120m);
        problem.GetProperty("productTotal").GetDecimal().Should().Be(120m);
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceScopeRuleSumTests"`
Expected: FAIL — scope tutarı 0 (çapanın kendi tutarı), transactions/L1 TransactionId = L1 ve tutar 100, bacak anahtarı 409 content-conflict (404 değil), `PersonPool` alanı yok.

- [ ] **Adım 3: Kurucu ve preview**

Kurucuya `PersonBalance` ekle:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly SalesSyncOptions _salesSync;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        IOptions<SalesSyncOptions> salesSync,
        ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _salesSync = salesSync.Value;
        _log = log;
    }
```

`PreviewResponse` ve preview gövdesi:

```csharp
    /// <param name="PersonPool">Ortak bakiye (spec 2026-10-11 §4.1): havuz kipinde true. S1 kip bayrağını okumaz,
    /// hep false döner — satır kipi, bakiye istenen satırınki.</param>
    public sealed record PreviewResponse(
        Guid WpfCustomerId,
        decimal Balance,
        DateTimeOffset UpdatedAt,
        bool PersonPool = false);
```

Preview'daki iki `PreviewResponse` kurulumu üç argümanla kalabilir (varsayılan `false`); `Select` içindekini açık yaz: `.Select(b => new PreviewResponse(wpfCustomerId, b.Balance, b.UpdatedAt, false))`.

- [ ] **Adım 4: scope ve yüklem**

`ScopeResponse`:

```csharp
    /// <param name="AppliedAmount">Grubun toplamı (<c>−Σ</c>, spec §4.3): çok bacaklı düşümde çapanın kendi payı 0
    /// olabilir. Eski tek bacaklı satırda <c>−Amount</c>.</param>
    /// <param name="PersonPool">Havuz kipinde true; S1'de hep false (WPF birleştirme notunda kullanır, §6.1-d).</param>
    public sealed record ScopeResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        decimal ProductTotal,
        DateTimeOffset CreatedAt,
        bool PersonPool = false);
```

`LatestScopeDeductionAsync` (`:194-206`):

```csharp
    /// <summary>
    /// <c>/scope</c>'un yüklemi; apply'ın kapsam kuralı (§6.6) da aynı sorguyu koşar: aynı asıl kayıt, aynı kapsam,
    /// <c>purchase-deduction</c>, geri alınmamış; en yenisi (<c>CreatedAt</c>, sonra <c>Id</c>). Tek sorgu: uç
    /// "düşüm yok" dediği hâlde kuralın 409 vermesi (ya da tersi) olamaz.
    ///
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.2, §4.3):</b> yalnız çapalar aranır (bacaklar kapsam taşımaz; süzgeç
    /// savunma) ve tutar grubun toplamıdır. Çapa düzeyinde kalır: aynı kişinin başka satırındaki çapa bu yüklemi
    /// tetiklemez (M10). Çapanın geri alma satırı grubun tamamını temsil eder (D5, grup tek işlemde geri alınır).</para>
    /// </summary>
    private Task<ScopeResponse?> LatestScopeDeductionAsync(
        Guid licenseId, Guid canonicalCustomerId, string saleScope, CancellationToken ct) =>
        _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId
                && t.WpfCustomerId == canonicalCustomerId
                && t.SaleScope == saleScope
                && t.Kind == KindPurchaseDeduction
                && (t.DrawGroupId == null || t.DrawGroupId == t.Id)
                && !_db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new ScopeResponse(
                t.Id,
                -_db.CustomerBalanceTransactions
                    .Where(g => g.LicenseId == licenseId && (g.Id == t.Id || g.DrawGroupId == t.Id))
                    .Sum(g => g.Amount),
                t.OriginalAmount ?? 0m,
                t.CreatedAt,
                false))
            .FirstOrDefaultAsync(ct);
```

(`g.Id == t.Id || g.DrawGroupId == t.Id`: eski satırda grup yalnız kendisi; S2 çapasında `DrawGroupId = Id` olduğu için çapa tek kez sayılır.)

- [ ] **Adım 5: transactions/{id}**

`TransactionStatus` gövdesi (`:233-257`; belge yorumuna "Ortak bakiye §4.4: bacak Id'si çapaya çözülür, tutar grubun toplamı, `Reversed` çapanın geri alma satırı." paragrafını ekle):

```csharp
    [HttpGet("transactions/{transactionId:guid}")]
    public async Task<IActionResult> TransactionStatus(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var tx = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        // Ortak bakiye §4.4: bacak çapasına çözülür; çapası olmayan (yetim) bacak tanınmayan düşüm sayılır —
        // verify onu ayrıca sayar.
        var anchor = tx is null ? null : await _person.AnchorOfAsync(tx, ct);
        // R9-F03: başlıklı 404 — istemci "işlem gerçekten yok"u yalnız bu
        // başlıkla kabul eder. Gövdesiz 404 (eski sunucu, rota yok) anahtar
        // silmeye yol açmamalı: istemci onu uç-hatası sayar ve diskteki
        // sonuçla devam eder. Başlığı değiştirme — istemci sözleşmesi.
        if (anchor is null)
            return Problem(
                title: "transaction-not-found",
                detail: "Bu kimlikle kayıtlı bir düşüm işlemi yok.",
                statusCode: 404);

        // Grup tek işlemde geri alınır (D5): çapanın geri alma satırı grubun tamamını temsil eder.
        var reversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == anchor.Id, ct);

        return Ok(new TransactionStatusResponse(anchor.Id, -await _person.GroupSumAsync(anchor, ct), reversed));
    }
```

- [ ] **Adım 6: Lookup (A11)**

`LookupAsync`'te (`:532-587`) üç değişiklik; belge yorumuna şu paragrafı ekle:

```csharp
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.2 "İdempotency ve A11"):</b> anahtar her zaman bir ÇAPANIN Id'sidir;
    /// bulunan satır bacaksa (<c>DrawGroupId</c> dolu ve ≠ Id) foreign sayılır → 404 (savunma: istemcinin ürettiği
    /// Guid'in sunucunun bacak Id'sine denk gelmesi pratikte imkânsız). Tutar karşılaştırması ve oynatılan
    /// <c>AppliedAmount</c> grubun toplamıdır (<c>−Σ</c>); <c>sameCustomer</c> ve <c>OriginalAmount</c> çapada,
    /// değişmez (M10).</para>
```

Foreign kolu:

```csharp
        if (tx.LicenseId != licenseId || tx.Kind != KindPurchaseDeduction || !PersonBalance.IsAnchor(tx))
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı başka bir kayda ait (key={Key}, license={LicenseId}, kind={Kind})",
                key, licenseId, tx.Kind);
            return (null, true, false);
        }
```

İçerik karşılaştırmasından hemen önce grubun toplamı, karşılaştırmada ve dönüşte o:

```csharp
        var applied = -await _person.GroupSumAsync(tx, ct);
        var sameCustomer = tx.WpfCustomerId == canonicalCustomerId
            || await _customerIds.CanonicalOfAsync(licenseId, tx.WpfCustomerId, ct) == canonicalCustomerId;
        if (!sameCustomer
            || tx.OriginalAmount != req.ProductTotal
            || applied > req.Amount)
        {
            // (var olan günlük satırı aynen)
        }
```

ve sondaki dönüş:

```csharp
        return (new ApplyResponse(tx.Id, applied, remaining), false, false);
```

(`remaining` satır kipinde bugünkü gibi asıl kaydın bakiyesi kalır; havuz `Σ`'sı S2.)

- [ ] **Adım 7: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceWpfReadersTests|FullyQualifiedName~PersonBalanceScopeRuleSumTests|FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRule"`
Expected: hepsi PASS. `LicensesCustomerBalanceApplyControllerTests` (A11 `:316-406`, kopya Id `:930-1003`) ve `CustomerBalanceScopeRuleTests` (§15-6 kapısı: `Mod_On_eski_istemci_scope_ve_reverse_409_client_update_required` vb.) değişmeden yeşil; `CustomerBalanceScopeRuleRelationalTests` Docker ister.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceWpfReadersTests.cs
git commit -F - <<'MSG'
feat(balance): WPF okuyucuları çok bacaklı düşümü grup olarak okur

scope ve scope-has-deduction yalnız çapayı bulur, tutar grubun toplamı;
transactions/{id} bacak Id'sini çapaya çözer; idempotency oynatması −Σ(grup)
ile karşılaştırır ve bacak anahtarını 404'le reddeder (ortak bakiye §4.2–§4.4).
preview ve scope PersonPool=false taşır. Satış senkronu sürüm kapısı yerinde.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 5: Grup geri alması (`ReverseGroupAsync`) + WPF reverse (spec §4.5, M12, M13)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` (yeni bölüm), `OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs` (reverse `:589-682`)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceGroupReversalTests.cs`

Çakışma (jeton, N01 ve satır açma yarışı) gerçek SQL Server'da Görev 8'de sınanır; InMemory `SaveChanges` atomik değildir.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceGroupReversalTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri alması (spec 2026-10-11 §4.5): çekimin HER bacağı için bir <c>reversal</c> satırı, parası bacağın
/// GÜNCEL satırına döner; eski tek bacaklı düşüm bugünkü biçimde (DrawGroupId NULL) geri alınır (M13).
/// </summary>
public sealed class PersonBalanceGroupReversalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceGroupReversalTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ornek_2_her_bacak_kendi_satirina_doner_tek_grup_tek_CreatedAt()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var tBefore = (await kit.BalanceRowAsync(ex.T))!.UpdatedAt;

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2]);
        rows.Should().HaveCount(3);
        var r0 = rows.Single(r => r.ReversesTransactionId == ex.K);
        rows.Should().OnlyContain(r => r.DrawGroupId == r0.Id && r.Kind == Kit.KindReversal && r.CreatedAt == r0.CreatedAt);
        r0.Should().BeEquivalentTo(new { WpfCustomerId = ex.T, Amount = 0m, Reason = $"Reverse of {ex.K:N}" });
        rows.Single(r => r.ReversesTransactionId == ex.L1).Should().BeEquivalentTo(
            new { WpfCustomerId = ex.I, Amount = 100m, Reason = (string?)null });
        rows.Single(r => r.ReversesTransactionId == ex.L2).Should().BeEquivalentTo(
            new { WpfCustomerId = ex.Y, Amount = 20m, Reason = (string?)null });

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.BalanceRowAsync(ex.T))!.UpdatedAt.Should().Be(tBefore, "tutarı 0 olan bacakta bakiye satırına dokunulmaz");
    }

    [Fact]
    public async Task Bacak_Id_si_ile_gelen_reverse_capaya_cozulur_ikincisi_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        (await kit.ReverseAsync(ex.L2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await kit.ReverseAsync(ex.K);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "grup bir kez geri alınır");
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
    }

    [Fact]
    public async Task Eski_tek_bacakli_dusum_bugunku_bicimde_DrawGroupId_null_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 80m);
        var d = await kit.SeedLegacyDeductionAsync(a, 30m, 50m, Kit.Session42);

        (await kit.ReverseAsync(d)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reversal = (await kit.ReversalsOfAsync([d])).Should().ContainSingle().Subject;
        reversal.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = 30m, Kind = Kit.KindReversal, DrawGroupId = (Guid?)null,
            Reason = $"Reverse of {d:N}",
        });
        (await kit.BalanceAsync(a)).Should().Be(80m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
    }

    [Fact]
    public async Task Bacagin_guncel_satirinda_bakiye_satiri_yoksa_acilir()
    {
        // Kimlik birleşmesi bacağı (ve iadesini) asıl kayda taşımış, asıl kaydın bakiye satırı yok
        // (birleştirici kopyanın 0'ını eklemez): geri alma parayı GÜNCEL satıra yazar, satırı açar.
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var canonical = await kit.MemberAsync(ex.Group, balanceRow: false, platform: "instagram");
        await kit.Sales.WithDbAsync(async db =>
        {
            var moved = await db.CustomerBalanceTransactions.Where(t => t.WpfCustomerId == ex.I).ToListAsync();
            foreach (var t in moved) t.WpfCustomerId = canonical;
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == ex.I)).MergedIntoId = canonical;
        });

        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.ReversalsOfAsync([ex.L1])).Single().WpfCustomerId.Should().Be(canonical);
        (await kit.BalanceAsync(canonical)).Should().Be(100m);
        (await kit.LedgerHoldsAsync(canonical)).Should().BeTrue();
        (await kit.BalanceAsync(ex.I)).Should().Be(0m, "kopyaya para yazılmaz");
    }

    [Fact]
    public async Task Iade_ve_elle_ayar_satiri_WPF_reverse_ile_geri_alinamaz_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null);
        var refund = await kit.FundAsync(a, 40m);
        var manual = (await kit.SeedDrawAsync(Kit.KindManual, [(a, 10m)], reason: "elle"))[0];

        (await kit.ReverseAsync(refund)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.ReverseAsync(manual)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceGroupReversalTests"`
Expected: FAIL — bugünkü reverse yalnız çapayı geri alır (tek satır, 0 TL), bacak Id'si 200 ile tek bacağı geri alır, satır yoksa 404.

- [ ] **Adım 3: `ReverseGroupAsync`**

`PersonBalance.cs`'e (hata tanıma bölümünden önce) ekle:

```csharp
    // ── Grup geri alması (§4.5) ──────────────────────────────────────────────

    public enum ReverseOutcome { Reversed, AlreadyReversed, NotFound, Busy }

    /// <summary>
    /// Bir çekimin grup geri alması (spec §4.5, M12, M13). Grubun HER bacağı için bir <c>reversal</c> satırı:
    /// <c>Amount = −bacak.Amount</c>, <c>ReversesTransactionId = bacak.Id</c> (N01 bacak başına hakem), ortak
    /// <c>CreatedAt</c>; çok bacaklı grupta hepsi geri alma çapasına (<c>DrawGroupId</c> = çapanın geri alma
    /// satırının Id'si) bağlanır, eski tek bacaklı satırın geri alması bugünkü biçimde <c>DrawGroupId = NULL</c>.
    /// Açıklama yalnız çapanınkinde.
    ///
    /// <para>Para her bacağın GÜNCEL satırına eklenir (kimlik birleşmesi bacağı taşımış olabilir, §4.12); tutarı 0
    /// olan bacakta bakiye satırına dokunulmaz; satır yoksa açılır (artı tutar her zaman güvenli). Tek
    /// <c>SaveChanges</c>.</para>
    ///
    /// <para><b>Çakışma:</b> jeton (<c>DbUpdateConcurrencyException</c>) ya da satır açma yarışında
    /// <c>ChangeTracker.Clear()</c>; çapa, bacaklar, bacakların güncel satırları ve "geri alındı mı" taze okunur, grup
    /// baştan kurulur; en çok <see cref="MaxAttempts"/> deneme, sonra <see cref="ReverseOutcome.Busy"/>. Bugünkü
    /// "yeniden yükle, deltayı yeniden uygula" kalıbı çok satırlı kayıtta YANLIŞ: EF yalnız çakışan girdiyi döndürür,
    /// öbür satırlar bellekteki <c>eski + delta</c>'yı korur ve delta onlara iki kez eklenirdi. N01 ihlali
    /// (eşzamanlı geri alma) bütün işlemi geri alır → <see cref="ReverseOutcome.AlreadyReversed"/>; istemci başarı
    /// sayar. Para yalnız eklendiği için eksi denetimi ve kişi kilidi gerekmez.</para>
    ///
    /// <para>Çağıran DbContext'te izlenen başka değişiklik bırakmamalı: denemeler arasında izleyici temizlenir.</para>
    /// </summary>
    /// <param name="anchorId">Çapanın Id'si (çağıran bacağı <see cref="AnchorOfAsync"/> ile çözer).</param>
    public async Task<ReverseOutcome> ReverseGroupAsync(
        Guid licenseId, Guid anchorId, Guid createdByCustomerId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1) _db.ChangeTracker.Clear();

            var anchor = await _db.CustomerBalanceTransactions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == anchorId && t.LicenseId == licenseId, ct);
            if (anchor is null || !IsAnchor(anchor)) return ReverseOutcome.NotFound;
            // Hızlı yol; asıl hakem N01 indeksi (aşağıdaki catch).
            if (await _db.CustomerBalanceTransactions.AnyAsync(t => t.ReversesTransactionId == anchorId, ct))
                return ReverseOutcome.AlreadyReversed;

            // Bacaklar Id sırasıyla: eşzamanlı iki geri alma geri alma satırlarını (N01 tekil anahtarları) aynı
            // sırayla ekler — biri öbürünü bekler, kilitlenme (1205) olmaz.
            List<CustomerBalanceTransaction> legs = anchor.DrawGroupId is null
                ? [anchor]
                : await _db.CustomerBalanceTransactions.AsNoTracking()
                    .Where(t => t.LicenseId == licenseId && t.DrawGroupId == anchorId)
                    .OrderBy(t => t.Id)
                    .ToListAsync(ct);
            await StageGroupReversalAsync(licenseId, anchor, legs, createdByCustomerId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                if (legs.Count > 1)
                    _log.LogInformation(
                        "Ortak bakiye: çok bacaklı çekim grup olarak geri alındı ({Legs} bacak, license={LicenseId}, anchor={AnchorId})",
                        legs.Count, licenseId, anchorId);
                return ReverseOutcome.Reversed;
            }
            // Sıra önemli: DbUpdateConcurrencyException, DbUpdateException'dan türer.
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Grup baştan kurulur (döngü başı).
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(
                    "Ortak bakiye: grup geri alması {Attempts} denemede de çakıştı, balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, licenseId, anchorId);
                return ReverseOutcome.Busy;
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                _db.ChangeTracker.Clear();
                return ReverseOutcome.AlreadyReversed;
            }
            catch (DbUpdateException ex) when ((IsDuplicateBalanceRow(ex) || IsDeadlock(ex)) && attempt < MaxAttempts)
            {
                // Bacağın satırını eşzamanlı biri açtı ya da kilitlenme kurbanı olduk (işlem bütünüyle geri alındı):
                // taze okumayla baştan.
            }
            catch (DbUpdateException ex) when (IsDuplicateBalanceRow(ex) || IsDeadlock(ex))
            {
                _db.ChangeTracker.Clear();
                return ReverseOutcome.Busy;
            }
        }
    }

    private async Task StageGroupReversalAsync(
        Guid licenseId, CustomerBalanceTransaction anchor, IReadOnlyList<CustomerBalanceTransaction> legs,
        Guid createdByCustomerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var reversalAnchorId = Guid.NewGuid();
        Guid? drawGroupId = anchor.DrawGroupId is null ? null : reversalAnchorId;
        foreach (var leg in legs)
        {
            var isAnchor = leg.Id == anchor.Id;
            var amount = -leg.Amount;
            _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = isAnchor ? reversalAnchorId : Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = leg.WpfCustomerId,
                Amount = amount,
                Kind = KindReversal,
                Reason = isAnchor ? $"Reverse of {anchor.Id:N}" : null,
                ReversesTransactionId = leg.Id,
                DrawGroupId = drawGroupId,
                CreatedByCustomerId = createdByCustomerId,
                CreatedAt = now,
            });
            if (amount == 0m) continue;

            // Aynı çekimin iki bacağı birleşmeden sonra aynı satıra düşebilir: önce izlenenlerde (eklenmiş dahil).
            var balance = _db.CustomerBalances.Local
                              .FirstOrDefault(b => b.LicenseId == licenseId && b.WpfCustomerId == leg.WpfCustomerId)
                          ?? await _db.CustomerBalances
                              .FirstOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == leg.WpfCustomerId, ct);
            if (balance is null)
            {
                balance = new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = leg.WpfCustomerId,
                    Balance = 0m, UpdatedAt = now,
                };
                _db.CustomerBalances.Add(balance);
            }
            balance.Balance += amount;
            balance.UpdatedAt = now;
        }
    }
```

Hata tanıma bölümüne:

```csharp
    /// <summary>SQL Server kilitlenme kurbanı (1205): işlem bütünüyle geri alındı, yeniden kurmak güvenli.</summary>
    public static bool IsDeadlock(DbUpdateException ex) => ex.InnerException is SqlException { Number: 1205 };
```

- [ ] **Adım 4: WPF reverse ucunu bağla**

Sabitlere ekle: `private const string CodeBalanceBusy = "balance-busy";`

`Reverse` (`:602-674`) ve `IsDuplicateReversal` (`:676-682`) yerine (belge yorumunun ilk paragrafından sonra şu paragrafı ekle: "Ortak bakiye §4.5: Id çapaya çözülür ve çekim GRUP olarak geri alınır (<see cref="PersonBalance.ReverseGroupAsync"/>); eski tek bacaklı düşümün geri alması bugünkü satırla aynı. Üç çakışmadan sonra 503 balance-busy — dayanıklılık katmanı yeniden dener, ikinci deneme already-reversed'i başarı sayar."):

```csharp
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "reverse");

        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        var anchor = target is null ? null : await _person.AnchorOfAsync(target, ct);
        if (anchor is null) return NotFound();

        return await _person.ReverseGroupAsync(licenseId, anchor.Id, User.GetTenantCustomerId(), ct) switch
        {
            PersonBalance.ReverseOutcome.Reversed => Ok(),
            PersonBalance.ReverseOutcome.AlreadyReversed => Problem(title: "already-reversed", statusCode: 409),
            PersonBalance.ReverseOutcome.Busy => BalanceBusy(licenseId, "reverse"),
            _ => NotFound(),
        };
    }

    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        _log.LogWarning(
            "customer-balance/{Endpoint}: bakiye satırları üst üste çakıştı, 503 balance-busy (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeBalanceBusy, statusCode: 503,
            detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
    }
```

`IsDuplicateReversal` bu sınıftan silinir (artık `PersonBalance.IsDuplicateReversal`; kullanılmayan özel üye derlemeyi bozmasın).

- [ ] **Adım 5: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceGroupReversalTests|FullyQualifiedName~LicensesCustomerBalanceApplyControllerTests|FullyQualifiedName~CustomerBalanceScopeRule|FullyQualifiedName~PersonBalanceWpfReadersTests"`
Expected: hepsi PASS. Docker açıksa `CustomerBalanceConcurrencyTests.WPF_ucunda_ayni_hareket_esazamanli_yalnizca_bir_kez_reverse_edilebilir` de koşmalı (1 OK + 7 Conflict, değişmeden): `--filter "FullyQualifiedName~CustomerBalanceConcurrencyTests"`.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer/Controllers/Licenses/LicensesCustomerBalanceApplyController.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceGroupReversalTests.cs
git commit -F - <<'MSG'
feat(balance): WPF reverse çekimi grup olarak geri alır

Her bacak için bir reversal satırı, para bacağın güncel satırına döner;
tutarı 0 olan bacakta bakiye satırına dokunulmaz, satır yoksa açılır.
Eski tek bacaklı düşümün geri alması bugünkü biçimde (DrawGroupId NULL).
Çakışmada izleyici temizlenip grup baştan kurulur; üç denemeden sonra 503
balance-busy (ortak bakiye §4.5, M12, M13).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 6: Panel reverse — `DrawGroupId` dolu hedefler (spec §4.9 "S1 tabanı", M8)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` (kurucu `:32-39`, reverse `:195-246`, `IsDuplicateReversal` `:341-350`), `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` (`IsGroupReversible`)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelReverseTests.cs`

Kural (iki kipte de, S1 bayrağı okumaz → satır kipi üyelik koşulu): hedef bir çekim grubuna aitse (`DrawGroupId` dolu) grubun HERHANGİ bir satırı rota satırında olmalı; hedef çapaya çözülür. Düşüm ve elle eksi grubu → grup geri alması; `reversal` türündeki grup (çekim türü iptalin çapası/bacağı, grup geri alma satırı) → 409 `not-reversible`. `DrawGroupId NULL` hedef bugünkü yoldan, birebir (fark 12).

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelReverseTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel reverse'in S1 tabanı (spec 2026-10-11 §4.9, §7.4, §8.3): S2 biçimindeki satırlar elle kurulur, kip kapalı.
/// Çekim grubuna ait hedef grup olarak geri alınır ya da 409 <c>not-reversible</c>; S2 elle geri alınıp S1'e
/// dönülse bile bir K5 iptal grubu tek satır yoluyla yarım geri alınamaz. <c>disable</c> sonrası panel listesi
/// grubu çapa Id'siyle gösterir: çapa başka satırdayken rota satırındaki bacaktan açılan iptal bulunmalı.
/// </summary>
public sealed class PersonBalancePanelReverseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePanelReverseTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Capasi_baska_satirda_olan_dusum_grubu_bacagin_satirindan_grup_olarak_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.PanelReverseAsync(ex.I, ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
    }

    [Fact]
    public async Task Bacak_Id_si_ile_gelen_panel_iptali_de_capaya_cozulur_ikincisi_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        (await kit.PanelReverseAsync(ex.Y, ex.L2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await kit.PanelReverseAsync(ex.T, ex.K);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
    }

    [Fact]
    public async Task Rota_satirinda_grubun_satiri_yoksa_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var stranger = await kit.MemberAsync(ex.Group, 5m);

        (await kit.PanelReverseAsync(stranger, ex.K)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.ReversalsOfAsync([ex.K])).Should().BeEmpty();
    }

    [Fact]
    public async Task Elle_eksi_grubu_grup_olarak_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var ids = await kit.SeedDrawAsync(Kit.KindManual, [(a, 10m), (b, 20m)], reason: "elle düzeltme");

        (await kit.PanelReverseAsync(b, ids[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task K5_iptal_grubu_capadan_da_bacaktan_da_not_reversible()
    {
        // Örnek 4'ün sonucu: A'daki iade R (+80) iptal edilmiş — çapa V (A, −30, reversal, Reverses R), bacak (B, −50).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        var b = await kit.MemberAsync(g, 60m);
        var refund = await kit.FundAsync(a, 0m);
        var ids = await kit.SeedDrawAsync(Kit.KindReversal, [(a, 30m), (b, 50m)], reverses: refund);

        var onAnchor = await kit.PanelReverseAsync(a, ids[0]);
        var onLeg = await kit.PanelReverseAsync(b, ids[1]);

        onAnchor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onAnchor)).Should().Be("not-reversible");
        onLeg.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onLeg)).Should().Be("not-reversible");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
    }

    [Fact]
    public async Task Grup_geri_alma_satiri_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);
        var r1 = (await kit.ReversalsOfAsync([ex.L1])).Single();

        var resp = await kit.PanelReverseAsync(ex.I, r1.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("not-reversible", "çok bacaklı 'iptalin iptali' kapalı (M8)");
    }

    [Fact]
    public async Task Eski_reversal_satiri_satir_kipinde_bugunku_gibi_geri_alinabilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var refund = await kit.FundAsync(a, 30m);
        (await kit.PanelReverseAsync(a, refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([refund])).Single();
        reversal.DrawGroupId.Should().BeNull();

        (await kit.PanelReverseAsync(a, reversal.Id)).StatusCode.Should().Be(HttpStatusCode.OK,
            "DrawGroupId NULL reversal satır kipinde bugünkü gibi (§4.9)");
        (await kit.BalanceAsync(a)).Should().Be(80m);
    }
}
```

Not: `FundAsync(a, 0m)` K5 testinde yalnız "iptal edilen iade"nin Id'sini üretir (tutarı testte önemsiz; A'nın 30'u `MemberAsync`'ten).

- [ ] **Adım 2: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePanelReverseTests"`
Expected: FAIL — çapa başka satırdayken 404, bacak tek satır olarak geri alınır, K5 satırları tek satır yoluna düşer.

- [ ] **Adım 3: `IsGroupReversible`**

`PersonBalance.cs`, çekim grupları bölümüne:

```csharp
    /// <summary>
    /// Grup olarak geri alınabilen çekim (spec §4.9, M8): düşüm ya da elle eksi grubu (çapa ≤ 0). <c>reversal</c>
    /// türündeki gruplar — çekim türü iptalin çapası/bacağı ve grup geri alma satırları — geri alınamaz
    /// (409 <c>not-reversible</c>): çok bacaklı "iptalin iptali" zinciri kapalı.
    /// </summary>
    public static bool IsGroupReversible(CustomerBalanceTransaction anchor) =>
        anchor.Amount <= 0m && anchor.Kind is KindPurchaseDeduction or KindManualAdjustment;
```

- [ ] **Adım 4: Panel reverse**

Kurucu:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly ILogger<PanelCustomerBalanceController> _log;

    public PanelCustomerBalanceController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        ILogger<PanelCustomerBalanceController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _log = log;
    }
```

`Reverse` (`:197-246`) şöyle olur — eski yol (`DrawGroupId NULL`) gövdesi birebir korunur, yalnız ilk sorgu iki adıma bölünür:

```csharp
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid wpfCustomerId,
        Guid transactionId,
        CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct);
        if (target is null) return NotFound();

        // Ortak bakiye S1 tabanı (spec 2026-10-11 §4.9): çekim grubuna ait hedef iki kipte de grup olarak ele
        // alınır — tek satırı geri almak yarım bir geri alma bırakırdı.
        if (target.DrawGroupId is { } anchorId)
            return await ReverseDrawGroupAsync(licenseId, canonicalId, customerId, target, anchorId, ct);

        // Eski, tek satırlık hedef: bugünkü yol, birebir (satır kipi).
        if (target.WpfCustomerId != canonicalId) return NotFound();

        // Daha önce reverse edilmiş mi? Bu ön kontrol hızlı yol — yarışta iki
        // istek de buradan geçebilir (check-then-insert). Asıl hakem aşağıdaki
        // filtered unique index (N01): kaybeden SaveChanges'te unique ihlali
        // alır ve 409'a çevrilir.
        var alreadyReversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == transactionId, ct);
        if (alreadyReversed) return Problem(title: "already-reversed", statusCode: 409);

        // Reversal balance'ı sıfırın altına düşürmesin — kontrol retry
        // döngüsünün içinde (F02, bkz. ManualAdjustment).
        var reverseAmount = -target.Amount;
        bool applied;
        try
        {
            applied = await ApplyTransactionAsync(licenseId, canonicalId, customerId,
                amount: reverseAmount,
                kind: "reversal",
                originalAmount: null,
                shippingDeducted: null,
                reason: $"Reverse of {transactionId:N}",
                reverses: transactionId,
                disallowNegative: true,
                ct: ct);
        }
        catch (DbUpdateException ex) when (PersonBalance.IsDuplicateReversal(ex))
        {
            // N01: eşzamanlı ikinci reverse — index hakemliğini kaybetti.
            // Transaction geri alındı, bakiyeye hiçbir şey yazılmadı.
            return Problem(title: "already-reversed", statusCode: 409);
        }
        if (!applied) return Problem(title: "insufficient-balance", statusCode: 409);

        return Ok();
    }

    /// <summary>
    /// Çekim grubuna ait hedef (spec §4.9). Üyelik: grubun herhangi bir satırı rota satırında olmalı (S1 kip bayrağını
    /// okumaz → satır kipi koşulu; havuz kipinin S(rota) genişlemesi S2'de). Panel listesi grubu çapa Id'siyle
    /// gösterdiği için (§4.6) çapa başka satırda olsa da rota satırındaki bacaktan açılan iptal bulunur; <c>disable</c>
    /// sonrası düzeltme yolu da budur.
    /// </summary>
    private async Task<IActionResult> ReverseDrawGroupAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, CustomerBalanceTransaction target, Guid anchorId,
        CancellationToken ct)
    {
        var group = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId && (t.Id == anchorId || t.DrawGroupId == anchorId))
            .ToListAsync(ct);
        if (!group.Any(t => t.WpfCustomerId == canonicalId)) return NotFound();
        var anchor = group.FirstOrDefault(t => t.Id == anchorId);
        if (anchor is null) return NotFound(); // yetim bacak — verify sayar (§7.3-7)

        if (!PersonBalance.IsGroupReversible(anchor) || target.Kind != anchor.Kind)
            return Problem(title: "not-reversible", statusCode: 409,
                detail: "Bu hareket bir çekim grubunun iptali; iptal edilemez.");

        switch (await _person.ReverseGroupAsync(licenseId, anchorId, customerId, ct))
        {
            case PersonBalance.ReverseOutcome.Reversed:
                return Ok();
            case PersonBalance.ReverseOutcome.AlreadyReversed:
                return Problem(title: "already-reversed", statusCode: 409);
            case PersonBalance.ReverseOutcome.Busy:
                _log.LogWarning(
                    "panel balance/reverse: bakiye satırları üst üste çakıştı, 503 balance-busy (license={LicenseId})",
                    licenseId);
                return Problem(title: "balance-busy", statusCode: 503,
                    detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
            default:
                return NotFound();
        }
    }
```

Sınıftaki `IsDuplicateReversal` (`:341-350`) silinir; tek tanım `PersonBalance.IsDuplicateReversal`.

- [ ] **Adım 5: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalancePanelReverseTests|FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~PersonBalanceGroupReversalTests"`
Expected: hepsi PASS (`Reverse_undoes_previous_transaction`, `Reverse_already_reversed_returns_409`, `Reverse_kopya_Id_ile_asil_kaydin_hareketini_geri_alir`, `Cross_tenant_isolation_returns_404` değişmeden).

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelReverseTests.cs
git commit -F - <<'MSG'
feat(balance): panel iptali çekim grubunu grup olarak geri alır ya da reddeder

DrawGroupId dolu hedef çapaya çözülür; grubun bir satırı rota satırındaysa
düşüm ve elle eksi grubu grup olarak geri alınır, reversal grupları 409
not-reversible (ortak bakiye §4.9 S1 tabanı, M8). Eski tek satırlık hedefin
yolu birebir aynı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 7: Toplanmış listeler — panel GET (`Members[]`), Shopper GET (spec §4.6, §4.10, M9, K4)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` (liste bölümü), `OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs` (DTO'lar `:41-59`, GET `:61-93`), `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBalanceController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceHistoryTests.cs`, `OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelListTests.cs`, `OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalanceShopperListTests.cs`

Satır kipi (S1): bakiye ve hareketler rota/bağlantı satırınınki, ama gruplara toplanmış: aynı `DrawGroupId ?? Id`'li satırlar tek satır, `Id` = çapa, `Amount` = grubun BU satırdaki bacaklarının toplamı (listedeki tutarların toplamı her zaman `Balance`'a eşit), `Kind/OriginalAmount/ShippingDeducted/Reason/ReversesTransactionId` çapadan; `take` grup sayar. `UpdatedAt` = son para hareketi (fark 6).

- [ ] **Adım 1: Saf toplama testi (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceHistoryTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary><see cref="PersonBalance.AggregateHistory"/> — spec 2026-10-11 §4.6 toplama kuralı (saf).</summary>
public sealed class PersonBalanceHistoryTests
{
    private static readonly Guid License = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static CustomerBalanceTransaction Row(
        Guid row, decimal amount, string kind, Guid? group = null, Guid? id = null, string? reason = null,
        decimal? original = null, DateTimeOffset? at = null) => new()
    {
        Id = id ?? Guid.NewGuid(), LicenseId = License, WpfCustomerId = row, Amount = amount, Kind = kind,
        DrawGroupId = group, Reason = reason, OriginalAmount = original, CreatedAt = at ?? At,
    };

    [Fact]
    public void Ayni_gruptaki_bu_satirdaki_bacaklar_tek_satir_olur_alanlar_capadan()
    {
        var a = Guid.NewGuid();
        var other = Guid.NewGuid();
        var anchorId = Guid.NewGuid();
        var anchor = Row(other, -10m, "purchase-deduction", anchorId, anchorId, original: 120m);
        var leg1 = Row(a, -30m, "purchase-deduction", anchorId);
        var leg2 = Row(a, -5m, "purchase-deduction", anchorId);
        var refund = Row(a, 50m, "refund-full", reason: "iade", at: At.AddMinutes(-1));

        var list = PersonBalance.AggregateHistory(
            [leg1, leg2, refund], new Dictionary<Guid, CustomerBalanceTransaction> { [anchorId] = anchor, [refund.Id] = refund });

        list.Should().HaveCount(2);
        list[0].Should().Be(new PersonBalance.HistoryEntry(
            anchorId, other, -35m, "purchase-deduction", 120m, null, null, null, At));
        list[1].Should().Be(new PersonBalance.HistoryEntry(
            refund.Id, a, 50m, "refund-full", null, null, "iade", null, At.AddMinutes(-1)));
        list.Sum(e => e.Amount).Should().Be(15m, "listedeki tutarlar satırın bakiyesini verir");
    }

    [Fact]
    public void Capasi_bulunmayan_yetim_bacak_kendi_alanlariyla_listelenir()
    {
        var a = Guid.NewGuid();
        var missingAnchor = Guid.NewGuid();
        var orphan = Row(a, -7m, "purchase-deduction", missingAnchor);

        var list = PersonBalance.AggregateHistory([orphan], new Dictionary<Guid, CustomerBalanceTransaction>());

        list.Should().ContainSingle().Which.Should().Be(new PersonBalance.HistoryEntry(
            missingAnchor, a, -7m, "purchase-deduction", null, null, null, null, At));
    }
}
```

- [ ] **Adım 2: Panel liste testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelListTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>Panel GET, satır kipi (spec 2026-10-11 §4.6): gruplara toplanmış liste, grup sayan take,
/// <c>Members[]</c> (S üzerinden), <c>UpdatedAt</c> = son para hareketi (M9).</summary>
public sealed class PersonBalancePanelListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePanelListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(Guid WpfCustomerId, Guid LicenseId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);
    private sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions, MemberDto[] Members);

    private static async Task<Response> GetAsync(Kit kit, Guid row, int take = 50) =>
        (await (await kit.PanelGetAsync(row, take)).Content.ReadFromJsonAsync<Response>())!;

    [Fact]
    public async Task Ornek_1_bacak_satirinda_grup_capa_Id_siyle_ve_bu_satirdaki_tutarla_gorunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onI = await GetAsync(kit, ex.I);

        onI.Balance.Balance.Should().Be(0m);
        onI.Transactions.Should().HaveCount(2);
        onI.Transactions[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, Amount = -100m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        onI.Transactions.Sum(t => t.Amount).Should().Be(onI.Balance.Balance);

        var onT = await GetAsync(kit, ex.T);
        onT.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
    }

    [Fact]
    public async Task Take_satir_degil_grup_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        // Birleşmeden sonra aynı çekimin iki bacağı aynı satırda (§4.12).
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(a, 10m), (a, 5m)], originalAmount: 40m);

        var list = await GetAsync(kit, a, take: 1);

        list.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ids[0], Amount = -15m });
    }

    [Fact]
    public async Task Ornek_3_Members_kisinin_butun_uyelerini_silinmis_dahil_bakiyeleriyle_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var p = await kit.MemberAsync(ex.Group, 40m, purged: true);

        var list = await GetAsync(kit, ex.I);

        list.Members.Should().BeEquivalentTo(new[]
        {
            new MemberDto(ex.T, 0m, false), new MemberDto(ex.I, 0m, false),
            new MemberDto(ex.Y, 10m, false), new MemberDto(p, 40m, true),
        });
        list.Balance.Balance.Should().Be(0m, "satır kipi: bakiye rota satırınınki");
    }

    [Fact]
    public async Task UpdatedAt_kilit_dokunusunu_degil_son_para_hareketini_gosterir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 25m);
        // Kilit dokunuşu (kanca ya da birleştirici): yalnız "değişti" işareti, jetonu StampVersions ilerletir.
        await kit.Sales.WithDbAsync(async db =>
        {
            var row = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a);
            db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
        });
        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        var lastMovement = await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.WpfCustomerId == a).MaxAsync(t => t.CreatedAt));

        (await GetAsync(kit, a)).Balance.UpdatedAt.Should().Be(lastMovement);
    }
}
```

- [ ] **Adım 3: Shopper liste testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalanceShopperListTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Shopper GET, satır kipi (spec 2026-10-11 §4.10, K4): bağlantının satırı asıl kayda çözülür, hareketler gruplara
/// toplanır; açıklama yalnız grubun çapası bağlı satırdaysa döner (başka üyeden gelen grupta tutar ve tür görünür,
/// <c>Reason</c> null).
/// </summary>
public sealed class PersonBalanceShopperListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceShopperListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, DateTimeOffset CreatedAt);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions);
    private sealed record AuthDto(string AccessToken);

    /// <summary>Yayıncının lisansına bir Shopper kaydı; kayıt bağlantıyı ve projeksiyonu açar. Döner: istemci ve
    /// bağlı projeksiyonun Id'si.</summary>
    private async Task<(HttpClient Client, Guid Linked)> RegisterShopperAsync(Kit kit)
    {
        var code = "pb" + Guid.NewGuid().ToString("N")[..8];
        await kit.Sales.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.ShopperCode = code;
            license.ShopperCodeUpdatedAt = DateTimeOffset.UtcNow;
        });
        var client = _factory.CreateClient();
        var giris = $"g-{Guid.NewGuid():N}";
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code, fullName = "Bakiye Alıcı", phone = TestPhone.NewE164(), password = giris,
            address = "Adres", platform = "youtube", username = $"s{Guid.NewGuid():N}"[..12],
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await resp.Content.ReadFromJsonAsync<AuthDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var linked = await kit.Sales.ReadDbAsync(db => db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.LicenseId == kit.LicenseId && l.WpfCustomerId != null)
            .Select(l => l.WpfCustomerId!.Value).SingleAsync());
        return (client, linked);
    }

    private static async Task<Response> GetAsync(HttpClient client, Guid licenseId) =>
        (await client.GetFromJsonAsync<Response>($"/api/v1/shopper/broadcasters/{licenseId}/balance"))!;

    [Fact]
    public async Task Baska_uyede_capalanan_grubun_aciklamasi_gizlenir_kendi_aciklamasi_gorunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, a) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        await kit.Sales.WithDbAsync(async db =>
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == a)).GroupId = g);
        var b = await kit.MemberAsync(g, 30m);
        var own = await kit.FundAsync(a, 50m, reason: "kendi iadesi");
        var ids = await kit.SeedDrawAsync(Kit.KindManual, [(b, 10m), (a, 20m)], reason: "yayıncının iç notu");

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(30m);
        body.Transactions.Should().HaveCount(2);
        body.Transactions.Single(t => t.Id == ids[0]).Should().BeEquivalentTo(
            new { Amount = -20m, Kind = Kit.KindManual, Reason = (string?)null });
        body.Transactions.Single(t => t.Id == own).Reason.Should().Be("kendi iadesi");
    }

    [Fact]
    public async Task Baglanti_kopyaya_isaret_ediyorsa_asil_kaydin_bakiyesi_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, a) = await RegisterShopperAsync(kit);
        await kit.FundAsync(a, 45m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);
        await kit.Sales.WithDbAsync(async db =>
            (await db.ShopperBroadcasterLinks.SingleAsync(l => l.LicenseId == kit.LicenseId)).WpfCustomerId = copy);

        (await GetAsync(client, kit.LicenseId)).Balance.Balance.Should().Be(45m);
    }
}
```

- [ ] **Adım 4: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalanceShopperListTests"`
Expected: FAIL — `AggregateHistory`/`HistoryEntry` yok (derleme).

- [ ] **Adım 5: `PersonBalance` liste bölümü**

`PersonBalance.cs`'e ekle:

```csharp
    // ── Toplanmış liste (§4.6, §4.10, M9) ────────────────────────────────────

    /// <summary>Listedeki bir grup.</summary>
    /// <param name="Id">Çapanın Id'si (eski satırda kendi Id'si).</param>
    /// <param name="AnchorWpfCustomerId">Çapanın satırı — Shopper'ın açıklama maskesi (K4) için.</param>
    /// <param name="Amount">Grubun verilen satırlardaki bacaklarının toplamı.</param>
    public sealed record HistoryEntry(
        Guid Id, Guid AnchorWpfCustomerId, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);

    /// <summary>
    /// Hareketleri gruba göre toplar (spec §4.6; saf). Aynı <c>DrawGroupId ?? Id</c>'li satırlar tek satır olur;
    /// tutar verilen satırların toplamı (ayırmadan sonra öbür kişide kalan bacaklar sayılmaz — listedeki tutarların
    /// toplamı her zaman bakiyeye eşit); tür, tutarlar, açıklama ve iptal bağı çapadan. Çapası bulunmayan (yetim)
    /// bacak kendi alanlarıyla listelenir; <c>verify</c> onu ayrıca sayar. Yeniden eskiye sıralı; bir grubun bütün
    /// satırları aynı <c>CreatedAt</c>'le yazıldığı için grup bölünmez.
    /// </summary>
    /// <param name="anchors">Grup anahtarı → çapa (çapa başka bir satırda olabilir).</param>
    public static IReadOnlyList<HistoryEntry> AggregateHistory(
        IEnumerable<CustomerBalanceTransaction> rows, IReadOnlyDictionary<Guid, CustomerBalanceTransaction> anchors) =>
        rows
            .GroupBy(GroupKeyOf)
            .Select(g =>
            {
                var anchor = anchors.TryGetValue(g.Key, out var found) ? found : g.OrderBy(r => r.Id).First();
                return new HistoryEntry(
                    g.Key, anchor.WpfCustomerId, g.Sum(r => r.Amount), anchor.Kind, anchor.OriginalAmount,
                    anchor.ShippingDeducted, anchor.Reason, anchor.ReversesTransactionId, g.Max(r => r.CreatedAt));
            })
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToList();

    /// <summary>
    /// Verilen satırların son <paramref name="take"/> GRUBU, toplanmış (§4.6). Satır kipinde tek satır; havuz kipinde
    /// (S2) S'nin bütün üyeleri.
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(
        Guid licenseId, IReadOnlyCollection<Guid> rowIds, int take, CancellationToken ct)
    {
        var ids = rowIds.ToList();
        var keys = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId))
            .GroupBy(t => t.DrawGroupId ?? t.Id)
            .Select(g => new { Key = g.Key, At = g.Max(t => t.CreatedAt) })
            .OrderByDescending(x => x.At)
            .ThenByDescending(x => x.Key)
            .Take(take)
            .Select(x => x.Key)
            .ToListAsync(ct);
        if (keys.Count == 0) return [];

        var rows = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId) && keys.Contains(t.DrawGroupId ?? t.Id))
            .ToListAsync(ct);
        var anchors = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && keys.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        return AggregateHistory(rows, anchors);
    }

    /// <summary>Verilen satırların son para hareketinin anı (M9): kilit dokunuşu "güncellendi" zamanını oynatmaz.</summary>
    public async Task<DateTimeOffset?> LastMovementAtAsync(
        Guid licenseId, IReadOnlyCollection<Guid> rowIds, CancellationToken ct)
    {
        var ids = rowIds.ToList();
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId))
            .MaxAsync(t => (DateTimeOffset?)t.CreatedAt, ct);
    }
```

- [ ] **Adım 6: Panel GET**

DTO'lar:

```csharp
    /// <summary>Ortak bakiye (spec 2026-10-11 §4.6): kişinin bir üyesi — WPF'in Ayır onayı kullanır; panel yok sayar.</summary>
    public sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);

    public sealed record BalanceDetailsResponse(
        BalanceDto Balance,
        TransactionDto[] Transactions,
        MemberDto[] Members);
```

`Get` (`:63-93`):

```csharp
    [HttpGet]
    public async Task<IActionResult> Get(
        Guid wpfCustomerId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take < 1 || take > 200) take = 50;

        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Ortak bakiye S1, satır kipi (spec 2026-10-11 §4.6): bakiye ve hareketler rota satırınınki, hareketler
        // gruplara toplanmış; take grup sayar. UpdatedAt son para hareketidir (M9): grup değişimi kancasının ve
        // birleştiricinin jeton dokunuşu "güncellendi" zamanını oynatmaz.
        var row = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new { b.Balance, b.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        var lastMovement = await _person.LastMovementAtAsync(licenseId, [canonicalId], ct);
        // Yanıttaki Id rotadaki Id'dir (önceki davranış); bakiye asıl kaydınki.
        var balance = new BalanceDto(wpfCustomerId, licenseId, row?.Balance ?? 0m,
            lastMovement ?? row?.UpdatedAt ?? DateTimeOffset.UtcNow);

        var history = await _person.HistoryAsync(licenseId, [canonicalId], take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.Reason, h.ReversesTransactionId, h.CreatedAt))
            .ToArray();

        // Members (§4.6): iki kipte de S üzerinden, üye başına satır bakiyesi (silinmişler dahil).
        var person = await _person.MembersAsync(licenseId, canonicalId, ct);
        var members = person.Members.Select(m => new MemberDto(m.WpfCustomerId, m.Balance, m.Purged)).ToArray();

        return Ok(new BalanceDetailsResponse(balance, transactions, members));
    }
```

- [ ] **Adım 7: Shopper GET**

`ShopperBalanceController.cs` — kurucu ve `Get`:

```csharp
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;

    public ShopperBalanceController(LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
    }
```

(`using OrderDeck.LicenseServer.Services.CustomerSync;` ekle.) `Get`'in `link` sorgusundan sonrası:

```csharp
        if (link is null) return NotFound();

        // Ortak bakiye (spec 2026-10-11 §4.10): kişi çözümü asıl kayıttan başlar. Birleştirme bağlantıyı taşıdığı
        // için bugün çoğunlukla aynı Id; eski bir bağlantı kopyaya işaret edebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, link.WpfCustomerId, ct);

        var row = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new { b.Balance, b.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        var lastMovement = await _person.LastMovementAtAsync(licenseId, [canonicalId], ct);
        var balance = new BalanceDto(row?.Balance ?? 0m, lastMovement ?? row?.UpdatedAt ?? DateTimeOffset.UtcNow);

        // Satır kipi: hareketler bağlı satırınki, gruplara toplanmış (§4.10). K4 maskesi: açıklama yalnız grubun
        // çapası bu satırdaysa döner — başka üyeden gelen grupta tutar ve tür görünür, serbest metin gizli.
        var history = await _person.HistoryAsync(licenseId, [canonicalId], take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.AnchorWpfCustomerId == canonicalId ? h.Reason : null, h.CreatedAt))
            .ToArray();

        return Ok(new BalanceDetailsResponse(balance, transactions));
```

- [ ] **Adım 8: Koştur — PASS + regresyon**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceHistoryTests|FullyQualifiedName~PersonBalancePanelListTests|FullyQualifiedName~PersonBalanceShopperListTests|FullyQualifiedName~PanelCustomerBalanceControllerTests|FullyQualifiedName~ShopperBalanceControllerTests|FullyQualifiedName~PersonBalancePanelReverseTests"`
Expected: hepsi PASS. Eski satırlarda her satır kendi grubudur: var olan panel/Shopper testlerinin çıktısı aynı (`Get_no_balance_returns_zero_with_empty_transactions` vb.).

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs \
  OrderDeck.LicenseServer/Controllers/Panel/PanelCustomerBalanceController.cs \
  OrderDeck.LicenseServer/Controllers/Shopper/ShopperBalanceController.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceHistoryTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Panel/PersonBalancePanelListTests.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Shopper/PersonBalanceShopperListTests.cs
git commit -F - <<'MSG'
feat(balance): panel ve Shopper listeleri çekim gruplarını toplar

Aynı DrawGroupId'li satırlar tek satır (Id = çapa, tutar bu satırdaki
bacakların toplamı), take grup sayar, UpdatedAt son para hareketi. Panel
yanıtına kişinin üyeleri (Members[], silinmişler dahil) eklenir; Shopper
bağlantıyı asıl kayda çözer ve başka üyede çapalanan grubun açıklamasını
gizler (ortak bakiye §4.6, §4.10, M9, K4). Satır kipi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 8: Testcontainers 3, 4, 5 + 503 — grup geri alması yarışları (spec §8.2-3, -4, -5; §4.5)

**Files:**
- Oluştur: `OrderDeck.LicenseServer.Tests/TestHelpers/RaceSaveHook.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceReversalRaceTests.cs`

Kod değişikliği beklenmez: Görev 2 (sıfır damga), Görev 5 (baştan kurma) ve Görev 6 (panel) bu yarışları kapatır; bu görev onları gerçek SQL Server'da kanıtlar. Bir test kırmızı kalırsa önce sistematik hata ayıklama: yarış sırayla (kanca) kurulur, zamanlamayla değil.

- [ ] **Adım 1: Yarış kancası**

`OrderDeck.LicenseServer.Tests/TestHelpers/RaceSaveHook.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sıralı yarış kancası (gerçek SQL Server testleri): bir <c>SaveChanges</c>'in SQL'i gitmeden HEMEN ÖNCE rakip bir
/// yazımı koşturur — yarış zamanlamaya değil sıraya dayanır (<see cref="SaveHookInterceptor"/> gerekçesi).
/// <see cref="Once"/> tek sefer, <see cref="Always"/> her kayıtta (üst üste çakışma). Kancanın kendi yazımları kancayı
/// yeniden tetiklemez. Jeton çakışmalarını (<see cref="DbUpdateConcurrencyException"/>) sayar.
/// </summary>
public sealed class RaceSaveHook : SaveChangesInterceptor
{
    private Func<Task>? _action;
    private bool _repeat;
    private int _running;
    private int _concurrencyFailures;

    public int ConcurrencyFailures => Volatile.Read(ref _concurrencyFailures);

    public void Once(Func<Task> action)
    {
        _repeat = false;
        Volatile.Write(ref _action, action);
    }

    public void Always(Func<Task> action)
    {
        _repeat = true;
        Volatile.Write(ref _action, action);
    }

    public void Disarm() => Volatile.Write(ref _action, null);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return result;
        try
        {
            var action = _repeat ? Volatile.Read(ref _action) : Interlocked.Exchange(ref _action, null);
            if (action is not null) await action();
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
        return result;
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is DbUpdateConcurrencyException) Interlocked.Increment(ref _concurrencyFailures);
        return Task.CompletedTask;
    }
}

/// <summary><see cref="RelationalApiFactory"/> + paylaşılan bir <see cref="RaceSaveHook"/>.</summary>
public sealed class RaceRelationalApiFactory : RelationalApiFactory
{
    public RaceRelationalApiFactory(string connectionString) : base(connectionString) { }

    public RaceSaveHook Hook { get; } = new();

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
}
```

- [ ] **Adım 2: Yarış testleri**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceReversalRaceTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri almasının eşzamanlılık sözleşmesi gerçek SQL Server'da (spec 2026-10-11 §8.2-3, -4, -5; §4.5, §4.12):
/// ya hepsi ya hiçbiri (D5), bacak başına tek geri alma (N01), çakışmada grup baştan kurulur (delta iki kez eklenmez),
/// para bacağın GÜNCEL satırına yazılır. InMemory'de tekil indeks ve atomik kayıt yok. Canlı yayın sırasında
/// ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceReversalRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;

    private readonly SqlServerContainerFixture _sql;
    private RaceRelationalApiFactory _factory = null!;

    public PersonBalanceReversalRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RaceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>TestServer, sunucu tarafında yakalanmayan istisnayı istemciye fırlatır; prod'da 500 olurdu.</summary>
    private static async Task<HttpStatusCode> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            return (await call()).StatusCode;
        }
        catch (Exception)
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    private static async Task ShouldBeReversedOnceAsync(Kit kit, Kit.Example1 ex)
    {
        var rows = await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2]);
        rows.Should().HaveCount(3, "ya hepsi ya hiçbiri (D5), bacak başına tek geri alma (N01)");
        rows.Select(r => r.DrawGroupId).Distinct().Should().ContainSingle("tek grup geri alması");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>§8.2-3a: WPF ∥ WPF — aynı çok bacaklı çekimi 8 paralel geri alma.</summary>
    [Fact]
    public async Task WPF_paralel_grup_geri_almasindan_yalniz_biri_kazanir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        // Isınma: rota + EF JIT (var olmayan hareket → 404), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.ReverseAsync(Guid.NewGuid())));

        var statuses = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(_ => StatusAsync(() => kit.ReverseAsync(ex.K))));

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(Parallel - 1,
            "kaybedenler already-reversed — 500 varsa N01/kilitlenme yolu eşleşmiyor");
        await ShouldBeReversedOnceAsync(kit, ex);
    }

    /// <summary>§8.2-3b: WPF ∥ panel — aynı grubu iki yüzey birlikte geri alır.</summary>
    [Fact]
    public async Task WPF_ve_panel_paralel_grup_geri_almasindan_yalniz_biri_kazanir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PanelReverseAsync(ex.I, Guid.NewGuid())));

        var statuses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? StatusAsync(() => kit.ReverseAsync(ex.K))
            : StatusAsync(() => kit.PanelReverseAsync(ex.I, ex.K))));

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(Parallel - 1);
        await ShouldBeReversedOnceAsync(kit, ex);
    }

    /// <summary>
    /// §8.2-4: grup geri alması ∥ bacak satırlarından birine eşzamanlı artı yazım (panel iadesi). İlk deneme I'nin
    /// jetonuna çarpar; eski "deltayı yeniden uygula" kalıbı çakışmayan Y'ye 20'yi iki kez eklerdi (Y = 50).
    /// </summary>
    [Fact]
    public async Task Bacak_satirina_eszamanli_iade_gelince_grup_bastan_kurulur_delta_iki_kez_eklenmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        _factory.Hook.Once(async () => (await kit.PanelRefundAsync(ex.I, 50m)).EnsureSuccessStatusCode());

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "ilk deneme iadenin ilerlettiği jetona çarpmalı");
        (await kit.BalanceAsync(ex.I)).Should().Be(150m, "0 + 50 iade + 100 geri alma");
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m, "çakışmayan satıra delta bir kez");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
    }

    /// <summary>
    /// §8.2-5: grup geri alması ∥ kimlik birleşmesi ("@" ikizi), bacak satırı I 0'da kalmışken kopyaya dönüyor.
    /// Birleştirici sıfır bakiyeli kopya satırını damgalar (M12) → geri alma çakışır, bacağın güncel (asıl) satırıyla
    /// yeniden kurulur; para asıl kayda yazılır, görünmez kopyada kalmaz.
    /// </summary>
    [Fact]
    public async Task Kimlik_birlesmesiyle_carpisan_grup_geri_almasi_parayi_asil_kayda_yazar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var ex = await kit.Example1Async(iUsername: "@" + name);
        var canonical = await kit.MemberAsync(null, balanceRow: false, platform: "instagram", username: name);
        _factory.Hook.Once(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>();
            (await job.RunAsync(kit.LicenseId, apply: true, default, atTwins: true)).FailedGroups.Should().Be(0);
        });

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.ProjectionAsync(ex.I)).MergedIntoId.Should().Be(canonical, "'@'lı satır '@'sız asıl kayda birleşti");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "sıfır bakiyeli kopya satırı damgalandı (M12)");
        (await kit.ReversalsOfAsync([ex.L1])).Single().WpfCustomerId.Should().Be(canonical);
        (await kit.BalanceAsync(canonical)).Should().Be(100m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m, "para görünmez kopyada kalmaz");
        (await kit.LedgerHoldsAsync(canonical)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(ex.I)).Should().BeTrue("D1");
    }

    /// <summary>§4.5: üç denemede de çakışan grup geri alması 503 <c>balance-busy</c>; işlem bütün ya da hiç.</summary>
    [Fact]
    public async Task Uc_denemede_de_cakisan_grup_geri_almasi_503_balance_busy_ve_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        _factory.Hook.Always(() => TouchBalanceAsync(ex.I));

        var resp = await kit.ReverseAsync(ex.K);
        _factory.Hook.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Kit.TitleAsync(resp)).Should().Be("balance-busy");
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().BeEmpty("SaveChanges tek işlem");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>Kilit dokunuşu: satıra yalnız "değişti" işareti, jetonu StampVersions ilerletir.</summary>
    private async Task TouchBalanceAsync(Guid row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == row);
        db.Entry(balance).Property(b => b.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Adım 3: Koştur (Docker açık)**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceReversalRaceTests|FullyQualifiedName~CustomerBalanceConcurrencyTests"`
Expected: 5 + 4 test PASS. (`CustomerBalanceConcurrencyTests`'in dört testi — §8.2-14'ün satır kipi yarısı — değişmeden yeşil.)

Kırmızı kalırsa olası nedenler: (a) 3a/3b'de 500 → `IsDuplicateReversal` ya da `IsDeadlock` eşleşmiyor (SqlException numarasına bak); (b) 4'te `ConcurrencyFailures = 0` → kanca reversal'ın değil başka bir kaydın önünde tetiklendi (istekte erken bir `SaveChanges` var mı?); (c) 5'te para kopyada → Görev 2'nin `else if` kolu eksik.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/TestHelpers/RaceSaveHook.cs \
  OrderDeck.LicenseServer.Tests/Controllers/Licenses/PersonBalanceReversalRaceTests.cs
git commit -F - <<'MSG'
test(balance): grup geri almasının yarışlarını gerçek SQL Server'da kanıtla

WPF ∥ WPF ve WPF ∥ panel'de tek kazanan ve ya hepsi ya hiçbiri; bacak
satırına eşzamanlı iade gelince delta iki kez eklenmez; kimlik birleşmesiyle
çarpışınca para asıl kayda yazılır; üç çakışmada 503 balance-busy, hiçbir
şey yazılmaz (ortak bakiye §8.2-3, -4, -5).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 9: `GroupId` kancası I — bakiye jetonu dokunuşu + convention testi + Testcontainers 12 (spec §4.11, M6)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonGroupSaveHook.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (`SaveChanges` override'ları `:198-213`)
- Test: `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookTests.cs`, `OrderDeck.LicenseServer.Tests/Services/CustomerSync/GroupIdWriteConventionTests.cs`, `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookRelationalTests.cs`

Kanca kaydetmeden önce: `GroupId`'si ordinal değişmiş her izlenen projeksiyonun bakiye satırını (önce `Local`, yoksa tek sorgu) "değişti" işaretler; jetonu Görev 2'nin `StampVersions`'ı ilerletir. Sunucuda `GroupId`'ye yazan üç yer (`CustomerFieldMerge.cs:54-56` senkron, `CustomerGroupUnion.cs:69` `group-customers`/`--at-twins`, `CustomerIdentityMergeJob.cs:567`) izlenen nesneler üzerinden yazar; convention testi ham SQL / `ExecuteUpdate` yazımını yasaklar (kanca onu göremezdi).

- [ ] **Adım 1: InMemory testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <c>GroupId</c> kancası (spec 2026-10-11 §4.11, M6): numara değişimi para tutan satırın bakiye jetonunu ilerletir —
/// apply üyeliği okuduktan sonra kaydedilen bir ayırma, apply'ın UPDATE'ini tutmaz. Tutar değişmez.
/// </summary>
public sealed class PersonGroupHookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonGroupHookTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Senkronun_GroupId_degisimi_bakiye_jetonunu_ilerletir_tutara_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;
        var g2 = Kit.NewGroup();

        (await kit.SyncGroupAsync(a, g2)).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g2);
        var after = (await kit.BalanceRowAsync(a))!;
        after.UpdatedAt.Should().BeAfter(before);
        after.Balance.Should().Be(40m);
    }

    [Fact]
    public async Task Ayni_numarayla_gelen_senkron_jetona_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        (await kit.SyncGroupAsync(a, g)).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task Yalniz_harf_farki_da_degisimdir_ordinal()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        (await kit.SyncGroupAsync(a, g.ToUpperInvariant())).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public async Task Bakiye_satiri_olmayan_uyede_satir_acilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), balanceRow: false);

        (await kit.SyncGroupAsync(a, Kit.NewGroup())).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a)).Should().BeNull();
    }

    [Fact]
    public async Task Senkron_SaveChanges_yolu_da_jetonu_ilerletir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == a)).GroupId = null;
            db.SaveChanges();
        }

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(before);
    }
}
```

- [ ] **Adım 2: Convention testi**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/GroupIdWriteConventionTests.cs`:

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Ortak bakiye §4.11: <c>GroupId</c> kancası yalnız izlenen nesnelerdeki değişimi görür. Sunucu kodunda
/// <c>GroupId</c>'ye <c>ExecuteUpdate</c> ya da ham SQL ile yazım olursa kanca onu kaçırır: ayırma, apply'ın
/// okuduğu bakiye satırının jetonunu ilerletmez ve para ayrılmış satırdan çekilebilir. Kaynak taranır (göçler hariç).
/// </summary>
public sealed class GroupIdWriteConventionTests
{
    private static readonly Regex ExecuteUpdateOnGroupId =
        new(@"SetProperty\s*\(\s*\w+\s*=>\s*\w+\.GroupId\s*,", RegexOptions.Compiled);

    // SQL anahtar sözcükleri bu kod tabanında büyük harfle yazılıyor; GroupIdChangedAt eşleşmez (\b).
    private static readonly Regex RawSqlOnGroupId =
        new(@"\bUPDATE\b[^;]{0,400}?\bSET\b[^;]{0,400}?\[?\bGroupId\b\]?\s*=(?!=)",
            RegexOptions.Compiled | RegexOptions.Singleline);

    [Theory]
    [InlineData("db.WpfCustomerProjections.ExecuteUpdateAsync(s => s.SetProperty(p => p.GroupId, (string?)null));")]
    [InlineData("\"UPDATE WpfCustomerProjections SET GroupId = @g WHERE Id = @id\"")]
    [InlineData("\"UPDATE [WpfCustomerProjections] SET [UpdatedAt] = @u, [GroupId] = NULL WHERE [Id] = @id\"")]
    public void Kaliplar_yasak_yazimi_yakalar(string sample) =>
        (ExecuteUpdateOnGroupId.IsMatch(sample) || RawSqlOnGroupId.IsMatch(sample)).Should().BeTrue();

    [Theory]
    [InlineData("row.GroupId = target;")]
    [InlineData("\"UPDATE Orders SET UpdatedAt = @u\"; var x = p.GroupIdChangedAt;")]
    [InlineData("s.SetProperty(p => p.GroupIdChangedAt, now)")]
    public void Kaliplar_izinli_yazimi_yakalamaz(string sample) =>
        (ExecuteUpdateOnGroupId.IsMatch(sample) || RawSqlOnGroupId.IsMatch(sample)).Should().BeFalse();

    [Fact]
    public void Sunucu_kodunda_GroupIdye_ExecuteUpdate_ya_da_ham_SQL_ile_yazim_yok()
    {
        var root = ServerSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Excluded(root, file))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return ExecuteUpdateOnGroupId.IsMatch(text) || RawSqlOnGroupId.IsMatch(text);
            })
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        offenders.Should().BeEmpty(
            "GroupId yalnız izlenen nesneler üzerinden yazılır — PersonGroupSaveHook başka yazımı göremez (§4.11)");
    }

    private static bool Excluded(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return relative.StartsWith("Data/Migrations/", StringComparison.Ordinal)
            || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string ServerSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "OrderDeck.LicenseServer");
            if (File.Exists(Path.Combine(candidate, "OrderDeck.LicenseServer.csproj"))) return candidate;
        }
        throw new InvalidOperationException("OrderDeck.LicenseServer kaynak dizini bulunamadı (test kaynak ağacından koşmalı).");
    }
}
```

- [ ] **Adım 3: Testcontainers 12**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookRelationalTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// §8.2-12: senkronun <c>GroupId</c> değişikliği bakiye jetonunu gerçek SQL Server'da ilerletir; okuma ile yazma
/// arasına giren bir grup değişimi bakiye yazımını çatıştırır, apply'ın F02 döngüsü taze okumayla tamamlar. Canlı
/// yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonGroupHookRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RaceRelationalApiFactory _factory = null!;

    public PersonGroupHookRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RaceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    [Fact]
    public async Task Okunmus_bakiye_satiri_grup_degisiminden_sonra_yazilamaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 100m);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a);

        (await kit.SyncGroupAsync(a, null)).EnsureSuccessStatusCode(); // Ayır

        balance.Balance -= 30m;
        var write = async () => await db.SaveChangesAsync();
        await write.Should().ThrowAsync<DbUpdateConcurrencyException>("kanca ayırmada para tutan satırın jetonunu ilerletti");
    }

    [Fact]
    public async Task Apply_okumasi_ile_yazimi_arasina_giren_grup_degisimi_cakisir_F02_taze_okumayla_tamamlar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 100m);
        var g2 = Kit.NewGroup();
        _factory.Hook.Once(async () => (await kit.SyncGroupAsync(a, g2)).EnsureSuccessStatusCode());

        var resp = await kit.ApplyAsync(a, 30m, 200m, key: Guid.NewGuid(), saleScope: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<ApplyDto>())!.AppliedAmount.Should().Be(30m);
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "apply'ın ilk UPDATE'i ilerlemiş jetona çarptı");
        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g2);
        (await kit.BalanceAsync(a)).Should().Be(70m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
    }
}
```

- [ ] **Adım 4: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonGroupHook|FullyQualifiedName~GroupIdWriteConventionTests"`
Expected: `PersonGroupHookTests` jeton testleri ve iki relational test FAIL (jeton ilerlemiyor, çakışma yok); convention testleri PASS (bugün yasak yazım yok — kural sabitlenir).

- [ ] **Adım 5: Kanca**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonGroupSaveHook.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// <c>LicenseDbContext.SaveChanges</c> kancası (ortak bakiye spec 2026-10-11 §4.11, M6, M11). Kaydetmeden önce,
/// <c>StampVersions</c>'tan önce koşar:
/// <list type="number">
///   <item><b>Bakiye jetonu dokunuşu:</b> <c>GroupId</c>'si ordinal değişmiş her izlenen projeksiyonun bakiye satırı
///   (önce izlenenlerde, yoksa tek sorguyla) "değişti" işaretlenir; jetonu <c>StampVersions</c> ilerletir. Apply
///   üyeliği okuyup bakiye satırını yükledikten sonra kaydedilen bir ayırma, apply'ın UPDATE'ini tutmaz ve plan
///   baştan kurulur. Bakiye satırı olmayan üyede dokunulacak bir şey yok: para taşımaz.</item>
/// </list>
/// Sunucuda GroupId'ye yazan üç yer de (senkron, <c>group-customers</c>/<c>--at-twins</c>, kimlik birleşmesi) izlenen
/// nesnelerle yazar; ham SQL / ExecuteUpdate yazımını <c>GroupIdWriteConventionTests</c> yasaklar.
///
/// <para><b>sync/async ikizi:</b> gövde tek; <paramref name="sync"/> true iken sorgular eşzamanlı koşar ve dönen görev
/// tamamlanmış olur (EF'in kendi kalıbı) — senkron <c>SaveChanges</c> onu bekleyebilir.</para>
/// </summary>
internal static class PersonGroupSaveHook
{
    internal static async Task RunAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        if (!db.ChangeTracker.AutoDetectChangesEnabled) return; // salt okunur bağlam (LicenseReadOnlyDbContext)
        await TouchBalancesAsync(db, sync, ct);
    }

    private static async Task TouchBalancesAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        var changed = db.ChangeTracker.Entries<WpfCustomerProjection>()
            .Where(e => e.State == EntityState.Modified && GroupChanged(e))
            .Select(e => (e.Entity.LicenseId, e.Entity.Id))
            .ToList();
        if (changed.Count == 0) return;

        foreach (var license in changed.GroupBy(c => c.LicenseId))
        {
            var ids = license.Select(c => c.Id).ToHashSet();
            var local = db.CustomerBalances.Local
                .Where(b => b.LicenseId == license.Key && ids.Contains(b.WpfCustomerId))
                .ToList();
            var missing = ids.Except(local.Select(b => b.WpfCustomerId)).ToList();
            var loaded = new List<CustomerBalance>();
            if (missing.Count > 0)
            {
                var query = db.CustomerBalances
                    .Where(b => b.LicenseId == license.Key && missing.Contains(b.WpfCustomerId));
                loaded = sync ? query.ToList() : await query.ToListAsync(ct);
            }
            foreach (var balance in local.Concat(loaded))
            {
                var entry = db.Entry(balance);
                if (entry.State is EntityState.Added or EntityState.Deleted) continue;
                entry.Property(b => b.UpdatedAt).IsModified = true;
            }
        }
    }

    private static bool GroupChanged(EntityEntry<WpfCustomerProjection> entry) =>
        !string.Equals(entry.Property(p => p.GroupId).OriginalValue, entry.Entity.GroupId, StringComparison.Ordinal);
}
```

`LicenseDbContext.cs` — iki override (başa `using OrderDeck.LicenseServer.Services.CustomerSync;`):

```csharp
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncDerivedColumns();
        // Ortak bakiye §4.11 kancası; sync: true iken görev tamamlanmış döner (bkz. PersonGroupSaveHook).
        PersonGroupSaveHook.RunAsync(this, sync: true, CancellationToken.None).GetAwaiter().GetResult();
        StampNetgsmAccountVersions();
        StampPaymentMatchVersions();
        StampCustomerBalanceVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncDerivedColumns();
        await PersonGroupSaveHook.RunAsync(this, sync: false, cancellationToken);
        StampNetgsmAccountVersions();
        StampPaymentMatchVersions();
        StampCustomerBalanceVersions();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
```

(Override'ların üstündeki "DİKKAT — parametresiz SaveChanges()…" yorumu aynen kalır.)

- [ ] **Adım 6: Koştur — PASS + senkron/iş regresyonu**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonGroupHook|FullyQualifiedName~GroupIdWriteConventionTests|FullyQualifiedName~LicensesWpfCustomersSync|FullyQualifiedName~WpfCustomer|FullyQualifiedName~CustomerGroupingJob|FullyQualifiedName~CustomerIdentityMergeJob|FullyQualifiedName~CustomerFieldMerge"`
Expected: hepsi PASS. Senkronun var olan çakışma döngüsü (`LicensesWpfCustomersSyncController.cs:256-297`) kancanın dokunuşunu taze okumayla yeniden uygular; iş testleri grup geri alıp sayar.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonGroupSaveHook.cs OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/GroupIdWriteConventionTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonGroupHookRelationalTests.cs
git commit -F - <<'MSG'
feat(balance): GroupId değişimi para tutan satırın bakiye jetonunu ilerletir

SaveChanges kancası numarası ordinal değişen projeksiyonun bakiye satırını
"değişti" işaretler; okuma ile yazma arasına giren ayırma apply'ı
çatıştırır, F02 taze okumayla tamamlar (ortak bakiye §4.11, M6). Convention
testi GroupId'ye ham SQL ya da ExecuteUpdate yazımını yasaklar.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 10: `GroupId` kancası II — silinmiş üyenin numara izlemesi + Testcontainers 13 (spec §4.11, §4.12, K3, M11)

**Files:**
- Değiştir: `OrderDeck.LicenseServer/Services/CustomerSync/PersonGroupSaveHook.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowTests.cs` (InMemory), `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowRelationalTests.cs` (Testcontainers 13)
- **İnceleme eki (2026-10-21, spec §15-7; sahip onayı S1 merge'ünden önce):** numara izlemesi silinmiş üyeyi yalnız sunucuda taşıdığı için `payment-requests/begin`'in (b) adımı yalnız canlı sunucu üyelerini ister, (f) toplamı istemcinin üyeleriyle sayar; arama ve kaydın üyeleri silinmişleri taşımaya devam eder. Değiştir: `OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentPersonResolver.cs` (`ServerGroupMembersAsync`), `OrderDeck.LicenseServer/Services/Sync/PaymentRequests/PaymentRequestLeaseService.cs` (`DecideAsync` b ve f). Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/PaymentRequestBeginTests.cs`, `PaymentRequestBeginRelationalTests.cs` (`Sunucuda_tasinan_silinmis_uye_istemcide_yoksa_kira_acilir_canli_uye_eksikse_409`), `PaymentRequestTestKit.cs` (`SeedProjectionAsync(purged:)`). Bu ek olmadan Görev 10 prod'a çıkmaz.
- **İnceleme eki 2 (2026-10-21, spec §5.5, §4.13, §10):** "canlı üye kaldı mı" kararı kilitsiz okunur, hakemi yok: eşzamanlı iki kayıt numaranın kalan canlı üyelerini ayrı ayrı ayırırsa silinmiş üye geride kalabilir. Belgelendi (kanca sınıf yorumu, spec §5.5 satırı, §4.13 "Hakem" hücresi); kalıcı hakem (`sp_getapplock`) ya da §7.3-4 kaleminin uyarıya çevrilmesi §10 açık kalanlarda sahip kararı. Kiranın üye kilidi istemcide olmayan silinmiş üyeyi kapsamaz; `PaymentRequestLeaseService` sınıf yorumunda ve §15-7'de bilinçli olarak yazıldı.

Kural: bu kayıtta canlı asıl bir üye `g`'den **ayrıldıysa** (a: `GroupId` ordinal değişti → hedef yeni numara; b: kopyaya döndü → hedef asıl kaydın kayıttaki numarası; KVKK silmesi ayrılma değil) ve kayıttan sonra `g`'de canlı asıl üye **kalmadıysa** (veritabanı + izlenen değişiklikler, ordinal), `g`'deki silinmiş asıl üyeler ayrılanların numarasına taşınır, `CustomerGroupUnion.StampAt` ile damgalı. Hedef boşsa (Ayır) silinmiş üye `g`'de tek kalır (K3 istisnası, §10 açık karar); ayrılanlar farklı numaralara gittiyse de kalır ve günlüğe yazılır. Taşıma da bir `GroupId` değişimidir: silinmiş üyenin bakiye jetonu Görev 9'un dokunuşuyla ilerler (sıra: önce izleme, sonra dokunuş).

- [ ] **Adım 1: InMemory testlerini yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Silinmiş üyenin numara izlemesi, senkron yolu (spec 2026-10-11 §4.11, §4.12, K3): grup boşalınca silinmiş üye
/// ayrılanların numarasına geçer; Ayır'da ve dağılan ayrılışta eski numarada kalır.
/// </summary>
public sealed class PurgedMemberFollowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PurgedMemberFollowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Iki_partide_birlesen_grupta_silinmis_uye_son_canli_uye_ayrilinca_yeni_numaraya_gecer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        await kit.MemberAsync(g0);
        var pBalanceBefore = (await kit.BalanceRowAsync(p))!.UpdatedAt;

        (await kit.SyncGroupAsync(a1, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1, "g1'de canlı üye (a2) var");

        (await kit.SyncGroupAsync(a2, g0)).EnsureSuccessStatusCode();
        var moved = await kit.ProjectionAsync(p);
        moved.GroupId.Should().Be(g0, "g1'i boşaltan kayıttaki ayrılanların numarası");
        moved.GroupIdChangedAt.Should().NotBeNull("taşıma damgalı (CustomerGroupUnion.StampAt)");
        moved.PurgedAt.Should().NotBeNull("silinmişlik değişmez");
        (await kit.BalanceRowAsync(p))!.UpdatedAt.Should().BeAfter(pBalanceBefore, "taşıma da bir GroupId değişimi");
        (await kit.BalanceAsync(p)).Should().Be(40m);
    }

    [Fact]
    public async Task Ayir_da_silinmis_uye_eski_numarada_tek_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a, null)).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    [Fact]
    public async Task Canli_uye_kaldikca_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a1, Kit.NewGroup())).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    [Fact]
    public async Task Ayrilanlar_ayni_kayitta_farkli_numaralara_giderse_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var stamp = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(HttpMethod.Post, $"/api/v1/licenses/{kit.LicenseId}/wpf-customers/sync", new
        {
            customers = new[]
            {
                await SyncItemAsync(kit, a1, Kit.NewGroup(), stamp),
                await SyncItemAsync(kit, a2, Kit.NewGroup(), stamp),
            },
        });
        resp.EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    private static async Task<object> SyncItemAsync(Kit kit, Guid id, string groupId, DateTimeOffset at)
    {
        var row = await kit.ProjectionAsync(id);
        return new
        {
            id, platform = row.Platform, username = row.Username, updatedAt = at, format = 2,
            groupId, groupIdChangedAt = at,
        };
    }
}
```

- [ ] **Adım 2: Testcontainers 13 (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowRelationalTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// §8.2-13: silinmiş üyenin numara izlemesi gerçek SQL Server'da — senkronla iki partide birleşme, Ayır,
/// <c>group-customers</c> ve <c>--at-twins</c> (iki iş de işlem açar; InMemory'de koşmaz). BIN2 harmanında SQL aday
/// daraltır, eşitlik bellekte ordinal. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PurgedMemberFollowRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PurgedMemberFollowRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Senkronla_iki_partide_birlesmede_silinmis_uye_yeni_numaraya_Ayir_da_eski_numarada()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        // Sondaki boşlukla ayrılan numara başka kişidir: oradaki silinmiş üye taşınmaz (ordinal).
        var pSpaced = await kit.MemberAsync(g1 + " ", 5m, purged: true);

        (await kit.SyncGroupAsync(a1, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
        (await kit.SyncGroupAsync(a2, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
        (await kit.ProjectionAsync(pSpaced)).GroupId.Should().Be(g1 + " ");

        var g2 = Kit.NewGroup();
        var b = await kit.MemberAsync(g2);
        var q = await kit.MemberAsync(g2, 7m, purged: true);
        (await kit.SyncGroupAsync(b, null)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(q)).GroupId.Should().Be(g2, "Ayır: silinmiş üye eski numarada tek kalır (K3 istisnası)");
    }

    [Fact]
    public async Task group_customers_sonrasi_silinmis_uye_hedef_numarada()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var buyer = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var registered = await kit.MemberAsync(g0);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(buyer, registered)], apply: true, default);
            report.FailedComponents.Should().Be(0);
        }

        (await kit.ProjectionAsync(buyer)).GroupId.Should().Be(g0);
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
    }

    [Fact]
    public async Task at_twins_sonrasi_kopyaya_donen_son_canli_uyenin_silinmis_esi_hedef_numarada()
    {
        // g1 = {"@x" (canlı), P (silinmiş)}; g0 = {"x" (canlı), B}. "@x" "x"e birleşir ve kopyaya döner: g1'in son
        // canlı üyesi GroupId değiştirmeden ayrılır (fark 5); hedef asıl kaydın numarası g0.
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var name = $"u{Guid.NewGuid():N}"[..16];
        var twin = await kit.MemberAsync(g1, platform: "instagram", username: "@" + name);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var canonical = await kit.MemberAsync(g0, platform: "instagram", username: name);
        await kit.MemberAsync(g0);

        using (var scope = _factory.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>()
                .RunAsync(kit.LicenseId, apply: true, default, atTwins: true);
            report.FailedGroups.Should().Be(0);
        }

        (await kit.ProjectionAsync(twin)).MergedIntoId.Should().Be(canonical);
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
    }
}
```

- [ ] **Adım 3: Koştur — FAIL bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PurgedMemberFollow"`
Expected: taşınma bekleyen testler FAIL (silinmiş üye `g1`'de kalıyor); "yerinde kalır" testleri PASS.

- [ ] **Adım 4: Numara izlemesini kancaya ekle**

`PersonGroupSaveHook.cs` — sınıf yorumunun listesine 2. maddeyi ekle:

```csharp
///   <item><b>Silinmiş üyenin numara izlemesi (K3, M11):</b> canlı asıl bir üye g'den ayrıldıysa (GroupId'si ordinal
///   değişti → hedef yeni numara; ya da kopyaya döndü → hedef asıl kaydın numarası) ve bu kayıttan sonra g'de canlı
///   asıl üye kalmadıysa (veritabanı + izlenen değişiklikler, ordinal), g'deki silinmiş asıl üyeler hedefe taşınır,
///   damgalı (<see cref="CustomerGroupUnion.StampAt"/>). Hedef boşsa (Ayır) silinmiş üye g'de tek kalır (K3'ün
///   istisnası, sahip kararı bekliyor); ayrılanlar farklı numaralara gittiyse de kalır ve günlüğe yazılır
///   (verify'ın "grubu dağılmış silinmiş satır" kalemi görür). KVKK silmesi ayrılma değildir. Taşıma da bir GroupId
///   değişimidir — silinmiş üyenin bakiye jetonu 1. maddeyle ilerler; bu yüzden önce bu madde koşar.</item>
```

`RunAsync`:

```csharp
    internal static async Task RunAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        if (!db.ChangeTracker.AutoDetectChangesEnabled) return; // salt okunur bağlam (LicenseReadOnlyDbContext)
        await FollowPurgedMembersAsync(db, sync, ct);
        await TouchBalancesAsync(db, sync, ct);
    }
```

Yeni üyeler (başa `using Microsoft.EntityFrameworkCore.Infrastructure;`, `using Microsoft.Extensions.DependencyInjection;`, `using Microsoft.Extensions.Logging.Abstractions;`):

```csharp
    /// <summary>Bu kayıtta canlı asıl bir üyenin <see cref="From"/> numarasından ayrılışı; <see cref="To"/> = gittiği
    /// numara (gruplu değilse null).</summary>
    private sealed record Departure(Guid LicenseId, string From, string? To);

    private static async Task FollowPurgedMembersAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        var departures = new List<Departure>();
        foreach (var entry in db.ChangeTracker.Entries<WpfCustomerProjection>()
                     .Where(e => e.State == EntityState.Modified).ToList())
        {
            var from = entry.Property(p => p.GroupId).OriginalValue;
            var wasLive = entry.Property(p => p.MergedIntoId).OriginalValue is null
                          && entry.Property(p => p.PurgedAt).OriginalValue is null;
            if (!wasLive || !PersonMembership.IsGrouped(from)) continue;

            var row = entry.Entity;
            if (row.PurgedAt is not null) continue; // KVKK silmesi: satır numarasında silinmiş üye olarak kalır
            if (row.MergedIntoId is { } canonicalId)
                departures.Add(new Departure(row.LicenseId, from!, await GroupOfAsync(db, row.LicenseId, canonicalId, sync, ct)));
            else if (!string.Equals(row.GroupId, from, StringComparison.Ordinal))
                departures.Add(new Departure(row.LicenseId, from!, row.GroupId));
        }
        if (departures.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var origin in departures.GroupBy(d => (d.LicenseId, d.From)))
        {
            var (licenseId, group) = origin.Key;
            var members = await MembersAfterSaveAsync(db, licenseId, group, sync, ct);
            if (members.Any(p => p.PurgedAt is null)) continue; // numarada canlı üye kaldı
            var purged = members.Where(p => p.PurgedAt is not null).ToList();
            if (purged.Count == 0) continue;

            var targets = origin.Select(d => PersonMembership.IsGrouped(d.To) ? d.To : null).ToList();
            var target = targets[0];
            if (targets.Any(t => !string.Equals(t, target, StringComparison.Ordinal)))
            {
                Logger(db).LogWarning(
                    "Ortak bakiye: numarası boşalan grubun ayrılanları farklı numaralara gitti; {Count} silinmiş üye eski numarada kaldı (lisans {LicenseId})",
                    purged.Count, licenseId);
                continue;
            }
            if (target is null) continue; // Ayır: silinmiş üye eski numarada tek kalır (K3 istisnası, §10)

            foreach (var p in purged)
            {
                p.GroupId = target;
                p.GroupIdChangedAt = CustomerGroupUnion.StampAt(now, p.GroupIdChangedAt);
            }
        }
    }

    /// <summary>
    /// Kayıttan SONRA numarası <paramref name="group"/> olacak asıl kayıtlar: SQL aday daraltır (sondaki boşluğu yutar),
    /// izlenen örnekler kimlik çözümüyle bellekteki (kaydedilecek) değerleriyle döner; veritabanında başka numarada
    /// olup bu kayıtta g'ye gelen izlenen satırlar da eklenir. Eşitlik ordinal; kopyaya dönen satır süzülür.
    /// </summary>
    private static async Task<List<WpfCustomerProjection>> MembersAfterSaveAsync(
        LicenseDbContext db, Guid licenseId, string group, bool sync, CancellationToken ct)
    {
        var query = db.WpfCustomerProjections.Where(p => p.LicenseId == licenseId && p.GroupId == group);
        var fromDb = sync ? query.ToList() : await query.ToListAsync(ct);
        return fromDb
            .Concat(db.ChangeTracker.Entries<WpfCustomerProjection>()
                .Where(e => e.State != EntityState.Deleted)
                .Select(e => e.Entity))
            .Distinct()
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId is null
                        && string.Equals(p.GroupId, group, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Kopyanın asıl kaydının bu kayıttaki numarası: izleniyorsa bellekteki, değilse veritabanındaki.</summary>
    private static async Task<string?> GroupOfAsync(
        LicenseDbContext db, Guid licenseId, Guid id, bool sync, CancellationToken ct)
    {
        var tracked = db.WpfCustomerProjections.Local.FirstOrDefault(p => p.Id == id);
        if (tracked is not null) return tracked.GroupId;
        var query = db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.Id == id)
            .Select(p => p.GroupId);
        return sync ? query.FirstOrDefault() : await query.FirstOrDefaultAsync(ct);
    }

    private static ILogger Logger(LicenseDbContext db) =>
        (db.GetInfrastructure().GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger(typeof(PersonGroupSaveHook).FullName!);
```

- [ ] **Adım 5: Koştur — PASS + iş/senkron regresyonu**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PurgedMemberFollow|FullyQualifiedName~PersonGroupHook|FullyQualifiedName~CustomerGroupingJob|FullyQualifiedName~CustomerIdentityMergeJob|FullyQualifiedName~LicensesWpfCustomersSync|FullyQualifiedName~ShopperPurgeService|FullyQualifiedName~WpfCustomer"`
Expected: hepsi PASS. Var olan bir iş/senkron testi, boşalan gruptaki silinmiş satırın numarasının DEĞİŞMEMESİNİ sabitliyorsa (ör. `ChangeSeq` eşitliği), bu spec §4.11/§4.12'nin istediği yeni davranıştır: iddiayı yeni kurala göre güncelle ve commit gövdesinde testin adını ve gerekçesini yaz. Başka nedenle kırmızıysa kural yanlış uygulanmıştır — düzelt, testi değiştirme.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonGroupSaveHook.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowTests.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PurgedMemberFollowRelationalTests.cs
git commit -F - <<'MSG'
feat(balance): silinmiş üye, numarası boşalan grubun yeni numarasını izler

Canlı asıl üye numarasından ayrılınca (GroupId değişimi ya da kopyaya
dönüş) ve numarada canlı üye kalmayınca, oradaki silinmiş üyeler ayrılanların
numarasına damgalı taşınır; Ayır'da ve dağılan ayrılışta yerinde kalır
(ortak bakiye §4.11, §4.12, K3, M11). Senkron, group-customers ve
--at-twins için gerçek SQL Server testi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 11: `PersonBalanceVerifier` — §7.3 maddeleri 1–10 ve §8.1 sapma kuralları

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI, Görev 3'ün `PersonBalance` kaydının altına)
- Test: `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifierTests.cs`

Denetim iki parçadır: `LoadAsync` lisansların anlık görüntüsünü (yalnız Id'ler, tutarlar, numaralar; ad/telefon yok) okur, `Analyze` saf bir fonksiyondur — CLI (Görev 12) ve günlük iş (Görev 13) aynısını çağırır. Taban işareti ve "tabanı var" kuralı: fark 7. D3 uyarısı S2'de (fark 8); 6. madde yalnız sayı. 4. maddenin "grubunda canlı asıl üye kalmamış" kalemi de yalnız sayı: uyarıya çevrilmesi spec §10 açık kalanlarda (numara izlemesinin hakemi, §5.5; Görev 10 inceleme eki 2) — sahip karar vermeden ekleme.

- [ ] **Adım 1: Saf analiz testleri (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifierTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using V = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifier;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalanceVerifier.Analyze"/> — spec 2026-10-11 §7.3 (rapor maddeleri 1–10), §3.3 (D1, D4, D5, D6),
/// §8.1 (yalnız tabana göre YENİ sapma uyarır; taban yokken eksi/D1/kopya yalnız sayılır).
/// </summary>
public sealed class PersonBalanceVerifierTests
{
    private static readonly Guid Lic = Guid.NewGuid();
    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static V.Snapshot Snap(
        IEnumerable<V.ProjectionRow>? projections = null, IEnumerable<V.BalanceRow>? balances = null,
        IEnumerable<V.LedgerRow>? ledger = null, IEnumerable<V.DrawRow>? draws = null,
        IEnumerable<Guid>? reversed = null, IEnumerable<V.BaselineRow>? baselines = null,
        IEnumerable<V.ActivationRow>? pcs = null, DateTimeOffset? issued = null) =>
        new([new V.LicenseRow(Lic, issued ?? Issued)],
            (projections ?? Array.Empty<V.ProjectionRow>()).ToList(),
            (balances ?? Array.Empty<V.BalanceRow>()).ToList(),
            (ledger ?? Array.Empty<V.LedgerRow>()).ToList(),
            (draws ?? Array.Empty<V.DrawRow>()).ToList(),
            (reversed ?? Array.Empty<Guid>()).ToHashSet(),
            (baselines ?? Array.Empty<V.BaselineRow>()).ToList(),
            (pcs ?? Array.Empty<V.ActivationRow>()).ToList(),
            ProjectionCount: 0, TransactionCount: 0);

    private static V.LicenseReport Single(V.Snapshot s) => V.Analyze(s).Licenses.Should().ContainSingle().Subject;

    private static V.ProjectionRow P(Guid id, string? group = null, bool purged = false, Guid? mergedInto = null) =>
        new(id, Lic, group, mergedInto, purged);

    private static V.BalanceRow B(Guid id, decimal balance) => new(Lic, id, balance);

    private static V.LedgerRow L(Guid id, decimal sum, int count = 1) => new(Lic, id, sum, count);

    private static V.BaselineRow Marker(DateTimeOffset? at = null, Guid? license = null) =>
        new(license ?? Lic, V.LicenseMarker, 0m, 0m, at ?? Captured, null);

    /// <summary>Örnek 1'in grubu: K (T, 0, Original 120), L1 (I, −100), L2 (Y, −20).</summary>
    private static (Guid K, Guid L1, Guid L2, List<V.DrawRow> Rows) Example1Draw(
        string legKind = "purchase-deduction", decimal original = 120m)
    {
        var (t, i, y) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (k, l1, l2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = new List<V.DrawRow>
        {
            new(k, Lic, t, 0m, "purchase-deduction", k, original, "session:42"),
            new(l1, Lic, i, -100m, legKind, k, null, null),
            new(l2, Lic, y, -20m, "purchase-deduction", k, null, null),
        };
        return (k, l1, l2, rows);
    }

    [Fact]
    public void Tutarli_cekim_grubunda_kural_bozulmasi_yok()
    {
        var (_, _, _, rows) = Example1Draw();

        var r = Single(Snap(draws: rows));

        (r.D4, r.D5, r.D6, r.OrphanLegs).Should().Be((0, 0, 0, 0));
        r.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void D4_bacagin_turu_capadan_farkli()
    {
        var (k, _, _, rows) = Example1Draw(legKind: "manual-adjustment");

        var r = Single(Snap(draws: rows));

        r.D4.Should().Be(1);
        r.Details.RuleBreakingGroups.Should().Equal(k);
        r.Alerts.Should().ContainSingle().Which.Should().StartWith("kural:");
    }

    [Fact]
    public void D5_bacaklarin_bir_kismi_geri_alinmis_hepsi_alinmissa_bozulma_yok()
    {
        var (k, l1, l2, rows) = Example1Draw();

        Single(Snap(draws: rows, reversed: [l1])).D5.Should().Be(1);
        Single(Snap(draws: rows, reversed: [k, l1, l2])).D5.Should().Be(0);
    }

    [Fact]
    public void D6_bacaklarin_toplami_OriginalAmounti_asamaz()
    {
        var (_, _, _, rows) = Example1Draw(original: 100m);

        Single(Snap(draws: rows)).D6.Should().Be(1, "−Σ = 120 > 100");
    }

    [Fact]
    public void Capasi_olmayan_bacak_yetimdir()
    {
        var leg = new V.DrawRow(Guid.NewGuid(), Lic, Guid.NewGuid(), -5m, "purchase-deduction", Guid.NewGuid(), null, null);

        var r = Single(Snap(draws: [leg]));

        r.OrphanLegs.Should().Be(1);
        r.Details.OrphanLegs.Should().Equal(leg.Id);
        r.Alerts.Should().ContainSingle().Which.Should().Contain("yetim=1");
    }

    [Fact]
    public void Taban_hic_alinmamisken_eksi_D1_ve_kopya_yalniz_sayilir_uyarmaz()
    {
        var (neg, broken, copy, head) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var r = Single(Snap(
            projections: [P(neg), P(broken), P(head), P(copy, mergedInto: head)],
            balances: [B(neg, -10m), B(broken, 50m)],
            ledger: [L(neg, -10m), L(broken, 40m), L(copy, 0m, count: 2)]));

        r.HasBaseline.Should().BeFalse();
        r.NegativeRows.Should().Be(new V.Tally(0, 1, -10m));
        r.LedgerMismatches.Should().Be(new V.Tally(0, 1, 10m));
        r.CopyRows.Outside.Should().Be(1);
        r.CopyRowsWithTransactions.Should().Be(1);
        r.Alerts.Should().BeEmpty("§8.1: taban alınana kadar yalnız sayı");
    }

    [Fact]
    public void Isaretli_lisansta_taban_disi_ve_daha_da_eksiye_inen_satir_uyarir_ayni_kalan_uyarmaz()
    {
        var (old, fresh) = (Guid.NewGuid(), Guid.NewGuid());
        V.BaselineRow Base(decimal balance) => new(Lic, old, balance, balance, Captured, Captured);

        var same = Single(Snap(projections: [P(old)], balances: [B(old, -10m)], ledger: [L(old, -10m)],
            baselines: [Marker(), Base(-10m)]));
        same.Alerts.Should().BeEmpty();
        same.NegativeRows.Should().Be(new V.Tally(1, 0, -10m));

        var worse = Single(Snap(projections: [P(old)], balances: [B(old, -15m)], ledger: [L(old, -15m)],
            baselines: [Marker(), Base(-10m)]));
        worse.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 0, daha da eksiye inen 1");

        var outside = Single(Snap(projections: [P(old), P(fresh)], balances: [B(old, -10m), B(fresh, -5m)],
            ledger: [L(old, -10m), L(fresh, -5m)], baselines: [Marker(), Base(-10m)]));
        outside.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 1, daha da eksiye inen 0");
        outside.Details.NegativeOutside.Should().Equal(fresh);
    }

    [Fact]
    public void Tabandaki_D1_farki_ayni_kalirsa_uyarmaz_degisirse_uyarir()
    {
        var row = Guid.NewGuid();
        var baseline = new V.BaselineRow(Lic, row, 100m, 90m, Captured, null);

        Single(Snap(projections: [P(row)], balances: [B(row, 70m)], ledger: [L(row, 60m)],
            baselines: [Marker(), baseline])).Alerts.Should().BeEmpty("fark 10 korunuyor");
        Single(Snap(projections: [P(row)], balances: [B(row, 70m)], ledger: [L(row, 70m)],
            baselines: [Marker(), baseline])).Alerts.Should().ContainSingle()
            .Which.Should().Be("D1: taban dışı 0, farkı değişen 1");
    }

    [Fact]
    public void Ilk_yakalamadan_sonra_verilen_lisans_isaretsiz_de_tabanli_sayilir()
    {
        var neg = Guid.NewGuid();

        var r = Single(Snap(projections: [P(neg)], balances: [B(neg, -3m)], ledger: [L(neg, -3m)],
            baselines: [Marker(Captured, license: Guid.NewGuid())], issued: Captured.AddDays(1)));

        r.HasMarker.Should().BeFalse();
        r.HasBaseline.Should().BeTrue("yeni lisansın S1 öncesi verisi olamaz");
        r.Alerts.Should().ContainSingle().Which.Should().StartWith("eksi:");
    }

    [Fact]
    public void Kisi_kalemleri_yayilmis_havuz_silinmis_kirpma_eksi_kisi_ve_surumler()
    {
        var g = $"g{Guid.NewGuid():N}";
        var orphanGroup = $"g{Guid.NewGuid():N}";
        var (a, b, purged, stranded, spaced, tabbed, neg) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var r = Single(Snap(
            projections:
            [
                P(a, g), P(b, g), P(purged, g, purged: true), P(stranded, orphanGroup, purged: true),
                P(spaced, "x "), P(tabbed, "y\tz"), P(neg, "   "),
            ],
            balances: [B(a, 30m), B(b, 20m), B(purged, 5m), B(stranded, 7m), B(neg, -2m)],
            ledger: [L(a, 30m), L(b, 20m), L(purged, 5m), L(stranded, 7m), L(neg, -2m)],
            pcs: [new(Lic, "0.9.11"), new(Lic, "0.9.11"), new(Lic, null)]));

        (r.SpreadPersons, r.SpreadPoolTotal).Should().Be((1, 55m), "g'de üç üyenin bakiyesi var");
        (r.PurgedWithBalance, r.PurgedBalanceTotal).Should().Be((2, 12m));
        (r.StrandedPurged, r.StrandedPurgedTotal).Should().Be((1, 7m));
        r.Details.StrandedPurged.Should().Equal(stranded);
        (r.TrimMismatches, r.ControlCharGroups).Should().Be((1, 1), "yalnız boşluk numara grupsuz sayılır");
        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0));
        r.ActivePcVersions.Should().BeEquivalentTo(new Dictionary<string, int> { ["0.9.11"] = 2, ["yok"] = 1 });
    }

    [Fact]
    public void Yakalanacak_taban_satirlari_tekildir_ve_isaret_eklenir()
    {
        var row = Guid.NewGuid();
        var s = Snap(projections: [P(row)], balances: [B(row, -10m)], ledger: [L(row, -8m)]);
        var r = Single(s);

        var rows = V.BaselineRowsToCapture(s, r, Captured);

        rows.Should().HaveCount(2, "eksi ve D1 aynı satır — bir kez; artı işaret");
        rows.Should().ContainSingle(x => x.WpfCustomerId == row)
            .Which.Should().BeEquivalentTo(new { Balance = -10m, LedgerSum = -8m, CapturedAt = Captured, ReviewedAt = (DateTimeOffset?)null });
        rows.Should().ContainSingle(x => x.WpfCustomerId == V.LicenseMarker);
    }
}

/// <summary><see cref="PersonBalanceVerifier.LoadAsync"/> InMemory'de: anlık görüntü lisansın gerçek satırlarından.</summary>
public sealed class PersonBalanceVerifierLoadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceVerifierLoadTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Lisansin_verisinden_rapor_kurulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var negative = await kit.MemberAsync(null, -10m);
        var stranded = await kit.MemberAsync(Kit.NewGroup(), 7m, purged: true);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: ex.T);
        await kit.Sales.WithDbAsync(db =>
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = copy, Balance = 3m,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAt = DateTimeOffset.UtcNow, AppVersion = "0.9.11",
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-30), LastSeenAt = DateTimeOffset.UtcNow.AddDays(-20),
                AppVersion = "0.9.9",
            });
            return Task.CompletedTask;
        });

        using var scope = _factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>();
        var report = V.Analyze(await verifier.LoadAsync(kit.LicenseId, DateTimeOffset.UtcNow, default));

        var r = report.Licenses.Should().ContainSingle().Subject;
        r.LicenseId.Should().Be(kit.LicenseId);
        (r.D4, r.D5, r.D6, r.OrphanLegs).Should().Be((0, 0, 0, 0), "Örnek 1 tutarlı");
        r.SpreadPersons.Should().Be(0, "Örnek 1'in sonunda yalnız Y'de para var");
        r.NegativeRows.Should().Be(new V.Tally(0, 1, -10m));
        r.Details.NegativeOutside.Should().Equal(negative);
        r.LedgerMismatches.Outside.Should().Be(1, "kopyanın 3'ü defterde yok");
        (r.StrandedPurged, r.StrandedPurgedTotal).Should().Be((1, 7m));
        r.Details.StrandedPurged.Should().Equal(stranded);
        (r.CopyRowsWithBalance, r.CopyRows.Outside).Should().Be((1, 1));
        r.ActivePcVersions.Should().BeEquivalentTo(new Dictionary<string, int> { ["0.9.11"] = 1 });
        report.ProjectionCount.Should().BeGreaterThan(0);
        report.TransactionCount.Should().BeGreaterThan(0);
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceVerifier"`
Expected: FAIL — `PersonBalanceVerifier` yok.

- [ ] **Adım 3: `PersonBalanceVerifier`**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiye denetimi (spec 2026-10-11 §7.3, §8.1) — <c>person-balance verify</c> ve günlük
/// <c>person-balance-verify</c> işinin ortak çekirdeği. <see cref="LoadAsync"/> salt okunur bir anlık görüntü alır
/// (Id'ler, tutarlar, numaralar; ad ve telefon YOK), <see cref="Analyze"/> saftır.
///
/// <para><b>Rapor:</b> 1 davranışı değişecek kişiler (≥ 2 üyesinde sıfır olmayan bakiye), 2 eksi satırlar, 3 D1
/// bozulmaları (<c>Balance ≠ Σ Amount</c>), 4 silinmiş üyede bakiye (ve grubunda canlı üye kalmamışlar), 5 kırpma
/// farkı (ham ↔ kırpılmış numara; sekme/satır sonu), 6 havuzu &lt; 0 kişi, 7 D4/D5/D6/yetim bacak, 8 kopya satırlar,
/// 9 aktif PC sürümleri, 10 iki tablonun satır sayısı. 2, 3 ve 8 taban içi/dışı ayrılır.</para>
///
/// <para><b>Uyarı (§8.1) yalnız YENİ sapmada:</b> D4/D5/D6/yetim ≠ 0 (her zaman); lisansın tabanı varken taban dışı
/// eksi satır ya da tabandaki eksi satırın daha da eksiye inmesi, taban dışı D1 ya da tabandaki satırın
/// <c>Balance − LedgerSum</c> farkının değişmesi, taban dışı kopya satır. Havuz kipindeki D3 uyarısı S2'de (S1 kip
/// bayrağını okumaz). <b>Tabanı var</b> ⇔ lisansın işaret satırı (<see cref="LicenseMarker"/>) var ya da lisans ilk
/// yakalamadan SONRA verilmiş (S1 öncesi verisi olamaz).</para>
/// </summary>
public sealed class PersonBalanceVerifier
{
    /// <summary>Aktif PC penceresi: satış senkronu kapısıyla aynı ölçüt (14 gün). Ayarı S2'nin <c>enable</c>'ı getirir.</summary>
    public static readonly TimeSpan ActivePcWindow = SalesModeGate.RecentWindow;

    /// <summary>Taban tablosunda lisansın "taban alındı" işareti.</summary>
    public static readonly Guid LicenseMarker = Guid.Empty;

    private readonly LicenseDbContext _db;

    public PersonBalanceVerifier(LicenseDbContext db) => _db = db;

    // ── Anlık görüntü ────────────────────────────────────────────────────────

    public sealed record LicenseRow(Guid Id, DateTimeOffset IssuedAt);
    public sealed record ProjectionRow(Guid Id, Guid LicenseId, string? GroupId, Guid? MergedIntoId, bool Purged);
    public sealed record BalanceRow(Guid LicenseId, Guid WpfCustomerId, decimal Balance);
    public sealed record LedgerRow(Guid LicenseId, Guid WpfCustomerId, decimal Sum, int Count);
    public sealed record DrawRow(
        Guid Id, Guid LicenseId, Guid WpfCustomerId, decimal Amount, string Kind, Guid DrawGroupId,
        decimal? OriginalAmount, string? SaleScope);
    public sealed record BaselineRow(
        Guid LicenseId, Guid WpfCustomerId, decimal Balance, decimal LedgerSum, DateTimeOffset CapturedAt,
        DateTimeOffset? ReviewedAt);
    public sealed record ActivationRow(Guid LicenseId, string? AppVersion);

    /// <param name="ReversedIds">Bir çekim grubu satırını geri alan satırların hedefleri (D5).</param>
    /// <param name="Baselines">BÜTÜN lisansların taban satırları (ilk yakalama anı için).</param>
    public sealed record Snapshot(
        IReadOnlyList<LicenseRow> Licenses,
        IReadOnlyList<ProjectionRow> Projections,
        IReadOnlyList<BalanceRow> Balances,
        IReadOnlyList<LedgerRow> Ledger,
        IReadOnlyList<DrawRow> DrawRows,
        IReadOnlySet<Guid> ReversedIds,
        IReadOnlyList<BaselineRow> Baselines,
        IReadOnlyList<ActivationRow> ActivePcs,
        long ProjectionCount,
        long TransactionCount);

    /// <param name="licenseId">null = verisi (projeksiyon, bakiye ya da hareket) olan bütün lisanslar.</param>
    public async Task<Snapshot> LoadAsync(Guid? licenseId, DateTimeOffset now, CancellationToken ct)
    {
        var licenses = licenseId is { } only
            ? await _db.Licenses.AsNoTracking().Where(l => l.Id == only)
                .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct)
            : await _db.Licenses.AsNoTracking()
                .Where(l => _db.WpfCustomerProjections.IgnoreQueryFilters().Any(p => p.LicenseId == l.Id)
                            || _db.CustomerBalances.Any(b => b.LicenseId == l.Id)
                            || _db.CustomerBalanceTransactions.Any(t => t.LicenseId == l.Id))
                .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct);
        var ids = licenses.Select(l => l.Id).ToList();

        var projections = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => ids.Contains(p.LicenseId))
            .Select(p => new ProjectionRow(p.Id, p.LicenseId, p.GroupId, p.MergedIntoId, p.PurgedAt != null))
            .ToListAsync(ct);
        var balances = await _db.CustomerBalances.AsNoTracking()
            .Where(b => ids.Contains(b.LicenseId))
            .Select(b => new BalanceRow(b.LicenseId, b.WpfCustomerId, b.Balance))
            .ToListAsync(ct);
        var ledger = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => ids.Contains(t.LicenseId))
            .GroupBy(t => new { t.LicenseId, t.WpfCustomerId })
            .Select(g => new LedgerRow(g.Key.LicenseId, g.Key.WpfCustomerId, g.Sum(t => t.Amount), g.Count()))
            .ToListAsync(ct);
        var drawRows = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => ids.Contains(t.LicenseId) && t.DrawGroupId != null)
            .Select(t => new DrawRow(t.Id, t.LicenseId, t.WpfCustomerId, t.Amount, t.Kind, t.DrawGroupId!.Value,
                t.OriginalAmount, t.SaleScope))
            .ToListAsync(ct);
        var reversed = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(r => r.ReversesTransactionId != null
                        && _db.CustomerBalanceTransactions.Any(l => l.Id == r.ReversesTransactionId
                                                                    && l.DrawGroupId != null
                                                                    && ids.Contains(l.LicenseId)))
            .Select(r => r.ReversesTransactionId!.Value)
            .ToListAsync(ct);
        var baselines = await _db.PersonBalanceBaselines.AsNoTracking()
            .Select(b => new BaselineRow(b.LicenseId, b.WpfCustomerId, b.Balance, b.LedgerSum, b.CapturedAt, b.ReviewedAt))
            .ToListAsync(ct);
        var since = now - ActivePcWindow;
        var pcs = await _db.Activations.AsNoTracking()
            .Where(a => ids.Contains(a.LicenseId) && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .Select(a => new ActivationRow(a.LicenseId, a.AppVersion))
            .ToListAsync(ct);
        var projectionCount = await _db.WpfCustomerProjections.IgnoreQueryFilters().LongCountAsync(ct);
        var transactionCount = await _db.CustomerBalanceTransactions.LongCountAsync(ct);

        return new Snapshot(licenses, projections, balances, ledger, drawRows, reversed.ToHashSet(), baselines, pcs,
            projectionCount, transactionCount);
    }

    // ── Rapor ────────────────────────────────────────────────────────────────

    /// <summary>Taban içi / dışı sayı ve tutar toplamı.</summary>
    public readonly record struct Tally(int InBaseline, int Outside, decimal Total);

    /// <summary><c>--details</c>'in GUID listeleri (ad/telefon yok).</summary>
    public sealed record Details(
        IReadOnlyList<Guid> NegativeOutside, IReadOnlyList<Guid> NegativeInBaseline,
        IReadOnlyList<Guid> LedgerMismatchOutside, IReadOnlyList<Guid> LedgerMismatchInBaseline,
        IReadOnlyList<Guid> CopyOutside, IReadOnlyList<Guid> StrandedPurged,
        IReadOnlyList<Guid> RuleBreakingGroups, IReadOnlyList<Guid> OrphanLegs,
        IReadOnlyList<Guid> WorsenedBaselineRows, IReadOnlyList<Guid> ChangedMismatchRows);

    public sealed record LicenseReport(
        Guid LicenseId,
        bool HasMarker,
        bool HasBaseline,
        int SpreadPersons, decimal SpreadPoolTotal,
        Tally NegativeRows,
        Tally LedgerMismatches,
        int PurgedWithBalance, decimal PurgedBalanceTotal,
        int StrandedPurged, decimal StrandedPurgedTotal,
        int TrimMismatches, int ControlCharGroups,
        int NegativePersons, int NegativePersonsExplained,
        int D4, int D5, int D6, int OrphanLegs,
        Tally CopyRows, int CopyRowsWithTransactions, int CopyRowsWithBalance,
        IReadOnlyDictionary<string, int> ActivePcVersions,
        IReadOnlyList<string> Alerts,
        Details Details);

    public sealed record Report(IReadOnlyList<LicenseReport> Licenses, long ProjectionCount, long TransactionCount);

    public static Report Analyze(Snapshot s)
    {
        var firstCapture = s.Baselines
            .Where(b => b.WpfCustomerId == LicenseMarker)
            .Select(b => (DateTimeOffset?)b.CapturedAt)
            .Min();
        var reports = s.Licenses.OrderBy(l => l.Id).Select(l => AnalyzeLicense(s, l, firstCapture)).ToList();
        return new Report(reports, s.ProjectionCount, s.TransactionCount);
    }

    private static LicenseReport AnalyzeLicense(Snapshot s, LicenseRow license, DateTimeOffset? firstCapture)
    {
        var id = license.Id;
        var projections = s.Projections.Where(p => p.LicenseId == id).ToDictionary(p => p.Id);
        var balances = s.Balances.Where(b => b.LicenseId == id).ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = s.Ledger.Where(l => l.LicenseId == id).ToDictionary(l => l.WpfCustomerId);
        var baselines = s.Baselines.Where(b => b.LicenseId == id && b.WpfCustomerId != LicenseMarker)
            .ToDictionary(b => b.WpfCustomerId);
        var hasMarker = s.Baselines.Any(b => b.LicenseId == id && b.WpfCustomerId == LicenseMarker);
        var hasBaseline = hasMarker || (firstCapture is { } first && license.IssuedAt > first);

        decimal Bal(Guid row) => balances.GetValueOrDefault(row);
        decimal Sum(Guid row) => ledger.TryGetValue(row, out var l) ? l.Sum : 0m;
        bool IsCopy(Guid row) => projections.TryGetValue(row, out var p) && p.MergedIntoId is not null;
        bool Worsened(Guid row) => baselines.TryGetValue(row, out var b) && Bal(row) < b.Balance;

        var rows = balances.Keys.Union(ledger.Keys).ToList();

        // 2. Eksi satırlar (kopyalar 8'de).
        var negative = rows.Where(r => !IsCopy(r) && Bal(r) < 0m).ToList();
        var (negIn, negOut) = Split(negative, baselines);
        // 3. D1.
        var mismatch = rows.Where(r => Bal(r) != Sum(r)).ToList();
        var (misIn, misOut) = Split(mismatch, baselines);
        // 8. Kopya satırlar: kopyaya bağlı hareket ya da sıfır olmayan bakiye.
        var copyTx = rows.Where(r => IsCopy(r) && ledger.TryGetValue(r, out var l) && l.Count > 0).ToList();
        var copyBal = rows.Where(r => IsCopy(r) && Bal(r) != 0m).ToList();
        var copies = copyTx.Union(copyBal).ToList();
        var (copyIn, copyOut) = Split(copies, baselines);

        // Kişiler: asıl kayıtlar, numara ordinal (PersonMembership kuralı); gruplanmamış satır tek başına kişi.
        var canonical = projections.Values.Where(p => p.MergedIntoId is null).ToList();
        var persons = canonical
            .GroupBy(p => PersonMembership.IsGrouped(p.GroupId) ? "g:" + p.GroupId : "c:" + p.Id.ToString("N"),
                StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();
        // 1.
        var spread = persons.Where(m => m.Count(p => Bal(p.Id) != 0m) >= 2).ToList();
        // 4.
        var purgedWithBalance = canonical.Where(p => p.Purged && Bal(p.Id) != 0m).ToList();
        var liveGroups = canonical.Where(p => !p.Purged && PersonMembership.IsGrouped(p.GroupId))
            .Select(p => p.GroupId!).ToHashSet(StringComparer.Ordinal);
        var stranded = purgedWithBalance
            .Where(p => PersonMembership.IsGrouped(p.GroupId) && !liveGroups.Contains(p.GroupId!)).ToList();
        // 5. Ham ↔ kırpılmış (gruplama işlerinin KeyOf'u .Trim() ile, WPF ham karşılaştırır).
        var trim = canonical.Count(p => !string.IsNullOrWhiteSpace(p.GroupId) && p.GroupId != p.GroupId.Trim());
        var control = canonical.Count(p => p.GroupId is { } g && g.IndexOfAny(['\t', '\n', '\r']) >= 0);
        // 6.
        var negativePersons = persons.Where(m => m.Sum(p => Bal(p.Id)) < 0m).ToList();
        var explained = negativePersons.Count(m => m.Where(p => Bal(p.Id) < 0m)
            .All(p => baselines.ContainsKey(p.Id) && !Worsened(p.Id)));
        // 7.
        var (d4, d5, d6, ruleGroups, orphans) = CheckDraws(s, id);
        // 9.
        var versions = s.ActivePcs.Where(a => a.LicenseId == id)
            .GroupBy(a => a.AppVersion ?? "yok", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // §8.1: yalnız yeni sapma.
        var worsened = negIn.Where(Worsened).ToList();
        var changedMismatch = baselines.Values
            .Where(b => Bal(b.WpfCustomerId) - Sum(b.WpfCustomerId) != b.Balance - b.LedgerSum)
            .Select(b => b.WpfCustomerId).ToList();
        var alerts = new List<string>();
        if (d4 + d5 + d6 + orphans.Count > 0)
            alerts.Add($"kural: D4={d4} D5={d5} D6={d6} yetim={orphans.Count}");
        if (hasBaseline)
        {
            if (negOut.Count + worsened.Count > 0)
                alerts.Add($"eksi: taban dışı {negOut.Count}, daha da eksiye inen {worsened.Count}");
            if (misOut.Count + changedMismatch.Count > 0)
                alerts.Add($"D1: taban dışı {misOut.Count}, farkı değişen {changedMismatch.Count}");
            if (copyOut.Count > 0)
                alerts.Add($"kopya: taban dışı {copyOut.Count}");
        }

        return new LicenseReport(
            id, hasMarker, hasBaseline,
            spread.Count, spread.Sum(m => m.Sum(p => Bal(p.Id))),
            new Tally(negIn.Count, negOut.Count, negative.Sum(Bal)),
            new Tally(misIn.Count, misOut.Count, mismatch.Sum(r => Bal(r) - Sum(r))),
            purgedWithBalance.Count, purgedWithBalance.Sum(p => Bal(p.Id)),
            stranded.Count, stranded.Sum(p => Bal(p.Id)),
            trim, control,
            negativePersons.Count, explained,
            d4, d5, d6, orphans.Count,
            new Tally(copyIn.Count, copyOut.Count, copies.Sum(Bal)), copyTx.Count, copyBal.Count,
            versions,
            alerts,
            new Details(negOut, negIn, misOut, misIn, copyOut, stranded.Select(p => p.Id).ToList(),
                ruleGroups, orphans, worsened, changedMismatch));
    }

    /// <summary>D4 (bacak türü ve boş alanları), D5 (ya hepsi ya hiçbiri geri alınmış), D6 (−Σ ≤ OriginalAmount),
    /// yetim bacak — yalnız <c>DrawGroupId</c> dolu gruplar. Çekim grubu ⇔ toplamı &lt; 0 (grup geri almaları &gt; 0).</summary>
    private static (int D4, int D5, int D6, List<Guid> RuleGroups, List<Guid> OrphanLegs) CheckDraws(Snapshot s, Guid licenseId)
    {
        int d4 = 0, d5 = 0, d6 = 0;
        var ruleGroups = new List<Guid>();
        var orphans = new List<Guid>();
        foreach (var group in s.DrawRows.Where(r => r.LicenseId == licenseId).GroupBy(r => r.DrawGroupId))
        {
            var anchor = group.FirstOrDefault(r => r.Id == group.Key);
            if (anchor is null)
            {
                orphans.AddRange(group.Select(r => r.Id));
                continue;
            }

            var broken = false;
            if (group.Where(r => r.Id != group.Key)
                .Any(l => l.Kind != anchor.Kind || l.SaleScope is not null || l.OriginalAmount is not null))
            {
                d4++;
                broken = true;
            }
            var sum = group.Sum(r => r.Amount);
            if (sum < 0m)
            {
                var reversedLegs = group.Count(r => s.ReversedIds.Contains(r.Id));
                if (reversedLegs != 0 && reversedLegs != group.Count())
                {
                    d5++;
                    broken = true;
                }
                if (anchor.Kind == PersonBalance.KindPurchaseDeduction
                    && (anchor.OriginalAmount is null || -sum > anchor.OriginalAmount))
                {
                    d6++;
                    broken = true;
                }
            }
            if (broken) ruleGroups.Add(group.Key);
        }
        return (d4, d5, d6, ruleGroups, orphans);
    }

    private static (List<Guid> In, List<Guid> Out) Split(List<Guid> rows, Dictionary<Guid, BaselineRow> baselines) =>
        (rows.Where(baselines.ContainsKey).ToList(), rows.Where(r => !baselines.ContainsKey(r)).ToList());

    // ── Taban ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--capture-baseline</c>'ın yazacağı satırlar (§7.3 "Taban çizgisi"): lisansın taban DIŞI eksi, D1 ve kopya
    /// satırları (satır başına bir kez, o anki Balance ve LedgerSum'la) ve işaret yoksa işaret. Var olan taban
    /// satırına dokunulmaz.
    /// </summary>
    public static IReadOnlyList<PersonBalanceBaseline> BaselineRowsToCapture(Snapshot s, LicenseReport r, DateTimeOffset now)
    {
        var balances = s.Balances.Where(b => b.LicenseId == r.LicenseId).ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = s.Ledger.Where(l => l.LicenseId == r.LicenseId).ToDictionary(l => l.WpfCustomerId, l => l.Sum);
        var rows = r.Details.NegativeOutside
            .Concat(r.Details.LedgerMismatchOutside)
            .Concat(r.Details.CopyOutside)
            .Distinct()
            .Select(id => new PersonBalanceBaseline
            {
                LicenseId = r.LicenseId, WpfCustomerId = id,
                Balance = balances.GetValueOrDefault(id), LedgerSum = ledger.GetValueOrDefault(id), CapturedAt = now,
            })
            .ToList();
        if (!r.HasMarker)
            rows.Add(new PersonBalanceBaseline { LicenseId = r.LicenseId, WpfCustomerId = LicenseMarker, CapturedAt = now });
        return rows;
    }
}
```

`Program.cs` — `PersonBalance` kaydının altına:

```csharp
        // Ortak bakiye denetimi (§7.3, §8.1): CLI person-balance verify kendi örneğini kurar, günlük iş DI'dan.
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifier>();
```

- [ ] **Adım 4: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceVerifier"`
Expected: 12 test PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifier.cs OrderDeck.LicenseServer/Program.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifierTests.cs
git commit -F - <<'MSG'
feat(balance): ortak bakiye denetimi — rapor ve tabana göre yeni sapma

PersonBalanceVerifier salt okunur anlık görüntüden §7.3'ün on maddesini
(davranışı değişecek kişiler, eksi ve D1 satırları, silinmiş üye bakiyesi,
kırpma farkı, D4/D5/D6/yetim bacak, kopya satırlar, PC sürümleri) ve §8.1'in
yalnız yeni sapmada çıkan uyarılarını üretir. Taban işareti satırı yeni
lisansları ve sapmasız lisansları da kapsar.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---
## Görev 12: `person-balance verify` CLI — taban, inceleme işareti, `--details` (spec §7.3, §7.1 adım 2 ve 4)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs:62-67` (CLI dağıtımı, `sales-sync` bloğundan sonra)
- Test: `OrderDeck.LicenseServer.Tests/Tools/PersonBalanceCommandTests.cs`

Kalıp `SalesSync`/`GroupCustomers` ile aynı: katı ayrıştırma, varsayılan salt okunur, yazan bayraklar `--apply` olmadan yalnız ne yazılacağını basar; çıkış 0 tamam, 1 eşzamanlı değişiklik ya da ön koşul tutmuyor (hiçbir şey yazılmadı), 2 kullanım/yapılandırma/bilinmeyen lisans. Çıktıda ad, telefon, makine adı YOK — yalnız sayılar, tutarlar, Id'ler. `enable`/`disable` S2'de bu sınıfa eklenecek.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Tools/PersonBalanceCommandTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary><c>person-balance verify</c> (spec 2026-10-11 §7.3): rapor salt okunur, taban yakalama ve inceleme
/// işareti yalnız <c>--apply</c> ile ve yalnız <c>PersonBalanceBaselines</c>'a yazar.</summary>
public sealed class PersonBalanceCommandTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceCommandTests(ApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("person-balance verify")]
    [InlineData("person-balance verify --license 7b0f3d2e-0c1a-4f7e-9a51-0d3c2b1a4e5f --details")]
    [InlineData("person-balance verify --capture-baseline")]
    [InlineData("person-balance verify --capture-baseline --apply")]
    [InlineData("person-balance verify --mark-reviewed --license 7b0f3d2e-0c1a-4f7e-9a51-0d3c2b1a4e5f --apply")]
    public void Gecerli_argumanlar(string line) =>
        PersonBalanceCommand.TryParseArgs(line.Split(' '), out _).Should().BeTrue();

    [Theory]
    [InlineData("person-balance")]
    [InlineData("person-balance enable --license 7b0f3d2e-0c1a-4f7e-9a51-0d3c2b1a4e5f")]
    [InlineData("person-balance verify --apply")]
    [InlineData("person-balance verify --capture-baseline --mark-reviewed --license 7b0f3d2e-0c1a-4f7e-9a51-0d3c2b1a4e5f")]
    [InlineData("person-balance verify --mark-reviewed --apply")]
    [InlineData("person-balance verify --license yok")]
    [InlineData("person-balance verify --license --details")]
    [InlineData("person-balance verify --details --details")]
    [InlineData("person-balance verify --bilinmeyen")]
    public void Gecersiz_argumanlar(string line) =>
        PersonBalanceCommand.TryParseArgs(line.Split(' '), out _).Should().BeFalse();

    private async Task<(int Exit, string Output, string Error)> RunAsync(string line)
    {
        PersonBalanceCommand.TryParseArgs(line.Split(' '), out var parsed).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PersonBalanceCommand.RunAsync(db, parsed!, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<int> BaselineCountAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines
            .CountAsync(b => b.LicenseId == licenseId);
    }

    [Fact]
    public async Task Rapor_salt_okunur_sayi_basar_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 20m);

        var (exit, output, _) = await RunAsync($"person-balance verify --license {kit.LicenseId}");

        exit.Should().Be(0);
        output.Should().Contain("RAPOR").And.Contain($"Lisans {kit.LicenseId}")
            .And.Contain("1. Davranışı değişecek kişi: 1, havuz toplamı 50.00")
            .And.Contain("10. Satır sayıları:");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);
    }

    [Fact]
    public async Task Taban_yakalama_kuru_kosuda_yazmaz_applyda_yazar_ikinci_kosu_bir_sey_eklemez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var negative = await kit.MemberAsync(null, -10m);

        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline");
        dry.Exit.Should().Be(0);
        dry.Output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain("eklenecek 1 satır").And.Contain("işaret eklenecek");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(0);

        var apply = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        apply.Exit.Should().Be(0);
        apply.Output.Should().Contain("UYGULANDI: 2 taban satırı");
        using (var scope = _factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines
                .AsNoTracking().Where(b => b.LicenseId == kit.LicenseId).ToListAsync();
            rows.Should().ContainSingle(b => b.WpfCustomerId == negative)
                .Which.Should().BeEquivalentTo(new { Balance = -10m, LedgerSum = -10m, ReviewedAt = (DateTimeOffset?)null });
            rows.Should().ContainSingle(b => b.WpfCustomerId == PersonBalanceVerifier.LicenseMarker);
        }

        var again = await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        again.Output.Should().Contain("Yazılacak satır yok");
        (await BaselineCountAsync(kit.LicenseId)).Should().Be(2);
    }

    [Fact]
    public async Task Inceleme_isareti_ReviewedAt_doldurur_tabansiz_lisansta_1()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.MemberAsync(null, -4m);

        (await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply")).Exit
            .Should().Be(1, "önce taban yakalanmalı");

        await RunAsync($"person-balance verify --license {kit.LicenseId} --capture-baseline --apply");
        var dry = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed");
        dry.Output.Should().Contain("incelenmemiş 2 taban satırı");
        var apply = await RunAsync($"person-balance verify --license {kit.LicenseId} --mark-reviewed --apply");
        apply.Exit.Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines.AsNoTracking()
                .Where(b => b.LicenseId == kit.LicenseId).ToListAsync())
            .Should().OnlyContain(b => b.ReviewedAt != null);
    }

    [Fact]
    public async Task Details_yalniz_GUID_basar_kullanici_adi_basmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"gizli{Guid.NewGuid():N}";
        var negative = await kit.MemberAsync(null, -6m, username: name);

        var (_, output, _) = await RunAsync($"person-balance verify --license {kit.LicenseId} --details");

        output.Should().Contain(negative.ToString()).And.NotContain(name);
    }

    [Fact]
    public async Task Bilinmeyen_lisans_2_doner()
    {
        var (exit, _, error) = await RunAsync($"person-balance verify --license {Guid.NewGuid()}");

        exit.Should().Be(2);
        error.Should().Contain("Lisans bulunamadı");
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceCommandTests"`
Expected: FAIL — `PersonBalanceCommand` yok.

- [ ] **Adım 3: Komut**

`OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance verify [seçenekler]
/// Ortak bakiye denetimi (spec 2026-10-11 §7.3; çekirdek <see cref="PersonBalanceVerifier"/>):
/// <list type="bullet">
///   <item><c>verify [--license &lt;guid&gt;] [--details]</c> — salt okunur rapor (§7.3 maddeleri 1–10). Lisans
///   verilmezse verisi olan bütün lisanslar. <c>--details</c> eksi ve kural bozan satırların Id'lerini (GUID; ad,
///   telefon yok) konsola basar; dosyaya yazılmaz.</item>
///   <item><c>--capture-baseline [--apply]</c> — taban çizgisi (§7.1 adım 2): taban dışı eksi, D1 ve kopya satırları
///   o anki Balance ve LedgerSum'la, lisans başına bir işaret satırıyla. Var olan taban satırına dokunmaz.</item>
///   <item><c>--mark-reviewed --license &lt;guid&gt; [--apply]</c> — sahip incelemesi (§7.1 adım 4): lisansın
///   incelenmemiş taban satırlarının <c>ReviewedAt</c>'i.</item>
/// </list>
/// Yazan bayraklar <c>--apply</c> olmadan yazacağını basar; yalnız <c>PersonBalanceBaselines</c>'a yazar. Rapora
/// <c>--apply</c> vermek kullanım hatasıdır. Çıkış: 0 tamam; 1 eşzamanlı yazım ya da tabansız lisansta inceleme,
/// hiçbir şey yazılmadı; 2 kullanım, yapılandırma ya da bilinmeyen lisans. Argümanlar katı (<see cref="SalesSync"/>
/// deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan (<c>ConnectionStrings:LicenseDb</c>).
/// </summary>
public static class PersonBalanceCommand
{
    private const string Usage =
        "Kullanım:\n"
        + "  person-balance verify [--license <guid>] [--details] [--capture-baseline | --mark-reviewed] [--apply]";

    /// <summary>Ayrıştırılmış <c>verify</c>.</summary>
    public sealed record VerifyArgs(Guid? License, bool Details, bool CaptureBaseline, bool MarkReviewed, bool Apply);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var parsed))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(options);
        return await RunAsync(db, parsed, Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary>Gövde: argümanlar ayrıştırılmış, bağlam kurulmuş. Testler buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, VerifyArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (args.License is { } only && !await db.Licenses.AsNoTracking().AnyAsync(l => l.Id == only, ct))
        {
            error.WriteLine($"Lisans bulunamadı: {only}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var snapshot = await new PersonBalanceVerifier(db).LoadAsync(args.License, now, ct);
        var report = PersonBalanceVerifier.Analyze(snapshot);

        var mode = !(args.CaptureBaseline || args.MarkReviewed) ? "RAPOR (salt okunur)"
            : args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA";
        output.WriteLine($"{mode} — person-balance verify — {Time(now)}");
        foreach (var license in report.Licenses) WriteLicense(output, license, args.Details);
        output.WriteLine(
            $"10. Satır sayıları: WpfCustomerProjections {report.ProjectionCount}, CustomerBalanceTransactions {report.TransactionCount}");

        if (args.CaptureBaseline) return await CaptureAsync(db, snapshot, report, args.Apply, now, output, error, ct);
        if (args.MarkReviewed) return await MarkReviewedAsync(db, args.License!.Value, args.Apply, now, output, error, ct);
        return 0;
    }

    private static async Task<int> CaptureAsync(
        LicenseDbContext db, PersonBalanceVerifier.Snapshot snapshot, PersonBalanceVerifier.Report report, bool apply,
        DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var rows = new List<PersonBalanceBaseline>();
        foreach (var license in report.Licenses)
        {
            var add = PersonBalanceVerifier.BaselineRowsToCapture(snapshot, license, now);
            var markers = add.Count(r => r.WpfCustomerId == PersonBalanceVerifier.LicenseMarker);
            output.WriteLine(
                $"Taban — lisans {license.LicenseId}: eklenecek {add.Count - markers} satır "
                + $"(eksi {license.Details.NegativeOutside.Count}, D1 {license.Details.LedgerMismatchOutside.Count}, "
                + $"kopya {license.Details.CopyOutside.Count}), işaret {(markers > 0 ? "eklenecek" : "zaten var")}");
            rows.AddRange(add);
        }

        if (!apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }
        if (rows.Count == 0)
        {
            output.WriteLine("Yazılacak satır yok.");
            return 0;
        }

        db.PersonBalanceBaselines.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            error.WriteLine("Taban bu arada başka bir koşuyla yazıldı; hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }
        output.WriteLine($"UYGULANDI: {rows.Count} taban satırı yazıldı (işaretler dahil).");
        return 0;
    }

    private static async Task<int> MarkReviewedAsync(
        LicenseDbContext db, Guid licenseId, bool apply, DateTimeOffset now, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        if (!await db.PersonBalanceBaselines.AnyAsync(
                b => b.LicenseId == licenseId && b.WpfCustomerId == PersonBalanceVerifier.LicenseMarker, ct))
        {
            error.WriteLine($"Lisansın tabanı yok: {licenseId}. Önce --capture-baseline --apply. Hiçbir şey yazılmadı.");
            return 1;
        }

        var pending = await db.PersonBalanceBaselines
            .Where(b => b.LicenseId == licenseId && b.ReviewedAt == null)
            .ToListAsync(ct);
        output.WriteLine($"İnceleme — lisans {licenseId}: incelenmemiş {pending.Count} taban satırı (işaret dahil)");
        if (!apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        foreach (var row in pending) row.ReviewedAt = now;
        await db.SaveChangesAsync(ct);
        output.WriteLine($"UYGULANDI: {pending.Count} satır incelendi olarak işaretlendi.");
        return 0;
    }

    private static void WriteLicense(TextWriter o, PersonBalanceVerifier.LicenseReport r, bool details)
    {
        var baseline = r.HasMarker ? "alındı" : r.HasBaseline ? "yeni lisans (boş taban)" : "YOK";
        o.WriteLine($"Lisans {r.LicenseId} — taban: {baseline}");
        o.WriteLine($"  1. Davranışı değişecek kişi: {r.SpreadPersons}, havuz toplamı {Amount(r.SpreadPoolTotal)}");
        o.WriteLine($"  2. Eksi satır: tabanda {r.NegativeRows.InBaseline}, taban dışı {r.NegativeRows.Outside}, toplam {Amount(r.NegativeRows.Total)}");
        o.WriteLine($"  3. D1 bozuk satır: tabanda {r.LedgerMismatches.InBaseline}, taban dışı {r.LedgerMismatches.Outside}, fark toplamı {Amount(r.LedgerMismatches.Total)}");
        o.WriteLine($"  4. Silinmiş üyede bakiye: {r.PurgedWithBalance} ({Amount(r.PurgedBalanceTotal)}); grubunda canlı üye kalmamış: {r.StrandedPurged} ({Amount(r.StrandedPurgedTotal)})");
        o.WriteLine($"  5. Kırpma farkı: {r.TrimMismatches}; sekme/satır sonu içeren numara: {r.ControlCharGroups}");
        o.WriteLine($"  6. Havuzu < 0 kişi: {r.NegativePersons} (tabanla açıklanan {r.NegativePersonsExplained})");
        o.WriteLine($"  7. D4 {r.D4}, D5 {r.D5}, D6 {r.D6}, yetim bacak {r.OrphanLegs}");
        o.WriteLine($"  8. Kopya satır: hareketli {r.CopyRowsWithTransactions}, bakiyeli {r.CopyRowsWithBalance}; tabanda {r.CopyRows.InBaseline}, taban dışı {r.CopyRows.Outside}");
        o.WriteLine($"  9. Aktif PC sürümleri (son {PersonBalanceVerifier.ActivePcWindow.Days} gün): "
            + (r.ActivePcVersions.Count == 0
                ? "yok"
                : string.Join(", ", r.ActivePcVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} × {v.Value}"))));
        if (r.Alerts.Count > 0) o.WriteLine($"  YENİ SAPMA: {string.Join("; ", r.Alerts)}");
        if (!details) return;
        WriteIds(o, "eksi (taban dışı)", r.Details.NegativeOutside);
        WriteIds(o, "eksi (tabanda)", r.Details.NegativeInBaseline);
        WriteIds(o, "D1 (taban dışı)", r.Details.LedgerMismatchOutside);
        WriteIds(o, "D1 (tabanda)", r.Details.LedgerMismatchInBaseline);
        WriteIds(o, "kopya (taban dışı)", r.Details.CopyOutside);
        WriteIds(o, "grubu dağılmış silinmiş", r.Details.StrandedPurged);
        WriteIds(o, "kural bozan grup", r.Details.RuleBreakingGroups);
        WriteIds(o, "yetim bacak", r.Details.OrphanLegs);
        WriteIds(o, "daha da eksiye inen", r.Details.WorsenedBaselineRows);
        WriteIds(o, "D1 farkı değişen", r.Details.ChangedMismatchRows);
    }

    private static void WriteIds(TextWriter o, string label, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        o.WriteLine($"    {label}: " + string.Join(", ", ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)));
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    /// <summary>args[0] komut adı, args[1] alt komut (yalnız <c>verify</c>; S2 <c>enable</c>/<c>disable</c>
    /// ekler). Tanınmayan argüman, ikinci kez verilen seçenek, değersiz ya da GUID olmayan <c>--license</c>, rapora
    /// <c>--apply</c>, birlikte <c>--capture-baseline</c> ve <c>--mark-reviewed</c>, lisanssız <c>--mark-reviewed</c>
    /// → false.</summary>
    public static bool TryParseArgs(string[] args, [NotNullWhen(true)] out VerifyArgs? parsed)
    {
        parsed = null;
        if (args.Length < 2 || args[1] != "verify") return false;

        Guid? license = null;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--license")
            {
                if (license is not null || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var id)) return false;
                license = id;
                i++;
            }
            else if (arg is "--details" or "--capture-baseline" or "--mark-reviewed" or "--apply")
            {
                if (!flags.Add(arg)) return false;
            }
            else
            {
                return false;
            }
        }

        var capture = flags.Contains("--capture-baseline");
        var mark = flags.Contains("--mark-reviewed");
        var apply = flags.Contains("--apply");
        if (capture && mark) return false;
        if (apply && !capture && !mark) return false;
        if (mark && license is null) return false;

        parsed = new VerifyArgs(license, flags.Contains("--details"), capture, mark, apply);
        return true;
    }
}
```

`Program.cs` — `sales-sync` bloğundan sonra:

```csharp
        if (args.Length > 0 && args[0] == "person-balance")
        {
            var exit = await OrderDeck.LicenseServer.Tools.PersonBalanceCommand.RunAsync(args);
            Environment.Exit(exit);
            return;
        }
```

- [ ] **Adım 4: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceCommandTests|FullyQualifiedName~PersonBalanceVerifier"`
Expected: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Tools/PersonBalanceCommand.cs OrderDeck.LicenseServer/Program.cs \
  OrderDeck.LicenseServer.Tests/Tools/PersonBalanceCommandTests.cs
git commit -F - <<'MSG'
feat(balance): person-balance verify CLI — rapor, taban yakalama, inceleme işareti

Salt okunur rapor (§7.3 maddeleri 1–10), --capture-baseline ve
--mark-reviewed yalnız --apply ile ve yalnız PersonBalanceBaselines'a yazar;
--details yalnız GUID basar. Katı ayrıştırma, kuru koşu varsayılan
(ortak bakiye §7.1 adım 2 ve 4).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 13: Günlük iş `person-balance-verify` + uyarı e-postası (spec §8.1)

**Files:**
- Oluştur: `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifyJob.cs`
- Değiştir: `OrderDeck.LicenseServer/Program.cs` (DI; tekrarlayan işler bloğunda `ScheduleSalesSyncJobs` çağrısından sonra; yeni `SchedulePersonBalanceJobs`)
- Test: `OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs`

Takvim 04:58 UTC: yayın penceresi (TR 20:00–01:00) dışında, 04:57 banka saklamasından sonra, 5 dakikalık ızgaranın (04:55, 05:00) ve saatlik :02/:07 işlerinin dışında; o dakikada başka iş yok. Açılışta kuyruğa alınmaz. E-posta `Admin:AlertEmail`'e (kalıp `BackupRestoreDrillJob.cs:106`), yalnız yeni sapma varken, günde bir (iş günde bir koşar); gövdede yalnız lisans Id'leri ve sayılar.

- [ ] **Adım 1: Testleri yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs`:

```csharp
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary><c>person-balance-verify</c> (spec 2026-10-11 §8.1): günlük denetim yalnız yeni sapmada
/// <c>Admin:AlertEmail</c>'e yazar; taban yokken eksi satır yalnız sayılır. Her test kendi fabrikasıyla (iş bütün
/// lisansları tarar).</summary>
public sealed class PersonBalanceVerifyJobTests
{
    private static readonly string AlertTo = $"uyari-{Guid.NewGuid():N}@example.test";

    private static async Task<(TestEmailSender Email, PersonBalanceVerifier.Report Report)> RunJobAsync(
        ApiFactory factory, string? alertTo = null)
    {
        var email = new TestEmailSender();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:AlertEmail"] = alertTo ?? AlertTo })
            .Build();
        using var scope = factory.Services.CreateScope();
        var job = new PersonBalanceVerifyJob(
            scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>(), email, config,
            NullLogger<PersonBalanceVerifyJob>.Instance);
        var report = await job.VerifyAndAlertAsync(default);
        return (email, report);
    }

    private static Task MarkerAsync(Kit kit) =>
        kit.Sales.WithDbAsync(db =>
        {
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Tabanli_lisansta_taban_disi_eksi_satir_e_posta_ile_uyarir_kisisel_veri_yok()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        var name = $"gizli{Guid.NewGuid():N}";
        await kit.MemberAsync(null, -10m, username: name);

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).Alerts.Should().ContainSingle();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.ToEmail.Should().Be(AlertTo);
        sent.PlainBody.Should().Contain(kit.LicenseId.ToString()).And.Contain("eksi: taban dışı 1").And.NotContain(name);
    }

    [Fact]
    public async Task Taban_yokken_eksi_satir_uyarmaz()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await kit.MemberAsync(null, -10m);

        var (email, report) = await RunJobAsync(factory);

        report.Licenses.Single(l => l.LicenseId == kit.LicenseId).NegativeRows.Outside.Should().Be(1);
        email.Sent.Should().BeEmpty("§8.1: taban alınana kadar yalnız sayı");
    }

    [Fact]
    public async Task Sapma_yoksa_e_posta_gitmez()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        await kit.Example1Async();

        (await RunJobAsync(factory)).Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AlertEmail_tanimsizsa_gondermez()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await MarkerAsync(kit);
        await kit.MemberAsync(null, -10m);

        (await RunJobAsync(factory, alertTo: "")).Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public void Is_0458_UTC_takvimiyle_kaydolur()
    {
        var storage = new MemoryStorage();

        Program.SchedulePersonBalanceJobs(new RecurringJobManager(storage));

        using var connection = storage.GetConnection();
        var job = connection.GetRecurringJobs().Should().ContainSingle(j => j.Id == "person-balance-verify").Subject;
        (job.Cron, job.TimeZoneId).Should().Be(("58 4 * * *", "UTC"));
        job.Job.Type.Should().Be(typeof(PersonBalanceVerifyJob));
        job.Job.Method.Name.Should().Be(nameof(PersonBalanceVerifyJob.RunAsync));
    }

    [Fact]
    public void Is_DI_kapsamindan_cozulur()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PersonBalanceVerifyJob>().Should().NotBeNull();
    }
}
```

- [ ] **Adım 2: Koştur — derleme hatası bekle**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceVerifyJobTests"`
Expected: FAIL — `PersonBalanceVerifyJob` yok.

- [ ] **Adım 3: İş**

`OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifyJob.cs`:

```csharp
using Hangfire;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// <c>person-balance-verify</c> — ortak bakiye günlük denetimi (spec 2026-10-11 §8.1). <c>person-balance verify</c>
/// ile aynı çekirdek (<see cref="PersonBalanceVerifier"/>): her lisansın sayıları günlüğe (kişisel veri yok); tabana
/// göre YENİ sapma varsa <c>Admin:AlertEmail</c>'e tek e-posta (kalıp <c>BackupRestoreDrillJob</c>). Taban
/// alınmamış lisansta eksi/D1/kopya kalemleri yalnız sayılır — kabul edilmiş eski veri her gün uyarıp gerçek bir
/// bozulmayı gizlemesin. Kazanan tasarımın "değişmez kuralı çalışırken denetleyen iş yok" riskini kapatır.
/// Takvim <see cref="Program.SchedulePersonBalanceJobs"/>'ta.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class PersonBalanceVerifyJob
{
    /// <summary>Hangfire işinin adı (recurring Id).</summary>
    public const string JobId = "person-balance-verify";

    /// <summary>04:58 UTC — yayın penceresi dışında, 04:57 banka saklamasından sonra, ızgara dışında.</summary>
    public const string Cron = "58 4 * * *";

    private readonly PersonBalanceVerifier _verifier;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonBalanceVerifyJob> _log;

    public PersonBalanceVerifyJob(
        PersonBalanceVerifier verifier, IEmailSender email, IConfiguration config, ILogger<PersonBalanceVerifyJob> log)
    {
        _verifier = verifier;
        _email = email;
        _config = config;
        _log = log;
    }

    /// <summary>Hangfire girişi. Raporu döndürmez: Hangfire dönüş değerini depoya yazardı.</summary>
    public async Task RunAsync(CancellationToken ct) => await VerifyAndAlertAsync(ct);

    public async Task<PersonBalanceVerifier.Report> VerifyAndAlertAsync(CancellationToken ct)
    {
        var report = PersonBalanceVerifier.Analyze(await _verifier.LoadAsync(null, DateTimeOffset.UtcNow, ct));
        foreach (var l in report.Licenses)
            _log.LogInformation(
                "person-balance-verify: lisans {LicenseId} — taban {HasBaseline}, eksi {NegIn}/{NegOut}, D1 {MisIn}/{MisOut}, "
                + "kopya {CopyIn}/{CopyOut}, D4 {D4} D5 {D5} D6 {D6} yetim {Orphans}, havuzu<0 kişi {NegativePersons}, "
                + "grubu dağılmış silinmiş {Stranded}, kırpma {Trim}",
                l.LicenseId, l.HasBaseline, l.NegativeRows.InBaseline, l.NegativeRows.Outside,
                l.LedgerMismatches.InBaseline, l.LedgerMismatches.Outside, l.CopyRows.InBaseline, l.CopyRows.Outside,
                l.D4, l.D5, l.D6, l.OrphanLegs, l.NegativePersons, l.StrandedPurged, l.TrimMismatches);

        var alerting = report.Licenses.Where(l => l.Alerts.Count > 0).ToList();
        if (alerting.Count == 0) return report;

        var lines = alerting.Select(l => $"Lisans {l.LicenseId}: {string.Join("; ", l.Alerts)}").ToList();
        foreach (var line in lines) _log.LogWarning("person-balance-verify: YENİ SAPMA — {Line}", line);

        var to = _config["Admin:AlertEmail"];
        if (string.IsNullOrWhiteSpace(to))
        {
            _log.LogWarning("person-balance-verify: yeni sapma var ama Admin:AlertEmail tanımsız — e-posta gönderilmedi");
            return report;
        }

        var plain = "Ortak bakiye günlük denetimi tabana göre yeni sapma buldu (yalnız sayılar; kişisel veri yok).\n\n"
                    + string.Join("\n", lines)
                    + "\n\nAyrıntı: docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll "
                    + "person-balance verify --license <id> --details";
        var html = "<h2>Ortak bakiye denetimi: yeni sapma</h2><ul>"
                   + string.Concat(lines.Select(l => $"<li>{System.Net.WebUtility.HtmlEncode(l)}</li>"))
                   + "</ul><p>Ayrıntı: <code>person-balance verify --license &lt;id&gt; --details</code></p>";
        try
        {
            await _email.SendAsync(to, "OrderDeck Admin", "[OrderDeck] Ortak bakiye denetimi: yeni sapma", html, plain, ct);
            _log.LogInformation("person-balance-verify: uyarı e-postası gönderildi ({Count} lisans)", alerting.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "person-balance-verify: uyarı e-postası gönderilemedi ({Count} lisans)", alerting.Count);
        }
        return report;
    }
}
```

`Program.cs`:

DI (verifier kaydının altına):

```csharp
        // Günlük ortak bakiye denetimi (§8.1); takvim SchedulePersonBalanceJobs'ta.
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifyJob>();
```

Tekrarlayan işler bloğunda `ScheduleSalesSyncJobs(...)` çağrısından sonra:

```csharp
            // Ortak bakiye denetimi (04:58 UTC): takvim ve gerekçe SchedulePersonBalanceJobs'ta.
            SchedulePersonBalanceJobs(manager);
```

`ScheduleSalesSyncJobs` metodunun altına:

```csharp
    /// <summary>
    /// Ortak bakiye denetiminin Hangfire takvimi (spec 2026-10-11 §8.1; saatler UTC). <c>person-balance-verify</c>
    /// günde bir, 04:58'de: yayın penceresi (TR 20:00–01:00) dışında, 04:57 banka saklamasından sonra; 5 dakikalık
    /// ızgaranın (04:55, 05:00) ve saatlik :02/:07 işlerinin dışında, o dakikada başka iş yok. Açılışta kuyruğa
    /// alınmaz: S1 deploy'u ile taban yakalama arasındaki günlerde zaten yalnız sayı basar.
    /// </summary>
    public static void SchedulePersonBalanceJobs(IRecurringJobManager manager) =>
        manager.AddOrUpdate<OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifyJob>(
            OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifyJob.JobId,
            j => j.RunAsync(CancellationToken.None),
            OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifyJob.Cron);
```

- [ ] **Adım 4: Koştur — PASS**

Run: `DOTNET_CLI_UI_LANGUAGE=en dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PersonBalanceVerifyJobTests|FullyQualifiedName~SalesCommandLogRetentionJobTests|FullyQualifiedName~BankJobsDiTests"`
Expected: PASS (öbür iki sınıf takvim yardımcılarının bozulmadığını gösterir).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/CustomerSync/PersonBalanceVerifyJob.cs OrderDeck.LicenseServer/Program.cs \
  OrderDeck.LicenseServer.Tests/Services/CustomerSync/PersonBalanceVerifyJobTests.cs
git commit -F - <<'MSG'
feat(balance): ortak bakiye günlük denetimi ve yeni sapma uyarısı

person-balance-verify her gün 04:58 UTC'de koşar, lisans başına sayıları
günlüğe yazar ve yalnız tabana göre yeni sapmada Admin:AlertEmail'e kişisel
veri içermeyen bir e-posta gönderir (ortak bakiye §8.1).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

---

## Görev 14: `deploy/README` uyarısı + tam paket (spec §7.4)

**Files:**
- Değiştir: `deploy/README.md` ("Deploy geri alma (rollback)" bölümü, "Satış senkronu S1 tabanı" uyarısından sonra, `## Masaüstü: …` başlığından önce)

- [ ] **Adım 1: Uyarıyı ekle**

```markdown
> **UYARI — Ortak bakiye S1 tabanı (spec 2026-10-11 §7.4).**
> `OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs` dosyasını
> İÇERMEYEN bir imaja (etiketin kaynağında
> `git cat-file -e <sha>:OrderDeck.LicenseServer/Services/CustomerSync/PersonBalance.cs`
> başarısızsa o imaj S1 öncesidir), `DrawGroupId`'si dolu ve kendi `Id`'sinden
> farklı bir defter satırı (bir bacak) oluştuktan sonra bu elle yoldan ASLA
> geçme. Kontrol:
> `SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE DrawGroupId IS NOT NULL AND DrawGroupId <> Id`.
> Eski reverse yalnız çapayı (tutarı 0 olabilir) geri alır; müşterinin öbür
> hesaplarından düşülen para geri dönmez. Ayrıca `scope` ve `transactions/{id}`
> yalnız çapa tutarını döner. Havuz kipi (S2) açılmadan `DrawGroupId` hiç
> dolmaz; bu sorgu o zamana kadar 0 döner.
>
> Otomatik geri alma bir önceki imaja döner ve güvenlidir: S1 göçü yalnız
> ekleyici, S1 öncesi imaj yeni şemada yazmayı sürdürür
> (`PersonBalanceS1MigrationTests`). S2 deploy'u düşerse imaj S1'e döner; S1
> kip bayrağını okumaz, bütün lisanslar satır kipine iner ve S1 çok bacaklı
> geçmişi okur ve grup olarak geri alır.
>
> S1'den sonra (Burak): `docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance verify`
> (salt okunur) → `… person-balance verify --capture-baseline` (kuru) →
> `… --capture-baseline --apply`. İnceleme: `… --mark-reviewed --license <id> --apply`.
```

- [ ] **Adım 2: Sunucu derlemesi — 0 uyarı**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj -warnaserror --nologo -v q`
Expected: `Build succeeded.` `0 Warning(s)`.

- [ ] **Adım 3: Tam sunucu paketi (detached, ~10+ dk, Docker açık)**

PowerShell'den (Testcontainers için gerekiyorsa önce `$env:DOCKER_HOST` — "Genel kurallar"):

```powershell
$root = 'C:\Users\burak\source\repos\LiveDeck\.claude\worktrees\ortak-bakiye-s1'
$log  = Join-Path $env:TEMP 'ortak-bakiye-s1-tam-paket.txt'
$done = Join-Path $env:TEMP 'ortak-bakiye-s1-tam-paket.done'
Remove-Item $log, $done -ErrorAction SilentlyContinue
Start-Process -FilePath powershell.exe -WindowStyle Hidden -ArgumentList @(
  '-NoProfile', '-Command',
  "Set-Location '$root'; `$env:DOTNET_CLI_UI_LANGUAGE='en'; " +
  "dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo -v q 2>&1 | Out-File -Encoding utf8 '$log'; " +
  "`$LASTEXITCODE | Out-File -Encoding utf8 '$done'")
```

Bitişi `$done` dosyasıyla izle (Monitor ya da kısa aralıklı denetim — `Test-Path $done`); bitince:

```powershell
Get-Content $done -Encoding UTF8
Select-String -Path $log -Pattern 'Passed!|Failed!|error' -Encoding UTF8
```

Expected: `$done` = `0`, özet satırı `Passed!  - Failed: 0`. Sayıyı plan ya da CLAUDE.md'ye yazma (CI'dan okunur). Kırmızı test varsa: önce hangi görevin dosyasına ait olduğunu bul, sistematik hata ayıklama; var olan bir testi spec gerekçesi olmadan değiştirme.

- [ ] **Adım 4: Commit**

```bash
git add deploy/README.md
git commit -F - <<'MSG'
docs(deploy): ortak bakiye S1 tabanı uyarısı ve verify komutları

S1 öncesi imaja bacak (DrawGroupId <> Id) oluştuktan sonra elle dönülmez;
tespit sorgusu, otomatik geri almanın neden güvenli olduğu ve S1 sonrası
person-balance verify / taban adımları (spec 2026-10-11 §7.4).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
MSG
```

- [ ] **Adım 5: Push ve PR — yalnız Burak isterse**

Bu plan push etmez. Burak PR isterse: `git push -u origin feat/ortak-bakiye-s1`, başlık `feat(balance): ortak bakiye S1 — okuyucular, grup geri alması, GroupId kancası, verify`; açıklamada göç süresi (Görev 1 çıktısı), "para davranışı değişmez" ve aşağıdaki saha notları. Merge Burak'ta, **yayın penceresi dışında** (Paz/Pzt/Çar/Per 20:00–01:00 TR değil); merge = otomatik prod deploy.

---

## Sürüm / saha notları (S1 sonrası, spec §7.1 adım 1–4, §8.5)

1. Merge öncesi (adım 0): prod'da iki tablonun salt okunur satır sayımı; Görev 1'in göç süresiyle karşılaştır.
2. Deploy sonrası: `/ready` geldi; `__EFMigrationsHistory`'de `*_PersonBalanceS1`; `sys.indexes`'te `IX_WpfCustomerProjections_LicenseId_GroupId` ve `IX_CustomerBalanceTransactions_DrawGroupId`; `sys.tables`'ta `PersonBalanceBaselines`; `sys.columns`'ta `GroupId` harmanı `Latin1_General_100_BIN2`.
3. Bir müşteride WPF preview yanıtı öncekiyle aynı, `personPool: false`.
4. `person-balance verify` → rapor Burak'a; `--capture-baseline --apply`; ertesi gün `person-balance-verify` uyarı üretmemeli (Hangfire panosu ve günlük).
5. ≥ 1 gün gözlem: D4/D5/D6/yetim 0, tabana göre sapma yok. Ardından sahip incelemesi (`--mark-reviewed`) — **S2'den önce**.

---

## Öz-inceleme (plan yazıldıktan sonra yapıldı)

**Spec kapsamı (§12 S1 satırı):**
- Göç (§3.1; taban tablosu dahil, `ONLINE` yok) → Görev 1; istatistik düşürme, Express testi, betik testi.
- `PersonBalance` okuyucuları, ordinal süzgeçli kişi sorgusu (§4.0, §4.13, §15-5) → Görev 3.
- `scope`, `transactions/{id}`, Lookup `−Σ`, bacak anahtar → 404, `PersonPool = false` (§4.1–§4.4) → Görev 4; §15-6 kapısı korunur (fark 3).
- Grup geri alması (WPF ve panel; çakışmada baştan kurma; tek bacaklıda `DrawGroupId = NULL`) (§4.5, M12, M13) → Görev 5 (WPF), Görev 6 (panel).
- Panel reverse'inde `DrawGroupId` dolu hedefler (üyelik, grup geri alma ya da `not-reversible`) (§4.9, M8) → Görev 6.
- Toplanmış listeler (panel, Shopper), panel `Members[]`, `UpdatedAt` (§4.6, §4.10, M9) → Görev 7.
- `CustomerBalance` → `StampVersions`; birleştiricinin sıfırda damgalaması (§4.11, §4.12, M6, M12) → Görev 2.
- `GroupId` kancası, silinmiş üyenin numara izlemesi, convention testi (§4.11, §4.12, M11) → Görev 9, 10.
- `person-balance verify` (taban, inceleme işareti, yeni kalemler) ve günlük iş (§7.3, §8.1) → Görev 11, 12, 13.
- `deploy/README` uyarısı (§7.4) → Görev 14.
- Testcontainers 3, 4, 5 → Görev 8; 11 → Görev 1; 12 → Görev 9; 13 → Görev 10. §8.3 InMemory/birim maddelerinin S1 kısmı ilgili görevlerde (S2'ye ait olanlar "Bilerek S1 dışında" listesinde).
- Boşluk yok. Bilinçli farklar başta listelendi (1–14).

**Yer tutucu taraması:** "TBD/TODO/sonra/uygun hata yönetimi" yok; her kod adımında tam kod. Görev 1'in göç dosyası EF'in üreteceğiyle karşılaştırılacak biçimde tam verildi (yalnız zaman damgalı dosya adı üretimde belli olur).

**Tip tutarlılığı:** `PersonBalance` üyeleri — `MembersAsync(Guid, Guid, CancellationToken) → Person(CanonicalId, GroupId, Members)`, `Member(WpfCustomerId, Balance, Purged)`, `IsAnchor`, `GroupKeyOf`, `AnchorOfAsync(CustomerBalanceTransaction, ct)`, `GroupSumAsync(CustomerBalanceTransaction, ct)`, `IsGroupReversible`, `IsDuplicateReversal`, `IsDuplicateBalanceRow`, `IsDeadlock`, `ReverseGroupAsync(licenseId, anchorId, createdBy, ct) → ReverseOutcome {Reversed, AlreadyReversed, NotFound, Busy}`, `MaxAttempts`, `HistoryEntry(Id, AnchorWpfCustomerId, Amount, Kind, OriginalAmount, ShippingDeducted, Reason, ReversesTransactionId, CreatedAt)`, `AggregateHistory`, `HistoryAsync`, `LastMovementAtAsync` — Görev 3/5/6/7'de tanımlı, Görev 4–13'te aynı adlarla kullanıldı. `PersonMembership.IsGrouped/GroupMembersAsync/MemberRow` Görev 3. `PersonBalanceVerifier` kayıtları (`Snapshot`, `LicenseRow`, `ProjectionRow`, `BalanceRow`, `LedgerRow`, `DrawRow`, `BaselineRow`, `ActivationRow`, `Tally`, `Details`, `LicenseReport`, `Report`, `LicenseMarker`, `ActivePcWindow`, `BaselineRowsToCapture`) Görev 11'de tanımlı, Görev 12/13 aynı adları kullanır. Test kiti (`PersonBalanceTestKit`: `MemberAsync`, `FundAsync`, `SeedDrawAsync`, `SeedLegacyDeductionAsync`, `Example1Async`/`Example1`, `BalanceAsync`, `BalanceRowAsync`, `LedgerSumAsync`, `LedgerHoldsAsync`, `ReversalsOfAsync`, `ProjectionAsync`, istek yardımcıları, `SendAsync`, `TitleAsync`, sabitler) Görev 3'te tanımlı; sonraki görevler yalnız bunları çağırır. `RaceSaveHook`/`RaceRelationalApiFactory` Görev 8'de tanımlı, Görev 9 da kullanır. Var olan adlar repo'da doğrulandı: `SalesCommandTestKit` (`CreateAsync`, `WithDbAsync`, `ReadDbAsync`, `SeedProjectionAsync`, `ProblemAsync`, `WpfUserAgent`), `SalesSyncConfiguredApiFactory`, `RelationalApiFactory`, `SqlServerContainerFixture`/`SqlServerExpressContainerFixture`/`SqlServerCollection`, `TestEmailSender.Sent`, `CustomerIdentityMergeJob.Report.FailedGroups`, `CustomerGroupingJob.Report.FailedComponents`, `CustomerGroupingJob.Pair`, `SalesModeGate.RecentWindow`, `CustomerGroupUnion.StampAt`.

**Sıra ve yeşil kalma:** her görev derlenir ve var olan testleri bozmaz — Görev 2 yalnız spec'in değiştirdiği bir iddiayı (sıfır bakiyeli kopyanın `UpdatedAt`'i) günceller; Görev 10'un güncelleyebileceği iş testleri için kural Adım 5'te. Göç ilk görevde; `verify` ve günlük iş, denetledikleri okuyuculardan ve kancadan sonra.
