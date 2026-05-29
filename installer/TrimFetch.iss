; Inno Setup — TrimFetchSetup-{version}-{arch}.exe (PowerToys-style naming)
; CI: pass absolute /DPublishDir (Inno resolves relative paths from this script's folder).

#ifndef MyAppName
  #define MyAppName "TrimFetch Media Downloader"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "TrimFetch.exe"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\TrimFetch\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef TargetArch
  #define TargetArch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#if TargetArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchInstallMode "arm64"
#else
  #define ArchAllowed "x64compatible"
  #define ArchInstallMode "x64compatible"
#endif

[Setup]
AppId={{EC99169C-4748-4C74-A6AE-7099E6C9E4FD}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Zied Kallel
AppPublisherURL=https://github.com/zioder/trimfetch
AppSupportURL=https://github.com/zioder/trimfetch/issues
AppUpdatesURL=https://github.com/zioder/trimfetch/releases
DefaultDirName={autopf}\TrimFetch
DefaultGroupName=TrimFetch
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=TrimFetchSetup-{#MyAppVersion}-{#TargetArch}
SetupIconFile=..\src\TrimFetch\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallMode}
MinVersion=10.0.17763
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
