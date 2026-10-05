"""Local-only phone-side touch observer for opt-in hardware verification."""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

PAGE = r'''<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no"><title>Five point touch test</title>
<style>html,body{margin:0;height:100%;overflow:hidden;background:#102035;color:white;font:18px system-ui;touch-action:none;user-select:none;-webkit-user-select:none}header{padding:16px;pointer-events:none}b{font-size:64px}#area{position:fixed;inset:0;touch-action:none}i{pointer-events:none;position:absolute;width:48px;height:48px;margin:-24px;border:3px solid #64e9b6;border-radius:50%;text-align:center;line-height:48px}pre{font-size:13px}</style>
<div id="area"></div><header>iPhoneMirror · 5-point hardware test<br>Active <b id="count">0</b> / Peak <span id="peak">0</span><pre id="history">Waiting for touch</pre></header>
<script>
let peak=0,seq=0,lines=[],pending=[];
function record(type,touches,changed=[]){const row={seq:++seq,type,time:performance.now(),touches:[...touches].map(t=>({id:t.identifier,x:t.clientX,y:t.clientY})),changed:[...changed].map(t=>t.identifier)};pending.push(row);count.textContent=touches.length;peak=Math.max(peak,touches.length);document.getElementById('peak').textContent=peak;lines.push(type+' '+touches.length+' ['+row.touches.map(t=>t.id).join(',')+']');document.getElementById('history').textContent=lines.slice(-12).join('\n');area.replaceChildren(...row.touches.map(t=>{let d=document.createElement('i');d.style.left=t.x+'px';d.style.top=t.y+'px';d.textContent=t.id;return d}));}
for(const type of ['touchstart','touchmove','touchend','touchcancel'])document.addEventListener(type,e=>{e.preventDefault();record(type,e.touches,e.changedTouches)}, {passive:false});
let sending=false;setInterval(async()=>{if(sending||!pending.length)return;sending=true;const batch=pending.splice(0);try{let r=await fetch('/events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch)});if(!r.ok)throw Error(r.status)}catch(e){pending.unshift(...batch)}finally{sending=false}},100);
record('ready',[]);
</script>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    events = []
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            if self.path not in ('/', '/events'):
                self.send_error(404)
                return
            with lock:
                body = (json.dumps(events) if self.path == '/events' else PAGE).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json' if self.path == '/events' else 'text/html; charset=utf-8')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            if self.path != '/events':
                self.send_error(404)
                return
            length = int(self.headers.get('Content-Length', 0))
            if not 0 < length <= 65536:
                self.send_error(400)
                return
            try:
                batch = json.loads(self.rfile.read(length))
                if not isinstance(batch, list):
                    raise ValueError('Expected event array')
                with lock:
                    for event in batch:
                        event['received'] = time.time()
                        events.append(event)
                    with (args.output / 'phone-events.jsonl').open('a', encoding='utf-8') as output:
                        for event in batch:
                            output.write(json.dumps(event) + '\n')
                print(f"PHONE {batch[-1]['type']}: active={len(batch[-1]['touches'])}", flush=True)
                self.send_response(204)
                self.end_headers()
            except (ValueError, KeyError, TypeError):
                self.send_error(400)

    print(f'Listening on http://{args.bind}:{args.port}/', flush=True)
    ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == '__main__':
    main()
