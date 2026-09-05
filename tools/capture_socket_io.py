import frida, json, sys, time

pid = int(sys.argv[1]); session = frida.attach(pid)
script = session.create_script(r'''
function hookSend(mod, name, idx, lenidx) {
 let f; try { f=Process.getModuleByName(mod).getExportByName(name); } catch(_){return;}
 Interceptor.attach(f,{onEnter(args){
  let n=0; try{n=args[lenidx].toInt32()}catch(_){}; if(n<=0||n>65536)return;
  try{let b=Memory.readByteArray(args[idx],n); send({api:name,size:n,hex:Array.from(new Uint8Array(b)).map(x=>x.toString(16).padStart(2,'0')).join('')});}catch(_){ }
 }});
}
hookSend('ws2_32.dll','send',1,2); hookSend('ws2_32.dll','WSASend',2,3);
hookSend('ws2_32.dll','sendto',1,2); hookSend('ws2_32.dll','WSASendTo',2,3);
function hook(mod,name){let f;try{f=Process.getModuleByName(mod).getExportByName(name)}catch(_){return} Interceptor.attach(f,{onEnter(args){send({api:name,a0:String(args[0]),a1:String(args[1]),a2:String(args[2])})}})}
hook('kernel32.dll','ReadFileEx'); hook('kernel32.dll','GetQueuedCompletionStatus');
''')
script.on('message',lambda m,d: print(json.dumps(m.get('payload',m),ensure_ascii=False),flush=True)); script.load(); print(json.dumps({'attached':pid}),flush=True)
try:
 while True: time.sleep(1)
except KeyboardInterrupt: session.detach()
