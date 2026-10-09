; Tersus - per-user installer (Inno Setup 6).
;   * installs under %LOCALAPPDATA%\Programs\Tersus, writes only to HKCU, never asks for administrator rights
;   * no autostart entry, no service, no scheduled task, no file association, no shell extension
;   * the uninstaller removes the program only; the person's own data (%LOCALAPPDATA%\Tersus: history and cleanup logs) is left alone
; Build:  ISCC.exe /DAppVersion=0.3.0 /DSourceExe=..\publish\Tersus.exe Tersus.iss      (see .github/workflows)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\Tersus.exe"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "..\dist"
#endif

[Setup]
AppId={{8D3F1C52-6B5A-4E0B-9C7A-2F1E6A4D9B30}
AppName=Tersus
AppVersion={#AppVersion}
AppVerName=Tersus {#AppVersion}
AppPublisher=Tersus
DefaultDirName={localappdata}\Programs\Tersus
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=Tersus-Setup-{#AppVersion}-x64
SetupIconFile=..\Tersus.App\Assets\Tersus.ico
UninstallDisplayIcon={app}\Tersus.exe
UninstallDisplayName=Tersus
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
LanguageDetectionMethod=none
InfoBeforeFile=LEIA-ME-ANTES.txt
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Tersus
VersionInfoProductName=Tersus
VersionInfoDescription=Instalador do Tersus
VersionInfoCopyright=Copyright (c) 2026 Tersus contributors. MIT License.

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Tersus.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Tersus"; Filename: "{app}\Tersus.exe"; Comment: "Análise de armazenamento e limpeza segura"
Name: "{autodesktop}\Tersus"; Filename: "{app}\Tersus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tersus.exe"; Description: "Abrir o Tersus agora"; Flags: nowait postinstall skipifsilent
