/* Production HA adapter. All requests stay below document.baseURI; no browser HA token. */
(()=>{'use strict';
const $=id=>document.getElementById(id),esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let applying=false,demo=false,saving=false,dirty=null,timer,historyDate='',historyRows=[],historyError='',historyLoading=false;
const request=async(path,options={})=>{const r=await fetch(new URL(path,document.baseURI),{credentials:'same-origin',...options});if(!r.ok){const data=await r.json().catch(()=>({}));throw Error(data.error||'HTTP '+r.status)}return r.json()};
const status=document.createElement('p');status.className='settings-note';status.setAttribute('role','status');document.querySelector('header').after(status);
const returnButton=document.createElement('button');returnButton.className='button';returnButton.textContent='Live Home Assistant';document.querySelector('header').append(returnButton);returnButton.onclick=()=>{demo=false;load()};
function midnight(date,zone){const target=Date.parse(date+'T00:00:00Z');let guess=target;for(let i=0;i<4;i++){const p=Object.fromEntries(new Intl.DateTimeFormat('en-GB',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'}).formatToParts(new Date(guess)).map(p=>[p.type,p.value]));const displayed=Date.parse(p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second+'Z');const adjustment=target-displayed;if(!adjustment)break;guess+=adjustment;}return new Date(guess)}
const live=window.EnergyLive={config:null,bmsHistory:{},reloadHistories(){historyDate='';loadBms();window.EnergyHistory.refreshRecorded()},renderMini(){if(!historyRows.length)return '<p class="energy-empty">Map Recorder readings and open daily history to inspect measured energy.</p>';return historyRows.filter(r=>r.intervals.length).slice(0,6).map(r=>`<p>${esc(r.key)}: <strong>${r.totalKwh.toFixed(2)} kWh</strong> · ${Math.round(r.coverageSeconds/60)} minutes covered</p>`).join('')||'<p class="energy-empty">Recorded observations available; no continuous energy intervals yet.</p>'},historyView(date){if(date!==historyDate&&!historyLoading)loadHistory(date);if(historyLoading)return '<p class="energy-empty">Loading Home Assistant Recorder…</p>';if(historyError)return `<p class="energy-empty">${esc(historyError)}</p>`;return recordedHistory();},isLive(){return !demo}};
function recordedHistory(){
 if(!historyRows.length)return '<p class="energy-empty">Map recorded energy counters or power readings to see real history. Recorder must be enabled for those entities.</p>';
 const currency=live.config?.layout.tariff?.currency||'GBP';
 return '<p class="energy-notice">Home Assistant Recorder · UTC timestamps rendered with local offsets. Counters take priority over corresponding power mappings. Outages and resets remain gaps. Categories are independent; totals are not added across overlapping meters.</p>'+historyRows.map(row=>{
 const max=Math.max(.001,...row.points.map(p=>Math.abs(p.value||0))),first=Date.parse(row.start),duration=Date.parse(row.end)-first;
 let lines='',prior=null;for(const p of row.points){const x=40+(Date.parse(p.time)-first)/duration*820,y=130-(p.value||0)/max*95;if(p.value===null||p.quality!=='good'){prior=null;continue;}if(prior&&Date.parse(p.time)-prior.time<=300000)lines+=`<path d="M${prior.x} ${prior.y}L${x} ${y}" stroke="#49efaf" fill="none"/>`;prior={x,y,time:Date.parse(p.time)};lines+=`<circle cx="${x}" cy="${y}" r="2" fill="#49efaf"><title>${esc(new Date(p.time).toString())}: ${p.value} ${esc(row.unit)}</title></circle>`;}
 return `<section class="energy-plot"><h3>${esc(row.key)}</h3><p>${esc(row.source||'ha-recorder')} · ${row.points.length} recorded observations · ${esc(row.unit)}</p><svg viewBox="0 0 900 160" role="img" aria-label="Recorded readings with gaps">${lines}</svg>${row.intervals.length?`<p>${row.totalKwh.toFixed(3)} kWh · ${Math.round(row.coverageSeconds/60)} minutes covered · ${row.intervals.some(i=>i.source==='power-estimate')?'Power estimate':'Counter deltas'}${row.costMinor!==null?` · ${(row.costMinor/100).toFixed(2)} ${esc(currency)} ${row.billingScope==='ev-separate-estimate'?'separate EV estimate':'estimated import cost'} · ${row.pricedKwh.toFixed(3)} kWh priced`:''}</p>`:''}<details><summary>Timestamped recorded readings</summary><table><thead><tr><th>Local time (offset)</th><th>Value</th><th>Quality</th></tr></thead><tbody>${row.points.map(p=>`<tr><td>${esc(new Date(p.time).toString())}</td><td>${p.value===null?'—':p.value.toFixed(3)}</td><td>${esc(p.quality)}</td></tr>`).join('')}</tbody></table></details></section>`;
 }).join('');
}
async function loadHistory(date){
 historyLoading=true;historyDate=date;historyError='';historyRows=[];
 try{
 const zone=live.config?.layout.tariff?.timezone||'Europe/London';const start=midnight(date,zone),next=new Date(date+'T12:00:00Z');next.setUTCDate(next.getUTCDate()+1);const end=midnight(next.toISOString().slice(0,10),zone);
 // Exact instants are retained, including a 23 or 25 hour local day.
 const mappings=live.config?.mappings||{},all=window.HAEnergyBindings.fields(live.config?.layout||{},live.config?.cells||{});
 const pairs={'grid.importEnergy':'grid.import','grid.exportEnergy':'grid.export'};
 for(const l of live.config?.layout.loads||[])pairs['loadEnergy.'+l.id]='loads.'+l.id;
 for(const v of (live.config?.layout.vehicles||[]).slice(0,live.config?.layout.cars||0))pairs['evEnergy.'+v.id]='ev.'+v.id;
 for(const b of live.config?.layout.storage||[])pairs['batteries.'+b.id+'.chargeEnergy']='batteries.'+b.id+'.charge';
 const covered=new Set(Object.entries(pairs).filter(([counter])=>mappings[counter]).map(([,power])=>power));
 if(mappings['grid.importEnergy'])covered.add('grid');const keys=all.filter(f=>mappings[f.key]&&['power','energy'].includes(f.role)&&!covered.has(f.key));
 for(const field of keys){const row=await request('api/energy/history?'+new URLSearchParams({key:field.key,start:start.toISOString(),end:end.toISOString()}));historyRows.push(row)}
 }catch(e){historyError=e.message}finally{historyLoading=false;window.EnergyHistory.refreshRecorded();}
}
async function loadBms(){
 if(!live.config)return;const end=new Date(),start=new Date(end-86400000),result={};
 for(const b of live.config.layout.storage||[]){const rows=new Map();const fields=window.HAEnergyBindings.fields(live.config.layout,live.config.cells).filter(f=>live.config.mappings[f.key]&&(f.key.startsWith('batteries.'+b.id+'.')||f.key.startsWith('cells.'+b.id+'.')));
 for(const field of fields){try{const data=await request('api/energy/history?'+new URLSearchParams({key:field.key,start:start.toISOString(),end:end.toISOString()}));for(const p of data.points){let row=rows.get(p.time);if(!row){row={time:p.time,cellVoltages:{}};rows.set(p.time,row)}if(field.key.startsWith('cells.')&&field.key.endsWith('.voltage'))row.cellVoltages[field.key.split('.')[2]]=p.value;else if(['soc','power','voltage','current'].includes(field.key.split('.').at(-1)))row[field.key.split('.').at(-1)]=p.value}}catch{}}
 result[b.id]=[...rows.values()].sort((a,b)=>Date.parse(a.time)-Date.parse(b.time));}live.bmsHistory=result;
}
async function load(){try{
 applying=true;live.config=await request('api/energy/config');const layout=Object.keys(live.config.layout).length?live.config.layout:window.LMSEnergy.getConfig();
 if(!Object.keys(live.config.layout).length){layout.tariff.confirmed=false;live.config=await request('api/energy/config',{method:'PUT',headers:{'Content-Type':'application/json','X-Energy-Studio':'1'},body:JSON.stringify({layout,cells:{},mappings:{},expectedRevision:live.config.revision})})}
 window.LMSEnergy.applyServerConfig(live.config.layout);historyDate='';loadBms();
 }catch(e){status.textContent='Configuration: '+e.message}finally{applying=false}}
async function save(){
 if(saving||!dirty||!live.config)return;saving=true;const layout=dirty;dirty=null;const fields=new Set(window.HAEnergyBindings.fields(layout,live.config.cells).map(f=>f.key));live.config.mappings=Object.fromEntries(Object.entries(live.config.mappings).filter(([key])=>fields.has(key)));live.config.cells=Object.fromEntries(Object.entries(live.config.cells).filter(([id])=>layout.storage.some(b=>b.id===id)));
 try{live.config=await request('api/energy/config',{method:'PUT',headers:{'Content-Type':'application/json','X-Energy-Studio':'1'},body:JSON.stringify({layout,cells:live.config.cells,mappings:live.config.mappings,expectedRevision:live.config.revision})});status.textContent='Layout saved';historyDate='';}
 catch(e){dirty=null;status.textContent='Layout was not saved: '+e.message+' · Reload Live Home Assistant before editing.';}
 finally{saving=false;if(dirty)save()}
}
window.addEventListener('energy-layout-change',e=>{if(applying||demo)return;dirty=e.detail;clearTimeout(timer);timer=setTimeout(save,600)});
window.addEventListener('energy-demo',()=>{demo=true;status.textContent='DEMO · Synthetic telemetry and histories';document.querySelectorAll('.live-label').forEach(e=>e.textContent='DEMO')});
// Keep live and demo explicit; SSE never resumes simulation on disconnect.
const stream=new EventSource(new URL('api/energy/live',document.baseURI));
stream.onmessage=e=>{if(demo)return;const snapshot=JSON.parse(e.data);if(live.config&&snapshot.revision!==live.config.revision&&!saving&&!dirty)load();window.LMSEnergy.updateSnapshot(snapshot);status.textContent='Home Assistant '+snapshot.connection+' · observed '+new Date(snapshot.observedAt).toLocaleTimeString();document.querySelectorAll('.live-label').forEach(e=>e.textContent='LIVE');};
stream.onerror=()=>{if(!demo){status.textContent='Home Assistant transport disconnected · reconnecting';window.LMSEnergy.updateSnapshot({connection:'disconnected',readings:{},residual:{value:null,quality:'disconnected'}})}};
document.querySelectorAll('[class*="demo"],.live-pill').forEach(e=>{if(e.textContent.trim()==='DEMO'){e.classList.add('live-label');e.textContent='LIVE'}});
// Detailed per-field qualities remain reviewable without claiming that an unmapped reading is zero.
const quality=document.createElement('details');quality.className='settings-note';quality.innerHTML='<summary>Mapping availability and freshness</summary><div></div>';status.after(quality);
stream.addEventListener('message',e=>{if(demo)return;const s=JSON.parse(e.data);quality.lastElementChild.innerHTML=Object.entries(s.readings).map(([key,m])=>`<div>${esc(key)}: ${m.value===null?'—':m.value.toFixed(3)+' '+esc(m.unit)} · ${esc(m.quality)}${m.measuredAt?' · '+esc(new Date(m.measuredAt).toLocaleString()):''}</div>`).join('')});
window.addEventListener('pagehide',()=>stream.close());load();
})();
