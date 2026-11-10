"""Local phone-side observer for real Bluetooth pointer verification."""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

PAGE = r'''<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no"><title>Bluetooth pointer test</title>
<style>html,body{margin:0;background:#102035;color:#eff7ff;font:16px system-ui;user-select:none;-webkit-user-select:none}header{position:sticky;top:0;background:#102035;padding:10px;z-index:2;pointer-events:none}#pad{height:38vh;margin:10px;border:2px solid #60e6be;display:grid;grid-template-columns:repeat(3,1fr);grid-template-rows:repeat(3,1fr);touch-action:none}button{border:1px solid #50718c;background:#15314b;color:white;font:24px system-ui}#scroll{height:36vh;overflow:auto;border:2px solid #ffbd66;margin:10px;touch-action:pan-y}#scroll div{padding:22px;border-bottom:1px solid #50718c}pre{font-size:11px;white-space:pre-wrap}b{color:#60e6be}</style>
<header>iPhoneMirror · Bluetooth pointer<br>Clicks <b id="clicks">0</b> · Held <b id="held">0</b><pre id="history">Waiting</pre></header>
<div id="pad">''' + ''.join(f'<button data-cell="{i}">{i}</button>' for i in range(1,10)) + r'''</div>
<div id="scroll">''' + ''.join(f'<div>Scroll row {i}</div>' for i in range(1,41)) + r'''</div>
<script>
let seq=0,pending=[],history=[],clicks=0,held=0;
const pad=document.querySelector('#pad'),scroller=document.querySelector('#scroll');
function record(type,e={}){
 const row={seq:++seq,type,time:performance.now(),x:e.clientX,y:e.clientY,buttons:e.buttons,button:e.button,pointerType:e.pointerType,cell:e.target?.dataset?.cell||null,scrollTop:scroller.scrollTop,width:innerWidth,height:innerHeight,screenWidth:screen.width,screenHeight:screen.height,visualOffsetY:visualViewport?.offsetTop||0,visualHeight:visualViewport?.height||innerHeight,deltaY:e.deltaY};
 if(type==='ready')row.pad=pad.getBoundingClientRect().toJSON(),row.scroll=scroller.getBoundingClientRect().toJSON();
 pending.push(row);history.push(type+' '+(row.cell||'')+' '+Math.round(row.x||0)+','+Math.round(row.y||0)+' held='+held+' scroll='+Math.round(row.scrollTop));
 document.querySelector('#history').textContent=history.slice(-4).join('\n');document.querySelector('#clicks').textContent=clicks;document.querySelector('#held').textContent=held;
}
for(const type of ['pointerdown','pointermove','pointerup','pointercancel','click','wheel'])document.addEventListener(type,e=>{if(type==='pointerdown')held=e.buttons;if(type==='pointerup'||type==='pointercancel')held=0;if(type==='click')clicks++;record(type,e)}, {passive:true});
scroller.addEventListener('scroll',e=>record('scroll',e),{passive:true});
let sending=false;setInterval(async()=>{if(sending||!pending.length)return;sending=true;const batch=pending.splice(0);try{const r=await fetch('/events',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch)});if(!r.ok)throw Error(r.status)}catch(e){pending.unshift(...batch)}finally{sending=false}},100);
record('ready');
</script>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=8765)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--phone-address', help='Accept events only from the selected phone IPv4 address')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    events, lock = [], threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def allowed(self):
            return not args.phone_address or self.client_address[0] in (args.phone_address, '127.0.0.1')

        def do_GET(self):
            if not self.allowed():
                self.send_error(403)
                return
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
            if not self.allowed():
                self.send_error(403)
                return
            if self.path != '/events':
                self.send_error(404)
                return
            length = int(self.headers.get('Content-Length', 0))
            if not 0 < length <= 65536:
                self.send_error(400)
                return
            try:
                batch = json.loads(self.rfile.read(length))
                if not isinstance(batch, list) or not all(isinstance(e, dict) for e in batch):
                    raise ValueError('Expected event array')
                with lock:
                    for event in batch:
                        event['received'] = time.time()
                        events.append(event)
                    # Bound a stalled browser/client without retaining unlimited RAM.
                    del events[:-10000]
                    with (args.output / 'phone-events.jsonl').open('a', encoding='utf-8') as output:
                        for event in batch:
                            output.write(json.dumps(event) + '\n')
                self.send_response(204)
                self.end_headers()
            except (ValueError, KeyError, TypeError):
                self.send_error(400)

    print(f'Listening on port {args.port}', flush=True)
    ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == '__main__':
    main()
