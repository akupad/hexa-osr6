(function(){
var M = window.MODEL, pages = M.pages;
var pending = {};
var pageSel = document.getElementById('pageSel');
var stage = document.getElementById('stage');
var meta = document.getElementById('meta');
var dirtyEl = document.getElementById('dirty');
var showRows = document.getElementById('showRows');
var compactEl = document.getElementById('compact');
var toastEl = document.getElementById('toast');
var cur = 0, dragEl = null, dragParent = null;
meta.textContent = pages.length + ' 个页面 · ' + M.generated;

var BASE = {};
function baseOf(list){ return list.filter(function(n){return n.kind!=='row';}).map(function(n){return n.id;}); }
function groupByContainer(items){ var groups=[]; items.forEach(function(n){ var c=n.c||'L0'; if(!groups.length || groups[groups.length-1].c!==c) groups.push({c:c, items:[]}); groups[groups.length-1].items.push(n); }); return groups; }
pages.forEach(function(p,pi){
  groupByContainer(p.items).forEach(function(g){ BASE[pi+':'+g.c]=baseOf(g.items); });
  var walk = function(items){ groupByContainer(items).forEach(function(g){ BASE[pi+':'+g.c]=baseOf(g.items); }); items.forEach(function(n){ if(n.kind!=='row' && n.children && n.children.length){ walk(n.children); } }); };
  walk(p.items);
});

function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c];});}
function toast(msg,err){toastEl.textContent=msg;toastEl.className='toast show'+(err?' err':'');setTimeout(function(){toastEl.className='toast'+(err?' err':'');},2400);}
pages.forEach(function(p,i){var o=document.createElement('option');o.value=i;o.textContent=p.page;pageSel.appendChild(o);});

function rowsHtml(rows){
  var h='';
  (rows||[]).forEach(function(r){
    var t=r.tag, lbl=esc(r.label||'');
    if(t==='CheckBox') h+='<div class="rowline"><span class="box"></span><span>'+lbl+'</span></div>';
    else if(t==='Slider'||t==='RangeSlider') h+='<div class="rowline"><span class="lbl">'+lbl+'</span><span class="track"><i></i><b></b></span></div>';
    else if(t==='Button') h+='<div class="rowline"><span class="pill">'+lbl+'</span></div>';
    else if(t==='ComboBox') h+='<div class="rowline"><span class="lbl">'+lbl+'</span><span class="combo">选择…</span></div>';
    else if(t==='TextBox') h+='<div class="rowline"><span class="lbl">'+lbl+'</span><span class="combo">&nbsp;</span></div>';
    else { var cls=(lbl.length<=18 && lbl.charAt(lbl.length-1)!=='。' && lbl.charAt(lbl.length-1)!=='」')?'head':'text'; h+='<div class="rowline '+cls+'">'+lbl+'</div>'; }
  });
  return h;
}

function blockHtml(n, pi){
  var kind=n.kind;
  var cls='blk '+kind+(n.collapsed?' closed':'');
  var kindTag = kind==='card'?'卡片':(kind==='expander'?'Expander':'折叠');
  var h='<div class="'+cls+'" data-id="'+n.id+'" data-kind="'+kind+'">';
  h+='<div class="bh"><span class="grip">⠿</span><span class="title">'+esc(n.title)+'</span>';
  h+='<span class="ck kind">'+kindTag+'</span>';
  h+='<span class="ck'+(n.collapsed?' warn':'')+'">'+(n.collapsed?'默认收起':'默认展开')+'</span>';
  if(n.name) h+='<span class="ck">'+esc(n.name)+'</span>';
  h+='<span class="ln">L'+n.line+'</span></div>';
  h+='<div class="bb">';
  h+=rowsHtml(n.rows);
  if(n.children && n.children.length){ h+='<div class="blocks" data-parent="'+pi+':'+n.id+'">'; n.children.forEach(function(c){h+=blockHtml(c, pi);}); h+='</div>'; }
  h+='</div></div>';
  return h;
}

function render(){
  var p=pages[cur];
  var h='';
  var groups=groupByContainer(p.items);
  if(groups.length===1){ h+='<div class="blocks" data-parent="'+cur+':'+groups[0].c+'">'; groups[0].items.forEach(function(n){ h+= n.kind==='row' ? rowsHtml([{tag:n.tag,label:n.label}]) : blockHtml(n, cur); }); h+='</div>'; }
  else { groups.forEach(function(g){ h+='<div class="blocks" data-parent="'+cur+':'+g.c+'">'; g.items.forEach(function(n){ h+= n.kind==='row' ? rowsHtml([{tag:n.tag,label:n.label}]) : blockHtml(n, cur); }); h+='</div>'; }); }
  stage.innerHTML=h;
  stage.querySelectorAll('.blk').forEach(function(el){ el.setAttribute('draggable','true'); });
  if(!showRows.checked) stage.querySelectorAll('.rowline').forEach(function(el){ el.style.display='none'; });
  pageSel.value=cur;
  updateDirty();
}

function containerIds(el){ return [].slice.call(el.children).filter(function(c){return c.classList && c.classList.contains('blk');}).map(function(c){return c.dataset.id;}); }
function pageDiffs(){
  var p=pages[cur], diff={};
  stage.querySelectorAll('.blocks').forEach(function(box){
    var key=box.dataset.parent; if(!key) return;
    var ids=containerIds(box), base=BASE[key]||[];
    if(ids.join('|')!==base.join('|')) diff[key]=ids;
  });
  return diff;
}
function snapshot(){ var d=pageDiffs(); pending[pages[cur].page]={file:pages[cur].file, diffs:d}; }
function updateDirty(){
  var n=0; Object.keys(pending).forEach(function(p){ n+=Object.keys(pending[p].diffs).length; });
  var curD=Object.keys(pageDiffs()).length;
  if(curD) n+=1;
  dirtyEl.textContent = n? ('有改动：'+n+' 处') : '';
}

stage.addEventListener('click', function(e){
  if(e.target.closest('.grip')) return;
  var bh=e.target.closest('.bh'); if(!bh) return;
  bh.parentElement.classList.toggle('closed');
});
stage.addEventListener('dragstart', function(e){
  var blk=e.target.closest('.blk'); if(!blk) return;
  dragEl=blk; dragParent=blk.parentElement;
  blk.classList.add('dragging');
  e.dataTransfer.effectAllowed='move';
  try{ e.dataTransfer.setData('text/plain', blk.dataset.id); }catch(err){}
});
stage.addEventListener('dragend', function(e){
  if(dragEl) dragEl.classList.remove('dragging');
  dragEl=null; dragParent=null; updateDirty();
});
stage.addEventListener('dragover', function(e){
  if(!dragEl) return;
  var box=e.target.closest('.blocks');
  if(!box || box!==dragParent) return;
  e.preventDefault();
  var after=[].slice.call(box.children).filter(function(c){return c.classList.contains('blk') && c!==dragEl;});
  var target=null;
  for(var i=0;i<after.length;i++){
    var r=after[i].getBoundingClientRect();
    if(e.clientY < r.top + r.height/2){ target=after[i]; break; }
  }
  if(target) box.insertBefore(dragEl, target); else box.appendChild(dragEl);
  updateDirty();
});
stage.addEventListener('drop', function(e){ if(dragEl) e.preventDefault(); });

pageSel.addEventListener('change', function(){ snapshot(); cur=+pageSel.value; render(); });
showRows.addEventListener('change', render);
compactEl.addEventListener('change', function(){ document.body.classList.toggle('compact', compactEl.checked); });

document.getElementById('reset').onclick=function(){ delete pending[pages[cur].page]; render(); toast('本页已回到原始顺序'); };
document.getElementById('clear').onclick=function(){ pending={}; render(); toast('全部改动已清空'); };

document.getElementById('save').onclick=function(){
  snapshot();
  var out={ts:new Date().toISOString(), pages:{}};
  var any=false;
  Object.keys(pending).forEach(function(pg){ if(Object.keys(pending[pg].diffs).length){ out.pages[pg]=pending[pg]; any=true; } });
  if(!any){ toast('还没有改动', true); return; }
  fetch('/api/save', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(out)})
    .then(function(r){ if(!r.ok) throw new Error('HTTP '+r.status); return r.text(); })
    .then(function(t){ toast('已存到电脑：'+t); })
    .catch(function(err){
      try{ navigator.clipboard.writeText(JSON.stringify(out,null,1)); }catch(e2){}
      toast('保存失败（'+err.message+'），已复制 JSON 到剪贴板', true);
    });
};
render();
})();