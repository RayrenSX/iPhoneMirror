"""Observe completed soak captures without controlling the app or phone."""
import datetime as dt
import io
import json
from pathlib import Path
import sys
import time
from PIL import Image

folder=Path(sys.argv[1]).resolve()
images=folder/'observed-frames';images.mkdir(exist_ok=True)
record=images/'observations.jsonl'
seen=set()
if record.exists():
 for line in record.read_text(encoding='utf-8').splitlines():seen.add(json.loads(line)['index'])
started=time.monotonic()
while time.monotonic()-started<14400:
 try:
  lines=(folder/'events.jsonl').read_text(encoding='utf-8-sig').splitlines()
  event=next((json.loads(line) for line in reversed(lines) if '"kind":"keypress"' in line),None)
  if event is not None and event['data']['index'] not in seen:
   sample=event['data'];index=sample['index'];source=folder/'after.png'
   changed=source.stat().st_mtime
   down=dt.datetime.fromisoformat(sample['downAt']).timestamp()
   completed=dt.datetime.fromisoformat(event['time']).timestamp()
   # Capture was closed before the event was appended. If a later capture
   # has replaced it, leave this sample uncovered instead of mislabelling it.
   if down<=changed<=completed+.02:
    data=source.read_bytes()
    if source.stat().st_mtime==changed:
     im=Image.open(io.BytesIO(data)).convert('RGB');im.thumbnail((160,360))
     name=f"{index:04}-{sample['action']}.png"
     im.save(images/name)
     with record.open('a',encoding='utf-8') as output:
      output.write(json.dumps(dict(index=index,action=sample['action'],file=name,
       eventTime=event['time'],captureMtime=changed),ensure_ascii=False)+'\n')
   seen.add(index)
   if index%100==0:print(f"Observed latest sample {index}",flush=True)
  summary=json.loads((folder/'summary.json').read_text(encoding='utf-8-sig'))
  if summary['result']!='running':break
 except (FileNotFoundError,OSError,ValueError):pass
 time.sleep(.1)
print('Observation finished',flush=True)
