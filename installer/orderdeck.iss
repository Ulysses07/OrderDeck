; Inno Setup 6.3+ installer for OrderDeck.
; Build via installer\build.ps1 -Version X.Y.Z (which sets APP_VERSION).
;
; Notes:
; - Per-user install (PrivilegesRequired=lowest) → no UAC prompt; writes
;   to %LOCALAPPDATA%\Programs\OrderDeck\, leaves AppPaths' real data
;   under %USERPROFILE%\Documents\OrderDeck untouched on uninstall.
; - x64compatible covers both x64 and ARM64 Windows 11.
; - Modern wizard style; LZMA2 ultra to keep ~150 MB self-contained
;   payload as small as possible (typical ratio ~50%).
; - Turkish language pack (Turkish.isl ships with Inno Setup 6).
; - WebView2 evergreen detection in [Code]; bootstrapper downloaded by
;   build.ps1 and embedded in [Files] → [Run] silent install if missing.
; - No SetupIconFile yet — uses Inno's default. TODO: design team .ico.
; - Code signing skipped in Phase 1; Phase 2 will add SignTool.

#ifndef APP_VERSION
  #define APP_VERSION "0.1.0"
#endif

[Setup]
AppId={{6F7B12C8-3D9E-4F2B-A1D6-ORDERDECK-001}}
AppName=OrderDeck
AppVersion={#APP_VERSION}
AppVerName=OrderDeck {#APP_VERSION}
AppPublisher=Ulysses07
AppPublisherURL=https://orderdeckapp.com
AppSupportURL=https://orderdeckapp.com/destek
AppUpdatesURL=https://orderdeckapp.com/indir
DefaultDirName={localappdata}\Programs\OrderDeck
DefaultGroupName=OrderDeck
DisableProgramGroupPage=yes
DisableDirPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
OutputBaseFilename=OrderDeck-{#APP_VERSION}-setup
OutputDir=..\dist
UninstallDisplayIcon={app}\OrderDeck.App.exe
UninstallDisplayName=OrderDeck {#APP_VERSION}
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek seçenekler:"; Flags: checkedonce

[Files]
; Main app — published into ..\publish by build.ps1
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Chrome extension (sideload). First-run wizard's "Klasörü Aç"
; button points at {app}\Extension so the operator can drag it
; into chrome://extensions.
Source: "..\Extension\*"; DestDir: "{app}\Extension"; Flags: ignoreversion recursesubdirs createallsubdirs

; WebView2 evergreen bootstrapper — silent-install runs in [Run] if
; the runtime isn't already on the machine. build.ps1 downloads
; this from Microsoft's public CDN before invoking iscc.
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\OrderDeck"; Filename: "{app}\OrderDeck.App.exe"
Name: "{group}\Logları Aç"; Filename: "{userdocs}\OrderDeck\Logs"
Name: "{group}\OrderDeck'i Kaldır"; Filename: "{uninstallexe}"
Name: "{userdesktop}\OrderDeck"; Filename: "{app}\OrderDeck.App.exe"; Tasks: desktopicon

[Run]
; WebView2 install runs first — silent, no UI; only fires when registry
; check fails (i.e., evergreen runtime missing). Win10 22H2 / Win11 ship
; with it preinstalled, so this is mostly a fallback for older builds.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; Check: not IsWebView2Installed; StatusMsg: "WebView2 çalışma zamanı kuruluyor..."

; Postinstall launch (default checked).
Filename: "{app}\OrderDeck.App.exe"; Description: "OrderDeck'i şimdi başlat"; Flags: postinstall nowait skipifsilent

[Code]
function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  // System-wide install (preferred; matches Edge installation)
  if RegQueryStringValue(HKLM,
       'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
       'pv', Version) and (Version <> '') then
  begin
    Result := True;
    exit;
  end;
  // Per-user install fallback
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
       'pv', Version) and (Version <> '') then
  begin
    Result := True;
    exit;
  end;
  Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Reserved hook for future steps (e.g. firewall rules for ports
    // 4747/4748 if Windows Firewall ever blocks loopback).
  end;
end;
