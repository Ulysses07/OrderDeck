# Netgsm Kurulum Yaşam Döngüsü — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncı kendi panelinden Netgsm + İYS kimliklerini girer, sistem bunları senkron doğrular, günlük yeniden doğrular ve doğrulanmamış kurulumda ne onay toplar ne SMS gönderir.

**Architecture:** `NetgsmAccount.Status` tek yetki anahtarıdır (`Failed` / `Verified` / `Disabled`, varsayılan `Failed`). Panel yazma ucu satırı önce `Failed` olarak kaydeder, sonra `/iys/search` ile tek bir senkron çağrı yapar ve sonucuna göre `Verified`'a yükseltir — arada kalan her an kapalıdır. Günlük bir Hangfire işi aynı doğrulamayı tekrarlar. Admin kill switch `Disabled` yazar ve o lisansın süren kampanyalarını `paused`'a çeker; bakiye tükenmesiyle aynı duraklama yolu.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test / Testcontainers ilişkisel test), Hangfire, `IDataProtection`, xUnit + FluentAssertions.

---

## Bağlam — bu planı uygulayacak mühendisin bilmesi gerekenler

**İYS nedir.** İleti Yönetim Sistemi, Türkiye'nin ulusal ticari-ileti izin
kayıt sistemi. 6563 sayılı kanun: İYS dışında alınan onay **üç iş günü**
içinde kaydedilmezse hukuken yok sayılır.

**Onay MARKA başınadır.** Aynı telefon, `731734` markası altında ONAY,
`763208` altında RET olabilir (2026-09-17'de ölçüldü). Yanlış markaya sormak
hata fırlatmaz — doğru satıra **yanlış cevap** yazar. Aynı sınıf bir karışıklık
daha önce 284 onaya mal oldu. Bu yüzden marka her zaman kampanyanın/kaydın
lisansından çözülür, global yapılandırmadan asla.

**`/iys/search` neden doğrulama kapısı.** Netgsm'in İYS ucunda abone no, API
şifresi ve marka kodu **gövdede birlikte** gider. Tek bir `search` çağrısı
üçünü birden doğrular. Kimlik yanlışsa `code 30`, marka hesaba ait değilse
`code 60` döner ve `NetgsmIysClient` bunları `IysConfigurationException` olarak
fırlatır — bu, elimizdeki tek kesin "kimlik/marka yanlış" sinyalidir.

**Test SMS'i ATILMAZ** (spec §2.2): yayıncının parasını harcar, bir alıcı
numarası ister ve İYS'ye dokunur.

### Bu planda alınan üç karar (kodu yazarken bunları değiştirme)

**1. Gönderici başlığı (`Header`) doğrulanamaz.** `/iys/search` payload'ında
başlık yok; Netgsm onaysız başlığı ancak gönderim anında reddediyor. Test SMS'i
yasak olduğuna göre başlığın geçerliliği bu planda **kanıtlanmaz** — yalnız
saklanır. `Verified` durumu "abone no + şifre + marka doğru" demektir, "başlık
onaylı" demek değildir. Panelde de böyle yazılır.

**2. Spec §2.4'ten bilinçli sapma: çözülemeyen şifre hesabı `Disabled` YAPMAZ.**
Spec "anahtar halkası kaybolursa hesap `disabled`" diyor. Uygulamıyoruz, çünkü
`Disabled → Verified` dönen bir kod yolu yok ve bağlanmamış tek bir anahtar
dizini **tek koşuda bütün yayıncıları** kalıcı olarak kilitlerdi (aynı gerekçe
`NetgsmAccountService.TryUnprotectPassword` doc'unda yazılı). Yerine: `Status`'e
dokunulmaz, `LastError` yazılır ve panel GET'i bu alanı gösterir — §2.4'ün
asıl talebi olan "sessiz bozulma yok" böyle karşılanır. Anahtar geri geldiğinde
sistem kendiliğinden düzelir.

**3. `BrandCode` tekil indeksi filtrelenir.** Bugünkü indeks
(`LicenseDbContext.cs` ~851) filtresiz: `b.HasIndex(a => a.BrandCode).IsUnique()`.
Bu, marka kodunu yanlış yazan bir yayıncının o kodu **global olarak ve kalıcı
olarak** işgal etmesine izin verir; gerçek sahibi kaydolmaya çalıştığında
`DbUpdateException` alır ve kendi kurulumunu asla tamamlayamaz. Görev 7 indeksi
`WHERE [Status] = 'Verified'` ile filtreler. Güvenlidir: marka→hesap arayan her
sorgu (`GetBrandCodeAsync`, `GetVerifiedByLicenseAsync`, `ListVerifiedAsync`)
zaten `Verified` süzüyor.

---

## Dosya yapısı

**Yeni:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs` | Tek bir hesabı `/iys/search` ile doğrular; sonucu **üç** sonuca sınıflar (Ok / Rejected / Unavailable). |
| `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifyJob.cs` | Günlük yeniden doğrulama; `Verified` hesapları dolaşır, düşeni `Failed`'a çeker. |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs` | Yayıncının kendi kurulum ucu: GET + PUT. |
| `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml(.cs)` | Admin kill switch: `Disabled` / yeniden açma, denetim kaydıyla. |
| `OrderDeck.LicenseServer/Data/Migrations/*_NetgsmBrandCodeFilteredUnique.cs` | Filtreli tekil indeks göçü. |

**Değişecek:**

| Dosya | Değişiklik |
|---|---|
| `Services/Sms/NetgsmAccountService.cs` | `UpsertAsync` + `ListVerifiedIdsAsync` + `ResumePausedCampaignsAsync` eklenir. |
| `Data/LicenseDbContext.cs` (~851) | `BrandCode` indeksine `HasFilter` eklenir. |
| `Domain/SmsCampaign.cs` (32. satır) | `Status` doc'una `"paused"` eklenir. |
| `Services/Sms/SmsCampaignSendJob.cs` | Alıcı döngüsünde duraklama yoklaması. |
| `Services/IntakeForm/IntakeFormService.cs` | `IsSmsConsentEnabledAsync`. |
| `Pages/Public/IntakeForm.cshtml(.cs)` | SMS onay kutusu koşullu görünür. |
| `Services/Audit/AuditEvents.cs` | Netgsm olay + hedef sabitleri. |
| `Pages/Admin/Index.cshtml` | Yönetici panosundan Netgsm sayfasına bağlantı. |
| `OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs` | `OnSent` kancası (koşu ortasında duraklatma testi için). |
| `Program.cs` | DI kayıtları + günlük cron. |

**Kapsam dışı:** Spec §2.1'deki panel uyarı şeridi React tarafında
(`OrderDeck-Mobile` deposu, `apps/panel`). Bu plan sunucu ucunu (`GET`
yanıtındaki `status` + `lastError`) sağlar; şeridi çizmek ayrı depoda ayrı bir iştir.

---

## Görev 1: `NetgsmAccountVerifier` — kabul ve kesin ret

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifierTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifierTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Doğrulayıcı üç sonuç üretir, iki değil. "Reddedildi" ile "şu an
/// ulaşamadım"ı ayırmak kritik: ikisini birleştirirsek İYS'nin geçici bir
/// arızası çalışan her yayıncının kurulumunu sessizce kapatır.
/// </summary>
public sealed class NetgsmAccountVerifierTests
{
    /// <summary>Kimlik-benzeri sabit metin YASAK (depo public, GitGuardian PR
    /// check'i sabit bir fixture ile gerçek bir sırrı ayırt edemiyor).</summary>
    private static IysAccountContext NewAccount() => new(
        LicenseId: Guid.NewGuid(),
        UserCode: Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        Password: $"pw-{Guid.NewGuid():N}",
        BrandCode: Random.Shared.Next(100_000, 999_999).ToString());

    private static NetgsmAccountVerifier Verifier(IIysClient client)
        => new(client, NullLogger<NetgsmAccountVerifier>.Instance);

    [Fact]
    public async Task Kod_sifir_donerse_Ok()
    {
        var client = new StubIysClient(_ => new IysSearchResult(
            "0", "{\"code\":\"0\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Ok);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Sorgu_sabit_prob_numarasiyla_yapilir()
    {
        // Doğrulama gerçek bir kişinin numarasını KULLANMAMALI: /iys/search
        // salt-okunur olsa da yayıncının müşteri listesinden rastgele bir
        // numara seçmek, doğrulama günlüklerine ilgisiz bir kişiyi düşürür.
        List<string>? seen = null;
        var client = new StubIysClient(r =>
        {
            seen = r.ToList();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });

        await Verifier(client).VerifyAsync(NewAccount());

        seen.Should().ContainSingle().Which.Should().Be(NetgsmAccountVerifier.ProbeRecipient);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("60")]
    public async Task Yapilandirma_hatasi_Rejected(string code)
    {
        var client = new StubIysClient(_ => throw new IysConfigurationException(
            code, $"İYS yapılandırma hatası (code={code})"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Rejected);
        result.Message.Should().NotBeNullOrWhiteSpace(
            "yayıncı panelde ne düzelteceğini okuyabilmeli");
    }

    [Fact]
    public async Task Red_mesaji_ham_IYS_govdesini_tasimaz()
    {
        // Ham gövde İYS header'ında API ŞİFRESİNİ taşıyor. LastError panele
        // dönüyor ve DB'de duruyor — oraya ham gövde sızarsa şifre, şifrelenmiş
        // sütunun yanındaki düz metin bir sütuna kopyalanmış olur.
        var secret = $"pw-{Guid.NewGuid():N}";
        var client = new StubIysClient(_ => throw new IysConfigurationException(
            "30", $"ham gövde: {{\"password\":\"{secret}\"}}"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Message.Should().NotContain(secret);
    }

    private sealed class StubIysClient : IIysClient
    {
        private readonly Func<IReadOnlyList<string>, IysSearchResult> _search;
        public StubIysClient(Func<IReadOnlyList<string>, IysSearchResult> search) => _search = search;

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Doğrulama yalnız search kullanır.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(_search(recipients));
    }
}
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: derleme hatası — `NetgsmAccountVerifier`, `NetgsmVerifyOutcome` yok.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Doğrulama sonucu. <b>Üç</b> değer, iki değil — gerekçe için
/// <see cref="NetgsmAccountVerifier"/>.</summary>
public enum NetgsmVerifyOutcome
{
    /// <summary>Abone no + şifre + marka kodu üçlüsü İYS tarafından kabul edildi.</summary>
    Ok,

    /// <summary>İYS kimliği/markayı KESİN olarak reddetti (code 30/60).</summary>
    Rejected,

    /// <summary>Şu an cevap alınamadı. Hesap hakkında hiçbir şey öğrenilmedi.</summary>
    Unavailable,
}

/// <param name="Message">Panelde yayıncıya gösterilecek insan okunur açıklama.
/// <b>Ham İYS gövdesi buraya yazılmaz</b> — gövde API şifresini taşıyor.</param>
public sealed record NetgsmVerifyResult(NetgsmVerifyOutcome Outcome, string? Message);

/// <summary>
/// Bir Netgsm/İYS kurulumunu tek <c>/iys/search</c> çağrısıyla doğrular.
/// Abone no, API şifresi ve marka kodu gövdede birlikte gittiği için tek çağrı
/// üçünü birden sınar (spec §2.2). <b>Test SMS'i atılmaz.</b>
///
/// <para><b>Neden üç sonuç.</b> "Reddedildi" ile "ulaşamadım" aynı kovaya
/// düşerse, İYS'nin yarım saatlik bir arızası günlük işin o turunda çalışan
/// BÜTÜN yayıncıları <c>Failed</c>'a çeker: kampanyalar durur, onay toplama
/// durur ve hiçbiri kendiliğinden geri gelmez (yayıncının panele girip
/// kaydetmesi gerekir). Yalnız <see cref="IysConfigurationException"/> kesin
/// karardır; başka her şey <see cref="NetgsmVerifyOutcome.Unavailable"/>'dır
/// ve hesabın durumuna DOKUNMAZ.</para>
///
/// <para><b>Başlık doğrulanmaz.</b> <c>Header</c> bu çağrının payload'ında yok;
/// Netgsm onaysız başlığı yalnız gönderim anında reddediyor. <c>Verified</c>
/// "başlık onaylı" anlamına GELMEZ.</para>
/// </summary>
public sealed class NetgsmAccountVerifier
{
    /// <summary>
    /// Doğrulama sorgusunun alıcısı. <c>/iys/search</c> en az bir alıcı istiyor
    /// ama salt-okunur: hiçbir onay oluşturmaz, değiştirmez.
    ///
    /// <para>Sabit ve <b>tahsis edilmemiş</b> bir numara seçildi (TR'de 500
    /// operatör öneki kullanımda değil). Gerçek bir müşterinin numarasını
    /// kullanmak, o kişiyi ilgisiz bir doğrulama turunun günlüklerine
    /// düşürürdü — KVKK'da veri minimizasyonunun tam tersi.</para>
    /// </summary>
    public const string ProbeRecipient = "+905000000000";

    private readonly IIysClient _iys;
    private readonly ILogger<NetgsmAccountVerifier> _log;

    public NetgsmAccountVerifier(IIysClient iys, ILogger<NetgsmAccountVerifier> log)
    {
        _iys = iys;
        _log = log;
    }

    public async Task<NetgsmVerifyResult> VerifyAsync(
        IysAccountContext account, CancellationToken ct = default)
    {
        var result = await _iys.SearchAsync(account, new[] { ProbeRecipient }, ct);
        return result.Code == "0"
            ? new NetgsmVerifyResult(NetgsmVerifyOutcome.Ok, null)
            : new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                $"İYS beklenmeyen yanıt kodu döndürdü ({result.Code}). Sorun sürerse Netgsm'e danışın.");
    }
}
```

Bu hâliyle `IysConfigurationException` yakalanmıyor — Adım 1'deki `Theory`
düşecek. Yakalamayı ekle:

```csharp
        try
        {
            var result = await _iys.SearchAsync(account, new[] { ProbeRecipient }, ct);
            return result.Code == "0"
                ? new NetgsmVerifyResult(NetgsmVerifyOutcome.Ok, null)
                : new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                    $"İYS beklenmeyen yanıt kodu döndürdü ({result.Code}). Sorun sürerse Netgsm'e danışın.");
        }
        catch (IysConfigurationException ex)
        {
            // ex.Message'ı DEĞİL sabit metni döndürüyoruz: istisna mesajı ileride
            // ham gövdeyi taşımaya başlarsa şifre LastError'a sızardı.
            _log.LogWarning("Netgsm doğrulaması reddedildi: lisans={LicenseId} kod={Code}",
                account.LicenseId, ex.Code);
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Rejected, ex.Code switch
            {
                "60" => "İYS marka kodu bu Netgsm hesabına ait değil (kod 60). "
                        + "Marka kodunu İYS panelinden kontrol edin.",
                _ => "Netgsm abone numarası veya API şifresi reddedildi (kod 30).",
            });
        }
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: PASS (5 test — `Theory` iki kez).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifierTests.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): hesap doğrulayıcı — /iys/search ile üç sonuçlu sınıflandırma

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 2: Geçici arıza hesabı düşürmez

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifierTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`NetgsmAccountVerifierTests` sınıfının içine, `StubIysClient` tanımından ÖNCE:

```csharp
    [Fact]
    public async Task Ag_hatasi_Unavailable()
    {
        var client = new StubIysClient(_ => throw new HttpRequestException("bağlantı yok"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable,
            "ağ arızası hesap hakkında HİÇBİR ŞEY söylemez; Rejected desek "
            + "İYS'nin yarım saatlik kesintisi çalışan her yayıncıyı kapatırdı");
    }

    [Fact]
    public async Task Zaman_asimi_Unavailable()
    {
        var client = new StubIysClient(_ => throw new TaskCanceledException("timeout"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
    }

    [Fact]
    public async Task Sistem_hatasi_kodu_Unavailable()
    {
        // İYS "100 = sistem hatası" gibi kodlar da döndürüyor. Bunlar
        // yapılandırmayla ilgili DEĞİL; NetgsmIysClient yalnız 30/60'ı
        // IysConfigurationException'a çeviriyor, gerisi buraya düz kod olarak
        // geliyor ve hesabı düşürmemeli.
        var client = new StubIysClient(_ => new IysSearchResult(
            "100", "{\"code\":100}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
    }

    [Fact]
    public async Task Iptal_istegi_yutulmaz()
    {
        // Uygulama kapanırken CancellationToken tetiklenir. Bunu Unavailable'a
        // çevirip yutarsak, kapanış turunda her hesaba "ulaşılamadı" yazar ve
        // LastError'lar gerçek bir sorun varmış gibi görünür.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new StubIysClient(_ => throw new TaskCanceledException("shutdown"));

        var act = async () => await Verifier(client).VerifyAsync(NewAccount(), cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: `Ag_hatasi_Unavailable` ve `Zaman_asimi_Unavailable` FAIL
(`HttpRequestException` / `TaskCanceledException` yakalanmadan dışarı çıkıyor).
Diğer ikisi zaten geçer.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`NetgsmAccountVerifier.VerifyAsync` içindeki `catch (IysConfigurationException ex)`
bloğunun ALTINA:

```csharp
        catch (Exception ex) when (ex is HttpRequestException
                                     or TaskCanceledException
                                     or System.Text.Json.JsonException)
        {
            // İptal GERÇEKTEN istendiyse yutma: kapanış turu her hesaba
            // "ulaşılamadı" yazmamalı.
            if (ct.IsCancellationRequested) throw;

            _log.LogWarning(ex, "Netgsm doğrulaması ulaşılamadı: lisans={LicenseId}",
                account.LicenseId);
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                "İYS'ye şu an ulaşılamadı. Kurulumunuz kapatılmadı, doğrulama "
                + "kendiliğinden tekrar denenecek.");
        }
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: PASS (9 test).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifier.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifierTests.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): geçici İYS arızası hesabı düşürmez — Unavailable ayrı sonuç

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 3: `NetgsmAccountService.UpsertAsync`

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`NetgsmAccountServiceTests.cs` dosyasının sonuna (son `}`'tan önce) ekle. Dosya
zaten `IClassFixture<ApiFactory>` kullanıyor; aşağıdaki yardımcılar sınıfın
içine, mevcut testlerin altına gelir:

```csharp
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    /// <summary>Boş bir yayıncı lisansı açar.</summary>
    private async Task<Guid> NewLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ups-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = $"LDK-UPS-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    [Fact]
    public async Task Upsert_yeni_hesabi_DOGRULANMAMIS_acar()
    {
        var licenseId = await NewLicenseAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var acc = await svc.UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "fail-closed: doğrulama henüz koşmadı, satır kapalı doğar");
        acc.LastVerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_sifreyi_sifreli_saklar()
    {
        var licenseId = await NewLicenseAsync();
        var raw = $"pw-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var acc = await svc.UpsertAsync(
            licenseId, NewUserCode(), raw, "ORDERDECK", NewBrandCode(), CancellationToken.None);

        acc.PasswordProtected.Should().NotBe(raw, "düz metin şifre DB'ye yazılmaz");
        svc.TryUnprotectPassword(acc.PasswordProtected).Should().Be(raw);
    }

    [Fact]
    public async Task Upsert_bos_sifreyle_saklananı_korur()
    {
        // Panel şifreyi geri GÖSTERMİYOR (yalnız "girildi/girilmedi").
        // Yayıncı başlığını düzeltmek için formu kaydettiğinde şifre alanı boş
        // gelir; boşu kaydedersek çalışan kurulumu kendi elimizle bozarız.
        var licenseId = await NewLicenseAsync();
        var raw = $"pw-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var userCode = NewUserCode();
        var brandCode = NewBrandCode();

        await svc.UpsertAsync(licenseId, userCode, raw, "ORDERDECK", brandCode, CancellationToken.None);
        var acc = await svc.UpsertAsync(
            licenseId, userCode, null, "YENIBASLIK", brandCode, CancellationToken.None);

        svc.TryUnprotectPassword(acc.PasswordProtected).Should().Be(raw);
        acc.Header.Should().Be("YENIBASLIK");
    }

    [Fact]
    public async Task Upsert_ilk_kayitta_sifre_zorunlu()
    {
        var licenseId = await NewLicenseAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var act = async () => await svc.UpsertAsync(
            licenseId, NewUserCode(), null, "ORDERDECK", NewBrandCode(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Upsert_lisans_basina_tek_satir_tutar()
    {
        var licenseId = await NewLicenseAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var first = await svc.UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);
        var second = await svc.UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        second.Id.Should().Be(first.Id, "ikinci kayıt YENİ satır açmamalı");
        (await db.NetgsmAccounts.CountAsync(a => a.LicenseId == licenseId)).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_alanlarin_bosluklarini_kirpar()
    {
        // Baştaki boşluk marka kodunu tekil indekste AYRI bir anahtar yapıyor
        // (bkz. NetgsmAccountUniqueIndexTests): " 731734" ile "731734" iki ayrı
        // satır olarak durabilir ve marka→hesap araması yalnız birini görür.
        var licenseId = await NewLicenseAsync();
        var brandCode = NewBrandCode();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var acc = await svc.UpsertAsync(
            licenseId, $"  {NewUserCode()} ", $"pw-{Guid.NewGuid():N}",
            " ORDERDECK ", $" {brandCode} ", CancellationToken.None);

        acc.BrandCode.Should().Be(brandCode);
        acc.Header.Should().Be("ORDERDECK");
        acc.UserCode.Should().NotStartWith(" ");
    }
```

Dosyanın `using` bloğunda eksik varsa ekle:
`Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.DependencyInjection`,
`OrderDeck.LicenseServer.Data`, `OrderDeck.LicenseServer.Domain`.

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountServiceTests
```
Beklenen: derleme hatası — `UpsertAsync` yok.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`NetgsmAccountService.cs` içine, `GetBrandCodeAsync`'in ÜSTÜNE:

```csharp
    /// <summary>
    /// Lisansın Netgsm kurulumunu yazar/günceller ve satırı <b>her zaman</b>
    /// <see cref="NetgsmAccountStatus.Failed"/> bırakır.
    ///
    /// <para><b>Neden hep Failed.</b> Kimlik bilgileri değiştiyse eski doğrulama
    /// geçersizdir; <c>Verified</c>'ı korumak, yanlış kimlikle "açık" duran bir
    /// kurulum demektir. Satırı kapalı yazıp doğrulamayı ayrı çalıştırmak aynı
    /// zamanda arada süreç ölse bile fail-closed kalmayı garanti eder.</para>
    ///
    /// <para><paramref name="rawPassword"/> boş/null ise saklanan şifre KORUNUR:
    /// panel şifreyi geri göstermediği için yayıncı başlığını düzeltirken alanı
    /// boş bırakır. İlk kayıtta ise zorunludur.</para>
    ///
    /// <para><b><c>Disabled</c> kontrolü burada DEĞİL.</b> Bu metot admin kill
    /// switch'ini tanımaz; kapıyı çağıran uç (panel controller) tutar. Gerekçe:
    /// admin ekranı da aynı metodu kullanabilmeli.</para>
    /// </summary>
    public async Task<NetgsmAccount> UpsertAsync(
        Guid licenseId, string userCode, string? rawPassword,
        string header, string brandCode, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        if (acc is null)
        {
            if (string.IsNullOrWhiteSpace(rawPassword))
                throw new ArgumentException(
                    "İlk kayıtta Netgsm API şifresi zorunlu.", nameof(rawPassword));

            acc = new NetgsmAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreatedAt = now,
            };
            _db.NetgsmAccounts.Add(acc);
        }

        acc.UserCode = userCode.Trim();
        acc.Header = header.Trim();
        acc.BrandCode = brandCode.Trim();
        if (!string.IsNullOrWhiteSpace(rawPassword))
            acc.PasswordProtected = ProtectPassword(rawPassword);

        acc.Status = NetgsmAccountStatus.Failed;
        acc.LastError = null;
        acc.LastVerifiedAt = null;
        acc.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return acc;
    }

    /// <summary>
    /// Doğrulanmış hesapların <b>yalnız kimlikleri</b>. Günlük doğrulama işi
    /// listeyi dolaşırken her turda satırı taze okur; nesneyi taşımak,
    /// <see cref="ListVerifiedAsync"/> doc'unda anlatılan detach tuzağını
    /// buraya da çağırırdı.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListVerifiedIdsAsync(CancellationToken ct)
        => await _db.NetgsmAccounts
            .AsNoTracking()
            .Where(a => a.Status == NetgsmAccountStatus.Verified)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id)
            .ToListAsync(ct);
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountServiceTests
```
Beklenen: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): UpsertAsync — kurulum satırı kapalı doğar, boş şifre saklananı korur

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 4: Panel `GET /api/panel/netgsm/account`

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs`

> **Desen kaynağı:** `Controllers/Panel/PanelWhatsAppAccountController.cs` —
> `[Authorize(AuthenticationSchemes = "Bearer-Customer")]`, `OwnerOnly(...)`,
> `PanelLicenseScope.ResolveAsync`, sırların panele DÖNMEMESİ. Test tarafında
> `Controllers/Panel/PanelWhatsAppAccountControllerTests.cs` kopyalanacak kalıp.

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm/İYS kurulum ucu. İki değişmez korunuyor:
/// (1) API şifresi panele ASLA dönmez, (2) uç owner-only — staff operatör
/// yayıncının SMS kimliklerini göremez/değiştiremez.
/// </summary>
public sealed class PanelNetgsmAccountControllerTests : IDisposable
{
    private readonly List<ApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private ApiFactory NewFactory()
    {
        var f = new ApiFactory();
        _factories.Add(f);
        return f;
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, ApiFactory Factory);

    private async Task<Seed> SeedTenantAsync(ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNA-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, factory);
    }

    private static async Task SeedAccountAsync(
        ApiFactory factory, Guid licenseId, NetgsmAccountStatus status,
        string? lastError = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = protector.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            LastError = lastError,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Hesap_yoksa_bos_gorunum_doner()
    {
        var seed = await SeedTenantAsync(NewFactory());

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "kurulumu olmayan yayıncı 404 değil BOŞ form görmeli");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("none");
        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Sifre_panele_DONMEZ()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().NotContain("assword",
            "şifre alanı adıyla bile dönmemeli; yalnız passwordSet bayrağı var");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Failed_hesapta_sms_kapali_ve_lastError_gorunur()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Failed,
            lastError: "İYS marka kodu bu Netgsm hesabına ait değil (kod 60).");

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("lastError").GetString().Should().Contain("kod 60");
    }

    [Fact]
    public async Task Staff_operator_goremez()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, seed.Client);

        var resp = await staff.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "SMS kimlikleri yayıncının kendi faturalı hesabı — staff görmez");
    }

    [Fact]
    public async Task Aktif_lisansi_olmayan_musteri_400_alir()
    {
        var factory = NewFactory();
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

> `PanelOperatorHelper.StaffClientAsync` HENÜZ YOK — Adım 3'te yazılıyor.

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelNetgsmAccountControllerTests
```
Beklenen: derleme hatası — `PanelOperatorHelper` yok; sonra 404'ler.

- [ ] **Adım 3: Yardımcıyı ve controller'ı yaz**

`OrderDeck.LicenseServer.Tests/TestHelpers/PanelOperatorHelper.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Owner istemcisinden bir <c>staff</c> operatör açar ve o operatörün jetonunu
/// taşıyan istemciyi döndürür. Owner-only uçların 403 döndürdüğünü kanıtlamak
/// için gereken tek kurulum bu.
/// (Kalıp: <c>PanelWhatsAppAccountControllerTests.StaffClientAsync</c>.)
/// </summary>
public static class PanelOperatorHelper
{
    public static async Task<HttpClient> StaffClientAsync(ApiFactory factory, HttpClient ownerClient)
    {
        var email = $"staff-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}";

        var create = await ownerClient.PostAsJsonAsync(
            "/api/panel/operators", new { email, password, role = "staff", name = "Staff" });
        create.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/operator-login", new { email, password });
        login.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
```

> **Not:** Gövde alan adlarını (`role`, `accessToken`) `PanelWhatsAppAccountControllerTests`
> içindeki mevcut `StaffClientAsync` kopyasıyla birebir karşılaştır; sözleşme
> orada zaten çalışıyor.

`OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm + İYS kurulumu (spec §2). Dört alan girilir;
/// kaydetme anında <c>/iys/search</c> ile senkron doğrulanır.
///
/// <para><b>Owner-only.</b> Bunlar yayıncının faturalı Netgsm hesabının
/// kimlikleri; staff operatör ne görür ne değiştirir.</para>
///
/// <para><b>Şifre tek yön.</b> Panel yalnız <c>passwordSet</c> bayrağını
/// görür. Geri okunabilen bir alan, panel oturumu ele geçiren birine
/// yayıncının Netgsm hesabını da verirdi.</para>
/// </summary>
[ApiController]
[Route("api/panel/netgsm/account")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelNetgsmAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelNetgsmAccountController(LicenseDbContext db) => _db = db;

    /// <param name="Status">none | failed | verified | disabled.</param>
    /// <param name="SmsEnabled">Yetki tablosunun (spec §2.1) tek cevabı:
    /// kampanya ve onay toplama yalnız bu true iken açık.</param>
    public sealed record AccountView(
        string Status,
        bool SmsEnabled,
        string? UserCode,
        string? Header,
        string? BrandCode,
        bool PasswordSet,
        string? LastError,
        DateTimeOffset? LastVerifiedAt);

    private IActionResult? OwnerOnly() =>
        User.IsOperator()
            ? Problem(title: "owner-only",
                detail: "Netgsm kurulumunu yalnız hesap sahibi görüntüleyip değiştirebilir.",
                statusCode: 403)
            : null;

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        if (OwnerOnly() is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var acc = await _db.NetgsmAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        return Ok(ToView(acc));
    }

    internal static AccountView ToView(NetgsmAccount? acc) => acc is null
        ? new AccountView("none", false, null, null, null, false, null, null)
        : new AccountView(
            Status: acc.Status switch
            {
                NetgsmAccountStatus.Verified => "verified",
                NetgsmAccountStatus.Disabled => "disabled",
                _ => "failed",
            },
            SmsEnabled: acc.Status == NetgsmAccountStatus.Verified,
            UserCode: acc.UserCode,
            Header: acc.Header,
            BrandCode: acc.BrandCode,
            PasswordSet: !string.IsNullOrEmpty(acc.PasswordProtected),
            LastError: acc.LastError,
            LastVerifiedAt: acc.LastVerifiedAt);
}
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelNetgsmAccountControllerTests
```
Beklenen: PASS (5 test).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/PanelOperatorHelper.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(panel): Netgsm kurulum görüntüleme ucu — owner-only, şifre dönmez

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 5: Panel `PUT` — senkron doğrulama

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountSaveTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountSaveTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Kaydetme anı: satır önce KAPALI yazılır, sonra tek bir /iys/search
/// çağrısıyla doğrulanır. Doğrulanana kadar (ve doğrulanamazsa) SMS ve onay
/// toplama kapalıdır — spec §2.1/§2.2.
/// </summary>
public sealed class PanelNetgsmAccountSaveTests : IDisposable
{
    private readonly List<NetgsmApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Doğrulama çağrısını yönlendirilebilir kılar: her test kendi
    /// İYS cevabını kurar.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public Func<IysSearchResult> OnSearch { get; set; } =
            () => new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());

        public int SearchCalls { get; private set; }

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(OnSearch());
        }
    }

    private sealed class NetgsmApiFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private NetgsmApiFactory NewFactory()
    {
        var f = new NetgsmApiFactory();
        _factories.Add(f);
        return f;
    }

    private static object NewBody(string? password = null) => new
    {
        userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        password = password ?? $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode = Random.Shared.Next(100_000, 999_999).ToString(),
    };

    private async Task<(NetgsmApiFactory Factory, HttpClient Client, Guid LicenseId)> SeedAsync()
    {
        var factory = NewFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNS-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (factory, client, license.Id);
    }

    [Fact]
    public async Task Iys_kabul_ederse_verified_olur()
    {
        var (factory, client, licenseId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Iys.SearchCalls.Should().Be(1, "doğrulama SENKRON koşar, kuyruğa alınmaz");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Verified);
        acc.LastVerifiedAt.Should().NotBeNull();
        acc.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Iys_reddederse_failed_kalir_ve_sebep_yazilir()
    {
        var (factory, client, licenseId) = await SeedAsync();
        factory.Iys.OnSearch = () => throw new IysConfigurationException("60", "marka yok");

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "kaydetme başarılı — kimlik saklandı, yalnız DOĞRULANMADI; 4xx "
            + "dönersek yayıncı girdiği değerleri kaybeder ve düzeltemez");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("lastError").GetString().Should().Contain("60");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed);
    }

    [Fact]
    public async Task Iys_ulasilamazsa_failed_kalir()
    {
        // Yeni kayıt zaten kapalı doğuyor; ulaşılamayan doğrulama onu AÇMAZ.
        // (Çalışan bir hesabın geçici arızada düşmemesi Görev 8'de.)
        var (factory, client, _) = await SeedAsync();
        factory.Iys.OnSearch = () => throw new HttpRequestException("bağlantı yok");

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        doc.RootElement.GetProperty("lastError").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Marka_kodu_rakam_disi_ise_400()
    {
        var (_, client, _) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", new
        {
            userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            password = $"pw-{Guid.NewGuid():N}",
            header = "ORDERDECK",
            brandCode = "73A734",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "DB check constraint'i (CK_NetgsmAccounts_BrandCode) zaten kesiyor "
            + "ama oraya varmak 500 üretirdi; kapıda anlaşılır hata dönmeli");
    }

    [Fact]
    public async Task Staff_operator_kaydedemez()
    {
        var (factory, client, _) = await SeedAsync();
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, client);

        var resp = await staff.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Iys.SearchCalls.Should().Be(0, "yetkisiz istek dış çağrı TETİKLEMEMELİ");
    }
}
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelNetgsmAccountSaveTests
```
Beklenen: 405/404 — `PUT` ucu yok.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`PanelNetgsmAccountController` sınıfına ekle; `_db` yanına bağımlılıkları da al:

```csharp
    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmAccountVerifier _verifier;

    public PanelNetgsmAccountController(
        LicenseDbContext db, NetgsmAccountService accounts, NetgsmAccountVerifier verifier)
    {
        _db = db;
        _accounts = accounts;
        _verifier = verifier;
    }

    public sealed record SaveRequest(
        string UserCode, string? Password, string Header, string BrandCode);

    [HttpPut]
    public async Task<IActionResult> SaveAsync([FromBody] SaveRequest req, CancellationToken ct)
    {
        if (OwnerOnly() is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var userCode = (req.UserCode ?? "").Trim();
        var header = (req.Header ?? "").Trim();
        var brandCode = (req.BrandCode ?? "").Trim();

        if (userCode.Length is 0 or > 32)
            return Problem(title: "invalid-user-code",
                detail: "Netgsm abone numarası zorunlu (en fazla 32 karakter).", statusCode: 400);
        if (header.Length is 0 or > 11)
            return Problem(title: "invalid-header",
                detail: "Gönderici başlığı zorunlu (en fazla 11 karakter).", statusCode: 400);
        // Rakam dışı karakteri kapıda kesiyoruz: DB'deki
        // CK_NetgsmAccounts_BrandCode aynı kuralı uyguluyor ama oraya varmak
        // DbUpdateException → 500 demek olurdu.
        if (brandCode.Length is 0 or > 16 || !brandCode.All(char.IsAsciiDigit))
            return Problem(title: "invalid-brand-code",
                detail: "İYS marka kodu yalnız rakamlardan oluşur.", statusCode: 400);

        NetgsmAccount account;
        try
        {
            account = await _accounts.UpsertAsync(
                licenseId.Value, userCode, req.Password, header, brandCode, ct);
        }
        catch (ArgumentException)
        {
            return Problem(title: "password-required",
                detail: "İlk kayıtta Netgsm API şifresi zorunlu.", statusCode: 400);
        }

        // Satır şu an Failed: doğrulama düşse bile kapı KAPALI kalır.
        var password = _accounts.TryUnprotectPassword(account.PasswordProtected);
        if (password is null)
        {
            account.LastError = NetgsmAccountService.UndecryptableMessage;
            await _db.SaveChangesAsync(ct);
            return Ok(ToView(account));
        }

        var result = await _verifier.VerifyAsync(
            new Services.Iys.IysAccountContext(licenseId.Value, userCode, password, brandCode), ct);

        if (result.Outcome == NetgsmVerifyOutcome.Ok)
        {
            account.Status = NetgsmAccountStatus.Verified;
            account.LastVerifiedAt = DateTimeOffset.UtcNow;
            account.LastError = null;
        }
        else
        {
            account.LastError = result.Message;
        }
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(ToView(account));
    }
```

`NetgsmAccountService`'e sabiti ekle (Görev 9 da kullanacak):

```csharp
    /// <summary>Şifre çözülemediğinde panelde gösterilen metin. Hesabın
    /// <c>Status</c>'üne DOKUNULMAZ — gerekçe
    /// <see cref="TryUnprotectPassword"/> doc'unda.</summary>
    public const string UndecryptableMessage =
        "Saklı Netgsm şifresi çözülemedi. Kimlik bilgilerini panelden tekrar girin.";
```

`using System.Linq;` gerekirse ekle (`brandCode.All`).

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelNetgsmAccount"
```
Beklenen: PASS (10 test — Görev 4'ün 5'i + buradaki 5).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs \
        OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountSaveTests.cs
git commit -m "$(cat <<'EOF'
feat(panel): Netgsm kaydetme ucu — /iys/search ile senkron doğrulama

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 6: `Disabled` yapışkan + marka işgali ön kontrolü

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountSaveTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`PanelNetgsmAccountSaveTests` sınıfına ekle:

```csharp
    private static async Task SetStatusAsync(
        NetgsmApiFactory factory, Guid licenseId, NetgsmAccountStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);
        acc.Status = status;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Disabled_hesap_panelden_yeniden_acilamaz()
    {
        var (factory, client, licenseId) = await SeedAsync();
        (await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody()))
            .EnsureSuccessStatusCode();
        await SetStatusAsync(factory, licenseId, NetgsmAccountStatus.Disabled);
        factory.Iys.OnSearch = () => new IysSearchResult(
            "0", "{}", new Dictionary<string, IysConsentStatus>());
        var callsBefore = factory.Iys.SearchCalls;

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "disabled bir ADMIN kararıdır; yayıncı kaydet'e basarak geri alamaz");
        factory.Iys.SearchCalls.Should().Be(callsBefore, "kapalı hesap dış çağrı tetiklemez");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled);
    }

    [Fact]
    public async Task Baska_lisansin_dogrulanmis_markasi_409()
    {
        // Marka kodu global tekil. Ön kontrol olmasaydı DbUpdateException →
        // 500 dönerdi ve yayıncı ne olduğunu anlamazdı.
        var factory = NewFactory();
        var (clientA, customerA, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var (clientB, customerB, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            foreach (var customerId in new[] { customerA, customerB })
                db.Licenses.Add(new License
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    LicenseKey = "LDK-SQ-" + Guid.NewGuid().ToString("N")[..12],
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                });
            await db.SaveChangesAsync();
        }

        object Body() => new
        {
            userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            password = $"pw-{Guid.NewGuid():N}",
            header = "ORDERDECK",
            brandCode,
        };

        (await clientA.PutAsJsonAsync("/api/panel/netgsm/account", Body()))
            .EnsureSuccessStatusCode();

        var resp = await clientB.PutAsJsonAsync("/api/panel/netgsm/account", Body());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("title").GetString().Should().Be("brand-code-taken");
    }
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~PanelNetgsmAccountSaveTests
```
Beklenen: iki yeni test FAIL — biri 200, öbürü 200/500.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`SaveAsync` içinde, doğrulama bloklarının ALTINA ve `UpsertAsync` çağrısının
ÜSTÜNE:

```csharp
        var existing = await _db.NetgsmAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        if (existing?.Status == NetgsmAccountStatus.Disabled)
            return Problem(title: "netgsm-account-disabled",
                detail: "Netgsm kurulumunuz yönetici tarafından kapatıldı. "
                        + "Yeniden açılması için destekle iletişime geçin.",
                statusCode: 409);

        // Marka kodu DOĞRULANMIŞ hesaplar arasında tekil (filtreli indeks,
        // Görev 7). Dış çağrıdan ÖNCE bakıyoruz: yoksa yayıncının tek
        // öğreneceği şey bir 500 olurdu.
        var squatted = await _db.NetgsmAccounts.AsNoTracking().AnyAsync(
            a => a.BrandCode == brandCode
                 && a.Status == NetgsmAccountStatus.Verified
                 && a.LicenseId != licenseId.Value, ct);
        if (squatted)
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);
```

Ve `SaveChangesAsync` çağrılarını yarış payı için sar — `Verified`'a yükselten
blok ile son kayıt arasında başka bir kiracı aynı markayı doğrulamış olabilir:

```csharp
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Ön kontrol ile bu kayıt arasında başka kiracı aynı markayı
            // doğruladı. Filtreli tekil indeks kesin kararı verdi; bizimki
            // Failed kalmalı ve yayıncı anlaşılır bir cevap almalı.
            _db.ChangeTracker.Clear();
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);
        }
```

> **Not:** InMemory sağlayıcı tekil indeks uygulamaz; bu `catch` yalnız gerçek
> SQL Server'da tetiklenir ve Görev 7'deki Testcontainers testiyle kanıtlanır.
> Yukarıdaki `Baska_lisansin_dogrulanmis_markasi_409` testi ön kontrolü sınar.

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelNetgsmAccount"
```
Beklenen: PASS (12 test).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelNetgsmAccountSaveTests.cs
git commit -m "$(cat <<'EOF'
feat(panel): disabled kurulum yapışkan, işgal edilmiş marka kodu 409

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 7: `BrandCode` tekil indeksini filtrele

**Files:**
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs:851`
- Create: `OrderDeck.LicenseServer/Data/Migrations/<zaman>_NetgsmBrandCodeFilteredUnique.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs`

> **Sorun.** Bugünkü indeks filtresiz. Marka kodunu yanlış yazan bir yayıncı
> (satırı `Failed` bile olsa) o kodu **global ve kalıcı** olarak işgal eder;
> gerçek sahibi kaydolmaya çalıştığında `DbUpdateException` alır ve kurulumunu
> hiç tamamlayamaz. Filtre bunu kapatır.
>
> **Testcontainers gerekiyor.** InMemory sağlayıcı tekil indeks UYGULAMAZ —
> bu davranış yalnız gerçek SQL Server'da kanıtlanabilir. Docker açık olmalı;
> yerelde düşerse `DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine`
> (iki eğik çizgi) ile PowerShell'den koş.

- [ ] **Adım 1: Düşen testi yaz**

`NetgsmAccountUniqueIndexTests.cs` dosyasındaki `Row` yardımcısını durumu
alacak şekilde genişlet (mevcut çağrılar varsayılanla çalışmaya devam eder):

```csharp
    private static NetgsmAccount Row(
        Guid licenseId, string brandCode,
        NetgsmAccountStatus status = NetgsmAccountStatus.Verified) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        PasswordProtected = $"pw-{Guid.NewGuid():N}",
        Header = $"OD{Guid.NewGuid():N}"[..11],
        BrandCode = brandCode,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
```

Sınıfın sonuna iki test ekle:

```csharp
    [Fact]
    public async Task Dogrulanmamis_satir_marka_kodunu_ISGAL_ETMEZ()
    {
        // Yayıncı A marka kodunu yanlış yazdı (satırı Failed). Filtresiz
        // indekste bu kod global olarak yanardı ve gerçek sahibi B kendi
        // kurulumunu ASLA tamamlayamazdı — kendi hatası olmayan, kendi
        // düzeltemeyeceği kalıcı bir kilit.
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, brandCode, NetgsmAccountStatus.Failed));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, brandCode, NetgsmAccountStatus.Verified));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync(
                "yalnız DOĞRULANMIŞ satırlar markayı sahiplenir");
        }
    }

    [Fact]
    public async Task Disabled_satir_marka_kodunu_SERBEST_BIRAKIR()
    {
        // Kill switch'le kapatılan yayıncının markası, aynı markayı gerçekten
        // İYS'de doğrulayabilen bir hesabı engellemeye devam etmemeli.
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, brandCode, NetgsmAccountStatus.Disabled));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, brandCode, NetgsmAccountStatus.Verified));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }
    }
```

> Mevcut `Ayni_marka_kodu_iki_lisansa_verilemez` testi iki `Verified` satır
> kurduğu için filtreden SONRA da geçmeye devam etmeli — indeksin hâlâ iş
> yaptığının kanıtı odur, silme.

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountUniqueIndexTests
```
Beklenen: iki yeni test FAIL (`DbUpdateException` — filtresiz indeks çakışıyor).
Docker kapalıysa hepsi düşer; önce Docker'ı başlat.

- [ ] **Adım 3: Eşlemeyi ve göçü yaz**

`LicenseDbContext.cs` (~851. satır):

```csharp
        // Marka kodu YALNIZ doğrulanmış hesaplar arasında tekil. Filtresiz
        // olsaydı marka kodunu yanlış yazan bir yayıncı o kodu global ve kalıcı
        // olarak işgal eder, gerçek sahibi kendi kurulumunu hiç tamamlayamazdı.
        // Güvenli: marka→hesap arayan her sorgu (GetBrandCodeAsync,
        // GetVerifiedByLicenseAsync, ListVerifiedAsync) zaten Verified süzüyor,
        // yani indeksin kapsamı aramanın kapsamıyla birebir.
        b.HasIndex(a => a.BrandCode).IsUnique().HasFilter("[Status] = 'Verified'");
```

Göçü üret:

```bash
dotnet ef migrations add NetgsmBrandCodeFilteredUnique \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Üretilen dosyanın `Up` metodu şuna denk olmalı (değilse elle düzelt):

```csharp
        migrationBuilder.DropIndex(
            name: "IX_NetgsmAccounts_BrandCode",
            table: "NetgsmAccounts");

        migrationBuilder.CreateIndex(
            name: "IX_NetgsmAccounts_BrandCode",
            table: "NetgsmAccounts",
            column: "BrandCode",
            unique: true,
            filter: "[Status] = 'Verified'");
```

`Down` bunun tersi (filtresiz yeniden kurar).

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountUniqueIndexTests
```
Beklenen: PASS (6 test — mevcut 4 + yeni 2).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations/ \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountUniqueIndexTests.cs
git commit -m "$(cat <<'EOF'
fix(netgsm): marka kodu tekilliği yalnız doğrulanmış hesaplara uygulanır

Doğrulanmamış bir satır marka kodunu global olarak işgal ediyor ve gerçek
sahibinin kurulumunu kalıcı biçimde kilitliyordu.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 8: `NetgsmAccountVerifyJob` — günlük yeniden doğrulama

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifyJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifyJobTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifyJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Spec §2.3: tek seferlik doğrulama yetmez — abonelik biter, şifre döner.
/// Günlük iş her doğrulanmış hesabı tekrar sınar. Asıl tehlike aşırı tepki:
/// İYS'nin geçici arızasında bütün yayıncıları kapatmak.
/// </summary>
public sealed class NetgsmAccountVerifyJobTests : IDisposable
{
    private readonly List<JobFactory> _factories = new();
    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private sealed class StubIysClient : IIysClient
    {
        /// <summary>Marka kodu → o marka için verilecek cevap.</summary>
        public Dictionary<string, Func<IysSearchResult>> ByBrand { get; } = new();

        public Func<IysSearchResult> Default { get; set; } =
            () => new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());

        public List<string> Asked { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            Asked.Add(account.BrandCode);
            var fn = ByBrand.TryGetValue(account.BrandCode, out var f) ? f : Default;
            return Task.FromResult(fn());
        }
    }

    private sealed class JobFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private JobFactory NewFactory()
    {
        var f = new JobFactory();
        _factories.Add(f);
        return f;
    }

    /// <summary>Doğrulanmış bir hesap tohumlar, marka kodunu döner.</summary>
    private static async Task<string> SeedVerifiedAsync(JobFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"vj-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = $"LDK-VJ-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return brandCode;
    }

    private static async Task<NetgsmAccount> ReadAsync(JobFactory factory, string brandCode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.BrandCode == brandCode);
    }

    private static async Task RunAsync(JobFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<NetgsmAccountVerifyJob>();
        await job.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Gecerli_hesabin_damgasi_tazelenir()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        var before = (await ReadAsync(factory, brandCode)).LastVerifiedAt;

        await RunAsync(factory);

        var after = await ReadAsync(factory, brandCode);
        after.Status.Should().Be(NetgsmAccountStatus.Verified);
        after.LastVerifiedAt.Should().BeAfter(before!.Value);
        after.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Reddedilen_hesap_Failed_olur()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[brandCode] =
            () => throw new IysConfigurationException("30", "kimlik reddedildi");

        await RunAsync(factory);

        var acc = await ReadAsync(factory, brandCode);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "geçersiz markayla toplanan onay zaten geçersiz olurdu (spec §2.3)");
        acc.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Gecici_ariza_hesabi_DUSURMEZ()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[brandCode] = () => throw new HttpRequestException("İYS kapalı");

        await RunAsync(factory);

        var acc = await ReadAsync(factory, brandCode);
        acc.Status.Should().Be(NetgsmAccountStatus.Verified,
            "İYS'nin yarım saatlik kesintisi bütün yayıncıları kapatmamalı; "
            + "kapanan hesap kendiliğinden geri GELMİYOR");
        acc.LastError.Should().NotBeNullOrWhiteSpace("sorun görünür olmalı");
    }

    [Fact]
    public async Task Bir_hesabin_patlamasi_digerini_ETKILEMEZ()
    {
        // Paylaşılan scoped DbContext'te A'nın kirli kayıtları B'nin
        // SaveChanges'ine binerse, B'nin satırına A'nın verisi yazılır.
        var factory = NewFactory();
        var bad = await SeedVerifiedAsync(factory);
        var good = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[bad] = () => throw new InvalidOperationException("beklenmeyen");

        await RunAsync(factory);

        factory.Iys.Asked.Should().Contain(good, "bir tur düşünce döngü DURMAMALI");
        (await ReadAsync(factory, good)).Status.Should().Be(NetgsmAccountStatus.Verified);
    }

    [Fact]
    public async Task Failed_hesap_ise_alinmaz()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.Status = NetgsmAccountStatus.Failed;
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);

        factory.Iys.Asked.Should().NotContain(brandCode,
            "kapalı hesabı her gün yoklamak yayıncı adına bedelsiz de olsa "
            + "gereksiz; yeniden açılma yolu panelden kaydetmektir");
    }
}
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifyJobTests
```
Beklenen: derleme hatası — `NetgsmAccountVerifyJob` yok.

- [ ] **Adım 3: En küçük uygulamayı yaz**

`OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifyJob.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Günlük yeniden doğrulama (spec §2.3). Abonelik biter, API şifresi döner,
/// marka kodu iptal olur — tek seferlik doğrulama bunları görmez. Düşen hesap
/// <see cref="NetgsmAccountStatus.Failed"/> olur; kampanya kapısı VE onay
/// toplama durur, çünkü geçersiz markayla toplanan onay zaten geçersizdir.
///
/// <para><b>Aşırı tepki asıl tehlike.</b> Yalnız
/// <see cref="NetgsmVerifyOutcome.Rejected"/> hesabı düşürür.
/// <see cref="NetgsmVerifyOutcome.Unavailable"/> yalnız
/// <see cref="NetgsmAccount.LastError"/> yazar: <c>Failed</c>'dan çıkışın tek
/// yolu yayıncının panele girip kaydetmesi olduğu için, İYS'nin yarım saatlik
/// bir kesintisi aksi hâlde bütün yayıncıları elle müdahale gerektiren bir
/// duruma sokardı.</para>
/// </summary>
public sealed class NetgsmAccountVerifyJob
{
    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmAccountVerifier _verifier;
    private readonly ILogger<NetgsmAccountVerifyJob> _log;

    public NetgsmAccountVerifyJob(
        LicenseDbContext db,
        NetgsmAccountService accounts,
        NetgsmAccountVerifier verifier,
        ILogger<NetgsmAccountVerifyJob> log)
    {
        _db = db;
        _accounts = accounts;
        _verifier = verifier;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var accountId in await _accounts.ListVerifiedIdsAsync(ct))
        {
            try
            {
                await VerifyOneAsync(accountId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Bir kiracının turu düşerse sıradakine geçilir. Temizlik ŞART:
                // paylaşılan scoped DbContext'te bu hesabın kirli kayıtları
                // sıradakinin SaveChanges'ine biner ve yanlış satıra yazılırdı.
                _log.LogWarning(ex,
                    "NetgsmAccountVerifyJob: hesap {AccountId} turu düştü", accountId);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task VerifyOneAsync(Guid accountId, CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        // Liste alındıktan sonra admin kill switch'i çalışmış olabilir.
        if (acc is null || acc.Status != NetgsmAccountStatus.Verified) return;

        var password = _accounts.TryUnprotectPassword(acc.PasswordProtected);
        if (password is null)
        {
            acc.LastError = NetgsmAccountService.UndecryptableMessage;
            acc.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var result = await _verifier.VerifyAsync(
            new IysAccountContext(acc.LicenseId, acc.UserCode, password, acc.BrandCode), ct);

        var now = DateTimeOffset.UtcNow;
        switch (result.Outcome)
        {
            case NetgsmVerifyOutcome.Ok:
                acc.LastVerifiedAt = now;
                acc.LastError = null;
                break;

            case NetgsmVerifyOutcome.Rejected:
                _log.LogWarning(
                    "NetgsmAccountVerifyJob: lisans {LicenseId} kurulumu düştü — {Message}",
                    acc.LicenseId, result.Message);
                acc.Status = NetgsmAccountStatus.Failed;
                acc.LastError = result.Message;
                break;

            case NetgsmVerifyOutcome.Unavailable:
                acc.LastError = result.Message;
                break;
        }

        acc.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }
}
```

DI'ı geçici olarak `Program.cs`'e ekle (kalıcı kablolama Görev 13'te):

```csharp
builder.Services.AddScoped<NetgsmAccountVerifier>();
builder.Services.AddScoped<NetgsmAccountVerifyJob>();
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifyJobTests
```
Beklenen: PASS (5 test).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifyJob.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifyJobTests.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): günlük yeniden doğrulama işi — yalnız kesin ret hesabı düşürür

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 9: Çözülemeyen şifre `Status`'e dokunmaz

**Files:**
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifyJobTests.cs`

> **Spec'ten bilinçli sapma.** §2.4 "anahtar halkası kaybolursa hesap
> `disabled`" diyor. Uygulamıyoruz: `Disabled → Verified` dönen bir kod yolu
> yok ve bağlanmamış tek bir anahtar dizini tek koşuda bütün yayıncıları kalıcı
> olarak kilitlerdi. §2.4'ün asıl talebi olan "sessiz bozulma yok", `LastError`
> panele döndüğü için karşılanıyor. Görev 8'in uygulaması bunu zaten yapıyor;
> bu görev davranışı **kilitler** — testi olmayan bir karar, sonraki mühendis
> tarafından "spec'e uymuyor" diye geri alınır.

- [ ] **Adım 1: Düşen testi yaz**

`NetgsmAccountVerifyJobTests` sınıfına ekle:

```csharp
    [Fact]
    public async Task Cozulemeyen_sifre_hesabi_KAPATMAZ()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);

        // Anahtar halkası kaybını taklit et: şifreli metni bozuk bir değerle
        // değiştir. Unprotect CryptographicException atar, TryUnprotectPassword
        // null döner.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.PasswordProtected = $"bozuk-{Guid.NewGuid():N}";
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);

        var after = await ReadAsync(factory, brandCode);
        after.Status.Should().Be(NetgsmAccountStatus.Verified,
            "spec §2.4'ten bilinçli sapma: Disabled→Verified dönen kod yolu YOK, "
            + "yani bağlanmamış tek bir anahtar dizini tek koşuda bütün "
            + "yayıncıları kalıcı olarak kilitlerdi");
        after.LastError.Should().Be(NetgsmAccountService.UndecryptableMessage,
            "§2.4'ün asıl talebi sessiz bozulmanın olmaması — panel bunu gösterir");
        factory.Iys.Asked.Should().NotContain(brandCode,
            "şifre çözülemeden İYS'ye çağrı yapılmamalı");
    }

    [Fact]
    public async Task Cozulemeyen_sifre_panelde_gorunur()
    {
        // LastError panele dönmüyorsa "sessiz bozulma yok" kuralı kâğıt üstünde
        // kalır: yayıncı SMS'lerinin neden gitmediğini hiçbir yerden öğrenemez.
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.PasswordProtected = $"bozuk-{Guid.NewGuid():N}";
            await db.SaveChangesAsync();
        }
        await RunAsync(factory);

        var licenseId = (await ReadAsync(factory, brandCode)).LicenseId;
        using var readScope = factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await readDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);

        var view = OrderDeck.LicenseServer.Controllers.Panel
            .PanelNetgsmAccountController.ToView(row);
        view.LastError.Should().Be(NetgsmAccountService.UndecryptableMessage);
        view.SmsEnabled.Should().BeTrue(
            "hesap hâlâ Verified — gönderim ilk denemede düşer ve gerçek "
            + "sebebi LastError'da yazar; kapatmanın bedeli daha ağır");
    }
```

`ToView` test derlemesinden görülebilmeli; `internal static` olduğu için
sunucu projesine `InternalsVisibleTo` gerekir. `OrderDeck.LicenseServer.csproj`
zaten test projesine açıksa ek iş yok; değilse `ToView`'u `public static` yap
(dönen tip `AccountView` zaten public).

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifyJobTests
```
Beklenen: Görev 8'in uygulaması doğruysa **geçerler**. Geçmiyorlarsa Görev 8
yanlış uygulanmış — düzelt, testleri değiştirme.

- [ ] **Adım 3: Mutasyon provası**

Testin gerçekten koruduğunu kanıtla: `NetgsmAccountVerifyJob.VerifyOneAsync`
içindeki `password is null` bloğuna geçici olarak
`acc.Status = NetgsmAccountStatus.Disabled;` ekle, testi koş, **düştüğünü gör**,
sonra satırı geri al.

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifyJobTests
```
Beklenen: PASS (7 test).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVerifyJobTests.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs
git commit -m "$(cat <<'EOF'
test(netgsm): anahtar kaybı hesabı kapatmaz, LastError'a yazar (§2.4 sapması)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 10: Intake formunda SMS kutusu yalnız doğrulanmış kurulumda görünür

Spec §2.1'in dürüstlük kuralı: kurulumu tamamlanmamış yayıncının formunda SMS
onay kutusu **gösterilmemeli**. Bugün kutu her formda duruyor; işaretleyen kişi
onay verdiğini sanıyor, toplayıcı ise marka çözemediği için satır açmıyor.
Sonuç: kullanıcıya yalan söylenen, hukuken var olmayan bir onay.

Kutunun görünürlüğü ile toplayıcının markayı çözdüğü yol **aynı sorgudan**
beslenmeli. Ayrı iki sorgu yazılırsa biri değişip diğeri kalır ve tam bu yalan
sessizce geri döner — bu yüzden `IsSmsConsentEnabledAsync` de
`PanelLicenseScope.ResolveAsync` kullanıyor.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml:385-392`
- Test: `OrderDeck.LicenseServer.Tests/Pages/IntakeFormSmsConsentVisibilityTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Pages/IntakeFormSmsConsentVisibilityTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

/// <summary>
/// §2.1 — kurulumu tamamlanmamış yayıncının formunda SMS onay kutusu
/// GÖRÜNMEZ. Kutu görünüp onay yazılmazsa kullanıcıya verilmemiş bir söz
/// verilmiş olur; kutu görünüp onay yazılırsa marka olmadan İYS'ye
/// bildirilemeyen, üç iş günü sonra hukuken geçersiz bir onay doğar.
/// İkisi de kabul edilemez, o yüzden kutu kurulumla birlikte doğar.
/// </summary>
public sealed class IntakeFormSmsConsentVisibilityTests : IClassFixture<ApiFactory>
{
    private const string CheckboxText = "SMS üzerinden bilgilendirme";

    private readonly ApiFactory _factory;
    public IntakeFormSmsConsentVisibilityTests(ApiFactory factory) => _factory = factory;

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private async Task<string> SeedConfigAsync(NetgsmAccountStatus? accountStatus)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"sv-{Guid.NewGuid():N}@x",
            Name = "Sv",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-SV-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        if (accountStatus is { } status)
        {
            db.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                UserCode = NewUserCode(),
                PasswordProtected = $"pw-{Guid.NewGuid():N}",
                Header = "ORDERDECK",
                BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var slug = $"sv-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return slug;
    }

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task Dogrulanmis_kurulumda_kutu_gorunur()
    {
        var slug = await SeedConfigAsync(NetgsmAccountStatus.Verified);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().Contain(CheckboxText);
    }

    [Fact]
    public async Task Hesapsiz_kurulumda_kutu_gorunmez()
    {
        var slug = await SeedConfigAsync(accountStatus: null);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().NotContain(CheckboxText);
    }

    [Theory]
    [InlineData(NetgsmAccountStatus.Failed)]
    [InlineData(NetgsmAccountStatus.Disabled)]
    public async Task Dogrulanmamis_hesapta_kutu_gorunmez(NetgsmAccountStatus status)
    {
        var slug = await SeedConfigAsync(status);
        var html = await NewClient().GetStringAsync($"/r/{slug}");

        WebUtility.HtmlDecode(html).Should().NotContain(CheckboxText);
    }

    [Fact]
    public async Task Kutu_kapaliyken_elle_gonderilen_onay_yok_sayilir()
    {
        // Kutunun gizlenmesi görsel bir önlem; istemci alanı elle ekleyebilir.
        // Sunucu tarafı da reddetmezse gizleme sadece dürüst kullanıcıyı korur.
        var slug = await SeedConfigAsync(accountStatus: null);
        var client = NewClient();
        var phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

        var getResp = await client.GetAsync($"/r/{slug}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(
            await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "kutu",
            ["Input.FullName"] = "Ad Soyad",
            ["Input.Email"] = "a@example.com",
            ["Input.Address"] = "Adres",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = phone,
            ["Input.SmsConsent"] = "true",
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions.AsNoTracking()
            .SingleAsync(s => s.Phone == phone);
        sub.SmsConsent.Should().BeFalse("kutu kapalıyken onay kaydı doğmamalı");
        (await db.IysConsentEvents.AsNoTracking().AnyAsync(e => e.Recipient == phone))
            .Should().BeFalse();
    }
}
```

- [ ] **Adım 2: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~IntakeFormSmsConsentVisibilityTests
```
Beklenen: FAIL — `Hesapsiz_kurulumda_kutu_gorunmez` ve iki `Theory` satırı kutuyu
buluyor; `Kutu_kapaliyken_...` testinde `sub.SmsConsent` true.

- [ ] **Adım 3: Servise tek doğruluk kaynağını ekle**

`IntakeFormService.cs` — `GetActiveBySlugAsync`'ten hemen önce ekle:

```csharp
    /// <summary>
    /// Bu form SMS onayı toplayabilir mi? Kutu bunu okur, böylece görünürlük
    /// ile toplayıcının davranışı aynı sorgudan beslenir: ikisi ayrı yazılırsa
    /// biri değişip diğeri kalır ve "kutu var, onay yok" yalanı geri döner.
    /// Yalnız <see cref="NetgsmAccountStatus.Verified"/> sayılır — marka ancak
    /// doğrulanmış hesaptan çözülür.
    /// </summary>
    public async Task<bool> IsSmsConsentEnabledAsync(Guid configId, CancellationToken ct = default)
    {
        var config = await _db.IntakeFormConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
        if (config is null) return false;

        var licenseId = await Controllers.Panel.PanelLicenseScope.ResolveAsync(
            _db, config.CustomerId, ct);
        if (licenseId is null) return false;

        return await _db.NetgsmAccounts.AsNoTracking().AnyAsync(
            a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified, ct);
    }
```

- [ ] **Adım 4: Sayfa modelini bağla**

`Pages/Public/IntakeForm.cshtml.cs` — `Config` özelliğinin hemen yanına ekle:

```csharp
    /// <summary>SMS onay kutusu render edilsin mi. Hem GET hem POST'ta set
    /// edilir; POST'ta ayrıca gelen değeri EZER (gizleme istemci tarafı).</summary>
    public bool SmsConsentAvailable { get; private set; }
```

`OnGetAsync` içinde, `if (Config is null) return StatusCode(StatusCodes.Status410Gone);`
satırından hemen sonra:

```csharp
        SmsConsentAvailable = await _service.IsSmsConsentEnabledAsync(Config.Id, ct);
```

`OnPostSubmitAsync` içinde, honeypot bloğunun ardındaki
`Config = await _service.GetActiveBySlugAsync(Slug, ct);` +
`if (Config is null) return StatusCode(StatusCodes.Status410Gone);` çiftinden
hemen sonra (yani `LoadLinkedIdentities();` çağrısından önce):

```csharp
        SmsConsentAvailable = await _service.IsSmsConsentEnabledAsync(Config.Id, ct);
        // Kutu kapalıyken gelen onay SUNUCUDA düşürülür: gizleme yalnız dürüst
        // istemciyi bağlar, alan elle eklenebilir. Marka yokken yazılan onay
        // İYS'ye bildirilemez ve üç iş günü sonra hukuken geçersiz olur.
        if (!SmsConsentAvailable) Input.SmsConsent = false;
```

Honeypot bloğu `Page()` döndürdüğü için orada da `Config` set ediliyor; oraya
dokunma — bot yanıtı zaten kaydetmiyor ve `SmsConsentAvailable` varsayılanı
`false` olduğu için kutu render edilmiyor.

- [ ] **Adım 5: Kutuyu koşula al**

`Pages/Public/IntakeForm.cshtml:389-392` — SMS `<label class="check">` bloğunu sar
(WhatsApp kutusuna DOKUNMA, o izin İYS'ye bağlı değil):

```cshtml
            @if (Model.SmsConsentAvailable)
            {
                <label class="check">
                    <input asp-for="Input.SmsConsent" type="checkbox" />
                    <span>SMS üzerinden bilgilendirme/kampanya mesajı almayı kabul ediyorum.</span>
                </label>
            }
```

- [ ] **Adım 6: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~IntakeFormSmsConsentVisibilityTests|FullyQualifiedName~IysConsentWiringTests|FullyQualifiedName~IntakeFormPhoneTests"
```
Beklenen: PASS. `IysConsentWiringTests` servisi doğrudan çağırdığı için sayfa
kapısından etkilenmez; yine de birlikte koşulur çünkü aynı onay yolunu paylaşırlar.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Pages/IntakeFormSmsConsentVisibilityTests.cs \
        OrderDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs \
        OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs \
        OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml
git commit -m "$(cat <<'EOF'
feat(intake): SMS onay kutusu yalnız doğrulanmış Netgsm kurulumunda görünür

Kurulumsuz yayıncıda kutu işaretleniyor ama marka çözülemediği için onay
satırı açılmıyordu — kullanıcıya verilmemiş bir söz. Görünürlük ve toplayıcı
artık aynı sorgudan besleniyor; sunucu tarafı elle gönderilen onayı düşürüyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 11: `paused` kampanya durumu — gönderim ortasında durdurulabilir

Spec §2.5: admin bir kurulumu kapattığında o lisansın **devam eden** kampanyası
da durmalı. Bugün `SmsCampaignSendJob` alıcı döngüsüne girdikten sonra hiçbir
şeye bakmıyor; bin kişilik bir kampanya, hesap kapatıldıktan sonra da sonuna
kadar gider.

Üç kural birlikte tutmalı:
1. Döngü her alıcıdan **önce** kampanyanın güncel durumunu okur; `paused` ise
   **iade yapmadan** çıkar. İade yok, çünkü kalan alıcılar hâlâ `pending` —
   rezervasyon onların karşılığı ve kampanya devam ettirilebilir.
2. `paused` bir kampanya baştan hiç üstlenilmez (mevcut durum kapısı zaten
   `pending` ve bayat `sending` dışını eliyor; bu bir gerileme koruması).
3. `SmsCampaignRecoveryJob` `paused` kampanyayı **diriltmez**.

Durumu `paused`'a yazan taraf Görev 12'dir; burada yalnız job'ın ona uyması
sağlanıyor.

Spec §2.5 "bakiye tükenmesiyle aynı duraklama yolu" diyor. **Bugün bakiye
tükenmesi diye bir duraklama yolu YOK** — krediler kampanya oluşturulurken
peşin rezerve edildiği için koşu ortasında bakiye bitemiyor. Burada kurulan
mekanizma o tek yoldur; ileride bakiye sebebi eklenirse aynı `paused` durumunu
kullanmalı, ikinci bir durum icat etmemeli.

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/SmsCampaign.cs:32`
- Modify: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs`
- Modify: `OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs`

- [ ] **Adım 1: Test sahtesine gönderim kancası ekle**

Döngü ortasında durumu değiştirebilmek için gönderim anına bir kanca gerekiyor.
`OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs` — `Sent`
listesine eklendikten SONRA çağrılan bir kanca ekle:

```csharp
    /// <summary>Kayıt yapıldıktan sonra çağrılır. Gönderim ortasında durum
    /// değiştiren testler için — job'a sahte bir gecikme/olay enjekte etmenin
    /// tek dürüst yolu, gerçek gönderim noktasına bağlanmak.</summary>
    public Action<Message>? OnSent { get; set; }
```

ve `SendAsync` içinde, mesaj listeye eklendikten sonra:

```csharp
        OnSent?.Invoke(msg);
```

`Clear()` kancayı **sıfırlamaz** (fixture paylaşımlı; her test kendi kancasını
kurar ve `try/finally` ile `OnSent = null` yapar — aşağıdaki test öyle yapıyor).

- [ ] **Adım 2: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs`:

```csharp
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// §2.5 — kurulum kapatıldığında devam eden kampanya da durur. Durmazsa
/// admin'in "kapat" düğmesi yalan söyler: hesap kapalıyken bin SMS daha gider
/// ve bunların İYS izni artık doğrulanamaz durumdadır.
/// </summary>
public sealed class SmsCampaignPauseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SmsCampaignPauseTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        _factory.Sms.OnSent = null;
    }

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewPhone()
        => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

    /// <summary>Onaylı iki alıcılı bir kampanya tohumlar.</summary>
    private static async Task<(Guid CampaignId, string[] Phones)> SeedAsync(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"pz-{Guid.NewGuid():N}@x",
            Name = "Pz",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-PZ-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.LicenseSmsBalances.Add(new LicenseSmsBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CreditsRemaining = 99,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Amount = 99,
            Kind = "purchase",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var phones = new[] { NewPhone(), NewPhone() };
        foreach (var p in phones)
        {
            db.IysConsents.Add(new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = p,
                Status = IysConsentStatus.Onay,
                LastVerifiedStatus = IysConsentStatus.Onay,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                PushState = IysPushState.Confirmed,
                LastLocalEventAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Kampanya",
            Status = "pending",
            SegmentsPerMessage = 1,
            RecipientCount = phones.Length,
            ReservedCredits = phones.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        foreach (var p in phones)
        {
            db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Phone = p,
                Status = "pending",
            });
        }

        await db.SaveChangesAsync();
        return (campaignId, phones);
    }

    [Fact]
    public async Task Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _) = await SeedAsync(db);

        // İlk gönderimden hemen sonra, AYRI bir scope'tan duraklat — admin'in
        // yaptığı tam olarak bu: job koşarken başka bir istek durumu yazıyor.
        _factory.Sms.OnSent = _ =>
        {
            using var s2 = _factory.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = db2.SmsCampaigns.Single(x => x.Id == campaignId);
            if (c.Status == "sending") { c.Status = "paused"; db2.SaveChanges(); }
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.Sms.OnSent = null; }

        _factory.Sms.Sent.Should().HaveCount(1, "duraklatma ikinci alıcıyı durdurmalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("paused", "job duraklatmayı ezmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0,
            "kalan alıcılar hâlâ pending — rezervasyon onların karşılığı, iade edilirse "
            + "kampanya devam ettirildiğinde kredi iki kez harcanmış olur");

        var recipients = await vdb.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();
        recipients.Count(r => r.Status == "sent").Should().Be(1);
        recipients.Count(r => r.Status == "pending").Should().Be(1);
    }

    [Fact]
    public async Task Paused_kampanya_hic_ustlenilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _) = await SeedAsync(db);

        var c = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        c.Status = "paused";
        await db.SaveChangesAsync();

        await job.RunAsync(campaignId);

        _factory.Sms.Sent.Should().BeEmpty();
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId))
            .Status.Should().Be("paused");
    }

    [Fact]
    public async Task Paused_kampanya_kurtarma_isiyle_diriltilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, _) = await SeedAsync(db);

        // Hem bayat claim hem eski CreatedAt: kurtarma işinin İKİ yakalama
        // koşulunu da tetikleyebilecek en kötü hâl.
        var c = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        c.Status = "paused";
        c.ClaimedAt = DateTimeOffset.UtcNow - SmsCampaignSendJob.ClaimLease - TimeSpan.FromHours(1);
        c.CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        await db.SaveChangesAsync();

        var enqueued = new List<Guid>();
        var jobs = new RecordingBackgroundJobClient(enqueued);
        var recovery = new SmsCampaignRecoveryJob(
            db, jobs,
            scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<SmsCampaignRecoveryJob>>());

        await recovery.RunAsync();

        enqueued.Should().NotContain(campaignId,
            "duraklatılmış kampanyayı diriltmek admin'in kapatma kararını geri alır");
    }
}
```

Kurtarma testindeki sahte istemci — aynı dosyanın altına, namespace içinde:

```csharp
/// <summary>Hangfire'a gerçekten iş atmadan, atılan kampanya kimliklerini
/// toplayan minimal <see cref="IBackgroundJobClient"/>.</summary>
internal sealed class RecordingBackgroundJobClient : IBackgroundJobClient
{
    private readonly List<Guid> _ids;
    public RecordingBackgroundJobClient(List<Guid> ids) => _ids = ids;

    public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
    {
        if (job.Args.Count > 0 && job.Args[0] is Guid id) _ids.Add(id);
        return Guid.NewGuid().ToString("N");
    }

    public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
}
```

- [ ] **Adım 3: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~SmsCampaignPauseTests
```
Beklenen: `Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez` FAIL —
2 SMS gitmiş, durum `completed`. Diğer iki test zaten PASS (gerileme koruması).

- [ ] **Adım 4: Durum sözlüğünü güncelle**

`OrderDeck.LicenseServer/Domain/SmsCampaign.cs:32` — XML yorumunu değiştir:

```csharp
    /// <summary>"pending" | "sending" | "paused" | "completed" | "failed".
    /// "paused": kurulum admin tarafından kapatıldı; kalan alıcılar "pending"
    /// kalır, rezervasyon iade EDİLMEZ — kampanya devam ettirilebilir.</summary>
```

`HasMaxLength(16)` altı harfi taşıyor; şema göçü gerekmiyor.

- [ ] **Adım 5: Döngüye durum yoklaması ekle**

`SmsCampaignSendJob.cs` — `foreach (var r in recipients)` bloğunun İLK satırı
olarak ekle:

```csharp
            // §2.5: kurulum koşu sırasında kapatılabilir. Skaler projeksiyon
            // bilinçli — `Select(c => c.Status)` kimlik çözümlemesine girmez,
            // yani bu bağlamda izlenen "sending" kopyasını değil DİSKTEKİ
            // değeri okur. Entity çekseydik kendi yazdığımızı geri okurduk.
            var currentStatus = await _db.SmsCampaigns
                .Where(c => c.Id == campaignId)
                .Select(c => c.Status)
                .FirstOrDefaultAsync(ct);
            if (currentStatus == "paused")
            {
                // İade YOK: kalan alıcılar "pending" ve rezervasyon onların
                // karşılığı. Burada iade edersek kampanya devam ettirildiğinde
                // aynı krediyi ikinci kez harcarız.
                _log.LogWarning(
                    "SmsCampaignSendJob: campaign {Id} paused mid-run, stopping after {Sent} sends",
                    campaignId, recipients.Count(x => x.Status == "sent"));
                return;
            }
```

Not: bu `return` kampanyanın durumunu YAZMAZ. İzlenen `campaign` nesnesinin
`Status`'ü claim `SaveChanges`'inden beri değişmedi, dolayısıyla EF onu değiştirilmiş
saymaz ve "sending" geri yazılmaz. Alıcı sonuçları zaten her adımda diske indi.

- [ ] **Adım 6: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~SmsCampaignPauseTests|FullyQualifiedName~SmsCampaignIysGateTests|FullyQualifiedName~SmsCampaignSendJob|FullyQualifiedName~SmsCampaignRecovery"
```
Beklenen: PASS — hem yeni testler hem mevcut gönderim/kurtarma paketleri.

- [ ] **Adım 7: Mutasyon provası — iadenin gerçekten korunduğunu gör**

`paused` dalındaki `return;` satırını geçici olarak şununla değiştir:

```csharp
                campaign.Status = "completed";
                await _db.SaveChangesAsync(ct);
                return;
```

Testi koş: `Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez`
**düşmeli** (`Status` "completed" geldi). Düşmüyorsa assert eksiktir. Sonra
değişikliği geri al.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs \
        OrderDeck.LicenseServer/Domain/SmsCampaign.cs \
        OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs
git commit -m "$(cat <<'EOF'
feat(sms): kampanya gönderimi koşu ortasında duraklatılabilir

Kurulum kapatıldığında devam eden kampanya sonuna kadar gidiyordu. Döngü artık
her alıcıdan önce diskteki durumu okuyor; "paused" görürse iade yapmadan çıkıyor
(kalan alıcılar pending, rezervasyon onların karşılığı).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 12: Admin kurulum kapatma anahtarı

Spec §2.5: kötüye kullanan ya da İYS uyumu bozulan bir yayıncının SMS'ini
yönetici anında kesebilmeli. Kesmek iki şeyi birden yapmalı:

- hesabı `Disabled` yapmak (panel bunu geri alamaz — Görev 6),
- o lisansın **bekleyen ve gönderilen** kampanyalarını `paused` yapmak.

İkincisi şart: `pending` bırakılan bir kampanyayı `SmsCampaignRecoveryJob` iki
dakika sonra kendiliğinden kuyruğa alır ve kapatma kararı sessizce geri alınır.

Açma `Verified` DEĞİL `Failed` yazar. Yönetici marka kodunun İYS'de hâlâ geçerli
olduğunu bilemez; doğrulama tek yoldan, normal doğrulama akışından geçmeli.
Durdurulan kampanyalar da burada devam ettirilmez — devam, doğrulama başarılı
olduğunda gelir (Görev 13).

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Test: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>
/// §2.5 — yönetici kapatma anahtarı. Kapatmanın SMS'i gerçekten kesmesi
/// gerekir: hesabı Disabled yapıp devam eden kampanyayı bırakmak, kararı
/// iki dakika sonra kurtarma işinin geri almasına izin verir.
/// </summary>
public sealed class AdminNetgsmPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminNetgsmPageTests(ApiFactory factory) => _factory = factory;

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private async Task<(Guid AccountId, Guid LicenseId)> SeedAccountAsync(
        NetgsmAccountStatus status = NetgsmAccountStatus.Verified)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"an-{Guid.NewGuid():N}@x",
            Name = "An",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-AN-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (accountId, licenseId);
    }

    private static async Task<Guid> SeedCampaignAsync(
        LicenseDbContext db, Guid licenseId, string status)
    {
        var id = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = id,
            LicenseId = licenseId,
            MessageBody = "Kampanya",
            Status = status,
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            ReservedCredits = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string handler, Guid accountId)
    {
        var getResp = await client.GetAsync("/admin/netgsm");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(
            await getResp.Content.ReadAsStringAsync());
        return await client.PostAsync(
            $"/admin/netgsm?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["AccountId"] = accountId.ToString(),
            }));
    }

    [Fact]
    public async Task Giris_yapmadan_sayfa_gorulemez()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/admin/netgsm");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Kapatma_hesabi_disabled_yapar_ve_kampanyalari_duraklatir()
    {
        var (accountId, licenseId) = await SeedAccountAsync();
        Guid pendingId, sendingId, completedId;
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            pendingId = await SeedCampaignAsync(db, licenseId, "pending");
            sendingId = await SeedCampaignAsync(db, licenseId, "sending");
            completedId = await SeedCampaignAsync(db, licenseId, "completed");
        }

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await PostAsync(client, "Disable", accountId);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var acc = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled);
        acc.LastError.Should().NotBeNullOrEmpty();

        string StatusOf(Guid id) => vdb.SmsCampaigns.AsNoTracking().Single(c => c.Id == id).Status;
        StatusOf(pendingId).Should().Be("paused",
            "pending bırakılırsa kurtarma işi iki dakika sonra diriltir");
        StatusOf(sendingId).Should().Be("paused");
        StatusOf(completedId).Should().Be("completed", "biten kampanya geçmiştir");

        var audit = await vdb.AuditLogs.AsNoTracking()
            .Where(e => e.EventType == AuditEvents.NetgsmAccountDisable
                        && e.TargetId == accountId.ToString())
            .ToListAsync();
        audit.Should().ContainSingle();
    }

    [Fact]
    public async Task Kapatma_baska_lisansin_kampanyasina_dokunmaz()
    {
        var (accountId, _) = await SeedAccountAsync();
        var (_, otherLicenseId) = await SeedAccountAsync();
        Guid otherCampaignId;
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            otherCampaignId = await SeedCampaignAsync(db, otherLicenseId, "pending");
        }

        var client = await _factory.CreateLoggedInAdminClientAsync();
        (await PostAsync(client, "Disable", accountId))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == otherCampaignId))
            .Status.Should().Be("pending");
    }

    [Fact]
    public async Task Acma_Verified_degil_Failed_yazar()
    {
        var (accountId, _) = await SeedAccountAsync(NetgsmAccountStatus.Disabled);

        var client = await _factory.CreateLoggedInAdminClientAsync();
        (await PostAsync(client, "Enable", accountId))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "yönetici markanın İYS'de hâlâ geçerli olduğunu bilemez; "
            + "doğrulama normal akıştan geçmeli");
        acc.LastError.Should().BeNull();

        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountEnable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(1);
    }
}
```

- [ ] **Adım 2: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~AdminNetgsmPageTests
```
Beklenen: derleme hatası — `AuditEvents.NetgsmAccountDisable` yok.

- [ ] **Adım 3: Denetim sabitlerini ekle**

`Services/Audit/AuditEvents.cs` — `OperatorDeleted` satırının altına:

```csharp

    // Netgsm kurulum kapatma anahtarı (§2.5). Kapatma bir yayıncının tüm
    // SMS'ini keser; kimin ne zaman kestiği iz bırakmadan olmamalı.
    public const string NetgsmAccountDisable = "netgsm.account.disable";
    public const string NetgsmAccountEnable = "netgsm.account.enable";
```

ve `AuditTargets` içine `Shopper` satırının altına:

```csharp
    public const string NetgsmAccount = "netgsm-account";
```

- [ ] **Adım 4: Sayfa modelini yaz**

`OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;

namespace OrderDeck.LicenseServer.Pages.Admin.Netgsm;

/// <summary>
/// Yayıncı Netgsm/İYS kurulumlarının yönetici görünümü + kapatma anahtarı.
///
/// <para><b>[Authorize] YOK — kasıtlı.</b> <c>Program.cs</c>'teki
/// <c>AuthorizeFolder("/Admin", "AdminOnly")</c> tüm klasörü kapsıyor;
/// sayfaya ayrıca öznitelik koymak ikinci bir doğruluk kaynağı yaratır
/// (bkz. komşu <c>Pages/Admin/Iys/Index.cshtml.cs</c>).</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record Row(
        Guid AccountId,
        Guid LicenseId,
        string CustomerEmail,
        string UserCode,
        string Header,
        string BrandCode,
        NetgsmAccountStatus Status,
        string? LastError,
        DateTimeOffset? LastVerifiedAt,
        int ActiveCampaigns);

    [BindProperty]
    public Guid AccountId { get; set; }

    public List<Row> Rows { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rows = await _db.NetgsmAccounts.AsNoTracking()
            .OrderBy(a => a.Status).ThenBy(a => a.UpdatedAt)
            .Select(a => new Row(
                a.Id,
                a.LicenseId,
                _db.Licenses.Where(l => l.Id == a.LicenseId)
                    .Select(l => l.Customer.Email).FirstOrDefault() ?? "(bilinmiyor)",
                a.UserCode,
                a.Header,
                a.BrandCode,
                a.Status,
                a.LastError,
                a.LastVerifiedAt,
                _db.SmsCampaigns.Count(c => c.LicenseId == a.LicenseId
                    && (c.Status == "pending" || c.Status == "sending"))))
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == AccountId, ct);
        if (acc is null) return NotFound();

        acc.Status = NetgsmAccountStatus.Disabled;
        acc.LastError = "Yönetici tarafından kapatıldı.";
        acc.UpdatedAt = DateTimeOffset.UtcNow;

        // Devam eden kampanyaları da durdur. "pending" bırakmak yetmez:
        // SmsCampaignRecoveryJob iki dakika sonra onu kuyruğa alır ve
        // kapatma kararı sessizce geri alınır.
        var live = await _db.SmsCampaigns
            .Where(c => c.LicenseId == acc.LicenseId
                        && (c.Status == "pending" || c.Status == "sending"))
            .ToListAsync(ct);
        foreach (var c in live) c.Status = "paused";

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditEvents.NetgsmAccountDisable, AuditTargets.NetgsmAccount,
            acc.Id.ToString(),
            new { licenseId = acc.LicenseId, pausedCampaigns = live.Count }, ct);

        TempData["Success"] = live.Count == 0
            ? "Kurulum kapatıldı."
            : $"Kurulum kapatıldı, {live.Count} kampanya duraklatıldı.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == AccountId, ct);
        if (acc is null) return NotFound();

        // Verified DEĞİL: yönetici markanın İYS'de hâlâ geçerli olduğunu
        // bilemez. Doğrulama tek yoldan — panel kaydı ya da günlük iş — geçer.
        // Duraklatılmış kampanyalar da burada devam ettirilmez; devam,
        // doğrulamanın başarılı olduğu anda gelir.
        acc.Status = NetgsmAccountStatus.Failed;
        acc.LastError = null;
        acc.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditEvents.NetgsmAccountEnable, AuditTargets.NetgsmAccount,
            acc.Id.ToString(), new { licenseId = acc.LicenseId }, ct);

        TempData["Success"] = "Kurulum açıldı — doğrulama bekleniyor.";
        return RedirectToPage();
    }
}
```

- [ ] **Adım 5: Sayfayı yaz**

`OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml`:

```cshtml
@page "/admin/netgsm"
@model IndexModel
@{
    ViewData["Title"] = "Netgsm Kurulumları";
}
<h1 class="h3 mb-4">Netgsm / İYS Kurulumları</h1>

@if (TempData["Success"] is string ok)
{
    <div class="alert alert-success">@ok</div>
}

<table class="table table-sm align-middle">
    <thead>
        <tr>
            <th>Müşteri</th><th>Abone No</th><th>Başlık</th><th>Marka</th>
            <th>Durum</th><th>Son doğrulama</th><th>Aktif kampanya</th>
            <th>Son hata</th><th></th>
        </tr>
    </thead>
    <tbody>
    @foreach (var r in Model.Rows)
    {
        <tr data-account="@r.AccountId">
            <td>@r.CustomerEmail</td>
            <td>@r.UserCode</td>
            <td>@r.Header</td>
            <td>@r.BrandCode</td>
            <td data-cell="status">@r.Status</td>
            <td>@(r.LastVerifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")</td>
            <td>@r.ActiveCampaigns</td>
            <td class="text-danger small">@r.LastError</td>
            <td>
                <form method="post" class="d-inline">
                    <input type="hidden" name="AccountId" value="@r.AccountId" />
                    @if (r.Status == NetgsmAccountStatus.Disabled)
                    {
                        <button class="btn btn-sm btn-outline-success"
                                asp-page-handler="Enable">Aç</button>
                    }
                    else
                    {
                        <button class="btn btn-sm btn-outline-danger"
                                asp-page-handler="Disable">Kapat</button>
                    }
                </form>
            </td>
        </tr>
    }
    </tbody>
</table>

@if (Model.Rows.Count == 0)
{
    <p class="text-muted">Henüz Netgsm kurulumu yapan yayıncı yok.</p>
}
```

`NetgsmAccountStatus`'ü sayfada kullanabilmek için `Pages/_ViewImports.cshtml`
zaten `@using OrderDeck.LicenseServer.Domain` içermiyorsa bu dosyanın başına ekle:

```cshtml
@using OrderDeck.LicenseServer.Domain
```

- [ ] **Adım 6: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~AdminNetgsmPageTests|FullyQualifiedName~AdminAuthFlowTests"
```
Beklenen: PASS (4 yeni test + mevcut admin yetkilendirme paketi).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs \
        OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs \
        OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs
git commit -m "$(cat <<'EOF'
feat(admin): Netgsm kurulum kapatma anahtarı

Kapatma hesabı Disabled yapar ve o lisansın pending/sending kampanyalarını
duraklatır — pending bırakılsaydı kurtarma işi kararı iki dakikada geri alırdı.
Açma Verified değil Failed yazar: doğrulama normal akıştan geçer.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 13: Devam ettirme + DI/cron bağlantıları

Son halka: doğrulama geri geldiğinde duraklatılmış kampanyalar devam etmeli,
ve yeni sınıflar uygulamaya bağlanmalı.

**Devam ettirme kuyruğa ATMAZ**, yalnız `paused → pending` yazar ve
`ClaimedAt`'i temizler. Kuyruğa atmayı `SmsCampaignRecoveryJob` yapar (5 dakikada
bir, 2 dakikalık `PendingGrace`). Gerekçe `Program.cs`'te İYS push işi için zaten
yazılı: kaçırılan bir `Enqueue` kaydı sessizce kaybeder, süpürme kaybetmez.
Burada da aynı: kapatma/açma nadir bir olay, 5 dakikalık gecikmenin bedeli yok;
buna karşılık "yazdım ama enqueue çökmüştü" sınıfı bir kayıp hiç doğmuyor.

Devam ettirme `Failed → Verified` geçişinde çağrılır — hem panel kaydından
(Görev 5) hem günlük işten (Görev 8). `Verified → Verified` geçişinde çağrılmaz:
zaten duraklatılmış kampanya olmaması gerekir ve her gün diriltme denemesi
gürültüden ibaret olur.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Index.cshtml`
- Modify: `OrderDeck.LicenseServer/Program.cs:195` (DI) ve `:908` civarı (cron)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountResumeTests.cs`

- [ ] **Adım 1: Düşen testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountResumeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// §2.5 son halkası — kurulum geri açılıp doğrulandığında duraklatılmış
/// kampanya devam eder. Etmezse kredisi rezerve edilmiş, alıcıları "pending"
/// bir kampanya sonsuza dek asılı kalır: yayıncı ne gönderim görür ne iade.
/// </summary>
public sealed class NetgsmAccountResumeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public NetgsmAccountResumeTests(ApiFactory factory) => _factory = factory;

    private static async Task<(Guid LicenseId, Guid PausedId, Guid CompletedId)> SeedAsync(
        LicenseDbContext db)
    {
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-RS-{Guid.NewGuid():N}",
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        Guid Add(string status, DateTimeOffset? claimedAt)
        {
            var id = Guid.NewGuid();
            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = id,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = status,
                ClaimedAt = claimedAt,
                SegmentsPerMessage = 1,
                RecipientCount = 1,
                ReservedCredits = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            return id;
        }

        var pausedId = Add("paused", DateTimeOffset.UtcNow.AddHours(-1));
        var completedId = Add("completed", null);
        await db.SaveChangesAsync();
        return (licenseId, pausedId, completedId);
    }

    [Fact]
    public async Task Devam_ettirme_paused_kampanyayi_pending_yapar_ve_claimi_temizler()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, pausedId, completedId) = await SeedAsync(db);

        var resumed = await svc.ResumePausedCampaignsAsync(licenseId, CancellationToken.None);

        resumed.Should().Be(1);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var paused = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == pausedId);
        paused.Status.Should().Be("pending");
        paused.ClaimedAt.Should().BeNull(
            "bayat claim kalırsa kurtarma işi devralma kaydı yazar — devir değil, temiz başlangıç");
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == completedId))
            .Status.Should().Be("completed");
    }

    [Fact]
    public async Task Devam_ettirme_baska_lisansa_dokunmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, _, _) = await SeedAsync(db);
        var (_, otherPausedId, _) = await SeedAsync(db);

        await svc.ResumePausedCampaignsAsync(licenseId, CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == otherPausedId))
            .Status.Should().Be("paused");
    }

    [Fact]
    public async Task Duraklatilmis_kampanya_yokken_sifir_doner()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-RS-{Guid.NewGuid():N}",
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();

        (await svc.ResumePausedCampaignsAsync(licenseId, CancellationToken.None))
            .Should().Be(0);
    }
}
```

- [ ] **Adım 2: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountResumeTests
```
Beklenen: derleme hatası — `ResumePausedCampaignsAsync` yok.

- [ ] **Adım 3: Devam ettirmeyi yaz**

`NetgsmAccountService.cs` — `GetBrandCodeAsync`'in altına ekle:

```csharp
    /// <summary>
    /// Kurulum yeniden doğrulandığında duraklatılmış kampanyaları devam
    /// ettirir. Geriye devam ettirilen kampanya sayısını döndürür.
    ///
    /// <para><b>Kuyruğa ATMAZ.</b> Yalnız "pending" yazar;
    /// <see cref="SmsCampaignRecoveryJob"/> 5 dakikada bir süpürüp kuyruğa
    /// alır. Gerekçe İYS push işiyle aynı: kaçırılan bir Enqueue kaydı sessizce
    /// kaybeder, süpürme kaybetmez. Kapatma/açma nadir bir olay, 5 dakikalık
    /// gecikmenin ölçülebilir bir bedeli yok.</para>
    ///
    /// <para><c>ClaimedAt</c> temizleniyor: bayat bir claim'le bırakılırsa
    /// gönderim job'ı bunu "devralınan koşu" sayar ve gereksiz yere uyarı
    /// yazar. Kampanya duraklatıldı, sahipsiz kalmadı.</para>
    /// </summary>
    public async Task<int> ResumePausedCampaignsAsync(Guid licenseId, CancellationToken ct = default)
    {
        var paused = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId && c.Status == "paused")
            .ToListAsync(ct);
        if (paused.Count == 0) return 0;

        foreach (var c in paused)
        {
            c.Status = "pending";
            c.ClaimedAt = null;
        }
        await _db.SaveChangesAsync(ct);
        return paused.Count;
    }
```

`NetgsmAccountService.cs` dosyasının başında `Microsoft.EntityFrameworkCore`
zaten `using`'de; ek bir şey gerekmiyor.

- [ ] **Adım 4: Testin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountResumeTests
```
Beklenen: PASS (3 test).

- [ ] **Adım 5: Geçiş noktasına bağla**

`Failed → Verified` geçişi **tek bir yerde** olur: panel `PUT`'u. Günlük iş
yalnız `Verified` hesapları tarıyor (`ListVerifiedIdsAsync` + `VerifyOneAsync`
başındaki `acc.Status != Verified → return` kapısı), yani orada böyle bir
yükselme hiç gerçekleşmez. Oraya devam ettirme çağrısı koymak ölü kod olurdu —
koyma.

`PanelNetgsmAccountController.cs` — `PUT` içinde, doğrulama `Ok` dönüp hesap
`Verified` yazıldıktan ve `SaveChangesAsync` geçtikten SONRA:

```csharp
            // Kurulum geri geldi: admin kapatmasıyla duraklatılmış kampanyalar
            // devam etsin. Önceki durum zaten Verified idiyse duraklatılmış
            // kampanya olmaması gerekir; çağrı o durumda 0 döner ve zararsızdır.
            await _accounts.ResumePausedCampaignsAsync(acc.LicenseId, ct);
```

- [ ] **Adım 6: DI kayıtlarını kalıcılaştır**

Görev 8'de `Program.cs`'e geçici olarak eklenen iki satırı, komşu kayıtların
biçimine (tam nitelenmiş ad) getir. `NetgsmAccountService` kaydının hemen
altında, `Program.cs:195` civarında duruyor olmalılar:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifier>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifyJob>();
```

Satırlar yoksa ekle; varsa yalnız biçimi düzelt — iki kez kaydetme.

- [ ] **Adım 7: Cron kaydını ekle**

`Program.cs` — `"iys-consent-recovery"` kaydının hemen altına:

```csharp
            // Netgsm kurulum yeniden doğrulama — günde bir. Kimlik bilgileri
            // ya da marka kaydı yayıncı tarafında iptal edilirse gönderim
            // kapısı FAIL-CLOSED hâle gelsin. Günlük yeterli: İYS marka
            // kaydının bir gün içinde iptal olup aynı gün SMS gönderilmesi
            // senaryosunda bile kapı /iys/search sonucuna bakmaya devam eder.
            // Saat 04:35 UTC — yayın penceresinin (TR 20:00-01:00) dışında.
            manager.AddOrUpdate<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifyJob>(
                "netgsm-account-verify",
                j => j.RunAsync(CancellationToken.None),
                "35 4 * * *");  // günde bir, 04:35 UTC
```

- [ ] **Adım 8: Yönetici panelinden bağlantı ver**

`Pages/Admin/Index.cshtml` — İYS uyarı bloğunun (`@if (Model.IysDeadlineWarnings > 0)`)
hemen altına kalıcı bir giriş ekle:

```cshtml
<div class="alert alert-secondary d-flex justify-content-between align-items-center mt-3">
    <span>Yayıncı Netgsm / İYS kurulumları ve kapatma anahtarı.</span>
    <a asp-page="/Admin/Netgsm/Index" class="btn btn-sm btn-outline-secondary">Görüntüle</a>
</div>
```

- [ ] **Adım 9: Tüm sunucu paketini koş**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```
Beklenen: PASS. Testcontainers gerektiren testler için **Docker açık olmalı**;
gerekirse PowerShell'den `$env:DOCKER_HOST="npipe://./pipe/dockerDesktopLinuxEngine"`.

- [ ] **Adım 10: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountResumeTests.cs \
        OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs \
        OrderDeck.LicenseServer/Pages/Admin/Index.cshtml \
        OrderDeck.LicenseServer/Program.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): doğrulama geri geldiğinde kampanyalar devam eder + DI/cron

Devam ettirme kuyruğa atmıyor, yalnız paused→pending yazıyor; kuyruğa almayı
mevcut kurtarma süpürmesi yapıyor (kaçırılan Enqueue kaydı kaybeder, süpürme
kaybetmez). Günlük yeniden doğrulama işi 04:35 UTC'ye bağlandı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Kapanış

- [ ] **Dal bitişi**

Tüm görevler bittiğinde `superpowers:finishing-a-development-branch` becerisini
kullan: önce `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
yeşil olsun, sonra dört seçeneği sun.

**Merge/deploy penceresi:** `master`'a merge otomatik prod deploy'u tetikler.
TR saatiyle **20:00–01:00 arası yayın penceresi** — o aralıkta merge etme.

**Bu plan kapsamı DIŞINDA kalanlar:**
- React panel arayüzü (Netgsm kurulum formu + "kurulum eksik" uyarı şeridi) —
  `OrderDeck-Mobile` deposunda, ayrı PR.
- Ticari SMS kilidinin kaldırılması — `ITenantSmsSender` Plan 3'e ait.
  Bu plan bittikten sonra da `NetgsmSmsSender.SendAsync` ticari gönderimde
  `iys-tenant-sender-missing` atmaya devam eder; bu bilinçli.
