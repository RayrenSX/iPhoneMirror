import frida
import json
import psutil
import struct
import subprocess
import sys
import time

path = sys.argv[1]
udid = sys.argv[2]
p = subprocess.Popen([path, "--rate-hz", "120", "--udid", udid], stdin=subprocess.PIPE,
                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
time.sleep(5)
children = [x for x in psutil.process_iter(["pid", "ppid", "name"]) if x.info["ppid"] == p.pid]
target = children[-1].info["pid"] if children else p.pid
session = frida.attach(target)
script = session.create_script(r'''
function emit(api, sock, ptr, len) {
  if (len <= 0 || len > 65536) return;
  try { const b=ptr.readByteArray(len); const h=Array.from(new Uint8Array(b)).map(x=>x.toString(16).padStart(2,'0')).join('');
    if (h.includes('795b3d1f') || h.includes('1f3d5b79')) send({api:api,socket:String(sock),size:len,hex:h});
  } catch (_) {}
}
let f;
try { f=Process.getModuleByName('ws2_32.dll').getExportByName('send'); Interceptor.attach(f,{onEnter(args){let n=parseInt(args[2].toString(),10); emit('send',args[0],args[1],n)}}); } catch(_){ }
try { f=Process.getModuleByName('ws2_32.dll').getExportByName('WSASend'); Interceptor.attach(f,{onEnter(args){
  let count=parseInt(args[2].toString(),10), bufs=args[1];
  for(let i=0;i<count && i<16;i++){let n=Memory.readU32(bufs.add(i*16)); let ptr=Memory.readPointer(bufs.add(i*16+8)); emit('WSASend',args[0],ptr,n);}
}}); } catch(_){ }
''')
script.on("message", lambda m, d: print(json.dumps(m.get("payload", m), ensure_ascii=False), flush=True))
script.load(); print(json.dumps({"attached": target}), flush=True)
time.sleep(2)
for seq in range(1, 31):
    phase = "down" if seq == 1 else ("up" if seq == 30 else "move")
    frame = {"type":"frame","seq":seq,"timestampNs":time.monotonic_ns(),
             "contacts":[{"id":1,"phase":phase,"x":0.5,"y":0.5}]}
    data=json.dumps(frame,separators=(",",":")).encode()
    p.stdin.write(struct.pack("<I",len(data))+data); p.stdin.flush(); time.sleep(0.02)
time.sleep(3)
p.stdin.close(); p.kill(); session.detach()
