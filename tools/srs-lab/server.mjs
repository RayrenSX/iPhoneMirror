import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';

const port = Number.parseInt(process.env.SRS_LAB_PORT ?? '8090', 10);
const root = resolve(import.meta.dirname);
const srsApi = process.env.SRS_LAB_API ?? 'http://127.0.0.1:1985';
const mediaMtxApi = process.env.MEDIAMTX_LAB_API ?? 'http://127.0.0.1:9997';
const types = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'application/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
]);

async function fetchJson(base, path) {
  const response = await fetch(`${base}${path}`, {
    signal: AbortSignal.timeout(1500),
  });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

async function status() {
  try {
    const [version, streams] = await Promise.all([
      fetchJson(srsApi, '/api/v1/versions'),
      fetchJson(srsApi, '/api/v1/streams'),
    ]);
    const items = streams?.streams ?? streams?.data ?? [];
    return {
      ready: true,
      backend: 'srs',
      label: `SRS ${version?.data?.version ?? version?.version ?? 'ready'}`,
      api: srsApi,
      endpoints: {
        rtmp: 'rtmp://127.0.0.1:1935/live/iphone-mirror',
        srt: 'srt://127.0.0.1:10080?streamid=#!::r=live/iphone-mirror,m=publish',
        whip: 'http://127.0.0.1:1985/rtc/v1/whip/?app=live&stream=iphone-mirror',
        whep: 'http://127.0.0.1:1985/rtc/v1/whep/?app=live&stream=iphone-mirror',
        player: 'http://127.0.0.1:8080/players/whep.html',
      },
      streams: items.map(stream => ({
        name: stream.name ?? stream.stream ?? `${stream.app ?? 'live'}/${stream.id ?? 'unknown'}`,
        video: stream.video?.codec ?? stream.video?.profile ?? 'video',
        clients: stream.clients ?? stream.nb_clients ?? 0,
      })),
    };
  } catch (srsError) {
    try {
      const paths = await fetchJson(mediaMtxApi, '/v3/paths/list');
      const items = paths?.items ?? [];
      return {
        ready: true,
        backend: 'mediamtx',
        label: 'MediaMTX ready',
        api: mediaMtxApi,
        endpoints: {
          rtmp: 'rtmp://127.0.0.1:1935/iphone-mirror',
          srt: 'srt://127.0.0.1:10080?streamid=publish:iphone-mirror',
          whip: 'http://127.0.0.1:1985/iphone-mirror/whip',
          whep: 'http://127.0.0.1:1985/iphone-mirror/whep',
          player: 'http://127.0.0.1:1985/iphone-mirror',
        },
        streams: items.filter(path => path.ready).map(path => ({
          name: path.name,
          video: path.tracks?.find(track => /H26|AV1|VP/i.test(track)) ??
            path.tracks?.[0] ?? 'video',
          clients: path.readers?.length ?? 0,
        })),
      };
    } catch (mediaMtxError) {
      return {
        ready: false,
        backend: 'none',
        api: `${srsApi} / ${mediaMtxApi}`,
        error: `SRS: ${srsError.message}; MediaMTX: ${mediaMtxError.message}`,
      };
    }
  }
}

createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', `http://${request.headers.host ?? 'localhost'}`);
  if (url.pathname === '/api/status') {
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify(await status()));
    return;
  }

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' });
    response.end();
    return;
  }

  const relative = normalize(decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname))
    .replace(/^([/\\])+/, '');
  const file = join(root, relative);
  if (!file.startsWith(`${root}${sep}`) || !existsSync(file) || !statSync(file).isFile()) {
    response.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    response.end('Not found');
    return;
  }

  response.writeHead(200, {
    'Content-Type': types.get(extname(file)) ?? 'application/octet-stream',
    'Cache-Control': 'no-store',
  });
  if (request.method === 'HEAD') response.end();
  else createReadStream(file).pipe(response);
}).listen(port, '127.0.0.1', () => {
  console.log(`Stream Lab dashboard: http://127.0.0.1:${port}`);
});
