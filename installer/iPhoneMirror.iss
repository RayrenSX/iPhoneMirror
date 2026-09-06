#ifndef MyAppVersion
  #error MyAppVersion must be provided by scripts/build_installer.ps1
#endif
#ifndef MyNumericVersion
  #error MyNumericVersion must be provided by scripts/build_installer.ps1
#endif
#ifndef MySourceDir
  #define MySourceDir "..\outputs\iPhoneMirror.Installer"
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
VersionInfoProductVersion={#MyNumericVersion}
VersionInfoCompany=RayrenSX
VersionInfoDescription={#MyAppName} Windows x64 Setup
VersionInfoProductName={#MyAppName}
DefaultDirName={#MyDefaultDir}
DisableDirPage=no
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
LZMAUseSeparateProcess=yes
LZMANumFastBytes=273
CloseApplications=yes
CloseApplicationsFilter=iPhoneMirror.exe,iPhoneMirror.Driver.exe
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
ChangesEnvironment=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "chinesetrad"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[LangOptions]
chinesetrad.LanguageName=繁體中文（香港）
chinesetrad.LanguageID=$0C04

[Messages]
chinesetrad.InvalidParameter=命令列收到無效的參數：%n%n%1
chinesetrad.SetupFileMissing=安裝資料夾缺少檔案「%1」。請修正問題或重新下載此軟件。
chinesetrad.SetupFileCorrupt=安裝檔案已損毀。請重新下載此軟件。
chinesetrad.SetupFileCorruptOrWrongVer=安裝檔案已損毀，或與安裝程式的版本不符。請重新下載此軟件。
chinesetrad.AboutSetupMessage=%1 版本 %2%n%3%n%n%1 網站：%n%4
chinesetrad.BrowseDialogLabel=請在下方的資料夾清單中選擇一個資料夾，然後按「確定」。
chinesetrad.WelcomeLabel2=安裝程式將會把 [name/ver] 安裝到您的電腦。%n%n強烈建議您在安裝期間關閉其他應用程式，以免發生衝突。
chinesetrad.CannotInstallToNetworkDrive=安裝程式無法安裝到網絡磁碟機。
chinesetrad.InvalidPath=您必須輸入完整路徑和磁碟機代號。%n%n例如 C:\App 或 UNC 路徑 \\伺服器\共用資料夾。
chinesetrad.DownloadingLabel2=正在下載檔案...
chinesetrad.ExtractingLabel=正在解壓縮檔案...
chinesetrad.ButtonStopExtraction=停止解壓縮 (&S)
chinesetrad.StopExtraction=確定要停止解壓縮嗎？
chinesetrad.ErrorExtractionAborted=解壓縮已中止
chinesetrad.ErrorExtractionFailed=解壓縮失敗：%1
chinesetrad.ArchiveIncorrectPassword=密碼不正確
chinesetrad.ArchiveIsCorrupted=壓縮檔已損毀
chinesetrad.ArchiveUnsupportedFormat=不支援此壓縮檔格式
chinesetrad.RetryCancelSelectAction=選擇操作
chinesetrad.RetryCancelRetry=重試 (&T)
chinesetrad.RetryCancelCancel=取消 (&C)
chinesetrad.StatusDownloadFiles=正在下載檔案...
chinesetrad.SourceVerificationFailed=來源檔案驗證失敗：%1
chinesetrad.VerificationSignatureDoesntExist=簽名檔案「%1」不存在
chinesetrad.VerificationSignatureInvalid=簽名檔案「%1」無效
chinesetrad.VerificationKeyNotFound=簽名檔案「%1」使用了未知的金鑰
chinesetrad.VerificationFileNameIncorrect=檔案名稱不正確
chinesetrad.VerificationFileTagIncorrect=檔案標記不正確
chinesetrad.VerificationFileSizeIncorrect=檔案大小不正確
chinesetrad.VerificationFileHashIncorrect=檔案雜湊值不正確
chinesetrad.ErrorDownloading=下載檔案時發生錯誤：
chinesetrad.ErrorExtracting=解壓縮檔案時發生錯誤：
chinesetrad.ErrorDownloadFailed=下載失敗：%1 %2
chinesetrad.ErrorDownloadSizeFailed=無法取得檔案大小：%1 %2
chinesetrad.ErrorProgress=進度無效：%1（共 %2）
chinesetrad.ErrorFileSize=檔案大小不正確：應為 %1，實際為 %2
chinesetrad.PreviousInstallNotCompleted=先前的安裝或解除安裝尚未完成，必須重新啟動電腦才能完成。%n%n重新啟動後，請再次執行此程式以安裝 [name]。
chinesetrad.ApplicationsFound=下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式。
chinesetrad.ApplicationsFound2=下列應用程式正在使用安裝程式需要更新的檔案。建議允許安裝程式自動關閉這些應用程式；安裝完成後，安裝程式會嘗試重新開啟它們。
chinesetrad.ShowReadmeCheck=是，我要閱讀說明檔案。
chinesetrad.ChangeDiskTitle=安裝程式需要下一張磁碟
chinesetrad.SelectDiskLabel2=請插入磁碟 %1，然後按「確定」。%n%n如果檔案不在下方顯示的資料夾中，請輸入正確的資料夾名稱或按「瀏覽」選取。
chinesetrad.FileNotInDir2=在「%2」找不到檔案「%1」。請插入正確的磁碟或選擇其他資料夾。
chinesetrad.SelectDirectoryLabel=請指定下一張磁碟的位置。
chinesetrad.StatusCreateIcons=正在建立捷徑...
chinesetrad.StatusCreateRegistryEntries=正在更新登錄...
chinesetrad.StatusRegisterFiles=正在註冊檔案...
chinesetrad.ErrorRegOpenKey=無法開啟登錄機碼：%n%1\%2
chinesetrad.ErrorRegCreateKey=無法建立登錄機碼：%n%1\%2
chinesetrad.ErrorRegWriteKey=無法變更登錄機碼：%n%1\%2
chinesetrad.ErrorRegisterServer=無法註冊 DLL/OCX 檔案：%1。
chinesetrad.ErrorRegisterTypeLib=無法註冊類型程式庫：%1。
chinesetrad.ErrorRegSvr32Failed=RegSvr32 失敗；結束代碼 %1
chinesetrad.ErrorIniEntry=在檔案「%1」中建立 INI 項目時發生錯誤。
chinesetrad.ErrorReadingExistingDest=讀取現有目的檔案時發生錯誤：
chinesetrad.ErrorChangingAttr=變更檔案屬性時發生錯誤：
chinesetrad.ErrorCreatingTemp=在目的資料夾建立檔案時發生錯誤：
chinesetrad.ErrorReadingSource=讀取來源檔案時發生錯誤：
chinesetrad.ErrorCopying=複製檔案時發生錯誤：
chinesetrad.ErrorReplacingExistingFile=取代現有檔案時發生錯誤：
chinesetrad.ErrorRestartReplace=重新啟動電腦後取代檔案失敗：
chinesetrad.ErrorRenamingTemp=在目的資料夾重新命名檔案時發生錯誤：
chinesetrad.ErrorOpeningReadme=開啟說明檔案時發生錯誤。

[CustomMessages]
chinesesimp.DeleteUserDataPrompt=是否同时删除 iPhoneMirror 的用户配置和已下载更新？选择“否”将保留这些数据，以便以后重新安装。
chinesetrad.DeleteUserDataPrompt=是否同時刪除 iPhoneMirror 的使用者設定和已下載的更新？選擇「否」會保留這些資料，以便日後重新安裝。
english.DeleteUserDataPrompt=Also delete iPhoneMirror settings and downloaded updates? Choose No to keep this data for a later reinstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Install the bridge's libusb0 copy explicitly. Inno Setup can collapse the
; identical root and _internal payload entries from a recursive wildcard,
; which left upgraded installations without the copy Python can load.
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Excludes: "tools\_internal\libusb0.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MySourceDir}\libusb0.dll"; DestDir: "{app}\tools\_internal"; DestName: "libusb0.dll"; Flags: ignoreversion

; Remove media-output files from older releases before copying the current
; payload. If the current build includes FFmpeg, [Files] restores these files.
[InstallDelete]
; The bridge is a PyInstaller onedir runtime. Delete its previous contents
; before install so an upgrade never combines new code with stale Python DLLs.
Type: filesandordirs; Name: "{app}\tools\_internal"
Type: files; Name: "{app}\tools\ffmpeg\ffmpeg.exe"
Type: files; Name: "{app}\tools\ffmpeg\LICENSE.txt"
Type: files; Name: "{app}\tools\ffmpeg\README.txt"
Type: files; Name: "{app}\tools\ffmpeg\SOURCE.txt"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"
Name: "{group}\更新日志"; Filename: "{app}\CHANGELOG.md"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Languages: chinesesimp
Name: "{group}\更新記錄"; Filename: "{app}\CHANGELOG.md"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Languages: chinesetrad
Name: "{group}\Changelog"; Filename: "{app}\CHANGELOG.md"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Languages: english
Name: "{group}\卸载"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"; Languages: chinesesimp
Name: "{group}\解除安裝"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"; Languages: chinesetrad
Name: "{group}\Uninstall"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"; Languages: english
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"; IconFilename: "{app}\iPhoneMirror.exe"; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppPathName}"; ValueType: string; ValueName: ""; ValueData: "{app}\iPhoneMirror.exe"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppPathName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\AppUserModelId\{#MyAppUserModelId}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#MyAppName}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\AppUserModelId\{#MyAppUserModelId}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\iPhoneMirror.exe"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\iPhoneMirror.exe"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WirelessFirewallRuleName = 'iPhoneMirror Wireless AirPlay';

var
  DeleteUserData: Boolean;

procedure ConfigureWirelessFirewall();
var
  ResultCode: Integer;
  HostPath: String;
  DeleteArguments: String;
  AddTcpArguments: String;
  AddUdpArguments: String;
begin
  if not IsAdminInstallMode then
    Exit;

  HostPath := ExpandConstant('{app}\Wireless\iPhoneMirror.WirelessHost.exe');
  DeleteArguments := 'advfirewall firewall delete rule name="' +
    WirelessFirewallRuleName + '"';
  { AirPlay SETUP negotiates per-session mirror and RAOP media ports. Keep
    the rule constrained to the installed host process and local subnet, but do
    not restrict it to the fixed discovery/control ports. }
  AddTcpArguments := 'advfirewall firewall add rule name="' +
    WirelessFirewallRuleName + '" dir=in action=allow program="' + HostPath +
    '" enable=yes profile=private,public remoteip=localsubnet protocol=TCP ' +
    'localport=any';
  AddUdpArguments := 'advfirewall firewall add rule name="' +
    WirelessFirewallRuleName + '" dir=in action=allow program="' + HostPath +
    '" enable=yes profile=private,public remoteip=localsubnet protocol=UDP ' +
    'localport=any';
  Exec(ExpandConstant('{sys}\netsh.exe'), DeleteArguments, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if not Exec(ExpandConstant('{sys}\netsh.exe'), AddTcpArguments, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    Log('Could not start netsh to add the wireless TCP firewall rule.')
  else if ResultCode <> 0 then
    Log(Format('Could not add the wireless TCP firewall rule: %d.', [ResultCode]));
  if not Exec(ExpandConstant('{sys}\netsh.exe'), AddUdpArguments, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    Log('Could not start netsh to add the wireless SSDP firewall rule.')
  else if ResultCode <> 0 then
    Log(Format('Could not add the wireless SSDP firewall rule: %d.', [ResultCode]));

  { UxPlay is launched by the adapter in a child process. Use a second
    program rule so the optional fallback receives the same local-subnet
    access as the bundled receiver. }
  HostPath := ExpandConstant('{app}\Wireless\UxPlay\uxplay.exe');
  if FileExists(HostPath) then begin
    AddTcpArguments := 'advfirewall firewall add rule name="' +
      WirelessFirewallRuleName + ' UxPlay" dir=in action=allow program="' + HostPath +
      '" enable=yes profile=private,public remoteip=localsubnet protocol=TCP ' +
      'localport=any';
    AddUdpArguments := 'advfirewall firewall add rule name="' +
      WirelessFirewallRuleName + ' UxPlay" dir=in action=allow program="' + HostPath +
      '" enable=yes profile=private,public remoteip=localsubnet protocol=UDP ' +
      'localport=any';
    Exec(ExpandConstant('{sys}\netsh.exe'), AddTcpArguments, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), AddUdpArguments, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    { mDNS discovery uses the link-local multicast address 224.0.0.251,
      which is not covered by remoteip=localsubnet. }
    AddUdpArguments := 'advfirewall firewall add rule name="' +
      WirelessFirewallRuleName + ' UxPlay mDNS" dir=in action=allow program="' + HostPath +
      '" enable=yes profile=private,public protocol=UDP localport=5353';
    Exec(ExpandConstant('{sys}\netsh.exe'), AddUdpArguments, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure EnsureBonjourService();
var
  ResultCode: Integer;
begin
  if not IsAdminInstallMode then
    Exit;

  Exec(ExpandConstant('{sys}\sc.exe'),
    'config "Bonjour Service" start= auto', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'),
    'start "Bonjour Service"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveWirelessFirewall();
var
  ResultCode: Integer;
begin
  if not IsAdminInstallMode then
    Exit;

  if not Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="' + WirelessFirewallRuleName + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not start netsh to remove the wireless AirPlay firewall rule.')
  else if ResultCode <> 0 then
    Log(Format('Could not remove the wireless AirPlay firewall rule: %d.', [ResultCode]));
  Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="' + WirelessFirewallRuleName + ' UxPlay"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="' + WirelessFirewallRuleName + ' UxPlay mDNS"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

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
  if CurUninstallStep = usUninstall then
    RemoveWirelessFirewall();

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(UserDataDirectory(''), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  { A previous per-user installation shadows the all-users shortcut because
    both use the same AppUserModelID. Remove its shortcuts only when the user
    explicitly switches to an administrative all-users installation. }
  if (CurStep = ssInstall) and IsAdminInstallMode then
  begin
    DelTree(ExpandConstant('{userprograms}\{#MyAppName}'), True, True, True);
    DeleteFile(ExpandConstant('{userdesktop}\{#MyAppName}.lnk'));
  end;

  if CurStep = ssPostInstall then
  begin
    ConfigureWirelessFirewall();
    EnsureBonjourService();
  end;

  if (CurStep = ssDone) and
     (ExpandConstant('{param:STARTAPP|0}') = '1') then
  begin
    { Give Restart Manager time to release handles from both application EXEs
      before the updated shared runtime is loaded by a new process. }
    Sleep(1000);
    Exec(ExpandConstant('{app}\iPhoneMirror.exe'), '', ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;
