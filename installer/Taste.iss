#define AppName    "Taste"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe     "Taste.exe"
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Taste
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\installer-output
OutputBaseFilename=Taste-Setup-{#AppVersion}
SetupIconFile=..\Assets\icon.ico
WizardSmallImageFile=..\Assets\icon.png
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "{#PublishDir}\{#AppExe}";                    DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\D3DCompiler_47_cor3.dll";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\PenImc_cor3.dll";               DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\PresentationNative_cor3.dll";   DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\vcruntime140_cor3.dll";          DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\wpfgfx_cor3.dll";               DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"
Name: startup;     Description: "Start {#AppName} with Windows (runs in system tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
