import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.argv[2] || 8788);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.png': 'image/png' };

const server = http.createServer((req, res) => {
  if (req.method === 'POST' && req.url.startsWith('/api/save')) {
    let body = '';
    req.on('data', c => { body += c; if (body.length > 2e6) req.destroy(); });
    req.on('end', () => {
      try {
        const data = JSON.parse(body || '{}');
        const pages = data.pages || {};
        const count = Object.keys(pages).reduce((a, k) => a + Object.keys(pages[k].diffs || {}).length, 0);
        if (!count) { res.writeHead(400); res.end('没有改动'); return; }
        fs.writeFileSync(path.join(here, 'orders.json'), JSON.stringify(data, null, 1), 'utf8');
        fs.appendFileSync(path.join(here, 'orders-history.jsonl'), JSON.stringify(data) + '\n', 'utf8');
        const t = new Date().toLocaleTimeString('zh-CN', { hour12: false });
        console.log('[save] ' + t + ' pages=' + Object.keys(pages).join(',') + ' 位置=' + count);
        res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end(t + ' · ' + count + ' 处');
      } catch (e) {
        res.writeHead(500); res.end('写盘失败: ' + e.message);
      }
    });
    return;
  }
  const urlPath = decodeURIComponent((req.url || '/').split('?')[0]);
  let file = urlPath === '/' ? '/editor.html' : urlPath;
  const full = path.join(here, file);
  if (!full.startsWith(here)) { res.writeHead(403); res.end(); return; }
  fs.readFile(full, (err, buf) => {
    if (err) { res.writeHead(404); res.end('not found'); return; }
    res.writeHead(200, { 'Content-Type': types[path.extname(full)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    res.end(buf);
  });
});

server.listen(port, '0.0.0.0', () => console.log('editor server on http://0.0.0.0:' + port + '  (root ' + here + ')'));