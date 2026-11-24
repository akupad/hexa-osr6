import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = process.argv[2] || '.';
const outHtml = process.argv[3] || path.join(root, 'tools', 'layout', 'layout.html');
const here = path.dirname(fileURLToPath(import.meta.url));
const viewsDir = path.join(root, 'Views');

function stripComments(text) { return text.replace(/<!--[\s\S]*?-->/g, m => m.replace(/[^\n]/g, ' ')); }

function parse(text) {
  const clean = stripComments(text);
  const lineStarts = [0];
  for (let i = 0; i < clean.length; i++) if (clean[i] === '\n') lineStarts.push(i + 1);
  const lineOf = off => { let lo = 0, hi = lineStarts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (lineStarts[mid] <= off) lo = mid; else hi = mid - 1; } return lo + 1; };
  const tagRe = /<(\/?)([A-Za-z_][\w:.]*)((?:[^>"']|"[^"]*"|'[^']*')*?)(\/?)>/g;
  const rootNode = { tag: '#root', attrs: {}, children: [], line: 0, parent: null };
  const stack = [rootNode];
  let m;
  while ((m = tagRe.exec(clean))) {
    const close = m[1], tag = m[2], attrText = m[3], selfClose = m[4];
    if (close) { for (let i = stack.length - 1; i > 0; i--) { if (stack[i].tag === tag) { stack.length = i; break; } } continue; }
    const attrs = {};
    const aRe = /([\w:.-]+)\s*=\s*"([^"]*)"/g;
    let a;
    while ((a = aRe.exec(attrText))) attrs[a[1]] = a[2];
    const parent = stack[stack.length - 1];
    const node = { tag, attrs, children: [], line: lineOf(m.index), parent };
    parent.children.push(node);
    if (!selfClose) stack.push(node);
  }
  return rootNode;
}

const nm = n => n.attrs['x:Name'] || n.attrs.Name || '';
const sty = n => n.attrs.Style || '';
const isCard = n => /StaticResource Card/.test(sty(n)) || /Card$/.test(nm(n));
const isCollapsed = n => (n.attrs.Visibility || '') === 'Collapsed';
const isExpander = n => n.tag === 'Expander';
const BODY_NAME = /(Body|SectionBody|FoldBody|ExpanderContent)$/;
const PANEL_NAME = /(Panel|Expander)$/;

function textOf(n) { const t = n.attrs.Text || n.attrs.Content || n.attrs.Header; if (typeof t === 'string') { const v = t.trim(); if (v && v[0] !== '{') return v.replace(/\s+/g, ' '); } return ''; }

function firstTitle(children, depth) {
  depth = depth || 0;
  for (const c of children) {
    const t = textOf(c);
    if (t && t.length <= 60) return { text: t, line: c.line };
    if (depth < 4) { const r = firstTitle(c.children, depth + 1); if (r) return r; }
  }
  return null;
}

function countControls(node) {
  let k = 0;
  const visit = x => { for (const c of x.children) { if (/^(Slider|CheckBox|ComboBox|TextBox|RangeSlider|AxisPreview3D|Image|TabControl|Button)$/.test(c.tag)) k++; visit(c); } };
  visit(node); return k;
}

const CTRL_TAGS = /^(TextBlock|Button|CheckBox|Label|Slider|ComboBox)$/;

function sectionsFor(node, level) {
  const out = [];
  const kids = node.children;
  const consumed = new Set();
  for (let i = 0; i < kids.length; i++) {
    if (consumed.has(i)) continue;
    const c = kids[i];
    const n = nm(c);
    if (isExpander(c)) {
      const t = textOf(c);
      const sec = { kind: 'expander', title: t || (firstTitle(c.children) ? firstTitle(c.children).text : '展折区'), name: n, line: c.line, collapsed: String(c.attrs.IsExpanded || 'False').toLowerCase() !== 'true', controls: countControls(c), children: sectionsFor(c, level + 1) };
      out.push(sec); continue;
    }
    if (isCard(c)) {
      const t = firstTitle(c.children);
      out.push({ kind: 'card', title: t ? t.text : (n || '(无标题卡)'), name: n, line: c.line, collapsed: isCollapsed(c), controls: countControls(c), children: sectionsFor(c, level + 1) });
      continue;
    }
    if (/Btn$/.test(n)) { const stem = n.replace(/Btn$/, ''); let pairIdx = -1; for (let j = i + 1; j < Math.min(kids.length, i + 5); j++) { const bnm = nm(kids[j]); if (BODY_NAME.test(bnm) && (bnm.indexOf(stem) === 0 || stem.indexOf(bnm.replace(/Body$/, '')) === 0)) { pairIdx = j; break; } } if (pairIdx >= 0) { consumed.add(pairIdx); const body = kids[pairIdx]; const btnText = textOf(c) || (firstTitle(c.children) ? firstTitle(c.children).text : ''); out.push({ kind: 'fold', title: btnText || n, name: nm(body), line: c.line, collapsed: isCollapsed(body), controls: countControls(body), children: sectionsFor(body, level + 1) }); continue; } }
    if (BODY_NAME.test(n) || (PANEL_NAME.test(n) && isCollapsed(c) && /Panel$/.test(n))) {
      const sec = { kind: 'fold', title: (firstTitle(c.children) ? firstTitle(c.children).text : n), name: n, line: c.line, collapsed: isCollapsed(c), controls: countControls(c), children: sectionsFor(c, level + 1) };
      out.push(sec); continue;
    }
    if (CTRL_TAGS.test(c.tag)) {
      const t = textOf(c);
      if (t && t.length <= 60) out.push({ kind: isCollapsed(c) ? 'hidden' : 'label', title: t, name: n, line: c.line });
      else if (isCollapsed(c) && n) out.push({ kind: 'hidden', title: n, name: n, line: c.line });
    }
    const sub = sectionsFor(c, level + 1);
    for (const s of sub) out.push(s);
  }
  return out;
}

const files = fs.readdirSync(viewsDir).filter(f => f.endsWith('.xaml')).sort();
const pages = [];
for (const f of files) {
  const tree = parse(fs.readFileSync(path.join(viewsDir, f), 'utf8'));
  const secs = sectionsFor(tree, 0);
  if (!secs.length) continue;
  pages.push({ file: f, page: f.replace(/\.xaml$/, '').replace(/Page$/, ''), sections: secs });
}

function count(nodes) { return nodes.reduce((a, s) => a + (s.kind === 'card' || s.kind === 'fold' || s.kind === 'expander' ? 1 : 0) + count(s.children || []), 0); }
const total = pages.reduce((a, p) => a + count(p.sections), 0);
const data = JSON.stringify({ generated: new Date().toISOString().slice(0, 19).replace('T', ' '), pages });

const css = fs.readFileSync(path.join(here, 'layout.css'), 'utf8');
const js = fs.readFileSync(path.join(here, 'layout.js'), 'utf8');
const html = ['<!DOCTYPE html>', '<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Hexa 排版与折叠</title><style>', css, '</style></head><body>',
  '<header><div class="title"><h1>Hexa 排版与折叠</h1><span id="meta" class="meta"></span></div>',
  '<div class="tools"><input id="q" placeholder="搜页面 / 卡片 / 控件名 / 行号"><button id="expandAll">全部展开</button><button id="foldAll">按默认收起</button><label class="chk"><input type="checkbox" id="onlyFolded">只看有折叠的</label><label class="chk"><input type="checkbox" id="compact">紧凑</label></div></header>',
  '<main><nav id="nav"></nav><section id="content"></section></main>',
  '<footer>数据直接从 Views/*.xaml 解析生成；改完界面后重跑 tools\\layout\\build-layout.mjs 即可刷新。行号可用来指位置。</footer>',
  '<script>window.LAYOUT=', data, ';</script><script>', js, '</script></body></html>'].join('\n');
fs.writeFileSync(outHtml, html, 'utf8');
console.log('pages=' + pages.length + ' blocks=' + total + ' -> ' + outHtml);