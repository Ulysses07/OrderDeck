# Müşteri (Shopper) App — Faz 0c-2: WPF UI + Sync Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Goal:** WPF App tarafında Faz 0c-1 server endpoint'lerinin (PR #84) tüketimi: Settings dialog'a "Müşteri App" sekmesi (shopper code yönet) + IBAN/AccountHolder otomatik sync + WPF lokal Customer kayıtlarının periyodik projection sync'i.

**Architecture:** `LicenseApiClient`'a 4 yeni method. SettingsDialog.xaml'a yeni TabItem. SettingsViewModel'e shopper code + sync trigger property/command'leri. Yeni `PaymentAccountSyncService` (event-driven: settings değişince + startup'ta) ve `WpfCustomerProjectionSyncHostedService` (periodic, mevcut PaymentSyncHostedService pattern'i).

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, EF Core SQLite (lokal), Microsoft.Extensions.Hosting.

---

## File Structure

**Modified:**
- `OrderDeck.Licensing/Api/LicenseApiClient.cs` — 4 yeni method + request/response models
- `OrderDeck.App/Views/SettingsDialog.xaml` — yeni TabItem "Müşteri App"
- `OrderDeck.App/ViewModels/SettingsViewModel.cs` — ShopperCode property + Load/Save command'leri
- `OrderDeck.App/Services/Settings/SettingsStore.cs` (veya save handler) — Iban/AccountHolder değişince trigger event
- `OrderDeck.App/AppHost.cs` — yeni 2 hosted service DI

**Created:**
- `OrderDeck.App/Services/Sync/PaymentAccountSyncService.cs` + `PaymentAccountSyncHostedService.cs`
- `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` + `WpfCustomerProjectionSyncHostedService.cs`
- `OrderDeck.Tests/Sync/PaymentAccountSyncServiceTests.cs`
- `OrderDeck.Tests/Sync/WpfCustomerProjectionSyncServiceTests.cs`
- Optional: `AppSettings.LastCustomerSyncAt` field (long unix seconds) for delta-sync watermark

---

## Task 1: LicenseApiClient yeni metodlar

**File**: `OrderDeck.Licensing/Api/LicenseApiClient.cs` + (probably) `OrderDeck.Licensing/Api/Models/*.cs`

4 yeni method:

```csharp
// 1. Shopper code GET
public sealed record ShopperCodeResponse(
    string? Code, DateTimeOffset? UpdatedAt, DateTimeOffset? CanChangeAt, Guid LicenseId);

public Task<ShopperCodeResponse> GetShopperCodeAsync(CancellationToken ct = default)
    => GetAsync<ShopperCodeResponse>("/api/panel/shopper-code", ct);

// 2. Shopper code PUT — returns updated response OR throws on validation error
public sealed record SetShopperCodeRequest(string Code);

/// <returns>
/// Updated response on success. On 400, throws <see cref="ShopperCodeValidationException"/>
/// with the server's error code ("empty"/"length"/"format"/"reserved"/"profanity"/"cooldown"/"taken").
/// </returns>
public async Task<ShopperCodeResponse> SetShopperCodeAsync(string code, CancellationToken ct = default)
{
    var resp = await _http.PutAsJsonAsync("/api/panel/shopper-code",
        new SetShopperCodeRequest(code), ct);
    if (resp.StatusCode == HttpStatusCode.BadRequest)
    {
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        throw new ShopperCodeValidationException(problem?.Title ?? "unknown");
    }
    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<ShopperCodeResponse>(ct))!;
}

public sealed class ShopperCodeValidationException : Exception
{
    public string ErrorCode { get; }
    public ShopperCodeValidationException(string errorCode) : base(errorCode) => ErrorCode = errorCode;
}

// 3. IBAN + AccountHolder sync
public sealed record SetPaymentAccountRequest(string? Iban, string? AccountHolder);

public async Task SyncPaymentAccountAsync(
    Guid licenseId, string? iban, string? accountHolder, CancellationToken ct = default)
{
    var resp = await _http.PostAsJsonAsync(
        $"/api/v1/licenses/{licenseId}/payment-account",
        new SetPaymentAccountRequest(iban, accountHolder), ct);
    resp.EnsureSuccessStatusCode();
}

// 4. WPF customers bulk sync
public sealed record WpfCustomerSyncItem(
    Guid Id, string Platform, string Username,
    string? FullName, string? Phone, string? Address,
    DateTimeOffset UpdatedAt);

public sealed record WpfCustomerSyncRequest(List<WpfCustomerSyncItem> Customers);
public sealed record WpfCustomerSyncResponse(int Synced, int RetroactiveMatches);

public async Task<WpfCustomerSyncResponse> SyncWpfCustomersAsync(
    Guid licenseId, IReadOnlyList<WpfCustomerSyncItem> customers, CancellationToken ct = default)
{
    var resp = await _http.PostAsJsonAsync(
        $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
        new WpfCustomerSyncRequest(customers.ToList()), ct);
    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<WpfCustomerSyncResponse>(ct))!;
}
```

**Tests**: Mock HttpClient handler; verify URL + method + body shape for each. Existing test pattern in `OrderDeck.Tests/Licensing/LicenseApiClientTests.cs` (if exists; if not, create).

**Commit**: `feat(api-client): shopper-code + payment-account + wpf-customers sync methods`

---

## Task 2: Settings dialog "Müşteri App" tab

**File**: `OrderDeck.App/Views/SettingsDialog.xaml` + `OrderDeck.App/ViewModels/SettingsViewModel.cs`

### XAML — Yeni TabItem (Ödeme tab'ından sonra)

Yeni section örnek:

```xml
<TabItem Header="Müşteri App" DataContext="{Binding ShopperApp}">
    <StackPanel Margin="16">
        <TextBlock TextWrapping="Wrap" Margin="0,0,0,12">
            Müşteri davet kodu — müşterilerine paylaşacağın "yayıncı kodu".
            Müşteri app'ten bu kodu girip seni takip etmeye başlar.
        </TextBlock>

        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0"
                     Text="{Binding CodeInput, UpdateSourceTrigger=PropertyChanged}"
                     MaxLength="20"
                     IsEnabled="{Binding CanEditCode}"
                     ToolTip="3-20 karakter, küçük harf ve rakam (örn. royal, mezat42)"/>
            <Button Grid.Column="1" Margin="8,0,0,0" Padding="12,4"
                    Content="Kaydet"
                    Command="{Binding SaveCommand}"/>
        </Grid>

        <TextBlock Foreground="DarkRed"
                   Visibility="{Binding ErrorMessage, Converter={StaticResource NullOrEmptyToCollapsedConverter}}"
                   Text="{Binding ErrorMessage}"
                   Margin="0,0,0,8"/>

        <TextBlock Foreground="Gray"
                   Visibility="{Binding CooldownMessage, Converter={StaticResource NullOrEmptyToCollapsedConverter}}"
                   Text="{Binding CooldownMessage}"
                   Margin="0,0,0,8"/>

        <TextBlock Margin="0,12,0,4" FontWeight="SemiBold">Kurallar:</TextBlock>
        <TextBlock TextWrapping="Wrap" Foreground="Gray" Margin="0,0,0,4">
            • 3-20 karakter, sadece a-z ve 0-9
        </TextBlock>
        <TextBlock TextWrapping="Wrap" Foreground="Gray" Margin="0,0,0,4">
            • Bir kere belirlenince 7 gün boyunca değiştirilemez
        </TextBlock>
        <TextBlock TextWrapping="Wrap" Foreground="Gray" Margin="0,0,0,4">
            • Herkese açık kodlar dünyada tektir (`admin`, `support` gibi kelimeler kullanılamaz)
        </TextBlock>
    </StackPanel>
</TabItem>
```

Note: If `NullOrEmptyToCollapsedConverter` doesn't exist, use `Visibility="{Binding HasError, Converter={StaticResource BooleanToVisibilityConverter}}"` pattern instead — check existing XAML for available converters.

### ViewModel — `ShopperAppSettingsViewModel` (new file, or nested in SettingsViewModel)

```csharp
public sealed partial class ShopperAppSettingsViewModel : ObservableObject
{
    private readonly LicenseApiClient _api;
    private readonly ILogger<ShopperAppSettingsViewModel> _log;

    [ObservableProperty] private string _codeInput = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _cooldownMessage;
    [ObservableProperty] private bool _canEditCode = true;
    [ObservableProperty] private bool _isLoading;

    private DateTimeOffset? _canChangeAt;

    public ShopperAppSettingsViewModel(LicenseApiClient api, ILogger<ShopperAppSettingsViewModel> log)
    {
        _api = api; _log = log;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            var resp = await _api.GetShopperCodeAsync(ct);
            CodeInput = resp.Code ?? "";
            _canChangeAt = resp.CanChangeAt;
            UpdateCooldownState();
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            ErrorMessage = "Lisans bulunamadı. Önce aktivasyon yap.";
            CanEditCode = false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ShopperCode load failed");
            ErrorMessage = "Sunucuya bağlanılamadı. Daha sonra dene.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateCooldownState()
    {
        if (_canChangeAt is { } at && at > DateTimeOffset.UtcNow)
        {
            CanEditCode = false;
            var remaining = at - DateTimeOffset.UtcNow;
            CooldownMessage = remaining.TotalDays >= 1
                ? $"Yeniden değiştirebileceğin tarih: {at.LocalDateTime:dd.MM.yyyy HH:mm} ({(int)remaining.TotalDays} gün kaldı)"
                : $"Yeniden değiştirebileceğin tarih: {at.LocalDateTime:dd.MM.yyyy HH:mm} ({(int)remaining.TotalHours} saat kaldı)";
        }
        else
        {
            CanEditCode = true;
            CooldownMessage = null;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(CodeInput))
        {
            ErrorMessage = "Kod boş olamaz.";
            return;
        }
        try
        {
            IsLoading = true;
            var resp = await _api.SetShopperCodeAsync(CodeInput.Trim().ToLowerInvariant(), default);
            CodeInput = resp.Code ?? "";
            _canChangeAt = resp.CanChangeAt;
            UpdateCooldownState();
        }
        catch (ShopperCodeValidationException ex)
        {
            ErrorMessage = ex.ErrorCode switch
            {
                "empty"      => "Kod boş olamaz.",
                "length"     => "Kod 3-20 karakter olmalı.",
                "format"     => "Sadece küçük harf ve rakam (a-z, 0-9).",
                "reserved"   => "Bu kelime sistem tarafından ayrılmış.",
                "profanity"  => "Bu kelime uygun değil.",
                "cooldown"   => "Henüz 7 günlük bekleme süresi dolmadı.",
                "taken"      => "Bu kod başka bir yayıncı tarafından kullanılıyor.",
                _            => $"Bilinmeyen hata: {ex.ErrorCode}",
            };
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Sunucu hatası ({ex.StatusCode}). Daha sonra dene.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ShopperCode save failed");
            ErrorMessage = "Beklenmedik hata. Logları kontrol et.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
```

Add to existing `SettingsViewModel` (parent VM):
```csharp
public ShopperAppSettingsViewModel ShopperApp { get; }

// In constructor — inject LicenseApiClient
public SettingsViewModel(... LicenseApiClient api, ...)
{
    // ...
    ShopperApp = new ShopperAppSettingsViewModel(api, ...);
}

// In an existing OnLoaded or OnShow method, call:
// await ShopperApp.LoadAsync(default);
```

**Tests**: Mock LicenseApiClient; verify Save command transitions ErrorMessage on each errorCode value.

**Commit**: `feat(wpf-settings): Müşteri App tab + shopper code editor`

---

## Task 3: IBAN/AccountHolder otomatik sync

When user saves Settings dialog with Iban or AccountHolder changed → push to server.

**Strategy:** Settings save observer. Look at existing `SettingsStore` save flow:
- If there's a "save completed" event, hook into it
- Else, modify the save method to invoke the sync

**File**: `OrderDeck.App/Services/Sync/PaymentAccountSyncService.cs` (new)

```csharp
public sealed class PaymentAccountSyncService
{
    private readonly LicenseApiClient _api;
    private readonly SettingsStore _settingsStore;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<PaymentAccountSyncService> _log;

    private string? _lastSyncedIban;
    private string? _lastSyncedAccountHolder;

    public PaymentAccountSyncService(
        LicenseApiClient api,
        SettingsStore settingsStore,
        ICurrentLicenseProvider licenseProvider,
        ILogger<PaymentAccountSyncService> log)
    {
        _api = api;
        _settingsStore = settingsStore;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    public async Task SyncIfChangedAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        var licenseId = _licenseProvider.GetActiveLicenseId();
        if (licenseId is null) return;

        if (settings.Iban == _lastSyncedIban && settings.AccountHolder == _lastSyncedAccountHolder)
            return; // No change

        try
        {
            await _api.SyncPaymentAccountAsync(licenseId.Value, settings.Iban, settings.AccountHolder, ct);
            _lastSyncedIban = settings.Iban;
            _lastSyncedAccountHolder = settings.AccountHolder;
            _log.LogInformation("PaymentAccount synced (iban={IbanLen} chars)", settings.Iban?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PaymentAccount sync failed; will retry on next trigger");
        }
    }
}
```

**Hosted service** for startup + periodic check:

```csharp
public sealed class PaymentAccountSyncHostedService : BackgroundService
{
    private readonly PaymentAccountSyncService _service;
    public PaymentAccountSyncHostedService(PaymentAccountSyncService service) => _service = service;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // First push on startup (catches any local change made while server was offline)
        await _service.SyncIfChangedAsync(ct);

        // Periodic poll — Settings save trigger would be cleaner but periodic is simpler
        // and matches the existing PaymentSyncHostedService pattern. 5min cadence is fine
        // since this is config-class data, not user-driven.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                await _service.SyncIfChangedAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
```

**ICurrentLicenseProvider**: extend if needed to return the active license's server-side `Guid Id`. Likely the existing `CurrentLicenseProvider` already has this; verify.

**Tests**: Mock LicenseApiClient + SettingsStore; assert that SyncIfChangedAsync skips when nothing changed, calls API when changed, swallows exceptions.

**Commit**: `feat(wpf-sync): PaymentAccount sync (IBAN + AccountHolder periodic + startup)`

---

## Task 4: WPF Customer projection sync

Periyodik olarak lokal Customer kayıtlarının değişimlerini server'a push.

**Strategy**: 
- Watermark: `AppSettings.LastCustomerProjectionSyncAt` (long unix seconds; new field)
- Query lokal Customer repo: `WHERE LastSeenAt > watermark` (delta)
- Convert to `WpfCustomerSyncItem` list (max 500 per batch; multiple batches if needed)
- POST to server
- Update watermark to `MAX(LastSeenAt)` from the batch

**Files**: 
- `OrderDeck.Core/Customers/CustomerRepository.cs` — add `IReadOnlyList<Customer> GetUpdatedSince(long unixSeconds, int max)` if not present (check first; if `LastSeenAt` queries exist, use them)
- `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncService.cs` (new)
- `OrderDeck.App/Services/Sync/WpfCustomerProjectionSyncHostedService.cs` (new)
- `OrderDeck.Core/Settings/AppSettings.cs` — add `public long LastCustomerProjectionSyncAt { get; set; } = 0;`

```csharp
public sealed class WpfCustomerProjectionSyncService
{
    private const int BatchSize = 500;
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<WpfCustomerProjectionSyncService> _log;

    public WpfCustomerProjectionSyncService(
        LicenseApiClient api, CustomerRepository customers,
        SettingsStore settingsStore, ICurrentLicenseProvider licenseProvider,
        ILogger<WpfCustomerProjectionSyncService> log)
    {
        _api = api; _customers = customers;
        _settingsStore = settingsStore; _licenseProvider = licenseProvider;
        _log = log;
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var licenseId = _licenseProvider.GetActiveLicenseId();
        if (licenseId is null) return 0;

        var settings = _settingsStore.Load();
        var watermark = settings.LastCustomerProjectionSyncAt;
        var batch = _customers.GetUpdatedSince(watermark, BatchSize);
        if (batch.Count == 0) return 0;

        var items = batch.Select(c => new WpfCustomerSyncItem(
            Id: Guid.ParseExact(c.Id, "N"),
            Platform: c.Platform,
            Username: c.Username,
            FullName: c.DisplayName,
            Phone: c.Phone,
            Address: c.Address,
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(c.LastSeenAt))).ToList();

        try
        {
            var resp = await _api.SyncWpfCustomersAsync(licenseId.Value, items, ct);
            var newWatermark = batch.Max(c => c.LastSeenAt);
            settings.LastCustomerProjectionSyncAt = newWatermark;
            _settingsStore.Save(settings);
            _log.LogInformation(
                "Customer projection sync: pushed {Synced} (retro matches {Matches}), watermark→{Watermark}",
                resp.Synced, resp.RetroactiveMatches, newWatermark);
            return resp.Synced;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Customer projection sync failed; will retry");
            return 0;
        }
    }
}
```

```csharp
public sealed class WpfCustomerProjectionSyncHostedService : BackgroundService
{
    private readonly WpfCustomerProjectionSyncService _service;
    public WpfCustomerProjectionSyncHostedService(WpfCustomerProjectionSyncService service) => _service = service;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 60s sync cadence, matches existing PaymentSyncHostedService.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _service.SyncOnceAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
```

**Tests**: Mock repository + api; verify watermark advance + batch flow + Guid parse from hex string.

**Notes on `Customer.Id`**: WPF stores as `string` (hex GUID via `Guid.ToString("N")`). Server expects `Guid`. Test that `Guid.ParseExact(c.Id, "N")` works on a sample (and throws if format wrong — skip that row with a warning rather than crash the batch).

**Commit**: `feat(wpf-sync): WpfCustomerProjection periodic sync (60s, batched 500)`

---

## Task 5: DI registration + PR

Edit `OrderDeck.App/AppHost.cs`:

```csharp
services.AddSingleton<PaymentAccountSyncService>();
services.AddHostedService<PaymentAccountSyncHostedService>();
services.AddSingleton<WpfCustomerProjectionSyncService>();
services.AddHostedService<WpfCustomerProjectionSyncHostedService>();
```

Also ensure `ShopperAppSettingsViewModel` is constructed in `SettingsViewModel` (already covered in Task 2).

Final:
```bash
dotnet build OrderDeck.App
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
git push -u origin feat/wpf-shopper-mgmt
gh pr create --title "feat(wpf-shopper-mgmt): Faz 0c-2 — settings UI + payment account + customer projection sync" --body "..."
```

PR body must mention:
- 4 new ApiClient methods
- Settings dialog "Müşteri App" tab (shopper code editor with 7d cooldown messaging, validator-driven error mapping)
- IBAN + AccountHolder periodic sync (5min) + startup
- Customer projection delta sync (60s, batched 500, watermark in AppSettings)
- Tests added
- Spec: `docs/superpowers/specs/2026-05-20-customer-app-design.md`

---

## Self-Review

**Spec coverage** (Faz 0c WPF maddeleri):

| Spec maddesi | Plan task |
|--------------|-----------|
| WPF Settings → Müşteri App tab + shopper code editor | T2 |
| WPF IBAN/AccountHolder sync to server | T3 |
| WPF Customer projection bulk sync (periodic) | T4 |
| Cooldown UI messaging (CanChangeAt) | T2 |

**Placeholder check**: T1-T4 hep eksiksiz, code+test+commit included.

**Type consistency**:
- `ShopperCodeResponse.LicenseId: Guid` — server side aynı
- `WpfCustomerSyncItem.Id: Guid` — WPF'ten string → ParseExact("N") conversion
- `WpfCustomerSyncItem.UpdatedAt: DateTimeOffset` — WPF long unix → FromUnixTimeSeconds
- `ShopperCodeValidationException.ErrorCode: string` — server'ın Problem.Title değerleri

---

## Sonraki Plan

Faz 0c-2 merge sonrası:
- **Faz 0c-3** mobile panel UI — DahaFazlaScreen → yeni ShopperCodeScreen (kod yönet)
- **Faz 1+** — Mobile shopper app (yeni repo, ilk ekranlar)
