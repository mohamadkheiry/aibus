#ifndef PublishDir
  #define PublishDir "C:\ArkaCodeBuild\desktop-publish"
#endif
#ifndef OutputDir
  #define OutputDir "C:\ArkaCodeBuild\installer-output"
#endif

[Setup]
AppId={{E47B7AE4-794F-49BE-BC83-7686CFD17C3E}
AppName=ArkaCode
AppVersion=1.0.0
AppPublisher=Arka
AppPublisherURL=https://aibus.00f.ir
AppSupportURL=https://aibus.00f.ir
DefaultDirName={localappdata}\Programs\ArkaCode
DefaultGroupName=ArkaCode
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=ArkaCode-Windows-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\ArkaCode.exe
VersionInfoVersion=1.0.0.0
VersionInfoCompany=Arka
VersionInfoDescription=ArkaCode Agentic Development Studio Installer
VersionInfoProductName=ArkaCode
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\ArkaCode.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ArkaCode"; Filename: "{app}\ArkaCode.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ArkaCode"; Filename: "{app}\ArkaCode.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ArkaCode.exe"; Description: "Launch ArkaCode"; Flags: nowait postinstall skipifsilent
