import { qs } from '../core/dom.js';
import { sendCurrent, readBase, readModel, ping } from '../net/ollama.js';
import { showToast, userMessageForError } from './errors.js';
import { t } from '../i18n/i18n.js';

export function wireSendAction(){
  const ta = qs('#composer-input'); const btn = qs('#send-btn');
  if(ta){ ta.addEventListener('keydown', (e)=>{ if(e.key==='Enter' && (e.ctrlKey || e.metaKey)){ e.preventDefault(); sendCurrent(); } }); }
  if(btn){ btn.addEventListener('click', (e)=>{ e.preventDefault(); sendCurrent(); }); }
}

export function mountStatusPill(){
  const label = document.querySelector('#model-label');
  if(!label) return;
  const pill = document.createElement('span'); pill.className='status-pill';
  let statusKey = 'status.notTested';
  const setPill = (ok, key)=>{ statusKey = key; pill.textContent=''; const dot=document.createElement('span'); dot.textContent='\u25CF'; dot.className = ok ? 'status-ok' : 'status-bad'; const text=document.createElement('span'); text.textContent = t(key); pill.append(dot,text); };
  const refreshTitle = ()=>{ const base = readBase(); const model = readModel(); pill.title = t('model.statusTitle', { base, model }); };
  const holder = document.createElement('span');
holder.style.display = 'inline-flex';
holder.style.alignItems = 'center';
holder.style.gap = '6px';
holder.style.whiteSpace = 'nowrap';
label.parentNode.insertBefore(holder, label);
holder.append(label, pill);
  setPill(false,'status.notTested'); refreshTitle();
  pill.addEventListener('click', async ()=>{
    const base = prompt(t('model.basePrompt'), readBase()); if(base!=null) localStorage.setItem('ollamaBase', base);
    const model = prompt(t('model.prompt'), readModel()); if(model!=null) localStorage.setItem('ollamaModel', model);
    refreshTitle();
    try{ await ping(readBase()); setPill(true,'common.ok'); }catch(e){ setPill(false,'common.failure'); showToast(userMessageForError(e, t('model.pingError'))); }
  });
  ping(readBase()).then(()=>setPill(true,'common.ok')).catch(()=>setPill(false,'common.failure'));
  document.addEventListener('i18n:language-changed', () => {
    const ok = statusKey === 'common.ok';
    setPill(ok, statusKey);
    refreshTitle();
  });
}
