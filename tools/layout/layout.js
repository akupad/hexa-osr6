(function(){
var L = window.LAYOUT || { pages: [] };
var pages = L.pages;
var meta = document.getElementById('meta');
var nav = document.getElementById('nav');
var content = document.getElementById('content');
var q = document.getElementById('q');
var onlyFolded = document.getElementById('onlyFolded');
var compact = document.getElementById('compact');
var openState = {};
var keySeq = 0;

function isFold(n){return n==='card'||n==='fold'||n==='expander';}
function walk(nodes,fn){nodes.forEach(function(n){fn(n);if(n.children&&n.children.length)walk(n.children,fn);});}
var totalCards=0,totalFolds=0,totalHidden=0;
pages.forEach(function(p){walk(p.sections,function(n){if(n.kind==='card')totalCards++;if(n.kind==='fold'||n.kind==='expander')totalFolds++;if(n.kind==='hidden')totalHidden++;});});
meta.textContent = pages.length+' 个页面 · '+totalCards+' 张卡 · '+totalFolds+' 处折叠 · '+totalHidden+' 处条件隐藏 · 生成于 '+L.generated;

function esc(s){return String(s==null?'':s).replace(/[&<>\"]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[c];});}

function matches(n,kw){
  if(!kw) return true;
  var blob=((n.title||'')+' '+(n.name||'')+' L'+n.line).toLowerCase();
  if(blob.indexOf(kw)>=0) return true;
  var kids=n.children||[];
  for(var i=0;i<kids.length;i++){if(matches(kids[i],kw))return true;}
  return false;
}

function renderNodes(nodes,host,kw,depth,parentTitle){
  nodes.forEach(function(n){
    if(!matches(n,kw)) return;
    if((n.kind==='label'||n.kind==='hidden') && parentTitle && n.title===parentTitle) return;
    var el=document.createElement('div');
    el.className='node '+n.kind+(depth?' d'+Math.min(depth,4):'');
    var key='k'+(keySeq++);
    el.dataset.key=key;
    if(isFold(n.kind)){
      var isOpen = openState.hasOwnProperty(key) ? openState[key] : true;
      var head=document.createElement('div');
      head.className='head';
      var badge = n.kind==='card' ? '卡片' : (n.kind==='expander' ? 'Expander' : '折叠');
      var stateTxt = n.collapsed ? '默认收起' : '默认展开';
      head.innerHTML='<span class="caret'+(isOpen?' open':'')+'">▸</span>'
        +'<span class="ttl">'+esc(n.title)+'</span>'
        +'<span class="tag">'+badge+'</span>'
        +'<span class="tag '+(n.collapsed?'warn':'ok')+'">'+stateTxt+'</span>'
        +(n.name?'<span class="tag name">'+esc(n.name)+'</span>':'')
        +'<span class="tag ln">L'+n.line+'</span>'
        +(n.controls?'<span class="tag">'+n.controls+' 控件</span>':'');
      var body=document.createElement('div');
      body.className='body'+(isOpen?'':' closed');
      renderNodes(n.children||[],body,kw,depth+1,n.title);
      head.onclick=function(){
        var open=!body.classList.contains('closed');
        if(open){body.classList.add('closed');}else{body.classList.remove('closed');}
        openState[key]=!open;
        head.querySelector('.caret').classList.toggle('open',!open);
      };
      el.appendChild(head); el.appendChild(body);
    } else {
      var mark = n.kind==='hidden' ? '<span class="tag hide">条件隐藏</span>' : '';
      el.innerHTML='<div class="leaf"><span class="ttl">'+esc(n.title)+'</span>'+mark+'<span class="tag ln">L'+n.line+'</span></div>';
    }
    host.appendChild(el);
  });
}

function render(){
  keySeq=0;
  var kw=(q.value||'').trim().toLowerCase();
  var only=onlyFolded.checked;
  nav.innerHTML=''; content.innerHTML='';
  var shown=0;
  pages.forEach(function(p,pi){
    if(only){var hasFold=false;walk(p.sections,function(n){if(n.kind==='fold'||n.kind==='expander')hasFold=true;});if(!hasFold)return;}
    if(kw && !p.sections.some(function(s){return matches(s,kw);}) && p.page.toLowerCase().indexOf(kw)<0) return;
    shown++;
    var a=document.createElement('a');
    a.href='#p'+(pi+1);
    a.innerHTML='<span style="flex:1">'+esc(p.page)+'</span><span>'+p.sections.filter(function(s){return s.kind==='card';}).length+'</span>';
    nav.appendChild(a);
    var sec=document.createElement('div'); sec.className='page'; sec.id='p'+(pi+1);
    var h=document.createElement('h2');
    h.innerHTML=esc(p.page)+' <em>'+esc(p.file)+'</em>';
    sec.appendChild(h);
    var box=document.createElement('div'); box.className='tree';
    renderNodes(p.sections,box,kw,0);
    sec.appendChild(box);
    content.appendChild(sec);
  });
  if(!shown) content.innerHTML='<div class="empty">没有匹配的内容</div>';
}

document.getElementById('expandAll').onclick=function(){openState={};render();};
document.getElementById('foldAll').onclick=function(){
  var i=0; openState={};
  var apply=function(nodes){nodes.forEach(function(n){if(isFold(n.kind)){openState['k'+(i)] = !n.collapsed; i++;} if(n.children&&n.children.length)apply(n.children);});};
  pages.forEach(function(p){apply(p.sections);});
  keySeq=0; render();
};
q.addEventListener('input',render);
onlyFolded.addEventListener('change',render);
compact.addEventListener('change',function(){document.body.classList.toggle('compact',compact.checked);});
nav.addEventListener('click',function(e){var a=e.target.closest('a');if(!a)return;[].forEach.call(nav.querySelectorAll('a'),function(x){x.classList.remove('active');});a.classList.add('active');});
render();
})();