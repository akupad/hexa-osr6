import fs from 'node:fs';
import path from 'node:path';
import { parse, findNode, blockIds } from './model.mjs';

const root = process.argv[2] || '.';
const ordersPath = process.argv[3] || path.join(root, 'tools', 'layout', 'orders.json');
if (!fs.existsSync(ordersPath)) { console.log('没有 orders.json，先在网页里点「保存改动」'); process.exit(0); }
const data = JSON.parse(fs.readFileSync(ordersPath, 'utf8'));
const pages = data.pages || {};
if (!Object.keys(pages).length) { console.log('orders.json 里没有页面改动'); process.exit(0); }

let applied = 0, skipped = 0;
for (const pageName of Object.keys(pages)) {
  const entry = pages[pageName];
  const fileName = entry.file || (pageName + 'Page.xaml');
  const file = path.join(root, 'Views', fileName);
  if (!fs.existsSync(file)) { console.log('跳过（文件不在）: ' + fileName); skipped++; continue; }
  for (const key of Object.keys(entry.diffs || {})) {
    const cid = key.split(':').slice(1).join(':');
    const wanted = entry.diffs[key];
    const text = fs.readFileSync(file, 'utf8');
    const tree = parse(text);
    const acc = [];
    findNode(tree, cid, acc);
    if (!acc.length) { console.log('跳过（' + fileName + ' 里找不到 ' + cid + '）'); skipped++; continue; }
    let parent = null;
    for (const cand of acc) {
      const ids = blockIds(cand);
      if (ids.length === wanted.length && ids.slice().sort().join(',') === wanted.slice().sort().join(',')) { parent = cand; break; }
    }
    if (!parent) {
      const detail = acc.map(c => c.tag + ':' + blockIds(c).join('/')).join(' | ');
      console.log('跳过（' + fileName + ' ' + cid + ' 块对不上，候选 ' + detail + '，要 ' + wanted.join('/') + '）');
      skipped++; continue;
    }
    const want = wanted;
    const lines = text.split('\n');
    const kids = parent.children.slice().sort((a, b) => a.line - b.line);
    const firstIdx = kids.findIndex(k => 'L' + k.line === want[0]);
    let lastIdx = -1;
    for (let i = kids.length - 1; i >= 0; i--) if (want.indexOf('L' + kids[i].line) >= 0) { lastIdx = i; break; }
    const span = kids.slice(firstIdx, lastIdx + 1);
    const segs = {};
    let pre = [];
    let cursor = span[0].line;
    for (const k of span) {
      const id = 'L' + k.line;
      const gap = lines.slice(cursor - 1, k.line - 1);
      const body = lines.slice(k.line - 1, k.endLine);
      if (want.indexOf(id) >= 0) { segs[id] = { pre: pre.concat(gap), body }; pre = []; }
      else pre = pre.concat(gap, body);
      cursor = k.endLine + 1;
    }
    const pieces = [];
    for (const id of want) { for (const p of segs[id].pre) pieces.push(p); for (const b of segs[id].body) pieces.push(b); }
    const next = lines.slice(0, span[0].line - 1).concat(pieces, lines.slice(span[span.length - 1].endLine)).join('\n');
    if (next === text) continue;
    fs.writeFileSync(file + '.bak', text, 'utf8');
    fs.writeFileSync(file, next, 'utf8');
    console.log('已调整 ' + fileName + '（' + cid + '）：' + want.join(' '));
    applied++;
  }
}
console.log('完成：调整 ' + applied + ' 处，跳过 ' + skipped + ' 处');