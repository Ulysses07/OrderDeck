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
| `Services/Sms/NetgsmAccountService.cs` | `UpsertAsync` + `ListVerifiedIdsAsync` + `CloseAccountAndPauseCampaignsAsync` + `StageResumePausedCampaignsAsync` eklenir. |
| `Data/LicenseDbContext.cs` (~851) | `BrandCode` indeksine `HasFilter` eklenir. |
| `Domain/SmsCampaign.cs` (32. satır) | `Status` doc'una `"paused"` eklenir. |
| `Services/Sms/SmsCampaignSendJob.cs` | Alıcı döngüsünde duraklama yoklaması + tamamlanma bloğunda iade idempotansı ve lease tazelemesi. |
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
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "0", "{\"code\":\"0\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Ok);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task Sorgu_verilen_hesap_baglamiyla_yapilir()
    {
        // Çok kiracıda en pahalı hata sınıfı: YANLIŞ markaya sormak. İstemciyi
        // `account` yerine boş/başka bir bağlamla çağıran bir mutasyon, alıcı da
        // sonuç da doğru kaldığı için diğer TÜM testleri geçerdi. `IIysClient`
        // bunu kendi doc'unda en kritik hata diye tanımlıyor; kardeş test
        // `NetgsmIysClientTests.Istek_basliginin_UCU_de_hesap_baglamindan_gelir`
        // bir alt katmanda aynı şeyi kilitliyor.
        IysAccountContext? seen = null;
        var client = new StubIysClient((a, _) =>
        {
            seen = a;
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });
        var account = NewAccount();

        await Verifier(client).VerifyAsync(account);

        seen.Should().BeSameAs(account,
            "istemciye VerifyAsync'e verilen hesabın ta kendisi geçmeli");
    }

    [Fact]
    public async Task Sorgu_sabit_prob_numarasiyla_yapilir()
    {
        // Doğrulama gerçek bir kişinin numarasını KULLANMAMALI: /iys/search
        // salt-okunur olsa da yayıncının müşteri listesinden rastgele bir
        // numara seçmek, doğrulama günlüklerine ilgisiz bir kişiyi düşürür.
        List<string>? seen = null;
        var client = new StubIysClient((_, r) =>
        {
            seen = r.ToList();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        });

        await Verifier(client).VerifyAsync(NewAccount());

        seen.Should().ContainSingle().Which.Should().Be(NetgsmAccountVerifier.ProbeRecipient);
    }

    [Theory]
    [InlineData("30", "API şifresi", "marka kodu")]
    [InlineData("60", "marka kodu", "API şifresi")]
    public async Task Yapilandirma_hatasi_Rejected(string code, string beklenenIs, string digerIs)
    {
        var client = new StubIysClient((_, _) => throw new IysConfigurationException(
            code, $"İYS yapılandırma hatası (code={code})"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Rejected);
        // Korunan şey kodun kendisi değil, yayıncıya verilen FARKLI iş
        // talimatı: 60'ta İYS panelinden marka kodu, 30'da abone no / API
        // şifresi düzeltilecek. Bu yüzden ipucunun VARLIĞI kadar diğerinin
        // YOKLUĞU da iddia ediliyor — `switch` silinip yalnız `_` dalı kalsa
        // mesaj hem "30"/"60"yı hem de her iki ipucunu birden içerirdi
        // ("Abone numarası, API şifresi ve marka kodunu kontrol edin"),
        // yani yalnız pozitif iddia o mutasyonu yakalamazdı.
        result.Message.Should().Contain(code,
                "yayıncı panelde ne düzelteceğini okuyabilmeli")
            .And.Contain(beklenenIs).And.NotContain(digerIs);
    }

    [Fact]
    public async Task Red_mesaji_ham_IYS_govdesini_tasimaz()
    {
        // Ham sağlayıcı yanıtı LastError'a, oradan da panele dönüyor: yayıncıya
        // gösterilecek bir metin değil. İstisna mesajı bugün gövdeyi taşımıyor
        // ama taşımaya başlarsa mesaj sabit kalmalı — gerekçenin tamamı için
        // `NetgsmVerifyResult.Message` doc'u. Sızıntıyı ölçmek için şifre
        // şeklinde bir belirteç kullanıyoruz.
        var secret = $"pw-{Guid.NewGuid():N}";
        var client = new StubIysClient((_, _) => throw new IysConfigurationException(
            "30", $"ham gövde: {{\"password\":\"{secret}\"}}"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Message.Should().NotContain(secret);
    }

    [Fact]
    public async Task Beklenmeyen_yanitta_ham_govde_mesaja_girmez()
    {
        // `result.Code` sınırsız uzunlukta olabilir (mekanizma:
        // `NetgsmAccountVerifier.Sanitize` doc'u). Ağ geçidi HTML hata sayfası
        // verdiğinde `result.Code` işte budur; o metin mesaja girerse
        // LastError'ın 500 karakterlik sütununu taşırır ve ham sağlayıcı yanıtı
        // yayıncının paneline düşer.
        var html = "<html><body>" + new string('x', 1500) + "</body></html>";
        var client = new StubIysClient((_, _) => new IysSearchResult(
            html, html, new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().NotContain("<html>");
        result.Message!.Length.Should().BeLessThan(200,
            "LastError sütunu 500 karakter; mesaj ham gövdeyle şişmemeli");
    }

    [Fact]
    public async Task Bilinen_kisa_kod_mesajda_gosterilir()
    {
        // Sanitize gereğinden fazla bastırmamalı: kısa rakamsal kod, yayıncının
        // Netgsm'e danışırken söyleyeceği tek somut bilgi.
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "70", "{\"code\":\"70\"}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().Contain("70");
    }

    [Fact]
    public async Task Esik_ustu_rakamsal_kod_bastirilir()
    {
        // Eşiğin var olma sebebi: kısa rakamsal kod yayıncının Netgsm'e
        // danışırken söyleyeceği tek somut bilgi; ondan uzun olan her şey ham
        // gövdedir (`Code` bütün yanıt gövdesi olabiliyor). 2 haneli kod ile
        // 1500 karakterlik HTML arasındaki mesafe o kadar geniş ki eşiği
        // 8'den 80'e çeken bir mutasyon ikisinde de hayatta kalır — sınırın
        // hemen üstünde, 9 haneli bir örnek şart.
        var code = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();
        var client = new StubIysClient((_, _) => new IysSearchResult(
            code, $"{{\"code\":\"{code}\"}}", new Dictionary<string, IysConsentStatus>()));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
        result.Message.Should().Contain("tanınmayan yanıt").And.NotContain(code);
    }

    private sealed class StubIysClient : IIysClient
    {
        // Delegate `account`'u DA taşıyor: atarsak, doğrulayıcıyı boş ya da
        // başka bir `IysAccountContext` ile çağıran mutasyon görünmez olur.
        private readonly Func<IysAccountContext, IReadOnlyList<string>, IysSearchResult> _search;
        public StubIysClient(Func<IysAccountContext, IReadOnlyList<string>, IysSearchResult> search)
            => _search = search;

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Doğrulama yalnız search kullanır.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            // `ct`'yi yok sayarsak, çağrıyı `CancellationToken.None` ile yapan
            // bir mutasyon TÜM testleri geçer — `Iptal_istegi_yutulmaz` bile,
            // çünkü muhafız çağıranın `ct`'sine bakıyor. Burada yoklamak o
            // mutasyonu görünür kılar: doğrulayıcının iptal sözleşmesi ancak
            // `ct` istemciye iletiliyorsa bir şey ifade eder.
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_search(account, recipients));
        }
    }
}
```

> **Not.** `ct.ThrowIfCancellationRequested()` Görev 2'nin inceleme turunda
> eklendi: Görev 1'in testleri iptal edilmiş bir jeton kullanmadığı için bu
> satır o turda davranışı değiştirmez, ama iptal sözleşmesi Görev 2'de
> doğduğunda stub'ın onu sessizce geçersiz kılmasını engeller.

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
/// <b>Ham İYS gövdesi buraya yazılmaz.</b> Gerekçe iki ayaklı: API şifresi
/// <i>istek</i> gövdesinin <c>header</c>'ında gidiyor (bkz.
/// <c>NetgsmIysClient.PostAsync</c>) — <i>yanıt</i> gövdesinde şifre
/// beklemiyoruz; ama yanıt gövdesi de ham sağlayıcı verisidir: yayıncıya
/// gösterilecek bir metin değil ve <c>LastError</c> sütununu taşırır.</param>
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
                $"İYS beklenmeyen yanıt kodu döndürdü ({Sanitize(result.Code)}). "
                + "Sorun sürerse Netgsm'e danışın.");
    }

    /// <summary>
    /// <c>result.Code</c> HER ZAMAN kısa bir kod değildir.
    /// <c>NetgsmIysClient.ReadCode</c>, gövdede <c>code</c> alanı bulamazsa
    /// <b>bütün gövdeyi</b> kod diye döndürüyor — üstelik
    /// <c>NetgsmIysClient.PostAsync</c> bunu <b>kırpılmamış</b> gövde üzerinde
    /// çağırıyor; 2000 karakterlik sınır yalnız döndürülen <c>RawBody</c>'ye
    /// uygulanıyor, yani <c>Code</c> sınırsız uzunlukta olabilir. Ağ geçidi bir
    /// HTML hata sayfası dönerse o HTML aynen <c>LastError</c>'a yazılır: hem
    /// 500 karakterlik sütunu taşırır (<c>DbUpdateException</c>), hem ham
    /// sağlayıcı yanıtını yayıncının paneline taşır. Yalnız kısa, rakamsal
    /// kodları göster: uzunluk eşiği rastgele değil — kısa rakamsal kod
    /// yayıncının Netgsm'e danışırken söyleyeceği tek somut bilgidir, o
    /// aralığın dışındaki her şey ham gövdedir.
    /// </summary>
    private static string Sanitize(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Length <= 8 && code.All(char.IsAsciiDigit)
            ? code
            : "tanınmayan yanıt";
}
```

Bu hâliyle `IysConfigurationException` yakalanmıyor — Adım 1'deki `Theory`
düşecek. Yakalamayı ekle (ve `Unavailable` dalını günlüğe bağla — `VerifyAsync`
gövdesinin son hâli budur):

```csharp
        try
        {
            var result = await _iys.SearchAsync(account, new[] { ProbeRecipient }, ct);
            if (result.Code == "0")
                return new NetgsmVerifyResult(NetgsmVerifyOutcome.Ok, null);

            // `Rejected` dalıyla simetrik günlük. Bu olmadan EN ZOR teşhis
            // edilen vaka hiç iz bırakmıyor: geçerli JSON ama `code` alanı yok
            // → `Sanitize` panele "tanınmayan yanıt" yazar, ham kod hiçbir yere
            // düşmez ("bütün yayıncılar Unavailable'a düştü, sebep ne?"
            // sorusu sunucu günlüğünden cevaplanamaz).
            // Yeni bir sır riski AÇMAZ: sunucu günlüğü yayıncıya gösterilmiyor
            // ve API şifresi İSTEK gövdesinde, yanıtta değil. Yine de kırpıyoruz
            // — `Code` sınırsız uzunlukta olabilir, gerekçe için `Sanitize` doc'u.
            var code = result.Code ?? "";
            _log.LogWarning(
                "Netgsm doğrulaması beklenmeyen yanıt: lisans={LicenseId} kod={Code} uzunluk={Length}",
                account.LicenseId, code.Length > 200 ? code[..200] : code, code.Length);

            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                $"İYS beklenmeyen yanıt kodu döndürdü ({Sanitize(result.Code)}). "
                + "Sorun sürerse Netgsm'e danışın.");
        }
        catch (IysConfigurationException ex)
        {
            // ex.Message'ı DEĞİL sabit metni döndürüyoruz: istisna mesajı ileride
            // ham gövdeyi taşımaya başlarsa ham SAĞLAYICI YANITI LastError'a,
            // oradan da panele düşerdi — gerekçenin tamamı için
            // `NetgsmVerifyResult.Message` doc'u.
            _log.LogWarning("Netgsm doğrulaması reddedildi: lisans={LicenseId} kod={Code}",
                account.LicenseId, ex.Code);
            // `_` dalı bugün ERİŞİLEMEZ (`NetgsmIysClient.PostAsync` yalnız
            // 30/60'ı fırlatıyor) ama "kod 30" diye sabitlemiyoruz: oraya üçüncü bir
            // kod eklendiği gün bu metin yayıncıya YANLIŞ işi yaptırırdı —
            // olmayan bir şifre sorununu kovalar.
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Rejected, ex.Code switch
            {
                "60" => "İYS marka kodu bu Netgsm hesabına ait değil (kod 60). "
                        + "Marka kodunu İYS panelinden kontrol edin.",
                "30" => "Netgsm abone numarası veya API şifresi reddedildi (kod 30).",
                _ => $"İYS kurulumu reddedildi (kod {Sanitize(ex.Code)}). "
                     + "Abone numarası, API şifresi ve marka kodunu kontrol edin.",
            });
        }
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: PASS (9 test — 7 `Fact` + `Theory` iki kez).

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
        var client = new StubIysClient((_, _) => throw new HttpRequestException("bağlantı yok"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable,
            "ağ arızası hesap hakkında HİÇBİR ŞEY söylemez; Rejected desek "
            + "İYS'nin yarım saatlik kesintisi çalışan her yayıncıyı kapatırdı");
    }

    [Fact]
    public async Task Zaman_asimi_Unavailable()
    {
        var client = new StubIysClient((_, _) => throw new TaskCanceledException("timeout"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable);
    }

    [Fact]
    public async Task Istemci_ici_iptal_Unavailable()
    {
        // (a) Neden ATA tip. `IIysClient` bir ARAYÜZ: bir uygulama isteğin
        //     başına kendi zaman aşımı jetonunu (`CancellationTokenSource`)
        //     bağlayabilir. O jeton söndüğünde `ThrowIfCancellationRequested`
        //     ATAYI fırlatır — düz `OperationCanceledException`, alt tipi
        //     `TaskCanceledException` değil — üstelik ÇAĞIRANIN `ct`'si
        //     bozulmamıştır. Bu geçici arıza `Unavailable` olmalı, dışarı
        //     kaçmamalı: kaçarsa doğrulama turu yarıda kalır ve yayıncının
        //     kurulumu o tur boyunca belirsiz kalır.
        // (b) `Iptal_istegi_yutulmaz`'dan farkı. O test iptal EDİLMİŞ bir `ct`
        //     ile çalışır; oradaki iddia iki şeydir — gerçek iptalin yutulmadığı
        //     ve `ct`'nin istemciye iletildiği. İstisnayı da ilk sıradaki
        //     `when (ct.IsCancellationRequested)` muhafızı yakalar, yani geniş
        //     filtrenin TİPİ o yolda hiç devreye girmez. Burada `ct` iptal
        //     EDİLMEMİŞ (stub'ın `ThrowIfCancellationRequested`'ı bu yüzden
        //     sessiz kalır, istisna delegate'ten gelir): muhafız elemez ve karar
        //     tamamen geniş filtrenin tipine kalır. Filtreyi
        //     `TaskCanceledException`'a daraltan mutasyon ata tipi kaçırır,
        //     istisna `VerifyAsync`'ten dışarı çıkar ve bu test kırmızıya döner.
        var client = new StubIysClient((_, _) => throw new OperationCanceledException(
            "istemci içi zaman aşımı"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable,
            "çağıranın jetonu sağlamken gelen iptal istisnası geçici bir arızadır");
    }

    [Fact]
    public async Task Ayristirma_hatasi_Unavailable()
    {
        // Dal SAVUNMA amaçlı: bugünkü `NetgsmIysClient` `JsonException`'ı iki
        // yerde kendi içinde yutuyor (`SearchAsync` ayrıştırması + `ReadCode`),
        // dolayısıyla üretimde neredeyse erişilemez. Ama `IIysClient` bir arayüz;
        // başka bir uygulama ayrıştırma hatasını dışarı verebilir ve o gün
        // yayıncının kurulumu kapanmamalı. Test olmadan `or JsonException`
        // satırını silen mutasyon görünmez kalıyordu.
        var client = new StubIysClient((_, _) => throw new System.Text.Json.JsonException(
            "beklenmeyen belirteç"));

        var result = await Verifier(client).VerifyAsync(NewAccount());

        result.Outcome.Should().Be(NetgsmVerifyOutcome.Unavailable,
            "bozuk yanıt gövdesi hesap hakkında HİÇBİR ŞEY söylemez");
    }

    [Fact]
    public async Task Sistem_hatasi_kodu_Unavailable()
    {
        // İYS "100 = sistem hatası" gibi kodlar da döndürüyor. Bunlar
        // yapılandırmayla ilgili DEĞİL; NetgsmIysClient yalnız 30/60'ı
        // IysConfigurationException'a çeviriyor, gerisi buraya düz kod olarak
        // geliyor ve hesabı düşürmemeli.
        var client = new StubIysClient((_, _) => new IysSearchResult(
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
        //
        // Test İKİ şeyi birden kilitliyor. (1) `ct` doğrulayıcıdan istemciye
        // GERÇEKTEN iletiliyor: delegate iptali değil BAŞARIYI döndürüyor, yani
        // istisna yalnız stub'ın `ThrowIfCancellationRequested`'ından gelebilir —
        // çağrı `CancellationToken.None` ile yapılsaydı test `Ok` alıp düşerdi.
        // (2) Gerçek iptal yutulmuyor: `Unavailable`'a çevrilseydi yine düşerdi.
        //
        // Beklenen tip `OperationCanceledException`, çünkü
        // `ThrowIfCancellationRequested` atayı fırlatır. Dikkat: bu test
        // doğrulayıcının filtresindeki ata-tip GENİŞLETMESİNİ kanıtlamaz —
        // iptal edilmiş `ct`'yi `when (ct.IsCancellationRequested)` muhafızı
        // zaten ilk sırada yakalayıp yeniden fırlatıyor.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new StubIysClient((_, _) => new IysSearchResult(
            "0", "{\"code\":\"0\"}", new Dictionary<string, IysConsentStatus>()));

        var act = async () => await Verifier(client).VerifyAsync(NewAccount(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: `Ag_hatasi_Unavailable`, `Zaman_asimi_Unavailable`,
`Istemci_ici_iptal_Unavailable` ve `Ayristirma_hatasi_Unavailable` FAIL
(`HttpRequestException` / `TaskCanceledException` /
`OperationCanceledException` / `JsonException` yakalanmadan dışarı çıkıyor).
Diğer ikisi zaten geçer — `Iptal_istegi_yutulmaz` bu turda yanıltıcı biçimde
yeşildir: henüz hiçbir `catch` olmadığı için istisna zaten dışarı çıkıyor.
Anlamını Adım 3'ten SONRA kazanır (o zaman yutulmadığını kanıtlar).

- [ ] **Adım 3: En küçük uygulamayı yaz**

`NetgsmAccountVerifier.VerifyAsync` içindeki `catch (IysConfigurationException ex)`
bloğunun ALTINA:

```csharp
        // İptal GERÇEKTEN istendiyse yutma: kapanış turu her hesaba
        // "ulaşılamadı" yazmamalı. Muhafız gövdede değil `when`'de: istisna
        // filtresi BİRİNCİ GEÇİŞTE, yığın çözülmeden çalışır — `false` dönerse
        // bu çerçeveye hiç girilmez, `true` dönerse asıl fırlatma noktası
        // korunur. Gövdeye yazılan `throw;` ise async'te "End of stack trace
        // from previous location" sınırı ekleyip izi bulandırırdı. Depoda aynı
        // desen 6 yerde böyle yazılı; en yakını kardeş işler
        // `IysConsentVerifyJob` ve `IysConsentPushJob`.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // `TaskCanceledException` DEĞİL atası. `IIysClient` bir ARAYÜZ:
        // `ct.ThrowIfCancellationRequested()` çağıran bir uygulama düz
        // `OperationCanceledException` fırlatır, dar tip onu kaçırırdı. Buradaki
        // kazanç yalnız ÇAĞIRANIN `ct`'si İPTAL EDİLMEMİŞKEN gelen iptal
        // istisnasıdır — gerçek iptali yukarıdaki muhafız zaten alıp yeniden
        // fırlatıyor. Örnek: istemci kendi içinde istek başına zaman aşımı
        // jetonu bağlarsa, o jeton yandığında çağıranın `ct`'si sağlamdır ve
        // bu geçici arıza `Unavailable` olmalı, dışarı kaçmamalı. Bugünkü tek
        // istemcide erişilemez (`HttpClient` zaman aşımı `TaskCanceledException`
        // atıyor) ama ata tipi yazmanın maliyeti sıfır.
        // `JsonException` dalı SAVUNMA amaçlı: bugünkü `NetgsmIysClient` onu iki
        // yerde kendi içinde yutuyor (`SearchAsync` ayrıştırması + `ReadCode`),
        // geriye yalnız `JsonSerializer.Serialize` kalıyor ve o pratikte
        // fırlatmaz. Ama arayüzün başka bir uygulaması ayrıştırma hatasını
        // dışarı verebilir; o gün yayıncının kurulumu kapanmamalı.
        catch (Exception ex) when (ex is HttpRequestException
                                     or OperationCanceledException
                                     or System.Text.Json.JsonException)
        {
            _log.LogWarning(ex, "Netgsm doğrulaması ulaşılamadı: lisans={LicenseId}",
                account.LicenseId);
            return new NetgsmVerifyResult(NetgsmVerifyOutcome.Unavailable,
                "İYS'ye şu an ulaşılamadı. Kurulumunuz kapatılmadı, doğrulama "
                + "kendiliğinden tekrar denenecek.");
        }
```

> **Sıra bağlayıcı.** `catch (IysConfigurationException ex)` en üstte kalmalı:
> kesin ret kararının önceliği odur. İptal muhafızı geniş filtreden ÖNCE
> gelmeli, yoksa gerçek iptal `Unavailable`'a çevrilip yutulur.

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifierTests
```
Beklenen: PASS (15 test — Görev 1'in 9'u + buradaki 6; Görev 1 inceleme
turlarında iki test daha ve iki durumlu bir `[Theory]` kazandı).

> **`Istemci_ici_iptal_Unavailable` mutasyonla ÖLÇÜLDÜ.** Geniş filtredeki
> `or OperationCanceledException` elle `or TaskCanceledException`'a
> daraltıldığında tam olarak bu tek test kırmızıya döndü (1 fail / 14 pass),
> sonra mutasyon geri alındı. Ata-tip genişletmesini kilitleyen tek test budur:
> `Iptal_istegi_yutulmaz` aynı mutasyon altında yeşil kalıyor, çünkü iptal
> edilmiş `ct`'yi ilk sıradaki muhafız yakalıyor.

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

## Görev 3: `NetgsmAccountService.UpsertAsync` + hesap sürüm jetonu

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs:113` (iki `SaveChanges` override'ı) ve `:824` (`NetgsmAccount` eşlemesi)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVersionTests.cs`

> **Bu görev üç işi birden yapıyor ve bunlar ayrılamaz.** `UpsertAsync` hesabı
> `Failed`'a düşürüyor; eğer aynı `SaveChanges` içinde lisansın koşan
> kampanyalarını da duraklatmazsa, marka çözülemez hâle gelirken işçi gönderime
> devam eder. Duraklatma da tek başına yetmez: işçi kampanyayı zaten okumuşsa
> `ClaimedAt` jetonu ilerlemedikçe üstlenme yazımını kazanır. Son olarak
> hesabın kendisinde eşzamanlılık jetonu yoksa, doğrulama sürerken değişen bir
> satırın üzerine bayat sonuç yazılır. Üçü de aynı dosyalara dokunduğu için tek
> görevde toplandı.

- [ ] **Adım 1: Düşen testi yaz**

`NetgsmAccountServiceTests.cs` dosyasının sonuna (son `}`'tan önce) ekle.

> **Sınıfın gerçek şeklini bozma.** Bu sınıf `IClassFixture` **kullanmıyor** ve
> `_factory` diye bir alanı **yok**; her test kendi InMemory bağlamını
> `NewDb()` ile açıp servisi `Service(db)` ile kuruyor
> (`NetgsmAccountServiceTests.cs:17-25`). `NewUserCode()` ve `Seed()`
> yardımcıları **zaten tanımlı** (`:29`, `:32`) — yeniden tanımlarsan `CS0111`
> alırsın. Yalnız `NewBrandCode()` ve `SeedCampaign()` yeni.
>
> Lisans satırı seed etmeye gerek yok: ne `NetgsmAccount.LicenseId` ne de
> `SmsCampaign.LicenseId` InMemory'de yabancı anahtar doğrulamasına giriyor ve
> `UpsertAsync` lisansı okumuyor. `Guid.NewGuid()` yeterli — mevcut `Seed()`
> yardımcısı da böyle çalışıyor.
>
> **Bu testler Görev 3'ün değişmezlerini mutasyonla öldürüyor.**
> `Upsert_dogrulanmis_hesabi_kapatir` olmadan `account.Status = Failed;`
> satırı silinse hiçbir test kırılmaz — yeni satır testinde `Failed` zaten
> enum varsayılanı (0), yani mevcut `Verified` satırın düşürülmesi ölçülmemiş
> kalır. `Upsert_kosan_kampanyalari_duraklatir` olmadan da
> `StagePauseActiveCampaignsAsync` çağrısı tamamen silinebilir: bu görevden
> önce test dosyalarında tek bir `SmsCampaign` satırı yok.
>
> **İddiaları İZLENEN örnekte bırakma.** Servis testle aynı `DbContext`'i
> kullanıyor; `Seed`/`SeedCampaign`'in döndürdüğü nesneyi bellekte değiştirmesi
> "alan atandı mı"yı yeşil yapar ama "kaydedildi mi"yi hiç ölçmez. Ölçüldü:
> `StagePauseActiveCampaignsAsync` çağrısı `SaveChangesAsync`'ten SONRAYA
> taşındığında — yani duraklatma hiç kaydedilmediğinde — yalnız `AsNoTracking`
> iddiası düşüyor, izlenen-örnek iddiaları yeşil kalıyor. Bu yüzden iki
> duraklatma/kapatma testi de kalıcı hâli ayrıca okuyor; ikisi birlikte durur.
>
> `Upsert_ileri_tarihli_jetonu_geri_almaz` `NextClaimedAt`'in monoton kolunu
> koşturan TEK test: varsayılan `SeedCampaign` jetonu geçmişte olduğu için
> `previous.AddTicks(1)` dalı aksi hâlde hiç koşmuyor ve
> `campaign.ClaimedAt = DateTimeOffset.UtcNow` mutasyonu fark edilmiyordu.
>
> `Upsert_cakisma_firlatirken_izlenen_nesne_birakmaz` ise
> `catch (DbUpdateException)` dalındaki `ChangeTracker.Clear()`'ı koruyor —
> o satır silindiğinde `Services.Sms|Services.Iys` kümesinin tamamı yeşil
> kalıyordu.

```csharp
    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    [Fact]
    public async Task Upsert_yeni_hesabi_DOGRULANMAMIS_acar()
    {
        using var db = NewDb();
        var svc = Service(db);

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "fail-closed: doğrulama henüz koşmadı, satır kapalı doğar");
        acc.LastVerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_sifreyi_sifreli_saklar()
    {
        using var db = NewDb();
        var svc = Service(db);
        var raw = $"pw-{Guid.NewGuid():N}";

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), raw, "ORDERDECK", NewBrandCode(),
            CancellationToken.None);

        acc.PasswordProtected.Should().NotBe(raw, "düz metin şifre DB'ye yazılmaz");
        svc.TryUnprotectPassword(acc.PasswordProtected).Should().Be(raw);
    }

    [Fact]
    public async Task Upsert_bos_sifreyle_saklanani_korur()
    {
        // Panel şifreyi geri GÖSTERMİYOR (yalnız "girildi/girilmedi").
        // Yayıncı başlığını düzeltmek için formu kaydettiğinde şifre alanı boş
        // gelir; boşu kaydedersek çalışan kurulumu kendi elimizle bozarız.
        using var db = NewDb();
        var svc = Service(db);
        var licenseId = Guid.NewGuid();
        var raw = $"pw-{Guid.NewGuid():N}";
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
        using var db = NewDb();
        var svc = Service(db);

        var act = async () => await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), null, "ORDERDECK", NewBrandCode(),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Upsert_lisans_basina_tek_satir_tutar()
    {
        using var db = NewDb();
        var svc = Service(db);
        var licenseId = Guid.NewGuid();

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
        using var db = NewDb();
        var svc = Service(db);
        var brandCode = NewBrandCode();
        var userCode = NewUserCode();

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), $"  {userCode} ", $"pw-{Guid.NewGuid():N}",
            " ORDERDECK ", $" {brandCode} ", CancellationToken.None);

        acc.BrandCode.Should().Be(brandCode);
        acc.Header.Should().Be("ORDERDECK");
        acc.UserCode.Should().Be(userCode);
    }

    [Fact]
    public async Task Upsert_dogrulanmis_hesabi_kapatir()
    {
        // Kimlikler değiştiyse eski doğrulama geçersizdir: Verified korunursa
        // yanlış kimlikle "açık" duran bir kurulum kalır ve marka çözülmeye
        // devam eder. Yeni satırda Failed zaten enum varsayılanı (0) — asıl
        // güvenlik özelliği MEVCUT Verified satırın düşürülmesi, burada ölçülen o.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();

        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Verified);
        account.LastVerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        account.LastError = null;
        await db.SaveChangesAsync();

        var acc = await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "kimlikler değişti: eski doğrulama artık geçerli değil");
        acc.LastVerifiedAt.Should().BeNull("bayat doğrulama damgası taşınmamalı");
        acc.LastError.Should().BeNull();

        // İzlenen nesne üstündeki iddialar yalnız "alan atandı mı"yı ölçer:
        // servis aynı bağlamı kullandığı için nesneyi bellekte değiştirmesi
        // yeter. Kalıcı hâli ayrıca oku — `AsNoTracking` InMemory'de saklanan
        // değerlerden YENİ örnek materyalize ediyor, yani gerçekten diske
        // ineni görüyoruz.
        var persisted = await db.NetgsmAccounts.AsNoTracking()
            .FirstAsync(a => a.LicenseId == licenseId);
        persisted.Status.Should().Be(NetgsmAccountStatus.Failed,
            "kapatma kaydedilmeliydi, yalnız bellekte kalmamalı");
        persisted.LastVerifiedAt.Should().BeNull();
        persisted.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_kosan_kampanyalari_duraklatir()
    {
        // Hesap Failed olduğu anda marka çözülemez; kampanya açık kalırsa işçi
        // izinsiz gönderime devam eder. Duraklatma hesap yazımıyla AYNI
        // SaveChanges içinde olmalı, yoksa arada açık bir pencere kalır.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var otherLicenseId = Guid.NewGuid();

        var pending = SeedCampaign(db, licenseId, "pending");
        var sending = SeedCampaign(db, licenseId, "sending");
        var foreignPending = SeedCampaign(db, otherLicenseId, "pending");
        await db.SaveChangesAsync();

        var pendingClaim = pending.ClaimedAt;
        var sendingClaim = sending.ClaimedAt;

        await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        pending.Status.Should().Be("paused");
        sending.Status.Should().Be("paused");
        foreignPending.Status.Should().Be(
            "pending", "başka yayıncının kampanyası bu kurulumdan etkilenmemeli");

        pending.ClaimedAt.Should().BeAfter(pendingClaim!.Value,
            "jeton ilerlemezse kampanyayı zaten okumuş işçi üstlenmeyi kazanır");
        sending.ClaimedAt.Should().BeAfter(sendingClaim!.Value);

        // Yukarıdaki iddialar İZLENEN örnekler üstünde: servis aynı bağlamı
        // kullandığı için onları bellekte değiştirmesi yeter ve duraklatma hiç
        // kaydedilmese bile yeşil kalırlar. Asıl değişmez "aynı SaveChanges'te
        // indi mi" — onu kalıcı hâlden oku (`AsNoTracking` InMemory'de saklanan
        // değerlerden yeni örnek materyalize ediyor).
        var persisted = await db.SmsCampaigns.AsNoTracking()
            .Where(c => c.LicenseId == licenseId).ToListAsync();
        persisted.Should().HaveCount(2);
        persisted.Should().OnlyContain(c => c.Status == "paused",
            "duraklatma hesap yazımıyla AYNI SaveChanges'te inmeli");

        var persistedForeign = await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.LicenseId == otherLicenseId);
        persistedForeign.Status.Should().Be("pending",
            "başka yayıncının kampanyası bu kurulumdan etkilenmemeli");
    }

    [Fact]
    public async Task Upsert_ileri_tarihli_jetonu_geri_almaz()
    {
        // `NextClaimedAt`'in monoton muhafızı: `UtcNow` monoton DEĞİL. Jeton
        // ileri tarihliyken ham `UtcNow` ataması onu GERİ alır ve kapatmadan
        // ÖNCE kampanyayı okumuş işçi üstlenme yazımını kazanır — yani
        // duraklatma sessizce delinir.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var originalClaim = DateTimeOffset.UtcNow.AddMinutes(5);

        var campaign = SeedCampaign(db, licenseId, "sending", originalClaim);
        await db.SaveChangesAsync();

        await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        campaign.ClaimedAt.Should().Be(originalClaim.AddTicks(1),
            "saat jetonun gerisindeyken tek güvenli sonraki değer özgün + 1 tik");
    }

    /// <summary><paramref name="claimedAt"/> verilmezse jeton GEÇMİŞTE kalır
    /// ve <c>NextClaimedAt</c> hep "saat ilerledi" kolunu seçer; ileri tarihli
    /// jeton veren test monoton muhafızın öbür kolunu koşturur.</summary>
    private static SmsCampaign SeedCampaign(
        LicenseDbContext db, Guid licenseId, string status,
        DateTimeOffset? claimedAt = null)
    {
        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            MessageBody = "Kurulum testi",
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            ReservedCredits = 1,
            Status = status,
            ClaimedAt = claimedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedByCustomerId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        db.SmsCampaigns.Add(campaign);
        return campaign;
    }

    [Fact]
    public async Task Upsert_disabled_hesabi_acmaz()
    {
        // Admin kill switch'i servis katmanında tutuluyor: controller ön kontrolü
        // yalnız erken ve anlaşılır bir 409 üretmek için var. Kapı burada olmazsa
        // panelin ön kontrolü ile yazım arasındaki pencerede kapatılan hesap,
        // yayıncının kaydıyla yeniden açılır.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();

        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Disabled);

        var originalPassword = account.PasswordProtected;

        Func<Task> write = async () =>
        {
            await Service(db).UpsertAsync(
                licenseId,
                NewUserCode(),
                $"pw-{Guid.NewGuid():N}",
                "ORDERDECK",
                account.BrandCode,
                CancellationToken.None);
        };

        await write.Should().ThrowAsync<NetgsmAccountDisabledException>();
        account.Status.Should().Be(NetgsmAccountStatus.Disabled);
        account.PasswordProtected.Should().Be(originalPassword);
    }

    [Fact]
    public async Task Upsert_cakisma_firlatirken_izlenen_nesne_birakmaz()
    {
        // `UpsertAsync` paylaşılan scoped bağlamda çalışıyor. Çakışmayla
        // fırlarken yarı-yazılmış hesabı izleniyor bırakırsa, o scope'ta
        // atılacak SONRAKİ herhangi bir `SaveChanges` onu kimsenin karar
        // vermediği bir anda diske basar. Kardeş metot
        // `CloseAccountAndPauseCampaignsAsync` için aynı iddia kuruluyor;
        // simetri burada da ölçülmeli.
        var databaseName = $"netgsm-{Guid.NewGuid():N}";
        using var db = NewDb(databaseName);
        var licenseId = Guid.NewGuid();

        Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Failed);

        // Jetonu ARKADAN kaydır: ikinci bağlam aynı satırı yazıyor, `db`'nin
        // izlediği kopyanın özgün sürümü bayatlıyor. Yeni jeton değerini
        // `LicenseDbContext.StampNetgsmAccountVersions()` üretiyor — burada
        // önemli olan yazımın BAŞKA bir bağlamdan gelmesi.
        using (var other = NewDb(databaseName))
        {
            var row = await other.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);
            row.UpdatedAt = row.UpdatedAt.AddMinutes(1);
            await other.SaveChangesAsync();
        }

        Func<Task> write = async () => await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        await write.Should().ThrowAsync<DbUpdateConcurrencyException>();

        db.ChangeTracker.Entries().Should().BeEmpty(
            "fırlatmadan önce temizlenmeli: kirli nesne çağıranın sonraki "
            + "SaveChanges'ine biner");
    }
```

Son test AYNI InMemory veritabanına ikinci bir bağlam istiyor; mevcut
`NewDb()` yardımcısına (`NetgsmAccountServiceTests.cs:17`) isteğe bağlı bir
parametre ekle — varsayılanı bugünkü davranış:

```csharp
    /// <summary><paramref name="databaseName"/> verilirse AYNI InMemory
    /// veritabanına ikinci bir bağlam açılabilir — eşzamanlılık yarışını
    /// kurmak için şart: jetonu "arkadan" kaydıran yazım, test edilen
    /// bağlamın izlemediği bir yerden gelmeli.</summary>
    private static LicenseDbContext NewDb(string? databaseName = null)
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"netgsm-{Guid.NewGuid():N}").Options);
```

Dosyanın `using` bloğu zaten `FluentAssertions`,
`Microsoft.EntityFrameworkCore`, `OrderDeck.LicenseServer.Data`,
`OrderDeck.LicenseServer.Domain`, `OrderDeck.LicenseServer.Services.Sms` ve
`Xunit` içeriyor — yenisine gerek yok.

Ayrıca YENİ dosya
`OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVersionTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public sealed class NetgsmAccountVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parola_yazimi_saat_ilerlemese_bile_bayat_yaziyi_reddeder(
        bool useAsyncSave)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"netgsm-version-{Guid.NewGuid():N}")
            .Options;

        var accountId = Guid.NewGuid();
        var originalStamp = DateTimeOffset.UtcNow.AddDays(1);
        var replacement = $"protected-{Guid.NewGuid():N}";

        await using (var seed = new LicenseDbContext(options))
        {
            seed.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = accountId,
                LicenseId = Guid.NewGuid(),
                UserCode = Random.Shared
                    .NextInt64(8_500_000_000, 8_599_999_999).ToString(),
                PasswordProtected = $"protected-{Guid.NewGuid():N}",
                Header = "ORDERDECK",
                BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
                Status = NetgsmAccountStatus.Failed,
                CreatedAt = originalStamp,
                UpdatedAt = originalStamp,
            });

            await seed.SaveChangesAsync();
        }

        await using var verifier = new LicenseDbContext(options);
        await using var writer = new LicenseDbContext(options);

        var stale = await verifier.NetgsmAccounts
            .SingleAsync(a => a.Id == accountId);
        var current = await writer.NetgsmAccounts
            .SingleAsync(a => a.Id == accountId);

        // Yazıcı UpdatedAt atamayı unutsa bile koruma çalışmalı.
        current.PasswordProtected = replacement;

        if (useAsyncSave)
            await writer.SaveChangesAsync();
        else
            writer.SaveChanges();

        current.UpdatedAt.Should().Be(originalStamp.AddTicks(1));
        stale.Status = NetgsmAccountStatus.Verified;

        Func<Task> staleWrite = async () =>
        {
            if (useAsyncSave)
                await verifier.SaveChangesAsync();
            else
                verifier.SaveChanges();
        };

        await staleWrite.Should()
            .ThrowAsync<DbUpdateConcurrencyException>();

        await using var read = new LicenseDbContext(options);
        var persisted = await read.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == accountId);

        persisted.PasswordProtected.Should().Be(replacement);
        persisted.Status.Should().Be(NetgsmAccountStatus.Failed);
        persisted.UpdatedAt.Should().Be(originalStamp.AddTicks(1));
    }
}
```

> **Neden InMemory yeterli, neden gelecek tarihli damga.** EF InMemory
> sağlayıcısı eşzamanlılık jetonunu **uyguluyor**: kaydın özgün jeton değerini
> depodakiyle karşılaştırıp uyuşmazlıkta `DbUpdateConcurrencyException`
> atıyor. Uygulamadığı tek şey birden çok entity'nin SQL transaction'ıyla
> birlikte geri alınması — bu testin böyle bir iddiası yok (tek satır).
> `CLAUDE.md`'deki "InMemory'nin eşzamanlılık semantiği yok" genellemesi bu
> ayrımı kaçırıyor; **satır-içi jeton reddi InMemory'de kanıtlanabilir**, çok
> entity'li rollback kanıtlanamaz (o Görev 7'de Testcontainers'a gidiyor).
>
> Seed damgası **bir gün ileride**: böylece `DateTimeOffset.UtcNow` mutlaka
> özgün damgadan küçük kalır ve `max(UtcNow, özgün + 1 tick)` dalının "saat
> ilerlemedi" kolu deterministik koşar. `Thread.Sleep` ya da saat
> çözünürlüğüne bağlı flaky bir teste gerek kalmaz.

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~NetgsmAccountServiceTests|FullyQualifiedName~NetgsmAccountVersionTests"
```
Beklenen: derleme hatası — `UpsertAsync` ve `NetgsmAccountDisabledException`
yok.

- [ ] **Adım 3: En küçük uygulamayı yaz**

**3a — Hesaba eşzamanlılık jetonu ver.** `LicenseDbContext.cs:824`'teki
`NetgsmAccount` eşlemesinin içine, `b.HasKey(a => a.Id);` satırının hemen
altına:

```csharp
            b.Property(a => a.UpdatedAt).IsConcurrencyToken();
```

> **Neden yeni sütun/göç yok.** `NetgsmAccount.UpdatedAt` sütunu **zaten var**
> (`Domain/NetgsmAccount.cs:92`). `IsConcurrencyToken()` yalnız EF model
> metadata'sı; üretilen `UPDATE`'e `WHERE ... AND [UpdatedAt] = @özgün`
> ekletir. Veri göçü gerekmez — ama Görev 7'de oluşturulacak migration'ın
> **model snapshot'ı bu metadata'yı da içermeli**, yoksa sonraki `dotnet ef
> migrations add` sahte bir fark üretir.
>
> `rowversion` daha güçlü bir mekanizma ama **yeni sütun** ister; koşullu
> `ExecuteUpdate` ise hesap ile kampanya yazımlarını birleştirmek için açık
> transaction + etkilenen satır sayısı kontrolü + tracker yönetimi gerektirir,
> yani daha kısa değil daha uzun. Var olan sütun bu ikisinden ucuz.

**3b — Jetonu her yazımda kesin ilerlet.** Tek başına `IsConcurrencyToken()`
YETMEZ: `UpdatedAt` atamayı unutan yazım yolları (panelin çözülemeyen şifre
dalı, entity'yi doğrudan değiştiren mevcut testler) jetonu hiç ilerletmez ve
koruma sahte olur. Atayan yollar da yalnız `DateTimeOffset.UtcNow` yazıyor —
`UtcNow` **ne benzersiz ne monoton**, dolayısıyla "farklı sürüm" garantisi
vermez. Damgalamayı tek merkeze al: `LicenseDbContext.cs:113`'teki iki
override'ı aşağıdaki gövdelerle değiştir ve yardımcıyı aynı sınıfa ekle.
`SyncDerivedColumns` aynen korunuyor. "Dördünü birden override etme"
yorumu da korunuyor ama **`SaveChanges(bool)` satırının hemen üstüne** iner:
araya `StampNetgsmAccountVersions` girdiği için eski yerinde kalsaydı onun
başlığı gibi okunurdu — anlattığı metot o değil.

```csharp
    /// <summary>
    /// <see cref="NetgsmAccount.UpdatedAt"/> bir eşzamanlılık jetonu; her
    /// güncellemede <b>kesin</b> ilerlemesi gerekiyor. Çağıranların
    /// <c>UtcNow</c> atamasına güvenemeyiz: atamayı unutan yol jetonu hiç
    /// ilerletmez, atayan yol da saat geri gittiyse/ilerlemediyse aynı değeri
    /// yazabilir. <c>max(UtcNow, özgün + 1 tick)</c> ikisini de kapatır — iki
    /// yazar aynı sonraki değeri üretse bile <c>WHERE UpdatedAt = özgün</c>
    /// nedeniyle yalnız biri kazanır.
    /// </summary>
    private void StampNetgsmAccountVersions()
    {
        foreach (var entry in ChangeTracker.Entries<NetgsmAccount>())
        {
            if (entry.State != EntityState.Modified)
                continue;

            var version = entry.Property(a => a.UpdatedAt);
            var original = version.OriginalValue;
            var now = DateTimeOffset.UtcNow;

            version.CurrentValue = now > original
                ? now
                : original.AddTicks(1);

            version.IsModified = true;
        }
    }

    // DİKKAT — parametresiz SaveChanges() ve SaveChangesAsync(ct) aşırı yüklemeleri
    // BİLEREK override edilmedi: EF'te ikisi de burada override edilen
    // (bool acceptAllChangesOnSuccess, …) sürümüne yönleniyor. Yani asıl zincir
    // bu ikisi; dördünü birden override etmek aynı işi iki kez yaptırırdı.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncDerivedColumns();
        StampNetgsmAccountVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncDerivedColumns();
        StampNetgsmAccountVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
```

`EntityState.Added` bilerek dışarıda: yeni satırın seed damgası
(`CreatedAt == UpdatedAt`) bozulmuyor, `OriginalValue` de henüz anlamlı değil.

> **Mevcut testleri kırar mı?** `UpdatedAt` atamadan `NetgsmAccount`
> değiştiren şu yollar merkezi damgalamayla kendiliğinden kapsanıyor, teste
> yapay timestamp eklemek gerekmiyor:
> `SmsCampaignIysGateTests.cs:181-183`, `IysConsentPushJobTests.cs:344-349` ve
> `:391-396`, `IysConsentVerifyJobTests.cs:328-333` ve `:443-448`. Jeton
> yalnız **başka bir bağlamın değiştirdiği** satıra bayat sürümle yazanı
> reddeder; tek bağlamdan yazan mevcut testler etkilenmez.

**3c — Ortak duraklatma yardımcıları.** `NetgsmAccountService.cs` içine,
`GetBrandCodeAsync`'in ÜSTÜNE:

```csharp
    /// <summary>
    /// Kampanya sahiplik jetonunun (<see cref="SmsCampaign.ClaimedAt"/>) bir
    /// sonraki değeri. <c>UtcNow</c> monoton olmadığı için ham atama, jetonu
    /// yerinde bırakabilir ya da geri alabilir; her iki durumda da kapatmadan
    /// ÖNCE kampanyayı okumuş bir işçi üstlenme yazımını kazanır.
    /// </summary>
    private static DateTimeOffset NextClaimedAt(DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        return previous.HasValue && now <= previous.Value
            ? previous.Value.AddTicks(1)
            : now;
    }

    /// <summary>
    /// Lisansın koşan/kuyruktaki kampanyalarını <c>paused</c> olarak
    /// <b>hazırlar</b> — kaydetmez. Çağıran, hesap yazımıyla aynı
    /// <c>SaveChanges</c> içinde kaydeder; böylece hesap kapanırken kampanya
    /// açık kalan bir ara durum oluşmaz.
    ///
    /// <para>Jetonu ilerletmek işin YARISI değil tamamı: durum yazımı tek
    /// başına, kampanyayı zaten okumuş işçinin <c>sending</c> yazımını
    /// engellemez.</para>
    ///
    /// <para><b>Kredi iade edilmez</b> — kalan alıcılar <c>pending</c> kalıyor
    /// ve rezervasyon tam olarak onların karşılığı. İade, kampanya gerçekten
    /// tamamlandığında (<c>failed</c> alıcı sayısına göre) yapılır.</para>
    /// </summary>
    private async Task<int> StagePauseActiveCampaignsAsync(
        Guid licenseId, CancellationToken ct)
    {
        var active = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId
                && (c.Status == "pending" || c.Status == "sending"))
            .ToListAsync(ct);

        foreach (var campaign in active)
        {
            campaign.Status = "paused";
            campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);
        }

        return active.Count;
    }
```

**3d — Admin kill switch'i için exception.** `NetgsmAccountService.cs`
namespace'i içinde, servis sınıfının DIŞINA:

```csharp
/// <summary>
/// Yönetici tarafından kapatılmış (<see cref="NetgsmAccountStatus.Disabled"/>)
/// bir kuruluma yazma girişimi. Panel bunu 409'a çevirir.
/// </summary>
public sealed class NetgsmAccountDisabledException : InvalidOperationException
{
    public NetgsmAccountDisabledException()
        : base("netgsm-account-disabled")
    {
    }
}
```

**3e — `UpsertAsync`.** `NetgsmAccountService.cs` içine, 3c'deki yardımcıların
altına:

```csharp
    /// <summary>
    /// Lisansın Netgsm kurulumunu yazar/günceller, satırı <b>her zaman</b>
    /// <see cref="NetgsmAccountStatus.Failed"/> bırakır ve lisansın koşan
    /// kampanyalarını <b>aynı işlemde</b> duraklatır.
    ///
    /// <para><b>Neden hep Failed.</b> Kimlik bilgileri değiştiyse eski doğrulama
    /// geçersizdir; <c>Verified</c>'ı korumak, yanlış kimlikle "açık" duran bir
    /// kurulum demektir. Satırı kapalı yazıp doğrulamayı ayrı çalıştırmak aynı
    /// zamanda arada süreç ölse bile fail-closed kalmayı garanti eder.</para>
    ///
    /// <para><b>Neden duraklatma BURADA, doğrulama sonucunda değil.</b> Hesap
    /// <c>Failed</c> olduğu anda marka çözülemez; ağ çağrısı sürerken (ya da o
    /// noktada süreç ölürse) kampanya açık kalırsa işçi izinsiz gönderime devam
    /// eder. Duraklatmayı ilk yazıma bağlamak bu pencereyi kapatır.</para>
    ///
    /// <para><paramref name="rawPassword"/> boş/null ise saklanan şifre KORUNUR:
    /// panel şifreyi geri göstermediği için yayıncı başlığını düzeltirken alanı
    /// boş bırakır. İlk kayıtta ise zorunludur.</para>
    ///
    /// <para><b><c>Disabled</c> kapısı BURADA.</b> Panel controller'ı da erken
    /// bir ön kontrol yapıyor, ama asıl kapı bu: ön kontrol ile yazım arasındaki
    /// pencerede kapatılan hesabı yalnız bu kontrol koruyabilir.</para>
    ///
    /// <para><b>Bu metot <c>ChangeTracker</c>'ı TEMİZLEYEBİLİR.</b> Hem retry
    /// turları hem de fırlatma yolları paylaşılan scoped bağlamda
    /// <c>ChangeTracker.Clear()</c> çağırıyor; bu da çağıranın ÖNCEDEN stage
    /// ettiği kaydedilmemiş değişiklikleri iz bırakmadan yutar. Dolayısıyla
    /// <c>UpsertAsync</c>, bekleyen yazımların üstüne çağrılmaz: önce onları
    /// kaydet, sonra buraya gel.</para>
    /// </summary>
    /// <exception cref="NetgsmAccountDisabledException">Hesap admin tarafından kapatılmış.</exception>
    /// <exception cref="ArgumentException">İlk kayıtta şifre verilmemiş.</exception>
    /// <exception cref="DbUpdateConcurrencyException">Hesap satırı yazım sürerken
    /// değişti — ya da kampanya üstlenme yarışı dört turda da kaybedildi.</exception>
    public async Task<NetgsmAccount> UpsertAsync(
        Guid licenseId, string userCode, string? rawPassword,
        string header, string brandCode, CancellationToken ct)
    {
        var account = await _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        var originalId = account?.Id;
        var originalVersion = account?.UpdatedAt;
        // Retry YALNIZ kampanya kalp atışı/tamamlanma yarışı için; hesap satırı
        // değişmişse ilk turda zaten 409'a düşüyoruz. 4, o yarışın pratik üst
        // sınırı: işçi bir kampanyayı en çok bir kez üstlenir, aynı lisansta
        // üst üste dört kaybetmek gerçekçi değil.
        const int maxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                account = await _db.NetgsmAccounts
                    .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
            }

            if (account?.Status == NetgsmAccountStatus.Disabled)
                throw new NetgsmAccountDisabledException();

            // Retry yalnız kampanya yarışı içindir. Hesap satırı bu arada
            // değiştiyse yeni sürümü sessizce sahiplenmeyiz — çağıran 409 alır.
            if (attempt > 1
                && (account?.Id != originalId
                    || account?.UpdatedAt != originalVersion))
            {
                _db.ChangeTracker.Clear();
                throw new DbUpdateConcurrencyException(
                    "Kurulum, kaydetme sürerken değişti.");
            }

            if (account is null)
            {
                if (string.IsNullOrWhiteSpace(rawPassword))
                {
                    throw new ArgumentException(
                        "İlk kayıtta Netgsm API şifresi zorunlu.",
                        nameof(rawPassword));
                }

                // Yalnız YENİ satırın seed damgası. Güncelleme yolunda buraya
                // hiçbir şey yazılmıyor: jetonu DbContext damgalıyor.
                var now = DateTimeOffset.UtcNow;

                account = new NetgsmAccount
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                _db.NetgsmAccounts.Add(account);
            }

            account.UserCode = userCode.Trim();
            account.Header = header.Trim();
            account.BrandCode = brandCode.Trim();

            if (!string.IsNullOrWhiteSpace(rawPassword))
                account.PasswordProtected = ProtectPassword(rawPassword);

            account.Status = NetgsmAccountStatus.Failed;
            account.LastError = null;
            account.LastVerifiedAt = null;

            // Yalnız başlık/marka aynı kalsa bile CAS UPDATE'i üretilmeli:
            // aksi halde EF hiç UPDATE yazmaz ve jeton kontrolü hiç koşmaz.
            if (_db.Entry(account).State != EntityState.Added)
            {
                _db.Entry(account).Property(a => a.UpdatedAt)
                    .IsModified = true;
            }

            await StagePauseActiveCampaignsAsync(licenseId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                return account;
            }
            catch (DbUpdateConcurrencyException ex) when (
                attempt < maxAttempts
                && ex.Entries.Count > 0
                && ex.Entries.All(e => e.Entity is SmsCampaign))
            {
                // Yalnız kampanya heartbeat/tamamlanma yarışını tekrar dene.
                // Hesabın özgün sürümü originalVersion olarak korunuyor.
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateException)
            {
                // Buraya tükenen CAS, marka kodu tekil indeks ihlali ve diğer
                // yazım hataları düşer. Fırlatmadan ÖNCE temizle: aksi hâlde
                // çağıranın scope'unda yarı-yazılmış hesap + `paused` damgalı
                // kampanyalar izleniyor kalır ve o scope'ta atılacak SONRAKİ
                // herhangi bir `SaveChanges` onları kimsenin karar vermediği bir
                // anda diske basar. Yukarıdaki dalın `Clear()`'ı bir sonraki tur
                // için; bu çıkış yolunda bir sonraki tur yok.
                _db.ChangeTracker.Clear();
                throw;
            }
        }
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

> **İki `catch`'in sırası zorunlu.** `DbUpdateConcurrencyException`,
> `DbUpdateException`'dan TÜREDİĞİ için süzgeçli özel olan üstte kalmalı;
> derleyici de tersini kabul etmez. Alttaki genel dal, `UpsertAsync`'in tek
> çıkış vanası: tükenen CAS de, `BrandCode` tekil indeks ihlali de (Görev 6
> bunu `brand-code-taken`'a çeviriyor) oradan geçiyor ve fırlatmadan önce
> tracker temizleniyor. Aksi hâlde çağıranın **paylaşılan scoped** bağlamında
> `Modified` hesap (yeni şifre + `Failed` + damgalanmış `UpdatedAt`) ve
> `paused` damgalı kampanyalar izleniyor kalır; o scope'ta atılacak sonraki
> herhangi bir `SaveChanges` onları kimsenin karar vermediği bir anda diske
> basar. Kardeş metot `CloseAccountAndPauseCampaignsAsync` (Görev 8) da aynı
> taban `catch (DbUpdateException)` dalını taşıyor — simetri **iki yönlü**:
> birinden silinirse öbüründen de silinmiş sayılmalı, çünkü ikisi de aynı
> paylaşılan scoped bağlamda çalışıyor. `Clear()` + `throw` güvenli, çünkü
> Görev 6'nın `IsBrandCodeConflict(ex)` yardımcısı `ex.InnerException`'a
> bakıyor, `ex.Entries`'e değil.
>
> Bu dalın `Clear()`'ı sessizce geri gelebilecek türden: satır silindiğinde
> `Services.Sms|Services.Iys` kümesinin tamamı yeşil kalıyordu.
> `Upsert_cakisma_firlatirken_izlenen_nesne_birakmaz` tam olarak bunu
> öldürmek için var — **silme.**

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~NetgsmAccountServiceTests|FullyQualifiedName~NetgsmAccountVersionTests"
```
Beklenen: PASS — 20 test (`NetgsmAccountServiceTests`'te 18 `[Fact]`,
`NetgsmAccountVersionTests`'te 2 `[InlineData]`'lı tek `[Theory]`).

Jeton ve damgalama mevcut paketin başka yerlerini bozmadığını doğrulamak için
SMS/İYS kümesini de koş:

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~Services.Sms|FullyQualifiedName~Services.Iys"
```
Beklenen: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountServiceTests.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountVersionTests.cs
git commit -m "$(cat <<'EOF'
feat(netgsm): UpsertAsync kurulumu kapalı yazar, kampanyayı duraklatır, sürüm jetonu ekler

Kurulum satırı her zaman Failed doğuyor ve aynı SaveChanges içinde lisansın
koşan kampanyaları paused'a çekiliyor: hesap Failed olduğu anda marka
çözülemediği için ağ çağrısı sürerken kampanyanın açık kalması izinsiz
gönderim demekti. UpdatedAt artık eşzamanlılık jetonu ve DbContext her
güncellemede max(UtcNow, özgün + 1 tick) damgalıyor — UtcNow monoton olmadığı
için ham atama sahte koruma üretiyordu.

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
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm/İYS kurulum ucu. Üç değişmez korunuyor:
/// (1) API şifresi panele ASLA dönmez, (2) uç owner-only — staff operatör
/// yayıncının SMS kimliklerini göremez/değiştiremez, (3) sorgu kiracıya
/// bağlı — bir yayıncı komşusunun Netgsm kimliklerini göremez.
/// </summary>
public sealed class PanelNetgsmAccountControllerTests : IDisposable
{
    // Her test KENDİ fabrikasını kurar (= kendi InMemory veritabanı).
    // Paylaşılan bir `IClassFixture` olmaz: `NetgsmAccount.BrandCode` GLOBAL
    // tekil, `LicenseId` de tekil (LicenseDbContext, `NetgsmAccount` eşlemesi).
    // Tek veritabanında biriken satırlar hem rastgele üretilen marka kodlarını
    // çakışmaya açar hem de kiracı izolasyonu testini komşu testlerin
    // satırlarına bağımlı kılar. Test başına bir fabrikanın bedeli bu
    // belirsizlikten ucuz.
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

    private sealed record Seed(HttpClient Client, Guid LicenseId);

    private static async Task<Seed> SeedTenantAsync(ApiFactory factory)
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

        return new Seed(client, license.Id);
    }

    /// <summary>Seed edilen satırın panelde görünmesi gereken değerleri.
    /// <paramref name="RawPassword"/> <b>düz metin şifredir</b> — şifre sızıntısı
    /// testi yanıtta gerçek sırrı arayabilsin diye döndürülüyor.</summary>
    private sealed record SeededAccount(
        string RawPassword,
        string UserCode,
        string Header,
        string BrandCode);

    /// <summary>Hesabı seed eder ve yazdığı değerleri döndürür. Değerler sabit
    /// değil, satır başına üretiliyor: görünümün alanları gerçekten o satırdan
    /// geliyor mu yoksa sabit mi dönüyor, ancak böyle ayırt edilebilir.</summary>
    private static async Task<SeededAccount> SeedAccountAsync(
        ApiFactory factory, Guid licenseId, NetgsmAccountStatus status,
        string? lastError = null, DateTimeOffset? lastVerifiedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var rawPassword = $"pw-{Guid.NewGuid():N}";
        // Başlık en fazla 11 karakter (Netgsm sınırı, kolonda da öyle eşlenmiş).
        var seeded = new SeededAccount(
            RawPassword: rawPassword,
            UserCode: Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            Header: $"OD{Random.Shared.Next(100_000, 999_999)}",
            BrandCode: Random.Shared.Next(100_000, 999_999).ToString());
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = seeded.UserCode,
            PasswordProtected = protector.ProtectPassword(rawPassword),
            Header = seeded.Header,
            BrandCode = seeded.BrandCode,
            Status = status,
            LastError = lastError,
            LastVerifiedAt = lastVerifiedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return seeded;
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
        var rawPassword = (await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Verified)).RawPassword;

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");
        var body = await resp.Content.ReadAsStringAsync();

        // 1) Gerçek sır gövdede olmamalı — ham da olsa şifrelenmiş hâliyle de.
        body.Should().NotContain(rawPassword, "düz metin şifre panele dönmez");

        using var doc = JsonDocument.Parse(body);

        // 2) `passwordSet` DIŞINDA şifreye benzeyen alan adı olmamalı.
        //    Ham metinde "assword" aramak işe yaramaz: `passwordSet`'in kendisi
        //    o dizgiyi içerir, test hiçbir doğru DTO ile yeşile dönemezdi.
        var leakyFields = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n.Contains("password", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("passwordSet", StringComparison.OrdinalIgnoreCase))
            .ToList();
        leakyFields.Should().BeEmpty("yalnız passwordSet bayrağı dönebilir");

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
    public async Task Disabled_hesapta_durum_disabled_ve_sms_kapali()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Disabled);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("disabled",
            "emekliye ayrılmış kimlik panelde 'failed' görünürse yanlış teşhis olur — "
            + "yayıncı çalışan bir şifreyi tekrar tekrar girip durur");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse(
            "gönderim kapısı yalnız Verified'da açılır");
    }

    [Fact]
    public async Task Gorunum_form_alanlarini_satirdan_doner()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        var verifiedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var acc = await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Verified,
            lastVerifiedAt: verifiedAt);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userCode").GetString().Should().Be(acc.UserCode,
            "panel formu bu alanlarla doluyor; boş dönen bir görünüm yayıncıya "
            + "kurulumu hiç yapılmamış gibi görünür");
        doc.RootElement.GetProperty("header").GetString().Should().Be(acc.Header,
            "İYS markası Netgsm tarafında BAŞLIKTAN çözülüyor — yanlış/boş başlık "
            + "yayıncının onaylarını başka bir markaya yazdırır");
        doc.RootElement.GetProperty("brandCode").GetString().Should().Be(acc.BrandCode);

        // Önce ValueKind: doğrudan `GetDateTimeOffset()` çağırmak, alan null
        // dönerse "element of type 'String'... has type 'Null'" diye opak bir
        // InvalidOperationException fırlatır — düşen testi okuyan kişi asıl
        // sorunun alanın boş dönmesi olduğunu göremez.
        var lastVerified = doc.RootElement.GetProperty("lastVerifiedAt");
        lastVerified.ValueKind.Should().NotBe(JsonValueKind.Null,
            "günlük yeniden doğrulama işinin izi panelde görünmeli");
        lastVerified.GetDateTimeOffset().Should()
            .BeCloseTo(verifiedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Baska_kiracinin_hesabini_GORMEZ()
    {
        var factory = NewFactory();
        var a = await SeedTenantAsync(factory);
        var b = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, b.LicenseId, NetgsmAccountStatus.Verified);

        var resp = await a.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("none",
            "B'nin kimlikleri A'nın panelinde görünmemeli");
        doc.RootElement.GetProperty("userCode").ValueKind.Should().Be(JsonValueKind.Null,
            "kiracı filtresi düşerse yanıt komşunun Netgsm abone numarasını taşır");
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

        // Alan adı `token` — `accessToken` DEĞİL. Sözleşme
        // `AuthController.OperatorLoginResponse.Token`; JSON camelCase ile
        // `token` olarak çıkar. Yanlış ad yazarsan `GetProperty` fırlatır ve
        // testler yetki denetimine hiç varamaz.
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
```

> **Not:** Gövde alan adlarını `PanelWhatsAppAccountControllerTests`
> içindeki mevcut `StaffClientAsync` kopyasıyla karşılaştır
> (`PanelWhatsAppAccountControllerTests.cs:167-172`) — orası yanıtı
> `OperatorLoginResp` kaydına deserialize edip `body.Token` okuyor; biz aynı
> sözleşmeyi ham JSON üzerinden okuyoruz.

`OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`:

> `using OrderDeck.LicenseServer.Services.Auth;` satırını atlamak derleme
> hatasıdır: `User.IsOperator()` ve `User.GetTenantCustomerId()` o namespace'te
> tanımlı uzantı metotları (`Services/Auth/TenantClaims.cs`). Projede
> `GlobalUsings.cs` **yok**; `ImplicitUsings` yalnız `System.*`/ASP.NET
> varsayılanlarını kapsıyor, proje namespace'lerini değil.
>
> Buna karşılık `using OrderDeck.LicenseServer.Services.Sms;` controller'da
> **gereksiz**: `NetgsmAccount` ve `NetgsmAccountStatus` `Domain` namespace'inde.
> Testlerde ise gerekiyor — orası `NetgsmAccountService`'i DI'dan çözüyor.

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

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
Beklenen: PASS (8 test).

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
using OrderDeck.LicenseServer.Services.Sms;
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
    public async Task Dogrulama_sirasinda_admin_kapatirsa_sonuc_UYGULANMAZ()
    {
        // `VerifyAsync` bir ağ çağrısı — saniyeler sürer. O pencerede admin
        // kapatma anahtarına basarsa, dönen "kabul" cevabını yazmak kapatmayı
        // sessizce geri alırdı. Yarışı zamanlamaya bırakmıyoruz: stub'ın
        // İÇİNDE, tam o pencerede kapatıyoruz — deterministik.
        var (factory, client, licenseId) = await SeedAsync();
        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.Status = NetgsmAccountStatus.Disabled;
            db.SaveChanges();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "doğruladığımız durum artık satırda durmuyor; sonuç atılmalı");

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db2.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled,
            "İYS 'kabul' dese bile admin kapatması geri alınamaz (Görev 6 sözleşmesi)");
    }

    [Fact]
    public async Task Dogrulama_sirasinda_kimlikler_degisirse_sonuc_UYGULANMAZ()
    {
        // İkinci bir PUT, biz İYS'yi beklerken BAŞKA kimlikleri yazdı. Bizim
        // "kabul" cevabımız o kimliklere ait DEĞİL; yazarsak doğrulanmamış
        // kimlikleri Verified yapmış oluruz — fail-closed tam burada delinir.
        var (factory, client, licenseId) = await SeedAsync();
        var otherUserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();
        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.UserCode = otherUserCode;
            db.SaveChanges();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db2.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "doğrulanmamış kimlikler ASLA Verified yazılmaz");
        acc.UserCode.Should().Be(otherUserCode, "ikinci PUT'un yazdığı kimlik korunur");
    }

    [Fact]
    public async Task Dogrulama_sirasinda_yalniz_parola_degisirse_sonuc_uygulanmaz()
    {
        // Yukarıdaki testin kardeşi ama ONDAN DAHA SERT: burada `UserCode` ve
        // `BrandCode` AYNI kalıyor, yalnız parola değişiyor. Alan karşılaştırmasına
        // dayanan bir CAS bu yarışı GÖREMEZ — "doğruladığım kimlikler duruyor"
        // der ve satırı açar. Oysa doğrulanan parola artık satırda yok: hiç
        // sınanmamış bir parola `Verified` damgası alırdı. Sürüm jetonu bunu
        // alan alan karşılaştırmadan yakalar.
        var (factory, client, licenseId) = await SeedAsync();
        var replacementPassword = $"pw-{Guid.NewGuid():N}";

        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.PasswordProtected = accounts.ProtectPassword(replacementPassword);
            db.SaveChanges();

            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var response = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("verification-superseded");

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var verifyAccounts = verify.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var persisted = await verifyDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);

        persisted.Status.Should().Be(NetgsmAccountStatus.Failed);
        verifyAccounts.TryUnprotectPassword(persisted.PasswordProtected)
            .Should().Be(replacementPassword, "araya giren PUT'un parolası korunur");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Panel_kaydi_dogrulama_baslamadan_kampanyayi_duraklatir(bool unavailable)
    {
        // Kurulum yeniden kaydedildiği AN kapı kapanır. Duraklatmayı doğrulama
        // sonucuna bağlasaydık, ağ çağrısını beklediğimiz saniyelerde (ya da
        // süreç tam orada ölürse sonsuza dek) koşan işçi gönderime devam
        // ederdi — işçi markayı ve onayları çoktan okumuştur, hesabın Failed
        // olması onu TEK BAŞINA durdurmaz. `unavailable` iki ret biçimini de
        // sınıyor: İYS'nin "hayır"ı ve İYS'ye hiç ulaşamama.
        var (factory, client, licenseId) = await SeedAsync();

        (await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody()))
            .EnsureSuccessStatusCode();

        var campaignId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = campaignId,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = "sending",
                ClaimedAt = DateTimeOffset.UtcNow,
                SegmentsPerMessage = 1,
                RecipientCount = 2,
                ReservedCredits = 2,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            for (var i = 0; i < 2; i++)
            {
                db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    Phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}",
                    Status = "pending",
                });
            }

            db.LicenseSmsBalances.Add(new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = 98,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        var observedPausedBeforeVerification = false;

        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            var account = db.NetgsmAccounts.AsNoTracking()
                .Single(a => a.LicenseId == licenseId);
            var campaign = db.SmsCampaigns.AsNoTracking()
                .Single(c => c.Id == campaignId);

            observedPausedBeforeVerification =
                account.Status == NetgsmAccountStatus.Failed
                && campaign.Status == "paused";

            if (unavailable) throw new HttpRequestException("İYS erişilemiyor");
            throw new IysConfigurationException("30", "kimlik reddedildi");
        };

        var response = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        observedPausedBeforeVerification.Should().BeTrue(
            "duraklatma İYS çağrısından ÖNCE, Upsert'in kendi SaveChanges'inde inmeli");

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var persisted = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        persisted.Status.Should().Be("paused");
        persisted.CompletedAt.Should().BeNull();
        persisted.RefundedCredits.Should().Be(0);

        // Duraklatma KREDİ İADE ETMEZ: kalan alıcılar "pending" kalıyor,
        // rezervasyon tam da onların karşılığı. Devam ettirildiğinde aynı
        // krediyle gönderilecekler.
        (await verifyDb.SmsCampaignRecipients.CountAsync(
            r => r.CampaignId == campaignId && r.Status == "pending"))
            .Should().Be(2);

        (await verifyDb.LicenseSmsBalances
            .Where(b => b.LicenseId == licenseId)
            .Select(b => b.CreditsRemaining)
            .SingleAsync()).Should().Be(98);

        (await verifyDb.LicenseSmsTransactions.CountAsync(
            t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0);
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

**Önce DI kaydı** — `NetgsmAccountVerifier` bu görevde ilk kez bir DI grafiğine
giriyor. Kaydı Görev 8'e ertelersen bu görevin "PASS" adımı gerçekleşmez:
controller çözülemez, her test `InvalidOperationException: Unable to resolve
service for type ... NetgsmAccountVerifier` ile düşer.

`Program.cs`, `NetgsmAccountService` kaydının hemen altına
(`Program.cs:195` civarı — komşularının biçimi tam nitelenmiş ad):

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifier>();
```

(`NetgsmAccountService` zaten kayıtlı — `Program.cs:195`. `IIysClient` de
kayıtlı; `Netgsm:Enabled` kapalıyken `NullIysClient`'a düşüyor, `Program.cs:191`.)

Sonra `PanelNetgsmAccountController` sınıfına ekle; `_db` yanına bağımlılıkları
da al:

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

        // --- Doğrulama penceresini KAPAT: SÜRÜM JETONU, reload DEĞİL ---
        // VerifyAsync bir AĞ çağrısı; saniyeler sürebilir. O aralıkta satır
        // değişmiş olabilir ve sonucu körlemesine yazmanın üç somut zararı var:
        //
        //  1. Admin bu arada hesabı `Disabled` yaptıysa, bizim `Ok`'umuz kapatma
        //     anahtarını sessizce geri alır — Görev 6'nın "Disabled yapışkan"
        //     garantisi tam da burada çöker.
        //  2. Aynı yayıncıdan ikinci bir PUT başka kimlikleri yazdıysa, bizim
        //     `Ok`'umuz BAŞKASININ doğrulanmamış kimliklerini `Verified` yapar.
        //     EF yalnız değişen sütunları yazdığı için `UserCode` korunur ama
        //     satır yine de açılır: fail-closed sözleşmesi delinir.
        //  3. O ikinci PUT yalnız **parolayı** değiştirdiyse alan karşılaştırması
        //     bunu göremez: `UserCode` ve `BrandCode` aynı kalır, satır açılır ve
        //     hiç doğrulanmamış bir parola `Verified` damgası alır.
        //
        // Bu yüzden `ReloadAsync` + alan karşılaştırması YAPMIYORUZ. Reload,
        // EF'in ÖZGÜN değerlerini de tazeler — yani tam da yarışı yakalayacak
        // kanıtı siler. Onun yerine `UpsertAsync`'ten dönen İZLENEN nesnenin
        // özgün `UpdatedAt` değeri son yazıma kadar korunur; Görev 3'te eklenen
        // eşzamanlılık jetonu `WHERE UpdatedAt = @original` üretir. Araya giren
        // HERHANGİ bir yazım (parola dahil) sürümü ilerletmiş olur ve
        // `SaveChanges` sıfır satır etkiler → `DbUpdateConcurrencyException`.
        try
        {
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
            // Şifre çözülemezse DIŞ ÇAĞRI YAPILMAZ — ama erken `return`
            // etmiyoruz: `LastError` yazımı da aynı CAS korumasından geçmeli,
            // yoksa bayat bir istek kapatılmış hesaba hata metni yazabilir.
            var password = _accounts.TryUnprotectPassword(account.PasswordProtected);

            var result = password is null
                ? new NetgsmVerifyResult(
                    NetgsmVerifyOutcome.Unavailable, NetgsmAccountService.UndecryptableMessage)
                : await _verifier.VerifyAsync(
                    new Services.Iys.IysAccountContext(
                        account.LicenseId, account.UserCode, password, account.BrandCode),
                    ct);

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

            // Sonuç başka hiçbir sütunu değiştirmese bile (örn. `Failed` satıra
            // AYNI `LastError` yazıldı) bir UPDATE üretilmeli: UPDATE yoksa
            // `WHERE UpdatedAt = @original` hiç koşmaz ve CAS sessizce atlanır.
            // `IsModified = true` damgalamayı da tetikler (Görev 3'teki
            // `StampNetgsmAccountVersions` yalnız `Modified` girdilere bakar).
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);

            return Ok(ToView(account));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Doğruladığımız sürüm artık satırda durmuyor. Sonucu ATIYORUZ —
            // yazmak, yukarıdaki üç zarardan birini üretmek olurdu.
            _db.ChangeTracker.Clear();
            return Problem(title: "verification-superseded",
                detail: "Kurulum, doğrulama sürerken değişti. Formu tekrar kaydedin.",
                statusCode: 409);
        }
    }
```

> **Görev 6 ve Görev 13 bu gövdeye EKLEME yapar, parça DEĞİŞTİRMEZ.** İkisi de
> yeni `catch` cümleleri ve tek satırlık eklemeler getiriyor; bir bloğu
> "tamamen değiştir" diyen bir adım bu metotta YOKTUR. (İlk taslakta Görev 13
> böyle yazılmıştı ve Görev 6'nın marka çakışması `catch`'ini sessizce siliyordu.)

`DbUpdateConcurrencyException` için `using Microsoft.EntityFrameworkCore;`
yeterli, zaten using listesinde var. `EntityState`'e artık ihtiyaç yok.

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
Beklenen: PASS (17 test — Görev 4'ün 8'i + buradaki 9; `Panel_kaydi_...`
bir `[Theory]`, iki vaka sayılır).

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs \
        OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
        OrderDeck.LicenseServer/Program.cs \
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
Beklenen: iki yeni test FAIL — `Disabled_hesap_panelden_yeniden_acilamaz` **500**
(Görev 3'ün `NetgsmAccountDisabledException`'ı `SaveAsync`'te yakalanmıyor),
`Baska_lisansin_dogrulanmis_markasi_409` **200** (ön kontrol yok; InMemory tekil
indeks uygulamadığı için ikinci kayıt da geçiyor).

- [ ] **Adım 3: En küçük uygulamayı yaz**

Bu adım Görev 5'in `SaveAsync`'ine **EKLEME** yapar; hiçbir bloğu silmez.
İki ekleme yeri var.

**3a — ön kontroller.** `SaveAsync` içinde, `brandCode` doğrulamasının ALTINA
ve `try {` satırının ÜSTÜNE (ikisi de salt okuma, `try`'ın dışında kalırlar):

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

**3b — iki yeni `catch` cümlesi.** Görev 5'in yazdığı
`catch (DbUpdateConcurrencyException)` bloğu YERİNDE KALIR; biri onun üstüne,
biri altına gelir. Üç `catch`'in son sırası şöyle olmalı:

```csharp
        catch (NetgsmAccountDisabledException)
        {
            // Ön kontrolle `UpsertAsync` arasında admin kapattı: servis
            // guard'ı (Görev 3) bize haber verdi. Ön kontrol bu yarışı
            // KAPATMAZ, yalnız tipik durumda anlaşılır cevap verir —
            // fail-closed garantisi servisteki guard'dan gelir.
            _db.ChangeTracker.Clear();
            return Problem(title: "netgsm-account-disabled",
                detail: "Netgsm kurulumunuz yönetici tarafından kapatıldı. "
                        + "Yeniden açılması için destekle iletişime geçin.",
                statusCode: 409);
        }
        catch (DbUpdateConcurrencyException)
        {
            // (Görev 5'te yazıldı — DOKUNMA.)
            _db.ChangeTracker.Clear();
            return Problem(title: "verification-superseded",
                detail: "Kurulum, doğrulama sürerken değişti. Formu tekrar kaydedin.",
                statusCode: 409);
        }
        catch (DbUpdateException ex) when (IsBrandCodeConflict(ex))
        {
            // Ön kontrol ile kayıt arasında başka kiracı aynı markayı
            // doğruladı. Filtreli tekil indeks kesin kararı verdi; bizimki
            // Failed kalmalı ve yayıncı anlaşılır bir cevap almalı.
            _db.ChangeTracker.Clear();
            return Problem(title: "brand-code-taken",
                detail: "Bu İYS marka kodu başka bir hesapta doğrulanmış durumda.",
                statusCode: 409);
        }
```

> **Sıra önemli:** `DbUpdateConcurrencyException`, `DbUpdateException`'dan
> TÜREMİŞTİR. Marka `catch`'i yukarı alınırsa (filtresi tutmasa bile) derleyici
> sorun çıkarmaz ama okuyan yanılır; daha kötüsü, filtre ileride gevşetilirse
> eşzamanlılık çatışmaları "marka kodu dolu" diye etiketlenir. Özelden genele
> sırala.

Ve sınıfın sonuna yardımcıyı ekle:

```csharp
    /// <summary>
    /// <c>DbUpdateException</c>, marka kodu tekil indeksinin ihlali mi?
    /// 2601/2627 = unique index/constraint ihlali; indeks adı filtresi, aynı
    /// hata koduyla gelen BAŞKA yarışların (ve tamamen ilgisiz DB
    /// arızalarının) "marka kodu dolu" diye yanlış etiketlenmesini önler.
    /// Filtresiz bir <c>catch (DbUpdateException)</c>, taşan bir
    /// <c>LastError</c>'ı ya da kopan bir bağlantıyı da yayıncıya "marka
    /// kodunuz başkasında" diye gösterirdi — yanlış yeri saatlerce aratır.
    /// Kalıp: <c>PanelCustomerBalanceController.IsDuplicateReversal</c>
    /// (PanelCustomerBalanceController.cs:335).
    /// </summary>
    private static bool IsBrandCodeConflict(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("BrandCode", StringComparison.Ordinal);
```

> **Not:** InMemory sağlayıcı tekil indeks uygulamaz; bu `catch` yalnız gerçek
> SQL Server'da tetiklenir ve Görev 7'deki Testcontainers testiyle kanıtlanır.
> Yukarıdaki `Baska_lisansin_dogrulanmis_markasi_409` testi ön kontrolü sınar.
>
> `IsBrandCodeConflict`'in ad filtresi, EF'in ürettiği varsayılan indeks adına
> dayanıyor: `IX_NetgsmAccounts_BrandCode`. Görev 7 indeksi filtreli hâle
> getiriyor ama **adını değiştirmiyor** — `HasDatabaseName(...)` ile yeniden
> adlandırırsan `BrandCode` dizgisini adın içinde bırak.

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelNetgsmAccount"
```
Beklenen: PASS (19 test — Görev 5 sonundaki 17 + buradaki 2).

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

Aynı sınıfa üçüncü bir test daha ekle. Bu marka indeksiyle ilgili DEĞİL; bu
sınıfa konmasının tek nedeni **zaten ilişkisel fixture'a sahip olması** —
kanıtlanacak şey SQL transaction'ı gerektiriyor. Dosyanın başına ek `using`:

```csharp
using OrderDeck.LicenseServer.Services.Sms;
```

```csharp
    [Fact]
    public async Task Bayat_hesap_yazimi_kampanya_duraklatmasini_da_geri_alir()
    {
        // Görev 3'te `UpsertAsync` hesabı `Failed` yaparken kampanyaları AYNI
        // `SaveChanges` içinde duraklatıyor. Burada kanıtlanan şey o birliğin
        // gerçek: hesap yazımı sürüm jetonuna takılıp reddedilirse kampanya
        // duraklatması da geri alınmalı. Alınmazsa, admin'in kapattığı bir
        // hesabın kampanyası "paused"a düşer ama hesap `Disabled` kalır —
        // kimsenin devam ettiremeyeceği, rezerve kredisi asılı bir kampanya.
        //
        // InMemory bunu KANITLAYAMAZ: jetonu uygular ama çok-varlıklı yazımı
        // bir transaction'da geri almaz. Bu yüzden Testcontainers.
        var licenseId = await NewLicenseAsync();
        var campaignId = Guid.NewGuid();

        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(
                licenseId, Random.Shared.Next(100_000, 999_999).ToString(),
                NetgsmAccountStatus.Verified));
            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = campaignId,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = "sending",
                ClaimedAt = DateTimeOffset.UnixEpoch,
                SegmentsPerMessage = 1,
                RecipientCount = 1,
                ReservedCredits = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Yayıncının paneli hesabı okudu (bu sürümü sahipleniyor).
        using var workerScope = _factory.Services.CreateScope();
        var workerDb = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = workerScope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var stale = await workerDb.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);

        // Admin araya girip kapattı — sürüm ilerledi.
        using (var adminScope = _factory.Services.CreateScope())
        {
            var adminDb = adminScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var current = await adminDb.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);
            current.Status = NetgsmAccountStatus.Disabled;
            await adminDb.SaveChangesAsync();
        }

        // Panel bayat sürümle yazmaya çalışıyor. Servisteki `Disabled` guard'ı
        // bu yarışı GÖREMEZ (izlenen kopya hâlâ Verified); kararı jeton verir.
        Func<Task> write = async () => await accounts.UpsertAsync(
            licenseId, stale.UserCode, $"pw-{Guid.NewGuid():N}",
            stale.Header, stale.BrandCode, CancellationToken.None);

        await write.Should().ThrowAsync<DbUpdateConcurrencyException>();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await verifyDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId))
            .Status.Should().Be(NetgsmAccountStatus.Disabled,
                "admin kararı bayat yazımla geri alınamaz");

        var campaign = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("sending",
            "hesap yazımı düştüyse duraklatma da geri alınmalı — ya ikisi ya hiçbiri");
        campaign.ClaimedAt.Should().Be(DateTimeOffset.UnixEpoch);
    }
```

- [ ] **Adım 2: Düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountUniqueIndexTests
```
Beklenen: ilk iki yeni test FAIL (`DbUpdateException` — filtresiz indeks
çakışıyor). Docker kapalıysa hepsi düşer; önce Docker'ı başlat.

> **`Bayat_hesap_yazimi_...` burada ZATEN YEŞİL olmalı** — ve bu bilinçli.
> Görev 3'ün eklediği `IsConcurrencyToken()` saf **model** metadata'sı: yeni
> sütun ya da DDL istemez, yalnız EF'in ürettiği `UPDATE`'e
> `WHERE ... AND UpdatedAt = @original` ekler. Yani koruma göç beklemeden
> gerçek SQL Server'da da yürürlüktedir. Testin buradaki işi kırmızıdan yeşile
> dönmek değil, **bunu kanıtlamak**: kırmızı görürsen Görev 3'teki eşleme
> satırı ya hiç uygulanmamıştır ya da yanlış `b` bloğuna yazılmıştır.

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

> **Snapshot'ı gözden geçir.** Bu, Görev 3'ün model değişikliğinden sonraki
> İLK göç. `dotnet ef migrations add` snapshot'ı modelin tamamından yeniden
> üretir, dolayısıyla `NetgsmAccountsSnapshot`'taki `UpdatedAt` özelliği artık
> `.IsConcurrencyToken()` taşımalı. Üretilen `Up`/`Down` gövdesinde bunun
> karşılığı **olmamalı** — jeton DDL üretmez. Snapshot'ta jeton yoksa Görev
> 3'ün eşlemesi uygulanmamıştır; `Up` içinde `AlterColumn` çıktıysa yanlış
> özelliğe (örn. `CreatedAt`) yazmışsındır.

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountUniqueIndexTests
```
Beklenen: PASS (7 test — mevcut 4 + yeni 3).

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

    [Theory]
    [InlineData(NetgsmAccountStatus.Disabled)]
    [InlineData(NetgsmAccountStatus.Verified)]
    public async Task Bayat_gunluk_ret_yeni_hesap_surumunu_degistirmez(
        NetgsmAccountStatus replacementStatus)
    {
        // `Failed_hesap_ise_alinmaz` ağ çağrısından ÖNCEKİ durumu koruyor.
        // Bu test ağ çağrısı SÜRERKEN açılan pencereyi kapatıyor — asıl
        // tehlike orada:
        //
        //  * `Disabled`: admin tam o saniyede kapattı. Ret sonucunu körlemesine
        //    yazmak hesabı `Failed`'a çeker ve ADMIN KİLİDİNİ KALDIRIR —
        //    yayıncı panelden kaydete basıp kurulumu geri açabilir hâle gelir.
        //  * `Verified` + yeni parola: yayıncı doğru kimliği girdi ve panel onu
        //    doğruladı. Bizim elimizdeki "kod 30" ESKİ parolaya ait; yazarsak
        //    çalışan bir kurulumu kapatırız.
        //
        // İkisini de ayıran şey durum kontrolü DEĞİL, sürümdür: `Verified`
        // vakasında durum hiç değişmedi. Bu yüzden ret, doğruladığı
        // `UpdatedAt` sürümünü şart koşar.
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        var replacementPassword = $"pw-{Guid.NewGuid():N}";
        const string currentMessage = "Güncel yönetici notu.";

        factory.Iys.ByBrand[brandCode] = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            var current = db.NetgsmAccounts.Single(a => a.BrandCode == brandCode);

            current.Status = replacementStatus;
            current.PasswordProtected = accounts.ProtectPassword(replacementPassword);
            current.LastError = currentMessage;
            db.SaveChanges();

            throw new IysConfigurationException("30", "kimlik reddedildi");
        };

        await RunAsync(factory);

        var persisted = await ReadAsync(factory, brandCode);
        persisted.Status.Should().Be(replacementStatus,
            "bayat ret araya giren kararı EZEMEZ");
        persisted.LastError.Should().Be(currentMessage);

        using var verify = factory.Services.CreateScope();
        var verifyAccounts = verify.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        verifyAccounts.TryUnprotectPassword(persisted.PasswordProtected)
            .Should().Be(replacementPassword);
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

        // Ağ çağrısından ÖNCE yakala: ret kararını yazarken "hangi sürümü
        // doğruladım" sorusunun cevabı bu. Çağrı sürerken yayıncı paneli
        // kaydedip şifreyi değiştirebilir ya da admin hesabı Disabled
        // yapabilir; o zaman elimizdeki ret ARTIK BAŞKA BİR HESABIN retidir.
        var expectedUpdatedAt = acc.UpdatedAt;
        var licenseId = acc.LicenseId;

        var password = _accounts.TryUnprotectPassword(acc.PasswordProtected);
        if (password is null)
        {
            acc.LastError = NetgsmAccountService.UndecryptableMessage;
            _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var result = await _verifier.VerifyAsync(
            new IysAccountContext(acc.LicenseId, acc.UserCode, password, acc.BrandCode), ct);

        switch (result.Outcome)
        {
            case NetgsmVerifyOutcome.Ok:
                acc.LastVerifiedAt = DateTimeOffset.UtcNow;
                acc.LastError = null;
                break;

            case NetgsmVerifyOutcome.Rejected:
                _log.LogWarning(
                    "NetgsmAccountVerifyJob: lisans {LicenseId} kurulumu düştü — {Message}",
                    licenseId, result.Message);
                // Hesabı Failed yapmak TEK BAŞINA yetmiyor: akmakta olan bir
                // kampanya marka/onayları döngüden ÖNCE okuyor
                // (SmsCampaignSendJob.cs:111-133), yani kapı kapansa bile
                // kalan alıcılara gönderim sürer. §2.3 "düşen kurulum
                // gönderimi durdurur" diyorsa durdurması gereken yer burası.
                // Kampanyaları da duraklatıyoruz — hesap yazımıyla TEK
                // SaveChanges'te. `expectedUpdatedAt` kararı doğruladığımız
                // sürüme BAĞLAR: araya giren yazım varsa ret sessizce düşer.
                var paused = await _accounts.CloseAccountAndPauseCampaignsAsync(
                    accountId, NetgsmAccountStatus.Failed, result.Message, ct,
                    expectedUpdatedAt);
                if (paused > 0)
                    _log.LogWarning(
                        "NetgsmAccountVerifyJob: lisans {LicenseId} için {Count} kampanya duraklatıldı",
                        licenseId, paused);
                return;   // kaydı o metot yaptı; aşağıdaki SaveChanges'e düşme

            case NetgsmVerifyOutcome.Unavailable:
                acc.LastError = result.Message;
                break;
        }

        _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;
        await _db.SaveChangesAsync(ct);
    }
}
```

> **`CloseAccountAndPauseCampaignsAsync` kendi `SaveChanges`'ini yapıyor** ve
> izlenen nesneleri tazeleyebiliyor; bu yüzden `Rejected` dalı `break` değil
> `return` ile çıkıyor. `break` deseydin aşağıdaki `SaveChangesAsync` ikinci
> bir yazım turu açardı.

> **Neden `acc.UpdatedAt = ...` değil `IsModified = true`?** Damgayı Görev
> 3'teki `LicenseDbContext.StampNetgsmAccountVersions()` merkezî olarak
> atıyor (`max(UtcNow, özgün + 1 tick)`). Burada elle `UtcNow` yazsaydık saat
> ilerlemediğinde jeton yerinde kalırdı. `IsModified = true` yalnız girdiyi
> `Modified` yapıp damgalayıcıyı tetikler — yalnız `LastVerifiedAt`/`LastError`
> değiştiğinde bile sürümün ilerlemesini garanti eder.

`NetgsmAccountService`'e bu metodu ekle (Görev 12 de aynısını kullanacak —
ikinci bir kopya yazma):

```csharp
    /// <summary>
    /// Kurulumu kapatır (<paramref name="status"/>) ve o lisansın HENÜZ
    /// BİTMEMİŞ kampanyalarını <c>paused</c> yapar — <b>tek</b>
    /// <c>SaveChanges</c>'te, yani ya ikisi de olur ya hiçbiri. Duraklatılan
    /// kampanya sayısını döndürür.
    ///
    /// <para><b><c>pending</c> de duraklatılmak ZORUNDA.</b> Yalnız
    /// <c>sending</c> duraklatılsaydı <see cref="SmsCampaignRecoveryJob"/>
    /// iki dakika içinde bekleyeni kuyruğa alır ve kararı sessizce geri
    /// alırdı (<c>SmsCampaignRecoveryJob.cs:48-53</c>).</para>
    ///
    /// <para><b>İade YOK.</b> Kalan alıcılar <c>pending</c> kalır ve
    /// rezervasyon onların karşılığıdır. Burada iade edersek kampanya devam
    /// ettirildiğinde aynı kredi ikinci kez harcanır.</para>
    ///
    /// <para><b>Neden yeniden deneme var.</b> <c>SmsCampaign.ClaimedAt</c> bir
    /// concurrency token (<c>LicenseDbContext.cs:786</c>) ve gönderim işi onu
    /// ALICI BAŞINA tazeliyor (<c>SmsCampaignSendJob.cs:172</c>). Okumamızla
    /// yazmamız arasına bir kalp atışı girerse
    /// <see cref="DbUpdateConcurrencyException"/> gelir. Yakalamazsak kapatma
    /// isteği 500 ile düşer — hem de tam kampanya akarken, yani anahtarın en
    /// çok gerektiği anda. Aynı token, kampanyanın tam o anda tamamlanmasıyla
    /// olan yarışı da yakalıyor (Görev 11, "tamamlanmada ClaimedAt
    /// tazelenir"): bayat okumayla tamamlanmış kampanyayı <c>paused</c>'a
    /// geri çevirip yeniden gönderime açamayız.</para>
    ///
    /// <para><b><paramref name="expectedUpdatedAt"/> — bayat ret koruması.</b>
    /// Günlük iş (<c>Failed</c>) hesabı ağ çağrısından ÖNCE okuyor; çağrı
    /// sürerken admin hesabı <c>Disabled</c> yapmış ya da yayıncı yeni bir
    /// şifreyle kaydetmiş olabilir. O sürümü doğrulamadık, o sürüme ret
    /// yazamayız: <c>Disabled</c>'ı <c>Failed</c>'a çevirmek admin kilidini
    /// kaldırır, değişmiş şifreye ret yazmak da doğrulanmamış bir kimliği
    /// yanlışlıkla mahkûm eder. Bu yüzden <c>Failed</c> çağrısı sürümü
    /// TAŞIMAK ZORUNDA ve eşleşmezse <c>0</c> dönüp sessizce çekilir —
    /// sonraki tur güncel sürümü baştan doğrular.</para>
    ///
    /// <para><c>Disabled</c> (admin kill switch, Görev 12) sürüm İSTEMEZ:
    /// yönetici kararı en güncel karardır ve her hâlükârda kazanmalıdır.</para>
    /// </summary>
    public async Task<int> CloseAccountAndPauseCampaignsAsync(
        Guid accountId,
        NetgsmAccountStatus status,
        string? lastError,
        CancellationToken ct = default,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        if (status is not (NetgsmAccountStatus.Disabled or NetgsmAccountStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status));

        if (status == NetgsmAccountStatus.Failed && expectedUpdatedAt is null)
            throw new ArgumentException(
                "Günlük ret doğrulanan hesap sürümünü taşımalıdır.", nameof(expectedUpdatedAt));

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // Her turda TEMİZ oku: çağıranın izlediği bayat nesne bu kararın
            // içine sızmamalı (günlük iş `acc`'i hâlâ izliyor).
            _db.ChangeTracker.Clear();

            var account = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return 0;

            if (status == NetgsmAccountStatus.Failed
                && (account.Status != NetgsmAccountStatus.Verified
                    || account.UpdatedAt != expectedUpdatedAt!.Value))
                return 0;   // araya giren karar var — bayat ret düşer

            account.Status = status;
            account.LastError = lastError;
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;

            var paused = await StagePauseActiveCampaignsAsync(account.LicenseId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                return paused;
            }
            catch (DbUpdateConcurrencyException) when (attempt >= maxAttempts)
            {
                // Tükendik. Fırlatmadan ÖNCE temizle: aksi hâlde çağıranın
                // scope'unda (`OnPostDisableAsync`, günlük iş) yarı-yazılmış
                // hesap + "paused" damgalı kampanyalar izleniyor kalır ve o
                // scope'ta atılacak SONRAKİ herhangi bir `SaveChanges` onları
                // kimsenin karar vermediği bir anda diske basar. Döngünün
                // başındaki `Clear()` bir sonraki tur için; bu çıkış yolunda
                // bir sonraki tur yok.
                _db.ChangeTracker.Clear();
                throw;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Kalp atışı araya girdi, kampanya tam o anda tamamlandı ya da
                // hesap satırı başkası tarafından yazıldı. Döngü başındaki
                // `Clear()` + taze okuma kararı GÜNCEL duruma göre yeniden
                // verir; tamamlanmış kampanya ikinci turda filtreye girmez
                // (resurrection yok), değişmiş hesap da sürüm kontrolüne
                // takılıp `0` döner.
            }
            catch (DbUpdateException)
            {
                // Eşzamanlılık DIŞI yazım hatası (ör. `LastError` sütun taşması,
                // FK ihlali). Retry anlamsız — aynı veriyle tekrar denemek aynı
                // hatayı verir. Fırlatmadan ÖNCE temizle: bu metodu günlük iş bir
                // DÖNGÜ içinden çağırıyor, kirli tracker sıradaki hesabın
                // `SaveChanges`'ine biner.
                _db.ChangeTracker.Clear();
                throw;
            }
        }
    }
```

> **Üç `catch`'in sırası zorunlu.** `DbUpdateConcurrencyException`,
> `DbUpdateException`'dan TÜREDİĞİ için iki türemiş cümle ÜSTTE, taban ALTTA
> kalmalı; derleyici tersini zaten kabul etmez. Taban dal olmadan
> eşzamanlılık dışı bir yazım hatası (bu metot tam olarak `lastError` yazıyor
> ve `LastError` sütunu 500 karakterle sınırlı) buradan `Clear()` yapılmadan
> çıkardı — üstelik çağıran bir DÖNGÜ, yani kirli tracker bir controller'daki
> hâlinden daha tehlikeli: karar verilmemiş bir `Failed` + `paused` seti
> sıradaki hesabın `SaveChanges`'iyle diske iner.

> **`StagePauseActiveCampaignsAsync` Görev 3'te yazıldı** — burada ikinci bir
> kopyası yok. Duraklatmanın `ClaimedAt` jetonunu da ilerletmesi oradaki
> ortak yardımcının işi; kapatma yolları (günlük ret, admin kill switch,
> panelden başarısız kayıt) hepsi aynı gövdeyi çağırıyor.

> **Görev 12 çağrısı değişmiyor.** `Disabled` için `expectedUpdatedAt`
> verilmez; yeni parametre isteğe bağlı ve varsayılanı `null`. Yalnız
> `Failed` yolu sürüm taşımak zorunda, bu da `ArgumentException` ile
> derlenme değil **çalışma zamanında** zorlanıyor — çağıran yalnız günlük iş.

DI: `NetgsmAccountVerifier` zaten Görev 5'te kaydedildi. Burada **yalnız** işi
ekle — komşularının biçimiyle, `Program.cs:196` civarı:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifyJob>();
```

- [ ] **Adım 4: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountVerifyJobTests
```
Beklenen: PASS (7 test — `Bayat_gunluk_ret_yeni_hesap_surumunu_degistirmez`
iki `InlineData` ile iki kez sayılır).

- [ ] **Adım 5: Commit**

`NetgsmAccountService.cs` de listede: `CloseAccountAndPauseCampaignsAsync`
bu görevde imza değiştiriyor (`expectedUpdatedAt`). Unutulursa commit
derlenmez.

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmAccountVerifyJob.cs \
        OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs \
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
- Modify: `OrderDeck.LicenseServer/Services/Sms/LicenseSmsBalanceService.cs:101-111`
- Modify: `OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsBalanceConcurrencyTests.cs`

> **Neden bakiye servisi bu göreve dahil?** Görev 3'ün duraklatması
> `SmsCampaign.ClaimedAt` jetonunu ilerletiyor, bu görev de tamamlanmada
> tazeliyor. İkisi birlikte **yeni kampanya çakışmaları üretiyor** — ve o
> çakışma bugün `LicenseSmsBalanceService.ApplyAndSaveAsync`'in retry'ına
> düşüp paraya dönüşüyor (`:101-111` `ex.Entries`'in TAMAMINI yeniden
> yüklüyor, hazırlanmış `completed` + `RefundedCredits` siliniyor, sonra
> `amount` ikinci kez ekleniyor). Duraklatmayı bu düzeltme olmadan
> göndermek, tamir ettiğimiz deliğin yerine bir kredi sızıntısı koyar.

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

> **`msg` diye bir yerel değişken bugün YOK.** Mevcut gövde mesajı satır içinde
> kuruyor: `lock (_lock) _sent.Add(new Message(toPhone, message, kind));`
> (`RecordingSmsSender.cs:34`). Kancayı çağırabilmek için mesajı önce bir
> yerele almalısın.

`SendAsync`'i (`RecordingSmsSender.cs:30-36`) **tamamen** şununla değiştir:

```csharp
    public Task SendAsync(string toPhone, string message, SmsKind kind, CancellationToken ct = default)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated SMS provider failure.");
        var msg = new Message(toPhone, message, kind);
        lock (_lock) _sent.Add(msg);
        // Kanca kilidin DIŞINDA çağrılır: testler bu kancanın içinden ayrı bir
        // scope açıp DB'ye yazıyor. Kilit tutulurken DB'ye gitmek, aynı
        // fixture'ı paylaşan başka bir testin Sent okumasını bekletirdi.
        OnSent?.Invoke(msg);
        return Task.CompletedTask;
    }
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
    private static async Task<(Guid CampaignId, Guid AccountId, string[] Phones)> SeedAsync(LicenseDbContext db)
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
        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
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
        return (campaignId, accountId, phones);
    }

    [Fact]
    public async Task Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

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
    public async Task Gercek_kapatma_yolu_kosan_isi_durdurur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        // Bir öncekinin aksine burada durum ELLE yazılmıyor: Görev 8'in
        // kesin-ret dalının ve Görev 12'nin kapatma düğmesinin ORTAK
        // çağırdığı gerçek metot koşuyor — hem de gönderim işi kampanyayı
        // üstlenmiş ve ClaimedAt'i her alıcıda tazelerken. Yani bu, iki
        // yazıcının aynı satırda buluştuğu tek testtir; metodun retry
        // döngüsünün var olma sebebi bu senaryo.
        var pausedCount = -1;
        _factory.Sms.OnSent = _ =>
        {
            if (pausedCount >= 0) return; // yalnız ilk gönderimde kapat
            using var closer = _factory.Services.CreateScope();
            var accounts = closer.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            pausedCount = accounts.CloseAccountAndPauseCampaignsAsync(
                    accountId, NetgsmAccountStatus.Disabled,
                    "Yönetici tarafından kapatıldı.")
                .GetAwaiter().GetResult();
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.Sms.OnSent = null; }

        pausedCount.Should().Be(1, "koşan kampanya kapatma yazımında yakalanmalı");
        _factory.Sms.Sent.Should().HaveCount(1, "kapatma ikinci alıcıyı durdurmalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        campaign.Status.Should().Be("paused", "job kapatmayı ezmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0);

        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Disabled);

        // Duraklatma artık ClaimedAt jetonunu da ilerlettiği için, GÖNDERİLMİŞ
        // ilk alıcının sonuç yazımı çakışmayla karşılaşır. O çakışma yanlış
        // ele alınırsa (kampanyayı yeniden yazmaya çalışmak ya da istisnayı
        // dışarı bırakmak) alıcı "pending" kalır: SMS gitmiş ama kayıtta
        // gitmemiş görünür, kampanya devam ettirildiğinde AYNI KİŞİYE ikinci
        // kez gider. Bu iki satır, `SaveRecipientResultAsync`'in detach edip
        // yeniden kaydetme davranışını kilitliyor.
        var recipients = await vdb.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();

        recipients.Count(r => r.Status == "sent").Should().Be(1);
        recipients.Count(r => r.Status == "pending").Should().Be(1);
    }

    /// <summary>
    /// Bulgu 1 — duraklatma, kampanyayı ZATEN OKUMUŞ bir işçiyi de geçersiz
    /// kılmalı. Durum yazımı tek başına yetmez: işçi elindeki kopyayla
    /// `sending` + kendi `ClaimedAt`'ini yazınca duraklatma sessizce geri
    /// alınır ve admin'in kapatma düğmesi yalan söyler.
    ///
    /// <para><c>futureStamp</c> vakası, ileri damgalı bir kampanyada da
    /// üstlenmenin düştüğünü gösterir — ama damganın <b>değerini</b>
    /// kanıtlamaz: üstlenme yazımı zaten çakıştığı için atanan damga diske
    /// hiç inmez ve aşağıdaki <c>BeAfter</c> duraklatmanın damgasını ölçer.
    /// Monotonluk iddiası
    /// <see cref="Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez"/>
    /// testine aittir; ikisini birbirine karıştırma.</para>
    /// </summary>
    [Theory]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("sending", false)]
    public async Task Kapatmadan_once_okunan_kampanya_sonradan_ustlenilemez(
        string status, bool futureStamp)
    {
        using var worker = _factory.Services.CreateScope();
        var db = worker.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        var stale = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        stale.Status = status;
        stale.ClaimedAt = futureStamp
            ? DateTimeOffset.UtcNow.AddHours(1)
            : status == "sending"
                ? DateTimeOffset.UtcNow - SmsCampaignSendJob.ClaimLease - TimeSpan.FromHours(1)
                : null;

        await db.SaveChangesAsync();
        var previous = stale.ClaimedAt;

        // `worker` scope'u kampanyayı İZLEMEYE devam ediyor — gerçek işçinin
        // kapatma anındaki hâli bu.
        using (var closer = _factory.Services.CreateScope())
        {
            var accounts = closer.ServiceProvider.GetRequiredService<NetgsmAccountService>();

            (await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled, "Yönetici tarafından kapatıldı."))
                .Should().Be(1);
        }

        await worker.ServiceProvider
            .GetRequiredService<SmsCampaignSendJob>()
            .RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        campaign.Status.Should().Be("paused");
        campaign.ClaimedAt.Should().NotBeNull();

        if (previous.HasValue)
            campaign.ClaimedAt!.Value.Should().BeAfter(previous.Value);

        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0);
        _factory.Sms.Sent.Should().BeEmpty();

        (await vdb.SmsCampaignRecipients.CountAsync(
            r => r.CampaignId == campaignId && r.Status == "pending"))
            .Should().Be(2);
    }

    /// <summary>
    /// Üstlenme damgasının monotonluğu — ÇAKIŞMASIZ yolda. Yukarıdaki teori
    /// bunu kanıtlayamaz: orada üstlenme yazımı zaten çakışmayla düşüyor, yani
    /// damganın DEĞERİ hiç diske inmiyor ve <c>BeAfter</c> aslında
    /// duraklatmanın damgasını ölçüyor. Burada karşı yazıcı YOK: kampanya
    /// bir saat ileri damgalı doğuyor, iş onu sorunsuz üstleniyor ve damganın
    /// kendi değeri diske iniyor. Ham <c>UtcNow</c> ataması jetonu bir saat
    /// GERİ alır; bunu yalnız bu test görür.
    /// </summary>
    [Fact]
    public async Task Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        // İleri damga uydurma değil: duraklatma `max(UtcNow, önceki + 1 tick)`
        // yazıyor, yani saat geri atlayan bir makinede jeton gerçekten
        // "gelecekte" kalabiliyor. Bir saat, saat çözünürlüğünden bağımsız
        // olsun diye seçildi — testin flaky olmaması bu farka dayanıyor.
        var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        campaign.ClaimedAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var previous = campaign.ClaimedAt!.Value;

        await job.RunAsync(campaignId);

        _factory.Sms.Sent.Should().HaveCount(2, "karşı yazıcı yok, koşu bitmeli");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        after.Status.Should().Be("completed");
        after.ClaimedAt.Should().NotBeNull();
        after.ClaimedAt!.Value.Should().BeAfter(previous,
            "üstlenme damgası ileri damgayı GERİ alırsa, duraklatmadan önce "
            + "kampanyayı okumuş bayat bir işçi sahipliği yeniden kazanır");
    }

    /// <summary>
    /// Üstlenme çakışmasının <c>return</c>'ü — alıcı listesi BOŞken. Döngü
    /// başındaki sahiplik yoklaması buradaki tek koruma DEĞİL, hiç koruma
    /// değil: gönderilecek alıcı kalmadığında döngü bir kez bile dönmez ve
    /// koşu doğrudan tamamlama + iade bloğuna gider. Üstlenemediğimiz bir
    /// kampanyanın iadesini yazmak krediyi yoktan var eder.
    /// </summary>
    [Fact]
    public async Task Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz()
    {
        using var worker = _factory.Services.CreateScope();
        var db = worker.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, _, _) = await SeedAsync(db);

        // İki alıcı da sonuçlanmış ("failed") ama iadesi HENÜZ yazılmamış:
        // `owed - RefundedCredits = 2 - 0 = 2`. Yani tamamlama bloğuna
        // ulaşılırsa para gerçekten hareket eder.
        var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        var licenseId = campaign.LicenseId;
        campaign.RefundedCredits = 0;
        foreach (var r in await db.SmsCampaignRecipients
                     .Where(r => r.CampaignId == campaignId).ToListAsync())
        {
            r.Status = "failed";
            r.Error = "provider-rejected";
        }
        await db.SaveChangesAsync();

        var creditsBefore = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        // İşçi kampanyayı ZATEN okudu (yukarıdaki `campaign` bu scope'ta
        // izleniyor, jeton özgün değeri null). Şimdi başkası jetonu ilerletiyor
        // — durumu değiştirmiyor, çünkü test edilen şey durum kapısı değil
        // üstlenme CAS'ı.
        using (var rival = _factory.Services.CreateScope())
        {
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = await rdb.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
            c.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await rdb.SaveChangesAsync();
        }

        await worker.ServiceProvider
            .GetRequiredService<SmsCampaignSendJob>()
            .RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("pending", "üstlenme düştüyse koşu hiç başlamamıştır");
        after.CompletedAt.Should().BeNull();
        after.RefundedCredits.Should().Be(0);

        (await vdb.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(creditsBefore,
                "üstlenemediğimiz kampanyanın iadesini yazmak krediyi yoktan var eder");

        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0, "bu koşu hiç sahip olmadı, ledger'a dokunmamalı");
    }

    [Fact]
    public async Task Paused_kampanya_hic_ustlenilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

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
        var (campaignId, _, _) = await SeedAsync(db);

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

    [Fact]
    public async Task Diriltilen_kampanya_iadeyi_ikinci_kez_yapmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        // Tamamlanmış ve iadesi YAPILMIŞ bir kampanyayı, bayat bir "duraklat"
        // yazımının + devam ettirmenin dirilteceği hâle kur: durum yine
        // "pending", ama her iki alıcı da sonuçlanmış ve RefundedCredits dolu.
        // Job bu kampanyayı üstlenip hiç alıcı bulamayacak ve doğrudan
        // tamamlamaya gidecek — iade orada ikinci kez yazılırsa kredi
        // yoktan var edilir.
        var campaign = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        var licenseId = campaign.LicenseId;
        campaign.Status = "pending";
        campaign.ClaimedAt = null;
        campaign.RefundedCredits = 2; // 2 failed × 1 segment — zaten ödendi
        foreach (var r in await db.SmsCampaignRecipients
                     .Where(r => r.CampaignId == campaignId).ToListAsync())
        {
            r.Status = "failed";
            r.Error = "boom";
        }
        await db.SaveChangesAsync();

        var creditsBefore = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        await job.RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId);
        after.Status.Should().Be("completed");
        after.RefundedCredits.Should().Be(2, "borç zaten ödenmişti, artmamalı");

        (await vdb.LicenseSmsBalances.AsNoTracking().SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(creditsBefore,
                "iade idempotent değilse aynı krediler ikinci kez bakiyeye eklenir");

        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0, "bu koşu yeni bir iade işlemi yazmamalı");
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

**2b — Bakiye retry'ının kampanya çakışmasını yutmadığını kanıtla.**

`OrderDeck.LicenseServer.Tests/Services/Sms/SmsBalanceConcurrencyTests.cs` —
**mevcut sınıfın içine** ekle. `_factory`, `SeedAsync(int)`, `ReadStateAsync`
ve ilişkisel fixture (`[Collection(SqlServerCollection.Name)]` +
`RelationalApiFactory`) zaten var; yeni bir sınıf açma.

> **Bu ikisi neden Testcontainers, yukarıdakiler neden InMemory?** Yukarıdaki
> kampanya testleri "eski izlenen nesne + jeton reddi" iddiası taşıyor; EF
> InMemory eşzamanlılık jetonlarını **uyguluyor** (özgün değeri karşılaştırıp
> `DbUpdateConcurrencyException` atıyor), o yüzden orada yeterli. Buradaki
> iddia farklı: çakışma anında bakiye `UPDATE`'inin ve ledger `INSERT`'ünün
> **birlikte** geri alındığı. InMemory çoklu-varlık geri alma sağlamaz —
> tek `SaveChanges`'in atomikliği yalnız ilişkisel sağlayıcıda kanıtlanır.

```csharp
    private async Task<Guid> SeedRefundCampaignAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaignId = Guid.NewGuid();

        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Kampanya",
            Status = "sending",
            ClaimedAt = DateTimeOffset.UnixEpoch,
            SegmentsPerMessage = 1,
            RecipientCount = 2,
            ReservedCredits = 2,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        for (var i = 0; i < 2; i++)
        {
            db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}",
                Status = "failed",
                Error = "provider-rejected",
            });
        }

        await db.SaveChangesAsync();
        return campaignId;
    }

    /// <summary>
    /// Bulgu 2 — bayat bir tamamlanma+iade, kampanya çakışmasında TOPTAN
    /// düşmeli. Bugün `ApplyAndSaveAsync` `ex.Entries`'in tamamını yeniden
    /// yüklüyor: hazırlanmış `completed` + `RefundedCredits` silinip yalnız
    /// bakiye artışı hayatta kalıyor, yani kampanya "hiç tamamlanmamış" ama
    /// krediler İADE EDİLMİŞ oluyor. Sonraki koşu iadeyi bir kez daha yazar.
    /// </summary>
    [Fact]
    public async Task Kampanya_cakismasi_iadeyi_yeniden_uygulamaz()
    {
        var licenseId = await SeedAsync(initialCredits: 100);
        var campaignId = await SeedRefundCampaignAsync(licenseId);

        using (var workerScope = _factory.Services.CreateScope())
        {
            var workerDb = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var balance = workerScope.ServiceProvider
                .GetRequiredService<LicenseSmsBalanceService>();
            var campaign = await workerDb.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

            // İşçi kampanyayı okuduktan SONRA başka biri devam ettiriyor:
            // ClaimedAt değişti, yani elimizdeki tamamlanma artık bayat.
            using (var resumeScope = _factory.Services.CreateScope())
            {
                var resumeDb = resumeScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var resumed = await resumeDb.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

                resumed.Status = "pending";
                resumed.ClaimedAt = null;
                await resumeDb.SaveChangesAsync();
            }

            campaign.Status = "completed";
            campaign.CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.ClaimedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.RefundedCredits = 2;

            Func<Task> staleRefund = async () =>
            {
                await balance.ApplyAndSaveAsync(
                    licenseId, 2, "send-refund",
                    reason: $"campaign:{campaignId} failed=2",
                    createdByCustomerId: null,
                    disallowNegative: false,
                    CancellationToken.None);
            };

            await staleRefund.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.AsNoTracking()
                .SingleAsync(c => c.Id == campaignId);

            campaign.Status.Should().Be("pending");
            campaign.ClaimedAt.Should().BeNull();
            campaign.CompletedAt.Should().BeNull();
            campaign.RefundedCredits.Should().Be(0);

            (await db.LicenseSmsBalances
                .Where(b => b.LicenseId == licenseId)
                .Select(b => b.CreditsRemaining)
                .SingleAsync()).Should().Be(100, "düşen tamamlanma kredi yaratmamalı");

            (await db.LicenseSmsTransactions.CountAsync(
                t => t.LicenseId == licenseId && t.Kind == "send-refund"))
                .Should().Be(0, "ledger satırı bakiyeyle birlikte geri alınmalı");
        }

        // Kampanya gerçekten yeniden koşturulduğunda iade BİR KEZ yazılmalı;
        // ikinci koşu hiç alıcı bulamayıp doğrudan tamamlamaya gider ve
        // idempotans farkı sıfır çıkar.
        using (var retryScope = _factory.Services.CreateScope())
        {
            await retryScope.ServiceProvider
                .GetRequiredService<SmsCampaignSendJob>().RunAsync(campaignId);
        }

        using (var duplicateScope = _factory.Services.CreateScope())
        {
            await duplicateScope.ServiceProvider
                .GetRequiredService<SmsCampaignSendJob>().RunAsync(campaignId);
        }

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.AsNoTracking()
                .SingleAsync(c => c.Id == campaignId);

            campaign.Status.Should().Be("completed");
            campaign.RefundedCredits.Should().Be(2);

            (await db.LicenseSmsBalances
                .Where(b => b.LicenseId == licenseId)
                .Select(b => b.CreditsRemaining)
                .SingleAsync()).Should().Be(102);

            var refunds = await db.LicenseSmsTransactions.AsNoTracking()
                .Where(t => t.LicenseId == licenseId && t.Kind == "send-refund")
                .ToListAsync();

            refunds.Should().ContainSingle();
            refunds.Single().Amount.Should().Be(2);
        }
    }

    /// <summary>
    /// Gerileme koruması: MEŞRU bakiye çakışması (paralel bakiye yüklemesi)
    /// hâlâ yeniden denenmeli. Retry'ı tamamen kaldıran "düzeltme" bu testi
    /// kırar — yükleme araya girdiğinde iade 409/500'e dönüşürdü.
    /// </summary>
    [Fact]
    public async Task Yalniz_bakiye_cakismasi_tamamlanma_ve_iadeyi_korur()
    {
        var licenseId = await SeedAsync(initialCredits: 100);
        var campaignId = await SeedRefundCampaignAsync(licenseId);

        using (var workerScope = _factory.Services.CreateScope())
        {
            var db = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var service = workerScope.ServiceProvider
                .GetRequiredService<LicenseSmsBalanceService>();

            var staleBalance = await db.LicenseSmsBalances
                .SingleAsync(b => b.LicenseId == licenseId);
            var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);

            using (var topupScope = _factory.Services.CreateScope())
            {
                var topupDb = topupScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var balance = await topupDb.LicenseSmsBalances
                    .SingleAsync(b => b.LicenseId == licenseId);

                balance.CreditsRemaining += 50;
                // Jeton KESİN ilerlemeli: `UtcNow` seed damgasının gerisinde
                // kalırsa çakışma hiç doğmaz ve test hiçbir şey kanıtlamaz.
                balance.UpdatedAt = staleBalance.UpdatedAt.AddTicks(1);

                topupDb.LicenseSmsTransactions.Add(new LicenseSmsTransaction
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    Amount = 50,
                    Kind = "purchase",
                    CreatedAt = DateTimeOffset.UtcNow,
                });

                await topupDb.SaveChangesAsync();
            }

            campaign.Status = "completed";
            campaign.CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.ClaimedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
            campaign.RefundedCredits = 2;

            (await service.ApplyAndSaveAsync(
                licenseId, 2, "send-refund",
                reason: $"campaign:{campaignId} failed=2",
                createdByCustomerId: null,
                disallowNegative: false,
                CancellationToken.None)).Should().Be(152);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var completed = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        completed.Status.Should().Be("completed", "bakiye retry'ı kampanyayı geri almamalı");
        completed.RefundedCredits.Should().Be(2);

        var (credits, ledgerSum, txCount) = await ReadStateAsync(licenseId);
        credits.Should().Be(152);
        // `ledgerSum` 52: seed başlangıç bakiyesini ledger satırı YAZMADAN
        // kuruyor, ledger'da yalnız +50 yükleme ve +2 iade var.
        ledgerSum.Should().Be(52);
        txCount.Should().Be(2);
    }
```

- [ ] **Adım 3: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~SmsCampaignPauseTests|FullyQualifiedName~SmsBalanceConcurrencyTests"
```
Beklenen FAIL —
- `Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez`: 2 SMS gitmiş,
  durum `completed`.
- `Gercek_kapatma_yolu_kosan_isi_durdurur`: aynı sebeple 2 SMS gitmiş.
- `Diriltilen_kampanya_iadeyi_ikinci_kez_yapmaz`: bakiye 2 kredi artmış.
- `Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez`: mevcut kod
  `campaign.ClaimedAt = now;` yazıyor (`SmsCampaignSendJob.cs:78`) ve her alıcıda
  ham `UtcNow` ile tazeliyor (`:172`) — son damga `UtcNow` civarında kalır,
  tohumlanan ileri damganın bir saat GERİSİNDE. `BeAfter` patlar.
- `Kampanya_cakismasi_iadeyi_yeniden_uygulamaz`: beklenen
  `DbUpdateConcurrencyException` HİÇ ÇIKMAZ — mevcut retry onu yutar ve bakiye
  `104` olur (`100 + 2 + 2`, ikinci ekleme reload'suz `amount` tekrarından).

Zaten PASS olması gerekenler — gerileme koruması, kırmızı görürsen ÖNCE onu
anla:
- `Paused_kampanya_hic_ustlenilmez`
- `Paused_kampanya_kurtarma_isiyle_diriltilmez`
- `Yalniz_bakiye_cakismasi_tamamlanma_ve_iadeyi_korur`
- `Kapatmadan_once_okunan_kampanya_sonradan_ustlenilemez` (üç `InlineData`'nın
  hepsi)
- `Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz`

> **Son iki madde neden KIRMIZI değil?** Üstlenme yazımının çakışma catch'i
> bugün **zaten var** (`SmsCampaignSendJob.cs:85-90`: log + `return`), ve
> `ClaimedAt` bugün de eşzamanlılık jetonu (`LicenseDbContext.cs:786`). Görev 3
> duraklatmayı jetonu ilerletir hâle getirdiği ANDA bayat işçinin üstlenmesi
> zaten düşüyor — yani Görev 11 bu davranışı **kurmuyor, koruyor**. Bu görevin
> o blokta yaptığı tek değişiklik `Detached` damgası (kirli kopya sonraki
> `SaveChanges`'e binmesin diye).
>
> İkisini "gereksiz" sayıp atma, ama rollerini de karıştırma:
> - `Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz` Adım 8'in
>   **mutasyon 3'ünün tek katili**. O olmadan üstlenme CAS'ının `return`'ü
>   bir gün fark edilmeden kaldırılabilir.
> - `Kapatmadan_once_okunan_kampanya_sonradan_ustlenilemez` **hiçbir mutasyonun
>   katili değil** — kilitlediği şey Görev 3 ile Görev 11'in BİRLİKTE ürettiği
>   uçtan uca davranış: duraklatma jetonu ilerletiyor, bayat işçi düşüyor,
>   kampanya `paused` kalıyor. Görev 3'ün jeton ilerletmesi geri alınırsa bu
>   test kırmızıya döner; o yüzden duruyor.
>
> Mutasyon 2'nin katili bu ikisi DEĞİL, yukarıdaki kırmızı doğan
> `Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez`'tir.
> **Bir testin kırmızı DOĞMAMASI hiçbir şeyi kilitlemediği anlamına gelmez —
> ama neyi kilitlediğini yazmazsan, bir sonraki okuyan onu siler.**

> **`SmsBalanceConcurrencyTests` Docker ister** (Testcontainers). Yerelde
> düşerse PowerShell'den `DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine`
> — git-bash bu değeri bozuyor.

- [ ] **Adım 4: Durum sözlüğünü güncelle**

`OrderDeck.LicenseServer/Domain/SmsCampaign.cs:32` — XML yorumunu değiştir:

```csharp
    /// <summary>"pending" | "sending" | "paused" | "completed" | "failed".
    /// "paused": kurulum admin tarafından kapatıldı; kalan alıcılar "pending"
    /// kalır, rezervasyon iade EDİLMEZ — kampanya devam ettirilebilir.</summary>
```

`HasMaxLength(16)` altı harfi taşıyor; şema göçü gerekmiyor.

- [ ] **Adım 5: `SmsCampaignSendJob`'a sahiplik yardımcılarını ekle**

Duraklatma artık `ClaimedAt` jetonunu ilerletiyor (Görev 3). Bu, job'a iki
sorumluluk getiriyor: (a) kendi damgasını **monoton** atmak, (b) gönderilmiş
bir SMS'in alıcı sonucunu çakışmada **kaybetmemek**.

`SmsCampaignSendJob.cs` — sınıfın içine, `RunAsync`'in ÜSTÜNE:

```csharp
    /// <summary>
    /// Sahiplik damgasının bir sonraki değeri. Ham <c>UtcNow</c> ataması
    /// yetmez: saat monoton değil, üstelik duraklatma damgayı ileri
    /// atabiliyor. Aynı ya da geri giden bir damga, duraklatmadan ÖNCE
    /// kampanyayı okumuş işçinin üstlenmeyi geri kazanmasına yol açar.
    ///
    /// <para><c>NetgsmAccountService</c>'te aynı isimde bir metot var —
    /// bu AYRI bir sınıfın özel metodu, ortaklaştırılmadı: iki taraf da
    /// tek satırlık ve birbirine bağımlı değil.</para>
    /// </summary>
    private static DateTimeOffset NextClaimedAt(DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        return previous.HasValue && now <= previous.Value
            ? previous.Value.AddTicks(1)
            : now;
    }

    /// <summary>
    /// Alıcı sonucunu + kalp atışını kaydeder. Kampanya bu arada başkası
    /// tarafından yazıldıysa (duraklatma, devam ettirme) <c>false</c> döner
    /// ve çağıran koşuyu bitirir.
    ///
    /// <para><b>Neden detach edip yeniden kaydediyoruz?</b> SMS çağrısının
    /// dış etkisi GERÇEKLEŞTİ — operatöre gitti, geri alınamaz. Çakışmayı
    /// olduğu gibi dışarı bıraksaydık alıcı satırı <c>pending</c> kalırdı ve
    /// kampanya devam ettirildiğinde AYNI KİŞİYE ikinci kez SMS giderdi.
    /// Kampanyayı detach edip yeniden kaydetmek, karşı tarafın kararına
    /// (paused/pending) dokunmadan yalnız alıcının sonucunu diske indirir.</para>
    ///
    /// <para><c>ReferenceEquals</c> kasıtlı: çakışan tek şey BU kampanya
    /// nesnesi değilse (ör. alıcı satırı) burası sorumlu değildir, istisna
    /// dışarı çıkar.</para>
    /// </summary>
    private async Task<bool> SaveRecipientResultAsync(
        SmsCampaign campaign, CancellationToken ct)
    {
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex) when (
            ex.Entries.Count > 0
            && ex.Entries.All(e => ReferenceEquals(e.Entity, campaign)))
        {
            _db.Entry(campaign).State = EntityState.Detached;
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }
```

- [ ] **Adım 6: `RunAsync`'i birleşik gövdeyle değiştir**

`SmsCampaignSendJob.RunAsync`'in **tamamını** aşağıdakiyle değiştir. Üç ayrı
düzeltme burada birleşiyor; parça parça uygulanırsa birbirini bozarlar.

**Delik 1 — çift iade (P1).** Bugün iade koşulsuz yazılıyor
(`SmsCampaignSendJob.cs:188-198`): `refund = failedCount × SegmentsPerMessage`.
`failedCount` DB'den, yani **koşudan bağımsız** okunuyor. Tamamlanmış bir
kampanya bir kez daha koşarsa (Delik 2, ya da kurtarma işinin herhangi bir
tekrarı) hiç alıcı bulunmaz, doğrudan tamamlamaya gidilir ve **aynı iade
ikinci kez yazılır**. Kredi yoktan var olur.

**Delik 2 — tamamlanmış kampanyanın `paused`'a düşmesi.** `ClaimedAt` bir
eşzamanlılık jetonu (`LicenseDbContext.cs:786`) ve job onu her alıcıda tazeliyor
(`:172`) — ama **tamamlanırken tazelemiyor** (`:182-183`). Görev 12'nin kapatma
yolu kampanyayı okuyup `paused` yazana kadar geçen mikro-saniyelerde job
tamamlanırsa, admin'in `UPDATE ... WHERE ClaimedAt = <son kalp atışı>` koşulu
hâlâ **tutar** ve tamamlanmış kampanya `paused` olur. Sonra Görev 13 onu
diriltir → Delik 1.

**Delik 3 — üstlenme damgasının geri gitmesi.** Üstlenme yazımı bugün çakışmayı
**yakalıyor** (`SmsCampaignSendJob.cs:85-90`: log + `return`) — burada onu
kurmuyoruz. Eksik olan iki şey var: (a) damga ham `UtcNow` (`:78`), yani
duraklatmanın ileri ittiği bir jetonu GERİ alabiliyor ve bayat işçi bir
sonraki turda üstlenmeyi kazanıyor; (b) çakışmada kampanya detach
edilmediği için bu bağlamın kirli `sending` kopyası izlenmeye devam ediyor
ve `_db`'de sonradan atılacak herhangi bir `SaveChanges`'e binebiliyor.

> **(b) tek başına iadeyi YAZDIRMAZ** — o sonucu `return` engelliyor
> (`SmsCampaignSendJob.cs:89`), yani koşu çakışmadan sonra zaten bitiyor.
> İkisi ayrı sözleşme ve ayrı testleri var: `return` korumasını Adım 2'deki
> `Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz` kilitliyor
> (mutasyon 3'ü tek öldüren test), detach'i ise aynı testin
> "kirli kopya sonraki yazıma binmesin" beklentisi taşıyor. Birini
> diğerinin gerekçesi olarak yazmak, `return` silindiğinde detach'in
> koruyacağı yanılgısını yaratır — korumaz, çünkü `ApplyAndSaveAsync`
> bakiyeyi kendi izlediği satırdan yazıyor.

```csharp
    public async Task RunAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _db.SmsCampaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct);

        if (campaign is null)
        {
            _log.LogWarning("SmsCampaignSendJob: campaign {Id} not found", campaignId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var staleSending = campaign.Status == "sending"
            && (campaign.ClaimedAt is null || now - campaign.ClaimedAt >= ClaimLease);

        // "paused" bu kapıdan zaten geçemez: ne "pending" ne bayat "sending".
        if (campaign.Status != "pending" && !staleSending)
        {
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} status={Status} claimedAt={ClaimedAt}, skipping",
                campaignId, campaign.Status, campaign.ClaimedAt);
            return;
        }

        var resumed = campaign.Status == "sending";
        campaign.Status = "sending";
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Delik 3: okuma ile üstlenme arasında biri kampanyayı yazdı
            // (büyük olasılıkla duraklatma). Elimizdeki karar bayat — SESSİZCE
            // ÇEKİL. Detach şart: bu bağlamın kirli kopyası sonraki
            // SaveChanges'e binmemeli.
            _db.Entry(campaign).State = EntityState.Detached;
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} claim changed, skipping", campaignId);
            return;
        }

        if (resumed)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} resumed from stale 'sending' state",
                campaignId);
        }

        var recipients = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId && r.Status == "pending")
            .ToListAsync(ct);

        var phones = recipients.Select(r => r.Phone).Distinct().ToList();
        var brandCode = await _accounts.GetBrandCodeAsync(campaign.LicenseId, ct);

        if (brandCode is null)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} lisansının doğrulanmış İYS markası yok",
                campaignId);
        }

        var consents = brandCode is null
            ? new Dictionary<string, IysConsent>()
            : await _db.IysConsents
                .Where(c => c.BrandCode == brandCode
                    && c.ChannelType == "MESAJ"
                    && c.RecipientType == "BIREYSEL"
                    && phones.Contains(c.Recipient))
                .ToDictionaryAsync(c => c.Recipient, ct);

        foreach (var recipient in recipients)
        {
            // §2.5: kurulum koşu sırasında kapatılabilir. Skaler projeksiyon
            // BİLİNÇLİ — anonim tipe `Select` kimlik çözümlemesine girmez,
            // yani bu bağlamda izlenen "sending" kopyasını değil DİSKTEKİ
            // değeri okur. Entity çekseydik kendi yazdığımızı geri okurduk.
            //
            // `ClaimedAt` karşılaştırması `Status` kontrolünün üstüne şunu
            // ekliyor: kampanya duraklatılıp YENİDEN "pending"/"sending"
            // yapıldıysa (Görev 13 devam ettirme) durum yine "sending"
            // görünebilir ama sahip ARTIK BİZ DEĞİLİZ. Damga bunu yakalar.
            var current = await _db.SmsCampaigns
                .Where(c => c.Id == campaignId)
                .Select(c => new { c.Status, c.ClaimedAt })
                .FirstOrDefaultAsync(ct);

            if (current is null
                || current.Status != "sending"
                || current.ClaimedAt != campaign.ClaimedAt)
            {
                // İade YOK: kalan alıcılar "pending" ve rezervasyon onların
                // karşılığı. Burada iade edersek kampanya devam ettirildiğinde
                // aynı krediyi ikinci kez harcarız.
                _log.LogWarning(
                    "SmsCampaignSendJob: campaign {Id} ownership lost mid-run, stopping after {Sent} sends",
                    campaignId, recipients.Count(x => x.Status == "sent"));
                return;
            }

            consents.TryGetValue(recipient.Phone, out var consent);

            if (!IysConsentGate.CanSend(consent))
            {
                recipient.Status = "failed";
                recipient.Error = brandCode is null
                    ? "iys-brand-missing"
                    : consent is null
                        ? "iys-consent-missing"
                        : "iys-consent-not-onay";
                recipient.SentAt = null;

                if (!await SaveRecipientResultAsync(campaign, ct)) return;
                continue;
            }

            try
            {
                await _sms.SendAsync(
                    recipient.Phone, campaign.MessageBody, SmsKind.Commercial, ct);

                recipient.Status = "sent";
                recipient.SentAt = DateTimeOffset.UtcNow;
                recipient.Error = null;
            }
            catch (Exception ex)
            {
                recipient.Status = "failed";
                recipient.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

                _log.LogWarning(ex,
                    "SmsCampaignSendJob: send failed for campaign {Id} recipient {RecipientId}",
                    campaignId, recipient.Id);
            }

            if (!await SaveRecipientResultAsync(campaign, ct)) return;
        }

        // İade, bu koşunun sayacından değil DB'deki toplam failed sayısından:
        // devralınan koşuda önceki koşunun failed'ları da iade edilmeli
        // (önceki koşu tamamlanamadığı için hiç iade yapmamıştı).
        var failedCount = await _db.SmsCampaignRecipients
            .CountAsync(r => r.CampaignId == campaignId && r.Status == "failed", ct);

        campaign.Status = "completed";
        campaign.CompletedAt = DateTimeOffset.UtcNow;
        // Delik 2: tamamlanmada da damga tazelenir. Tazelemezsek, elinde
        // tamamlanma ÖNCESİ kopya tutan bir "duraklat" yazımı (Görev 12)
        // çakışma ALMAZ ve bitmiş kampanyayı paused'a çevirir. Tazeleyince o
        // yazım DbUpdateConcurrencyException alır, yeniden okur ve kampanyayı
        // artık pending/sending listesinde bulamaz — doğru olanı yapar.
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        // Delik 1 — iade İDEMPOTENT: hak edilen toplamın, bugüne dek FİİLEN
        // iade edilenin üstünde kalan kısmı ödenir. Kampanya duraklatılıp
        // devam ettirilerek ikinci kez tamamlanırsa failedCount aynı kalır,
        // fark sıfır çıkar ve ikinci bir iade yazılmaz.
        var owed = failedCount * campaign.SegmentsPerMessage;
        var refund = owed - campaign.RefundedCredits;

        if (refund > 0)
        {
            // N05: gerçekleşen iade kampanyaya da yazılır — iade tx'iyle aynı
            // SaveChanges'te (atomik), raporlama hesap yerine bunu okur.
            campaign.RefundedCredits = owed;
            await _balance.ApplyAndSaveAsync(
                campaign.LicenseId, refund, "send-refund",
                reason: $"campaign:{campaignId} failed={failedCount}",
                createdByCustomerId: null, disallowNegative: false, ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "SmsCampaignSendJob: campaign {Id} completed — {Sent} sent this run, {Failed} failed total",
            campaignId, recipients.Count(r => r.Status == "sent"), failedCount);
    }
```

> **Tamamlanma çakışması `SaveRecipientResultAsync`'e GİRMEZ.** Son bloktaki
> `SaveChangesAsync`/`ApplyAndSaveAsync` doğrudan çağrılıyor: orada çakışma
> alırsak tamamlanma kararı bayattır ve **düşmesi gerekir** — detach edip
> yeniden kaydetmek, karşı tarafın devam ettirme kararını silerdi. Bir
> sonraki adım bu istisnanın bakiye servisinde yutulmadığını garanti ediyor.

> **`ClaimedAt` tazelemesinin adanmış testi neden yok?** Delik 2'yi
> deterministik kanıtlamak için son alıcı kaydı ile tamamlanma yazımı ARASINDA
> bir kanca gerekirdi; öyle bir nokta yok. Paraya dönüşen sonuç (Delik 1)
> `Diriltilen_kampanya_iadeyi_ikinci_kez_yapmaz` ile, damganın monotonluğu ise
> `Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez` ile
> kapanıyor: o test ileri damgalı kampanyayı çakışmasız üstlendirip koşuyu
> sonuna kadar götürüyor, yani üstlenme ve alıcı tazelemelerinin ürettiği
> NİHAİ damgayı ölçüyor — saate hiç bakmadan. Tamamlanmadaki tazelemenin tek
> başına izole edilmemesi **bilinçli karar; "test eksik" diye geri çevirme.**

- [ ] **Adım 6b: Bakiye retry'ı yalnız kendi satırını yeniden yüklesin**

Yukarıdaki değişiklikler kampanya çakışmalarını **gerçek** hâle getirdi. Bugün
o çakışma `LicenseSmsBalanceService.ApplyAndSaveAsync`'in retry'ına düşüyor
(`:101-111`): `ex.Entries`'in TAMAMI yeniden yükleniyor, yani hazırlanmış
`completed` + `RefundedCredits` siliniyor, sonra `amount` ikinci kez ekleniyor.
Sonuç: kampanya tamamlanmamış görünürken krediler iade edilmiş oluyor, sonraki
koşu iadeyi bir kez daha yazıyor.

`LicenseSmsBalanceService.ApplyAndSaveAsync`'in **tamamını** değiştir:

```csharp
    public async Task<int?> ApplyAndSaveAsync(
        Guid licenseId,
        int amount,
        string kind,
        string? reason,
        Guid? createdByCustomerId,
        bool disallowNegative,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        var now = DateTimeOffset.UtcNow;

        _db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Amount = amount,
            Kind = kind,
            Reason = reason,
            CreatedByCustomerId = createdByCustomerId,
            CreatedAt = now,
        });

        var balance = await _db.LicenseSmsBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId, ct);

        if (balance is null)
        {
            if (disallowNegative && amount < 0) return null;

            balance = new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = amount,
                UpdatedAt = now,
            };

            _db.LicenseSmsBalances.Add(balance);
            await _db.SaveChangesAsync(ct);
            return balance.CreditsRemaining;
        }

        balance.CreditsRemaining += amount;
        // Jeton KESİN ilerlemeli — `UtcNow` monoton değil.
        balance.UpdatedAt = now > balance.UpdatedAt ? now : balance.UpdatedAt.AddTicks(1);

        for (var attempt = 1; ; attempt++)
        {
            if (disallowNegative && balance.CreditsRemaining < 0) return null;

            try
            {
                await _db.SaveChangesAsync(ct);
                return balance.CreditsRemaining;
            }
            catch (DbUpdateConcurrencyException ex) when (
                attempt < maxAttempts
                && ex.Entries.Count > 0
                && ex.Entries.All(e => ReferenceEquals(e.Entity, balance)))
            {
                // YALNIZ bakiye satırı. Çağıran bu SaveChanges'e kendi
                // kararlarını da (kampanya tamamlanması, RefundedCredits)
                // iliştirmiş olabilir; `ex.Entries`'i toptan reload etmek
                // onları siler ve `amount`u ikinci kez ekler. Kampanya
                // çakışması buraya AİT DEĞİLDİR: dışarı çıkar, çağıranın
                // kararı düşer, iş yeniden koştuğunda taze okunur.
                await _db.Entry(balance).ReloadAsync(ct);

                // Satır silinmişse tazeleyecek bir şey yok.
                if (_db.Entry(balance).State == EntityState.Detached) throw;

                balance.CreditsRemaining += amount;

                var retryAt = DateTimeOffset.UtcNow;
                balance.UpdatedAt = retryAt > balance.UpdatedAt
                    ? retryAt
                    : balance.UpdatedAt.AddTicks(1);
            }
        }
    }
```

- [ ] **Adım 7: Testlerin geçtiğini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~SmsCampaignPauseTests|FullyQualifiedName~SmsBalanceConcurrencyTests|FullyQualifiedName~SmsCampaignIysGateTests|FullyQualifiedName~SmsCampaignSendJob|FullyQualifiedName~SmsCampaignRecovery"
```
Beklenen: PASS — hem yeni testler hem mevcut gönderim/kurtarma/bakiye
paketleri. Özellikle şu ikisi hâlâ geçmeli:
- `SmsCampaignTests.Job_resumes_stale_sending_campaign_without_resending`:
  `RefundedCredits` 0'dan başlıyor, `owed - 0 = 1`, iade aynen yazılıyor.
- `Yalniz_bakiye_cakismasi_tamamlanma_ve_iadeyi_korur`: meşru bakiye
  çakışmasında retry hâlâ çalışıyor.

- [ ] **Adım 8: Mutasyon provası — beş iddianın da gerçekten kilitli olduğunu gör**

Her mutasyonu tek tek uygula, testi koş, **sonra geri al**.

**1) Duraklatma yoklaması kampanyayı tamamlasın.** Döngü başındaki sahiplik
koşulunun `return;` satırını şununla değiştir:
```csharp
                campaign.Status = "completed";
                await _db.SaveChangesAsync(ct);
                return;
```
Düşmeli: `Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez`
(`Status` `completed` geldi). Düşmüyorsa assert eksiktir.

> Döngü içindeki `current.ClaimedAt != campaign.ClaimedAt` karşılaştırmasının
> **bu görevde adanmış mutasyonu yok**: buradaki testlerde sahipliği zaten
> üstlenme çakışması ya da `SaveRecipientResultAsync` yakalıyor. O
> karşılaştırma Görev 13'ün devam ettirme yolu için var (duraklatılmış
> kampanya yeniden `pending` yapılınca durum tekrar `sending` olabilir ama
> sahip değişmiştir) ve mutasyonu **Görev 13 Adım 5b'de** koşulur — orada
> araya girme noktası `RecordingSmsSender.OnSent` DEĞİL, Görev 12'nin
> `SaveHookInterceptor.AfterSave` kancasıdır (gerekçe o adımda yazılı:
> `OnSent` alıcı sonucu kaydedilmeden ÖNCE ateşlendiği için sahiplik
> kaybı `SaveRecipientResultAsync`'e düşer ve karşılaştırmaya hiç sıra
> gelmez).

**2) Üstlenme damgası ham `UtcNow` olsun.** `campaign.ClaimedAt =
NextClaimedAt(campaign.ClaimedAt);` satırını (üstlenme bloğundakini)
`campaign.ClaimedAt = DateTimeOffset.UtcNow;` yap.
Düşmeli: `Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez` —
tohumlanan damga bir saat ileride, ham `UtcNow` onu geri alır, `BeAfter` patlar.

> **`Kapatmadan_once_okunan_kampanya_sonradan_ustlenilemez(pending, true)` bu
> mutasyonu ÖLDÜRMEZ** — sanılabileceğinin aksine. O vakada üstlenme yazımı
> jetonun değeri ne olursa olsun çakışmayla düşüyor, yani atanan damga diske
> hiç inmiyor; testin `BeAfter` assert'i **duraklatmanın** damgasını ölçüyor.
> Mutasyonu gören tek test, üstlenmenin ÇAKIŞMASIZ geçtiği yukarıdaki yenidir.

**3) Üstlenme çakışması yutulsun.** Üstlenmedeki `catch
(DbUpdateConcurrencyException)` bloğunun `return;` satırını sil (detach + log
kalsın).
Düşmeli: `Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz` — alıcı
listesi boş olduğu için döngü hiç dönmez, koşu doğrudan tamamlama bloğuna
gider ve detached kampanyanın iadesi (`2` kredi + bir `send-refund` satırı)
gerçekten yazılır.

> **Teori testi bu mutasyonu da ÖLDÜRMEZ.** Orada `return` silinse bile döngü
> başındaki sahiplik yoklaması diskte `paused` görüp çıkıyor; kampanya
> `completed` olmuyor ve üç vaka da yeşil kalıyor. Üstlenme CAS'ını gerçekten
> kilitleyen şey, döngünün hiç dönmediği senaryodur.

**4) İade idempotansı kalksın.** `var refund = owed - campaign.RefundedCredits;`
→ `var refund = owed;`.
Düşmeli: `Diriltilen_kampanya_iadeyi_ikinci_kez_yapmaz`.

**5) Bakiye retry'ı yine toptan reload etsin.** Catch'i **`:101-111`'deki
özgün hâline** döndür — `when` filtresinden `ex.Entries` koşullarını çıkar,
gövdeyi de tamamen değiştir:
```csharp
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                balance.CreditsRemaining += amount;
                balance.UpdatedAt = DateTimeOffset.UtcNow;
            }
```
Düşmeli: `Kampanya_cakismasi_iadeyi_yeniden_uygulamaz` — beklenen istisna
çıkmaz, bakiye `104` olur.

> **`104` sayısı yukarıdaki gövdenin TAMAMINA bağlı.** Kampanya entry'si
> reload edilince `completed` + `RefundedCredits` geri alınır, ama bakiye
> entry'si `ex.Entries`'te olmadığı için hazırlanmış `102` duruyor; `+=
> amount` onu `104` yapar ve ikinci tur sorunsuz kaydeder. Yalnız `foreach`
> satırını bırakıp `+= amount`'u silersen bakiye `102` çıkar ve mutasyon
> **farklı bir sebeple** düşer — prova o zaman iade tekrarını değil, retry'ın
> varlığını ölçmüş olur. Gövdeyi eksiksiz yaz.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs \
        OrderDeck.LicenseServer.Tests/Services/Sms/SmsBalanceConcurrencyTests.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/RecordingSmsSender.cs \
        OrderDeck.LicenseServer/Domain/SmsCampaign.cs \
        OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs \
        OrderDeck.LicenseServer/Services/Sms/LicenseSmsBalanceService.cs
git commit -m "$(cat <<'EOF'
feat(sms): kampanya gönderimi koşu ortasında duraklatılabilir

Kurulum kapatıldığında devam eden kampanya sonuna kadar gidiyordu. Döngü artık
her alıcıdan önce diskteki durumu VE sahiplik damgasını okuyor; sahiplik
kaybolmuşsa iade yapmadan çıkıyor (kalan alıcılar pending, rezervasyon onların
karşılığı).

Durum yazmak tek başına yetmiyordu: duraklatmadan önce kampanyayı okumuş bir
işçi kendi üstlenmesini yazıp kararı siliyordu. ClaimedAt artık hem
duraklatmada hem üstlenmede hem tamamlanmada monoton ilerliyor ve üstlenme
çakışması işi sessizce durduruyor. Gönderilmiş SMS'in alıcı sonucu çakışmada
da korunuyor — yoksa devam ettirmede aynı kişiye ikinci kez giderdi.

İade RefundedCredits üstünden idempotent hâle geldi ve bakiye servisinin retry'ı
artık yalnız kendi bakiye satırını yeniden yüklüyor: kampanya çakışması oraya
yutulup krediyi yoktan var edemiyor.

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

> **Duraklatma mantığı burada YENİDEN YAZILMAZ.** Görev 8, kesin-ret dalı için
> `NetgsmAccountService.CloseAccountAndPauseCampaignsAsync` metodunu zaten
> yazdı (hesap yazımı + `pending`/`sending` duraklatması tek `SaveChanges`,
> `ClaimedAt` çakışmasında 4 denemeli retry). Bu sayfa onu **çağırır**.
> İki ayrı kopya olsaydı, biri er geç `sending`i ya da retry'ı unutur ve
> "kapat" düğmesi bazı yayıncılarda sessizce hiçbir şey yapmazdı.
>
> Kapatmanın **koşan** bir gönderim işini gerçekten kestiği, Görev 11'in
> `Gercek_kapatma_yolu_kosan_isi_durdurur` testiyle kanıtlandı — orada aynı
> metot, job alıcı döngüsündeyken çağrılıyor. Buradaki testler HTTP yüzeyini
> (yetkilendirme, kiracı sızıntısı, denetim kaydı) doğruluyor.

Açma `Verified` DEĞİL `Failed` yazar. Yönetici marka kodunun İYS'de hâlâ geçerli
olduğunu bilemez; doğrulama tek yoldan, normal doğrulama akışından geçmeli.
Durdurulan kampanyalar da burada devam ettirilmez — devam, doğrulama başarılı
olduğunda gelir (Görev 13).

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs`
- Modify: `OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/SaveHookInterceptor.cs`
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/HookedApiFactory.cs`
- Test: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs`

- [ ] **Adım 0: Kayıt kancası test altyapısını yaz**

Bu görevdeki iki test ve Görev 13'ün yarış testi, **başka bir yazarın tam
doğru anda araya girmesini** kurgulamak zorunda. `Task.Run` + gecikme ile
kurgulanan yarışlar CI'da flaky olur; kancalı bir interceptor deterministik
olur: yazım noktasında dururuz, araya gireriz, devam ederiz.

`ApiFactory` bunun için **zaten** bir uzatma noktası taşıyor
(`ApiFactory.cs:55-59`, doc'u birebir "a fault-injecting `SaveChanges`
interceptor" diyor) — yeni bir genişletme icat etmiyoruz, var olanı
kullanıyoruz.

`OrderDeck.LicenseServer.Tests/TestHelpers/SaveHookInterceptor.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Test kancası: <c>SaveChangesAsync</c>'in HEMEN ÖNCESİNDE ya da HEMEN
/// SONRASINDA rastgele bir iş çalıştırır. Amacı, "tam o anda başka biri
/// yazdı" senaryosunu zamanlamaya değil, SIRAYA dayalı olarak kurmak —
/// <c>Task.Run</c> + <c>Delay</c> ile kurulan yarışlar CI'da flaky olur.
///
/// <para><b>Yeniden girmez.</b> Kanca gövdesi kendi <c>SaveChanges</c>'ini
/// atıyor (zaten bütün mesele o); bayrak olmasaydı kanca kendini sonsuz
/// tetiklerdi. Bayrak <b>örnek</b> düzeyinde, <c>static</c> değil: her test
/// sınıfı kendi fabrikasını kurduğu için paralel sınıflar birbirinin
/// kancasını kilitlemez.</para>
///
/// <para>Kanca <b>her</b> kaydetmede koşar; "yalnız bir kez" isteyen test
/// gövdenin ilk satırında alanı <c>null</c>'lar. İkisi de gerekiyor:
/// devam-ettirme yarışı tek seferlik, retry tükenmesi ise tur tur
/// çakışmak zorunda.</para>
///
/// <para>Boş bırakıldığında tamamen şeffaftır — bu interceptor'ı taşıyan
/// fabrikayı kullanan diğer testlerin davranışı değişmez.</para>
/// </summary>
public sealed class SaveHookInterceptor : SaveChangesInterceptor
{
    private bool _running;

    /// <summary>Yazım diske inmeden önce koşar.</summary>
    public Func<Task>? BeforeSave { get; set; }

    /// <summary>Yazım başarıyla indikten sonra koşar.</summary>
    public Func<Task>? AfterSave { get; set; }

    public void Reset()
    {
        BeforeSave = null;
        AfterSave = null;
        _running = false;
    }

    private async Task RunAsync(Func<Task>? hook)
    {
        if (hook is null || _running) return;
        _running = true;
        try { await hook(); }
        finally { _running = false; }
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(BeforeSave);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(AfterSave);
        return result;
    }
}
```

`OrderDeck.LicenseServer.Tests/TestHelpers/HookedApiFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// <see cref="ApiFactory"/> + paylaşılan bir <see cref="SaveHookInterceptor"/>.
/// Kanca dolduruluncaya kadar davranış <see cref="ApiFactory"/> ile
/// birebir aynıdır, bu yüzden bir test sınıfı bunu fixture olarak alıp
/// testlerinin yalnız birinde kancayı kullanabilir.
/// </summary>
public sealed class HookedApiFactory : ApiFactory
{
    public SaveHookInterceptor Hook { get; } = new();

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
        => opt.AddInterceptors(Hook);
}
```

> **Neden `AddInterceptors` DbContext seviyesinde?** `ApiFactory` interceptor'ı
> InMemory sağlayıcı takasıyla **aynı** options builder'a ekliyor
> (`ApiFactory.cs:113-117`), yani paylaşılan test veritabanı adı korunuyor.
> Interceptor'ı DI'a `IInterceptor` olarak eklemek de çalışırdı ama
> `LicenseReadOnlyDbContext`'e de bulaşırdı; burada yalnız yazan bağlamı
> istiyoruz.

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
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>
/// §2.5 — yönetici kapatma anahtarı. Kapatmanın SMS'i gerçekten kesmesi
/// gerekir: hesabı Disabled yapıp devam eden kampanyayı bırakmak, kararı
/// iki dakika sonra kurtarma işinin geri almasına izin verir.
/// </summary>
public sealed class AdminNetgsmPageTests : IClassFixture<HookedApiFactory>
{
    private readonly HookedApiFactory _factory;

    // Kanca boşken HookedApiFactory, ApiFactory'nin aynısı. Yalnız
    // "retry tükeniyor" testi dolduruyor; o test de kendi içinde
    // temizliyor. Burada ek olarak ctor'da sıfırlıyoruz ki sınıf
    // fixture'ı paylaşan testler birbirinin kancasını miras almasın.
    public AdminNetgsmPageTests(HookedApiFactory factory)
    {
        _factory = factory;
        _factory.Hook.Reset();
    }

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
        LicenseDbContext db, Guid licenseId, string status,
        DateTimeOffset? claimedAt = null)
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
            ClaimedAt = claimedAt,
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
            // TAZE ClaimedAt: canlı bir işçi elinde tutuyor. ClaimedAt bir
            // eşzamanlılık jetonu olduğu için bu, kapatmanın "meşgul" satırı
            // da yazabildiğini gösterir — jeton yüzünden sessizce atlanırsa
            // yayıncıya SMS gitmeye devam ederdi.
            sendingId = await SeedCampaignAsync(
                db, licenseId, "sending", DateTimeOffset.UtcNow);
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
        StatusOf(sendingId).Should().Be("paused",
            "taze claim'li (canlı işçinin elindeki) kampanya da durdurulmalı");
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

    [Fact]
    public async Task Acma_Disabled_olmayan_hesaba_dokunmaz()
    {
        // İki yönetici listeyi hesap Disabled'ken açtı. Biri açtı, yayıncı
        // panelden kaydedip doğrulattı (Verified). İkincisinin ekranı hâlâ
        // "Aç" gösteriyor. Eşzamanlılık jetonu bunu YAKALAMAZ: aradaki
        // yazımlar bittiği için tek yazan biziz, jeton eşleşir, CAS geçer —
        // ve canlı bir Verified kurulum Failed'a düşer. Failed marka
        // çözemediği için o yayıncının SMS'i sessizce durur, üstelik "aç"
        // yolu kampanya duraklatmadığı için rezerve krediler asılı kalır.
        var (accountId, _) = await SeedAccountAsync(NetgsmAccountStatus.Verified);

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await PostAsync(client, "Enable", accountId);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Verified,
                "\"aç\" yalnız \"kapat\"ın geri alınmasıdır; canlı bir kurulumu "
                + "Failed'a düşürmek gönderimi sessizce keserdi");

        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountEnable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(0, "gerçekleşmemiş bir karar denetime yazılmamalı");
    }

    [Fact]
    public async Task Kapatma_retry_tukenirse_500_degil_hata_mesaji_doner()
    {
        var (accountId, licenseId) = await SeedAccountAsync();
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await SeedCampaignAsync(db, licenseId, "pending");
        }

        // Kancayı istemci KURULDUKTAN sonra kur: CreateLoggedInAdminClientAsync
        // kendi SaveChanges'ini atıyor (admin tohumu) ve onu da çakıştırırsak
        // test kurulumu düşer.
        var client = await _factory.CreateLoggedInAdminClientAsync();

        // Her kaydetme denemesinden HEMEN ÖNCE hesabı dışarıdan yaz: servis
        // taze okuduğu satırı kaydetmeye çalıştığında jeton artık eskimiş
        // olur. Dört tur da böyle düşünce servis DbUpdateConcurrencyException
        // fırlatır. Yakalanmazsa yönetici 500 görür ve kapatmanın olup
        // olmadığını bilemez — anahtarın en çok gerektiği an tam da budur.
        _factory.Hook.BeforeSave = async () =>
        {
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await rdb.NetgsmAccounts.SingleAsync(a => a.Id == accountId);
            acc.LastError = $"rakip-{Guid.NewGuid():N}";
            await rdb.SaveChangesAsync();
        };

        try
        {
            var resp = await PostAsync(client, "Disable", accountId);

            resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "tükenme yöneticiye 500 değil, yeniden denenebilir bir mesaj olarak dönmeli");
        }
        finally
        {
            _factory.Hook.Reset();
        }

        // Yönlendirmeyi TAKİP ET. "302 döndü" tek başına bir şey kanıtlamaz:
        // başarı yolu da 302 dönüyor. Yöneticinin ekranında kapatmanın
        // OLMADIĞI yazmazsa, yönlendirmeyi başarı sanıp hesabın hâlâ açık
        // olduğunu fark etmez — ki bu tam olarak 500'den kaçınarak
        // engellemeye çalıştığımız şey. TempData çerezi istemcide
        // taşındığı için bu GET şeridi görür.
        var page = await (await client.GetAsync("/admin/netgsm"))
            .Content.ReadAsStringAsync();
        page.Should().Contain("alert-danger");
        page.Should().Contain("Tekrar deneyin",
            "hata şeridi kaldırılırsa tükenme sessiz bir başarı gibi görünür");

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Çakışan yazım hesap satırıydı, dolayısıyla UYGULANMADI.
        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Verified);

        // Gerçekleşmemiş bir karar denetime yazılmamalı.
        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountDisable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(0);
    }

    [Fact]
    public async Task Kapatma_retry_tukenirse_izlenen_nesne_birakmaz()
    {
        // Yukarıdaki test tükenmenin HTTP yüzeyini ölçüyor; bu test Görev 8'in
        // `when (attempt >= maxAttempts)` dalındaki ChangeTracker.Clear()'ı
        // ölçüyor. İkisi ayrı olmak zorunda: temizlik servisin KENDİ
        // context'inde olup bitiyor, sayfa testi ise sonucu her zaman AYRI bir
        // scope'ta okuyor ve oradaki tracker zaten boş — yani Clear() silinse
        // bile sayfa testi yeşil kalır.
        //
        // Neden önemli: scoped LicenseDbContext istek boyunca yaşıyor. Yarım
        // yazılmış (Disabled + paused) izlenen kopyalar orada kalırsa, aynı
        // istekte sonradan atılacak HERHANGİ bir SaveChanges — denetim kaydı,
        // TempData'yı yazan bir filtre, sonraki bir handler — onları da
        // diske indirir. O zaman "kapatma başarısız" mesajını gösterirken
        // kapatmayı sessizce UYGULAMIŞ oluruz: yönetici tekrar dener, hiçbir
        // şey değişmemiş görünür, hesap ise çoktan kapanmıştır.
        var (accountId, licenseId) = await SeedAccountAsync();
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await SeedCampaignAsync(db, licenseId, "pending");
        }

        using var scope = _factory.Services.CreateScope();
        var sdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        _factory.Hook.BeforeSave = async () =>
        {
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await rdb.NetgsmAccounts.SingleAsync(a => a.Id == accountId);
            acc.LastError = $"rakip-{Guid.NewGuid():N}";
            await rdb.SaveChangesAsync();
        };

        try
        {
            var act = async () => await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled,
                "Yönetici tarafından kapatıldı.", CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
                "dört tur da çakıştı; servis sessizce başarı raporlamamalı");
        }
        finally
        {
            _factory.Hook.Reset();
        }

        sdb.ChangeTracker.Entries().Should().BeEmpty(
            "fırlatmadan önce temizlenmezse, bu context'te sonradan atılacak "
            + "herhangi bir SaveChanges yarım kapatmayı diske indirir");
    }
}
```

> **`Kapatma_retry_tukenirse...` neden InMemory'de geçerli.** EF InMemory
> eşzamanlılık jetonlarını UYGULUYOR (sağlayıcı, `SaveChanges` sırasında
> jeton özelliklerini depodaki değerle karşılaştırıp uyuşmazlıkta
> `DbUpdateConcurrencyException` atar). InMemory'nin YAPAMADIĞI şey varlıklar
> arası işlemsel geri alma, tekil indeks ve check constraint — bu test
> bunların hiçbirine dayanmıyor, tek bir satırın jetonuna dayanıyor.
> (`CLAUDE.md:57-59` "InMemory'de eşzamanlılık semantiği yok" diyor; bu ifade
> ilişkisel yarışlar için doğru, jeton için değil. Jeton gerektiren Görev 11
> testleri yine de Testcontainers'ta — orada mesele iki **bağlantının** aynı
> satıra gerçekten aynı anda yazması.)
>
> **Kampanyanın `paused` olup olmadığı BİLEREK doğrulanmıyor.** InMemory'de
> varlıklar arası işlemsel geri alma yok: hesap yazımı jetona takılıp
> fırlatırken kampanya yazımı uygulanmış olabilir. Gerçek SQL Server'da
> ikisi tek işlemde geri alınır. Bu testin kanıtlamak istediği şey zaten
> kampanya değil, **tükenmenin 500 üretmemesi**.
>
> **Neden iki ayrı test.** Tükenmenin iki farklı sözleşmesi var ve tek test
> ikisini birden kilitleyemiyor:
> - `Kapatma_retry_tukenirse_500_degil_hata_mesaji_doner` — HTTP yüzeyi:
>   302 + yönlendirme sonrası ekranda `alert-danger` şeridi. Şerit
>   doğrulanmadan yalnız 302'ye bakmak boş bir iddiadır, çünkü **başarı yolu
>   da 302 dönüyor**; `TempData["Error"]` satırı silinse test yeşil kalırdı.
> - `Kapatma_retry_tukenirse_izlenen_nesne_birakmaz` — servisin kendi
>   context'i: istisna sonrası `ChangeTracker` boş. Sayfa testi bunu
>   ölçemez, çünkü sonucu her zaman AYRI bir scope'tan okuyor ve oradaki
>   tracker `Clear()` olsa da olmasa da boştur.

- [ ] **Adım 2: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~AdminNetgsmPageTests
```
Beklenen: derleme hatası — `AuditEvents.NetgsmAccountDisable` yok.

> **Adım 3-4'ten sonra bu testlerden ikisi HÂLÂ düşmeli.** Sayfa yazıldığında
> `Acma_Disabled_olmayan_hesaba_dokunmaz` (durum kapısı yoksa 409 yerine 302
> gelir ve hesap `Failed` olur) ve
> `Kapatma_retry_tukenirse_500_degil_hata_mesaji_doner` (tükenme dalı yoksa
> istisna dışarı sızar, 500 gelir) ancak Adım 4'teki kapı + tükenme dalıyla
> yeşile döner. Bu iki test, aynı görevin iki ayrı kırmızı-yeşil turudur.
>
> Testin aradığı `alert-danger` şeridini Adım 5 BASMIYOR — `_AdminLayout`ın
> `_ToastPartial`ı zaten basıyor. Yani o iddia `TempData["Error"]` satırını
> (Adım 4) kilitliyor, sayfayı değil.
>
> **`Kapatma_retry_tukenirse_izlenen_nesne_birakmaz` ise kırmızı BAŞLAMAZ** —
> dosya derlenir derlenmez geçer. Ölçtüğü `ChangeTracker.Clear()` + `throw`
> dalı Görev 8'de yazıldı; bu görev onu yalnız HTTP yüzeyinde karşılıyor.
> Testi buraya koymamızın nedeni teknik: kancayı veren `HookedApiFactory` bu
> görevin Adım 0'ında doğuyor, Görev 8'in testleri düz `ApiFactory` kullanıyor
> ve dört turluk çakışmayı kuramıyor. Yani bu bir **gerileme koruması**,
> yeni davranışın kanıtı değil — ve tam da bu yüzden gerekli: Görev 8'deki
> `Clear()` satırının bugün hiçbir testi yok.

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
using OrderDeck.LicenseServer.Services.Sms;

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
    private readonly NetgsmAccountService _accounts;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, NetgsmAccountService accounts, IAuditService audit)
    {
        _db = db;
        _accounts = accounts;
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
        // AsNoTracking bilinçli: asıl yazımı servis yapıyor ve çakışmada
        // ChangeTracker'ı temizliyor. Burada izlenen bir kopya tutarsak o
        // temizlik onu da kopartır ve elimizde yarı-geçerli bir nesne kalır.
        var licenseId = await _db.NetgsmAccounts.AsNoTracking()
            .Where(a => a.Id == AccountId)
            .Select(a => (Guid?)a.LicenseId)
            .FirstOrDefaultAsync(ct);
        if (licenseId is null) return NotFound();

        // Kapatma + kampanya duraklatma TEK yerde: Görev 8'in kesin-ret dalı
        // da aynı metodu çağırıyor. İki kopya olsaydı biri ileride "sending"i
        // ya da retry'ı unutur, kapatma sessizce yarım kalırdı. Metot
        // "pending" VE "sending" kampanyaları hesabın yazımıyla aynı
        // SaveChanges'te duraklatır; "pending" bırakmak yetmezdi, çünkü
        // SmsCampaignRecoveryJob iki dakika sonra onu kuyruğa alıp kapatma
        // kararını sessizce geri alırdı.
        int pausedCampaigns;
        try
        {
            pausedCampaigns = await _accounts.CloseAccountAndPauseCampaignsAsync(
                AccountId, NetgsmAccountStatus.Disabled,
                "Yönetici tarafından kapatıldı.", ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Retry döngüsü dört turda da çakıştı (Görev 8). Yönetici kararı
            // bilinçli olarak jetonsuz, yani "kaybetmez" — ama sonsuz da
            // denemez. Buraya düşmek satırın o anda başka bir yazarla
            // dövüştüğü anlamına gelir; yakalamazsak yöneticiye 500 gider ve
            // kapatmanın gerçekleşip gerçekleşmediğini bilmez. Servis
            // fırlatmadan önce ChangeTracker'ı temizliyor, bu scope'ta
            // yarı-yazılmış bir nesne kalmıyor.
            TempData["Error"] =
                "Kurulum şu anda başka bir işlemle güncelleniyor. Tekrar deneyin.";
            return RedirectToPage();
        }

        await _audit.LogAsync(
            AuditEvents.NetgsmAccountDisable, AuditTargets.NetgsmAccount,
            AccountId.ToString(),
            new { licenseId, pausedCampaigns }, ct);

        TempData["Success"] = pausedCampaigns == 0
            ? "Kurulum kapatıldı."
            : $"Kurulum kapatıldı, {pausedCampaigns} kampanya duraklatıldı.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct)
    {
        var acc = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == AccountId, ct);
        if (acc is null) return NotFound();

        // "Aç" YALNIZ "Kapat"ın geri alınmasıdır. Aşağıdaki CAS eşzamanlılığı
        // koruyor ama bayat SAYFAYI korumuyor: iki yönetici listeyi hesap
        // Disabled'ken açar, biri açar, kurulum panelden doğrulanıp Verified
        // olur, sonra ikincisi hâlâ "Aç" düğmesini gösteren ekranından
        // basarsa TEK yazım olur, jeton eşleşir, CAS geçer — ve canlı bir
        // Verified kurulum Failed'a düşer. Failed marka çözemediği için o
        // yayıncının gönderimi sessizce durur, üstelik kampanyalar
        // duraklatılmadığı için rezerve krediler asılı kalır. Durum kapısı
        // bunu kapatıyor: Failed ya da Verified bir hesapta "aç" anlamsız.
        if (acc.Status != NetgsmAccountStatus.Disabled)
        {
            _db.ChangeTracker.Clear();
            return new ConflictObjectResult(new
            {
                title = "netgsm-account-not-disabled",
                detail = "Kurulum zaten açık. Sayfayı yenileyin.",
            });
        }

        // Verified DEĞİL: yönetici markanın İYS'de hâlâ geçerli olduğunu
        // bilemez. Doğrulama tek yoldan — panel kaydı ya da günlük iş — geçer.
        // Duraklatılmış kampanyalar da burada devam ettirilmez; devam,
        // doğrulamanın başarılı olduğu anda gelir.
        acc.Status = NetgsmAccountStatus.Failed;
        acc.LastError = null;
        // Damgayı LicenseDbContext merkezî olarak atıyor (Görev 3); burada
        // elle UtcNow yazmak, saat ilerlemediğinde jetonu yerinde bırakırdı.
        _db.Entry(acc).Property(a => a.UpdatedAt).IsModified = true;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Yönetici sayfayı açtıktan sonra biri hesabı yazmış: yayıncı
            // panelden kaydetmiş ya da günlük iş sonuç yazmış olabilir.
            // Ekrandaki "Disabled" artık gerçeği göstermiyor, dolayısıyla
            // "aç" kararı da bayat — sessizce uygulamak yerine yöneticiye
            // güncel hâli gösteriyoruz.
            _db.ChangeTracker.Clear();
            return new ConflictObjectResult(new
            {
                title = "netgsm-account-changed",
                detail = "Kurulum başka bir işlemle değişti. Sayfayı yenileyin.",
            });
        }

        await _audit.LogAsync(
            AuditEvents.NetgsmAccountEnable, AuditTargets.NetgsmAccount,
            acc.Id.ToString(), new { licenseId = acc.LicenseId }, ct);

        TempData["Success"] = "Kurulum açıldı — doğrulama bekleniyor.";
        return RedirectToPage();
    }
}
```

> **İki yolun `catch`'i neden FARKLI.** "Aç" bayat bir karardır: yönetici
> sayfayı açtığında gördüğü durum artık geçerli değilse kararı da geçerli
> değildir → 409, yenile. "Kapat" bayat DEĞİLDİR: hesap hangi durumda olursa
> olsun yönetici onu kapatmak istiyor, bu yüzden `CloseAccountAndPauseCampaignsAsync`
> çağrısı `expectedUpdatedAt` TAŞIMIYOR (Görev 8) ve dört turluk retry
> döngüsüyle ısrar ediyor — anahtarın en çok gerektiği an, kampanyanın aktığı
> ve satırın en çok yazıldığı andır.
>
> Ama **"ısrar eder" ≠ "hiç düşmez".** Dördüncü tur da çakışırsa servis
> `DbUpdateConcurrencyException`'ı dışarı bırakır; yukarıdaki `catch` onu
> 500'e dönüşmeden yakalayıp yöneticiye "tekrar deneyin" diyor. Tekrar
> denemek güvenli: kapatma idempotent (zaten `Disabled` bir hesabı yeniden
> `Disabled` yazmak, duraklatılacak kampanya bırakmadığı için `0` döner).

> **Tükenmenin `_db.ChangeTracker.Clear()` + `throw` dalı Görev 8'de YAZILDI**
> (`CloseAccountAndPauseCampaignsAsync`, `when (attempt >= maxAttempts)`
> süzgeçli `catch`). Burada ikinci bir kopyası yok; bu görev yalnız o
> istisnayı HTTP yüzeyinde karşılıyor. Görev 8'in kendi testleri etkilenmez —
> hiçbiri dört tur çakışma kurgulamıyor, dolayısıyla hiçbiri o dala girmiyor.

- [ ] **Adım 5: Sayfayı yaz**

`OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml`:

```cshtml
@page "/admin/netgsm"
@model IndexModel
@{
    ViewData["Title"] = "Netgsm Kurulumları";
}
@* TempData["Success"] / TempData["Error"] şeritleri BU SAYFADA basılmaz:
   Pages/Admin/_ViewStart.cshtml `_AdminLayout`ı seçiyor, o da
   `_ToastPartial`ı çağırıyor (_AdminLayout.cshtml:36) ve partial iki şeridi
   de zaten gösteriyor (_ToastPartial.cshtml:1-14). Burada ikinci bir kopya
   yazmak mesajı ÇİFT bastırır — TempData aynı istek içinde tekrar okunabilir,
   silinme istek sonunda olur. Komşu Pages/Admin/Iys/Index.cshtml de aynı
   şekilde partial'a güveniyor. *@
<h1 class="h3 mb-4">Netgsm / İYS Kurulumları</h1>

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
Beklenen: PASS (`AdminNetgsmPageTests`'in 7 testi + mevcut admin yetkilendirme
paketi).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Pages/Admin/AdminNetgsmPageTests.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/SaveHookInterceptor.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/HookedApiFactory.cs \
        OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Netgsm/Index.cshtml.cs \
        OrderDeck.LicenseServer/Services/Audit/AuditEvents.cs
git commit -m "$(cat <<'EOF'
feat(admin): Netgsm kurulum kapatma anahtarı

Kapatma hesabı Disabled yapar ve o lisansın pending/sending kampanyalarını
duraklatır — pending bırakılsaydı kurtarma işi kararı iki dakikada geri alırdı.
Açma Verified değil Failed yazar: doğrulama normal akıştan geçer, ve yalnız
Disabled bir hesapta çalışır — bayat bir liste sayfasından basılan "aç",
canlı bir kurulumu Failed'a düşürüp gönderimi sessizce kesiyordu.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 13: Devam ettirme + DI/cron bağlantıları

Son halka: doğrulama geri geldiğinde duraklatılmış kampanyalar devam etmeli,
ve yeni sınıflar uygulamaya bağlanmalı.

**Devam ettirme kuyruğa ATMAZ**, yalnız `paused → pending` yazar ve
`ClaimedAt` jetonunu bir kademe ilerletir (temizlemez — gerekçe Adım 3'te).
Kuyruğa atmayı `SmsCampaignRecoveryJob` yapar (5 dakikada
bir, 2 dakikalık `PendingGrace`). Gerekçe `Program.cs`'te İYS push işi için zaten
yazılı: kaçırılan bir `Enqueue` kaydı sessizce kaybeder, süpürme kaybetmez.
Burada da aynı: kapatma/açma nadir bir olay, 5 dakikalık gecikmenin bedeli yok;
buna karşılık "yazdım ama enqueue çökmüştü" sınıfı bir kayıp hiç doğmuyor.

Devam ettirme `Failed → Verified` geçişinde çağrılır — **tek yer**: panel `PUT`'u
(Görev 5). Gerekçesi Adım 5'te.

> **Devam ettirme kendi `SaveChanges`'ini ÇAĞIRMAZ.** Kapatma yolunun aynası
> olmalı: Görev 8/12'deki `CloseAccountAndPauseCampaignsAsync` hesabı ve
> kampanyaları tek `SaveChanges`'te yazıyor. Açma yolu iki ayrı kayda
> bölünürse aradaki çökme kampanyaları `paused`'da bırakır — hesap `Verified`,
> kampanya ölü, rezerve kredi asılı ve hiçbir süpürme bunu düzeltmez
> (`SmsCampaignRecoveryJob` `paused`'a bakmıyor, Görev 11). Bu yüzden metot
> yalnız nesneleri **hazırlar**; kaydı, hesabı `Verified` yazan çağıran yapar.

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/NetgsmAccountService.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelNetgsmAccountController.cs`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Index.cshtml`
- Modify: `OrderDeck.LicenseServer/Program.cs:908` civarı (cron; DI kayıtları
  Görev 5 ve Görev 8'de yapıldı, burada yalnız doğrulanıyor)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmAccountResumeTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs`
  (Adım 5b — devam ettirme yarışı; Görev 11'de yazılan sınıfa tek test eklenir
  ve fixture tipi Görev 12'nin `HookedApiFactory`'sine çevrilir)

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
    public async Task Devam_ettirme_paused_kampanyayi_pending_yapar_ve_jetonu_ilerletir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, pausedId, completedId) = await SeedAsync(db);

        var previous = (await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == pausedId)).ClaimedAt!.Value;

        // Metot kaydetmiyor — çağıranın SaveChanges'ine biniyor. Gerçek
        // çağıran (panel PUT'u) hesabın Verified yazımıyla aynı kayıtta
        // birleştiriyor; test o rolü üstleniyor.
        var resumed = await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None);
        await db.SaveChangesAsync();

        resumed.Should().Be(1);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var paused = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == pausedId);
        paused.Status.Should().Be("pending");
        paused.ClaimedAt.Should().NotBeNull();
        paused.ClaimedAt!.Value.Should().BeAfter(previous,
            "jeton monoton artmalı; null'a çekmek zinciri koparır ve "
            + "duraklatmada bir tick ileri itilmiş damganın GERİSİNDE "
            + "bir değer üretebilir");
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

        await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None);
        await db.SaveChangesAsync();

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

        (await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None))
            .Should().Be(0);
    }
}
```

- [ ] **Adım 2: Testi koş, düştüğünü gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~NetgsmAccountResumeTests
```
Beklenen: derleme hatası — `StageResumePausedCampaignsAsync` yok.

- [ ] **Adım 3: Devam ettirmeyi yaz**

`NetgsmAccountService.cs` — `CloseAccountAndPauseCampaignsAsync`'in (Görev 8)
altına ekle:

```csharp
    /// <summary>
    /// Kurulum yeniden doğrulandığında duraklatılmış kampanyaları devam
    /// ettirmeye HAZIRLAR: bellekteki nesneleri "pending" yapar ve devam
    /// ettirilecek kampanya sayısını döndürür. Geriye kaç kampanyanın
    /// hazırlandığını döndürür.
    ///
    /// <para><b>KAYDETMEZ.</b> Çağıran, hesabı <c>Verified</c> yazan
    /// <c>SaveChanges</c>'in içine alır — kapatma yolunun
    /// (<see cref="CloseAccountAndPauseCampaignsAsync"/>) atomikliğinin
    /// aynası. Ayrı kaydedilseydi aradaki çökme hesabı Verified, kampanyaları
    /// "paused" bırakırdı; <see cref="SmsCampaignRecoveryJob"/> "paused"a
    /// bakmadığı için o kampanyalar rezerve kredileriyle birlikte sonsuza dek
    /// asılı kalırdı.</para>
    ///
    /// <para><b>Kuyruğa da ATMAZ.</b> Yalnız "pending" yazılır;
    /// <see cref="SmsCampaignRecoveryJob"/> 5 dakikada bir süpürüp kuyruğa
    /// alır. Gerekçe İYS push işiyle aynı: kaçırılan bir Enqueue kaydı sessizce
    /// kaybeder, süpürme kaybetmez. Kapatma/açma nadir bir olay, 5 dakikalık
    /// gecikmenin ölçülebilir bir bedeli yok.</para>
    ///
    /// <para><b><c>ClaimedAt</c> TEMİZLENMEZ, ilerletilir.</b> Jetonun tek işi
    /// monoton artmak: "bu satırı en son kim yazdı" sorusunun cevabı o.
    /// <c>null</c>'a çekmek zinciri koparır — sonraki üstlenme
    /// <c>NextClaimedAt(null) = UtcNow</c> üretir ve bu değer, duraklatma
    /// sırasında bir tick ileri itilmiş eski damganın GERİSİNDE kalabilir.
    /// O an <c>sending/L → paused/L+1 → pending/null → sending/L</c> dizisi
    /// mümkün olur ve duraklatmadan önce okumuş bir işçi kendi jetonunu
    /// yeniden görüp hem sahiplik yoklamasından hem CAS'tan geçer.</para>
    ///
    /// <para><c>null</c> gerekmiyor da: <see cref="SmsCampaignRecoveryJob"/>'ın
    /// <c>pending</c> dalı <c>CreatedAt</c>'e bakıyor
    /// (<c>SmsCampaignRecoveryJob.cs:51</c>), <c>SmsCampaignSendJob</c>'ın
    /// üstlenme kapısı da <c>pending</c> için <c>ClaimedAt</c>'e hiç bakmıyor
    /// (<c>SmsCampaignSendJob.cs:66-74</c>). "Devralınan koşu" uyarısı da
    /// tetiklenmez: <c>resumed</c> yalnız <c>Status == "sending"</c> iken
    /// doğru olur.</para>
    /// </summary>
    public async Task<int> StageResumePausedCampaignsAsync(
        Guid licenseId, CancellationToken ct = default)
    {
        var paused = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId && c.Status == "paused")
            .ToListAsync(ct);

        foreach (var c in paused)
        {
            c.Status = "pending";
            c.ClaimedAt = NextClaimedAt(c.ClaimedAt);
        }
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

`PanelNetgsmAccountController.cs` — bu **saf bir EKLEME**. Görev 5'te yazdığın
`PUT` gövdesinde zaten duran

```csharp
            if (result.Outcome == NetgsmVerifyOutcome.Ok)
            {
                account.Status = NetgsmAccountStatus.Verified;
                account.LastVerifiedAt = DateTimeOffset.UtcNow;
                account.LastError = null;
            }
```

bloğunun **içine, `account.LastError = null;` satırının hemen altına** şu çağrıyı
ekle — blok dışında hiçbir şeye dokunma (blok `try` içinde, girinti 12 boşluk):

```csharp
                // Kurulum geri geldi: admin kapatmasıyla duraklatılmış
                // kampanyalar devam etsin. Kayıt AŞAĞIDAKİ tek SaveChanges'te —
                // hesabın Verified'ı ile kampanyaların pending'i ya birlikte
                // iner ya hiç inmez. Ayrılsalardı aradaki çökme kampanyaları
                // paused'da bırakırdı ve hiçbir süpürme onları bulmazdı.
                await _accounts.StageResumePausedCampaignsAsync(account.LicenseId, ct);
```

> **Bu bloğun dışındaki HİÇBİR şeyi değiştirme.** Özellikle şunlar Görev 5 ve
> Görev 6'nın yazdığı hâlde KALIR:
> - `else { account.LastError = result.Message; }` dalı,
> - `_db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;` satırı
>   — burada `account.UpdatedAt = DateTimeOffset.UtcNow;` yazmak Görev 3'ün
>   merkezî monotonik damgalamasını devre dışı bırakır ve jetonun geri
>   gitmesine izin verir,
> - `await _db.SaveChangesAsync(ct);` ve `return Ok(ToView(account));`,
> - **üç catch bloğunun üçü de**: Görev 5'in `DbUpdateConcurrencyException`'ı
>   (→409 `verification-superseded`), Görev 6'nın
>   `NetgsmAccountDisabledException`'ı (→409 `netgsm-account-disabled`) ve
>   `DbUpdateException ex when IsBrandCodeConflict(ex)`'i (→409
>   `brand-code-taken`).
>
> Bu gövdeyi baştan yazarsan catch'ler düşer ve marka çakışması ile doğrulama
> yarışı 409 yerine **500** döner — Görev 6'nın `Baska_lisansin_dogrulanmis_markasi_409`
> ve Görev 5'in `Dogrulama_sirasinda_kimlikler_degisirse_sonuc_UYGULANMAZ` /
> `Dogrulama_sirasinda_yalniz_parola_degisirse_sonuc_uygulanmaz` testleri
> kırmızıya döner.
> Görev 5'in blok alıntısı bunu zaten söylüyor: *"Görev 6 ve Görev 13 bu
> gövdeye EKLEME yapar, parça DEĞİŞTİRMEZ."*

> **Değişken adı `account`, `acc` değil.** Bu metodun yerelini Görev 5 öyle
> adlandırdı; `acc` yalnız testlerde ve admin sayfasında geçiyor.

- [ ] **Adım 5b: Devam ettirme yarışının düşen testini yaz**

Görev 11'de döngü içi sahiplik yoklamasının (`current.ClaimedAt !=
campaign.ClaimedAt`) adanmış testi yoktu; o karşılaştırma **tam olarak bu
adımda eklenen yol için** var. Senaryo: işçi A kampanyayı üstlenmiş ve ilk
alıcıya göndermişken admin hesabı kapatıyor (kampanya `paused`), sonra yayıncı
kimlikleri düzeltip `PUT`'u çağırıyor (kampanya `pending`), kurtarma süpürmesi
onu yeni bir işçiye veriyor (kampanya yine `sending`, **ama yeni `ClaimedAt`**).
İşçi A hâlâ döngüsünde. `Status` yoklaması tek başına bakarsa durum yine
`"sending"` göründüğü için A devam eder ve **aynı kişiye ikinci SMS gider** —
para da harcanır, hukuken de ikinci ticari ileti olur. Jeton karşılaştırması
bunu görür.

**Araya girme ANI kritik — `OnSent` YANLIŞ nokta.** `RecordingSmsSender.OnSent`,
`SendAsync`'in *içinde*, yani alıcı 1'in sonucu daha kaydedilmeden koşar. Oraya
konursa `ClaimedAt` sıçraması alıcı 1'in kendi kaydını
(`SaveRecipientResultAsync`) çakıştırır, metot `false` döner ve `RunAsync`
**döngünün ikinci turuna hiç girmeden** çıkar. Sonuç `Sent == 1` olur — ama
yoklama sayesinde değil, kaydetme çakışması sayesinde. Mutasyon o yolu
etkilemediği için test yeşil kalır ve hiçbir şey kanıtlamaz.

Doğru nokta **alıcı 1'in kaydından hemen SONRASI**: Görev 12'de yazılan
`SaveHookInterceptor.AfterSave`. Kancayı `OnSent`'in içinde kuruyoruz, çünkü
`OnSent` koştuğu anda üstlenme kaydı çoktan inmiştir — dolayısıyla "bir
sonraki `SavedChangesAsync`" tam olarak alıcı 1'in kaydıdır.

Önce **fixture tipini değiştir** (Görev 11'de yazılan sınıfın üç satırı):

```csharp
public sealed class SmsCampaignPauseTests : IClassFixture<HookedApiFactory>
{
    private readonly HookedApiFactory _factory;

    public SmsCampaignPauseTests(HookedApiFactory factory)
    {
        _factory = factory;
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        _factory.Sms.OnSent = null;
        _factory.Hook.Reset();
    }
```

> Sınıfın gövdesinde başka hiçbir şey değişmiyor: `HookedApiFactory`,
> `ApiFactory`'den türüyor ve kanca boşken davranışı birebir aynı, dolayısıyla
> Görev 11'in testleri aynen geçer.

Sonra sınıfın sonuna ekle:

```csharp
    [Fact]
    public async Task Devam_ettirilip_yeniden_ustlenilen_kampanyaya_eski_isci_gondermez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        // OnSent alıcı 1'in SendAsync'i içinde koşar; oradan yalnız KANCAYI
        // kuruyoruz. Araya girme, alıcı 1'in sonucu diske indikten sonra
        // çalışsın ki işçi A gerçekten döngünün ikinci turuna girsin —
        // test etmek istediğimiz yoklama orada.
        _factory.Sms.OnSent = _ =>
        {
            _factory.Sms.OnSent = null;
            _factory.Hook.AfterSave = InterleaveAsync;
        };

        // Kapat → devam ettir → BAŞKA bir işçi üstlensin. Üçü de ayrı
        // scope'ta: işçi A'nın DbContext'i hiçbirini görmüyor, elindeki
        // `campaign` nesnesi bayatlıyor.
        async Task InterleaveAsync()
        {
            _factory.Hook.AfterSave = null;

            using var other = _factory.Services.CreateScope();
            var odb = other.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = other.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            var licenseId = (await odb.NetgsmAccounts.AsNoTracking()
                .SingleAsync(a => a.Id == accountId)).LicenseId;

            // 1) Admin kapatması — kampanya paused, ClaimedAt ileri damgalanır.
            await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled, "Yönetici kapattı.");

            // 2) Yayıncı kimlikleri düzeltti, PUT doğrulandı — paused → pending.
            await accounts.StageResumePausedCampaignsAsync(licenseId);
            await odb.SaveChangesAsync();

            // 3) Kurtarma süpürmesi yeni bir işçiye verdi: taze ClaimedAt.
            var c = await odb.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
            c.Status = "sending";
            c.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await odb.SaveChangesAsync();
        }

        try { await job.RunAsync(campaignId); }
        finally
        {
            _factory.Sms.OnSent = null;
            _factory.Hook.Reset();
        }

        _factory.Sms.Sent.Should().HaveCount(1,
            "işçi A sahipliğini kaybetti; ikinci alıcı artık YENİ işçinin işi. "
            + "2 olursa aynı kişiye iki ticari ileti gitmiş demektir");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("sending", "yeni sahibin durumu ezilmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0,
            "sahipliği kaybeden işçi iade YAPMAZ — kalan alıcı yeni işçide");

        // Alıcı 1'in sonucu KAYBOLMAMALI: SMS gerçekten gitti, kaydı da inmiş
        // olmalı. Araya girme `OnSent`'in içinde yapılsaydı bu satır YİNE
        // geçerdi — `SaveRecipientResultAsync` çakışmada kampanyayı detach
        // edip yeniden kaydediyor, yani alıcı satırı her hâlükârda iniyor.
        // Fark davranışta değil, KAPSAMDA: `OnSent` ile sahiplik kaybı alıcı
        // 1'in yazımında yakalanır, koşu oracıkta `return` eder ve döngü
        // ikinci tura HİÇ girmez — yani Adım 5c'nin öldürmek istediği
        // `current.ClaimedAt` karşılaştırmasına sıra gelmez. `AfterSave` ile
        // alıcı 1 temiz kapanır, işçi A ikinci tura girer ve tek kapı o
        // karşılaştırma olur.
        (await vdb.SmsCampaignRecipients.AsNoTracking()
            .CountAsync(r => r.CampaignId == campaignId && r.Status == "sent"))
            .Should().Be(1);
    }
```

> **Kanca neden `AfterSave`, `BeforeSave` değil?** `BeforeSave` olsaydı araya
> girme, alıcı 1'in yazımı diske inmeden koşar ve o yazımı çakıştırırdı —
> `OnSent` ile aynı hataya düşerdik. `AfterSave` yalnız BAŞARILI yazımdan
> sonra koşar, yani alıcı 1'in sonucu güvende, işçi A'nın `campaign.ClaimedAt`
> değeri de artık kesinleşmiş: karşılaştırmanın anlamlı olması için gereken
> tam durum.
>
> **Kilitlenme yok:** kanca `async Task`, `.GetAwaiter().GetResult()` yok.
> Interceptor'ın `_running` bayrağı, kanca içindeki üç `SaveChanges`'in
> kancayı yeniden tetiklemesini engelliyor (alan zaten ilk satırda
> `null`'lanıyor — iki katmanlı koruma bilinçli, biri kaldırılırsa diğeri
> testi flaky değil ölü kılsın).

- [ ] **Adım 5c: Testin gerçekten kırmızı başladığını mutasyonla doğrula**

Test şu anda GEÇİYOR olmalı (Görev 11 yoklamayı zaten yazdı). Kırmızı
başladığını kanıtlamak için `SmsCampaignSendJob`'ın döngü içi yoklamasından

```csharp
            if (current.Status != "sending" || current.ClaimedAt != campaign.ClaimedAt)
```

`|| current.ClaimedAt != campaign.ClaimedAt` kısmını **geçici olarak** sil:

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter FullyQualifiedName~Devam_ettirilip_yeniden_ustlenilen_kampanyaya_eski_isci_gondermez
```
Beklenen: **FAIL** — `Sent` 1 değil 2; eski işçi ikinci alıcıya da göndermiş.

Mutasyon bu testi gerçekten düşürür, çünkü araya girme alıcı 1'in kaydından
SONRA koşuyor: işçi A döngünün ikinci turuna **giriyor** ve yoklamaya
çarpıyor. Kalan tek kapı `current.Status`, o da devam ettirme+yeniden üstlenme
sonrası yine `"sending"` — yani jeton karşılaştırması olmadan hiçbir şey
durdurmuyor.

Mutasyonu geri al (silinen `||` parçasını yerine yaz) ve aynı komutu tekrar
koş. Beklenen: PASS.

> **`git checkout --` ile geri alma.** Bu dosya Görev 11'de commit edildiği
> için `git checkout -- OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs`
> güvenlidir — ama bu görevde o dosyada BAŞKA bir değişiklik yapmadığından emin
> ol, yoksa onu da siler. Şüphedeysen satırı elle geri yaz.

**Neden `_accounts` alanı var:** Görev 4'te controller'a `NetgsmAccountService`
zaten enjekte edildi (`_accounts.TryUnprotectPassword` çağrısı orada). Yeni bir
bağımlılık eklemiyorsun.

- [ ] **Adım 6: DI kayıtlarını DOĞRULA (yeni kayıt ekleme)**

Her iki kayıt da daha önce, ilk ihtiyaç duyulan görevde eklendi:
`NetgsmAccountVerifier` Görev 5'te (panel `PUT`'u onu enjekte ediyor),
`NetgsmAccountVerifyJob` Görev 8'de. Burada yalnız **ikisinin de bir kez**
kayıtlı olduğunu doğrula — çift `AddScoped` sessizce derlenir ve son kayıt
kazanır, yani hata ancak ileride biri değişince ortaya çıkar.

```bash
grep -n "NetgsmAccountVerifier\|NetgsmAccountVerifyJob\|NetgsmAccountService" \
  OrderDeck.LicenseServer/Program.cs
```

Beklenen: `Program.cs:195` civarında, `NetgsmAccountService` kaydının hemen
altında **tam olarak** şu üç satır (her biri bir kez):

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountService>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifier>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Sms.NetgsmAccountVerifyJob>();
```

(`NetgsmAccountService` satırı Plan 1'den beri var; ikisi yeni.) Bir ad iki kez
çıkıyorsa fazlasını sil; biri hiç çıkmıyorsa buraya ekle. Ayrıca `AddScoped`
satırlarından biri `AddSingleton`/`AddTransient` yazıyorsa düzelt: üçü de
`LicenseDbContext`'e bağlı, yani **scoped olmak zorunda**.

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
        OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignPauseTests.cs \
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
