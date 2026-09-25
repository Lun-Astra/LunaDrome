; LunaDrome Windows installer (Inno Setup 6).
; Build:  ISCC.exe /DAppVersion=2.0.0 /DSourceDir=<publish folder> installer\windows\LunaDrome.iss
; The publish folder comes from:
;   dotnet publish app/LunaDrome/LunaDrome.csproj -f net10.0-desktop -c Release -r win-x64 --self-contained true -o publish/LunaDrome
; Output: installer\output\LunaDrome-Setup-<version>.exe (with uninstaller + Apps & features entry).

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\LunaDrome"
#endif

[Setup]
; Fixed AppId: keeps upgrades/uninstall tied to the same installation across versions. Never change it.
AppId={{6E4C2A8B-3F1D-4B7E-9C5A-2D8F1E7B4A93}
AppName=LunaDrome
AppVersion={#AppVersion}
AppVerName=LunaDrome {#AppVersion}
AppPublisher=LunAstra
AppPublisherURL=https://github.com/Lun-Astra/LunaDrome
AppSupportURL=https://github.com/Lun-Astra/LunaDrome/issues
DefaultDirName={autopf}\LunaDrome
DefaultGroupName=LunaDrome
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); the user can choose all-users in the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\output
OutputBaseFilename=LunaDrome-Setup-{#AppVersion}
SetupIconFile=..\..\app\LunaDrome\Assets\lunadrome.ico
UninstallDisplayIcon={app}\LunaDrome.exe
UninstallDisplayName=LunaDrome
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Close a running LunaDrome before upgrading/uninstalling instead of failing on locked files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LunaDrome"; Filename: "{app}\LunaDrome.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\LunaDrome"; Filename: "{app}\LunaDrome.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LunaDrome.exe"; Description: "{cm:LaunchProgram,LunaDrome}"; Flags: nowait postinstall skipifsilent
; The app's built-in updater runs this setup with /SILENT after closing itself: start it again.
Filename: "{app}\LunaDrome.exe"; Flags: nowait; Check: WizardSilent

; Settings (server URL, remembered login) live in the user's profile, not {app}, so they survive
; uninstall/reinstall on purpose - uninstalling the app shouldn't sign you out of your server.
