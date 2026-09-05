import frida, json, sys, time

pid = int(sys.argv[1])
session = frida.attach(pid)
script = session.create_script(r'''
function hook(mod, name, fmt, indexes) {
  let fn; try { fn = Process.getModuleByName(mod).getExportByName(name); } catch (_) { return; }
  Interceptor.attach(fn, { onEnter(args) {
    let out = {api: name};
    indexes.forEach(i => { try { out["a"+i] = args[i].readUtf16String(); } catch (_) { try { out["a"+i] = String(args[i]); } catch (_) {} } });
    send(out);
  }});
}
hook("kernel32.dll", "CreateFileW", "", [0]);
hook("kernel32.dll", "CreateNamedPipeW", "", [0]);
hook("kernel32.dll", "ConnectNamedPipe", "", [0]);
hook("kernel32.dll", "CreateFileMappingW", "", [1]);
hook("kernel32.dll", "OpenFileMappingW", "", [2]);
hook("kernel32.dll", "MapViewOfFile", "", []);
''')
script.on("message", lambda m, d: print(json.dumps(m.get("payload",m), ensure_ascii=False), flush=True))
script.load(); print(json.dumps({"attached":pid}), flush=True)
try:
  while True: time.sleep(1)
except KeyboardInterrupt: session.detach()
