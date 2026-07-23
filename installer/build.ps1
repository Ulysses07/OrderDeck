<#
.SYNOPSIS
  Builds the OrderDeck Velopack release (Setup.exe + full/delta .nupkg + feed
  manifest) using the vpk CLI.

.DESCRIPTION
  Steps:
    1. dotnet publish OrderDeck.App (win-x64-installer profile, self-contained)
       → publish/ at the repo root.
    2. Copies the Chrome extension into publish/Extension and the WebView2
       evergreen bootstrapper into publish/ (App.OnAfterInstallFastCallback
       silently installs WebView2 from there if the runtime is missing).
    3. Ensures the vpk tool is installed (version must match the Velopack
       NuGet package — see Directory.Packages.props).
    4. vpk pack → Releases/ (Setup.exe, OrderDeck-<ver>-full.nupkg, delta
       against any previous releases already present in Releases/, and the
       releases.win.json feed manifest).

  Delta packages are produced only when previous releases exist in the
  output dir; CI pre-populates Releases/ from the live feed before packing.

.PARAMETER Version
  Semantic version stamped into the package + Setup.exe. Default 0.1.0.

.PARAMETER SkipPublish
  Reuse an existing publish/ tree (iterate on packaging without re-publishing).

.EXAMPLE
  installer\build.ps1 -Version 0.5.0
#>
param(
  [string]$Version = "0.1.0",
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # speeds up Invoke-WebRequest

# vpk sürümü Velopack NuGet paketiyle AYNI olmalı (Directory.Packages.props).
$VpkVersion = '1.2.0'

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repoRoot
try {
  $publishDir  = Join-Path $repoRoot 'publish'
  $releasesDir = Join-Path $repoRoot 'Releases'

  if (-not $SkipPublish) {
    Write-Host "[1/4] dotnet publish (self-contained win-x64)..." -ForegroundColor Cyan
    dotnet publish OrderDeck.App\OrderDeck.App.csproj `
      -p:PublishProfile=win-x64-installer `
      -p:Version=$Version `
      -c Release `
      --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
  } else {
    Write-Host "[1/4] dotnet publish - SKIPPED (-SkipPublish)" -ForegroundColor Yellow
  }

  Write-Host "[2/4] Extension + WebView2 bootstrapper -> publish/..." -ForegroundColor Cyan
  # Chrome extension: Velopack app kökü = publish. First-run sihirbazının
  # "Klasörü Aç" butonu {app}\Extension'ı açar.
  $extSrc = Join-Path $repoRoot 'Extension'
  $extDst = Join-Path $publishDir 'Extension'
  if (Test-Path $extDst) { Remove-Item $extDst -Recurse -Force }
  Copy-Item $extSrc $extDst -Recurse -Force

  # WebView2 evergreen bootstrapper — App.TryInstallWebView2 BaseDirectory'de arar.
  $bootstrap = Join-Path $repoRoot 'installer\MicrosoftEdgeWebview2Setup.exe'
  if (-not (Test-Path $bootstrap)) {
    Write-Host "      WebView2 bootstrapper indiriliyor..." -ForegroundColor DarkGray
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" `
                      -OutFile $bootstrap -UseBasicParsing
  }
  Copy-Item $bootstrap (Join-Path $publishDir 'MicrosoftEdgeWebview2Setup.exe') -Force

  Write-Host "[3/4] vpk (Velopack CLI) kontrol/kur ($VpkVersion)..." -ForegroundColor Cyan
  if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install -g vpk --version $VpkVersion
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
  }

  Write-Host "[4/4] vpk pack (Version=$Version)..." -ForegroundColor Cyan
  vpk pack `
    --packId OrderDeck `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe OrderDeck.App.exe `
    --packTitle OrderDeck `
    --icon (Join-Path $repoRoot 'OrderDeck.App\orderdeck.ico') `
    --outputDir $releasesDir
  if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (code $LASTEXITCODE)" }

  Write-Host ""
  Write-Host "Done. Velopack release at: $releasesDir" -ForegroundColor Green
  Get-ChildItem $releasesDir | Where-Object { $_.Name -notlike '*.nupkg.tmp' } |
    Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
}
finally {
  Pop-Location
}
