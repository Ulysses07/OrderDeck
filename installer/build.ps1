<#
.SYNOPSIS
  Builds the OrderDeck Windows installer (.exe) using Inno Setup.

.DESCRIPTION
  Runs three steps:
    1. dotnet publish OrderDeck.App with the win-x64-installer profile
       → publish/ at the repo root (~150 MB self-contained).
    2. Downloads MicrosoftEdgeWebview2Setup.exe (the evergreen bootstrapper)
       from Microsoft's public CDN if not already cached in installer/.
    3. Invokes ISCC.exe (Inno Setup compiler) on installer/orderdeck.iss
       with the supplied version, producing dist/OrderDeck-X.Y.Z-setup.exe.

.PARAMETER Version
  Semantic version stamped into AppVersion / output filename. Default 0.1.0.

.PARAMETER InnoSetupPath
  Path to ISCC.exe. Default = standard Inno Setup 6 install location.

.PARAMETER SkipPublish
  Skip the dotnet publish step (use existing publish/ tree). Useful for
  iterating on the .iss script without re-publishing 150 MB.

.EXAMPLE
  installer\build.ps1 -Version 1.0.0
#>
param(
  [string]$Version = "0.1.0",
  [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # speeds up Invoke-WebRequest

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repoRoot
try {
  if (-not $SkipPublish) {
    Write-Host "[1/3] dotnet publish (self-contained win-x64)..." -ForegroundColor Cyan
    dotnet publish OrderDeck.App\OrderDeck.App.csproj `
      -p:PublishProfile=win-x64-installer `
      -c Release `
      --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
  } else {
    Write-Host "[1/3] dotnet publish — SKIPPED (-SkipPublish)" -ForegroundColor Yellow
  }

  $bootstrap = Join-Path $repoRoot "installer\MicrosoftEdgeWebview2Setup.exe"
  if (-not (Test-Path $bootstrap)) {
    Write-Host "[2/3] Downloading WebView2 evergreen bootstrapper..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" `
                      -OutFile $bootstrap -UseBasicParsing
  } else {
    Write-Host "[2/3] WebView2 bootstrapper already cached." -ForegroundColor DarkGray
  }

  if (-not (Test-Path $InnoSetupPath)) {
    throw "Inno Setup compiler not found at: $InnoSetupPath`n" +
          "Install from https://jrsoftware.org/isdl.php and re-run, or pass -InnoSetupPath."
  }

  Write-Host "[3/3] Compiling installer (Version=$Version)..." -ForegroundColor Cyan
  & $InnoSetupPath "/DAPP_VERSION=$Version" "$repoRoot\installer\orderdeck.iss"
  if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (code $LASTEXITCODE)" }

  $output = Join-Path $repoRoot "dist\OrderDeck-$Version-setup.exe"
  if (Test-Path $output) {
    $size = (Get-Item $output).Length / 1MB
    Write-Host ""
    Write-Host "✓ Done: $output ($([math]::Round($size,1)) MB)" -ForegroundColor Green
  } else {
    Write-Host "⚠ Compile reported success but output not found at $output" -ForegroundColor Yellow
  }
}
finally {
  Pop-Location
}
