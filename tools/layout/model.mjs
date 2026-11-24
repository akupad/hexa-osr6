export function stripComments(text) { return text.replace(/<!--[\s\S]*?-->/g, m => m.replace(/[^\n]/g, ' ')); }

export function parse(text) {
  const clean = stripComments(text);
  const starts = [0];
  for (let i = 0; i < clean.length; i++) if (clean[i] === '\n') starts.push(i + 1);
  const lineOf = off => { let lo = 0, hi = starts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (starts[mid] <= off) lo = mid; else hi = mid - 1; } return lo + 1; };
  const re = /<(\/?)([A-Za-z_][\w:.]*)((?:[^>"']|"[^"]*"|'[^']*')*?)(\/?)>/g;
  const rootNode = { tag: '#root', attrs: {}, children: [], line: 0, endLine: 0 };
  const stack = [rootNode];
  let m;
  while ((m = re.exec(clean))) {
    const close = m[1], tag = m[2], attrText = m[3], selfClose = m[4];
    if (close) { for (let i = stack.length - 1; i > 0; i--) { if (stack[i].tag === tag) { stack[i].endLine = lineOf(m.index); stack.length = i; break; } } continue; }
    const attrs = {};
    const are = /([\w:.-]+)\s*=\s*"([^"]*)"/g;
    let a;
    while ((a = are.exec(attrText))) attrs[a[1]] = a[2];
    const node = { tag, attrs, children: [], line: lineOf(m.index), endLine: lineOf(m.index) };
    stack[stack.length - 1].children.push(node);
    if (!selfClose) stack.push(node);
  }
  return rootNode;
}

export const nm = n => n.attrs['x:Name'] || n.attrs.Name || '';
export const sty = n => n.attrs.Style || '';
export const isCard = n => /StaticResource Card/.test(sty(n));
export const isCollapsed = n => (n.attrs.Visibility || '') === 'Collapsed';
export const BODY = /(Body|SectionBody|FoldBody|ExpanderContent)$/;
export const CTRL = /^(TextBlock|Button|CheckBox|ComboBox|Slider|TextBox|Label|RangeSlider)$/;

export function textOf(n) { const t = n.attrs.Text || n.attrs.Content || n.attrs.Header; if (typeof t === 'string') { const v = t.trim(); if (v && v[0] !== '{') return v.replace(/\s+/g, ' '); } return ''; }
export function firstText(children, d) { d = d || 0; for (const c of children) { const t = textOf(c); if (t && t.length <= 90) return t; if (d < 3) { const r = firstText(c.children, d + 1); if (r) return r; } } return ''; }

function rowsOf(node) {
  const out = [];
  const walk = n => { for (const c of n.children) { if (isCard(c) || c.tag === 'Expander' || BODY.test(nm(c))) continue; if (/Btn$/.test(nm(c))) continue; if (CTRL.test(c.tag)) { const t = textOf(c); if (t) out.push({ tag: c.tag, label: t, name: nm(c), line: c.line }); else if (nm(c)) out.push({ tag: c.tag, label: '', name: nm(c), line: c.line }); } walk(c); } };
  walk(node);
  return out;
}

export function build(node) {
  const items = [];
  const kids = node.children;
  const used = new Set();
  const cid = 'L' + node.line;
  for (let i = 0; i < kids.length; i++) {
    if (used.has(i)) continue;
    const c = kids[i];
    const n = nm(c);
    if (c.tag === 'Expander') {
      items.push({ c: cid, kind: 'expander', id: 'L' + c.line, title: textOf(c) || firstText(c.children), name: n, line: c.line, endLine: c.endLine, collapsed: String(c.attrs.IsExpanded || 'False').toLowerCase() !== 'true', children: build(c) });
      continue;
    }
    if (isCard(c)) {
      items.push({ c: cid, kind: 'card', id: 'L' + c.line, title: firstText(c.children) || n || '卡片', name: n, line: c.line, endLine: c.endLine, collapsed: false, rows: rowsOf(c), children: build(c) });
      continue;
    }
    if (/Btn$/.test(n)) {
      const stem = n.replace(/Btn$/, '');
      let pair = -1;
      for (let j = i + 1; j < Math.min(kids.length, i + 5); j++) {
        const bn = nm(kids[j]);
        if (BODY.test(bn) && (bn.indexOf(stem) === 0 || stem.indexOf(bn.replace(/Body$/, '')) === 0)) { pair = j; break; }
      }
      if (pair >= 0) {
        used.add(pair);
        const body = kids[pair];
        items.push({ c: cid, kind: 'fold', id: 'L' + c.line, title: textOf(c) || firstText(c.children) || n, name: nm(body), line: c.line, endLine: body.endLine, collapsed: isCollapsed(body), rows: rowsOf(body), children: build(body) });
        continue;
      }
    }
    if (BODY.test(n) && isCollapsed(c)) {
      items.push({ c: cid, kind: 'group', id: 'L' + c.line, title: firstText(c.children) || n, name: n, line: c.line, endLine: c.endLine, collapsed: true, rows: rowsOf(c), children: build(c) });
      continue;
    }
    if (CTRL.test(c.tag)) {
      const t = textOf(c);
      if (t || n) items.push({ c: cid, kind: 'row', id: 'L' + c.line, tag: c.tag, label: t, name: n, line: c.line });
      continue;
    }
    for (const s of build(c)) items.push(s);
  }
  return items.sort((a, b) => a.line - b.line);
}

export function blockIds(node) { return build(node).filter(i => i.kind !== 'row' && i.c === 'L' + node.line).map(i => i.id); }

export function findNode(node, id, acc) { if ('L' + node.line === id) { acc.push(node); return; } for (const c of node.children) findNode(c, id, acc); }