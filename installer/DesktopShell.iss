type
  { IShellWindows, from Windows SDK ExDisp.h. Unused methods preserve the
    IDispatch/IShellWindows vtable slots. The explicit signature is necessary:
    dynamic Variant dispatch cannot marshal FindWindowSW's by-reference args. }
  IDesktopShellWindows = interface(IUnknown)
    '{85CB6900-4D95-11CF-960C-0080C7F4EE85}'
    procedure SkipGetTypeInfoCount;
    procedure SkipGetTypeInfo;
    procedure SkipGetIDsOfNames;
    procedure SkipInvoke;
    procedure SkipCount;
    procedure SkipItem;
    procedure SkipNewEnum;
    procedure SkipRegister;
    procedure SkipRegisterPending;
    procedure SkipRevoke;
    procedure SkipOnNavigate;
    procedure SkipOnActivated;
    function FindWindowSW(var Location: Variant; var Root: Variant;
      WindowClass: Integer; out Handle: Integer; Options: Integer;
      out Desktop: IDispatch): HResult;
  end;

function GetInteractiveDesktopShell(): Variant;
var
  ShellWindows: IDesktopShellWindows;
  DesktopDispatch: IDispatch;
  DesktopWindow: Variant;
  DesktopLocation: Variant;
  DesktopRoot: Variant;
  DesktopHandle: Integer;
begin
  ShellWindows := IDesktopShellWindows(CreateComObject(
    StringToGuid('{9BA05972-F6A8-11CF-A442-00A0C90A8F39}')));
  DesktopLocation := 0;
  DesktopRoot := 0;
  DesktopHandle := 0;
  { SWC_DESKTOP=8, SWFO_NEEDDISPATCH=1. Resolve the existing Explorer desktop. }
  OleCheck(ShellWindows.FindWindowSW(DesktopLocation, DesktopRoot,
    8, DesktopHandle, 1, DesktopDispatch));
  if DesktopHandle = 0 then
    RaiseException('The interactive desktop shell could not be resolved.');
  DesktopWindow := DesktopDispatch;
  DesktopWindow := DesktopWindow.Document;
  Result := DesktopWindow.Application;
end;
