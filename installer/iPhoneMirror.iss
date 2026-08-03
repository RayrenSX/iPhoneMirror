#ifndef MyAppVersion
  #define MyAppVersion "1.5.7"
#endif
#ifndef MyNumericVersion
  #define MyNumericVersion "1.5.7.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\outputs\iPhoneMirror"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\outputs\releases"
#endif
#ifndef MyAppId
  #define MyAppId "{{8A9D1A65-2C8F-4DC8-86CD-8576405E93C0}"
#endif
#ifndef MyAppName
  #define MyAppName "iPhoneMirror"
#endif
#ifndef MyDefaultDir
  #define MyDefaultDir "{autopf}\iPhoneMirror"
#endif
#ifndef MyPrivilegesRequired
  #define MyPrivilegesRequired "admin"
#endif
#ifndef MyAppUserModelId
  #define MyAppUserModelId "RayrenSX.iPhoneMirror"
#endif
#ifndef MyUserDataDir
  #define MyUserDataDir "{localappdata}\iPhoneMirror"
#endif
#ifndef MyAppPathName
  #define MyAppPathName "iPhoneMirror.exe"
#endif
#ifndef MyCompression
  #define MyCompression "lzma2/ultra64"
#endif
#ifndef MySolidCompression
  #define MySolidCompression "yes"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=RayrenSX
AppPublisherURL=https://github.com/RayrenSX/iPhoneMirror
AppSupportURL=https://github.com/RayrenSX/iPhoneMirror/issues
AppUpdatesURL=https://github.com/RayrenSX/iPhoneMirror/releases
VersionInfoVersion={#MyNumericVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany=RayrenSX
VersionInfoDescription={#MyAppName} Windows x64 Setup
VersionInfoProductName={#MyAppName}
DefaultDirName={#MyDefaultDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=no
OutputDir={#MyOutputDir}
OutputBaseFilename=iPhoneMirror-Setup-v{#MyAppVersion}-x64
SetupIconFile=..\src\App\Assets\iPhoneMirror.ico
UninstallDisplayIcon={app}\iPhoneMirror.exe
UninstallDisplayName={#MyAppName} {#MyAppVersion}
LicenseFile=..\LICENSE
PrivilegesRequired={#MyPrivilegesRequired}
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
WizardSizePercent=110
Compression={#MyCompression}
SolidCompression={#MySolidCompression}
CloseApplications=yes
CloseApplicationsFilter=iPhoneMirror.exe
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
ChangesEnvironment=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.DeleteUserDataPrompt=是否同时删除 iPhoneMirror 的用户配置和已下载更新？选择“否”将保留这些数据，以便以后重新安装。
english.DeleteUserDataPrompt=Also delete iPhoneMirror settings and downloaded updates? Choose No to keep this data for a later reinstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"
Name: "{group}\更新日志"; Filename: "{app}\CHANGELOG.md"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Languages: chinesesimp
Name: "{group}\Changelog"; Filename: "{app}\CHANGELOG.md"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Languages: english
Name: "{group}\卸载"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"; Languages: chinesesimp
Name: "{group}\Uninstall"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"; Languages: english
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppPathName}"; ValueType: string; ValueName: ""; ValueData: "{app}\iPhoneMirror.exe"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppPathName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\iPhoneMirror.exe"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteUserData: Boolean;

function UserDataDirectory(Param: String): String;
begin
  Result := ExpandConstant('{#MyUserDataDir}');
end;

function InitializeUninstall(): Boolean;
var
  DeleteParam: String;
  KeepParam: String;
begin
  DeleteParam := ExpandConstant('{param:DELETEUSERDATA|0}');
  KeepParam := ExpandConstant('{param:KEEPUSERDATA|0}');
  if DeleteParam = '1' then
    DeleteUserData := True
  else if KeepParam = '1' then
    DeleteUserData := False
  else
    DeleteUserData := MsgBox(ExpandConstant('{cm:DeleteUserDataPrompt}'),
      mbConfirmation, MB_YESNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(UserDataDirectory(''), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssDone) and
     (ExpandConstant('{param:RESTARTAPP|0}') = '1') then
    Exec(ExpandConstant('{app}\iPhoneMirror.exe'), '', ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
