# SMS Kredi Emekliliği + Kiracı Gönderim Yolu (Plan 3) — Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dahili SMS kredi sistemini emekliye ayır, ticari kampanya gönderimini yayıncının kendi `NetgsmAccount` kimlikleriyle yapan `ITenantSmsSender` yolunu aç ve §3.4'ün üç sınıflı hata modelini uygula.

**Architecture:** Spec `docs/superpowers/specs/2026-09-18-coklu-yayinci-sms-iys-design.md` §1.1, §1.2, §1.4, §3 + §8 sözleşmeleri 4/5/6/7/10/13/17. Merkezi `ISmsSender` yalnız `Transactional` (OTP) taşır ve `Commercial`'da fırlatmaya devam eder; kampanya işi kimlikleri kampanyanın lisansının `NetgsmAccount` satırından çözer. Kredi tabloları/servisi/uçları silinir; **eski WPF istemcileri sahada kaldığı için** `/sms/balance` bir sürüm boyunca sabit değer döndüren stub olarak yaşar ve `Sufficient`/`CreditsRefunded` JSON alanları korunur.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (InMemory testler + Testcontainers SQL), Hangfire, WPF (CommunityToolkit.Mvvm), xUnit.

**Çalışma dizini:** git worktree `C:\Users\burak\source\repos\LiveDeck\.claude\worktrees\kurulum-yasam-dongusu`, dal `feat/sms-kredi-emekliligi-kiraci-gonderim` (taban: `d7519866`, `feat/netgsm-kurulum-yasam-dongusu` ucu — PR #475 merge olana kadar master'a PR AÇILMAZ).

---

## Karara bağlanan spec soruları (plan aşaması kararları)

1. **§1.4b — eski WPF uyumluluğu:** uçlar **bir sürüm boyunca sabit değerle yaşatılır** (silme zorunlu WPF sürümüne bağlanmaz). `GET /sms/balance` → `{creditsRemaining: 0, updatedAt: now}` stub. Preview yanıtında `Sufficient := doğrulanmış NetgsmAccount var mı` (eski istemcinin Gönder düğmesi bu alana bağlı — alan dolmazsa düğme kalıcı kapalı kalırdı). `CreditsRefunded` alanları JSON'da `0` sabitiyle kalır. `TotalCredits` = alıcı × segment hesaplanmaya devam eder (bilgi amaçlı, Netgsm segment başına ücretlendirir).
2. **§3.4 — bilinmeyen hata kodu varsayılanı:** bilinmeyen/sınıflandırılamayan **temiz ret** kodu → **kampanya `paused`** (kitle korunur). Gerekçe: §9.3'te bakiye hatasının kodu doğrulanmamış; bilinmeyen kodu alıcıya `failed` yazmak, bakiye bittiğinde tüm kitleyi tek turda tüketirdi — spec'in "hesap hatası kitleyi harcamamalı" ilkesinin aynısı bakiye için de geçerli.
3. **§3.4 — `jobid`:** saklanır (`SmsCampaignRecipient.ProviderJobId`, yeni sütun). Rapor-aşaması takip adımı (stats/`notEnoughCredit`) **kurulmaz** — §9.3 doğrulanmamış varsayım, ileri iş. Sütun ileride mutabakat için ham veri bırakır.
4. **Belirsiz hata (ağ/timeout, temiz ret DEĞİL):** alıcı `failed`, döngü devam (bugünkü davranış). `pending`'e döndürülemez — SMS gitmiş olabilir, devam ettirmede aynı kişiye ikinci ticari SMS gider (6563). Bilinen sınır: ağ kesintisi kitleyi `failed`'a yazabilir; spec'in üç sınıfı ağı kapsamıyor, kapsam genişletilmedi.
5. **Şifre çözülemezse (sözleşme 10, §2.4):** hesap `Disabled` + `NetgsmAccountService.UndecryptableMessage`, kampanyalar `paused` — mevcut `CloseAccountAndPauseCampaignsAsync` ile.

## Netgsm REST v2 hata kodu sınıflandırması (Görev 1'in tablosu)

| kod | Netgsm anlamı | sınıf |
|---|---|---|
| `30` | geçersiz kullanıcı adı/şifre veya API erişim izni yok | **Account** |
| `40` | gönderici başlığı (msgheader) sistemde tanımsız | **Account** |
| `50` | abone hesabı İYS'li gönderim yapamıyor | **Account** |
| `51` | aboneye ait İYS marka bilgisi bulunamadı | **Account** |
| `70` | hatalı/eksik parametre (alıcı numarası dahil) | **Recipient** |
| `20`, `80`, `85`, diğer/bilinmeyen | mesaj metni / limit aşımı / mükerrer / bilinmeyen (bakiye dahil) | **CampaignPause** |

`NetgsmSmsException` **yalnız temiz ret** demektir: Netgsm'den yanıt alındı ve gönderim kabul EDİLMEDİ → hiçbir şey gitmedi → alıcı `pending`'e güvenle dönebilir. Ağ hatası bu istisnaya sarılMAZ.

---

### Görev 1: `NetgsmSmsException` + hata sınıflandırması

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Sms/NetgsmSmsException.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmSmsExceptionTests.cs`

- [ ] **Step 1: Failing test yaz**

```csharp
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmSmsExceptionTests
{
    [Theory]
    [InlineData("30")]
    [InlineData("40")]
    [InlineData("50")]
    [InlineData("51")]
    public void AccountClass_Codes(string code)
        => Assert.Equal(NetgsmErrorClass.Account, new NetgsmSmsException(code, "x").Classify());

    [Fact]
    public void RecipientClass_Code70()
        => Assert.Equal(NetgsmErrorClass.Recipient, new NetgsmSmsException("70", "x").Classify());

    // §3.4 karar 2: bilinmeyen kod kitleyi HARCAMAZ — varsayılan kampanya duraklatma.
    [Theory]
    [InlineData("20")]
    [InlineData("80")]
    [InlineData("85")]
    [InlineData("999")]
    [InlineData(null)]
    public void UnknownClass_DefaultsToCampaignPause(string? code)
        => Assert.Equal(NetgsmErrorClass.CampaignPause, new NetgsmSmsException(code, "x").Classify());
}
```

- [ ] **Step 2: Testin FAIL ettiğini doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmSmsExceptionTests`
Expected: derleme hatası (`NetgsmSmsException` yok).

- [ ] **Step 3: Implementasyon**

`OrderDeck.LicenseServer/Services/Sms/NetgsmSmsException.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Netgsm hata sınıfı — §3.4'ün üç sınıfı. Sınıf, hatanın KİMİ
/// cezalandıracağını belirler: tek alıcıyı mı, kampanyayı mı, hesabı mı.</summary>
public enum NetgsmErrorClass
{
    /// <summary>Yalnız bu alıcı geçersiz (kod 70) → alıcı <c>failed</c>, döngü devam.</summary>
    Recipient,
    /// <summary>Hesap düzeyinde arıza (şifre/başlık/İYS yetkisi) → kampanya
    /// <c>paused</c> + hesap <c>Failed</c>; kalan alıcılar <c>pending</c> korunur.</summary>
    Account,
    /// <summary>Bakiye/limit/bilinmeyen → kampanya <c>paused</c>, alıcılar
    /// <c>pending</c> korunur. Varsayılan sınıf: bilinmeyen kodu alıcıya
    /// <c>failed</c> yazmak bakiye bittiğinde tüm kitleyi tek turda tüketirdi.</summary>
    CampaignPause,
}

/// <summary>
/// Netgsm'in gönderimi TEMİZ REDDETMESİ: yanıt alındı ve iş kabul edilmedi,
/// yani hiçbir SMS gitmedi. <b>Bu garanti sınıfın sözleşmesidir</b> — çağıran
/// (SmsCampaignSendJob) buna dayanarak alıcıyı <c>pending</c>'e geri döndürür.
/// Ağ hatası / timeout bu istisnaya SARILMAZ: istek Netgsm'e ulaşmış ve mesaj
/// gitmiş olabilir; onlar ham istisna olarak çıkar ve alıcı <c>failed</c> yazılır
/// (belirsizlikte çift ticari SMS riskine karşı fail-closed).
/// </summary>
public sealed class NetgsmSmsException : Exception
{
    /// <summary>Netgsm REST v2 hata kodu ("30", "70"…). Gövdeden kod
    /// çıkarılamadıysa null — null da <see cref="NetgsmErrorClass.CampaignPause"/>.</summary>
    public string? Code { get; }

    public NetgsmSmsException(string? code, string message) : base(message) => Code = code;

    /// <summary>Plan dokümanındaki kod tablosu. 30/40/50/51 = hesap kimliği/
    /// başlığı/İYS yetkisi — alıcıdan bağımsız, her alıcıda aynen tekrarlanır.
    /// 70 = parametre/alıcı hatası — yalnız o alıcıyı ilgilendirir.</summary>
    public NetgsmErrorClass Classify() => Code switch
    {
        "30" or "40" or "50" or "51" => NetgsmErrorClass.Account,
        "70" => NetgsmErrorClass.Recipient,
        _ => NetgsmErrorClass.CampaignPause,
    };
}
```

- [ ] **Step 4: Testin PASS ettiğini doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmSmsExceptionTests`
Expected: PASS (10 test).

- [ ] **Step 5: Mutasyon provası**

`"70" => NetgsmErrorClass.Recipient` satırını geçici olarak `"71"` yap → `RecipientClass_Code70` FAIL etmeli. Geri al. `"30" or "40"...` dalından `"51"`i çıkar → `AccountClass_Codes("51")` FAIL etmeli. Geri al.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/NetgsmSmsException.cs OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmSmsExceptionTests.cs
git commit -m "feat(sms): NetgsmSmsException + üç sınıflı hata modeli (§3.4)"
```

---

### Görev 2: `ITenantSmsSender` + Netgsm/Log implementasyonları + DI

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Sms/ITenantSmsSender.cs`
- Create: `OrderDeck.LicenseServer/Services/Sms/NetgsmTenantSmsSender.cs`
- Create: `OrderDeck.LicenseServer/Services/Sms/LogTenantSmsSender.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (~satır 168-175, SMS DI bloğu)
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmTenantSmsSenderTests.cs`

Mevcut `NetgsmSmsSenderTests.cs`'i incele: `HttpClient`'ı sahte `HttpMessageHandler` ile besleme kalıbını AYNEN kopyala (yeni kalıp icat etme).

- [ ] **Step 1: Failing testler**

`NetgsmTenantSmsSenderTests.cs` — mevcut `NetgsmSmsSenderTests`'teki handler kalıbıyla şu davranışları kilitle (tam istek/yanıt gövdelerini o dosyadaki örneklere göre kur):

```csharp
// 1) Başarı: HTTP 200 + {"code":"00","jobid":"12345"} → dönüş "12345";
//    istek gövdesinde msgheader = credentials.Header; Authorization Basic
//    base64(UserCode:Password) — KİMLİKLER PARAMETREDEN, NetgsmOptions'tan değil.
// 2) Başarı + jobid yok: {"code":"00"} → dönüş null, istisna yok.
// 3) iysfilter DAİMA ticari: gövdede "iysfilter":"11" (NetgsmOptions.CommercialIysFilter).
// 4) Temiz ret JSON: HTTP 406 + {"code":"30","description":"..."} →
//    NetgsmSmsException, Code=="30".
// 5) Temiz ret düz metin: HTTP 406 + gövde "40" → NetgsmSmsException, Code=="40".
// 6) 2xx ama tanınmayan gövde: HTTP 200 + "garip" → NetgsmSmsException, Code==null
//    (yanıt alındı, kabul kodu yok → temiz ret varsayılır; iş kabul edilmedi).
// 7) Ağ hatası: handler HttpRequestException fırlatır → HttpRequestException
//    AYNEN dışarı çıkar (NetgsmSmsException'a SARILMAZ — sözleşme: sarma = "hiç gitmedi" garantisi).
// 8) Telefon dönüşümü: "+905321234567" → gövdede "no":"5321234567".
```

Test kimlikleri **üretilmiş** olmalı (repo public, tarayıcı kuralı): `var creds = new TenantSmsCredentials($"u-{Guid.NewGuid():N}", $"p-{Guid.NewGuid():N}", "BASLIK");`

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter NetgsmTenantSmsSenderTests`
Expected: derleme hatası.

- [ ] **Step 3: Implementasyon**

`ITenantSmsSender.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Bir kiracının Netgsm kimlikleri. Şifre ÇÖZÜLMÜŞ hâlde taşınır —
/// yalnız bu record'un ömrü boyunca bellekte; asla loglanmaz, asla saklanmaz.</summary>
public sealed record TenantSmsCredentials(string UserCode, string Password, string Header);

/// <summary>
/// Kiracı (yayıncı) kimlikleriyle TİCARİ SMS gönderimi — §1.2. Merkezi
/// <see cref="ISmsSender"/>'dan ayrı arayüz olması kasıtlı: tek arayüze kimlik
/// parametresi eklemek "yanlış marka altında ticari SMS" hatasını çalışma-anı
/// kontrolüne indirger; ayrı arayüz onu yapısal olarak imkânsız kılar.
/// <see cref="SmsKind"/> parametresi YOK: bu yol tanımı gereği Commercial,
/// iysfilter daima ticari filtre ile gider.
/// </summary>
public interface ITenantSmsSender
{
    /// <summary>Tek alıcıya ticari SMS. Dönüş: Netgsm <c>jobid</c> (yanıtta
    /// yoksa null) — ileride rapor mutabakatı için saklanır (§3.4 karar 3).
    /// Temiz ret → <see cref="NetgsmSmsException"/>; ağ hatası → ham istisna
    /// (ayrım sözleşmesi o sınıfın doc'unda).</summary>
    Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default);
}
```

`NetgsmTenantSmsSender.cs` — `NetgsmSmsSender`'ın istek kurulumunu taklit eder (BaseUrl/Encoding/Timeout `NetgsmOptions`'tan, kimlik + başlık parametreden):

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Netgsm REST v2 ile kiracı kimlikli ticari gönderim. Kardeşi
/// <see cref="NetgsmSmsSender"/> (merkezi/Transactional); istek biçimi aynı,
/// fark: (a) Basic auth ve msgheader ÇAĞRI parametresinden gelir,
/// (b) iysfilter daima <see cref="NetgsmOptions.CommercialIysFilter"/>,
/// (c) başarıda jobid döndürülür, (d) temiz ret <see cref="NetgsmSmsException"/>
/// olarak kod taşır (§3.4 sınıflandırması için).
/// </summary>
public sealed class NetgsmTenantSmsSender : ITenantSmsSender
{
    private readonly HttpClient _http;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<NetgsmTenantSmsSender> _log;

    public NetgsmTenantSmsSender(
        HttpClient http, IOptions<NetgsmOptions> opt, ILogger<NetgsmTenantSmsSender> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        var no = ToNetgsmNo(toPhone);

        var payload = new Dictionary<string, object?>
        {
            ["msgheader"] = credentials.Header,
            ["messages"] = new[] { new { msg = message, no } },
            // Bu yol tanımı gereği ticari: Netgsm alıcıyı kendi İYS kaydına
            // göre de eler (çift kapı — bizim IysConsentGate + Netgsm filtresi).
            ["iysfilter"] = _opt.CommercialIysFilter,
        };
        if (!string.IsNullOrWhiteSpace(_opt.Encoding))
            payload["encoding"] = _opt.Encoding;
        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opt.BaseUrl.TrimEnd('/')}/sms/rest/v2/send");
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.UserCode}:{credentials.Password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            // Ağ hatası: mesaj Netgsm'e ULAŞMIŞ olabilir. NetgsmSmsException'a
            // SARMA — o sınıf "hiçbir şey gitmedi" garantisi taşıyor.
            _log.LogWarning(ex, "Netgsm tenant SMS failed (network) for {Phone}",
                PiiMasker.MaskPhone(toPhone));
            throw;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var (code, jobId) = ParseResponse(body);

        if (resp.IsSuccessStatusCode && code is "00" or "01" or "02")
        {
            _log.LogInformation("Netgsm tenant SMS sent to {Phone} (jobid={JobId})",
                PiiMasker.MaskPhone(toPhone), jobId ?? "-");
            return jobId;
        }

        // Yanıt alındı ve kabul kodu yok → Netgsm işi reddetti, hiçbir SMS
        // gitmedi. Gövde LOG'A yazılır ama İSTİSNAYA yazılmaz: istisna mesajı
        // alıcı satırının Error alanına düşüyor ve panelde görünecek —
        // ham gövde oraya taşınmamalı (LastError kuralının kardeşi).
        _log.LogWarning("Netgsm tenant SMS rejected for {Phone}: status={Status} body={Body}",
            PiiMasker.MaskPhone(toPhone), (int)resp.StatusCode, body);
        throw new NetgsmSmsException(code,
            $"Netgsm reddetti (code={code ?? "?"}, http={(int)resp.StatusCode}).");
    }

    // Kardeşi NetgsmSmsSender.ToNetgsmNo — bilinçli kopya, iki satır.
    private static string ToNetgsmNo(string e164)
    {
        var digits = new string(e164.Where(char.IsDigit).ToArray());
        return digits.StartsWith("90") && digits.Length == 12 ? digits[2..] : digits;
    }

    /// <summary>Yanıttan (code, jobid) çıkarır. JSON değilse düz metin kod
    /// denenir ("30", "00 ..." gibi). Hiçbiri değilse (null, null).</summary>
    private static (string? Code, string? JobId) ParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? code = null, jobId = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var codeEl))
                    code = codeEl.ValueKind == JsonValueKind.String
                        ? codeEl.GetString() : codeEl.ToString();
                if (doc.RootElement.TryGetProperty("jobid", out var jobEl))
                    jobId = jobEl.ValueKind == JsonValueKind.String
                        ? jobEl.GetString() : jobEl.ToString();
            }
            return (code, jobId);
        }
        catch (JsonException)
        {
            var trimmed = body.Trim().Trim('"');
            var head = trimmed.Split(' ')[0];
            return (head.Length is 2 or 3 && head.All(char.IsDigit) ? head : null, null);
        }
    }
}
```

`LogTenantSmsSender.cs` (dev/test — `LogSmsSender` kalıbını kopyala):

```csharp
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Dev/test: kiracı SMS'ini yalnız loglar. Kardeşi <see cref="LogSmsSender"/>.</summary>
public sealed class LogTenantSmsSender : ITenantSmsSender
{
    private readonly ILogger<LogTenantSmsSender> _log;
    public LogTenantSmsSender(ILogger<LogTenantSmsSender> log) => _log = log;

    public Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        _log.LogInformation("(log) tenant SMS to {Phone} header={Header}: {Message}",
            PiiMasker.MaskPhone(toPhone), credentials.Header, message);
        return Task.FromResult<string?>(null);
    }
}
```

`Program.cs` — mevcut `if (smsProvider == "netgsm")` bloğunun İÇİNE typed client, `else`'e log kaydı ekle:

```csharp
// if dalına (mevcut AddHttpClient<ISmsSender,...>'ın altına):
builder.Services.AddHttpClient<OrderDeck.LicenseServer.Services.Sms.ITenantSmsSender,
        OrderDeck.LicenseServer.Services.Sms.NetgsmTenantSmsSender>(
        c => c.Timeout = TimeSpan.FromSeconds(smsTimeout <= 0 ? 10 : smsTimeout));
// else dalına:
builder.Services.AddSingleton<OrderDeck.LicenseServer.Services.Sms.ITenantSmsSender,
    OrderDeck.LicenseServer.Services.Sms.LogTenantSmsSender>();
```

(`else` tek satırdı; blok `{ }` hâline getir.)

- [ ] **Step 4: PASS doğrula + sözleşme 7 kontrolü**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "NetgsmTenantSmsSenderTests|NetgsmSmsSenderTests"`
Expected: hepsi PASS. `NetgsmSmsSenderTests` içinde "Commercial → InvalidOperationException(iys-tenant-sender-missing)" testi VAR MI bak; yoksa ekle (sözleşme 7 — merkezi gönderici `Commercial` alırsa fırlatır; bu kilit KALICI, kaldırma koşulu artık gerçekleşti ama merkezi yol yine de ticari taşımamalı). `NetgsmSmsSender.cs:28-51`'deki kilit yorumunu güncelle: "KALDIRMA KOŞULU" paragrafını "KALICI: ticari yol ITenantSmsSender'dan gider (Plan 3); bu fırlatma §1.2'nin yapısal güvencesi" ile değiştir.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/ITenantSmsSender.cs OrderDeck.LicenseServer/Services/Sms/NetgsmTenantSmsSender.cs OrderDeck.LicenseServer/Services/Sms/LogTenantSmsSender.cs OrderDeck.LicenseServer/Services/Sms/NetgsmSmsSender.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmTenantSmsSenderTests.cs OrderDeck.LicenseServer.Tests/Services/Sms/NetgsmSmsSenderTests.cs
git commit -m "feat(sms): ITenantSmsSender — kiracı kimlikli ticari gönderim yolu (§1.2)"
```

---

### Görev 3: `SmsCampaignSendJob` — hesap kapısı, `skipped`, üç sınıflı hata, kredi iadesinin sökülmesi

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignSendJob.cs`
- Modify: `OrderDeck.LicenseServer/Domain/SmsCampaignRecipient.cs` (+`ProviderJobId`)
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (recipient config'e `ProviderJobId` maxlength 64)
- Modify/Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignIysGateTests.cs`, `SmsCampaignPauseTests.cs`, `SmsCampaignRecipientClaimTests.cs`, `NetgsmAccountResumeTests.cs`, `Controllers/Licenses/SmsCampaignTests.cs` (ctor değişikliği + yeni sözleşmeler)
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/FakeTenantSmsSender.cs`

**Önce mevcut test dosyalarını oku** — job'ı nasıl kurduklarını, sahte `ISmsSender`'ı nasıl verdiklerini gör; aynı kalıbı `ITenantSmsSender` için uygula.

- [ ] **Step 1: `FakeTenantSmsSender` yaz**

```csharp
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>Kampanya job testleri için programlanabilir kiracı göndericisi.
/// Telefon başına istisna kuyruklanabilir; gönderilenler kaydedilir.</summary>
public sealed class FakeTenantSmsSender : ITenantSmsSender
{
    public List<(TenantSmsCredentials Creds, string Phone, string Message)> Sent { get; } = new();
    public Dictionary<string, Exception> FailWith { get; } = new();
    public string? NextJobId { get; set; }

    public Task<string?> SendAsync(
        TenantSmsCredentials credentials, string toPhone, string message,
        CancellationToken ct = default)
    {
        if (FailWith.TryGetValue(toPhone, out var ex)) throw ex;
        Sent.Add((credentials, toPhone, message));
        return Task.FromResult(NextJobId);
    }
}
```

- [ ] **Step 2: Yeni sözleşme testlerini yaz (failing)**

Mevcut test dosyalarındaki seed yardımcılarını (lisans + Verified `NetgsmAccount` + kampanya + alıcılar + İYS onayları kurulumu) kullanarak şu testleri ekle — dosya: `SmsCampaignPauseTests.cs`'e (kampanya-durum testleri orada) ya da yeni `SmsCampaignSendJobErrorClassTests.cs`:

```csharp
// A) Sözleşme 5 — kapı elerse alıcı SKIPPED, failed değil:
//    Verified hesap + onayı olmayan alıcı → RunAsync → recipient.Status == "skipped",
//    Error == "iys-consent-missing"; onayı RET olan → "iys-consent-not-onay".
// B) Kampanya kapısı — Verified hesap YOKSA kampanya hiç başlamaz:
//    hesap yok → RunAsync → campaign.Status == "paused"; TÜM alıcılar "pending";
//    FakeTenantSmsSender.Sent boş. (Eski davranış alıcı başına failed
//    "iys-brand-missing" yazıyordu — artık kampanya hatası, alıcı hatası değil.)
// C) Sözleşme 13 — hesap sınıfı kod kitleyi harcamaz:
//    3 alıcı, 2.'ye FailWith[phone2] = new NetgsmSmsException("30", "x") →
//    RunAsync → alıcı1 "sent"; alıcı2 "pending" (temiz ret, geri döndü);
//    alıcı3 "pending" (hiç denenmedi); campaign "paused";
//    NetgsmAccount.Status == Failed; LastError != null.
// D) Sözleşme 4 — CampaignPause sınıfı (bakiye/bilinmeyen kod):
//    FailWith[phone2] = new NetgsmSmsException("85", "x") →
//    alıcı1 "sent"; alıcı2+3 "pending"; campaign "paused";
//    hesap Verified KALIR (hesap suçlu değil).
// E) Recipient sınıfı: FailWith[phone2] = new NetgsmSmsException("70", "x") →
//    alıcı2 "failed", alıcı1+3 "sent"; campaign "completed".
// F) Belirsiz hata: FailWith[phone2] = new HttpRequestException("boom") →
//    alıcı2 "failed" (pending DEĞİL — çift SMS riski), döngü devam, campaign "completed".
// G) Sözleşme 10 — şifre çözülemiyor: hesabın PasswordProtected alanına
//    çözülemez değer yaz (örn. "bozuk") → RunAsync → hesap Disabled,
//    LastError == NetgsmAccountService.UndecryptableMessage, campaign "paused",
//    alıcılar "pending", Sent boş.
// H) jobid saklanır: NextJobId = "j-1" → sent alıcının ProviderJobId == "j-1".
// I) Kimlik doğru: Sent[0].Creds.Header == hesabın Header'ı;
//    Sent[0].Creds.UserCode == hesabın UserCode'u.
```

- [ ] **Step 3: FAIL doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter SmsCampaignSendJobErrorClassTests`
Expected: FAIL/derleme hatası.

- [ ] **Step 4: Domain + DbContext**

`SmsCampaignRecipient.cs`'e ekle (Error'un altına):

```csharp
    /// <summary>Netgsm'in kabul yanıtındaki <c>jobid</c> (§3.4 karar 3).
    /// Rapor-aşaması mutabakatı bugün YOK (§9.3 doğrulanmamış); sütun ileride
    /// "gitti mi" sorusuna ham veri bırakır. Gönderim kabul edilmediyse null.</summary>
    public string? ProviderJobId { get; set; }
```

`Status` doc'unu güncelle: `"skipped"` = İYS kapısı eledi (onay yok/ret) — altyapı arızası DEĞİL, sistem doğru çalıştı (§3.3). `Error` doc: "failed VE skipped durumunda sebep; CampaignPause'da pending satıra son ret kodu yazılabilir".

`LicenseDbContext.cs`'te `SmsCampaignRecipient` config bloğunu bul, ekle: `e.Property(x => x.ProviderJobId).HasMaxLength(64);`

- [ ] **Step 5: `SmsCampaignSendJob` yeniden yaz**

Değişmeyenler: sınıf doc'un F08/Görev 16 bölümleri, `ClaimLease`, `NextClaimedAt`, `SaveRecipientResultAsync`, `ClaimRecipientAsync`, kampanya claim bloğu (satır 178-227), alıcı yükleme, tur başı skaler sahiplik yoklaması, alıcı claim sırası.

Değişenler:

1. **Ctor:** `ISmsSender _sms` → `ITenantSmsSender _tenantSms`; `LicenseSmsBalanceService _balance` SİL. Sınıf doc'una ekle: "Plan 3: kimlikler kampanyanın lisansının NetgsmAccount satırından çözülür; kredi sistemi emekli — iade yok."

2. **Claim'den hemen sonra hesap kapısı** (`recipients` yüklemeden önce):

```csharp
        // §3.2: kampanya başlarken HESAP kontrolü — bu bir kampanya hatası,
        // alıcı hatası değil. Eski kod marka yokluğunu alıcı başına
        // "iys-brand-missing" failed yazıyordu ve kitleyi harcıyordu; şimdi
        // kampanya duraklar, kitle pending korunur, kurulum doğrulanınca
        // devam ettirilebilir (StageResumePausedCampaignsAsync).
        var account = await _accounts.GetVerifiedByLicenseAsync(campaign.LicenseId, ct);
        if (account is null)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} lisansının doğrulanmış Netgsm hesabı yok — duraklatılıyor",
                campaignId);
            await TryPauseCampaignAsync(campaign, ct);
            return;
        }

        var password = _accounts.TryUnprotectPassword(account.PasswordProtected);
        if (password is null)
        {
            // Sözleşme 10 (§2.4): anahtar halkası kaybı sessiz bozulma OLMAZ —
            // hesap Disabled + panelde kalıcı mesaj; kampanyalar (bizimki dahil)
            // aynı yazımda paused. Kampanyayı ayrıca duraklatmıyoruz:
            // CloseAccountAndPauseCampaignsAsync "pending" VE "sending"
            // kampanyaları hesapla aynı SaveChanges'te duraklatır.
            _log.LogError(
                "SmsCampaignSendJob: campaign {Id} hesabının şifresi çözülemedi — hesap kapatılıyor",
                campaignId);
            await _accounts.CloseAccountAndPauseCampaignsAsync(
                account.Id, NetgsmAccountStatus.Disabled,
                NetgsmAccountService.UndecryptableMessage, ct);
            return;
        }

        var creds = new TenantSmsCredentials(account.UserCode, password, account.Header);
        var brandCode = account.BrandCode;
```

(`GetBrandCodeAsync` çağrısı ve `brandCode is null` dalı SİLİNİR; onay sözlüğü koşulsuz `brandCode` ile kurulur. `NetgsmAccountStatus` için `using OrderDeck.LicenseServer.Domain;` zaten var.)

`TryPauseCampaignAsync` yardımcısı:

```csharp
    /// <summary>Kampanyayı duraklatır (damga ilerletilerek — bayat işçi
    /// sahipliği geri kazanamasın). Çakışma = biri bizden önce yazdı;
    /// kararı ona bırakıp sessizce çekiliriz (kampanya claim'indeki
    /// Detach gerekçesinin aynısı).</summary>
    private async Task TryPauseCampaignAsync(SmsCampaign campaign, CancellationToken ct)
    {
        campaign.Status = "paused";
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.Entry(campaign).State = EntityState.Detached;
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} pause yarışı kaybetti, çekiliyor", campaign.Id);
        }
    }
```

3. **İYS kapısı → `skipped`** (sözleşme 5; `iys-brand-missing` dalı öldü, kapı hesabın markasıyla):

```csharp
            if (!IysConsentGate.CanSend(consent))
            {
                // §3.3: kapı elemesi altyapı arızası DEĞİL — sistem doğru
                // çalıştı. "failed" yazmak yayıncıya "47 başarısız" gösterip
                // arıza sandırır; skipped oranı ayrıca kötüye kullanımın
                // tek erken göstergesi.
                recipient.Status = "skipped";
                recipient.Error = consent is null
                    ? "iys-consent-missing"
                    : "iys-consent-not-onay";
                recipient.SentAt = null;

                if (!await SaveRecipientResultAsync(campaign, ct)) return;
                continue;
            }
```

4. **Gönderim + üç sınıf:**

```csharp
            try
            {
                var jobId = await _tenantSms.SendAsync(
                    creds, recipient.Phone, campaign.MessageBody, ct);

                recipient.Status = "sent";
                recipient.SentAt = DateTimeOffset.UtcNow;
                recipient.Error = null;
                recipient.ProviderJobId = jobId;
            }
            catch (NetgsmSmsException ex)
            {
                var cls = ex.Classify();
                if (cls == NetgsmErrorClass.Recipient)
                {
                    // Yalnız bu alıcı geçersiz; döngü devam eder.
                    recipient.Status = "failed";
                    recipient.Error = Truncate(ex.Message);
                    _log.LogWarning(
                        "SmsCampaignSendJob: recipient {RecipientId} rejected (code={Code})",
                        recipient.Id, ex.Code);
                }
                else
                {
                    // Temiz ret (NetgsmSmsException sözleşmesi): hiçbir şey
                    // gitmedi → alıcı kitleye GERİ döner. failed yazmak
                    // kitleyi harcardı (§3.4 — "şifre düzeltilince geri
                    // gelecek kimse kalmaz").
                    recipient.Status = "pending";
                    recipient.Error = Truncate(ex.Message);
                    recipient.SentAt = null;

                    if (cls == NetgsmErrorClass.Account)
                    {
                        // Hesap sınıfı: alıcıyı geri yaz, sonra hesabı kapat —
                        // kapatma "pending"+"sending" kampanyaları (bizimki
                        // dahil) hesapla aynı SaveChanges'te duraklatır.
                        // İki yazım arasında çökersek kampanya taze damgalı
                        // "sending" kalır; lease bayatlayınca recovery yeniden
                        // koşar, aynı hata sınıfına çarpar ve kapatmayı bitirir.
                        _log.LogError(
                            "SmsCampaignSendJob: campaign {Id} hesap hatası (code={Code}) — hesap kapatılıyor",
                            campaignId, ex.Code);
                        if (!await SaveRecipientResultAsync(campaign, ct)) return;
                        await _accounts.CloseAccountAndPauseCampaignsAsync(
                            account.Id, NetgsmAccountStatus.Failed,
                            $"Netgsm hesabı reddetti (kod {ex.Code ?? "?"}). Panelden bilgileri kontrol edin.",
                            ct);
                        return;
                    }

                    // CampaignPause sınıfı (bakiye/limit/bilinmeyen): alıcı
                    // geri dönüşü + duraklatma TEK SaveChanges'te.
                    _log.LogWarning(
                        "SmsCampaignSendJob: campaign {Id} duraklatılıyor (code={Code})",
                        campaignId, ex.Code);
                    campaign.Status = "paused";
                    campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        _db.Entry(campaign).State = EntityState.Detached;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                // Belirsiz hata (ağ/timeout): SMS gitmiş OLABİLİR. pending'e
                // döndürmek devam ettirmede aynı kişiye ikinci ticari SMS
                // göndermek olurdu (para + 6563) — failed yaz, devam et.
                // Bilinen sınır: ağ kesintisi kitleyi failed'a yazabilir.
                recipient.Status = "failed";
                recipient.Error = Truncate(ex.Message);
                _log.LogWarning(ex,
                    "SmsCampaignSendJob: send failed for campaign {Id} recipient {RecipientId}",
                    campaignId, recipient.Id);
            }

            if (!await SaveRecipientResultAsync(campaign, ct)) return;
```

`Truncate` yardımcısı: `private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;` (mevcut inline `[..500]` yerine).

5. **Tamamlama — iade SÖKÜLÜR:**

```csharp
        var counts = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int CountOf(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

        campaign.Status = "completed";
        campaign.CompletedAt = DateTimeOffset.UtcNow;
        // Delik 2 (değişmedi): tamamlanmada damga tazelenir — bayat "duraklat"
        // yazımı çakışma alsın diye.
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "SmsCampaignSendJob: campaign {Id} completed — sent={Sent} failed={Failed} skipped={Skipped}",
            campaignId, CountOf("sent"), CountOf("failed"), CountOf("skipped"));
```

(`failedCount`/`owed`/`refund`/`RefundedCredits`/`ApplyAndSaveAsync` blokları ve "Delik 1" yorumu tamamen gider — kredi yok, iade yok. "İKİZİ: RecoveryJob" notu da gider; Görev 5 kardeş formülü siliyor.)

- [ ] **Step 6: Mevcut testleri uyarla**

`SmsCampaignIysGateTests` / `SmsCampaignPauseTests` / `SmsCampaignRecipientClaimTests` / `NetgsmAccountResumeTests` / `SmsCampaignTests`: job ctor'ları `FakeTenantSmsSender` ile güncelle; her test artık **Verified NetgsmAccount satırı** seed etmeli (yoksa kampanya kapıda duraklar — testler bunu görecek). Kapı testlerinde `failed` beklentilerini `skipped`'a çevir; `iys-brand-missing` bekleyen testleri B senaryosuna (kampanya paused) dönüştür. `ApplyAndSaveAsync`/kredi seed'i kalıntılarını sil. Şifre seed'i: `service.ProtectPassword(...)` ile üretilmiş değer (düz metin yazma — repo public, kimlik üret: `$"p-{Guid.NewGuid():N}"`).

- [ ] **Step 7: PASS doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "SmsCampaign|NetgsmAccount"`
Expected: hepsi PASS.

- [ ] **Step 8: Mutasyon provası**

(1) `skipped` → `failed` çevir → A testi FAIL. (2) Account dalındaki `recipient.Status = "pending"` → `"failed"` çevir → C testi FAIL. (3) Hesap kapısındaki `TryPauseCampaignAsync` çağrısını kaldır (return bırak) → B testi FAIL. Hepsini geri al.

- [ ] **Step 9: Commit**

```bash
git add -A -- OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests
git commit -m "feat(sms): kampanya gönderimi kiracı kimlikleriyle — hesap kapısı, skipped, üç sınıflı hata (§3.2-3.4)"
```

---

### Görev 4: `LicensesSmsCampaignsController` — kredisiz atomik kampanya kaydı (sözleşme 17)

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSmsCampaignsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsCampaignTests.cs`, `SmsCampaignIdempotencyConcurrencyTests.cs`

- [ ] **Step 1: Failing testler**

`SmsCampaignTests.cs`'e ekle/uyarla:

```csharp
// 1) Sözleşme 17: Verified hesap + alıcılar varken POST → 200; kampanya +
//    alıcı satırları DB'de (kredi servisi olmadan tek SaveChanges); Hangfire
//    enqueue çağrıldı. (Kredi seed'i YOK.)
// 2) Hesap kapısı: Verified hesap YOKken POST → 409, title "netgsm-account-missing";
//    DB'de kampanya YOK.
// 3) Preview: Verified hesap varken → Sufficient == true, CreditsRemaining == 0;
//    hesap yokken → Sufficient == false. (Eski istemcinin Gönder düğmesi bu
//    alana bağlı — kapı buradan da görünmeli.)
// 4) Status/List: CreditsRefunded == 0 (JSON alanı eski istemci için sabit).
// 5) F09 ön-kontrol: aynı ClientRequestId ikinci POST → ilk kampanyanın yanıtı,
//    TotalCredits == RecipientCount * SegmentsPerMessage.
```

`SmsCampaignIdempotencyConcurrencyTests` (Testcontainers): kredi seed'ini kaldır; yarış sonrası doğrulamayı "tek kampanya + tek alıcı seti yazıldı" olarak bırak (çift kredi düşümü iddiası öldü).

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter SmsCampaignTests`

- [ ] **Step 3: Controller'ı yeniden yaz**

- Ctor: `LicenseSmsBalanceService _balance` SİL → `(LicenseDbContext db, IBackgroundJobClient jobs)`.
- Sınıf doc: "Kredi sistemi emekli (Plan 3). Kampanya kapısı: doğrulanmış NetgsmAccount. Kredi/bakiye alanları eski WPF istemcileri için JSON'da sabit değerle yaşar — kaldırma koşulu: saha WPF sürümleri bu plandaki istemciye geçtiğinde."
- **Preview:**

```csharp
        var recipientCount = await ConsentedRecipients(licenseId)
            .Select(r => r.Phone).Distinct().CountAsync(ct);
        var segments = SmsSegmentCalculator.Segments(req.MessageBody);
        var totalCredits = recipientCount * segments;

        // Sufficient artık "kurulum hazır mı" demek. Eski WPF CanSend()'i bu
        // alana bağlı (BulkSmsViewModel.cs:182) — alan false'ken düğme kapalı,
        // yani doğrulanmamış kurulumda eski istemci de doğru şekilde bloklanır.
        // CreditsRemaining=0 sabit: eski istemcide yalnız kozmetik rozet.
        var accountVerified = await _db.NetgsmAccounts.AnyAsync(
            a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified, ct);

        return Ok(new PreviewResponse(
            recipientCount, segments, totalCredits,
            CreditsRemaining: 0, Sufficient: accountVerified));
```

- **Create:** F09 ön-kontrol yanıtlarında `existing.ReservedCredits` → `existing.RecipientCount * existing.SegmentsPerMessage`. Bakiye ön-kontrol bloğu (satır 115-120) yerine:

```csharp
        // §3.2 kapısı burada DA: kampanyayı yaratıp hemen duraklatmak yerine
        // hiç açmamak — yayıncı hatayı anında görür. Job'daki kapı yine kalır
        // (yarış: create ile job arasında hesap kapatılabilir).
        var accountVerified = await _db.NetgsmAccounts.AnyAsync(
            a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified, ct);
        if (!accountVerified)
            return Problem(title: "netgsm-account-missing", statusCode: 409,
                detail: "Netgsm kurulumu doğrulanmamış; kampanya açılamaz. Panelden Netgsm bilgilerini girin.");
```

Kampanya nesnesinden `ReservedCredits = totalCredits,` satırını SİL. Rezervasyon bloğu (155-181) yerine:

```csharp
        // Sözleşme 17: kampanya + alıcı satırları enqueue'dan ÖNCE tek
        // SaveChanges ile yazılır. Eskiden bu yazımın taşıyıcısı kredi
        // servisinin ApplyAndSaveAsync'iydi; kredi öldü, SaveChanges artık
        // burada. F09 unique index yakalaması aynı kaldı: iki eş istek ön
        // kontrolü aynı anda geçerse kaybeden (LicenseId, ClientRequestId)
        // index'ine çarpar ve kazananın yanıtını döndürür.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (req.ClientRequestId is not null)
        {
            var winner = await _db.SmsCampaigns.FirstOrDefaultAsync(
                c => c.LicenseId == licenseId && c.ClientRequestId == req.ClientRequestId, ct);
            if (winner is null) throw;
            return Ok(new CreateResponse(
                winner.Id, winner.RecipientCount,
                winner.RecipientCount * winner.SegmentsPerMessage));
        }
```

- **Status/List:** `CreditsRefunded: campaign.RefundedCredits` → `CreditsRefunded: 0` (yorum: "eski WPF istemcisi bu alanı parse ediyor; kredi emekli, sabit 0"). `using OrderDeck.LicenseServer.Services.Sms;` hâlâ gerekli (`SmsSegmentCalculator`, `SmsCampaignSendJob`).

- [ ] **Step 4: PASS doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "SmsCampaignTests|SmsCampaignIdempotency"`
(Idempotency testleri Docker ister — açık olduğundan emin ol.)

- [ ] **Step 5: Mutasyon provası**

Create'teki `accountVerified` kontrolünü kaldır → test 2 FAIL. `Sufficient: accountVerified` → `Sufficient: true` → test 3 FAIL. Geri al.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesSmsCampaignsController.cs OrderDeck.LicenseServer.Tests/Controllers/Licenses/
git commit -m "feat(sms): kampanya oluşturma kredisiz — hesap kapısı + atomik kayıt (sözleşme 17)"
```

---

### Görev 5: `SmsCampaignRecoveryJob` — iade sökümü + pending yeniden-kuyruklama düzeltmesi

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Sms/SmsCampaignRecoveryJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignRecoveryJobTests.cs`

- [ ] **Step 1: Failing testler**

```csharp
// 1) Sözleşme 6: "paused" + bekleyen alıcısı OLAN kampanya → sweep NE enqueue
//    eder NE tamamlar (status "paused" kalır). (Muhtemelen var — kontrol et.)
// 2) §3.4 düzeltme 2: "pending" + CreatedAt eski AMA ClaimedAt TAZE (devam
//    ettirilmiş kampanya) → enqueue EDİLMEZ. ClaimedAt bayatsa/null'sa edilir.
// 3) Asılı paused tamamlama İADESİZ: paused + bekleyen alıcı yok + failed'lar
//    var → status "completed"; (kredi iddiası yok — LicenseSmsTransactions
//    sorgusu testten silinir).
```

- [ ] **Step 2: FAIL doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter SmsCampaignRecoveryJobTests`

- [ ] **Step 3: Implementasyon**

- Ctor: `LicenseSmsBalanceService _balance` SİL.
- Stuck sorgusunun pending dalı:

```csharp
                || (c.Status == "pending"
                    && c.CreatedAt < pendingCutoff
                    // Devam ettirilen kampanyanın damgası TAZE olur
                    // (StageResumePausedCampaignsAsync damgayı ilerletir).
                    // Damga filtresi olmadan böyle bir kampanya HER süpürmede
                    // yeniden kuyruklanırdı (§3.4 düzeltme 2). Bedeli: devam
                    // ettirilen kampanyanın ilk enqueue'su lease bayatlayınca
                    // (≤15 dk) gelir — süpürme zaten güvenlik ağı, hız yolu değil.
                    && (c.ClaimedAt == null || c.ClaimedAt < staleClaim))
```

- `CompleteStrandedAsync`: `failedCount` sorgusu, `owed`/`refund` formülü, `RefundedCredits` yazımı ve `_balance.ApplyAndSaveAsync` dalı SİLİNİR → gövde: status/CompletedAt/ClaimedAt yaz + `await _db.SaveChangesAsync(ct)`; `DbUpdateConcurrencyException` → Detach + devam bloğu AYNEN kalır (yalnız iade cümleleri yorumlardan ayıklanır; "İKİZİ" paragrafı silinir, N05 yorumu silinir). Sınıf doc'unun 3. sınıf paragrafındaki "başarısızların kredisi iade edilmemiştir" cümlesi çıkar.

- [ ] **Step 4: PASS + mutasyon provası**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter SmsCampaignRecoveryJobTests`
Mutasyon: pending dalındaki `&& (c.ClaimedAt == null || ...)` filtresini kaldır → test 2 FAIL. Geri al.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Sms/SmsCampaignRecoveryJob.cs OrderDeck.LicenseServer.Tests/Services/Sms/SmsCampaignRecoveryJobTests.cs
git commit -m "fix(sms): recovery — iade sökümü + devam ettirilen kampanyanın tekrar kuyruklanmaması (§3.4)"
```

---

### Görev 6: Bakiye stub'ı + `AdminSmsController` silme

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSmsBalanceController.cs`
- Delete: `OrderDeck.LicenseServer/Controllers/Licenses/AdminSmsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/SmsBalanceTests.cs`

- [ ] **Step 1: Testleri uyarla (failing)**

`SmsBalanceTests.cs`: topup/admin testlerini SİL; balance GET testlerini stub'a uyarla — sahiplik/auth kontrolleri kalır, gövde beklentisi `creditsRemaining == 0`. Kredi seed'li senaryolar gider.

- [ ] **Step 2: Implementasyon**

`AdminSmsController.cs` dosyasını sil. `LicensesSmsBalanceController`'ı yeniden yaz:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// UYUMLULUK STUB'I — kredi sistemi emekli (Plan 3, §1.4b kararı).
///
/// <para>Sahadaki eski WPF istemcileri (Velopack gecikmesi) geçmiş listesini
/// yüklemeden ÖNCE bu ucu await ediyor (BulkSmsViewModel.ReloadBalanceAndHistoryAsync);
/// uç 404 dönerse toplu SMS ekranı tamamen ölür. Bu yüzden uç bir sürüm boyunca
/// sabit değerle yaşar. KALDIRMA KOŞULU: saha WPF sürümleri bakiye çağrısı
/// yapmayan istemciye (bu planın Görev 8'i) geçtiğinde.</para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sms")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSmsBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesSmsBalanceController(LicenseDbContext db) => _db = db;

    public sealed record BalanceResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        var owns = await _db.Licenses.AnyAsync(
            l => l.Id == licenseId && l.CustomerId == callerId, ct);
        if (!owns) return NotFound();

        // Sabit 0: eski istemcide yalnız kozmetik rozet ("Kredi: 0").
        // Gönderilebilirlik oradan değil Preview.Sufficient'tan geliyor.
        return Ok(new BalanceResponse(0, DateTimeOffset.UtcNow));
    }
}
```

(Mevcut dosyadaki auth/ownership kalıbı farklıysa — örn. yardımcı metot — mevcut kalıbı koru, davranışı değiştir.)

- [ ] **Step 3: PASS doğrula + Commit**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter SmsBalanceTests`

```bash
git add -A -- OrderDeck.LicenseServer/Controllers/Licenses OrderDeck.LicenseServer.Tests/Controllers/Licenses
git commit -m "feat(sms): /sms/balance uyumluluk stub'ı + AdminSmsController silindi (§1.4)"
```

---

### Görev 7: Kredi sisteminin kökten silinmesi + göç

**Files:**
- Delete: `OrderDeck.LicenseServer/Services/Sms/LicenseSmsBalanceService.cs`, `OrderDeck.LicenseServer/Domain/LicenseSmsBalance.cs`, `OrderDeck.LicenseServer/Domain/LicenseSmsTransaction.cs`
- Delete: `OrderDeck.LicenseServer.Tests/Services/Sms/SmsBalanceConcurrencyTests.cs`
- Modify: `OrderDeck.LicenseServer/Domain/SmsCampaign.cs` (`ReservedCredits` + `RefundedCredits` SİL; sınıf doc'undan kredi cümleleri çıkar)
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` (DbSet'ler + entity config blokları)
- Modify: `OrderDeck.LicenseServer/Program.cs` (satır ~175 DI kaydı)
- Create: göç `RetireSmsCredits`

- [ ] **Step 1: Kalan referansları bul**

```bash
grep -rn "LicenseSmsBalance\|LicenseSmsTransaction\|ReservedCredits\|RefundedCredits" --include=*.cs OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests
```

Görev 3-6 doğru yapıldıysa yalnız: domain dosyaları, DbContext, Program.cs, silinecek servis/testler. Başka çıkan varsa önce onu Görev 3-6 kalıbına göre düzelt.

- [ ] **Step 2: Sil + derle**

Dosyaları sil, `SmsCampaign`'den iki alanı çıkar, DbContext'ten `DbSet<LicenseSmsBalance>`, `DbSet<LicenseSmsTransaction>` ve config bloklarını çıkar, Program.cs DI satırını çıkar.

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: temiz derleme (derlenmezse kaçırılan referans var — sil/uyarla).

- [ ] **Step 3: Göç üret**

```bash
dotnet ef migrations add RetireSmsCredits --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer
```

Üretilen `Up`'ı DOĞRULA: `DropTable(LicenseSmsBalances)`, `DropTable(LicenseSmsTransactions)`, `DropColumn(SmsCampaigns.ReservedCredits)`, `DropColumn(SmsCampaigns.RefundedCredits)`, `AddColumn(SmsCampaignRecipients.ProviderJobId, nvarchar(64), nullable)`. Göç dosyasına `<remarks>` ekle: "Prod'da her iki tablo 0 satır (spec §1.4 ölçümü) — göç değil temiz silme. Down tabloları boş şemayla geri kurar; veri geri gelmez (gelecek veri yok)."

- [ ] **Step 4: Tam suite**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: hepsi PASS (Docker açık).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sms): kredi sistemi emekli — tablolar, servis, alanlar ve göç (§1.4)"
```

---

### Görev 8: WPF istemcisi — kredisiz toplu SMS ekranı

**Files:**
- Modify: `OrderDeck.Licensing/Api/Models/SmsCampaignDtos.cs`
- Modify: `OrderDeck.Licensing/Api/LicenseApiClient.cs` (satır ~462-492)
- Modify: `OrderDeck.App/ViewModels/BulkSmsViewModel.cs`
- Modify: `OrderDeck.App/Views/Pages/BulkSmsPage.xaml`
- Test: `OrderDeck.Tests/ViewModels/BulkSmsViewModelTests.cs`, LicenseApiClient testleri (`grep -rln GetSmsBalanceAsync OrderDeck.Tests`)

- [ ] **Step 1: Failing testler**

`BulkSmsViewModelTests`'i uyarla + ekle:

```csharp
// 1) LoadAsync balance ÇAĞIRMAZ (fake api'de balance çağrısı sayaçla kilitle
//    ya da metodu sil — derleme kilidi yeter), geçmiş yine yüklenir.
// 2) Preview Sufficient=false → StatusMessage Netgsm kurulumuna işaret eder
//    ("Netgsm kurulumu doğrulanmamış" içerir), CanSend false.
// 3) CampaignRow.From: Skipped > 0 → CountsLabel "atlandı" içerir;
//    CreditsRefunded referansı derlenmez (alan DTO'dan gitti).
// 4) StatusLabel("paused") == "Duraklatıldı".
```

- [ ] **Step 2: DTO + istemci**

`SmsCampaignDtos.cs`:
- `SmsBalanceResponse` SİL.
- `SmsPreviewResponse`: alanlar `(int RecipientCount, int SegmentsPerMessage, int TotalCredits, int CreditsRemaining, bool Sufficient)` → `(int RecipientCount, int SegmentsPerMessage, int TotalCredits, bool Sufficient)`; doc: "`Sufficient` = Netgsm kurulumu doğrulanmış mı (server alan adını eski istemciler için koruyor); `CreditsRemaining` JSON'da hâlâ gelir, artık parse edilmez (bilinmeyen alanlar yok sayılır). `TotalCredits` = alıcı × segment (Netgsm segment başına ücretlendirir; 'kaça mal olur' göstergesi)."
- `SmsCampaignStatusResponse` + `SmsCampaignListItem`: `CreditsRefunded` alanını SİL (server 0 sabit gönderiyor; bilinmeyen JSON alanı yok sayılır).

`LicenseApiClient.cs`: `GetSmsBalanceAsync` metodunu SİL.

- [ ] **Step 3: ViewModel**

`BulkSmsViewModel.cs`:
- `_creditsRemaining` property'sini SİL.
- `ReloadBalanceAndHistoryAsync` → `ReloadHistoryAsync` (balance çağrısı gider; üç çağrı yeri güncellenir). `LoadAsync` doc: "lisans çöz + geçmiş yükle".
- `PreviewAsync`: `CreditsRemaining = resp.CreditsRemaining;` satırı gider; mesajlar:

```csharp
            if (resp.RecipientCount == 0)
                StatusMessage = "SMS izinli, bağlı ve telefonu olan müşteri yok.";
            else if (!resp.Sufficient)
                StatusMessage = "Netgsm kurulumu doğrulanmamış — panelden Netgsm bilgilerini girip doğrulayın. Gönderim o zamana dek kapalı.";
            else
                StatusMessage = $"{resp.RecipientCount} alıcı × {resp.SegmentsPerMessage} segment = {resp.TotalCredits} SMS.";
```

- Onay penceresi: `$"Tahmini {TotalCredits} kredi kullanılacak.\n\n"` → `$"Yaklaşık {TotalCredits} SMS (segment) tüketilecek — ücret Netgsm bakiyenden düşer.\n\n"`.
- `StatusLabel`'a `"paused" => "Duraklatıldı",` ekle.
- `CampaignRow.From`:

```csharp
            CountsLabel = $"{d.RecipientCount} alıcı · {d.Sent} gönderildi · {d.Failed} başarısız"
                          + (d.Skipped > 0 ? $" · {d.Skipped} atlandı (İYS)" : ""),
```

- Sınıf doc'undan "Bakiye göstergesi" ve "kredi iadesi otomatik" cümlelerini çıkar; "Ücretlendirme Netgsm tarafında — yayıncı kendi Netgsm bakiyesini kullanır" ekle.

- [ ] **Step 4: XAML**

`BulkSmsPage.xaml`: "Kredi rozeti" `Border`'ını tamamen kaldır (CreditsRemaining binding'i ile birlikte); önizleme özetindeki `<Run Text=" kredi"/>` → `<Run Text=" SMS"/>`.

- [ ] **Step 5: PASS doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` ve `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: PASS + temiz derleme (XAML binding hatası derlemede çıkmaz — `BulkSmsPage.xaml`'da `CreditsRemaining` geçmediğini grep'le doğrula).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.Licensing OrderDeck.App OrderDeck.Tests
git commit -m "feat(wpf): toplu SMS ekranı kredisiz — kurulum kapısı mesajları + skipped (§1.4b)"
```

---

### Görev 9: Kapanış — spec kararları, tam suite, dal push

**Files:**
- Modify: `docs/superpowers/specs/2026-09-18-coklu-yayinci-sms-iys-design.md` (§1.4b + §3.4 açık sorulara karar notu)
- Modify: `docs/superpowers/plans/2026-09-18-coklu-yayinci-kiraci-izolasyonu.md` (Plan 3 durumu)

- [ ] **Step 1: Spec'e karar notları**

§1.4 (b) paragrafının sonuna: "**Karar (Plan 3, 2026-09-21):** uçlar bir sürüm boyunca sabit değerle yaşatılıyor — `/sms/balance` stub (0), `Sufficient` = kurulum doğrulanmış, `CreditsRefunded` = 0. Kaldırma koşulu: saha WPF sürümleri kredisiz istemciye geçtiğinde." §3.4 jobid paragrafına: "**Karar (Plan 3):** `jobid` `SmsCampaignRecipient.ProviderJobId`'de saklanıyor; rapor-takip adımı kurulmadı (§9.3 doğrulanmadan kurulmayacak). Bilinmeyen temiz-ret kodu varsayılanı: kampanya `paused` (kitle korunur)."

- [ ] **Step 2: Tam suite (iki taraf) + WPF build**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

Expected: hepsi yeşil (Docker açık — Testcontainers).

- [ ] **Step 3: Kalıntı taraması**

```bash
grep -rn "LicenseSmsBalance\|LicenseSmsTransaction\|ReservedCredits\|RefundedCredits\|GetSmsBalanceAsync\|SmsBalanceResponse" --include=*.cs --include=*.xaml OrderDeck.LicenseServer OrderDeck.App OrderDeck.Licensing OrderDeck.Core OrderDeck.Tests OrderDeck.LicenseServer.Tests
```

Beklenen kalıntılar YALNIZ: göç dosyaları (tarihî), `LicensesSmsBalanceController` stub'ı (+ testleri) ve spec/plan dokümanları. Başka her şey temizlenir.

- [ ] **Step 4: Commit + push (dal — PR YOK)**

```bash
git add -A
git commit -m "docs(sms-iys): Plan 3 kararları spec'e işlendi"
git push -u origin feat/sms-kredi-emekliligi-kiraci-gonderim
```

**PR AÇMA.** Sıralama: sabah kullanıcı PR #475'i merge eder → bu dal master'a rebase edilir → PR o zaman açılır (yığılmış-PR retarget tuzağı).

---

## Self-Review (yazım sonrası kontrol)

- **Spec kapsaması:** §1.1 (Commercial → NetgsmAccount: Görev 2+3; OTP dokunulmadı), §1.2 (ayrı arayüz + merkezi fırlatma: Görev 2), §1.4 (silinenler + iki taşıyıcı davranış: Görev 4 sözleşme 17, Görev 6 WPF stub, Görev 7 silme, Görev 8 istemci), §3.2 (hesap kapısı: Görev 3+4), §3.3 (skipped: Görev 3), §3.4 (üç sınıf + jobid kararı: Görev 1+3; recovery düzeltmeleri: Görev 5). Sözleşmeler: 4→Görev 3-D, 5→3-A, 6→5-1, 7→2-Step4, 10→3-G, 13→3-C, 17→4-1. ✓
- **Tip tutarlılığı:** `TenantSmsCredentials(UserCode, Password, Header)` Görev 2'de tanımlı, Görev 3 aynı imzayla kullanıyor; `NetgsmErrorClass`/`Classify()` Görev 1'de tanımlı, Görev 3 kullanıyor; `ProviderJobId` Görev 3'te modele giriyor, Görev 7 göçü içeriyor. `TryPauseCampaignAsync`/`Truncate` Görev 3 içinde tanımlı. ✓
- **Placeholder taraması:** tüm kod blokları tam; mevcut test dosyalarının uyarlanmasında "kalıbı oku ve uygula" talimatları dosya adlarıyla verildi (test altyapısı dosyalarda — koddan türetilebilir). ✓
- **Sıralama güvenliği:** her görev sonunda proje derlenir ve testler yeşildir; kredi referansları görevler ilerledikçe azalır, Görev 7'de sıfırlanır. Göç tek sefer, model değişiklikleri bittikten sonra (ApiFactory `EnsureCreated` kullandığı için ara görevlerde göçsüz model sorun çıkarmaz). ✓
