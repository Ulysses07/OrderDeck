# Faz 3b-2 — Multi-Select Queue + Smart Print + Enter Chat→Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yazdırma kuyruğunda multi-select + akıllı yazdırma (seçim varsa sadece seçilenler) + multi-delete + Enter ile chat'ten kuyruğa ekleme.

**Architecture:** `MainShellViewModel` davranış genişletmesi: `SelectedQueueItems` ObservableCollection, dinamik label property'leri, `Print` smart mode, `RemoveSelectedFromQueue` multi-delete. WPF tarafında `SelectionMode="Extended"` + code-behind sync handler + ChatList Enter handler. Test edilebilirlik için `ILabelPrinter` interface eklenir; LiveDeck.Tests target framework `net10.0-windows`'a yükseltilir ki LiveDeck.App'i referans alabilsin.

**Tech Stack:** .NET 10 WPF (existing), CommunityToolkit.Mvvm (existing). Yeni paket yok.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-3b-2 state:** Faz 3b-1 HEAD `17af78d` + spec commit `f427b18`. 114/114 tests passing.

**Spec reference:** `docs/superpowers/specs/2026-04-28-phase-3b-2-multi-select-queue-design.md`

---

## Task Index

**Test infrastructure (1):** LiveDeck.Tests'e net10.0-windows + LiveDeck.App ref + ILabelPrinter interface
**ViewModel (2-4):** SelectedQueueItems + dinamik labels + smart Print + multi-delete
**View (5):** XAML SelectionMode + button bindings + handler
**Tests (6):** MainShellPrintTests (3 senaryo)
**Acceptance (7):** Manual smoke

---

### Task 1: ILabelPrinter interface + DI + Tests project Windows target

**Files:**
- Create: `LiveDeck.Labeling/ILabelPrinter.cs`
- Modify: `LiveDeck.Labeling/LabelPrinter.cs`
- Modify: `LiveDeck.App/AppHost.cs`
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs` (constructor parameter type)
- Modify: `LiveDeck.App/ViewModels/SettingsViewModel.cs:167` (LabelPrinter direct usage)
- Modify: `LiveDeck.Tests/LiveDeck.Tests.csproj` (target net10.0-windows + add LiveDeck.App reference)

**Context:** `LabelPrinter` `sealed` ve Windows-platform-bound. Test edilebilirlik için minimal `ILabelPrinter` interface (`Print(IReadOnlyList<Label>)` tek metod) çıkarıyoruz. Tests project mevcut `net10.0` — `LiveDeck.App` (net10.0-windows) referansı için tests'i `net10.0-windows`'a yükseltiyoruz. Kayıp: Linux'ta test çalıştırma (proje zaten Windows-only, gerçek kayıp yok). `SettingsViewModel`'de `new LabelPrinter(temp, ...)` çağrısı (test print preview için) `LabelPrinter`'ı doğrudan kullanmaya devam edebilir — interface tek consumer'ı `MainShellViewModel`.

- [ ] **Step 1: Create ILabelPrinter interface**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Labeling/ILabelPrinter.cs`:

```csharp
using System.Collections.Generic;
using LiveDeck.Core.Sales;

namespace LiveDeck.Labeling;

/// <summary>Etiket yazdırma soyutlaması. Test'lerde fake implementation kullanılır.</summary>
public interface ILabelPrinter
{
    /// <summary>Verilen etiketleri sırayla yazdırır. Boş listede no-op.</summary>
    void Print(IReadOnlyList<Label> labels);
}
```

- [ ] **Step 2: Make LabelPrinter implement ILabelPrinter**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Labeling/LabelPrinter.cs`. Find the class declaration:

```csharp
[SupportedOSPlatform("windows")]
public sealed class LabelPrinter
```

Replace with:

```csharp
[SupportedOSPlatform("windows")]
public sealed class LabelPrinter : ILabelPrinter
```

Mevcut `Print(IReadOnlyList<Label> labels)` metodu zaten interface ile uyumlu, başka değişiklik gerekmez.

- [ ] **Step 3: Update AppHost DI**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`. Find the LabelPrinter registration:

```csharp
services.AddSingleton(sp => new LabelPrinter(
    sp.GetRequiredService<AppSettings>(),
    sp.GetRequiredService<ILogger<LabelPrinter>>()));
```

Replace with:

```csharp
services.AddSingleton<LabelPrinter>(sp => new LabelPrinter(
    sp.GetRequiredService<AppSettings>(),
    sp.GetRequiredService<ILogger<LabelPrinter>>()));
services.AddSingleton<ILabelPrinter>(sp => sp.GetRequiredService<LabelPrinter>());
```

`SettingsViewModel` hâlâ concrete `LabelPrinter` kullanıyor (test print preview); interface sadece `MainShellViewModel` için. İki kayıt da tek instance'ı paylaşır.

- [ ] **Step 4: Change MainShellViewModel ctor parameter**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`.

Add `using LiveDeck.Labeling;` if not already present.

Find the field declaration:
```csharp
private readonly LabelPrinter _printer;
```

Replace with:
```csharp
private readonly ILabelPrinter _printer;
```

Find the constructor parameter:
```csharp
LabelPrinter printer,
```

Replace with:
```csharp
ILabelPrinter printer,
```

(The assignment `_printer = printer;` stays unchanged.)

- [ ] **Step 5: Bump LiveDeck.Tests to net10.0-windows + add LiveDeck.App reference**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/LiveDeck.Tests.csproj`. Replace contents with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Dapper" Version="2.1.35" />
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Moq" Version="4.20.72" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\LiveDeck.Core\LiveDeck.Core.csproj" />
    <ProjectReference Include="..\LiveDeck.Chat\LiveDeck.Chat.csproj" />
    <ProjectReference Include="..\LiveDeck.Labeling\LiveDeck.Labeling.csproj" />
    <ProjectReference Include="..\LiveDeck.App\LiveDeck.App.csproj" />
  </ItemGroup>
</Project>
```

`UseWPF=true` test projesinde WPF tiplerini (Application, Window, vs.) ihtiyaç duyduğunda kullanmamızı sağlar. Tests `App.Host.Services` gibi static state'e erişmek istemese de, MainShellViewModel'in transitive dependency'leri WPF içerir.

- [ ] **Step 6: Build whole solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Expected: 0 errors. Eğer "platform mismatch" hatası gelirse Tests project'in hâlâ `net10.0` olduğu anlaşılır — Step 5'i tekrar uygula.

- [ ] **Step 7: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 114/114 pass (no regression). Test framework Windows-only target ile aynı şekilde çalışır.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Labeling/ILabelPrinter.cs LiveDeck.Labeling/LabelPrinter.cs LiveDeck.App/AppHost.cs LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.Tests/LiveDeck.Tests.csproj
git commit -m "refactor(labeling): add ILabelPrinter interface + bump Tests to net10.0-windows"
```

---

### Task 2: MainShellViewModel — SelectedQueueItems + dinamik label properties

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`

**Context:** Multi-select state'i ve buton label'ları için derived property'ler. `SelectedQueueItems.CollectionChanged` → label property change notifications.

- [ ] **Step 1: Add SelectedQueueItems collection**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`. Find the existing `PrintQueue` declaration:

```csharp
public ObservableCollection<LabelViewModel>       PrintQueue   { get; } = new();
```

Add immediately below:

```csharp
/// <summary>Multi-select kuyrukta seçili etiketler. Code-behind QueueList.SelectionChanged
/// event'inden senkronize eder. Boş = hiç seçim yok.</summary>
public ObservableCollection<LabelViewModel>       SelectedQueueItems { get; } = new();

/// <summary>Yazdır butonu için dinamik label.</summary>
public string PrintButtonLabel => SelectedQueueItems.Count > 0
    ? $"Yazdır ({SelectedQueueItems.Count})"
    : "Yazdır";

/// <summary>Sil butonu için dinamik label.</summary>
public string DeleteButtonLabel => SelectedQueueItems.Count switch
{
    0 => "Seçileni Sil",
    1 => "Seçileni Sil",
    _ => $"Seçilenleri Sil ({SelectedQueueItems.Count})"
};
```

- [ ] **Step 2: Wire CollectionChanged to label property change notifications**

Find the existing constructor (starts with `public MainShellViewModel(...)`). Locate the line:

```csharp
Banner.AutoDrawRequested += () => DrawGiveawayNowCommand.Execute(null);
```

Add immediately below:

```csharp
SelectedQueueItems.CollectionChanged += (_, _) =>
{
    OnPropertyChanged(nameof(PrintButtonLabel));
    OnPropertyChanged(nameof(DeleteButtonLabel));
};
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Run tests (regression)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 114/114 pass.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs
git commit -m "feat(app): add SelectedQueueItems + dynamic Print/Delete button labels"
```

---

### Task 3: MainShellViewModel — smart Print

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`

**Context:** Mevcut `Print()` `PrintQueue.Clear()` yapıyor (hepsini yazdır + clear). Yeni davranış: seçim varsa sadece seçilenler yazdırılır + sadece yazdırılanlar kuyruktan çıkar; seçim yoksa hepsi.

- [ ] **Step 1: Replace Print method**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`. Find:

```csharp
[RelayCommand]
private void Print()
{
    if (PrintQueue.Count == 0) return;
    var snapshot = PrintQueue.Select(vm => vm.Label).ToList();
    _printer.Print(snapshot);
    _labels.MarkPrintedAndRecord(snapshot.Select(l => l.Id).ToList());
    PrintQueue.Clear();
}
```

Replace with:

```csharp
[RelayCommand]
private void Print()
{
    var snapshot = SelectedQueueItems.Count > 0
        ? SelectedQueueItems.ToList()
        : PrintQueue.ToList();
    if (snapshot.Count == 0) return;

    var labels = snapshot.Select(vm => vm.Label).ToList();
    _printer.Print(labels);
    _labels.MarkPrintedAndRecord(labels.Select(l => l.Id).ToList());

    // Sadece yazdırılanları kuyruktan kaldır (smart mode'da kalan seçimsizler korunur).
    foreach (var vm in snapshot) PrintQueue.Remove(vm);
    SelectedQueueItems.Clear();
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 3: Run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 114/114 pass. (Akıllı print kuyruk boşken aynı erken-return davranışı; kuyruk dolu + seçim yokken hepsini yazdırır + boşaltır — eski davranış korunur.)

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs
git commit -m "feat(app): smart Print — selection prints subset, no selection prints all"
```

---

### Task 4: MainShellViewModel — multi-delete + DeleteSelectedFromQueueViaShortcut

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`

**Context:** `RemoveSelectedFromQueue` parametresiz multi-delete'a dönüşür. `DeleteSelectedFromQueueViaShortcut` (Phase 3b-1) artık aynı yola döner.

- [ ] **Step 1: Replace RemoveSelectedFromQueue**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`. Find:

```csharp
[RelayCommand]
private void RemoveSelectedFromQueue(LabelViewModel? selected)
{
    if (selected is null) return;
    _labels.Delete(selected.Id);
    PrintQueue.Remove(selected);
}
```

Replace with:

```csharp
[RelayCommand]
private void RemoveSelectedFromQueue()
{
    if (SelectedQueueItems.Count == 0) return;
    foreach (var vm in SelectedQueueItems.ToList())
    {
        _labels.Delete(vm.Id);
        PrintQueue.Remove(vm);
    }
    SelectedQueueItems.Clear();
}
```

- [ ] **Step 2: Update DeleteSelectedFromQueueViaShortcut to delegate**

Find the existing method (added in Phase 3b-1):

```csharp
[RelayCommand]
private void DeleteSelectedFromQueueViaShortcut()
{
    if (SelectedQueueItem is null) return;
    _labels.Delete(SelectedQueueItem.Id);
    PrintQueue.Remove(SelectedQueueItem);
    SelectedQueueItem = null;
}
```

Replace with:

```csharp
[RelayCommand]
private void DeleteSelectedFromQueueViaShortcut() => RemoveSelectedFromQueue();
```

Multi-select varken Del kısayolu tüm seçimi siler. Tek seçim varsa onu siler. Hiç seçim yoksa no-op.

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
```

Expected: 0 errors. (XAML'deki `RemoveSelectedFromQueueCommand` `CommandParameter` binding'i hâlâ var ama generated command artık parametresiz. WPF runtime'da extra parametre warning'i logger'a düşer, build hatası vermez. Task 5'te XAML temizlenir.)

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs
git commit -m "feat(app): multi-delete in queue + shortcut delegates to RemoveSelectedFromQueue"
```

---

### Task 5: MainShellView — SelectionMode + dynamic labels + Enter handler

**Files:**
- Modify: `LiveDeck.App/Views/MainShellView.xaml`
- Modify: `LiveDeck.App/Views/MainShellView.xaml.cs`

**Context:** XAML'de QueueList `SelectionMode="Extended"` + `SelectionChanged` handler; ChatList `PreviewKeyDown` handler; Yazdır + Sil butonu `Content` binding'leri dinamik.

- [ ] **Step 1: Update QueueList in MainShellView.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`. Find:

```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
         SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
         Background="#FF1A1A1A" Foreground="White"
         BorderBrush="#FF333333" BorderThickness="1">
```

Replace with:

```xml
<ListBox Grid.Row="1"
         x:Name="QueueList"
         ItemsSource="{Binding PrintQueue}"
         SelectedItem="{Binding SelectedQueueItem, Mode=TwoWay}"
         SelectionMode="Extended"
         SelectionChanged="QueueList_OnSelectionChanged"
         Background="#FF1A1A1A" Foreground="White"
         BorderBrush="#FF333333" BorderThickness="1">
```

- [ ] **Step 2: Update Yazdır + Sil button bindings**

In the same file find the bottom button bar:

```xml
<Button Content="Yazdır"  Command="{Binding PrintCommand}"
        Padding="20,8" FontSize="14" FontWeight="Bold"/>
<Button Content="Seçileni Sil"
        Command="{Binding RemoveSelectedFromQueueCommand}"
        CommandParameter="{Binding ElementName=QueueList, Path=SelectedItem}"
        Padding="14,8" Margin="8,0,0,0"/>
```

Replace with:

```xml
<Button Content="{Binding PrintButtonLabel}"
        Command="{Binding PrintCommand}"
        Padding="20,8" FontSize="14" FontWeight="Bold"/>
<Button Content="{Binding DeleteButtonLabel}"
        Command="{Binding RemoveSelectedFromQueueCommand}"
        Padding="14,8" Margin="8,0,0,0"/>
```

`CommandParameter` kalkar — VM artık `SelectedQueueItems` listesini kendi state'inden okur.

- [ ] **Step 3: Add PreviewKeyDown to ChatList**

Find the existing ChatList declaration:

```xml
<ListBox  Grid.Row="1"
          x:Name="ChatList"
          ItemsSource="{Binding ChatMessages}"
          MouseDoubleClick="ChatList_OnDoubleClick"
```

Replace with:

```xml
<ListBox  Grid.Row="1"
          x:Name="ChatList"
          ItemsSource="{Binding ChatMessages}"
          MouseDoubleClick="ChatList_OnDoubleClick"
          PreviewKeyDown="ChatList_OnPreviewKeyDown"
```

(Diğer öznitelikler aynen kalır.)

- [ ] **Step 4: Add code-behind handlers**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml.cs`.

Add usings if not present:
```csharp
using System.Linq;
using System.Windows.Controls;
```

The file has existing `ChatList_OnDoubleClick` and `OnMenuClick`. Add two new handlers after `OnMenuClick`:

```csharp
private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (DataContext is not ViewModels.MainShellViewModel vm) return;

    foreach (var added in e.AddedItems.OfType<ViewModels.LabelViewModel>())
        if (!vm.SelectedQueueItems.Contains(added))
            vm.SelectedQueueItems.Add(added);

    foreach (var removed in e.RemovedItems.OfType<ViewModels.LabelViewModel>())
        vm.SelectedQueueItems.Remove(removed);
}

private void ChatList_OnPreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key != Key.Enter) return;
    if (DataContext is not ViewModels.MainShellViewModel vm) return;
    if (ChatList.SelectedItem is not ViewModels.ChatMessageViewModel msgVm) return;

    vm.AddChatToQueue(msgVm);
    e.Handled = true;
}
```

- [ ] **Step 5: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 6: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 114/114 pass.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/MainShellView.xaml LiveDeck.App/Views/MainShellView.xaml.cs
git commit -m "feat(app): QueueList Extended + dynamic button labels + ChatList Enter handler"
```

---

### Task 6: MainShellPrintTests — smart Print + multi-delete

**Files:**
- Create: `LiveDeck.Tests/App/MainShellPrintTests.cs`

**Context:** `MainShellViewModel`'in tam DI graph'ı: `IChatBus`, `LabelService`, `StreamSessionService`, `ILabelPrinter`, `CustomerService`, `CustomerRepository`, `GiveawayService`, `GiveawayBannerViewModel`. Test fixture `InMemorySqlite` + gerçek service'ler + fake `ILabelPrinter` kurar.

- [ ] **Step 1: Create test file**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/App/MainShellPrintTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiveDeck.App.ViewModels;
using LiveDeck.Chat;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Labeling;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.App;

public class MainShellPrintTests
{
    private sealed class FakeLabelPrinter : ILabelPrinter
    {
        public List<List<Label>> Calls { get; } = new();
        public void Print(IReadOnlyList<Label> labels) => Calls.Add(labels.ToList());
    }

    private static (MainShellViewModel Vm, FakeLabelPrinter Printer, InMemorySqlite Db) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        var sessionRepo  = new SessionRepository(db);
        var customerRepo = new CustomerRepository(db);
        var labelRepo    = new LabelRepository(db);
        var giveawayRepo = new GiveawayRepository(db);

        var customerSvc  = new CustomerService(customerRepo, clock.Object);
        var sessionSvc   = new StreamSessionService(sessionRepo, clock.Object);
        var labelSvc     = new LabelService(labelRepo, customerRepo, clock.Object);
        var drawer       = new GiveawayDrawer();
        var giveawaySvc  = new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object);

        var bus = new ChatBus(ringBufferSize: 50);
        var printer = new FakeLabelPrinter();
        var banner = new GiveawayBannerViewModel(giveawayRepo, clock.Object);

        // Start a session so AddChatToQueue works
        sessionSvc.Start("Test", new[] { "instagram" });

        var vm = new MainShellViewModel(
            bus, labelSvc, sessionSvc, printer, customerSvc, customerRepo, giveawaySvc, banner);

        return (vm, printer, db);
    }

    private static ChatMessageViewModel ChatVm(string username, string text)
    {
        var msg = new ChatMessage(
            Guid.NewGuid().ToString("N"), "instagram", null,
            username, username, null, text, 1000, Array.Empty<string>());
        return new ChatMessageViewModel(msg, isSenderBlacklisted: false);
    }

    private static void Enqueue(MainShellViewModel vm, string username, decimal price)
    {
        vm.ActivePriceText = price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        vm.AddChatToQueue(ChatVm(username, $"alıyorum {Guid.NewGuid():N}"));
    }

    [Fact]
    public void Print_with_no_selection_prints_all_and_empties_queue()
    {
        var (vm, printer, db) = Fx();
        using var _ = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        vm.PrintCommand.Execute(null);

        printer.Calls.Should().HaveCount(1);
        printer.Calls[0].Should().HaveCount(3);
        vm.PrintQueue.Should().BeEmpty();
        vm.SelectedQueueItems.Should().BeEmpty();
    }

    [Fact]
    public void Print_with_partial_selection_prints_selected_only_and_keeps_remainder()
    {
        var (vm, printer, db) = Fx();
        using var _ = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        // Select 2 of 3
        vm.SelectedQueueItems.Add(vm.PrintQueue[0]);
        vm.SelectedQueueItems.Add(vm.PrintQueue[2]);

        vm.PrintCommand.Execute(null);

        printer.Calls.Should().HaveCount(1);
        printer.Calls[0].Should().HaveCount(2);
        vm.PrintQueue.Should().HaveCount(1);
        vm.PrintQueue[0].Username.Should().Be("@b");   // unselected, retained
        vm.SelectedQueueItems.Should().BeEmpty();
    }

    [Fact]
    public void RemoveSelectedFromQueue_with_multi_selection_deletes_all_selected()
    {
        var (vm, _, db) = Fx();
        using var _2 = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        vm.SelectedQueueItems.Add(vm.PrintQueue[0]);
        vm.SelectedQueueItems.Add(vm.PrintQueue[1]);

        vm.RemoveSelectedFromQueueCommand.Execute(null);

        vm.PrintQueue.Should().HaveCount(1);
        vm.PrintQueue[0].Username.Should().Be("@c");
        vm.SelectedQueueItems.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MainShellPrintTests" 2>&1 | tail -10
```

Expected: tests should compile (LiveDeck.App reference Task 1'de eklendi). Test sonuçları başarılı olmalı — production code zaten Tasks 2-4'te yapıldı, bu Task 6'da sadece test ekleniyor.

Eğer test başarısız olursa:
- `MainShellViewModel` ctor parametre sırası değişmiş olabilir — Task 1 Step 4 ile yeniden eşleşmeli
- `ChatBus` constructor parameter — kontrol et
- `LabelService` ctor parametreleri kontrol et

- [ ] **Step 3: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 117/117 pass (114 baseline + 3 new).

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Tests/App/MainShellPrintTests.cs
git commit -m "test(app): add MainShellPrintTests for smart Print + multi-delete"
```

---

### Task 7: Manual Acceptance Smoke

**Files:** None — execute and observe.

**Context:** 9 senaryo: multi-select temelleri + smart Print + multi-delete + Enter chat→queue + native Ctrl+A + ContextMenu primary selection.

- [ ] **Step 1: Start app cleanly**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

App opens without errors.

- [ ] **Step 2: Multi-select temelleri**

- "Yayın Başlat" → 5 chat mesajı çift-tıkla kuyruğa ekle (her birine 100 TL fiyat).
- Ctrl+click ile 2'sini seç → "Seçilenleri Sil (2)" butonu metni güncellensin.
- Butona bas → 2 etiket silinir, kuyrukta 3 kalır.

- [ ] **Step 3: Shift+click + Del**

- 5 etiket daha ekle (toplam 8).
- İlk etikete tıkla, Shift+click ile 4. etikete → 4 satır seçili.
- Del bas → kuyrukta 4 kalır.

- [ ] **Step 4: Smart Print — hepsi**

- Mevcut 4 etiket, hiç seçim yok.
- "Yazdır" butonu metni: `Yazdır`.
- Bas → kuyruk boşalır, 4 etiket yazdırılır.

- [ ] **Step 5: Smart Print — alt küme**

- 5 etiket ekle.
- Ctrl+click ile 2'sini seç.
- "Yazdır (2)" butonu görünür.
- Bas → 2 etiket yazdırılır, 3 kalır.

- [ ] **Step 6: Ctrl+A native**

- 3 etiket var, ListBox focus.
- Ctrl+A bas → 3 satır seçili.
- "Yazdır (3)" görünür → bas → kuyruk boşalır.

- [ ] **Step 7: Enter chat→queue**

- ActiveCode TextBox'ına `K01` yaz, ActivePriceText'e `100`.
- ChatList'te bir mesaja tıkla.
- **ChatList focus iken** Enter bas → kuyruğa eklenir.

- [ ] **Step 8: TextBox Enter etkisiz**

- ActivePriceText TextBox'ında `200` yaz, **TextBox focus iken** Enter bas.
- Kuyruğa ekleme **olmaz** (TextBox kendi default Enter davranışını kullanır veya no-op).

- [ ] **Step 9: ContextMenu primary**

- Multi-select 2 satır.
- Sağ tık herhangi birine → "Müşteri Detayı" → tıkladığın satırın detayı açılır (primary `SelectedQueueItem`).

- [ ] **Step 10: Hepsini Temizle**

- 3 etiket ekle, 1'ini seç.
- "Hepsini Temizle" → onay → kuyruk boşalır + SelectedQueueItems boş.

- [ ] **Step 11: Phase 3b-1 regresyon**

- F1 → kısayol yardım dialog'u
- F2 → Ayarlar → Kısayollar tab'ı çalışıyor
- Ctrl+P → Yazdır

---

## Self-Review

**Spec coverage check:**

| Spec section | Plan task |
|---|---|
| §3.1 SelectedQueueItems + dynamic labels | Task 2 |
| §3.2 Smart Print | Task 3 |
| §3.3 Multi-delete | Task 4 |
| §3.4 QueueList SelectionMode + handler | Task 5 |
| §3.5 Buton bindingleri | Task 5 |
| §3.6 Code-behind sync + Enter | Task 5 |
| §4 Hata yönetimi | Distributed (snapshot.Count==0 in Task 3, ToList() guard in Task 4, Contains guard in Task 5) |
| §5.1 ILabelPrinter + 3 yeni test | Tasks 1 + 6 |
| §5.2 Manuel kabul testi | Task 7 |
| §6 YAGNI | Plan refrains: no drag-drop, no separate select-all button, no confirm dialog for multi-delete |
| §8 Kabul kriterleri | Task 7 + 117/117 test |

All sections covered.

**Placeholder scan:** "TBD", "TODO", "implement later", "fill in", "appropriate", "similar to" — none found. Each code step has actual code.

**Type consistency check:**

- `SelectedQueueItems: ObservableCollection<LabelViewModel>` — defined Task 2, consumed Tasks 3 (`SelectedQueueItems.ToList()` for Print snapshot), 4 (foreach), 5 (XAML AncestorType=Window? no, code-behind direct field access).
- `PrintButtonLabel`, `DeleteButtonLabel` derived properties — defined Task 2, bound by Task 5 XAML `Content`.
- `RemoveSelectedFromQueueCommand` — parametresiz, defined Task 4 (replaces parameterful version). XAML in Task 5 strips `CommandParameter`.
- `DeleteSelectedFromQueueViaShortcut` — was added in Phase 3b-1 commit `83c7600` with single-item logic; Task 4 replaces with delegation to `RemoveSelectedFromQueue`.
- `ILabelPrinter.Print(IReadOnlyList<Label>)` — defined Task 1, consumed by `MainShellViewModel._printer` (Task 1 ctor change), test fake `FakeLabelPrinter` (Task 6).
- `QueueList_OnSelectionChanged`, `ChatList_OnPreviewKeyDown` — handler names match XAML `SelectionChanged="..."` / `PreviewKeyDown="..."` attribute values in Task 5.

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-3b-2-multi-select-queue.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?
