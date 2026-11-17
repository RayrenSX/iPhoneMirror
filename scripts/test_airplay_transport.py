"""Exercise actual static receiver library and encrypted FCUP on an isolated port."""
import os,subprocess,sys,urllib.request,threading,queue,time,re
from pathlib import Path
root=Path(sys.argv[1]).resolve()
smoke=Path(sys.argv[2]).resolve()
rounds=int(sys.argv[3]) if len(sys.argv)>3 else 3
env=dict(os.environ,IPHONE_MIRROR_TEST_PORT='17011',IPHONE_MIRROR_TEST_ROUNDS=str(rounds),IPHONE_MIRROR_TEST_EXPLICIT_STATES='1')
host=subprocess.Popen([str(root/'airplay_transport_host.exe')],cwd=root,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
lines=queue.Queue()
diagnostics=[]
def readerrors():
 for line in host.stderr: diagnostics.append(line.strip())
threading.Thread(target=readerrors,daemon=True).start()
def readlines():
 for line in host.stdout: lines.put(line.strip())
threading.Thread(target=readlines,daemon=True).start()
phone=None
try:
 assert lines.get(timeout=10)=='READY'
 phone=subprocess.Popen([str(smoke)],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
 line=lines.get(timeout=25)
 assert line.startswith('URL ')
 with urllib.request.urlopen(line[4:],timeout=7) as r: master=r.read().decode()
 child=next(l for l in master.splitlines() if l and not l.startswith('#'))
 with urllib.request.urlopen(child,timeout=7) as r: media=r.read().decode(); final=r.url
 assert '#EXT-X-ENDLIST' in media
 for _ in range(rounds-3):
  time.sleep(0.5)
  with urllib.request.urlopen(final,timeout=7) as r: assert '#EXT-X-ENDLIST' in r.read().decode()
 out,err=phone.communicate(timeout=10)
 assert phone.returncode==0,err
 # The native FIFO must correlate actual encrypted EVENT acknowledgements,
 # not merely report that it wrote a stopped request to the socket.
 acks=[tuple(map(int,m.groups())) for line in diagnostics
       if (m:=re.search(r'VIDEO_DIAG 31 (\d+) (-?\d+) (\d+) (\d+) (\d+) (\d+)',line))]
 for state,minimum in ((6,7),(4,3),(5,1)):
  assert sum(status==200 and actual==state and current==1 and encrypted==1
             for status,actual,current,sequence,encrypted,size in acks)>=minimum,(state,acks)
 assert not any(status!=200 for status,state,current,sequence,encrypted,size in acks),acks
 (root/f'transport-{rounds}-diagnostics.log').write_text('\n'.join(diagnostics),encoding='utf-8')
 print(f'PASS actual receiver: distinct natural completion/user stop/failure, three-item encrypted queue handoff, correlated ACKs, playback-info flags, encrypted mixed HTTP/RTSP/EVENT ACK, redirects and {rounds} FCUP requests')
except Exception:
 print('\n'.join(diagnostics[-20:]))
 if phone and phone.poll() is not None:
  print(phone.communicate()[1])
 raise
finally:
 if phone and phone.poll() is None: phone.kill();phone.wait()
 if host.poll() is None:
  try: host.communicate('\n',timeout=8)
  except subprocess.TimeoutExpired: host.kill();host.wait()
