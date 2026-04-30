# Faz 4f — Müşteri Intake Formu + WhatsApp Deep-Link (Tasarım)

**Hedef:** Her lisanslı yayıncıya kalıcı, kişisel bir form linki (`https://license.livedeck.app/r/{slug}`) ver. Müşteri formu doldurur (kullanıcı adı + ad soyad + adres) → "Tamamla" butonu otomatik WhatsApp deep-link ile yayıncının numarasına yönlendirir, mesaj prefilled. Submission paralel olarak `LiveDeck.LicenseServer` DB'sine yazılır; desktop app 2 dakika periyodik polling ile yeni başvuruları çeker ve `Customer` entity oluşturur/günceller. License-bound: yayıncının aktif lisansı yoksa form 410 Gone.

**Kapsam:** `LiveDeck.LicenseServer` + `LiveDeck.Licensing` + `LiveDeck.App` + `LiveDeck.Core` projelerine eklemeler. Yeni proje YOK. Phase 4a/4b/4c/4d/4e ile uyumlu (Phase 4d Pages/Public + Phase 4b LicenseApiClient + Phase 4b HostedService pattern reuse).

**Pre-Faz-4f state:** Phase 4e HEAD `fce951a` (master). 362/362 test (128 LiveDeck + 104 Licensing + 130 LicenseServer). Build 0/0.

---

## 1. Bağlam

**Sorun:** LiveDeck yayıncılarının canlı yayın sırasında müşteri bilgilerini (kullanıcı adı, ad soyad, kargo adresi) toplaması manuel ve hata yapmaya açık. Yayıncı chat'ten DM atmasını ister, müşteri WhatsApp'ta cevaplar, yayıncı bilgileri elle yazar — error-prone, yavaş. Çözüm: kalıcı, kişisel bir form linki + müşterinin kendi WhatsApp'ından prefilled mesaj göndermesi. Yayıncı tek dokunuşla kayda alır.

**4f kapsamı:** Public form (LicenseServer Razor sayfası), per-yayıncı slug + WhatsApp telefon konfigürasyonu, 2dk polling-tabanlı sync, Customer entity Address alanı, Settings dialog yeni "Form Linki" tab. License-bound (lisans expire → 410). Honeypot + IP rate-limit anti-spam.

**4f kapsamında DEĞİL:**
- CAPTCHA (honeypot+rate-limit ilk versiyon için yeter)
- Email confirmation
- Form analytics
- Multi-language (sadece Türkçe)
- Custom branding (logo/renk; sadece `customTitle`)
- Form alanı özelleştirme (4 alan sabit)
- WhatsApp Business API (sadece `wa.me` deep-link)
- Submission moderation queue
- QR kod paylaşımı (kopyala-yapıştır yeter)
- Form preview admin panelinden

---

## 2. Mimari

### 2.1 Solution etkisi

Yeni proje YOK. 4 mevcut projeye dosya eklenir:

```
LiveDeck.LicenseServer/                    (Phase 4a, 4d, 4e — public ASP.NET Core)
├─ Domain/
│  ├─ IntakeFormConfig.cs                  (YENİ)
│  └─ IntakeFormSubmission.cs              (YENİ)
├─ Data/Migrations/{ts}_AddIntakeForm.cs   (YENİ)
├─ Pages/Public/
│  └─ IntakeForm.cshtml + .cs              (YENİ — /r/{slug})
├─ Controllers/
│  └─ IntakeFormController.cs              (YENİ — REST: me/intake-form, me/form-submissions)
└─ Services/IntakeForm/
   ├─ IntakeFormService.cs                 (YENİ — domain orchestration)
   ├─ SlugValidator.cs                     (YENİ — pure validation)
   └─ WhatsAppLinkBuilder.cs               (YENİ — deep-link URL üretici)

LiveDeck.Licensing/                        (Phase 4b — client SDK)
├─ Api/Models/IntakeFormDtos.cs            (YENİ — config + submission DTO'lar)
└─ Api/LicenseApiClient.cs                 MODIFIED (3 yeni metot)

LiveDeck.App/                              (Phase 4b/4c/2a — desktop)
├─ Services/IntakeForm/
│  ├─ IntakeFormSyncService.cs             (YENİ — pull + Customer create/update)
│  └─ IntakeFormSyncHostedService.cs       (YENİ — 2dk PeriodicTimer)
├─ ViewModels/IntakeFormSettingsViewModel.cs (YENİ)
├─ Views/SettingsDialog.xaml               MODIFIED (yeni "Form Linki" tab)
└─ ViewModels/MainShellViewModel.cs        MODIFIED (NewIntakeSubmissionsCount badge)

LiveDeck.Core/                             (Phase 1+ — domain + storage)
├─ Customers/Customer.cs                   MODIFIED (+Address)
└─ Storage/Migrations/006_intake_form_address.sql (YENİ)
```

### 2.2 Stack

| Bileşen | Seçim | Gerekçe |
|---|---|---|
| Form host | LicenseServer Razor Pages (Phase 4d/4e Pages/Public pattern) | Mevcut public server, yeni proje yok |
| Sync | HTTP polling (Phase 4b HeartbeatHostedService pattern) | SignalR'a göre %50 daha ucuz, mevcut HttpClient infra reuse |
| Polling interval | 2 dakika | Form doldurma + WhatsApp send akışı zaten ~1dk; gecikme tolere edilir |
| Anti-spam | Honeypot + IP rate-limit | Sıfır UX friction; CAPTCHA YAGNI |
| Slug yönetimi | Self-service (yayıncı seçer) | Kişisel + hatırlanabilir |
| WhatsApp deep-link | `wa.me` URL | Universal: mobil app açar, desktop web.whatsapp.com açar, hiç yoksa indirme sayfası |
| CSS | Bootstrap 5 CDN | Phase 4d/4e ile uyumlu |
| Form testing | AngleSharp | Phase 4d Razor Pages tests pattern |

### 2.3 URL haritası

| URL | Method | Auth | İşlev |
|---|---|---|---|
| `https://license.livedeck.app/r/{slug}` | GET | Anon | Public form sayfası |
| `https://license.livedeck.app/r/{slug}/submit` | POST | Anon (rate-limit + honeypot) | Form submission → 302 wa.me |
| `https://license.livedeck.app/api/v1/me/intake-form` | GET | Bearer-Customer | Yayıncı'nın config'i (slug, phone, isActive) |
| `https://license.livedeck.app/api/v1/me/intake-form` | PUT | Bearer-Customer | Config claim/update |
| `https://license.livedeck.app/api/v1/me/form-submissions?since={iso8601}&limit=50` | GET | Bearer-Customer | Polling cursor pagination |

### 2.4 Veri akışı (10 adım)

```
1. Yayıncı (desktop) → Settings → "Form Linki" tab → slug "burakstreamer" + phone "+905551234567" → "Kaydet"
2. Desktop app → PUT /api/v1/me/intake-form → server uniqueness check + persist → 200 OK
3. Yayıncı: link = "https://license.livedeck.app/r/burakstreamer" — Settings'ten kopyala
4. Yayıncı linki müşteriye paylaşır (chat, story, biyo, vs.)
5. Müşteri tarayıcıda link aç → form sayfası mobile-first Bootstrap → 4 alan doldur (kullanıcı adı, ad soyad, adres, honeypot gizli)
6. Müşteri "Tamamla ve WhatsApp'tan Gönder" → POST /r/burakstreamer/submit
7. Server: license aktif mi check → submission persist → WhatsApp URL üret → 302 redirect
8. Tarayıcı 302 → wa.me/905551234567?text=... → WhatsApp uygulaması açılır mesaj prefilled
9. Müşteri WhatsApp'ta "Gönder" tıklar → mesaj kendi numarasından yayıncıya gider
10. Paralel: 2 dakika sonra desktop app → GET /api/v1/me/form-submissions?since=lastSync → submission JSON → Customer entity create/update + MainShell badge artar
```

### 2.5 License-bound policy

Yayıncı'nın aktif lisansı yoksa (Phase 4a `Licenses` tablosunda `WHERE CustomerId = configOwner AND RevokedAt IS NULL AND ExpiresAt > UtcNow` kaydı yok):

| Endpoint | Lisans aktif | Lisans yok/expire/revoke |
|---|---|---|
| GET /r/{slug} | 200 form | 410 Gone "Form geçici kapalı" |
| POST /r/{slug}/submit | 302 wa.me | 410 Gone |
| GET /me/intake-form | 200 config | 200 config (yayıncı kendi durumunu görebilir) |
| PUT /me/intake-form | 200 update | 200 update (yayıncı önceden konfigüre edebilir) |
| GET /me/form-submissions | 200 (boş veya mevcut) | 200 (mevcut görünür, yeni gelmiyor) |

License yenilenince form otomatik açılır.

---

## 3. Form Alanları + WhatsApp Deep-Link

### 3.1 Form alanları

| Alan | Property | Tip | Validation | WhatsApp mesajında |
|---|---|---|---|---|
| Kullanıcı adı | `Username` | text, max 64 | Required, trim | "Kullanıcı adı: {x}" |
| Ad Soyad | `FullName` | text, max 200 | Required, trim, ≥1 boşluk | "Ad Soyad: {y}" |
| Adres | `Address` | textarea, max 500 | Required, trim | "Adres: {z}" |
| Honeypot | `website` | hidden text | Boş olmalı; doluysa silent reject | — |

### 3.2 Form layout (mobile-first)

`Pages/Public/IntakeForm.cshtml`:
```cshtml
@page "/r/{slug}"
@model IntakeFormModel
@{
    Layout = null;
    ViewData["Title"] = Model.Config?.CustomTitle ?? "Bilgi Gönder";
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>@ViewData["Title"]</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css">
</head>
<body class="bg-light">
<main class="container py-4" style="max-width: 480px;">
    <h2 class="mb-4">@Model.Config!.CustomTitle</h2>
    <form method="post" asp-page-handler="Submit" autocomplete="off">
        <input type="hidden" asp-for="Slug" />
        <div class="mb-3">
            <label asp-for="Input.Username" class="form-label">Kullanıcı adı (Instagram/TikTok)</label>
            <input asp-for="Input.Username" class="form-control form-control-lg" required maxlength="64">
        </div>
        <div class="mb-3">
            <label asp-for="Input.FullName" class="form-label">Ad Soyad</label>
            <input asp-for="Input.FullName" class="form-control form-control-lg" required maxlength="200">
        </div>
        <div class="mb-3">
            <label asp-for="Input.Address" class="form-label">Kargo Adresi</label>
            <textarea asp-for="Input.Address" class="form-control" rows="3" required maxlength="500"></textarea>
        </div>
        <input type="text" name="website" tabindex="-1" autocomplete="off"
               style="position:absolute;left:-9999px;display:none">
        <button type="submit" class="btn btn-success btn-lg w-100">Tamamla ve WhatsApp'tan Gönder</button>
    </form>
</main>
</body>
</html>
```

### 3.3 WhatsApp deep-link

**`WhatsAppLinkBuilder.Build`:**
```csharp
public string Build(string e164Phone, string username, string fullName, string address)
{
    var phone = e164Phone.TrimStart('+').Replace(" ", "").Replace("-", "");
    var msg = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}";
    return $"https://wa.me/{phone}?text={Uri.EscapeDataString(msg)}";
}
```

`Uri.EscapeDataString` `\n` → `%0A`. WhatsApp `%0A`'ı gerçek satır sonu olarak render eder.

**Mesaj örneği (URL decode):**
```
Kullanıcı adı: bilalcanli
Ad Soyad: Bilal Canlı
Adres: Atatürk Mah. Cumhuriyet Cad. No:12/4 Beyoğlu/İstanbul
```

### 3.4 Submit handler

```csharp
public async Task<IActionResult> OnPostSubmitAsync(string slug, CancellationToken ct)
{
    // Honeypot — bot doldurursa silent 200 (ama asla persist etme + asla redirect etme)
    if (!string.IsNullOrEmpty(Request.Form["website"]))
    {
        _log.LogInformation("Honeypot triggered for slug {Slug}", slug);
        return Page(); // sessizce form sayfasına geri
    }

    if (!ModelState.IsValid) return Page();

    var config = await _intakeService.GetActiveBySlugAsync(slug, ct);
    if (config is null) return StatusCode(StatusCodes.Status410Gone);

    var submission = await _intakeService.SaveSubmissionAsync(config.Id,
        Input.Username.Trim(), Input.FullName.Trim(), Input.Address.Trim(),
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        ct);

    var url = _linkBuilder.Build(config.WhatsAppPhone,
        Input.Username.Trim(), Input.FullName.Trim(), Input.Address.Trim());
    return Redirect(url);
}
```

### 3.5 Mobile davranış

- **iOS Safari + Android Chrome:** `wa.me` link tıklayınca WhatsApp uygulaması açılır, mesaj alanı dolu, kullanıcı sadece ✓ Gönder
- **Desktop tarayıcı:** `web.whatsapp.com` açılır (eğer kullanıcı oturum açıksa)
- **WhatsApp yüklü değil:** `wa.me` "WhatsApp'ı yükle" sayfası gösterir (graceful)
- **Telefonda boş ekran:** Bazı eski Android'lerde `wa.me` çalışmaz → kullanıcı manuel WhatsApp açar (edge case, %1)

---

## 4. Slug + Telefon Konfigürasyonu

### 4.1 IntakeFormConfig entity

```csharp
public sealed class IntakeFormConfig
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Slug { get; set; } = "";              // unique, lowercase
    public string WhatsAppPhone { get; set; } = "";     // E.164 with leading +
    public string? CustomTitle { get; set; }            // form sayfasında başlık
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

**FluentAPI:**
- `HasIndex(c => c.Slug).IsUnique()` (case-insensitive collation)
- `HasOne(c => c.Customer).WithOne().HasForeignKey<IntakeFormConfig>(c => c.CustomerId).OnDelete(Cascade)` — 1:1 relation
- `Slug` max 32, `WhatsAppPhone` max 20, `CustomTitle` max 100

### 4.2 SlugValidator

```csharp
public static class SlugValidator
{
    private static readonly Regex Pattern =
        new(@"^[a-z0-9](?:[a-z0-9-]{1,30}[a-z0-9])?$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "hangfire", "me", "r", "unsubscribe",
        "password-reset", "auth", "login", "logout", "null",
        "undefined", "app", "assets", "static", "livedeck"
    };

    public static SlugValidationResult Validate(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return SlugValidationResult.Empty;
        if (slug.Length < 3 || slug.Length > 32) return SlugValidationResult.InvalidLength;
        if (!Pattern.IsMatch(slug)) return SlugValidationResult.InvalidFormat;
        if (Reserved.Contains(slug)) return SlugValidationResult.Reserved;
        return SlugValidationResult.Valid;
    }
}

public enum SlugValidationResult { Valid, Empty, InvalidLength, InvalidFormat, Reserved }
```

### 4.3 WhatsApp telefon validasyonu

E.164 regex: `^\+[1-9]\d{6,14}$`. Toplam 8-16 char (`+` dahil). Country code first digit non-zero.

### 4.4 REST endpoint'leri

**GET /api/v1/me/intake-form** — Bearer-Customer
- Customer'ın config'i (varsa). 404 if yok.
- Response: `{ slug, whatsAppPhone, customTitle, isActive, formUrl }`

**PUT /api/v1/me/intake-form** — Bearer-Customer
- Body: `{ slug, whatsAppPhone, customTitle?, isActive? }`
- Validation: SlugValidator + E.164 phone. Hatalar 400 + Problem details (`title` = error code).
- Server check: slug uniqueness (`UNIQUE INDEX` aşılırsa 409 `slug-already-taken`).
- Idempotent: aynı slug yeniden PUT → 200, fields update.

### 4.5 Settings dialog yeni tab

`SettingsDialog.xaml` (Phase 2a tabbed) — yeni `<TabItem Header="Form Linki">`:

```xml
<TabItem Header="Form Linki">
    <StackPanel Margin="16">
        <TextBlock Text="Müşteri Bilgi Formu" FontWeight="Bold" FontSize="16" Margin="0,0,0,12"/>

        <Label Content="Slug (URL'in son parçası)"/>
        <TextBox Text="{Binding Slug, UpdateSourceTrigger=PropertyChanged}"/>
        <TextBlock Text="Sadece küçük harf, rakam, tire. 3-32 karakter."
                   Foreground="Gray" FontSize="11"/>

        <Label Content="WhatsApp telefon" Margin="0,8,0,0"/>
        <TextBox Text="{Binding WhatsAppPhone, UpdateSourceTrigger=PropertyChanged}"
                 ToolTip="+905551234567 formatında"/>

        <Label Content="Form başlığı (opsiyonel)" Margin="0,8,0,0"/>
        <TextBox Text="{Binding CustomTitle, UpdateSourceTrigger=PropertyChanged}"
                 ToolTip="Örn: 'Burak'a Bilgi Gönder'"/>

        <CheckBox Content="Form aktif" IsChecked="{Binding IsActive}" Margin="0,8,0,0"/>

        <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
            <Button Content="Kaydet" Command="{Binding SaveCommand}" Padding="16,6"/>
            <Button Content="Linki Kopyala" Command="{Binding CopyLinkCommand}"
                    Padding="16,6" Margin="8,0,0,0"
                    IsEnabled="{Binding HasFormUrl}"/>
        </StackPanel>

        <TextBlock Text="{Binding FormUrl}" FontFamily="Consolas" Margin="0,8,0,0"
                   TextWrapping="Wrap" Foreground="DodgerBlue"/>
        <TextBlock Text="{Binding StatusMessage}" Margin="0,4,0,0"
                   Foreground="{Binding StatusBrush}"/>
    </StackPanel>
</TabItem>
```

`IntakeFormSettingsViewModel`:
- `LoadAsync()` — GET çağrısı, mevcut config varsa fields populate
- `SaveCommand` — PUT çağrısı, validation hata → kırmızı `StatusMessage`, başarı → yeşil
- `CopyLinkCommand` — `Clipboard.SetText(FormUrl)` + "Kopyalandı!" toast

**Edge case'ler:**
- Slug değişikliği → uyarı: "Linki değiştirirsen eski paylaştığın bağlantılar 404 olur. Devam?"
- IsActive=false → server submit 410 + form sayfası 410

---

## 5. Polling + Customer Sync

### 5.1 IntakeFormSubmission entity

```csharp
public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
```

**FluentAPI:**
- FK `IntakeFormConfigId` cascade
- `HasIndex(s => new { s.IntakeFormConfigId, s.SubmittedAt })` — polling cursor query için
- `Username` max 64, `FullName` max 200, `Address` max 500, `IpAddress` max 64, `UserAgent` max 500

### 5.2 Polling endpoint

**GET /api/v1/me/form-submissions?since=2026-04-30T12:00:00Z&limit=50** — Bearer-Customer

```csharp
[HttpGet("form-submissions")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public async Task<IActionResult> GetSubmissions(
    [FromQuery] DateTimeOffset? since,
    [FromQuery] int limit = 50,
    CancellationToken ct = default)
{
    if (limit < 1 || limit > 200) limit = 50;
    var customerId = GetCustomerId();
    var sinceUtc = since ?? DateTimeOffset.MinValue;

    var rows = await _db.IntakeFormSubmissions
        .Where(s => s.Config.CustomerId == customerId && s.SubmittedAt > sinceUtc)
        .OrderBy(s => s.SubmittedAt)
        .Take(limit)
        .Select(s => new IntakeFormSubmissionDto(
            s.Id, s.Username, s.FullName, s.Address, s.SubmittedAt))
        .ToListAsync(ct);

    return Ok(rows);
}
```

### 5.3 Desktop app: IntakeFormSyncService

```csharp
public sealed class IntakeFormSyncService
{
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;

    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var lastSync = _settingsStore.LastIntakeFormSync;
        List<IntakeFormSubmissionDto> submissions;
        try
        {
            submissions = await _api.GetFormSubmissionsAsync(lastSync, limit: 50, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Intake form sync failed");
            return 0;
        }

        if (submissions.Count == 0) return 0;

        var nowUnix = _clock.UnixNow();
        foreach (var sub in submissions.OrderBy(s => s.SubmittedAt))
        {
            _customers.UpsertFromIntakeForm(sub.Username, sub.FullName, sub.Address, nowUnix);
            _settingsStore.LastIntakeFormSync = sub.SubmittedAt;
        }
        _settingsStore.Save();
        return submissions.Count;
    }
}
```

### 5.4 IntakeFormSyncHostedService

Phase 4b `HeartbeatHostedService` pattern:

```csharp
public sealed class IntakeFormSyncHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    // Ctor: IntakeFormSyncService + ILogger
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try { await _syncService.SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Intake form sync tick failed"); }
        }
    }
}
```

### 5.5 Customer dedup

`CustomerRepository.UpsertFromIntakeForm(username, fullName, address, nowUnix)`:
1. `GetByPlatformAndUsername("form", username)` (yeni metot — Phase 1+ pattern)
2. Bulundu → `UpdateLastSeenAtAndAddress(id, fullName, address, nowUnix)`
3. Bulunamadı → `Insert(platform="form", username, displayName=fullName, address, firstSeen=now, lastSeen=now)`

`Platform = "form"` yeni kategori (mevcut `instagram`, `tiktok` yanına). LiveDeck'in chat-merkezli model'i için extension. CustomerSearchDialog (Phase 3a) Platform filter'ında "form" da görünür.

### 5.6 Customer.Address alanı (LiveDeck.Core)

`Customer.cs` 14. alan eklenir:
```csharp
public sealed record Customer(
    string Id, string Platform, string Username, string? DisplayName,
    string? AvatarUrl, long FirstSeenAt, long LastSeenAt,
    bool IsBlacklisted, string? BlacklistReason, string? Notes,
    int TotalLabelsPrinted, decimal TotalAmount, long? BlacklistedAt,
    string? Address);   // YENİ Phase 4f
```

**Migration `006_intake_form_address.sql`:**
```sql
-- Phase 4f: address from intake form submissions
ALTER TABLE Customer ADD COLUMN Address TEXT;
UPDATE _meta SET SchemaVersion = 6 WHERE Id = 1;
```

### 5.7 SettingsStore yeni alan

`SettingsStore` (Phase 1+):
```csharp
public DateTimeOffset? LastIntakeFormSync { get; set; }
```

Persist: appsettings.json içinde `IntakeForm.LastSync` alanı. Phase 4b `auth.dat` / `license.dat` ile aynı klasör, plain JSON file zaten var.

### 5.8 MainShell badge

`MainShellViewModel` yeni property:
```csharp
[ObservableProperty] private int _newIntakeSubmissionsCount;
```

`IntakeFormSyncService` her `UpsertFromIntakeForm` çağrısı sonrası `NewIntakeSubmissionsCount++`. UI thread dispatch (Phase 4b pattern).

XAML:
```xml
<Border Background="Crimson" CornerRadius="10" Padding="6,2"
        Visibility="{Binding NewIntakeSubmissionsCount, Converter={StaticResource ZeroToCollapsed}}">
    <TextBlock Text="{Binding NewIntakeSubmissionsCount, StringFormat='{}🔔 {0} yeni başvuru'}"
               Foreground="White" FontSize="11" FontWeight="Bold"
               Cursor="Hand"
               MouseLeftButtonUp="OpenIntakeSubmissions"/>
</Border>
```

Tıklayınca CustomerSearchDialog (Phase 3a) Platform=`form` filtreyle açılır + `NewIntakeSubmissionsCount = 0` reset.

---

## 6. Anti-Spam

### 6.1 Honeypot

Form'da hidden alan `name="website"`. Submit handler ilk kontrol: doluysa silent 200 (return Page) — bot bilgi alamaz.

### 6.2 IP rate-limit

Phase 4a rate limiter mevcut. Yeni policy:
```csharp
opt.AddPolicy("intake-form-submit", ctx =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromHours(1)
        }));
```

`POST /r/{slug}/submit` üzerinde `[EnableRateLimiting("intake-form-submit")]`. Aşılırsa 429.

### 6.3 Validation

| Durum | Response |
|---|---|
| Username/FullName/Address boş | 400 (form sayfası kalır, validation summary) |
| Maxlength aşımı | 400 (client-side de engellenir maxlength attr ile) |
| Slug bulunamadı | 404 |
| License inactive / form IsActive=false | 410 Gone |
| Honeypot doluysa | 200 silent |
| Rate-limit aşıldı | 429 |

---

## 7. Konfigürasyon

### 7.1 LicenseServer appsettings.json

Mevcut Phase 4a config korunur. Yeni section gerekmez (`App:PublicBaseUrl` zaten var, `formUrl` üretimi bunu kullanır).

### 7.2 Env override

- `LIVEDECK_INTAKE_RATELIMIT_PER_HOUR` (default 5) — testlerde 1000 yapılıp test akışı bozulmaz.

### 7.3 Desktop app

Yeni env yok. `IntakeFormSyncHostedService` interval `2 dk` sabit (override gerekmiyor — production polling).

---

## 8. Test Stratejisi

| Dosya | Tip | Sayı |
|---|---|---|
| `LicenseServer.Tests/Domain/IntakeFormConfigTests.cs` | Smoke | 1 |
| `LicenseServer.Tests/Services/IntakeForm/SlugValidatorTests.cs` | Unit (pure) | 6 (valid, empty, too short, too long, invalid format, reserved) |
| `LicenseServer.Tests/Services/IntakeForm/WhatsAppLinkBuilderTests.cs` | Unit (pure) | 4 (basic build, phone format clean, special char escape, multiline) |
| `LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs` | Integration (DbContext) | 7 (claim slug, uniqueness, update, license check active, license expired, save submission, get since cursor) |
| `LicenseServer.Tests/Controllers/IntakeFormControllerTests.cs` | Integration (REST) | 6 (GET 404, GET 200, PUT 200, PUT 409 conflict, PUT 400 invalid, polling submissions) |
| `LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs` | Integration (AngleSharp) | 6 (GET form, POST → 302 wa.me, POST honeypot → 200 silent, POST validation 400, license expired 410, slug not found 404) |
| `LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncServiceTests.cs` | Integration | 5 (pull empty, pull + customer create, pull + customer update existing, cursor advance, network fail tolerated) |
| `LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncHostedServiceTests.cs` | Integration | 2 (timer fires + retry on fail) |

**Toplam:** ~37 yeni test (LicenseServer 30 + LiveDeck.Tests 7).

**Hedef:** 362 baseline → ~399.

---

## 9. Manuel Smoke Plan

1. LicenseServer + Phase 4a-4e infrastructure ayağa kalkmış
2. Yayıncı desktop app açar → Settings → "Form Linki" tab
3. Slug `burak-test` + phone `+905551234567` + title "Burak Test Form" → Kaydet → 200 OK + URL görünür
4. URL'i mobilde aç (gerçek phone) → Bootstrap form render → 4 alan + button
5. Form doldur (kullanıcı adı `bilalcanli`, ad soyad `Bilal Canlı`, adres `Atatürk Cad. No:12 İstanbul`) → Tamamla
6. WhatsApp uygulaması açılır → mesaj prefilled (3 satır) → Gönder
7. Yayıncı'nın WhatsApp'ı: müşterinin kendi numarasından gelen mesaj görünür ✓
8. Desktop app 2dk bekle (veya app restart) → MainShell top-bar'da `🔔 1 yeni başvuru` badge
9. Badge'e tıkla → CustomerSearchDialog Platform=form filter → `bilalcanli` görünür → detay aç → adres alanı dolu
10. Postman/curl → POST aynı slug'a 6. submission → 429 Too Many Requests
11. Postman/curl → POST `?website=bot` honeypot → 200 OK, ama DB'de submission yok ✓
12. Admin panelden yayıncı'nın license'ını revoke → `/r/burak-test` → 410 Gone
13. License yenile → form tekrar 200

---

## 10. YAGNI

- CAPTCHA (Cloudflare Turnstile, reCAPTCHA)
- Email confirmation submit sonrası
- Form analytics (open rate, completion rate)
- Multi-language
- Custom branding (logo, renk)
- Form alanı özelleştirme (yayıncı alan ekle/sil)
- Submission limit per slug (quota)
- WhatsApp Business API (template approval gerek)
- Submission moderation queue
- QR kod üretimi (mevcut Settings'de copy yeter)
- Form preview admin'den
- Slug değişiklik tarihi audit
- Slug history (eski sluglar redirect)
- Bulk submission export (CSV)
- Auto-customer-merge if Username matches existing chat-platform customer

---

## 11. Phase 4f Sonrası Açık

- **Phase 5:** Stripe/PayTR webhook + form integration (form submit + ödeme aynı flow)
- Phase sonra: CAPTCHA, multi-language, custom branding, custom alanlar, QR kod, form analytics

---

## 12. Kabul Kriterleri

- ✅ Build temiz: 0 error / 0 warning
- ✅ ~399 test pass (362 baseline + ~37 yeni)
- ✅ Yayıncı desktop app Settings → "Form Linki" tab → slug "burakstreamer" + phone "+905551234567" → server'da unique reserve
- ✅ Public URL `https://license.livedeck.app/r/burakstreamer` mobilde Bootstrap responsive form
- ✅ Form doldur + Tamamla → 302 wa.me/905551234567?text=... → WhatsApp uygulaması mesaj prefilled
- ✅ Honeypot doluysa silent 200 (submission persist YOK)
- ✅ Aynı IP 5+ submission/saat → 6. 429
- ✅ Yayıncı'nın aktif lisansı yoksa form 410
- ✅ Polling 2dk: desktop app yeni submission'ları çeker, Customer create veya update
- ✅ Customer.Address alanı eklenir (Core migration 006), CustomerDetailDialog'da görünür
- ✅ MainShell badge yeni başvuru sayısını gösterir, tıklayınca CustomerSearchDialog Platform=form
- ✅ Slug değişikliği eski URL'i 404 yapar (uyarı UI'da)
- ✅ Phase 4a/4b/4c/4d/4e (362 test) regression: bozulmaz
- ✅ License revoke/expire → form 410; license yenile → otomatik 200
