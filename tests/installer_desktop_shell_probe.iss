; Optional interactive-desktop regression. Compile with ISCC and run the probe
; as the ordinary user. It exits before setup and never calls ShellExecute.
[Setup]
AppName=Desktop COM read-only probe
AppVersion=1.0
DefaultDirName={tmp}\DesktopCOMProbeUnused
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
OutputDir=..\work\installer-desktop-probe
OutputBaseFilename=desktop-com-probe
Compression=none
SolidCompression=no

[Code]
#include "..\installer\DesktopShell.iss"

function InitializeSetup(): Boolean;
var
  DesktopShell: Variant;
  Message: String;
begin
  Result := False;
  try
    DesktopShell := GetInteractiveDesktopShell();
    Message := 'PASS: existing Explorer desktop automation resolved.';
  except
    Message := 'FAIL: ' + GetExceptionMessage;
  end;
  SaveStringToFile(ExpandConstant('{src}\desktop-probe-result.txt'), Message, False);
end;
