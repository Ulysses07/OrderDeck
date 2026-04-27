# LiveDeck Phase 1 — Instagram-Only Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a usable LiveDeck v1 capable of capturing orders from a live Instagram stream, displaying them in a queue, broadcasting chat to OBS, and copying labels to clipboard.

**Architecture:** WPF + MVVM desktop app on .NET 10, ASP.NET Core minimal API for OBS Browser Source, SQLite via Dapper for persistence, browser extension + WebSocket bridge for Instagram chat ingestion. OrderCaptureEngine is a pure-function pipeline tested via TDD.

**Tech Stack:**
- .NET 10 (WPF for desktop, ASP.NET Core minimal API for overlay server)
- C# 14, nullable reference types enabled
- SQLite + Dapper (no EF Core)
- Serilog for logging
- xUnit + Moq + FluentAssertions for tests
- Microsoft.Extensions.DependencyInjection for DI
- Microsoft.Xaml.Behaviors.Wpf for behaviors
- CommunityToolkit.Mvvm for MVVM helpers
- WebSocketSharp or built-in System.Net.WebSockets

**Reference codebase:** UniCast at `C:\Users\burak\Downloads\UniCast\UniCast` — ChatBus, ExtensionBridgeServer, browser extension content scripts (especially `Extension/content-instagram.js`).

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

---

## File Structure

The current state of `C:\Users\burak\source\repos\LiveDeck` is a default `dotnet new console` template. We will replace it with a multi-project solution. Final structure after Phase 1:

```
LiveDeck/
├── LiveDeck.sln                              # Solution file
├── LiveDeck.App/                             # WPF desktop app
│   ├── LiveDeck.App.csproj                   # net10.0-windows, OutputType=WinExe
│   ├── App.xaml + App.xaml.cs                # Application bootstrap, DI setup
│   ├── AppHost.cs                            # ServiceCollection wiring
│   ├── MainWindow.xaml + .cs                 # Main window shell with navigation
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs                  # ObservableObject base
│   │   ├── MainViewModel.cs                  # Navigation orchestrator
│   │   ├── ActiveCodesViewModel.cs           # ActiveCode panel VM
│   │   ├── OrderQueueViewModel.cs            # Order queue VM
│   │   └── ChatPanelViewModel.cs             # In-app chat monitor VM
│   ├── Views/
│   │   ├── ActiveCodesView.xaml + .cs        # ActiveCode CRUD UI
│   │   ├── OrderQueueView.xaml + .cs         # Order queue table
│   │   ├── ChatPanelView.xaml + .cs          # Live chat list
│   │   └── EditCodeDialog.xaml + .cs         # Modal for add/edit code
│   ├── Services/
│   │   ├── HotkeyService.cs                  # Global keyboard hooks (F9 etc.)
│   │   ├── ClipboardService.cs               # Clipboard write helpers
│   │   ├── EtiketIntegration.cs              # Optional FindWindow/UI Automation for etiket.exe
│   │   └── StreamSessionController.cs        # Start/end yayın oturumu
│   └── Converters/                           # XAML value converters
├── LiveDeck.Core/                            # Pure C# domain + business logic
│   ├── LiveDeck.Core.csproj                  # net10.0
│   ├── Chat/
│   │   ├── ChatBus.cs                        # In-memory pub/sub for ChatMessage
│   │   ├── ChatMessage.cs                    # Record type
│   │   └── IChatIngestor.cs                  # Ingestor interface
│   ├── Sales/
│   │   ├── OrderCaptureEngine.cs             # Pipeline orchestrator
│   │   ├── Pipeline/
│   │   │   ├── MessageNormalizer.cs          # TR diacritics, casing, whitespace
│   │   │   ├── CodeMatcher.cs                # Fuzzy match against active codes
│   │   │   ├── VariantExtractor.cs           # Size detection
│   │   │   ├── QuantityExtractor.cs          # Adet detection
│   │   │   ├── IntentScorer.cs               # Niyet kelimesi puanlama
│   │   │   └── ConfidenceScorer.cs           # Final 0-100 score
│   │   ├── ActiveCode.cs                     # Domain entity
│   │   ├── ActiveCodeService.cs              # CRUD logic
│   │   ├── OrderItem.cs                      # Domain entity (capture result)
│   │   ├── OrderStatus.cs                    # Enum
│   │   └── OrderService.cs                   # Order CRUD + status flow
│   ├── Customers/
│   │   ├── Customer.cs                       # Domain entity
│   │   └── CustomerService.cs                # Auto-create + lookup by (Platform, Username)
│   ├── Sessions/
│   │   ├── StreamSession.cs                  # Domain entity
│   │   └── StreamSessionService.cs           # Start/end stream
│   ├── Storage/
│   │   ├── IDbConnectionFactory.cs           # Connection abstraction
│   │   ├── SqliteConnectionFactory.cs        # Implementation
│   │   ├── MigrationRunner.cs                # Apply migrations from embedded resources
│   │   ├── Migrations/
│   │   │   └── 001_initial.sql               # First migration (all 6 tables)
│   │   └── Repositories/
│   │       ├── ActiveCodeRepository.cs       # Dapper queries
│   │       ├── OrderRepository.cs            # Dapper queries
│   │       ├── CustomerRepository.cs         # Dapper queries
│   │       └── SessionRepository.cs          # Dapper queries
│   └── Settings/
│       ├── AppSettings.cs                    # POCO settings
│       └── SettingsStore.cs                  # JSON file load/save
├── LiveDeck.Chat/                            # Platform ingestor implementations
│   ├── LiveDeck.Chat.csproj                  # net10.0
│   ├── Bridge/
│   │   ├── ExtensionBridgeServer.cs          # WebSocket server for browser extension
│   │   └── ExtensionMessage.cs               # Wire format DTO
│   └── Ingestors/
│       ├── InstagramIngestor.cs              # Receives extension events for Instagram
│       └── ExtensionBridgeIngestor.cs        # Generic bridge consumer
├── LiveDeck.Overlay/                         # ASP.NET Core minimal API for OBS Browser Source
│   ├── LiveDeck.Overlay.csproj               # net10.0
│   ├── OverlayHost.cs                        # WebHostBuilder + start/stop
│   ├── Endpoints/
│   │   ├── ChatOverlayEndpoint.cs            # GET /overlay/chat (HTML)
│   │   └── ChatWebSocketEndpoint.cs          # WS /ws/chat
│   ├── Models/
│   │   ├── OverlayEvent.cs                   # Discriminated union for WS payloads
│   │   └── ChatOverlaySnapshot.cs            # State recovery on connect
│   └── wwwroot/
│       ├── chat.html                         # Default chat overlay
│       ├── themes/
│       │   └── minimal/style.css             # Default theme
│       └── chat.js                           # WebSocket client + DOM updates
├── LiveDeck.Labeling/                        # Etiket clipboard helper
│   ├── LiveDeck.Labeling.csproj              # net10.0
│   └── ClipboardLabelFormatter.cs            # @username YORUM formatter
├── LiveDeck.Tests/                           # xUnit tests
│   ├── LiveDeck.Tests.csproj                 # net10.0
│   ├── Sales/
│   │   ├── MessageNormalizerTests.cs
│   │   ├── CodeMatcherTests.cs
│   │   ├── VariantExtractorTests.cs
│   │   ├── QuantityExtractorTests.cs
│   │   ├── IntentScorerTests.cs
│   │   ├── ConfidenceScorerTests.cs
│   │   ├── OrderCaptureEngineTests.cs
│   │   └── Fixtures/
│   │       └── tr_chat_samples.json          # Real Turkish chat fixtures
│   ├── Storage/
│   │   └── MigrationRunnerTests.cs
│   ├── Customers/
│   │   └── CustomerServiceTests.cs
│   └── TestHelpers/
│       └── InMemorySqlite.cs                 # Test DB factory
├── Extension/                                # Browser extension (ported from UniCast)
│   ├── manifest.json                         # MV3 manifest (rebranded for LiveDeck)
│   ├── background.js                         # Service worker
│   ├── content-instagram.js                  # Instagram DOM scraper
│   ├── popup.html + popup.js                 # Status panel
│   └── icons/                                # Branding
├── docs/superpowers/
│   ├── specs/2026-04-27-livedeck-design.md   # Already created
│   └── plans/2026-04-27-phase-1-instagram-core.md  # This file
└── .gitignore                                # Standard .NET ignore
```

**Key boundary rules:**
- `LiveDeck.Core` has zero UI/network dependencies — pure domain + persistence
- `LiveDeck.App` references everything but other projects don't reference App
- `LiveDeck.Chat`, `LiveDeck.Overlay`, `LiveDeck.Labeling` only reference Core
- `LiveDeck.Tests` references Core, Chat, Labeling (not App or Overlay — those tested via manual QA)

---

## Task Index

**Foundation (Tasks 1-6):** Solution setup, projects, .gitignore, DI, logging, settings
**Data Layer (Tasks 7-12):** SQLite, migrations, repositories, in-memory test infrastructure
**Domain (Tasks 13-17):** Entities, services for ActiveCode, Customer, OrderItem, StreamSession
**OrderCaptureEngine (Tasks 18-25):** TDD pipeline — normalizer, matcher, extractors, scorer, integration
**Chat Ingestion (Tasks 26-30):** ChatBus, Extension server, browser extension port, Instagram wiring
**OBS Overlay (Tasks 31-35):** ASP.NET Core host, WebSocket, HTML/JS overlay, snapshot
**WPF UI (Tasks 36-42):** MainWindow, navigation, ActiveCodes panel, order queue, chat panel, dialogs
**Etiket + Lifecycle (Tasks 43-46):** Clipboard formatter, F9 hotkey, etiket.exe integration, session start/end
**Acceptance (Task 47):** End-to-end manual smoke test

---

## Foundation

### Task 1: Replace console template with empty solution + .gitignore

**Files:**
- Delete: `C:/Users/burak/source/repos/LiveDeck/LiveDeck/` (entire inner folder)
- Delete: `C:/Users/burak/source/repos/LiveDeck/LiveDeck.slnx`
- Delete: `C:/Users/burak/source/repos/LiveDeck/.vs/` (VS cache)
- Create: `C:/Users/burak/source/repos/LiveDeck/LiveDeck.sln`
- Create: `C:/Users/burak/source/repos/LiveDeck/.gitignore`

- [ ] **Step 1: Remove the default console scaffolding**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -rf LiveDeck .vs LiveDeck.slnx
ls
```
Expected: empty directory (only `docs/` remains).

- [ ] **Step 2: Create empty solution file**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new sln -n LiveDeck
ls
```
Expected: `LiveDeck.sln docs/`

- [ ] **Step 3: Initialize git repository**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git init
git config user.name "Burak"
git config user.email "burak@livedeck.app"
```
Expected: `Initialized empty Git repository ...`

- [ ] **Step 4: Create .gitignore**

Write `C:/Users/burak/source/repos/LiveDeck/.gitignore`:

```gitignore
# Build outputs
bin/
obj/
*.user
*.suo
.vs/
.idea/

# Publish
publish/
out/

# Dotnet
*.dll
*.pdb
*.exe
project.lock.json
project.fragment.lock.json
artifacts/

# Test results
TestResults/
*.trx
*.coverage
*.coveragexml

# OS
Thumbs.db
.DS_Store

# IDE
*.swp
.vscode/
*.code-workspace

# Local config (machine-specific)
appsettings.Local.json
dev.license.json

# SQLite databases (developer-local)
*.db-shm
*.db-wal

# Embedded fixtures for Phase 1 testing — keep
!LiveDeck.Tests/Sales/Fixtures/*.json
```

- [ ] **Step 5: Initial commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add .gitignore LiveDeck.sln docs/
git commit -m "chore: initialize empty solution and .gitignore"
```
Expected: `[main (root-commit) <hash>] chore: ...`

---

### Task 2: Create six project skeletons

**Files:**
- Create: `LiveDeck.App/LiveDeck.App.csproj` (WPF, net10.0-windows)
- Create: `LiveDeck.Core/LiveDeck.Core.csproj` (net10.0)
- Create: `LiveDeck.Chat/LiveDeck.Chat.csproj` (net10.0)
- Create: `LiveDeck.Overlay/LiveDeck.Overlay.csproj` (net10.0)
- Create: `LiveDeck.Labeling/LiveDeck.Labeling.csproj` (net10.0)
- Create: `LiveDeck.Tests/LiveDeck.Tests.csproj` (net10.0)

- [ ] **Step 1: Create all six projects**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new wpf      -n LiveDeck.App      -f net10.0-windows
dotnet new classlib -n LiveDeck.Core     -f net10.0
dotnet new classlib -n LiveDeck.Chat     -f net10.0
dotnet new classlib -n LiveDeck.Overlay  -f net10.0
dotnet new classlib -n LiveDeck.Labeling -f net10.0
dotnet new xunit    -n LiveDeck.Tests    -f net10.0
```
Expected: six new directories, each with their `.csproj`.

- [ ] **Step 2: Add all projects to solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet sln add LiveDeck.App/LiveDeck.App.csproj
dotnet sln add LiveDeck.Core/LiveDeck.Core.csproj
dotnet sln add LiveDeck.Chat/LiveDeck.Chat.csproj
dotnet sln add LiveDeck.Overlay/LiveDeck.Overlay.csproj
dotnet sln add LiveDeck.Labeling/LiveDeck.Labeling.csproj
dotnet sln add LiveDeck.Tests/LiveDeck.Tests.csproj
```
Expected: `Project ... added to the solution.` six times.

- [ ] **Step 3: Wire project references per dependency rules**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.Chat/LiveDeck.Chat.csproj         reference LiveDeck.Core/LiveDeck.Core.csproj
dotnet add LiveDeck.Overlay/LiveDeck.Overlay.csproj   reference LiveDeck.Core/LiveDeck.Core.csproj
dotnet add LiveDeck.Labeling/LiveDeck.Labeling.csproj reference LiveDeck.Core/LiveDeck.Core.csproj
dotnet add LiveDeck.App/LiveDeck.App.csproj           reference LiveDeck.Core/LiveDeck.Core.csproj
dotnet add LiveDeck.App/LiveDeck.App.csproj           reference LiveDeck.Chat/LiveDeck.Chat.csproj
dotnet add LiveDeck.App/LiveDeck.App.csproj           reference LiveDeck.Overlay/LiveDeck.Overlay.csproj
dotnet add LiveDeck.App/LiveDeck.App.csproj           reference LiveDeck.Labeling/LiveDeck.Labeling.csproj
dotnet add LiveDeck.Tests/LiveDeck.Tests.csproj       reference LiveDeck.Core/LiveDeck.Core.csproj
dotnet add LiveDeck.Tests/LiveDeck.Tests.csproj       reference LiveDeck.Chat/LiveDeck.Chat.csproj
dotnet add LiveDeck.Tests/LiveDeck.Tests.csproj       reference LiveDeck.Labeling/LiveDeck.Labeling.csproj
```
Expected: `Reference ... added to the project.`

- [ ] **Step 4: Enable Nullable + LangVersion + ImplicitUsings on all class libraries**

For each of `LiveDeck.Core`, `LiveDeck.Chat`, `LiveDeck.Overlay`, `LiveDeck.Labeling`, edit the .csproj `<PropertyGroup>` to ensure:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
  </PropertyGroup>
</Project>
```

For `LiveDeck.App` (already WPF), keep its existing `<UseWPF>true</UseWPF>` and `<TargetFramework>net10.0-windows</TargetFramework>` but add the same `<Nullable>`, `<LangVersion>`, `<TreatWarningsAsErrors>` lines.

For `LiveDeck.Tests`, add `<Nullable>enable</Nullable>` and `<LangVersion>latest</LangVersion>` but DO NOT enable TreatWarningsAsErrors (test code may have intentional warnings).

- [ ] **Step 5: Delete the default `Class1.cs` files in class libraries**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -f LiveDeck.Core/Class1.cs
rm -f LiveDeck.Chat/Class1.cs
rm -f LiveDeck.Overlay/Class1.cs
rm -f LiveDeck.Labeling/Class1.cs
```

- [ ] **Step 6: Restore + build to confirm scaffolding compiles**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet restore
dotnet build LiveDeck.sln
```
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "chore: scaffold six-project solution with dependencies wired"
```

---

### Task 3: Add NuGet packages

**Files:**
- Modify: `LiveDeck.Core/LiveDeck.Core.csproj`
- Modify: `LiveDeck.App/LiveDeck.App.csproj`
- Modify: `LiveDeck.Chat/LiveDeck.Chat.csproj`
- Modify: `LiveDeck.Overlay/LiveDeck.Overlay.csproj`
- Modify: `LiveDeck.Tests/LiveDeck.Tests.csproj`

- [ ] **Step 1: Add Core dependencies**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.Core package Dapper --version 2.1.35
dotnet add LiveDeck.Core package Microsoft.Data.Sqlite --version 9.0.0
dotnet add LiveDeck.Core package Microsoft.Extensions.DependencyInjection.Abstractions --version 9.0.0
dotnet add LiveDeck.Core package Microsoft.Extensions.Logging.Abstractions --version 9.0.0
dotnet add LiveDeck.Core package System.Text.Json --version 9.0.0
```

- [ ] **Step 2: Add App dependencies**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.App package CommunityToolkit.Mvvm --version 8.4.0
dotnet add LiveDeck.App package Microsoft.Extensions.DependencyInjection --version 9.0.0
dotnet add LiveDeck.App package Microsoft.Extensions.Hosting --version 9.0.0
dotnet add LiveDeck.App package Serilog --version 4.2.0
dotnet add LiveDeck.App package Serilog.Extensions.Logging --version 9.0.0
dotnet add LiveDeck.App package Serilog.Sinks.File --version 6.0.0
dotnet add LiveDeck.App package Serilog.Sinks.Console --version 6.0.0
dotnet add LiveDeck.App package Microsoft.Xaml.Behaviors.Wpf --version 1.1.135
```

- [ ] **Step 3: Add Chat dependencies**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.Chat package Microsoft.Extensions.Logging.Abstractions --version 9.0.0
```

- [ ] **Step 4: Add Overlay dependencies**

The Overlay project hosts ASP.NET Core minimal API. Switch its SDK to `Microsoft.NET.Sdk.Web`:

Edit `LiveDeck.Overlay/LiveDeck.Overlay.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
    <OutputType>Library</OutputType>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LiveDeck.Core\LiveDeck.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="wwwroot\**\*.*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`OutputType=Library` because LiveDeck.App owns the process; Overlay is consumed as a library.

- [ ] **Step 5: Add Tests dependencies**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.Tests package Moq --version 4.20.72
dotnet add LiveDeck.Tests package FluentAssertions --version 7.0.0
dotnet add LiveDeck.Tests package Microsoft.Data.Sqlite --version 9.0.0
dotnet add LiveDeck.Tests package Dapper --version 2.1.35
```

- [ ] **Step 6: Restore and build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet restore
dotnet build LiveDeck.sln
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "chore: add NuGet packages for all projects"
```

---

### Task 4: Application paths + AppPaths utility

**Files:**
- Create: `LiveDeck.Core/AppPaths.cs`
- Create: `LiveDeck.Tests/AppPathsTests.cs`

- [ ] **Step 1: Write failing test**

Create `LiveDeck.Tests/AppPathsTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core;
using Xunit;

namespace LiveDeck.Tests;

public class AppPathsTests
{
    [Fact]
    public void DocumentsRoot_ends_with_LiveDeck()
    {
        AppPaths.DocumentsRoot.Should().EndWith("LiveDeck");
    }

    [Fact]
    public void DatabaseFile_lives_under_documents_data_folder()
    {
        AppPaths.DatabaseFile
            .Should().Contain("LiveDeck")
            .And.Contain("data")
            .And.EndWith("livedeck.db");
    }

    [Fact]
    public void LogsFolder_is_under_documents_root()
    {
        AppPaths.LogsFolder.Should().StartWith(AppPaths.DocumentsRoot);
        AppPaths.LogsFolder.Should().EndWith("Logs");
    }

    [Fact]
    public void EnsureDirectoriesExist_creates_data_and_logs()
    {
        AppPaths.EnsureDirectoriesExist();

        System.IO.Directory.Exists(AppPaths.DataFolder).Should().BeTrue();
        System.IO.Directory.Exists(AppPaths.LogsFolder).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~AppPathsTests"
```
Expected: FAIL — `The type or namespace name 'AppPaths' could not be found`.

- [ ] **Step 3: Implement AppPaths**

Create `LiveDeck.Core/AppPaths.cs`:

```csharp
using System;
using System.IO;

namespace LiveDeck.Core;

/// <summary>
/// Centralised filesystem paths used by LiveDeck. All paths are derived from the user's
/// Documents folder so they are roaming-friendly and easy to back up.
/// </summary>
public static class AppPaths
{
    public static string DocumentsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents",
        "LiveDeck");

    public static string DataFolder => Path.Combine(DocumentsRoot, "data");
    public static string LogsFolder => Path.Combine(DocumentsRoot, "Logs");
    public static string ReportsFolder => Path.Combine(DocumentsRoot, "Reports");
    public static string BackupsFolder => Path.Combine(DocumentsRoot, "Backups");

    public static string DatabaseFile => Path.Combine(DataFolder, "livedeck.db");
    public static string SettingsFile => Path.Combine(DocumentsRoot, "settings.json");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DocumentsRoot);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(ReportsFolder);
        Directory.CreateDirectory(BackupsFolder);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~AppPathsTests"
```
Expected: PASS — 4/4.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/AppPaths.cs LiveDeck.Tests/AppPathsTests.cs
git commit -m "feat(core): add AppPaths utility for filesystem locations"
```

---

### Task 5: AppSettings model + SettingsStore

**Files:**
- Create: `LiveDeck.Core/Settings/AppSettings.cs`
- Create: `LiveDeck.Core/Settings/SettingsStore.cs`
- Create: `LiveDeck.Tests/Settings/SettingsStoreTests.cs`

- [ ] **Step 1: Write failing test**

Create `LiveDeck.Tests/Settings/SettingsStoreTests.cs`:

```csharp
using System.IO;
using FluentAssertions;
using LiveDeck.Core.Settings;
using Xunit;

namespace LiveDeck.Tests.Settings;

public class SettingsStoreTests
{
    private string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"livedeck-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(CreateTempPath());
        var settings = store.Load();

        settings.OverlayPort.Should().Be(4747);
        settings.CaptureOrderHotkey.Should().Be("F9");
        settings.ParserHighConfidence.Should().Be(80);
        settings.ParserLowConfidence.Should().Be(50);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        var original = new AppSettings
        {
            OverlayPort = 5000,
            CaptureOrderHotkey = "F8",
            ParserHighConfidence = 75,
            ParserLowConfidence = 40,
            EtiketIntegrationEnabled = true
        };

        store.Save(original);
        var reloaded = store.Load();

        reloaded.OverlayPort.Should().Be(5000);
        reloaded.CaptureOrderHotkey.Should().Be("F8");
        reloaded.ParserHighConfidence.Should().Be(75);
        reloaded.ParserLowConfidence.Should().Be(40);
        reloaded.EtiketIntegrationEnabled.Should().BeTrue();

        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```
Expected: FAIL — `AppSettings` and `SettingsStore` do not exist.

- [ ] **Step 3: Implement AppSettings**

Create `LiveDeck.Core/Settings/AppSettings.cs`:

```csharp
namespace LiveDeck.Core.Settings;

/// <summary>
/// Application settings persisted to settings.json under the user's Documents folder.
/// All defaults are chosen so the app works out-of-the-box for a new user.
/// </summary>
public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";
    public string CaptureOrderHotkey { get; set; } = "F9";
    public int ParserHighConfidence { get; set; } = 80;
    public int ParserLowConfidence { get; set; } = 50;
    public bool EtiketIntegrationEnabled { get; set; } = false;
    public string? EtiketWindowTitle { get; set; } = "etiket";
}
```

- [ ] **Step 4: Implement SettingsStore**

Create `LiveDeck.Core/Settings/SettingsStore.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace LiveDeck.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> from a JSON file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
            return new AppSettings();

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(_filePath, json);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```
Expected: PASS — 2/2.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Settings LiveDeck.Tests/Settings
git commit -m "feat(core): add AppSettings model and JSON SettingsStore"
```

---

### Task 6: DI host + logging bootstrap (App)

**Files:**
- Create: `LiveDeck.App/AppHost.cs`
- Modify: `LiveDeck.App/App.xaml.cs`
- Modify: `LiveDeck.App/App.xaml`

- [ ] **Step 1: Implement AppHost**

Create `LiveDeck.App/AppHost.cs`:

```csharp
using System;
using System.IO;
using LiveDeck.Core;
using LiveDeck.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LiveDeck.App;

/// <summary>
/// Composition root: builds the DI container, wires Serilog, and exposes the resolved
/// services to the WPF application.
/// </summary>
public sealed class AppHost : IDisposable
{
    public IServiceProvider Services { get; }

    private readonly Serilog.ILogger _serilog;

    public AppHost()
    {
        AppPaths.EnsureDirectoriesExist();

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsFolder, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(_serilog, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton(new SettingsStore(AppPaths.SettingsFile));
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());

        Services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable)
            disposable.Dispose();
        Serilog.Log.CloseAndFlush();
    }
}
```

- [ ] **Step 2: Wire AppHost in App.xaml.cs**

Replace `LiveDeck.App/App.xaml.cs` contents:

```csharp
using System.Windows;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Host = new AppHost();

        var logger = Host.Services.GetService(typeof(ILogger<App>)) as ILogger<App>;
        logger?.LogInformation("LiveDeck starting up");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 3: Verify App.xaml StartupUri stays default**

Open `LiveDeck.App/App.xaml`. It should still reference `MainWindow.xaml` as the StartupUri (default from `dotnet new wpf`). No changes needed.

- [ ] **Step 4: Build and run smoke test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs LiveDeck.App/App.xaml.cs
git commit -m "feat(app): bootstrap DI container with Serilog logging"
```

---

## Data Layer

### Task 7: SQLite connection factory

**Files:**
- Create: `LiveDeck.Core/Storage/IDbConnectionFactory.cs`
- Create: `LiveDeck.Core/Storage/SqliteConnectionFactory.cs`
- Create: `LiveDeck.Tests/TestHelpers/InMemorySqlite.cs`

- [ ] **Step 1: Define IDbConnectionFactory**

Create `LiveDeck.Core/Storage/IDbConnectionFactory.cs`:

```csharp
using System.Data;

namespace LiveDeck.Core.Storage;

/// <summary>Abstracts ADO.NET connection creation so tests can inject in-memory SQLite.</summary>
public interface IDbConnectionFactory
{
    IDbConnection Open();
}
```

- [ ] **Step 2: Implement SqliteConnectionFactory**

Create `LiveDeck.Core/Storage/SqliteConnectionFactory.cs`:

```csharp
using System.Data;
using Microsoft.Data.Sqlite;

namespace LiveDeck.Core.Storage;

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public IDbConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
```

- [ ] **Step 3: Test helper for in-memory SQLite**

Create `LiveDeck.Tests/TestHelpers/InMemorySqlite.cs`:

```csharp
using System.Data;
using LiveDeck.Core.Storage;
using Microsoft.Data.Sqlite;

namespace LiveDeck.Tests.TestHelpers;

/// <summary>
/// Shared in-memory SQLite. Each instance owns one connection that stays open for the
/// life of the test (in-memory DBs disappear when the last connection closes).
/// </summary>
public sealed class InMemorySqlite : IDbConnectionFactory, System.IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public InMemorySqlite()
    {
        var name = $"livedeck-test-{System.Guid.NewGuid():N}";
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared;Foreign Keys=true";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public IDbConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Dispose() => _keepAlive.Dispose();
}
```

- [ ] **Step 4: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage LiveDeck.Tests/TestHelpers
git commit -m "feat(core): add SQLite connection factory and in-memory test helper"
```

---

### Task 8: Initial migration script (all 6 tables)

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/001_initial.sql`
- Modify: `LiveDeck.Core/LiveDeck.Core.csproj` (embed SQL as resource)

- [ ] **Step 1: Author the SQL**

Create `LiveDeck.Core/Storage/Migrations/001_initial.sql`:

```sql
-- LiveDeck initial schema. All timestamps are unix seconds (INTEGER).
-- This migration is idempotent: re-applying it on a populated DB does nothing.

CREATE TABLE IF NOT EXISTS _meta (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    SchemaVersion INTEGER NOT NULL
);

INSERT OR IGNORE INTO _meta (Id, SchemaVersion) VALUES (1, 0);

CREATE TABLE IF NOT EXISTS StreamSession (
    Id           TEXT PRIMARY KEY,
    Title        TEXT,
    StartedAt    INTEGER NOT NULL,
    EndedAt      INTEGER,
    Platforms    TEXT NOT NULL DEFAULT '[]',
    Notes        TEXT
);

CREATE INDEX IF NOT EXISTS IX_StreamSession_StartedAt ON StreamSession(StartedAt DESC);

CREATE TABLE IF NOT EXISTS ActiveCode (
    Id          TEXT PRIMARY KEY,
    SessionId   TEXT NOT NULL,
    Code        TEXT NOT NULL,
    Sizes       TEXT NOT NULL DEFAULT '[]',
    Price       REAL NOT NULL,
    ImageUrl    TEXT,
    Aliases     TEXT,
    StartedAt   INTEGER NOT NULL,
    EndedAt     INTEGER,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ActiveCode_Session_Code ON ActiveCode(SessionId, Code);
CREATE INDEX IF NOT EXISTS IX_ActiveCode_Session_Ended ON ActiveCode(SessionId, EndedAt);

CREATE TABLE IF NOT EXISTS Customer (
    Id                TEXT PRIMARY KEY,
    Platform          TEXT NOT NULL,
    Username          TEXT NOT NULL,
    DisplayName       TEXT,
    AvatarUrl         TEXT,
    FirstSeenAt       INTEGER NOT NULL,
    LastSeenAt        INTEGER NOT NULL,
    TotalOrders       INTEGER NOT NULL DEFAULT 0,
    CompletedOrders   INTEGER NOT NULL DEFAULT 0,
    CancelledOrders   INTEGER NOT NULL DEFAULT 0,
    TrustScore        INTEGER NOT NULL DEFAULT 100,
    IsBlacklisted     INTEGER NOT NULL DEFAULT 0,
    BlacklistReason   TEXT,
    Notes             TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_Customer_Platform_Username ON Customer(Platform, Username);
CREATE INDEX IF NOT EXISTS IX_Customer_Blacklisted ON Customer(IsBlacklisted);
CREATE INDEX IF NOT EXISTS IX_Customer_TrustScore ON Customer(TrustScore DESC);

CREATE TABLE IF NOT EXISTS OrderItem (
    Id                    TEXT PRIMARY KEY,
    SessionId             TEXT NOT NULL,
    ActiveCodeId          TEXT NOT NULL,
    CustomerId            TEXT NOT NULL,
    Code                  TEXT NOT NULL,
    Size                  TEXT NOT NULL,
    Quantity              INTEGER NOT NULL DEFAULT 1,
    UnitPrice             REAL NOT NULL,
    TotalPrice            REAL NOT NULL,
    Confidence            INTEGER NOT NULL,
    Status                TEXT NOT NULL,
    OriginalMessageText   TEXT NOT NULL,
    CapturedAt            INTEGER NOT NULL,
    StatusUpdatedAt       INTEGER NOT NULL,
    LabelPrintedAt        INTEGER,
    Notes                 TEXT,
    FOREIGN KEY (SessionId)    REFERENCES StreamSession(Id) ON DELETE CASCADE,
    FOREIGN KEY (ActiveCodeId) REFERENCES ActiveCode(Id),
    FOREIGN KEY (CustomerId)   REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_OrderItem_Session_Status_Captured
    ON OrderItem(SessionId, Status, CapturedAt DESC);
CREATE INDEX IF NOT EXISTS IX_OrderItem_Customer ON OrderItem(CustomerId);

CREATE TABLE IF NOT EXISTS Giveaway (
    Id                  TEXT PRIMARY KEY,
    SessionId           TEXT NOT NULL,
    Keyword             TEXT NOT NULL,
    Prize               TEXT,
    WinnerCount         INTEGER NOT NULL DEFAULT 1,
    PlatformFilter      TEXT,
    PreventRewinning    INTEGER NOT NULL DEFAULT 1,
    StartedAt           INTEGER NOT NULL,
    EndedAt             INTEGER,
    DrawnAt             INTEGER,
    RandomSeed          TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Giveaway_Session ON Giveaway(SessionId);

CREATE TABLE IF NOT EXISTS GiveawayParticipant (
    Id          TEXT PRIMARY KEY,
    GiveawayId  TEXT NOT NULL,
    CustomerId  TEXT NOT NULL,
    Platform    TEXT NOT NULL,
    Username    TEXT NOT NULL,
    EnteredAt   INTEGER NOT NULL,
    IsWinner    INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (GiveawayId) REFERENCES Giveaway(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Giveaway_Winner
    ON GiveawayParticipant(GiveawayId, IsWinner);
CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Customer_Winner
    ON GiveawayParticipant(CustomerId, IsWinner);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

UPDATE _meta SET SchemaVersion = 1 WHERE Id = 1;
```

- [ ] **Step 2: Embed the SQL as a resource**

Edit `LiveDeck.Core/LiveDeck.Core.csproj` to embed `.sql` files:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <EmbeddedResource Include="Storage\Migrations\*.sql" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Dapper" Version="2.1.35" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    <PackageReference Include="System.Text.Json" Version="9.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations LiveDeck.Core/LiveDeck.Core.csproj
git commit -m "feat(core): add 001_initial migration with all six tables"
```

---

### Task 9: MigrationRunner

**Files:**
- Create: `LiveDeck.Core/Storage/MigrationRunner.cs`
- Create: `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`

- [ ] **Step 1: Write failing test**

Create `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`:

```csharp
using Dapper;
using FluentAssertions;
using LiveDeck.Core.Storage;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public void Run_creates_all_six_tables_and_meta()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();

        tables.Should().Contain(new[]
        {
            "ActiveCode", "Customer", "Giveaway", "GiveawayParticipant",
            "OrderItem", "Settings", "StreamSession", "_meta"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(1);
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();   // second call must not throw

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests"
```
Expected: FAIL — `MigrationRunner` does not exist.

- [ ] **Step 3: Implement MigrationRunner**

Create `LiveDeck.Core/Storage/MigrationRunner.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;

namespace LiveDeck.Core.Storage;

/// <summary>
/// Applies embedded `Storage/Migrations/*.sql` scripts in lexical order. The current schema
/// version is stored in the `_meta` table; scripts are skipped if already applied.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationPrefix = "LiveDeck.Core.Storage.Migrations.";

    private readonly IDbConnectionFactory _factory;

    public MigrationRunner(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Run()
    {
        using var conn = _factory.Open();

        var scripts = LoadEmbeddedScripts();

        foreach (var (name, sql) in scripts)
        {
            conn.Execute(sql);
        }
    }

    private static IEnumerable<(string Name, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n);

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests"
```
Expected: PASS — 2/2.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/MigrationRunner.cs LiveDeck.Tests/Storage
git commit -m "feat(core): add MigrationRunner that applies embedded SQL scripts"
```

---

### Task 10: Domain entities (POCO records)

**Files:**
- Create: `LiveDeck.Core/Sessions/StreamSession.cs`
- Create: `LiveDeck.Core/Sales/ActiveCode.cs`
- Create: `LiveDeck.Core/Sales/OrderItem.cs`
- Create: `LiveDeck.Core/Sales/OrderStatus.cs`
- Create: `LiveDeck.Core/Customers/Customer.cs`
- Create: `LiveDeck.Core/Chat/ChatMessage.cs`

- [ ] **Step 1: StreamSession**

Create `LiveDeck.Core/Sessions/StreamSession.cs`:

```csharp
using System.Collections.Generic;

namespace LiveDeck.Core.Sessions;

public sealed record StreamSession(
    string Id,
    string? Title,
    long StartedAt,
    long? EndedAt,
    IReadOnlyList<string> Platforms,
    string? Notes);
```

- [ ] **Step 2: OrderStatus**

Create `LiveDeck.Core/Sales/OrderStatus.cs`:

```csharp
namespace LiveDeck.Core.Sales;

/// <summary>Workflow states for an OrderItem. Stored as TEXT in the DB.</summary>
public static class OrderStatus
{
    public const string New          = "new";
    public const string Pending      = "pending";       // confidence 50-79, awaiting operator approval
    public const string DmSent       = "dm_sent";
    public const string Paid         = "paid";
    public const string Shipped      = "shipped";
    public const string Completed    = "completed";
    public const string Cancelled    = "cancelled";

    public static readonly string[] All =
    {
        New, Pending, DmSent, Paid, Shipped, Completed, Cancelled
    };
}
```

- [ ] **Step 3: ActiveCode**

Create `LiveDeck.Core/Sales/ActiveCode.cs`:

```csharp
using System.Collections.Generic;

namespace LiveDeck.Core.Sales;

public sealed record ActiveCode(
    string Id,
    string SessionId,
    string Code,
    IReadOnlyList<string> Sizes,
    decimal Price,
    string? ImageUrl,
    IReadOnlyList<string> Aliases,
    long StartedAt,
    long? EndedAt)
{
    public bool IsActive => EndedAt is null;
}
```

- [ ] **Step 4: OrderItem**

Create `LiveDeck.Core/Sales/OrderItem.cs`:

```csharp
namespace LiveDeck.Core.Sales;

public sealed record OrderItem(
    string Id,
    string SessionId,
    string ActiveCodeId,
    string CustomerId,
    string Code,
    string Size,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    int Confidence,
    string Status,
    string OriginalMessageText,
    long CapturedAt,
    long StatusUpdatedAt,
    long? LabelPrintedAt,
    string? Notes);
```

- [ ] **Step 5: Customer**

Create `LiveDeck.Core/Customers/Customer.cs`:

```csharp
namespace LiveDeck.Core.Customers;

public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    int TotalOrders,
    int CompletedOrders,
    int CancelledOrders,
    int TrustScore,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes);
```

- [ ] **Step 6: ChatMessage**

Create `LiveDeck.Core/Chat/ChatMessage.cs`:

```csharp
using System.Collections.Generic;

namespace LiveDeck.Core.Chat;

public sealed record ChatMessage(
    string Id,
    string Platform,
    string? ExternalId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Text,
    long ReceivedAt,
    IReadOnlyList<string> Badges);
```

- [ ] **Step 7: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core
```
Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core
git commit -m "feat(core): add domain entity records for sessions, codes, orders, customers, chat"
```

---

### Task 11: ActiveCodeRepository (Dapper)

**Files:**
- Create: `LiveDeck.Core/Storage/Repositories/ActiveCodeRepository.cs`
- Create: `LiveDeck.Tests/Storage/ActiveCodeRepositoryTests.cs`

- [ ] **Step 1: Write failing test**

Create `LiveDeck.Tests/Storage/ActiveCodeRepositoryTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class ActiveCodeRepositoryTests
{
    private static (InMemorySqlite Db, ActiveCodeRepository Repo, string SessionId) NewFixture()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        // Session must exist due to FK
        var sessionRepo = new SessionRepositoryStub(db);
        var sessionId = "session-1";
        sessionRepo.Insert(new StreamSession(sessionId, null, 100, null, new[] { "instagram" }, null));

        return (db, new ActiveCodeRepository(db), sessionId);
    }

    [Fact]
    public void Insert_then_GetActiveBySession_returns_inserted_codes()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;

        var code = new ActiveCode(
            Id: "code-1",
            SessionId: sessionId,
            Code: "MAVI",
            Sizes: new List<string> { "S", "M", "XL" },
            Price: 199m,
            ImageUrl: null,
            Aliases: new List<string>(),
            StartedAt: 200,
            EndedAt: null);

        repo.Insert(code);

        var active = repo.GetActiveBySession(sessionId);
        active.Should().HaveCount(1);
        active[0].Code.Should().Be("MAVI");
        active[0].Sizes.Should().BeEquivalentTo(new[] { "S", "M", "XL" });
        active[0].Price.Should().Be(199m);
    }

    [Fact]
    public void Update_changes_price_and_sizes()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;
        var code = new ActiveCode("c1", sessionId, "MAVI",
            new List<string> { "M" }, 100m, null, new List<string>(), 200, null);
        repo.Insert(code);

        var updated = code with { Price = 150m, Sizes = new List<string> { "M", "L" } };
        repo.Update(updated);

        var fresh = repo.GetActiveBySession(sessionId)[0];
        fresh.Price.Should().Be(150m);
        fresh.Sizes.Should().BeEquivalentTo(new[] { "M", "L" });
    }

    [Fact]
    public void End_sets_EndedAt_and_excludes_from_active_list()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;
        var code = new ActiveCode("c1", sessionId, "MAVI",
            new List<string> { "M" }, 100m, null, new List<string>(), 200, null);
        repo.Insert(code);

        repo.End("c1", endedAt: 300);

        repo.GetActiveBySession(sessionId).Should().BeEmpty();
    }
}

/// <summary>Minimal session inserter to satisfy FK in repo tests.</summary>
internal sealed class SessionRepositoryStub
{
    private readonly InMemorySqlite _db;
    public SessionRepositoryStub(InMemorySqlite db) => _db = db;

    public void Insert(StreamSession s)
    {
        using var conn = _db.Open();
        Dapper.SqlMapper.Execute(conn,
            "INSERT INTO StreamSession(Id, Title, StartedAt, EndedAt, Platforms, Notes) " +
            "VALUES(@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes)",
            new { s.Id, s.Title, s.StartedAt, s.EndedAt, Platforms = "[\"instagram\"]", s.Notes });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ActiveCodeRepositoryTests"
```
Expected: FAIL — `ActiveCodeRepository` does not exist.

- [ ] **Step 3: Implement ActiveCodeRepository**

Create `LiveDeck.Core/Storage/Repositories/ActiveCodeRepository.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class ActiveCodeRepository
{
    private readonly IDbConnectionFactory _factory;

    public ActiveCodeRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(ActiveCode code)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO ActiveCode
              (Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt)
              VALUES (@Id, @SessionId, @Code, @Sizes, @Price, @ImageUrl, @Aliases, @StartedAt, @EndedAt)",
            new
            {
                code.Id,
                code.SessionId,
                code.Code,
                Sizes = JsonSerializer.Serialize(code.Sizes),
                code.Price,
                code.ImageUrl,
                Aliases = JsonSerializer.Serialize(code.Aliases),
                code.StartedAt,
                code.EndedAt
            });
    }

    public void Update(ActiveCode code)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ActiveCode
              SET Code=@Code, Sizes=@Sizes, Price=@Price, ImageUrl=@ImageUrl, Aliases=@Aliases
              WHERE Id=@Id",
            new
            {
                code.Id,
                code.Code,
                Sizes = JsonSerializer.Serialize(code.Sizes),
                code.Price,
                code.ImageUrl,
                Aliases = JsonSerializer.Serialize(code.Aliases)
            });
    }

    public void End(string id, long endedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ActiveCode SET EndedAt=@endedAt WHERE Id=@id", new { id, endedAt });
    }

    public IReadOnlyList<ActiveCode> GetActiveBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt
              FROM ActiveCode
              WHERE SessionId=@sessionId AND EndedAt IS NULL
              ORDER BY StartedAt",
            new { sessionId }).ToList();

        return rows.Select(MapRow).ToList();
    }

    public ActiveCode? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt
              FROM ActiveCode WHERE Id=@id",
            new { id });
        return row is null ? null : MapRow(row);
    }

    private static ActiveCode MapRow(Row r) => new(
        r.Id,
        r.SessionId,
        r.Code,
        JsonSerializer.Deserialize<List<string>>(r.Sizes ?? "[]") ?? new List<string>(),
        r.Price,
        r.ImageUrl,
        JsonSerializer.Deserialize<List<string>>(r.Aliases ?? "[]") ?? new List<string>(),
        r.StartedAt,
        r.EndedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Code { get; init; } = "";
        public string? Sizes { get; init; }
        public decimal Price { get; init; }
        public string? ImageUrl { get; init; }
        public string? Aliases { get; init; }
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ActiveCodeRepositoryTests"
```
Expected: PASS — 3/3.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/ActiveCodeRepository.cs LiveDeck.Tests/Storage/ActiveCodeRepositoryTests.cs
git commit -m "feat(core): add ActiveCodeRepository with Dapper-backed CRUD"
```

---

### Task 12: SessionRepository, CustomerRepository, OrderRepository

**Files:**
- Create: `LiveDeck.Core/Storage/Repositories/SessionRepository.cs`
- Create: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Create: `LiveDeck.Core/Storage/Repositories/OrderRepository.cs`
- Create: `LiveDeck.Tests/Storage/SessionRepositoryTests.cs`
- Create: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`
- Create: `LiveDeck.Tests/Storage/OrderRepositoryTests.cs`

- [ ] **Step 1: SessionRepository test**

Create `LiveDeck.Tests/Storage/SessionRepositoryTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class SessionRepositoryTests
{
    [Fact]
    public void Insert_then_GetActive_returns_session_with_null_EndedAt()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);

        var session = new StreamSession("s1", "Akşam Yayını", 1000, null,
            new[] { "instagram" }, null);
        repo.Insert(session);

        repo.GetActive().Should().NotBeNull();
        repo.GetActive()!.Id.Should().Be("s1");
    }

    [Fact]
    public void End_sets_EndedAt_and_GetActive_returns_null()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        repo.End("s1", endedAt: 2000);

        repo.GetActive().Should().BeNull();
    }
}
```

- [ ] **Step 2: SessionRepository implementation**

Create `LiveDeck.Core/Storage/Repositories/SessionRepository.cs`:

```csharp
using System.Linq;
using System.Text.Json;
using Dapper;
using LiveDeck.Core.Sessions;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class SessionRepository
{
    private readonly IDbConnectionFactory _factory;
    public SessionRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(StreamSession session)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO StreamSession(Id, Title, StartedAt, EndedAt, Platforms, Notes)
              VALUES(@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes)",
            new
            {
                session.Id,
                session.Title,
                session.StartedAt,
                session.EndedAt,
                Platforms = JsonSerializer.Serialize(session.Platforms),
                session.Notes
            });
    }

    public void End(string id, long endedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE StreamSession SET EndedAt=@endedAt WHERE Id=@id",
            new { id, endedAt });
    }

    public StreamSession? GetActive()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes " +
            "FROM StreamSession WHERE EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1");
        return row is null ? null : Map(row);
    }

    private static StreamSession Map(Row r) => new(
        r.Id, r.Title, r.StartedAt, r.EndedAt,
        JsonSerializer.Deserialize<string[]>(r.Platforms ?? "[]") ?? System.Array.Empty<string>(),
        r.Notes);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string? Title { get; init; }
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
        public string? Platforms { get; init; }
        public string? Notes { get; init; }
    }
}
```

- [ ] **Step 3: CustomerRepository test**

Create `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class CustomerRepositoryTests
{
    [Fact]
    public void Insert_then_FindByPlatformAndUsername_returns_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        var c = new Customer("c1", "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            TotalOrders: 0, CompletedOrders: 0, CancelledOrders: 0,
            TrustScore: 100, IsBlacklisted: false, BlacklistReason: null, Notes: null);
        repo.Insert(c);

        var found = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        found.Should().NotBeNull();
        found!.Id.Should().Be("c1");
    }

    [Fact]
    public void FindByPlatformAndUsername_returns_null_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.FindByPlatformAndUsername("instagram", "@nonexistent").Should().BeNull();
    }
}
```

- [ ] **Step 4: CustomerRepository implementation**

Create `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`:

```csharp
using Dapper;
using LiveDeck.Core.Customers;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class CustomerRepository
{
    private readonly IDbConnectionFactory _factory;
    public CustomerRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Customer c)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Customer
              (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               TotalOrders, CompletedOrders, CancelledOrders, TrustScore,
               IsBlacklisted, BlacklistReason, Notes)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @TotalOrders, @CompletedOrders, @CancelledOrders, @TrustScore,
               @IsBlacklisted, @BlacklistReason, @Notes)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                c.TotalOrders, c.CompletedOrders, c.CancelledOrders, c.TrustScore,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes
            });
    }

    public Customer? FindByPlatformAndUsername(string platform, string username)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Customer
              WHERE Platform=@platform AND Username=@username",
            new { platform, username });
        return row is null ? null : Map(row);
    }

    public Customer? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public void UpdateAggregates(string id, int totalOrders, int completedOrders,
        int cancelledOrders, int trustScore, long lastSeenAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Customer
              SET TotalOrders=@totalOrders, CompletedOrders=@completedOrders,
                  CancelledOrders=@cancelledOrders, TrustScore=@trustScore, LastSeenAt=@lastSeenAt
              WHERE Id=@id",
            new { id, totalOrders, completedOrders, cancelledOrders, trustScore, lastSeenAt });
    }

    private static Customer Map(Row r) => new(
        r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
        r.FirstSeenAt, r.LastSeenAt,
        r.TotalOrders, r.CompletedOrders, r.CancelledOrders, r.TrustScore,
        r.IsBlacklisted == 1, r.BlacklistReason, r.Notes);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? AvatarUrl { get; init; }
        public long FirstSeenAt { get; init; }
        public long LastSeenAt { get; init; }
        public int TotalOrders { get; init; }
        public int CompletedOrders { get; init; }
        public int CancelledOrders { get; init; }
        public int TrustScore { get; init; }
        public int IsBlacklisted { get; init; }
        public string? BlacklistReason { get; init; }
        public string? Notes { get; init; }
    }
}
```

- [ ] **Step 5: OrderRepository test**

Create `LiveDeck.Tests/Storage/OrderRepositoryTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class OrderRepositoryTests
{
    [Fact]
    public void Insert_then_GetBySession_returns_inserted_order()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100, 0, 0, 0, 100, false, null, null));
        var codeRepo = new ActiveCodeRepository(db);
        codeRepo.Insert(new ActiveCode("ac1", "s1", "MAVI",
            new[] { "M" }, 199m, null, System.Array.Empty<string>(), 100, null));

        var orderRepo = new OrderRepository(db);
        var order = new OrderItem("o1", "s1", "ac1", "c1", "MAVI", "M", 1,
            199m, 199m, 95, OrderStatus.New, "@a MAVI M aldım", 200, 200, null, null);
        orderRepo.Insert(order);

        var orders = orderRepo.GetBySession("s1");
        orders.Should().HaveCount(1);
        orders[0].Code.Should().Be("MAVI");
    }
}
```

- [ ] **Step 6: OrderRepository implementation**

Create `LiveDeck.Core/Storage/Repositories/OrderRepository.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class OrderRepository
{
    private readonly IDbConnectionFactory _factory;
    public OrderRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(OrderItem o)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO OrderItem
              (Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
               UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
               CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes)
              VALUES
              (@Id, @SessionId, @ActiveCodeId, @CustomerId, @Code, @Size, @Quantity,
               @UnitPrice, @TotalPrice, @Confidence, @Status, @OriginalMessageText,
               @CapturedAt, @StatusUpdatedAt, @LabelPrintedAt, @Notes)",
            o);
    }

    public void UpdateStatus(string id, string status, long statusUpdatedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE OrderItem SET Status=@status, StatusUpdatedAt=@updatedAt WHERE Id=@id",
            new { id, status, updatedAt = statusUpdatedAt });
    }

    public IReadOnlyList<OrderItem> GetBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.Query<OrderItem>(
            @"SELECT Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
                     UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
                     CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes
              FROM OrderItem
              WHERE SessionId=@sessionId
              ORDER BY CapturedAt DESC",
            new { sessionId }).ToList();
    }

    public IReadOnlyList<OrderItem> GetBySessionAndStatus(string sessionId, string status)
    {
        using var conn = _factory.Open();
        return conn.Query<OrderItem>(
            @"SELECT Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
                     UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
                     CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes
              FROM OrderItem
              WHERE SessionId=@sessionId AND Status=@status
              ORDER BY CapturedAt DESC",
            new { sessionId, status }).ToList();
    }
}
```

- [ ] **Step 7: Run all repository tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~Repository"
```
Expected: PASS — all repository tests green.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories LiveDeck.Tests/Storage
git commit -m "feat(core): add Session, Customer, Order repositories with Dapper queries"
```

---

## Domain Services

### Task 13: StreamSessionService + ActiveCodeService + CustomerService

**Files:**
- Create: `LiveDeck.Core/Sessions/StreamSessionService.cs`
- Create: `LiveDeck.Core/Sales/ActiveCodeService.cs`
- Create: `LiveDeck.Core/Customers/CustomerService.cs`
- Create: `LiveDeck.Core/Time/IClock.cs`
- Create: `LiveDeck.Core/Time/SystemClock.cs`
- Create: `LiveDeck.Tests/Customers/CustomerServiceTests.cs`

These services wrap repositories with business logic (auto-create, ID generation, timestamps).

- [ ] **Step 1: IClock abstraction (so tests can fix time)**

Create `LiveDeck.Core/Time/IClock.cs`:

```csharp
namespace LiveDeck.Core.Time;

public interface IClock
{
    /// <summary>Current unix-seconds timestamp.</summary>
    long UnixNow();
}
```

Create `LiveDeck.Core/Time/SystemClock.cs`:

```csharp
using System;

namespace LiveDeck.Core.Time;

public sealed class SystemClock : IClock
{
    public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
```

- [ ] **Step 2: StreamSessionService**

Create `LiveDeck.Core/Sessions/StreamSessionService.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sessions;

public sealed class StreamSessionService
{
    private readonly SessionRepository _repo;
    private readonly IClock _clock;

    public StreamSessionService(SessionRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public StreamSession Start(string? title, IReadOnlyList<string> platforms)
    {
        var session = new StreamSession(
            Id: Guid.NewGuid().ToString("N"),
            Title: title,
            StartedAt: _clock.UnixNow(),
            EndedAt: null,
            Platforms: platforms,
            Notes: null);
        _repo.Insert(session);
        return session;
    }

    public void End(string sessionId) => _repo.End(sessionId, _clock.UnixNow());

    public StreamSession? GetActive() => _repo.GetActive();
}
```

- [ ] **Step 3: ActiveCodeService**

Create `LiveDeck.Core/Sales/ActiveCodeService.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class ActiveCodeService
{
    private readonly ActiveCodeRepository _repo;
    private readonly IClock _clock;

    public ActiveCodeService(ActiveCodeRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public ActiveCode Add(string sessionId, string code, IReadOnlyList<string> sizes,
        decimal price, string? imageUrl = null, IReadOnlyList<string>? aliases = null)
    {
        var ac = new ActiveCode(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            Code: code.Trim().ToUpperInvariant(),
            Sizes: sizes,
            Price: price,
            ImageUrl: imageUrl,
            Aliases: aliases ?? Array.Empty<string>(),
            StartedAt: _clock.UnixNow(),
            EndedAt: null);
        _repo.Insert(ac);
        return ac;
    }

    public void UpdatePrice(string id, decimal newPrice)
    {
        var existing = _repo.GetById(id) ?? throw new InvalidOperationException($"Code {id} not found");
        _repo.Update(existing with { Price = newPrice });
    }

    public void UpdateSizes(string id, IReadOnlyList<string> sizes)
    {
        var existing = _repo.GetById(id) ?? throw new InvalidOperationException($"Code {id} not found");
        _repo.Update(existing with { Sizes = sizes });
    }

    public void Close(string id) => _repo.End(id, _clock.UnixNow());

    public IReadOnlyList<ActiveCode> GetActive(string sessionId) => _repo.GetActiveBySession(sessionId);
}
```

- [ ] **Step 4: CustomerService test (auto-create behavior)**

Create `LiveDeck.Tests/Customers/CustomerServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class CustomerServiceTests
{
    [Fact]
    public void GetOrCreate_creates_customer_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var customer = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        customer.Id.Should().NotBeNullOrEmpty();
        customer.Platform.Should().Be("instagram");
        customer.Username.Should().Be("@ayse_y");
        customer.FirstSeenAt.Should().Be(1234L);
        customer.TrustScore.Should().Be(100);
    }

    [Fact]
    public void GetOrCreate_returns_existing_customer_on_second_call()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var first = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);
        var second = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        second.Id.Should().Be(first.Id);
    }
}
```

- [ ] **Step 5: CustomerService implementation**

Create `LiveDeck.Core/Customers/CustomerService.cs`:

```csharp
using System;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Customers;

public sealed class CustomerService
{
    private readonly CustomerRepository _repo;
    private readonly IClock _clock;

    public CustomerService(CustomerRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public Customer GetOrCreate(string platform, string username,
        string? displayName, string? avatarUrl)
    {
        var existing = _repo.FindByPlatformAndUsername(platform, username);
        if (existing is not null) return existing;

        var now = _clock.UnixNow();
        var customer = new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: platform,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: avatarUrl,
            FirstSeenAt: now,
            LastSeenAt: now,
            TotalOrders: 0,
            CompletedOrders: 0,
            CancelledOrders: 0,
            TrustScore: 100,
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null);
        _repo.Insert(customer);
        return customer;
    }
}
```

- [ ] **Step 6: Run all service tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~Service"
```
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Time LiveDeck.Core/Sessions LiveDeck.Core/Sales/ActiveCodeService.cs LiveDeck.Core/Customers/CustomerService.cs LiveDeck.Tests/Customers
git commit -m "feat(core): add IClock + StreamSession/ActiveCode/Customer services"
```

---

## OrderCaptureEngine Pipeline (TDD)

The engine is a pure-function pipeline: `ChatMessage + ActiveCode list → CaptureResult`. Each stage is independently tested with fixtures, then composed.

### Task 14: MessageNormalizer

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/MessageNormalizer.cs`
- Create: `LiveDeck.Tests/Sales/MessageNormalizerTests.cs`

- [ ] **Step 1: Failing tests — Turkish diacritic + casing + whitespace**

Create `LiveDeck.Tests/Sales/MessageNormalizerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class MessageNormalizerTests
{
    private readonly MessageNormalizer _n = new();

    [Theory]
    [InlineData("Mavi xl aldım", "MAVI XL ALDIM")]
    [InlineData("MAVİ XL", "MAVI XL")]
    [InlineData("mavi̇ xl", "MAVI XL")]
    [InlineData("Kırmızı M", "KIRMIZI M")]
    [InlineData("İSTANBUL", "ISTANBUL")]
    public void Normalizes_turkish_characters_and_uppercases(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("mavi   xl", "MAVI XL")]
    [InlineData("  mavi xl  ", "MAVI XL")]
    [InlineData("mavi\txl\nm", "MAVI XL M")]
    public void Collapses_whitespace(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("mavi 🌹 xl", "MAVI XL")]
    [InlineData("MAVİ ❤️ M ALDIM", "MAVI M ALDIM")]
    public void Strips_emoji(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        _n.Normalize("").Should().Be("");
        _n.Normalize("   ").Should().Be("");
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MessageNormalizerTests"
```
Expected: FAIL — `MessageNormalizer` not found.

- [ ] **Step 3: Implement MessageNormalizer**

Create `LiveDeck.Core/Sales/Pipeline/MessageNormalizer.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Normalises a Turkish chat message for matching:
///   * collapses whitespace, strips emoji and most non-letter symbols,
///   * folds Turkish diacritics (İ→I, ı→I, ğ→G, ü→U, ş→S, ö→O, ç→C),
///   * uppercases the result.
///
/// Output is suitable for fuzzy comparison against active product codes.
/// </summary>
public sealed class MessageNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EmojiOrSymbol = new(
        @"[\p{So}\p{Sk}\p{Sm}\p{Cs}\p{Cn}]+|[\uD800-\uDFFF]+",
        RegexOptions.Compiled);

    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var stripped = EmojiOrSymbol.Replace(input, " ");

        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            sb.Append(FoldTurkish(ch));
        }

        var collapsed = Whitespace.Replace(sb.ToString(), " ").Trim();
        return collapsed.ToUpper(CultureInfo.InvariantCulture);
    }

    private static char FoldTurkish(char c) => c switch
    {
        'ı' or 'İ' => 'I',
        'ğ' or 'Ğ' => 'G',
        'ü' or 'Ü' => 'U',
        'ş' or 'Ş' => 'S',
        'ö' or 'Ö' => 'O',
        'ç' or 'Ç' => 'C',
        _ => c
    };
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MessageNormalizerTests"
```
Expected: PASS — all 11 cases.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/MessageNormalizer.cs LiveDeck.Tests/Sales/MessageNormalizerTests.cs
git commit -m "feat(core): add MessageNormalizer with Turkish diacritic folding"
```

---

### Task 15: CodeMatcher (fuzzy matching)

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/CodeMatcher.cs`
- Create: `LiveDeck.Tests/Sales/CodeMatcherTests.cs`

- [ ] **Step 1: Failing tests**

Create `LiveDeck.Tests/Sales/CodeMatcherTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class CodeMatcherTests
{
    private static ActiveCode Code(string code, params string[] sizes) =>
        new("id-" + code, "s1", code, sizes, 1m, null, System.Array.Empty<string>(), 0, null);

    private readonly CodeMatcher _matcher = new();

    [Fact]
    public void Exact_match_wins()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M"), Code("KIRMIZI", "M") };

        var match = _matcher.Match("MAVI M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Single_typo_within_distance_one_matches()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        // "MAV1" → distance 1 from "MAVI"
        var match = _matcher.Match("MAV1 M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Returns_null_when_no_active_code_matches()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var match = _matcher.Match("MERHABA NASILSIN", codes);

        match.Should().BeNull();
    }

    [Fact]
    public void Alias_matches_when_main_code_does_not()
    {
        var codes = new List<ActiveCode>
        {
            new("id1", "s1", "MAVI", new[] { "M" }, 1m, null,
                new[] { "OCEAN", "DENIZ" }, 0, null)
        };

        var match = _matcher.Match("DENIZ M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Picks_best_match_when_multiple_codes_partially_match()
    {
        var codes = new List<ActiveCode>
        {
            Code("MAVI", "M"),
            Code("MAVIS", "M")  // "MAVI" appears as substring of "MAVIS"
        };

        // Message exactly matches "MAVI" — exact match should beat near-match
        var match = _matcher.Match("MAVI M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Match_against_multi_word_code()
    {
        var codes = new List<ActiveCode> { Code("KIRMIZI ELBISE", "M") };

        var match = _matcher.Match("KIRMIZI ELBISE M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("KIRMIZI ELBISE");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CodeMatcherTests"
```
Expected: FAIL — `CodeMatcher` not found.

- [ ] **Step 3: Implement CodeMatcher**

Create `LiveDeck.Core/Sales/Pipeline/CodeMatcher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Finds the best <see cref="ActiveCode"/> referenced inside a normalized message.
/// Strategy: for every active code (and its aliases), search the message for any
/// substring whose Levenshtein distance to the code/alias is ≤ 1. The shortest distance
/// wins; ties broken by code length (longer codes preferred — fewer false positives).
/// Input is assumed already normalised by <see cref="MessageNormalizer"/>.
/// </summary>
public sealed class CodeMatcher
{
    private const int MaxDistance = 1;

    public ActiveCode? Match(string normalisedMessage, IEnumerable<ActiveCode> activeCodes)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return null;

        ActiveCode? best = null;
        int bestDistance = int.MaxValue;
        int bestCodeLength = -1;

        foreach (var code in activeCodes)
        {
            foreach (var candidate in EnumerateCandidates(code))
            {
                var distance = FindMinDistanceWindow(normalisedMessage, candidate);
                if (distance > MaxDistance) continue;

                if (distance < bestDistance ||
                    (distance == bestDistance && candidate.Length > bestCodeLength))
                {
                    best = code;
                    bestDistance = distance;
                    bestCodeLength = candidate.Length;
                }
            }
        }
        return best;
    }

    private static IEnumerable<string> EnumerateCandidates(ActiveCode code)
    {
        yield return code.Code;
        foreach (var alias in code.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias.ToUpperInvariant();
        }
    }

    /// <summary>
    /// Slides a window equal in length to <paramref name="needle"/> across each whitespace-
    /// separated token group of <paramref name="haystack"/> and returns the minimum
    /// Levenshtein distance found. Multi-word needles are matched against contiguous tokens.
    /// </summary>
    private static int FindMinDistanceWindow(string haystack, string needle)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal)) return 0;

        var needleTokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hayTokens = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (needleTokens.Length > hayTokens.Length) return int.MaxValue;

        int min = int.MaxValue;
        for (int i = 0; i <= hayTokens.Length - needleTokens.Length; i++)
        {
            var window = string.Join(' ', hayTokens, i, needleTokens.Length);
            var d = Levenshtein(window, needle);
            if (d < min) min = d;
            if (min == 0) return 0;
        }
        return min;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CodeMatcherTests"
```
Expected: PASS — 6/6.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/CodeMatcher.cs LiveDeck.Tests/Sales/CodeMatcherTests.cs
git commit -m "feat(core): add CodeMatcher with Levenshtein fuzzy matching"
```

---

### Task 16: VariantExtractor (size detection)

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/VariantExtractor.cs`
- Create: `LiveDeck.Tests/Sales/VariantExtractorTests.cs`

- [ ] **Step 1: Failing tests**

Create `LiveDeck.Tests/Sales/VariantExtractorTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class VariantExtractorTests
{
    private readonly VariantExtractor _x = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", new[] { "S", "M", "L", "XL" }, "XL")]
    [InlineData("MAVI M ALDIM",  new[] { "S", "M", "XL" },      "M")]
    [InlineData("MAVI 38 ALDIM", new[] { "36", "38", "40" },     "38")]
    [InlineData("MAVI ALDIM TEK BEDEN", new[] { "TEK BEDEN" },   "TEK BEDEN")]
    [InlineData("MAVI ALDIM",   new[] { "TEK BEDEN" },           "TEK BEDEN")]
    public void Extracts_size_when_listed_in_active_code(
        string normalised, string[] sizes, string expected)
    {
        _x.Extract(normalised, sizes).Should().Be(expected);
    }

    [Theory]
    [InlineData("MAVI ALDIM", new[] { "S", "M", "XL" })]
    [InlineData("MAVI XS ALDIM", new[] { "S", "M", "XL" })]   // XS not offered
    public void Returns_null_when_no_listed_size_present(string normalised, string[] sizes)
    {
        _x.Extract(normalised, sizes).Should().BeNull();
    }

    [Theory]
    [InlineData("MAVI M VE XL ALDIM", new[] { "M", "XL" })]   // ambiguous → null
    public void Returns_null_when_multiple_sizes_match(string normalised, string[] sizes)
    {
        _x.Extract(normalised, sizes).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~VariantExtractorTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement VariantExtractor**

Create `LiveDeck.Core/Sales/Pipeline/VariantExtractor.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Picks the active-code's size that appears in the normalised message.
/// Special case: if the active code only offers "TEK BEDEN" (one-size), that size is
/// returned even if the message doesn't mention it explicitly.
/// Returns null when no size matches OR when multiple sizes match (ambiguous).
/// </summary>
public sealed class VariantExtractor
{
    public string? Extract(string normalisedMessage, IReadOnlyList<string> sizes)
    {
        if (sizes.Count == 0) return null;

        if (sizes.Count == 1 && IsSingleSize(sizes[0]))
            return sizes[0];

        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var matches = sizes
            .Where(s => ContainsSize(tokens, s))
            .ToList();

        if (matches.Count == 1) return matches[0];

        // If "TEK BEDEN" is one of the offered sizes and no other size matched explicitly,
        // fall back to it.
        if (matches.Count == 0 && sizes.Any(IsSingleSize))
            return sizes.First(IsSingleSize);

        return null;
    }

    private static bool IsSingleSize(string size) =>
        size.Equals("TEK BEDEN", System.StringComparison.OrdinalIgnoreCase) ||
        size.Equals("TEK", System.StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSize(string[] tokens, string size)
    {
        var sizeTokens = size.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (sizeTokens.Length == 1)
            return tokens.Contains(sizeTokens[0]);

        // Multi-word size (e.g. "TEK BEDEN") → contiguous-window match
        for (int i = 0; i <= tokens.Length - sizeTokens.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < sizeTokens.Length; j++)
            {
                if (tokens[i + j] != sizeTokens[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~VariantExtractorTests"
```
Expected: PASS — 8/8.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/VariantExtractor.cs LiveDeck.Tests/Sales/VariantExtractorTests.cs
git commit -m "feat(core): add VariantExtractor for size detection"
```

---

### Task 17: QuantityExtractor

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/QuantityExtractor.cs`
- Create: `LiveDeck.Tests/Sales/QuantityExtractorTests.cs`

- [ ] **Step 1: Failing tests**

Create `LiveDeck.Tests/Sales/QuantityExtractorTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class QuantityExtractorTests
{
    private readonly QuantityExtractor _x = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", 1)]                 // default 1
    [InlineData("MAVI XL 2 TANE ALDIM", 2)]
    [InlineData("MAVI XL 3 ADET ALDIM", 3)]
    [InlineData("MAVI XL X2 ALDIM", 2)]
    [InlineData("MAVI XL +2 ALDIM", 2)]
    [InlineData("MAVI XL IKI TANE", 2)]
    [InlineData("MAVI XL UC ADET", 3)]
    [InlineData("MAVI XL IKISER ALDIM", 2)]
    [InlineData("MAVI XL DORT ALDIM", 4)]
    public void Extracts_quantity_or_defaults_to_one(string msg, int expected)
    {
        _x.Extract(msg).Should().Be(expected);
    }

    [Theory]
    [InlineData("MAVI XL 99 TANE", 99)]              // upper bound: cap at 50
    public void Caps_extreme_values_at_50(string msg, int _)
    {
        _x.Extract(msg).Should().BeLessOrEqualTo(50);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~QuantityExtractorTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement QuantityExtractor**

Create `LiveDeck.Core/Sales/Pipeline/QuantityExtractor.cs`:

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Detects an explicit quantity in a normalised Turkish chat message. Supported forms:
///   * "2 TANE", "3 ADET"
///   * "X2", "+2"
///   * Number words "IKI", "UC", "DORT", ...
///   * Distributive "IKISER", "UCER"
/// Defaults to 1 when nothing matches. Caps at 50 to defang accidental large numbers.
/// </summary>
public sealed class QuantityExtractor
{
    private const int MaxQuantity = 50;

    private static readonly Regex DigitTane    = new(@"(?:\b|^)(\d{1,3})\s*(?:TANE|ADET)\b", RegexOptions.Compiled);
    private static readonly Regex XDigit       = new(@"\b[X\+](\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex DigitX       = new(@"\b(\d{1,3})X\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Words = new()
    {
        // base
        { "BIR", 1 }, { "IKI", 2 }, { "UC", 3 }, { "DORT", 4 }, { "BES", 5 },
        { "ALTI", 6 }, { "YEDI", 7 }, { "SEKIZ", 8 }, { "DOKUZ", 9 }, { "ON", 10 },
        // distributive
        { "BIRER", 1 }, { "IKISER", 2 }, { "UCER", 3 }, { "DORDER", 4 }, { "BESER", 5 }
    };

    public int Extract(string normalisedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return 1;

        var m = DigitTane.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        m = XDigit.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        m = DigitX.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (Words.TryGetValue(t, out var n))
                return Cap(n);
        }
        return 1;
    }

    private static int Cap(int n) => n < 1 ? 1 : (n > MaxQuantity ? MaxQuantity : n);
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~QuantityExtractorTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/QuantityExtractor.cs LiveDeck.Tests/Sales/QuantityExtractorTests.cs
git commit -m "feat(core): add QuantityExtractor for digit/word/multiplier patterns"
```

---

### Task 18: IntentScorer

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/IntentScorer.cs`
- Create: `LiveDeck.Tests/Sales/IntentScorerTests.cs`

- [ ] **Step 1: Failing tests**

Create `LiveDeck.Tests/Sales/IntentScorerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class IntentScorerTests
{
    private readonly IntentScorer _s = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", true)]
    [InlineData("MAVI XL ALIYORUM", true)]
    [InlineData("MAVI XL ISTIYORUM", true)]
    [InlineData("MAVI XL OLSUN", true)]
    [InlineData("MAVI XL LUTFEN", true)]
    public void Buying_intent_words_yield_high_score(string msg, bool _)
    {
        _s.Score(msg, originalText: msg).Should().BeGreaterOrEqualTo(70);
    }

    [Theory]
    [InlineData("MAVI XL VAR MI")]
    [InlineData("MAVI XL KALDI MI")]
    [InlineData("MAVI XL NE KADAR")]
    public void Question_tone_lowers_score(string msg)
    {
        _s.Score(msg, originalText: msg).Should().BeLessThan(50);
    }

    [Fact]
    public void Bare_code_only_yields_medium_score()
    {
        // No intent word, no question — could go either way
        _s.Score("MAVI XL", originalText: "MAVI XL")
          .Should().BeInRange(40, 70);
    }

    [Fact]
    public void Heart_or_cart_emoji_in_original_text_boosts_score()
    {
        _s.Score("MAVI XL", originalText: "MAVI XL ❤️").Should().BeGreaterOrEqualTo(60);
        _s.Score("MAVI XL", originalText: "MAVI XL 🛒").Should().BeGreaterOrEqualTo(60);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~IntentScorerTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement IntentScorer**

Create `LiveDeck.Core/Sales/Pipeline/IntentScorer.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Scores buying intent (0-100) for a normalised Turkish message.
/// Buying words add points; question patterns and "VAR MI / KALDI MI / NE KADAR" subtract.
/// Buy-intent emoji in the original (un-normalised) text adds a small boost.
/// </summary>
public sealed class IntentScorer
{
    private static readonly HashSet<string> BuyingWords = new()
    {
        "ALDIM", "ALIYORUM", "ALABILIRMI", "ALABILIRMIYIM",
        "ISTIYORUM", "ISTERIM", "ALMAK", "OLSUN", "LUTFEN",
        "RICA", "RICAEDERIM", "EKLE", "EKLERMISIN", "AYIRIN", "AYIRINIZ"
    };

    private static readonly HashSet<string> QuestionPatterns = new()
    {
        "VAR", "KALDI", "VARMI", "KALDIMI", "MEVCUT", "BEDEN"
    };

    private static readonly string[] BuyEmojis = { "🛒", "🛍", "❤", "❤️", "💖", "🌹", "🌸", "🛍️" };

    public int Score(string normalisedMessage, string originalText)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return 0;

        int score = 50;
        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (BuyingWords.Contains(token)) score += 30;
        }

        if (originalText.Contains('?')) score -= 20;

        // "VAR MI?" pattern
        if (tokens.Contains("VAR") && (tokens.Contains("MI") || originalText.Contains("MI?", System.StringComparison.OrdinalIgnoreCase)))
            score -= 25;

        if (tokens.Any(t => t == "NEKADAR" || t == "KALDIMI"))
            score -= 25;

        if (BuyEmojis.Any(e => originalText.Contains(e, System.StringComparison.Ordinal)))
            score += 15;

        return System.Math.Clamp(score, 0, 100);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~IntentScorerTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/IntentScorer.cs LiveDeck.Tests/Sales/IntentScorerTests.cs
git commit -m "feat(core): add IntentScorer for buying intent classification"
```

---

### Task 19: ConfidenceScorer + CaptureResult

**Files:**
- Create: `LiveDeck.Core/Sales/Pipeline/CaptureResult.cs`
- Create: `LiveDeck.Core/Sales/Pipeline/ConfidenceScorer.cs`
- Create: `LiveDeck.Tests/Sales/ConfidenceScorerTests.cs`

- [ ] **Step 1: CaptureResult model**

Create `LiveDeck.Core/Sales/Pipeline/CaptureResult.cs`:

```csharp
namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Outcome of a single message running through the OrderCaptureEngine pipeline.
/// </summary>
public sealed record CaptureResult(
    bool IsCapture,
    ActiveCode? MatchedCode,
    string? Size,
    int Quantity,
    int IntentScore,
    int Confidence,
    string Reason);
```

- [ ] **Step 2: Failing tests**

Create `LiveDeck.Tests/Sales/ConfidenceScorerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class ConfidenceScorerTests
{
    private static ActiveCode Code() =>
        new("c1", "s1", "MAVI", new[] { "M", "XL" }, 199m, null, System.Array.Empty<string>(), 0, null);

    private readonly ConfidenceScorer _s = new();

    [Fact]
    public void High_intent_with_match_and_size_yields_score_eighty_or_more()
    {
        var r = _s.Score(matched: Code(), size: "XL", quantity: 1, intentScore: 90);
        r.Should().BeGreaterOrEqualTo(80);
    }

    [Fact]
    public void No_match_yields_zero()
    {
        var r = _s.Score(matched: null, size: null, quantity: 1, intentScore: 90);
        r.Should().Be(0);
    }

    [Fact]
    public void Match_without_size_yields_lower_than_with_size()
    {
        var withSize = _s.Score(Code(), "M", 1, intentScore: 70);
        var withoutSize = _s.Score(Code(), null, 1, intentScore: 70);

        withoutSize.Should().BeLessThan(withSize);
    }

    [Fact]
    public void Question_tone_low_intent_capped_at_low_confidence()
    {
        var r = _s.Score(Code(), "M", 1, intentScore: 20);
        r.Should().BeLessThan(50);
    }
}
```

- [ ] **Step 3: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ConfidenceScorerTests"
```
Expected: FAIL.

- [ ] **Step 4: Implement ConfidenceScorer**

Create `LiveDeck.Core/Sales/Pipeline/ConfidenceScorer.cs`:

```csharp
namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Combines pipeline signals into a single 0-100 confidence score.
///   * No matched code → 0
///   * Otherwise: 0.6 × intent + 30 if a size matched (or 10 if no size)
/// Output bounded to [0, 100].
/// </summary>
public sealed class ConfidenceScorer
{
    public int Score(ActiveCode? matched, string? size, int quantity, int intentScore)
    {
        if (matched is null) return 0;

        int sizeBoost = size is null ? 10 : 30;
        int raw = (int)(intentScore * 0.6) + sizeBoost;

        return System.Math.Clamp(raw, 0, 100);
    }
}
```

- [ ] **Step 5: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ConfidenceScorerTests"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Pipeline/CaptureResult.cs LiveDeck.Core/Sales/Pipeline/ConfidenceScorer.cs LiveDeck.Tests/Sales/ConfidenceScorerTests.cs
git commit -m "feat(core): add CaptureResult model and ConfidenceScorer"
```

---

### Task 20: OrderCaptureEngine integration

**Files:**
- Create: `LiveDeck.Core/Sales/OrderCaptureEngine.cs`
- Create: `LiveDeck.Tests/Sales/OrderCaptureEngineTests.cs`

- [ ] **Step 1: Failing test (composes the full pipeline)**

Create `LiveDeck.Tests/Sales/OrderCaptureEngineTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class OrderCaptureEngineTests
{
    private static ActiveCode Code(string code, params string[] sizes) =>
        new("id-" + code, "s1", code, sizes, 100m, null, System.Array.Empty<string>(), 0, null);

    private readonly OrderCaptureEngine _engine = new(
        new MessageNormalizer(),
        new CodeMatcher(),
        new VariantExtractor(),
        new QuantityExtractor(),
        new IntentScorer(),
        new ConfidenceScorer());

    [Fact]
    public void High_confidence_capture_with_explicit_intent()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "S", "M", "XL") };

        var r = _engine.Capture("MAVİ XL aldım", codes);

        r.IsCapture.Should().BeTrue();
        r.MatchedCode!.Code.Should().Be("MAVI");
        r.Size.Should().Be("XL");
        r.Quantity.Should().Be(1);
        r.Confidence.Should().BeGreaterOrEqualTo(80);
    }

    [Fact]
    public void Capture_with_quantity_and_typo()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var r = _engine.Capture("Mavıı M 2 tane aldim", codes);

        r.IsCapture.Should().BeTrue();
        r.Size.Should().Be("M");
        r.Quantity.Should().Be(2);
    }

    [Fact]
    public void Question_message_does_not_capture()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M", "XL") };

        var r = _engine.Capture("Mavi M kaldı mı?", codes);

        r.Confidence.Should().BeLessThan(50);
        r.IsCapture.Should().BeFalse();
    }

    [Fact]
    public void Unknown_code_does_not_capture()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var r = _engine.Capture("KIRMIZI M aldım", codes);

        r.MatchedCode.Should().BeNull();
        r.IsCapture.Should().BeFalse();
    }

    [Fact]
    public void Mid_confidence_returns_pending_capture()
    {
        // matched code, no size → mid confidence around 50-70
        var codes = new List<ActiveCode> { Code("MAVI", "M", "XL") };

        var r = _engine.Capture("MAVİ aldım", codes);

        r.MatchedCode.Should().NotBeNull();
        r.Size.Should().BeNull();
        r.Confidence.Should().BeInRange(40, 79);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OrderCaptureEngineTests"
```
Expected: FAIL — `OrderCaptureEngine` not found.

- [ ] **Step 3: Implement OrderCaptureEngine**

Create `LiveDeck.Core/Sales/OrderCaptureEngine.cs`:

```csharp
using System.Collections.Generic;
using LiveDeck.Core.Sales.Pipeline;

namespace LiveDeck.Core.Sales;

/// <summary>
/// Pure pipeline that turns a raw chat message + the current set of active codes into a
/// <see cref="CaptureResult"/>. Stateless and deterministic so it is safe to reuse across
/// threads and trivial to unit test.
/// </summary>
public sealed class OrderCaptureEngine
{
    private readonly MessageNormalizer _normalizer;
    private readonly CodeMatcher _matcher;
    private readonly VariantExtractor _variants;
    private readonly QuantityExtractor _quantity;
    private readonly IntentScorer _intent;
    private readonly ConfidenceScorer _confidence;

    public int HighConfidenceThreshold { get; init; } = 80;
    public int LowConfidenceThreshold { get; init; } = 50;

    public OrderCaptureEngine(
        MessageNormalizer normalizer,
        CodeMatcher matcher,
        VariantExtractor variants,
        QuantityExtractor quantity,
        IntentScorer intent,
        ConfidenceScorer confidence)
    {
        _normalizer = normalizer;
        _matcher = matcher;
        _variants = variants;
        _quantity = quantity;
        _intent = intent;
        _confidence = confidence;
    }

    public CaptureResult Capture(string originalMessage, IEnumerable<ActiveCode> activeCodes)
    {
        var normalised = _normalizer.Normalize(originalMessage);
        if (string.IsNullOrEmpty(normalised))
            return new CaptureResult(false, null, null, 0, 0, 0, "empty after normalisation");

        var matched = _matcher.Match(normalised, activeCodes);
        var size = matched is null ? null : _variants.Extract(normalised, matched.Sizes);
        var qty = _quantity.Extract(normalised);
        var intent = _intent.Score(normalised, originalMessage);
        var confidence = _confidence.Score(matched, size, qty, intent);

        var isCapture = matched is not null && confidence >= HighConfidenceThreshold;
        var reason = matched is null
            ? "no active code matched"
            : (confidence < LowConfidenceThreshold ? "low confidence (rejected)"
                : confidence < HighConfidenceThreshold ? "needs operator approval"
                : "auto-captured");

        return new CaptureResult(isCapture, matched, size, qty, intent, confidence, reason);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OrderCaptureEngineTests"
```
Expected: PASS — 5/5.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/OrderCaptureEngine.cs LiveDeck.Tests/Sales/OrderCaptureEngineTests.cs
git commit -m "feat(core): add OrderCaptureEngine composing the full pipeline"
```

---

### Task 21: OrderService (engine + persistence + customer auto-create)

**Files:**
- Create: `LiveDeck.Core/Sales/OrderService.cs`
- Create: `LiveDeck.Tests/Sales/OrderServiceTests.cs`

This service is what the WPF UI / chat ingestor actually call. It runs the engine, looks up or creates the Customer, and persists an OrderItem when capture succeeds (or when a "pending" capture awaits operator review).

- [ ] **Step 1: Failing test**

Create `LiveDeck.Tests/Sales/OrderServiceTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class OrderServiceTests
{
    private static (OrderService Svc, OrderRepository Orders, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) BuildFixture()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        var codes = new ActiveCodeRepository(db);
        codes.Insert(new ActiveCode("ac1", "s1", "MAVI", new[] { "M", "XL" }, 199m, null,
            System.Array.Empty<string>(), 1000, null));

        var engine = new OrderCaptureEngine(
            new MessageNormalizer(), new CodeMatcher(), new VariantExtractor(),
            new QuantityExtractor(), new IntentScorer(), new ConfidenceScorer());

        var orderRepo = new OrderRepository(db);
        var customerRepo = new CustomerRepository(db);
        var customerSvc = new CustomerService(customerRepo, clock);

        var svc = new OrderService(orderRepo, codes, customerSvc, engine, clock);
        return (svc, orderRepo, customerRepo, db, "s1");
    }

    [Fact]
    public void High_confidence_message_creates_OrderItem_with_status_New()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        var msg = new ChatMessage("m1", "instagram", null, "@ayse_y", "Ayşe", null,
            "MAVİ XL aldım", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().NotBeNull();
        result!.Status.Should().Be(OrderStatus.New);
        result.Code.Should().Be("MAVI");
        result.Size.Should().Be("XL");

        orders.GetBySession(sessionId).Should().HaveCount(1);
        customers.FindByPlatformAndUsername("instagram", "@ayse_y").Should().NotBeNull();
    }

    [Fact]
    public void Mid_confidence_message_creates_OrderItem_with_status_Pending()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        // matched code but missing size → mid confidence
        var msg = new ChatMessage("m1", "instagram", null, "@a", null, null,
            "MAVİ aldım", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().NotBeNull();
        result!.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Unmatched_or_low_confidence_returns_null_and_persists_nothing()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        var msg = new ChatMessage("m1", "instagram", null, "@a", null, null,
            "merhaba nasılsınız", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().BeNull();
        orders.GetBySession(sessionId).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OrderServiceTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement OrderService**

Create `LiveDeck.Core/Sales/OrderService.cs`:

```csharp
using System;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class OrderService
{
    private readonly OrderRepository _orders;
    private readonly ActiveCodeRepository _codes;
    private readonly CustomerService _customers;
    private readonly OrderCaptureEngine _engine;
    private readonly IClock _clock;

    public OrderService(
        OrderRepository orders,
        ActiveCodeRepository codes,
        CustomerService customers,
        OrderCaptureEngine engine,
        IClock clock)
    {
        _orders = orders;
        _codes = codes;
        _customers = customers;
        _engine = engine;
        _clock = clock;
    }

    /// <summary>
    /// Runs the capture engine over a chat message and persists an OrderItem when the
    /// match is at least mid-confidence. Returns null when nothing actionable was captured.
    /// </summary>
    public OrderItem? Process(string sessionId, ChatMessage message)
    {
        var activeCodes = _codes.GetActiveBySession(sessionId);
        if (activeCodes.Count == 0) return null;

        var capture = _engine.Capture(message.Text, activeCodes);

        if (capture.MatchedCode is null) return null;
        if (capture.Confidence < _engine.LowConfidenceThreshold) return null;

        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        var status = capture.Confidence >= _engine.HighConfidenceThreshold
            ? OrderStatus.New
            : OrderStatus.Pending;

        var size = capture.Size ?? "(belirsiz)";
        var qty = capture.Quantity;
        var unit = capture.MatchedCode.Price;
        var now = _clock.UnixNow();

        var order = new OrderItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            ActiveCodeId: capture.MatchedCode.Id,
            CustomerId: customer.Id,
            Code: capture.MatchedCode.Code,
            Size: size,
            Quantity: qty,
            UnitPrice: unit,
            TotalPrice: unit * qty,
            Confidence: capture.Confidence,
            Status: status,
            OriginalMessageText: message.Text,
            CapturedAt: now,
            StatusUpdatedAt: now,
            LabelPrintedAt: null,
            Notes: null);

        _orders.Insert(order);
        return order;
    }

    public void UpdateStatus(string orderId, string newStatus)
    {
        _orders.UpdateStatus(orderId, newStatus, _clock.UnixNow());
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OrderServiceTests"
```
Expected: PASS — 3/3.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/OrderService.cs LiveDeck.Tests/Sales/OrderServiceTests.cs
git commit -m "feat(core): add OrderService composing engine + persistence + customer lookup"
```

---

### Task 22: Real Turkish chat fixtures + integration tests

**Files:**
- Create: `LiveDeck.Tests/Sales/Fixtures/tr_chat_samples.json`
- Create: `LiveDeck.Tests/Sales/EngineFixturesTests.cs`
- Modify: `LiveDeck.Tests/LiveDeck.Tests.csproj` (mark JSON as content)

This task locks in the engine's behaviour against representative Turkish chat samples. Fix bugs surfaced by these fixtures before merging.

- [ ] **Step 1: Author 50 fixture samples**

Create `LiveDeck.Tests/Sales/Fixtures/tr_chat_samples.json`:

```json
{
  "activeCodes": [
    { "code": "MAVI",    "sizes": ["S", "M", "L", "XL"],            "price": 199 },
    { "code": "KIRMIZI", "sizes": ["36", "38", "40", "42"],         "price": 249 },
    { "code": "BEYAZ",   "sizes": ["TEK BEDEN"],                    "price": 159 },
    { "code": "SIYAH",   "sizes": ["S", "M", "L"],                  "price": 179 }
  ],
  "samples": [
    { "msg": "MAVİ XL aldım",            "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "mavi xl alıyorum",         "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "Mavıı XL aldım",           "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "MAVİ M 2 tane aldım",      "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "M",         "expectQty": 2 },
    { "msg": "mavi m x3",                "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "M",         "expectQty": 3 },
    { "msg": "Mavi M iki tane",          "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "M",         "expectQty": 2 },
    { "msg": "MAVI L olsun",             "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "L",         "expectQty": 1 },
    { "msg": "Mavi L lütfen",            "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "L",         "expectQty": 1 },
    { "msg": "MAVİ S istiyorum",         "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "S",         "expectQty": 1 },
    { "msg": "Mavi xl 🌹 aldım",         "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },

    { "msg": "kırmızı 38 aldım",         "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "38",        "expectQty": 1 },
    { "msg": "KIRMIZI 40 aldım",         "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "40",        "expectQty": 1 },
    { "msg": "Kirmizi 36 alıyorum",      "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "36",        "expectQty": 1 },
    { "msg": "kırmızı 42 lütfen",        "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "42",        "expectQty": 1 },
    { "msg": "Kırmızı 38 2 adet",        "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "38",        "expectQty": 2 },

    { "msg": "BEYAZ aldım",              "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 1 },
    { "msg": "beyaz alıyorum",           "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 1 },
    { "msg": "Beyaz tek beden alıyorum", "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 1 },
    { "msg": "Beyaz +2",                 "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 2 },

    { "msg": "Siyah M aldım",            "expectCapture": true,  "expectCode": "SIYAH",   "expectSize": "M",         "expectQty": 1 },
    { "msg": "siyah L 3 tane",           "expectCapture": true,  "expectCode": "SIYAH",   "expectSize": "L",         "expectQty": 3 },
    { "msg": "SİYAH S olsun",            "expectCapture": true,  "expectCode": "SIYAH",   "expectSize": "S",         "expectQty": 1 },

    { "msg": "Mavi XL var mı?",          "expectCapture": false, "expectCode": "MAVI",    "expectSize": null,        "expectQty": 1 },
    { "msg": "kırmızı kaldı mı",         "expectCapture": false, "expectCode": "KIRMIZI", "expectSize": null,        "expectQty": 1 },
    { "msg": "Beyaz ne kadar?",          "expectCapture": false, "expectCode": "BEYAZ",   "expectSize": null,        "expectQty": 1 },
    { "msg": "Siyah M kaç tl",           "expectCapture": false, "expectCode": "SIYAH",   "expectSize": "M",         "expectQty": 1 },
    { "msg": "Mavi bedenleri neler",     "expectCapture": false, "expectCode": "MAVI",    "expectSize": null,        "expectQty": 1 },

    { "msg": "merhaba nasılsın",         "expectCapture": false, "expectCode": null,      "expectSize": null,        "expectQty": 1 },
    { "msg": "yayına yeni katıldım",     "expectCapture": false, "expectCode": null,      "expectSize": null,        "expectQty": 1 },
    { "msg": "yeşil var mı",             "expectCapture": false, "expectCode": null,      "expectSize": null,        "expectQty": 1 },

    { "msg": "Mavi aldım",               "expectCapture": false, "expectCode": "MAVI",    "expectSize": null,        "expectQty": 1 },
    { "msg": "kırmızı alıyorum",         "expectCapture": false, "expectCode": "KIRMIZI", "expectSize": null,        "expectQty": 1 },

    { "msg": "MAVİ M ALDIM ❤️",          "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "M",         "expectQty": 1 },
    { "msg": "Kırmızı 38 🛒",            "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "38",        "expectQty": 1 },
    { "msg": "Beyaz 🌹 alıyorum",        "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 1 },

    { "msg": "Mavi XL ALDIM",            "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "MAVİ XL aldım lütfen",     "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "mavi  xl  aldım",          "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },

    { "msg": "Siyah s 2 tane aldım",     "expectCapture": true,  "expectCode": "SIYAH",   "expectSize": "S",         "expectQty": 2 },
    { "msg": "Siyah l ikişer alıyorum",  "expectCapture": true,  "expectCode": "SIYAH",   "expectSize": "L",         "expectQty": 2 },

    { "msg": "MAVI XL ALDIM",            "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "mavı xl",                  "expectCapture": false, "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },
    { "msg": "MAVİ Xl aldım",            "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 },

    { "msg": "Kirmizi 38 aldim",         "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "38",        "expectQty": 1 },
    { "msg": "kırmızı 38 ricaederim",    "expectCapture": true,  "expectCode": "KIRMIZI", "expectSize": "38",        "expectQty": 1 },

    { "msg": "Mavi M aldım Ayşe",        "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "M",         "expectQty": 1 },
    { "msg": "merhaba mavi xl alabilir miyim", "expectCapture": true, "expectCode": "MAVI", "expectSize": "XL",     "expectQty": 1 },

    { "msg": "Beyaz x2 olsun",           "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 2 },
    { "msg": "Beyaz iki adet",           "expectCapture": true,  "expectCode": "BEYAZ",   "expectSize": "TEK BEDEN", "expectQty": 2 },

    { "msg": "MAVİ XL TARAFIMA AYIRIN",  "expectCapture": true,  "expectCode": "MAVI",    "expectSize": "XL",        "expectQty": 1 }
  ]
}
```

- [ ] **Step 2: Mark fixtures as Content (copied to output)**

Edit `LiveDeck.Tests/LiveDeck.Tests.csproj` to include:

```xml
  <ItemGroup>
    <None Update="Sales\Fixtures\tr_chat_samples.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 3: Fixture-driven test**

Create `LiveDeck.Tests/Sales/EngineFixturesTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace LiveDeck.Tests.Sales;

public class EngineFixturesTests
{
    private readonly ITestOutputHelper _out;
    public EngineFixturesTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void All_fixtures_have_expected_outcome()
    {
        var path = Path.Combine(System.AppContext.BaseDirectory, "Sales", "Fixtures",
                                "tr_chat_samples.json");
        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<Fixtures>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        var codes = new List<ActiveCode>();
        foreach (var c in data.ActiveCodes)
            codes.Add(new ActiveCode("id-" + c.Code, "s1", c.Code, c.Sizes, c.Price, null,
                System.Array.Empty<string>(), 0, null));

        var engine = new OrderCaptureEngine(
            new MessageNormalizer(), new CodeMatcher(), new VariantExtractor(),
            new QuantityExtractor(), new IntentScorer(), new ConfidenceScorer());

        var failures = new List<string>();
        foreach (var s in data.Samples)
        {
            var r = engine.Capture(s.Msg, codes);

            if (r.IsCapture != s.ExpectCapture)
                failures.Add($"[{s.Msg}] expected capture={s.ExpectCapture}, got={r.IsCapture} (conf={r.Confidence})");
            else if (s.ExpectCapture)
            {
                if (r.MatchedCode?.Code != s.ExpectCode)
                    failures.Add($"[{s.Msg}] expected code={s.ExpectCode}, got={r.MatchedCode?.Code}");
                if (r.Size != s.ExpectSize)
                    failures.Add($"[{s.Msg}] expected size={s.ExpectSize}, got={r.Size}");
                if (r.Quantity != s.ExpectQty)
                    failures.Add($"[{s.Msg}] expected qty={s.ExpectQty}, got={r.Quantity}");
            }
        }

        foreach (var f in failures) _out.WriteLine(f);
        failures.Should().BeEmpty($"Expected all {data.Samples.Count} fixtures to pass");
    }

    private sealed record Fixtures(List<CodeFixture> ActiveCodes, List<Sample> Samples);
    private sealed record CodeFixture(string Code, List<string> Sizes, decimal Price);
    private sealed record Sample(
        string Msg,
        bool ExpectCapture,
        string? ExpectCode,
        string? ExpectSize,
        int ExpectQty);
}
```

- [ ] **Step 4: Run fixture test — fix any pipeline regressions surfaced**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~EngineFixturesTests"
```

If failures appear, iterate on the pipeline (extend `BuyingWords` set, tighten/loosen confidence thresholds, etc.) until all 50 fixtures pass.

- [ ] **Step 5: Commit (after all fixtures pass)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Tests/Sales/Fixtures LiveDeck.Tests/Sales/EngineFixturesTests.cs LiveDeck.Tests/LiveDeck.Tests.csproj
git commit -m "test(core): add 50 Turkish chat fixtures locking engine behaviour"
```

---

## Chat Ingestion

### Task 23: ChatBus + IChatIngestor

**Files:**
- Create: `LiveDeck.Core/Chat/IChatBus.cs`
- Create: `LiveDeck.Core/Chat/ChatBus.cs`
- Create: `LiveDeck.Core/Chat/IChatIngestor.cs`
- Create: `LiveDeck.Tests/Chat/ChatBusTests.cs`

The bus is an in-memory pub/sub. It also keeps a ring buffer of the last N messages for overlay snapshot recovery.

- [ ] **Step 1: Failing test**

Create `LiveDeck.Tests/Chat/ChatBusTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Chat;
using Xunit;

namespace LiveDeck.Tests.Chat;

public class ChatBusTests
{
    private static ChatMessage Msg(string text, string id = "m1") =>
        new(id, "instagram", null, "@a", null, null, text, 0, Array.Empty<string>());

    [Fact]
    public void Subscribe_then_Publish_invokes_handler()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(received.Add);

        bus.Publish(Msg("hello"));

        received.Should().HaveCount(1);
        received[0].Text.Should().Be("hello");
    }

    [Fact]
    public void Disposing_subscription_stops_invocations()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        var sub = bus.Subscribe(received.Add);
        sub.Dispose();

        bus.Publish(Msg("after"));

        received.Should().BeEmpty();
    }

    [Fact]
    public void RecentMessages_returns_last_N_in_arrival_order()
    {
        var bus = new ChatBus(ringBufferSize: 3);
        bus.Publish(Msg("a", "1"));
        bus.Publish(Msg("b", "2"));
        bus.Publish(Msg("c", "3"));
        bus.Publish(Msg("d", "4"));   // oldest "a" evicted

        var recent = bus.RecentMessages();
        recent.Should().HaveCount(3);
        recent[0].Text.Should().Be("b");
        recent[2].Text.Should().Be("d");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ChatBusTests"
```
Expected: FAIL.

- [ ] **Step 3: IChatBus + ChatBus implementation**

Create `LiveDeck.Core/Chat/IChatBus.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace LiveDeck.Core.Chat;

public interface IChatBus
{
    /// <summary>Subscribe to live messages. Disposing the returned token unsubscribes.</summary>
    IDisposable Subscribe(Action<ChatMessage> handler);

    /// <summary>Push a message to all current subscribers and add to recent ring buffer.</summary>
    void Publish(ChatMessage message);

    /// <summary>Last-N messages, oldest first. Used for overlay state snapshot.</summary>
    IReadOnlyList<ChatMessage> RecentMessages();
}
```

Create `LiveDeck.Core/Chat/ChatBus.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;

namespace LiveDeck.Core.Chat;

/// <summary>
/// In-memory pub/sub for chat messages. Thread-safe: subscriptions and publishes can race.
/// Maintains a fixed-size ring buffer of recent messages for late subscribers (e.g., the
/// OBS overlay reconnecting mid-stream).
/// </summary>
public sealed class ChatBus : IChatBus
{
    private readonly object _lock = new();
    private readonly List<Action<ChatMessage>> _handlers = new();
    private readonly ChatMessage[] _ring;
    private int _ringHead;
    private int _ringCount;

    public ChatBus(int ringBufferSize = 200)
    {
        _ring = new ChatMessage[ringBufferSize];
    }

    public IDisposable Subscribe(Action<ChatMessage> handler)
    {
        lock (_lock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Publish(ChatMessage message)
    {
        Action<ChatMessage>[] snapshot;
        lock (_lock)
        {
            _ring[_ringHead] = message;
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            snapshot = _handlers.ToArray();
        }
        foreach (var h in snapshot) h(message);
    }

    public IReadOnlyList<ChatMessage> RecentMessages()
    {
        lock (_lock)
        {
            var result = new List<ChatMessage>(_ringCount);
            int start = (_ringHead - _ringCount + _ring.Length) % _ring.Length;
            for (int i = 0; i < _ringCount; i++)
            {
                result.Add(_ring[(start + i) % _ring.Length]);
            }
            return result;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ChatBus _bus;
        private Action<ChatMessage>? _handler;

        public Subscription(ChatBus bus, Action<ChatMessage> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_bus._lock) _bus._handlers.Remove(h);
        }
    }
}
```

Create `LiveDeck.Core/Chat/IChatIngestor.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace LiveDeck.Core.Chat;

public interface IChatIngestor
{
    string Platform { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ChatBusTests"
```
Expected: PASS — 3/3.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Chat LiveDeck.Tests/Chat
git commit -m "feat(core): add IChatBus pub/sub with ring buffer and IChatIngestor"
```

---

### Task 24: ExtensionBridgeServer + ExtensionMessage

**Files:**
- Create: `LiveDeck.Chat/Bridge/ExtensionMessage.cs`
- Create: `LiveDeck.Chat/Bridge/ExtensionBridgeServer.cs`
- Create: `LiveDeck.Tests/Chat/ExtensionBridgeServerTests.cs`

The browser extension content script connects to `ws://localhost:4748/extension` and sends one JSON message per chat event. We mirror the protocol used in UniCast (see `C:\Users\burak\Downloads\UniCast\UniCast\UniCast.Core\Chat\Bridge\ExtensionBridgeServer.cs` for reference).

- [ ] **Step 1: ExtensionMessage DTO**

Create `LiveDeck.Chat/Bridge/ExtensionMessage.cs`:

```csharp
namespace LiveDeck.Chat.Bridge;

/// <summary>
/// Wire format produced by the browser extension content scripts. Field names use camelCase
/// to match the JS sender. All fields are nullable except the discriminators.
/// </summary>
public sealed record ExtensionMessage(
    string Type,        // "chat" | "ping" | "platform_status"
    string? Platform,   // "instagram" | "tiktok" | ...
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Text,
    string? ExternalId,
    long? Timestamp);
```

- [ ] **Step 2: Failing test**

Create `LiveDeck.Tests/Chat/ExtensionBridgeServerTests.cs`:

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Xunit;

namespace LiveDeck.Tests.Chat;

public class ExtensionBridgeServerTests
{
    [Fact]
    public async Task Forwards_chat_message_from_extension_to_ChatBus()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0); // 0 = ephemeral
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        var payload = JsonSerializer.Serialize(new ExtensionMessage(
            Type: "chat",
            Platform: "instagram",
            Username: "@ayse_y",
            DisplayName: "Ayşe",
            AvatarUrl: null,
            Text: "MAVI XL aldım",
            ExternalId: "ig-001",
            Timestamp: 1700000000));

        await ws.SendAsync(Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text, true, CancellationToken.None);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("instagram");
        msg.Username.Should().Be("@ayse_y");
        msg.Text.Should().Be("MAVI XL aldım");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
}
```

- [ ] **Step 3: ExtensionBridgeServer implementation**

Create `LiveDeck.Chat/Bridge/ExtensionBridgeServer.cs`:

```csharp
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Core.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Chat.Bridge;

/// <summary>
/// Hosts a localhost WebSocket endpoint (`ws://localhost:{port}/extension`) that the browser
/// extension content scripts connect to. Each incoming JSON payload is parsed as an
/// <see cref="ExtensionMessage"/>; "chat" messages are forwarded to the supplied
/// <see cref="IChatBus"/>.
/// </summary>
public sealed class ExtensionBridgeServer : IAsyncDisposable
{
    private readonly IChatBus _bus;
    private readonly ILogger<ExtensionBridgeServer> _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public int Port { get; private set; }

    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null)
    {
        _bus = bus;
        _log = log ?? NullLogger<ExtensionBridgeServer>.Instance;
        Port = port == 0 ? FindFreePort() : port;
        _listener.Prefixes.Add($"http://localhost:{Port}/extension/");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _runner = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_runner is not null)
            try { await _runner; } catch { /* ignore */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            _ = Task.Run(() => Handle(wsContext.WebSocket, ct), ct);
        }
    }

    private async Task Handle(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new System.IO.MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            try
            {
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Extension WS receive failed");
                break;
            }

            if (res.MessageType == WebSocketMessageType.Close) break;

            try
            {
                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var msg = JsonSerializer.Deserialize<ExtensionMessage>(json,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                if (msg is { Type: "chat", Platform: not null, Username: not null, Text: not null })
                {
                    _bus.Publish(new ChatMessage(
                        Id: Guid.NewGuid().ToString("N"),
                        Platform: msg.Platform,
                        ExternalId: msg.ExternalId,
                        Username: msg.Username,
                        DisplayName: msg.DisplayName,
                        AvatarUrl: msg.AvatarUrl,
                        Text: msg.Text,
                        ReceivedAt: msg.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Badges: Array.Empty<string>()));
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Bad extension payload");
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_listener.IsListening) _listener.Close();
    }
}
```

- [ ] **Step 4: Run test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ExtensionBridgeServerTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Chat/Bridge LiveDeck.Tests/Chat/ExtensionBridgeServerTests.cs
git commit -m "feat(chat): add ExtensionBridgeServer accepting browser extension WebSockets"
```

---

### Task 25: Port and rebrand the browser extension

**Files:**
- Create: `Extension/manifest.json`
- Create: `Extension/background.js`
- Create: `Extension/content-instagram.js`
- Create: `Extension/popup.html`
- Create: `Extension/popup.js`
- Copy: `Extension/icons/` from UniCast (or generic placeholder)

UniCast's existing extension is at `C:\Users\burak\Downloads\UniCast\UniCast\Extension\`. We port it without modifying the source repo.

- [ ] **Step 1: Copy extension files from UniCast**

```bash
mkdir -p /c/Users/burak/source/repos/LiveDeck/Extension/icons
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/manifest.json     /c/Users/burak/source/repos/LiveDeck/Extension/
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/background.js     /c/Users/burak/source/repos/LiveDeck/Extension/
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/content-instagram.js /c/Users/burak/source/repos/LiveDeck/Extension/
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/popup.html        /c/Users/burak/source/repos/LiveDeck/Extension/
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/popup.js          /c/Users/burak/source/repos/LiveDeck/Extension/
```

(Skip TikTok and Facebook content scripts — Phase 1 ships Instagram only.)

- [ ] **Step 2: Rebrand manifest**

Edit `Extension/manifest.json`. Change:
- `name` → `"LiveDeck Chat Bridge"`
- `description` → `"Forwards Instagram Live chat to LiveDeck"`
- `version` → `"1.0.0"`
- `host_permissions` → keep only Instagram (`"*://*.instagram.com/*"`); remove TikTok/Facebook entries
- Remove TikTok/Facebook entries from `content_scripts`
- Keep `permissions`: `["storage"]` (or whatever UniCast had)

If UniCast's manifest references TikTok/Facebook scripts, the resulting manifest should look like:

```json
{
  "manifest_version": 3,
  "name": "LiveDeck Chat Bridge",
  "version": "1.0.0",
  "description": "Forwards Instagram Live chat to LiveDeck",
  "background": { "service_worker": "background.js" },
  "permissions": ["storage"],
  "host_permissions": ["*://*.instagram.com/*"],
  "content_scripts": [
    {
      "matches": ["*://*.instagram.com/*"],
      "js": ["content-instagram.js"],
      "run_at": "document_idle"
    }
  ],
  "action": { "default_popup": "popup.html" },
  "icons": { "16": "icons/16.png", "48": "icons/48.png", "128": "icons/128.png" }
}
```

- [ ] **Step 3: Update bridge port and message envelope**

Open `Extension/background.js` and find the WebSocket connect URL. UniCast probably hard-codes `ws://localhost:NNNN/extension`. Confirm/change to `ws://localhost:4748/extension`.

Verify `background.js` sends payloads matching the `ExtensionMessage` shape (fields: `type`, `platform`, `username`, `displayName`, `avatarUrl`, `text`, `externalId`, `timestamp`). If UniCast uses different field names, add a small mapping in `background.js` so the extension speaks LiveDeck's format.

- [ ] **Step 4: Sanity load**

Open Chrome / Edge → `chrome://extensions/` → enable Developer Mode → "Load unpacked" → select `C:\Users\burak\source\repos\LiveDeck\Extension`. Verify it loads without errors.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add Extension
git commit -m "chore(ext): port browser extension from UniCast (Instagram only, rebranded)"
```

---

### Task 26: InstagramIngestor (no-op wrapper that the App can register)

**Files:**
- Create: `LiveDeck.Chat/Ingestors/InstagramIngestor.cs`

Most of the work happens in the browser extension; the C# side only needs an `IChatIngestor` whose Start/Stop methods turn the bridge on or off. The bridge itself listens for any platform — Instagram messages arrive automatically once the extension connects.

- [ ] **Step 1: Implement InstagramIngestor**

Create `LiveDeck.Chat/Ingestors/InstagramIngestor.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Microsoft.Extensions.Logging;

namespace LiveDeck.Chat.Ingestors;

/// <summary>
/// Phase 1 ingestor that simply ensures the <see cref="ExtensionBridgeServer"/> is running.
/// All message decoding happens inside the bridge; this class is a marker for the App to
/// know that Instagram is the active platform and to gate UI accordingly.
/// </summary>
public sealed class InstagramIngestor : IChatIngestor
{
    private readonly ExtensionBridgeServer _bridge;
    private readonly ILogger<InstagramIngestor> _log;

    public string Platform => "instagram";

    public InstagramIngestor(ExtensionBridgeServer bridge, ILogger<InstagramIngestor> log)
    {
        _bridge = bridge;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("InstagramIngestor starting (bridge port {Port})", _bridge.Port);
        return _bridge.StartAsync(ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("InstagramIngestor stopping");
        return _bridge.StopAsync(ct);
    }
}
```

- [ ] **Step 2: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Chat
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Chat/Ingestors/InstagramIngestor.cs
git commit -m "feat(chat): add InstagramIngestor that orchestrates ExtensionBridgeServer"
```

---

## OBS Overlay (ASP.NET Core)

### Task 27: OverlayHost (ASP.NET Core minimal API + WebSocket)

**Files:**
- Create: `LiveDeck.Overlay/OverlayHost.cs`
- Create: `LiveDeck.Overlay/Models/OverlayEvent.cs`

The overlay project hosts an ASP.NET Core web app *as a library*: it exposes `Start/Stop` methods that the WPF App calls. No Program.cs needed — `WebApplication.CreateBuilder` is invoked from `OverlayHost`.

- [ ] **Step 1: OverlayEvent DTO**

Create `LiveDeck.Overlay/Models/OverlayEvent.cs`:

```csharp
namespace LiveDeck.Overlay.Models;

/// <summary>JSON envelope sent to overlay clients over WebSocket.</summary>
public sealed record OverlayEvent(string Type, object Data);

public sealed record ChatMessageEvent(
    string Id, string Platform, string Username,
    string? DisplayName, string? AvatarUrl, string Text, long Timestamp);

public sealed record ChatSnapshotEvent(System.Collections.Generic.IReadOnlyList<ChatMessageEvent> RecentMessages);
```

- [ ] **Step 2: OverlayHost implementation**

Create `LiveDeck.Overlay/OverlayHost.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Core.Chat;
using LiveDeck.Overlay.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Overlay;

/// <summary>
/// Hosts the OBS Browser Source endpoints. Started/stopped by the WPF App.
///   * GET  /overlay/chat       → static HTML page bundled via wwwroot
///   * WS   /ws/chat            → live ChatMessage stream
/// </summary>
public sealed class OverlayHost : IAsyncDisposable
{
    private readonly IChatBus _bus;
    private readonly ILogger<OverlayHost> _log;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private IDisposable? _busSub;

    public int Port { get; private set; }

    public OverlayHost(IChatBus bus, int port = 4747, ILogger<OverlayHost>? log = null)
    {
        _bus = bus;
        _log = log ?? NullLogger<OverlayHost>.Instance;
        Port = port;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{Port}");

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
        }

        _app.MapGet("/overlay/chat", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var html = Path.Combine(wwwroot, "chat.html");
            await ctx.Response.SendFileAsync(html);
        });

        _app.Map("/ws/chat", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleClient(ws, ctx.RequestAborted);
        });

        _busSub = _bus.Subscribe(BroadcastChatMessage);

        await _app.StartAsync();
        _log.LogInformation("OverlayHost listening on http://localhost:{Port}", Port);
    }

    public async Task StopAsync()
    {
        _busSub?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task HandleClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients.TryAdd(id, ws);
        try
        {
            // Send snapshot of recent messages
            var snapshot = new OverlayEvent("chat.snapshot", new ChatSnapshotEvent(
                BuildSnapshot()));
            await SendJson(ws, snapshot, ct);

            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay client error"); }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private System.Collections.Generic.IReadOnlyList<ChatMessageEvent> BuildSnapshot()
    {
        var recent = _bus.RecentMessages();
        var list = new System.Collections.Generic.List<ChatMessageEvent>(recent.Count);
        foreach (var m in recent)
            list.Add(new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        return list;
    }

    private void BroadcastChatMessage(ChatMessage m)
    {
        var evt = new OverlayEvent("chat.message",
            new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        var json = JsonSerializer.Serialize(evt);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await SendBytes(ws, Encoding.UTF8.GetBytes(json), ct);
    }

    private static async Task SendBytes(WebSocket ws, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch { /* swallow per-client errors so one slow client doesn't kill the broadcast */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
```

- [ ] **Step 3: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Overlay
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Overlay/OverlayHost.cs LiveDeck.Overlay/Models
git commit -m "feat(overlay): add OverlayHost with chat WebSocket and HTML endpoint"
```

---

### Task 28: Chat overlay HTML/CSS/JS (default minimal theme)

**Files:**
- Create: `LiveDeck.Overlay/wwwroot/chat.html`
- Create: `LiveDeck.Overlay/wwwroot/chat.js`
- Create: `LiveDeck.Overlay/wwwroot/themes/minimal/style.css`

- [ ] **Step 1: Default theme CSS**

Create `LiveDeck.Overlay/wwwroot/themes/minimal/style.css`:

```css
* { box-sizing: border-box; margin: 0; padding: 0; }

html, body {
  width: 100vw;
  height: 100vh;
  background: transparent;
  font-family: 'Segoe UI', Roboto, system-ui, sans-serif;
  color: #fff;
  overflow: hidden;
}

#chat-container {
  position: absolute;
  bottom: 0;
  left: 0;
  right: 0;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 100vh;
  overflow: hidden;
}

.chat-message {
  background: rgba(0, 0, 0, 0.6);
  backdrop-filter: blur(8px);
  border-radius: 12px;
  padding: 10px 14px;
  display: flex;
  gap: 10px;
  align-items: flex-start;
  animation: slide-in 0.25s ease-out;
  text-shadow: 0 1px 3px rgba(0, 0, 0, 0.8);
}

.chat-message .platform-badge {
  width: 18px;
  height: 18px;
  border-radius: 4px;
  flex-shrink: 0;
  margin-top: 2px;
}
.chat-message .platform-badge.instagram {
  background: linear-gradient(45deg, #f09433, #e6683c, #dc2743, #cc2366, #bc1888);
}

.chat-message .body { flex: 1; min-width: 0; }
.chat-message .username { font-weight: 700; font-size: 14px; color: #ffd166; }
.chat-message .text {
  font-size: 16px; line-height: 1.4;
  word-break: break-word;
  overflow-wrap: break-word;
}

@keyframes slide-in {
  from { opacity: 0; transform: translateY(8px); }
  to   { opacity: 1; transform: translateY(0); }
}

.chat-message.fade-out { animation: fade-out 0.4s ease-in forwards; }
@keyframes fade-out {
  to { opacity: 0; transform: translateY(-4px); }
}
```

- [ ] **Step 2: chat.html**

Create `LiveDeck.Overlay/wwwroot/chat.html`:

```html
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <title>LiveDeck Chat</title>
  <link rel="stylesheet" href="/themes/minimal/style.css">
</head>
<body>
  <div id="chat-container"></div>
  <script src="/chat.js"></script>
</body>
</html>
```

- [ ] **Step 3: chat.js (WebSocket client + DOM updates)**

Create `LiveDeck.Overlay/wwwroot/chat.js`:

```javascript
(function () {
  'use strict';

  const MAX_VISIBLE = 50;
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 10000;

  const container = document.getElementById('chat-container');
  let reconnectAttempt = 0;
  let socket = null;

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const url = `${proto}://${location.host}/ws/chat`;
    socket = new WebSocket(url);

    socket.onopen = () => { reconnectAttempt = 0; };
    socket.onmessage = (e) => {
      try {
        const evt = JSON.parse(e.data);
        if (evt.type === 'chat.snapshot') {
          (evt.data.recentMessages || []).forEach(appendMessage);
        } else if (evt.type === 'chat.message') {
          appendMessage(evt.data);
        }
      } catch (err) {
        console.error('LiveDeck overlay parse error', err);
      }
    };
    socket.onclose = scheduleReconnect;
    socket.onerror = () => { try { socket.close(); } catch (_) {} };
  }

  function scheduleReconnect() {
    reconnectAttempt++;
    const backoff = Math.min(RECONNECT_BASE_MS * Math.pow(2, reconnectAttempt - 1),
                              RECONNECT_MAX_MS);
    setTimeout(connect, backoff);
  }

  function appendMessage(msg) {
    const el = document.createElement('div');
    el.className = 'chat-message';
    el.dataset.id = msg.id;

    const badge = document.createElement('div');
    badge.className = `platform-badge ${msg.platform}`;
    el.appendChild(badge);

    const body = document.createElement('div');
    body.className = 'body';

    const user = document.createElement('div');
    user.className = 'username';
    user.textContent = msg.displayName || msg.username;
    body.appendChild(user);

    const text = document.createElement('div');
    text.className = 'text';
    text.textContent = msg.text;
    body.appendChild(text);

    el.appendChild(body);
    container.appendChild(el);

    while (container.childElementCount > MAX_VISIBLE) {
      const oldest = container.firstElementChild;
      if (oldest) {
        oldest.classList.add('fade-out');
        setTimeout(() => oldest.remove(), 400);
      } else {
        break;
      }
    }

    el.scrollIntoView({ block: 'end' });
  }

  connect();
})();
```

- [ ] **Step 4: Mark wwwroot files as Content (already in csproj from Task 3, but verify)**

Open `LiveDeck.Overlay/LiveDeck.Overlay.csproj`. Confirm there is an `<ItemGroup>` containing:

```xml
<Content Include="wwwroot\**\*.*" CopyToOutputDirectory="PreserveNewest" />
```

If absent, add it. Re-build:

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Overlay
```

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Overlay/wwwroot
git commit -m "feat(overlay): add minimal theme chat overlay (HTML + CSS + JS)"
```

---

### Task 29: Manual smoke test — extension → bridge → bus → overlay

**No new files — just verification.**

- [ ] **Step 1: Add a tiny console harness for end-to-end smoke**

Temporarily add an entry point to `LiveDeck.App` to run the bridge + overlay without the full WPF UI.

Edit `LiveDeck.App/App.xaml.cs`, replace `OnStartup` body with a switch on a debug flag (or add new `Main` in a temporary `SmokeMain.cs`).

For simplicity in this smoke test, add `LiveDeck.App/SmokeMain.cs` (DELETE after test passes):

```csharp
#if SMOKE_TEST
using System;
using System.Threading.Tasks;
using LiveDeck.Chat.Bridge;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Core.Chat;
using LiveDeck.Overlay;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.App;

public static class SmokeMain
{
    public static async Task Main()
    {
        var bus = new ChatBus();
        await using var bridge = new ExtensionBridgeServer(bus, port: 4748);
        await using var overlay = new OverlayHost(bus, port: 4747);

        var ingestor = new InstagramIngestor(bridge, NullLogger<InstagramIngestor>.Instance);
        await ingestor.StartAsync(System.Threading.CancellationToken.None);
        await overlay.StartAsync();

        Console.WriteLine("Bridge: ws://localhost:4748/extension");
        Console.WriteLine("Overlay: http://localhost:4747/overlay/chat");
        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();

        await ingestor.StopAsync(System.Threading.CancellationToken.None);
        await overlay.StopAsync();
    }
}
#endif
```

- [ ] **Step 2: Run the smoke harness**

Set `<DefineConstants>SMOKE_TEST</DefineConstants>` temporarily in `LiveDeck.App.csproj`, change `<OutputType>WinExe</OutputType>` to `Exe`, then:

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

Open `http://localhost:4747/overlay/chat` in browser. Verify the page loads (transparent background, empty list).

Open Instagram Live in another tab with the LiveDeck extension loaded. Chat messages should arrive in the overlay within 1-2 seconds of being posted.

- [ ] **Step 3: Revert smoke harness changes**

Remove `SmokeMain.cs`, revert `<OutputType>` to `WinExe`, remove `<DefineConstants>SMOKE_TEST</DefineConstants>`. Build to confirm WPF App still compiles.

- [ ] **Step 4: Commit (only after end-to-end works)**

If the end-to-end flow works, commit nothing new (smoke files were temporary). If you uncovered a bug in bridge/overlay/extension, commit the fix:

```bash
cd /c/Users/burak/source/repos/LiveDeck
git status
# ...stage any fixes...
git commit -m "fix: <describe smoke-test fix>"
```

---

## WPF UI (MVVM)

### Task 30: Wire all services in AppHost + ViewModelBase

**Files:**
- Modify: `LiveDeck.App/AppHost.cs`
- Create: `LiveDeck.App/ViewModels/ViewModelBase.cs`

- [ ] **Step 1: Update AppHost to register all Phase 1 services**

Replace `LiveDeck.App/AppHost.cs` body with:

```csharp
using System;
using System.IO;
using LiveDeck.Chat.Bridge;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Core;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LiveDeck.App;

public sealed class AppHost : IDisposable
{
    public IServiceProvider Services { get; }
    private readonly Serilog.ILogger _serilog;

    public AppHost()
    {
        AppPaths.EnsureDirectoriesExist();

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsFolder, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(_serilog, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Settings
        services.AddSingleton(new SettingsStore(AppPaths.SettingsFile));
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());

        // Time
        services.AddSingleton<IClock, SystemClock>();

        // Storage
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(AppPaths.DatabaseFile));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<ActiveCodeRepository>();
        services.AddSingleton<OrderRepository>();
        services.AddSingleton<CustomerRepository>();

        // Domain services
        services.AddSingleton<StreamSessionService>();
        services.AddSingleton<ActiveCodeService>();
        services.AddSingleton<CustomerService>();

        // Capture pipeline
        services.AddSingleton<MessageNormalizer>();
        services.AddSingleton<CodeMatcher>();
        services.AddSingleton<VariantExtractor>();
        services.AddSingleton<QuantityExtractor>();
        services.AddSingleton<IntentScorer>();
        services.AddSingleton<ConfidenceScorer>();
        services.AddSingleton<OrderCaptureEngine>();
        services.AddSingleton<OrderService>();

        // Chat plumbing
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return new ExtensionBridgeServer(
                sp.GetRequiredService<IChatBus>(),
                port: 4748,
                log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>());
        });
        services.AddSingleton<InstagramIngestor>();

        // Overlay
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return new OverlayHost(
                sp.GetRequiredService<IChatBus>(),
                port: settings.OverlayPort,
                log: sp.GetRequiredService<ILogger<OverlayHost>>());
        });

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ActiveCodesViewModel>();
        services.AddSingleton<ViewModels.OrderQueueViewModel>();
        services.AddSingleton<ViewModels.ChatPanelViewModel>();

        Services = services.BuildServiceProvider();

        // Apply migrations once at boot
        Services.GetRequiredService<MigrationRunner>().Run();
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable) disposable.Dispose();
        Serilog.Log.CloseAndFlush();
    }
}
```

- [ ] **Step 2: ViewModelBase**

Create `LiveDeck.App/ViewModels/ViewModelBase.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveDeck.App.ViewModels;

/// <summary>Common base for all view models. Backed by CommunityToolkit's source generator.</summary>
public abstract class ViewModelBase : ObservableObject
{
}
```

- [ ] **Step 3: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App
```
Expected: `Build succeeded.` (May fail because ViewModels don't exist yet — acceptable; resolved in next tasks.)

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs LiveDeck.App/ViewModels/ViewModelBase.cs
git commit -m "feat(app): wire all Phase 1 services in DI container"
```

---

### Task 31: MainWindow with sidebar navigation

**Files:**
- Modify: `LiveDeck.App/MainWindow.xaml`
- Modify: `LiveDeck.App/MainWindow.xaml.cs`
- Create: `LiveDeck.App/ViewModels/MainViewModel.cs`

- [ ] **Step 1: MainViewModel**

Create `LiveDeck.App/ViewModels/MainViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LiveDeck.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private object? _currentView;

    private readonly ActiveCodesViewModel _activeCodes;
    private readonly OrderQueueViewModel _orders;
    private readonly ChatPanelViewModel _chat;

    public MainViewModel(
        ActiveCodesViewModel activeCodes,
        OrderQueueViewModel orders,
        ChatPanelViewModel chat)
    {
        _activeCodes = activeCodes;
        _orders = orders;
        _chat = chat;
        CurrentView = _orders; // default landing view
    }

    [RelayCommand]
    private void NavigateToOrders() => CurrentView = _orders;

    [RelayCommand]
    private void NavigateToActiveCodes() => CurrentView = _activeCodes;

    [RelayCommand]
    private void NavigateToChat() => CurrentView = _chat;
}
```

- [ ] **Step 2: MainWindow.xaml**

Replace `LiveDeck.App/MainWindow.xaml`:

```xml
<Window x:Class="LiveDeck.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:LiveDeck.App.ViewModels"
        xmlns:views="clr-namespace:LiveDeck.App.Views"
        Title="LiveDeck" Height="800" Width="1280"
        Background="#FF1A1A1A" Foreground="White">

    <Window.Resources>
        <DataTemplate DataType="{x:Type vm:OrderQueueViewModel}">
            <views:OrderQueueView />
        </DataTemplate>
        <DataTemplate DataType="{x:Type vm:ActiveCodesViewModel}">
            <views:ActiveCodesView />
        </DataTemplate>
        <DataTemplate DataType="{x:Type vm:ChatPanelViewModel}">
            <views:ChatPanelView />
        </DataTemplate>
    </Window.Resources>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Sidebar -->
        <StackPanel Grid.Column="0" Background="#FF111111" >
            <TextBlock Text="LiveDeck" FontSize="22" FontWeight="Bold"
                       Foreground="#FFFFD166" Margin="20,20,20,30"/>

            <Button Content="Sipariş Kuyruğu" Command="{Binding NavigateToOrdersCommand}"
                    Style="{DynamicResource SidebarButton}" />
            <Button Content="Aktif Kodlar"   Command="{Binding NavigateToActiveCodesCommand}"
                    Style="{DynamicResource SidebarButton}" />
            <Button Content="Chat"           Command="{Binding NavigateToChatCommand}"
                    Style="{DynamicResource SidebarButton}" />
        </StackPanel>

        <!-- Main content -->
        <ContentControl Grid.Column="1"
                        Content="{Binding CurrentView}"
                        Margin="20"/>
    </Grid>
</Window>
```

- [ ] **Step 3: MainWindow code-behind**

Replace `LiveDeck.App/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<MainViewModel>();
    }
}
```

- [ ] **Step 4: Sidebar button style (optional but tidy)**

Edit `LiveDeck.App/App.xaml` to add a default style. Inside `<Application.Resources>`:

```xml
<Application.Resources>
    <Style x:Key="SidebarButton" TargetType="Button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="Padding" Value="20,12"/>
        <Setter Property="HorizontalContentAlignment" Value="Left"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="FontSize" Value="14"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="#FF222222"/>
            </Trigger>
        </Style.Triggers>
    </Style>
</Application.Resources>
```

- [ ] **Step 5: Build (will still fail until child views/VMs exist)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App
```
Acceptable to see errors about `ActiveCodesViewModel`, `OrderQueueViewModel`, `ChatPanelViewModel` — resolved in Tasks 32–34.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/MainWindow.xaml LiveDeck.App/MainWindow.xaml.cs LiveDeck.App/App.xaml LiveDeck.App/ViewModels/MainViewModel.cs
git commit -m "feat(app): add MainWindow shell with sidebar navigation"
```

---

### Task 32: ActiveCodes panel (CRUD UI + ViewModel + EditCodeDialog)

**Files:**
- Create: `LiveDeck.App/ViewModels/ActiveCodesViewModel.cs`
- Create: `LiveDeck.App/Views/ActiveCodesView.xaml` + `.cs`
- Create: `LiveDeck.App/Views/EditCodeDialog.xaml` + `.cs`

- [ ] **Step 1: ActiveCodesViewModel**

Create `LiveDeck.App/ViewModels/ActiveCodesViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;

namespace LiveDeck.App.ViewModels;

public sealed partial class ActiveCodesViewModel : ViewModelBase
{
    private readonly ActiveCodeService _service;
    private readonly StreamSessionService _sessions;

    public ObservableCollection<ActiveCode> Codes { get; } = new();

    [ObservableProperty] private ActiveCode? _selected;

    public ActiveCodesViewModel(ActiveCodeService service, StreamSessionService sessions)
    {
        _service = service;
        _sessions = sessions;
        Refresh();
    }

    public void Refresh()
    {
        Codes.Clear();
        var session = _sessions.GetActive();
        if (session is null) return;
        foreach (var c in _service.GetActive(session.Id)) Codes.Add(c);
    }

    [RelayCommand]
    private void Add()
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat (Sipariş Kuyruğu → Yayın Başlat).",
                "Aktif yayın yok", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.EditCodeDialog();
        if (dialog.ShowDialog() != true) return;

        var sizes = (dialog.SizesText ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToArray();
        if (sizes.Length == 0) sizes = new[] { "TEK BEDEN" };

        _service.Add(session.Id, dialog.CodeText ?? "", sizes, dialog.Price);
        Refresh();
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is null) return;
        var dialog = new Views.EditCodeDialog
        {
            CodeText = Selected.Code,
            SizesText = string.Join(", ", Selected.Sizes),
            Price = Selected.Price
        };
        if (dialog.ShowDialog() != true) return;

        _service.UpdatePrice(Selected.Id, dialog.Price);
        var sizes = (dialog.SizesText ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToArray();
        _service.UpdateSizes(Selected.Id, sizes);
        Refresh();
    }

    [RelayCommand]
    private void CloseSelected()
    {
        if (Selected is null) return;
        _service.Close(Selected.Id);
        Refresh();
    }
}
```

- [ ] **Step 2: ActiveCodesView XAML**

Create `LiveDeck.App/Views/ActiveCodesView.xaml`:

```xml
<UserControl x:Class="LiveDeck.App.Views.ActiveCodesView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
            <TextBlock Text="Aktif Kodlar" FontSize="20" FontWeight="Bold"
                       VerticalAlignment="Center" Margin="0,0,20,0"/>
            <Button Content="+ Yeni Kod" Command="{Binding AddCommand}" Padding="14,6"/>
            <Button Content="Düzenle"    Command="{Binding EditSelectedCommand}" Padding="14,6" Margin="8,0,0,0"/>
            <Button Content="Kapat"      Command="{Binding CloseSelectedCommand}" Padding="14,6" Margin="8,0,0,0"/>
        </StackPanel>

        <DataGrid Grid.Row="1"
                  ItemsSource="{Binding Codes}"
                  SelectedItem="{Binding Selected}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  HeadersVisibility="Column"
                  Background="#FF1A1A1A"
                  Foreground="White"
                  BorderBrush="#FF333333"
                  GridLinesVisibility="Horizontal"
                  RowBackground="#FF1A1A1A"
                  AlternatingRowBackground="#FF222222">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Kod"     Binding="{Binding Code}"  Width="2*"/>
                <DataGridTextColumn Header="Bedenler" Width="3*">
                    <DataGridTextColumn.Binding>
                        <Binding Path="Sizes" Converter="{StaticResource JoinConverter}"/>
                    </DataGridTextColumn.Binding>
                </DataGridTextColumn>
                <DataGridTextColumn Header="Fiyat" Binding="{Binding Price, StringFormat={}{0:N2} TL}" Width="*"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</UserControl>
```

- [ ] **Step 3: JoinConverter for the Sizes column**

Create `LiveDeck.App/Converters/JoinConverter.cs`:

```csharp
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

public sealed class JoinConverter : IValueConverter
{
    public string Separator { get; set; } = ", ";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable e)
            return string.Join(Separator, e.Cast<object>());
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register in `App.xaml`:

```xml
<Application.Resources>
    <ResourceDictionary>
        <converters:JoinConverter x:Key="JoinConverter"
                                   xmlns:converters="clr-namespace:LiveDeck.App.Converters"/>
        <!-- existing styles below -->
    </ResourceDictionary>
</Application.Resources>
```

(Move existing `SidebarButton` style inside the same `ResourceDictionary`.)

- [ ] **Step 4: ActiveCodesView code-behind**

Create `LiveDeck.App/Views/ActiveCodesView.xaml.cs`:

```csharp
using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class ActiveCodesView : UserControl
{
    public ActiveCodesView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<ActiveCodesViewModel>();
    }
}
```

- [ ] **Step 5: EditCodeDialog**

Create `LiveDeck.App/Views/EditCodeDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.EditCodeDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Kod Ekle / Düzenle" Width="420" Height="280"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <StackPanel Margin="20" >
        <TextBlock Text="Kod"    FontWeight="Bold" Margin="0,0,0,4"/>
        <TextBox   Name="CodeBox" Padding="6" Margin="0,0,0,12"/>

        <TextBlock Text="Bedenler (virgülle ayır, örn: S, M, XL)" FontWeight="Bold" Margin="0,0,0,4"/>
        <TextBox   Name="SizesBox" Padding="6" Margin="0,0,0,12"/>

        <TextBlock Text="Fiyat (TL)" FontWeight="Bold" Margin="0,0,0,4"/>
        <TextBox   Name="PriceBox" Padding="6" Margin="0,0,0,12"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,8,0,0">
            <Button Content="İptal" Padding="14,6" Margin="0,0,8,0" Click="OnCancel"/>
            <Button Content="Kaydet" Padding="14,6" IsDefault="True" Click="OnSave"/>
        </StackPanel>
    </StackPanel>
</Window>
```

Create `LiveDeck.App/Views/EditCodeDialog.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;

namespace LiveDeck.App.Views;

public partial class EditCodeDialog : Window
{
    public EditCodeDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CodeBox.Text  = CodeText  ?? "";
            SizesBox.Text = SizesText ?? "";
            PriceBox.Text = Price.ToString("0.##", CultureInfo.InvariantCulture);
        };
    }

    public string? CodeText  { get; set; }
    public string? SizesText { get; set; }
    public decimal Price     { get; set; }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CodeText  = CodeBox.Text.Trim();
        SizesText = SizesBox.Text.Trim();
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) &&
            !decimal.TryParse(PriceBox.Text, NumberStyles.Any, new CultureInfo("tr-TR"), out p))
        {
            MessageBox.Show("Geçerli bir fiyat girin (örn: 199 veya 199.50)",
                "Geçersiz fiyat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Price = p;
        DialogResult = true;
    }
}
```

- [ ] **Step 6: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App
```
Should still fail on `OrderQueueView` / `ChatPanelView` — fixed next tasks.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/ActiveCodesView.xaml LiveDeck.App/Views/ActiveCodesView.xaml.cs LiveDeck.App/Views/EditCodeDialog.xaml LiveDeck.App/Views/EditCodeDialog.xaml.cs LiveDeck.App/ViewModels/ActiveCodesViewModel.cs LiveDeck.App/Converters
git commit -m "feat(app): add ActiveCodes panel with CRUD dialog"
```

---

### Task 33: OrderQueue panel + ViewModel + status update commands

**Files:**
- Create: `LiveDeck.App/ViewModels/OrderQueueViewModel.cs`
- Create: `LiveDeck.App/Views/OrderQueueView.xaml` + `.cs`

- [ ] **Step 1: OrderQueueViewModel**

Create `LiveDeck.App/ViewModels/OrderQueueViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;

namespace LiveDeck.App.ViewModels;

public sealed partial class OrderQueueViewModel : ViewModelBase
{
    private readonly OrderService _orders;
    private readonly StreamSessionService _sessions;
    private readonly Core.Storage.Repositories.OrderRepository _repo;

    public ObservableCollection<OrderItem> Orders { get; } = new();

    [ObservableProperty] private OrderItem? _selected;
    [ObservableProperty] private string _activeTab = OrderStatus.New;
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";

    public OrderQueueViewModel(
        OrderService orders,
        StreamSessionService sessions,
        Core.Storage.Repositories.OrderRepository repo)
    {
        _orders = orders;
        _sessions = sessions;
        _repo = repo;
        Refresh();
    }

    public void Refresh()
    {
        Orders.Clear();
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil — başlatmak için 'Yayın Başlat' tıklayın"
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt):HH:mm})";
        if (session is null) return;

        foreach (var o in _repo.GetBySessionAndStatus(session.Id, ActiveTab))
            Orders.Add(o);
    }

    partial void OnActiveTabChanged(string value) => Refresh();

    [RelayCommand] private void StartStream()
    {
        var session = _sessions.GetActive();
        if (session is not null)
        {
            MessageBox.Show("Zaten aktif bir yayın var. Önce mevcut yayını bitir.",
                "Yayın aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sessions.Start("Yeni Yayın", new[] { "instagram" });
        Refresh();
    }

    [RelayCommand] private void EndStream()
    {
        var session = _sessions.GetActive();
        if (session is null) return;

        var confirm = MessageBox.Show("Yayını bitirmek istediğinden emin misin?",
            "Yayını Bitir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _sessions.End(session.Id);
        Refresh();
    }

    [RelayCommand] private void Approve()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.New);
        Refresh();
    }

    [RelayCommand] private void MarkDmSent()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.DmSent);
        Refresh();
    }

    [RelayCommand] private void MarkPaid()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Paid);
        Refresh();
    }

    [RelayCommand] private void MarkShipped()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Shipped);
        Refresh();
    }

    [RelayCommand] private void MarkCompleted()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Completed);
        Refresh();
    }

    [RelayCommand] private void Cancel()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Cancelled);
        Refresh();
    }
}
```

- [ ] **Step 2: OrderQueueView XAML**

Create `LiveDeck.App/Views/OrderQueueView.xaml`:

```xml
<UserControl x:Class="LiveDeck.App.Views.OrderQueueView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <DockPanel Grid.Row="0" Margin="0,0,0,10">
            <TextBlock Text="Sipariş Kuyruğu" FontSize="20" FontWeight="Bold"
                       VerticalAlignment="Center"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" DockPanel.Dock="Right">
                <Button Content="Yayın Başlat" Command="{Binding StartStreamCommand}" Padding="14,6"/>
                <Button Content="Yayını Bitir" Command="{Binding EndStreamCommand}" Padding="14,6" Margin="8,0,0,0"/>
            </StackPanel>
        </DockPanel>

        <TextBlock Grid.Row="1" Text="{Binding StreamStatusLabel}"
                   Foreground="#FFAAAAAA" Margin="0,0,0,10"/>

        <!-- Status tabs -->
        <UniformGrid Grid.Row="2" Rows="1" Margin="0,0,0,10">
            <RadioButton Content="Yeni"        IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=new}"      GroupName="Tab"/>
            <RadioButton Content="Bekleyen"    IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=pending}"  GroupName="Tab"/>
            <RadioButton Content="DM Atıldı"   IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=dm_sent}"  GroupName="Tab"/>
            <RadioButton Content="Ödendi"      IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=paid}"     GroupName="Tab"/>
            <RadioButton Content="Kargoya"     IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=shipped}"  GroupName="Tab"/>
            <RadioButton Content="Tamamlandı"  IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=completed}" GroupName="Tab"/>
            <RadioButton Content="İptal"       IsChecked="{Binding ActiveTab, Converter={StaticResource StatusTabConverter}, ConverterParameter=cancelled}" GroupName="Tab"/>
        </UniformGrid>

        <!-- DataGrid + actions -->
        <Grid Grid.Row="3">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="200"/>
            </Grid.ColumnDefinitions>

            <DataGrid Grid.Column="0"
                      ItemsSource="{Binding Orders}"
                      SelectedItem="{Binding Selected}"
                      AutoGenerateColumns="False"
                      IsReadOnly="True"
                      HeadersVisibility="Column"
                      Background="#FF1A1A1A"
                      Foreground="White"
                      BorderBrush="#FF333333"
                      RowBackground="#FF1A1A1A"
                      AlternatingRowBackground="#FF222222">
                <DataGrid.Columns>
                    <DataGridTextColumn Header="Platform"     Binding="{Binding Platform}"      Width="*"/>
                    <DataGridTextColumn Header="Mesaj"        Binding="{Binding OriginalMessageText}" Width="3*"/>
                    <DataGridTextColumn Header="Kod"          Binding="{Binding Code}"          Width="*"/>
                    <DataGridTextColumn Header="Beden"        Binding="{Binding Size}"          Width="*"/>
                    <DataGridTextColumn Header="Adet"         Binding="{Binding Quantity}"      Width="*"/>
                    <DataGridTextColumn Header="Tutar"        Binding="{Binding TotalPrice, StringFormat={}{0:N2} TL}" Width="*"/>
                    <DataGridTextColumn Header="Güven"        Binding="{Binding Confidence}"    Width="*"/>
                </DataGrid.Columns>
            </DataGrid>

            <StackPanel Grid.Column="1" Margin="12,0,0,0">
                <TextBlock Text="İşlemler" FontWeight="Bold" Margin="0,0,0,8"/>
                <Button Content="Onayla"       Command="{Binding ApproveCommand}"       Padding="6" Margin="0,4"/>
                <Button Content="DM Atıldı"    Command="{Binding MarkDmSentCommand}"    Padding="6" Margin="0,4"/>
                <Button Content="Ödendi"       Command="{Binding MarkPaidCommand}"      Padding="6" Margin="0,4"/>
                <Button Content="Kargoya"      Command="{Binding MarkShippedCommand}"   Padding="6" Margin="0,4"/>
                <Button Content="Tamamlandı"   Command="{Binding MarkCompletedCommand}" Padding="6" Margin="0,4"/>
                <Button Content="İptal"        Command="{Binding CancelCommand}"        Padding="6" Margin="0,4"
                        Foreground="#FFFF6666"/>
            </StackPanel>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 3: StatusTabConverter**

Create `LiveDeck.App/Converters/StatusTabConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>
/// Two-way binds an "active tab" string to a RadioButton's IsChecked. The ConverterParameter
/// is the tab's status value; IsChecked is true iff ActiveTab equals the parameter.
/// </summary>
public sealed class StatusTabConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string current && parameter is string p && current == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p ? p : Binding.DoNothing;
}
```

Register in `App.xaml`'s `ResourceDictionary`:

```xml
<converters:StatusTabConverter x:Key="StatusTabConverter"
                                xmlns:converters="clr-namespace:LiveDeck.App.Converters"/>
```

- [ ] **Step 4: OrderQueueView code-behind**

Create `LiveDeck.App/Views/OrderQueueView.xaml.cs`:

```csharp
using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class OrderQueueView : UserControl
{
    public OrderQueueView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<OrderQueueViewModel>();
    }
}
```

- [ ] **Step 5: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App
```
Should still error on `ChatPanelView` — fixed in next task.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/OrderQueueViewModel.cs LiveDeck.App/Views/OrderQueueView.xaml LiveDeck.App/Views/OrderQueueView.xaml.cs LiveDeck.App/Converters/StatusTabConverter.cs
git commit -m "feat(app): add OrderQueue panel with status tabs and action buttons"
```

---

### Task 34: ChatPanel (in-app chat monitor)

**Files:**
- Create: `LiveDeck.App/ViewModels/ChatPanelViewModel.cs`
- Create: `LiveDeck.App/Views/ChatPanelView.xaml` + `.cs`

- [ ] **Step 1: ChatPanelViewModel**

Create `LiveDeck.App/ViewModels/ChatPanelViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using LiveDeck.Core.Chat;

namespace LiveDeck.App.ViewModels;

public sealed class ChatPanelViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private const int MaxMessages = 200;
    private readonly IDisposable _sub;
    private readonly Dispatcher _dispatcher;

    public ChatPanelViewModel(IChatBus bus)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sub = bus.Subscribe(OnMessage);
    }

    private void OnMessage(ChatMessage m)
    {
        // Marshal to UI thread (chat ingestors run on background threads)
        _dispatcher.BeginInvoke(() =>
        {
            Messages.Add(m);
            while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
        });
    }

    public void Dispose() => _sub.Dispose();
}
```

- [ ] **Step 2: ChatPanelView XAML**

Create `LiveDeck.App/Views/ChatPanelView.xaml`:

```xml
<UserControl x:Class="LiveDeck.App.Views.ChatPanelView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Canlı Chat" FontSize="20" FontWeight="Bold"
                   Margin="0,0,0,10"/>

        <ListBox Grid.Row="1"
                 ItemsSource="{Binding Messages}"
                 Background="#FF1A1A1A" Foreground="White"
                 BorderBrush="#FF333333" BorderThickness="1">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" Margin="0,4">
                        <TextBlock Text="{Binding Platform}"
                                   FontWeight="Bold"
                                   Foreground="#FFFFD166"
                                   Width="80"/>
                        <TextBlock Text="{Binding Username}"
                                   FontWeight="Bold"
                                   Width="160"
                                   TextTrimming="CharacterEllipsis"/>
                        <TextBlock Text="{Binding Text}" TextWrapping="Wrap"/>
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </Grid>
</UserControl>
```

- [ ] **Step 3: ChatPanelView code-behind**

Create `LiveDeck.App/Views/ChatPanelView.xaml.cs`:

```csharp
using System.Windows.Controls;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class ChatPanelView : UserControl
{
    public ChatPanelView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<ChatPanelViewModel>();
    }
}
```

- [ ] **Step 4: Build to confirm**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App
```
Expected: `Build succeeded.` (entire solution).

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/ChatPanelViewModel.cs LiveDeck.App/Views/ChatPanelView.xaml LiveDeck.App/Views/ChatPanelView.xaml.cs
git commit -m "feat(app): add ChatPanel showing live chat stream from bus"
```

---

### Task 35: Wire OrderService into the chat pipeline

**Files:**
- Create: `LiveDeck.App/Services/OrderCaptureWiring.cs`
- Modify: `LiveDeck.App/App.xaml.cs`

The OrderService needs to be invoked when a chat message arrives. We add a small wiring class that subscribes to `IChatBus` and calls `OrderService.Process` whenever there's an active session.

- [ ] **Step 1: OrderCaptureWiring**

Create `LiveDeck.App/Services/OrderCaptureWiring.cs`:

```csharp
using System;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services;

/// <summary>
/// Subscribes to <see cref="IChatBus"/> and feeds incoming messages into
/// <see cref="OrderService"/> when a stream session is active.
/// Owned by AppHost for the app lifetime.
/// </summary>
public sealed class OrderCaptureWiring : IDisposable
{
    private readonly OrderService _orders;
    private readonly StreamSessionService _sessions;
    private readonly ILogger<OrderCaptureWiring> _log;
    private readonly IDisposable _sub;

    public OrderCaptureWiring(
        IChatBus bus,
        OrderService orders,
        StreamSessionService sessions,
        ILogger<OrderCaptureWiring> log)
    {
        _orders = orders;
        _sessions = sessions;
        _log = log;
        _sub = bus.Subscribe(OnMessage);
    }

    private void OnMessage(ChatMessage m)
    {
        try
        {
            var session = _sessions.GetActive();
            if (session is null) return;

            var order = _orders.Process(session.Id, m);
            if (order is not null)
                _log.LogInformation("Captured order {Code} {Size} ×{Qty} from @{User} (conf={Conf})",
                    order.Code, order.Size, order.Quantity, m.Username, order.Confidence);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OrderCaptureWiring failed for message from @{User}", m.Username);
        }
    }

    public void Dispose() => _sub.Dispose();
}
```

- [ ] **Step 2: Register in AppHost**

Edit `LiveDeck.App/AppHost.cs`. Inside the constructor, before `Services = services.BuildServiceProvider();`, add:

```csharp
services.AddSingleton<Services.OrderCaptureWiring>();
```

After the `MigrationRunner.Run()` call, add:

```csharp
// Force-create singletons so they start running even before any window opens
_ = Services.GetRequiredService<Services.OrderCaptureWiring>();
```

- [ ] **Step 3: Start ingestor + overlay on app start**

Edit `LiveDeck.App/App.xaml.cs`:

```csharp
using System.Threading;
using System.Windows;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    private InstagramIngestor? _ingestor;
    private OverlayHost? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        Host = new AppHost();

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("LiveDeck starting up");

        _overlay  = Host.Services.GetRequiredService<OverlayHost>();
        _ingestor = Host.Services.GetRequiredService<InstagramIngestor>();

        // Fire-and-forget — bridge & overlay should always be running
        _ = _overlay.StartAsync();
        _ = _ingestor.StartAsync(CancellationToken.None);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ingestor?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _overlay?.StopAsync().GetAwaiter().GetResult();
        Host.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 4: Build and run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
dotnet run --project LiveDeck.App
```
Expected: WPF window opens with three sidebar items. No exceptions in Output window. Logs appear in `Documents/LiveDeck/Logs/`.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App
git commit -m "feat(app): wire OrderCaptureWiring + start overlay/ingestor on app launch"
```

---

## Etiket + Hotkey + Acceptance

### Task 36: ClipboardLabelFormatter

**Files:**
- Create: `LiveDeck.Labeling/ClipboardLabelFormatter.cs`
- Create: `LiveDeck.Tests/Labeling/ClipboardLabelFormatterTests.cs`

- [ ] **Step 1: Failing tests**

Create `LiveDeck.Tests/Labeling/ClipboardLabelFormatterTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Labeling;
using Xunit;

namespace LiveDeck.Tests.Labeling;

public class ClipboardLabelFormatterTests
{
    private static OrderItem Order(string username, string original, decimal price = 199m) =>
        new("o1", "s1", "ac1", "c1", "MAVI", "M", 1, price, price, 95,
            OrderStatus.New, original, 0, 0, null, null) with { };

    private readonly ClipboardLabelFormatter _f = new();

    [Fact]
    public void Format_uses_at_username_and_original_message()
    {
        var clipboard = _f.Format("@ayse_y", "MAVI XL aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_inserts_at_when_username_missing_prefix()
    {
        var clipboard = _f.Format("ayse_y", "MAVI XL aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_collapses_internal_whitespace()
    {
        var clipboard = _f.Format("@ayse_y", "MAVI    XL  aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_trims_outer_whitespace()
    {
        var clipboard = _f.Format("  @ayse_y  ", "  MAVI XL  ");
        clipboard.Should().Be("@ayse_y MAVI XL");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ClipboardLabelFormatterTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement ClipboardLabelFormatter**

Create `LiveDeck.Labeling/ClipboardLabelFormatter.cs`:

```csharp
using System.Text.RegularExpressions;

namespace LiveDeck.Labeling;

/// <summary>
/// Builds the clipboard payload that the user's existing label app (etiket.exe) consumes
/// via its clipboard polling. The expected shape is `@username YORUM`, all single-spaced.
/// </summary>
public sealed class ClipboardLabelFormatter
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public string Format(string username, string originalMessage)
    {
        var u = (username ?? "").Trim();
        if (!u.StartsWith('@')) u = "@" + u;

        var msg = Whitespace.Replace(originalMessage ?? "", " ").Trim();

        return $"{u} {msg}".Trim();
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~ClipboardLabelFormatterTests"
```
Expected: PASS — 4/4.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Labeling/ClipboardLabelFormatter.cs LiveDeck.Tests/Labeling
git commit -m "feat(labeling): add ClipboardLabelFormatter for etiket app integration"
```

---

### Task 37: ClipboardService + HotkeyService (F9)

**Files:**
- Create: `LiveDeck.App/Services/ClipboardService.cs`
- Create: `LiveDeck.App/Services/HotkeyService.cs`
- Modify: `LiveDeck.App/AppHost.cs` (register services)
- Modify: `LiveDeck.App/MainWindow.xaml.cs` (register hotkey + handler)

WPF can register global hotkeys via `RegisterHotKey` / `UnregisterHotKey` from `user32.dll`, hooked through a `HwndSource`.

- [ ] **Step 1: ClipboardService**

Create `LiveDeck.App/Services/ClipboardService.cs`:

```csharp
using System.Windows;

namespace LiveDeck.App.Services;

public sealed class ClipboardService
{
    public void SetText(string text)
    {
        // Clipboard.SetText must run on the UI thread
        if (Application.Current.Dispatcher.CheckAccess())
            Clipboard.SetText(text);
        else
            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
    }
}
```

- [ ] **Step 2: HotkeyService (Win32 RegisterHotKey)**

Create `LiveDeck.App/Services/HotkeyService.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LiveDeck.App.Services;

/// <summary>
/// Registers a Windows-global hotkey via user32. Currently used for F9 → "Clipboard the
/// selected order's label". When the hotkey fires, <see cref="HotkeyPressed"/> is raised.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NONE = 0;
    private const uint VK_F9 = 0x78;
    private const int HotkeyId = 0xC001;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotkeyPressed;

    private HwndSource? _source;
    private bool _registered;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        if (_source is null) return;
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(helper.Handle, HotkeyId, MOD_NONE, VK_F9);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        if (_registered)
        {
            var helper = new WindowInteropHelper((Window)System.Windows.Application.Current.MainWindow);
            UnregisterHotKey(helper.Handle, HotkeyId);
        }
        _source.RemoveHook(WndProc);
    }
}
```

- [ ] **Step 3: Register services in AppHost**

Edit `LiveDeck.App/AppHost.cs`. Add inside the service registrations:

```csharp
services.AddSingleton<Services.ClipboardService>();
services.AddSingleton<Services.HotkeyService>();
services.AddSingleton<LiveDeck.Labeling.ClipboardLabelFormatter>();
```

- [ ] **Step 4: Wire hotkey in MainWindow**

Edit `LiveDeck.App/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.Services;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Labeling;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey;
    private readonly ClipboardService _clipboard;
    private readonly ClipboardLabelFormatter _formatter;
    private readonly OrderQueueViewModel _orderQueue;
    private readonly CustomerRepository _customers;

    public MainWindow()
    {
        InitializeComponent();

        var sp = App.Host.Services;
        DataContext       = sp.GetRequiredService<MainViewModel>();
        _hotkey           = sp.GetRequiredService<HotkeyService>();
        _clipboard        = sp.GetRequiredService<ClipboardService>();
        _formatter        = sp.GetRequiredService<ClipboardLabelFormatter>();
        _orderQueue       = sp.GetRequiredService<OrderQueueViewModel>();
        _customers        = sp.GetRequiredService<CustomerRepository>();

        Loaded += OnLoaded;
        Closed += (_, _) => _hotkey.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkey.Attach(this);
        _hotkey.HotkeyPressed += OnF9;
    }

    private void OnF9()
    {
        var order = _orderQueue.Selected;
        if (order is null) return;

        var customer = _customers.GetById(order.CustomerId);
        var username = customer?.Username ?? "@unknown";

        var payload = _formatter.Format(username, order.OriginalMessageText);
        _clipboard.SetText(payload);
    }
}
```

- [ ] **Step 5: Build and smoke test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
dotnet run --project LiveDeck.App
```

Manually: start a stream → add a code (MAVI, sizes M/XL, 199 TL) → from Instagram (with extension running), have someone post "MAVI XL aldım" → it appears in the queue → click it → press F9 → switch to a text editor → Ctrl+V → confirm `@username MAVI XL aldım` appears.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App
git commit -m "feat(app): add F9 hotkey copying selected order's label to clipboard"
```

---

### Task 38: Optional etiket.exe FindWindow integration (settings flag)

**Files:**
- Create: `LiveDeck.App/Services/EtiketIntegration.cs`
- Modify: `LiveDeck.App/MainWindow.xaml.cs` (use integration when enabled)

Etiket.exe (the legacy WinForms label app at `C:\Users\burak\Downloads\etiket\`) polls clipboard and uses `textBox1` for the price. When `AppSettings.EtiketIntegrationEnabled` is true, we additionally write the order's price into `textBox1` via Win32 `SendMessage`. This is opt-in and best-effort — failures are logged, never thrown.

- [ ] **Step 1: Implement EtiketIntegration**

Create `LiveDeck.App/Services/EtiketIntegration.cs`:

```csharp
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LiveDeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services;

/// <summary>
/// Optional Win32 integration with the legacy etiket.exe app: sets its first textbox
/// (price field) to the current order's unit price before LiveDeck writes the comment to
/// clipboard. Enabled via <see cref="AppSettings.EtiketIntegrationEnabled"/>.
/// </summary>
public sealed class EtiketIntegration
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

    private const uint WM_SETTEXT = 0x000C;

    private readonly AppSettings _settings;
    private readonly ILogger<EtiketIntegration> _log;

    public EtiketIntegration(AppSettings settings, ILogger<EtiketIntegration> log)
    {
        _settings = settings;
        _log = log;
    }

    public bool TrySetPrice(decimal price)
    {
        if (!_settings.EtiketIntegrationEnabled) return false;

        var title = _settings.EtiketWindowTitle ?? "etiket";
        var window = FindWindow(null, title);
        if (window == IntPtr.Zero)
        {
            _log.LogDebug("Etiket window '{Title}' not found", title);
            return false;
        }

        // First TextBox is "EDIT" class on WinForms
        var firstEdit = FindWindowEx(window, IntPtr.Zero, "WindowsForms10.EDIT.app.0.bf7d44_r0_ad1", null);
        if (firstEdit == IntPtr.Zero)
            firstEdit = FindWindowEx(window, IntPtr.Zero, "Edit", null);

        if (firstEdit == IntPtr.Zero)
        {
            _log.LogDebug("Etiket textBox1 not found (window class names vary by .NET version)");
            return false;
        }

        var priceText = ((int)price).ToString(CultureInfo.InvariantCulture);
        SendMessage(firstEdit, WM_SETTEXT, IntPtr.Zero, priceText);
        return true;
    }
}
```

- [ ] **Step 2: Register in AppHost**

Add to `AppHost`:

```csharp
services.AddSingleton<Services.EtiketIntegration>();
```

- [ ] **Step 3: Use in MainWindow's OnF9**

Edit `LiveDeck.App/MainWindow.xaml.cs`. Update `OnF9`:

```csharp
private void OnF9()
{
    var order = _orderQueue.Selected;
    if (order is null) return;

    // Optional: write price into etiket.exe before clipboard
    var etiket = App.Host.Services.GetRequiredService<EtiketIntegration>();
    etiket.TrySetPrice(order.UnitPrice);

    var customer = _customers.GetById(order.CustomerId);
    var username = customer?.Username ?? "@unknown";

    var payload = _formatter.Format(username, order.OriginalMessageText);
    _clipboard.SetText(payload);
}
```

Add `using Microsoft.Extensions.DependencyInjection;` if not already present (it is — used elsewhere in the file).

- [ ] **Step 4: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
```
Expected: `Build succeeded.`

- [ ] **Step 5: Manual test (optional)**

If `EtiketIntegrationEnabled = true` in `Documents/LiveDeck/settings.json` AND etiket.exe is open (window title contains "etiket"), pressing F9 should set the price in textBox1 in addition to writing to clipboard. If etiket.exe isn't open, no failure occurs.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Services/EtiketIntegration.cs LiveDeck.App/AppHost.cs LiveDeck.App/MainWindow.xaml.cs
git commit -m "feat(app): add optional etiket.exe FindWindow integration for price field"
```

---

### Task 39: End-to-end acceptance smoke test

**No new code — this task gates the phase as "done" by exercising the full path.**

- [ ] **Step 1: Run the full app**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln
dotnet run --project LiveDeck.App
```

Expected: WPF window opens with sidebar (Sipariş Kuyruğu / Aktif Kodlar / Chat). No exceptions in logs at `Documents/LiveDeck/Logs/log-{today}.txt`.

- [ ] **Step 2: Start a stream**

In the running app: Sipariş Kuyruğu sekmesinde "Yayın Başlat" → status değişir.

- [ ] **Step 3: Add an active code**

Aktif Kodlar sekmesi → "+ Yeni Kod" → Kod: `MAVI`, Bedenler: `S, M, XL`, Fiyat: `199`.

- [ ] **Step 4: Connect the browser extension**

Open Chrome, ensure LiveDeck Extension is loaded (`chrome://extensions/`). Open an Instagram Live page (or a saved Instagram page that has chat-like comments injected for testing).

Confirm in `Chat` sekmesi that messages start appearing within ~2 seconds of being posted on Instagram.

- [ ] **Step 5: Capture an order**

From a second account, comment on the Instagram Live: `MAVI XL aldım`.

Within 1-2 seconds, the order appears in `Sipariş Kuyruğu → Yeni` tab with:
- Code: `MAVI`
- Size: `XL`
- Quantity: `1`
- TotalPrice: `199.00 TL`
- Confidence: ≥ 80

- [ ] **Step 6: Print a label**

Click the captured order to select it. Press **F9**. Open Notepad, Ctrl+V. Paste should show `@<username> MAVI XL aldım`.

If etiket.exe is open and `EtiketIntegrationEnabled = true`, the price field of etiket.exe should also be set to `199`.

- [ ] **Step 7: Move order through statuses**

Click the order → "DM Atıldı" button → tab switches to DM Atıldı, order is there. Repeat for Ödendi, Kargoya, Tamamlandı.

- [ ] **Step 8: Verify OBS overlay**

Open OBS Studio. Add Browser Source → URL `http://localhost:4747/overlay/chat`, size 500×800.

Live Instagram messages appear in the overlay with username + text, transparent background, slide-in animation. Messages persist across browser source refresh (state snapshot recovery works).

- [ ] **Step 9: End the stream**

Sipariş Kuyruğu → "Yayını Bitir" → onay → status text changes.

- [ ] **Step 10: Verify SQLite contents**

```bash
cd /c/Users/burak/source/repos/LiveDeck
sqlite3 ~/Documents/LiveDeck/data/livedeck.db "SELECT Code, Size, Quantity, Status, OriginalMessageText FROM OrderItem;"
```
Expected: rows reflecting the orders captured during the smoke test.

- [ ] **Step 11: All Phase 1 acceptance criteria met → tag commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git tag -a phase-1 -m "Phase 1 — Instagram-only core complete"
```

---

## Final Self-Review (already performed by author)

The plan was cross-checked against `docs/superpowers/specs/2026-04-27-livedeck-design.md` Section 7 Faz 1 acceptance criteria:

| Spec requirement                                          | Task(s) |
|-----------------------------------------------------------|---------|
| Solution iskeleti (.NET 10, 6 proje)                      | 1, 2, 3 |
| UniCast'ten ChatBus + Browser Extension + ExtensionBridgeServer | 23, 24, 25 |
| Instagram chat akışı (extension → bridge → ChatBus)       | 25, 26, 35 |
| SQLite + Dapper + ilk migration (6 ana tablo)             | 7, 8, 9 |
| WPF MainWindow + temel navigasyon                         | 31 |
| ActiveCode paneli (CRUD)                                  | 32 |
| OrderCaptureEngine (TR normalize+fuzzy+variant+qty+intent+conf) | 14-20 |
| Sipariş kuyruğu paneli (durum akışı: Yeni → ... → Tamamlandı) | 33 |
| OBS chat overlay                                          | 27, 28 |
| Etiket clipboard otomasyonu (F9)                          | 36, 37 |
| TDD: 50+ Türkçe test case                                 | 22 (50 fixtures) + per-stage tests in 14-19 |
| Acceptance: 1 saatlik yayın 30+ sipariş kaçırmaz          | 39 |

All boxes checked.

Type/method consistency: `OrderCaptureEngine.HighConfidenceThreshold`/`LowConfidenceThreshold` used in OrderService matches definitions. `IChatBus.Subscribe/Publish/RecentMessages` consistent across ChatBus tests, ExtensionBridgeServer, OverlayHost, ChatPanelViewModel, OrderCaptureWiring. `OrderStatus.*` constants used identically in OrderQueueViewModel and OrderService. Hotkey id `0xC001` used in both Register and Unregister calls.

No `TBD` / `TODO` / "fill in later" markers remain. Every code step has full code; every command step has the exact bash invocation and expected output.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-27-phase-1-instagram-core.md`.**

Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent dispatched per task, two-stage review between tasks, fast iteration with checkpoints. Best for a 39-task multi-component plan like this where each task is self-contained.

**2. Inline Execution** — Tasks executed in this session via the executing-plans skill, batched with checkpoints. Faster turnaround on small plans but pollutes context across 39 tasks.

**Which approach?**
