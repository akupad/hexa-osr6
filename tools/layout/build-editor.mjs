import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, build } from './model.mjs';

const root = process.argv[2] || '.';
const outHtml = process.argv[3] || path.join(root, 'tools', 'layout', 'editor.html');
const here = path.dirname(fileURLToPath(import.meta.url));
const viewsDir = path.join(root, 'Views');

const files = fs.readdirSync(viewsDir).filter(f => f.endsWith('.xaml')).sort();
const pages = [];
for (const f of files) {
  const tree = parse(fs.readFileSync(path.join(viewsDir, f), 'utf8'));
  const items = build(tree);
  if (!items.length) continue;
  pages.push({ file: f, page: f.replace(/\.xaml$/, '').replace(/Page$/, ''), items });
}

const model = JSON.stringify({ generated: new Date().toISOString().slice(0, 19).replace('T', ' '), pages });
const css = fs.readFileSync(path.join(here, 'editor.css'), 'utf8');
const js = fs.readFileSync(path.join(here, 'editor.js'), 'utf8');
const html = ['<!DOCTYPE html>', '<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Hexa 界面编辑器</title><style>', css, '</style></head><body>',
  '<header><div class="row1"><h1>Hexa 界面编辑器</h1><span id="meta" class="meta"></span><span class="spacer"></span>',
  '<button id="save" class="primary">保存改动</button><button id="reset">撤销本页改动</button><button id="clear">清空全部改动</button></div>',
  '<div class="row2"><select id="pageSel"></select><label class="chk"><input type="checkbox" id="showRows" checked>显示里面的控件</label><label class="chk"><input type="checkbox" id="compact">紧凑</label><span id="hint" class="hint">拖动卡片左上角 ⠿ 换位置；点标题收起/展开。</span><span id="dirty" class="dirty"></span></div></header>',
  '<main><div id="stage"></div></main>',
  '<div id="toast" class="toast"></div>',
  '<script>window.MODEL=', model, ';</script><script>', js, '</script></body></html>'].join('\n');
fs.writeFileSync(outHtml, html, 'utf8');
console.log('pages=' + pages.length + ' -> ' + outHtml);