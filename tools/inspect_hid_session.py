import importlib.util
import sys
import types

base = sys.argv[1]
sys.path.insert(0, base)
pkg = types.ModuleType("ios_sidecar")
pkg.__path__ = [base + "/ios_sidecar"]
sys.modules["ios_sidecar"] = pkg
sys.modules["ios_sidecar.ipc"] = types.ModuleType("ios_sidecar.ipc")
err = types.ModuleType("ios_sidecar.errors")
for name in ("DeviceBridgeRuntimeException", "SidecarError", "DeviceNotFoundError", "CoreDeviceError"):
    setattr(err, name, type(name, (Exception,), {}))
sys.modules["ios_sidecar.errors"] = err
path = base + "/ios_sidecar/session.cp314-win_amd64.pyd"
spec = importlib.util.spec_from_file_location("ios_sidecar.session", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
for name in ("__aenter__", "send_report", "__aexit__", "_close_touch", "_close_tunnel"):
    fn = getattr(module.HidSession, name)
    code = fn.__code__
    print("===", name, "===")
    print("names", code.co_names)
    print("vars", code.co_varnames)
    print("consts", code.co_consts)
