; Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬ Tasky Ã¢â‚¬â€œ Inno Setup 6 installer script Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬Ã¢â€â‚¬
; Download Inno Setup 6 from: https://jrsoftware.org/isdl.php
; Compile: iscc.exe setup.iss
; Output:  installer\Output\TaskySetup.exe

#define AppName      "Tasky"
; The build passes the project version with /DAppVersion=...; this is the fallback
#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#define AppPublisher "Muhammad Aadil"
#define AppURL       "https://github.com/muaa3405-spec/tasky"
#define AppExeName   "Tasky.exe"
; Path to the published output of  dotnet publish -c Release -r win-x64 -o <PublishDir>
#define PublishDir   "..\Tasky\bin\Release\net10.0\win-x64\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
; File properties of TaskySetup.exe; code signing (SignPath) checks the product name
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=TaskySetup
SetupIconFile=..\Tasky\Assets\icon.ico
; Icon shown in Windows Settings Ã¢â€ â€™ Apps Ã¢â€ â€™ Installed apps
UninstallDisplayIcon={app}\icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
; Run at startup without UAC elevation on next login
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";    Description: "{cm:CreateDesktopIcon}";    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupentry";   Description: "Start Tasky with Windows";  GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Publish the entire self-contained folder
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Copy the icon file explicitly Ã¢â‚¬â€ in a self-contained .NET publish the .ico is
; compiled into the exe as a resource and is NOT present as a loose file in the
; publish folder, so shortcuts cannot extract it from {app}\Tasky.exe reliably.
Source: "..\Tasky\Assets\icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Registry]
; Add to Windows startup (HKCU so no UAC needed)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "Tasky"; \
    ValueData: """{app}\{#AppExeName}"""; \
    Tasks: startupentry; Flags: uninsdeletevalue

[Run]
; Launch after install (as the logged-in user, not elevated)
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Kill the process gracefully before uninstall
Filename: "taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: skipifdoesntexist runhidden
