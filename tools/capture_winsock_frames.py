import frida, json, sys, time
pid = int(sys.argv[1]); session = frida.attach(pid)
script = session.create_script(r'''
function emit(api, sock, ptr, len) {
  if (len <= 0 || len > 65536) return;
  try { const b=ptr.readByteArray(len); send({api:api,socket:String(sock),size:len,hex:Array.from(new Uint8Array(b)).map(x=>x.toString(16).padStart(2,'0')).join('')}); } catch (_) {}
}
let f;
try { f=Process.getModuleByName('ws2_32.dll').getExportByName('send'); Interceptor.attach(f,{onEnter(args){
  let n = parseInt(args[2].toString(), 10); if (!Number.isFinite(n)) n = 0;
  emit('send', args[0], args[1], n);
}}); } catch(_){ }
// WSASend uses an ABI-specific WSABUF layout; send() is sufficient for the
// control socket and avoids false failures on newer Frida runtimes.
try { f=Process.getModuleByName('ws2_32.dll').getExportByName('WSASend'); Interceptor.attach(f,{onEnter(args){
  const count=parseInt(args[2].toString(),10); const bufs=args[1];
  for(let i=0;i<count && i<16;i++){
    const len=Memory.readU32(bufs.add(i*16)); const ptr=Memory.readPointer(bufs.add(i*16+8));
    emit('WSASend',args[0],ptr,len);
  }
}}); } catch(_){ }
''')
script.on('message',lambda m,d: print(json.dumps(m.get('payload',m),ensure_ascii=False),flush=True)); script.load(); print(json.dumps({'attached':pid}),flush=True)
try:
 while True: time.sleep(1)
except KeyboardInterrupt: session.detach()
