#ifndef AppVersion
  #error AppVersion must be supplied with /DAppVersion=1.2.3
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion AppVersion
#endif
#ifndef PublishDir
  #define PublishDir SourcePath + "..\artifacts\AwsManager"
#endif

[Setup]
AppId={{D1C80E5F-51DE-4FAE-A7E9-8435B0CD67C8}
AppName=AwsManager
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={localappdata}\Programs\AwsManager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\AwsManager.exe
SetupIconFile=..\amazon_aws_logo_icon_145507.ico
OutputDir=..\artifacts\release
OutputBaseFilename=AwsManager-v{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AwsManager"; Filename: "{app}\AwsManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\AwsManager"; Filename: "{app}\AwsManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AwsManager.exe"; Description: "{cm:LaunchProgram,AwsManager}"; Flags: nowait postinstall skipifsilent unchecked