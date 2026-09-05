import frida
import json
import sys
import time

pid = int(sys.argv[1])
session = frida.attach(pid)
script = session.create_script(r'''
function hook(mod, name, bufferIndex, sizeIndex) {
  let fn;
  try { fn = Process.getModuleByName(mod).getExportByName(name); } catch (_) { return; }
  Interceptor.attach(fn, {
  onEnter(args) {
    this.handle = args[0];
    this.buffer = args[bufferIndex];
    this.size = args[sizeIndex].toInt32();
  },
  onLeave(retval) {
    if (retval.toInt32() === 0 || this.size <= 0 || this.size > 65536) return;
    const n = this.size;
    try {
      const b = Memory.readByteArray(this.buffer, n);
      const h = Array.from(new Uint8Array(b)).map(x => x.toString(16).padStart(2, "0")).join("");
      send({handle: String(this.handle), size: n, hex: h});
    } catch (_) {}
  }
  });
}
hook("kernel32.dll", "WriteFile", 1, 2);
hook("kernel32.dll", "WriteFileEx", 1, 2);
hook("ntdll.dll", "NtWriteFile", 5, 6);
''')

def on_message(message, data):
    if message.get("type") == "send":
        print(json.dumps(message["payload"], ensure_ascii=False), flush=True)
    elif message.get("type") == "error":
        print(json.dumps(message, ensure_ascii=False), flush=True)

script.on("message", on_message)
script.load()
print(json.dumps({"attached": pid}), flush=True)
try:
    while True: time.sleep(1)
except KeyboardInterrupt:
    session.detach()
