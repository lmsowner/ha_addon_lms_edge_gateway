/* Edit mode opens one flyout for the dashboard part you clicked. */
(()=>{'use strict';
const $=id=>document.getElementById(id);
const b=window.HAEnergyBindings;
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function fillDevices(list){const groups=new Map();for(const d of b.devices(list)){const area=d.area||'No area';if(!groups.has(area))groups.set(area,[]);groups.get(area).push(d);}$('ha-device').innerHTML='<option value="">All devices</option>'+[...groups].map(([area,devices])=>`<optgroup label="${esc(area)}">${devices.map(d=>`<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('')}</optgroup>`).join('');}
let entities=[],state,pending={},demo=false,partId='',reading='',series=null,search='',device='',hideUnavailable=true,preferLocal=false;
try{hideUnavailable=sessionStorage.getItem('lms-energy-hide-unavailable')!=='0';}catch{}
const request=async(path,options={})=>{const r=await fetch(new URL(path,document.baseURI),{credentials:'same-origin',...options});if(!r.ok)throw Error(r.status===409?'Mappings changed elsewhere. Reload before saving.':'Energy backend returned HTTP '+r.status);return r.status===204?null:r.json()};
const layout=()=>window.LMSEnergy.getConfig();
const cellsOf=pack=>(state?.cells?.[pack])||[];
function spec(id){
 const c=layout(),all=b.fields(c,state?.cells||{}),take=(...keys)=>all.filter(f=>keys.some(k=>f.key===k||f.key.startsWith(k)));
 const link=(next,label)=>({id:next,label});
 if(id==='home')return {title:'Home consumption',blurb:'Map the meter for the whole house. Each circuit below the flow has its own edit icon.',fields:take('home'),accounting:true};
 if(id==='grid')return {title:'Grid',blurb:'Use the net sensor, or the import and export sensors. Do not map both.',fields:take('grid','grid.import','grid.export','grid.importEnergy','grid.exportEnergy')};
 if(id==='solar')return {title:'Solar total',blurb:'Optional combined solar sensor. Each array on the diagram has its own mappings.',fields:take('solar'),links:(c.arrayRoutes||[]).map(a=>link('array:'+a.id,a.name))};
 if(id==='battery')return {title:'Battery reserve',blurb:'Optional combined power across every battery and battery array. Open each one for its own sensors.',fields:take('battery'),links:(c.storage||[]).map(a=>link('storage:'+a.id,a.name))};
 if(id==='tariff')return {title:'Tariff',blurb:'The current import price used by this dashboard.',fields:take('tariff.currentPrice')};
 if(id.startsWith('array:')){const a=(c.arrayRoutes||[]).find(v=>v.id===id.slice(6));if(!a)return null;return {title:a.name,blurb:'Sensors that belong to this array only.',fields:take('arrays.'+a.id,'arrayEnergy.'+a.id)};}
 if(id.startsWith('inverter:')){const inv=(c.inverters||[]).find(v=>v.id===id.slice(9));if(!inv)return null;const links=[...(c.arrayRoutes||[]).filter(a=>a.inverter===inv.id).map(a=>link('array:'+a.id,a.name)),...(c.storage||[]).filter(a=>a.connection==='inverter'&&a.inverter===inv.id).map(a=>link('storage:'+a.id,a.name)),...(c.sources||[]).filter(a=>a.connection==='inverter'&&a.inverter===inv.id).map(a=>link('source:'+a.id,a.name))];return {title:inv.name,blurb:'The inverter’s own AC output. Arrays, batteries and generators attached to it are mapped on their own.',fields:take('inverters.'+inv.id),links};}
 if(id.startsWith('storage:')){const pack=(c.storage||[]).find(v=>v.id===id.slice(8));if(!pack)return null;const prefix='batteries.'+pack.id+'.';const blurb=pack.array?`One entry for all ${pack.units} batteries. The number on the left is the total capacity. Map current capacity, or the capacity sensor you already chose. SoC is that reading divided by the total. Power is one positive or negative number in W or kW.`:'Pack sensors for this battery. The number on the left is the total capacity. Map current capacity for the energy still stored. SoC is that reading divided by the total. Cell voltages and resistances can be mapped as a numbered run.';return {title:pack.name,blurb,fields:all.filter(f=>f.key.startsWith(prefix)),pack};}
 if(id.startsWith('source:')){const src=(c.sources||[]).find(v=>v.id===id.slice(7));if(!src)return null;return {title:src.name,blurb:'Generation sensor for this input.',fields:take('sources.'+src.id)};}
 if(id.startsWith('load:')){const load=(c.loads||[]).find(v=>v.id===id.slice(5));if(!load)return null;return {title:load.name,blurb:'Power and energy for this circuit only.',fields:take('loads.'+load.id,'loadEnergy.'+load.id)};}
 if(id.startsWith('charger:')){const index=Number(id.slice(8));return {title:(c.chargerNames||[])[index]||'Charger',blurb:'Power for this charger.',fields:take('chargers.charger-'+(index+1))};}
 if(id.startsWith('vehicle:')){const v=(c.vehicles||[]).find(x=>x.id===id.slice(8));if(!v)return null;return {title:v.name,blurb:'Power, state of charge and charge energy for this vehicle.',fields:take('ev.'+v.id,'evSoc.'+v.id,'evEnergy.'+v.id)};}
 return null;
}
function currentItem(c){
 if(partId.startsWith('array:'))return (c.arrayRoutes||[]).find(v=>v.id===partId.slice(6));
 if(partId.startsWith('inverter:'))return (c.inverters||[]).find(v=>v.id===partId.slice(9));
 if(partId.startsWith('storage:'))return (c.storage||[]).find(v=>v.id===partId.slice(8));
 if(partId.startsWith('source:'))return (c.sources||[]).find(v=>v.id===partId.slice(7));
 return null;
}
function optionList(entries,current){return entries.map(([value,label])=>`<option value="${esc(value)}" ${String(value)===String(current)?'selected':''}>${esc(label)}</option>`).join('');}
function componentForm(){
 const c=layout(),item=currentItem(c);if(!item)return '';
 const inverters=(c.inverters||[]).map(v=>[v.id,v.name]);
 const invSelect=`<label>Connected inverter<select data-comp="inverter"><option value="">No inverter</option>${optionList(inverters,item.inverter)}</select></label>`;
 if(partId.startsWith('array:'))return `<div class="ha-component"><h3>Solar array</h3><label>Name<input data-comp="name" maxlength="30" value="${esc(item.name)}"></label><label>Number of panels<input data-comp="panels" type="number" min="0" max="120" step="1" value="${item.panels}"></label><label>Each panel (W)<input data-comp="rating" type="number" min="100" max="1000" step="10" value="${item.rating}"></label>${invSelect}<button type="button" class="button" data-comp-add="inverter" ${(c.inverters||[]).length>=4?'disabled':''}>Add an inverter for this array</button><button type="button" class="button ha-remove" data-comp-remove="array">Remove this array</button></div>`;
 if(partId.startsWith('inverter:')){const solar=(c.arrayRoutes||[]).filter(a=>a.inverter===item.id).map(a=>a.name),batteries=(c.storage||[]).filter(b=>b.connection==='inverter'&&b.inverter===item.id).map(b=>b.name);return `<div class="ha-component"><h3>Inverter</h3><label>Name<input data-comp="name" maxlength="30" value="${esc(item.name)}"></label><label>AC rating (kW)<input data-comp="limit" type="number" min="0.1" max="100" step="0.1" value="${item.limit}"></label><p class="ha-linked">Solar: ${esc(solar.join(', ')||'none linked yet')}</p><button type="button" class="button" data-comp-add="array" ${(c.arrays||0)>=4?'disabled':''}>Add a solar array on this inverter</button><p class="ha-linked">Batteries: ${esc(batteries.join(', ')||'none linked yet')}</p><button type="button" class="button" data-comp-add="battery" ${(c.batteries||0)>=6?'disabled':''}>Add a battery on this inverter</button><button type="button" class="button ha-remove" data-comp-remove="inverter">Remove this inverter</button></div>`;}
 if(partId.startsWith('storage:')){const place=item.connection==='inverter'?'Charges and supplies through the selected inverter.':item.connection==='plugin'?'Plug-in battery on the home AC bus.':'Connects on the home AC bus.';return `<div class="ha-component"><h3>${item.array?'Battery array':'Battery'}</h3><label>Name<input data-comp="name" maxlength="30" value="${esc(item.name)}"></label><label class="ha-check"><input data-comp="array" type="checkbox" ${item.array?'checked':''}> This is an array of batteries with one combined reading</label>${item.array?`<label>Number of batteries<input data-comp="units" type="number" min="2" max="6" step="1" value="${item.units}"></label><p class="ha-linked">One state of charge and one signed power reading for all ${item.units} batteries. Positive power is discharge.</p>`:''}<label>Connection<select data-comp="connection">${optionList([['ac','Home AC bus'],['inverter','This inverter'],['plugin','Plug-in battery']],item.connection)}</select></label>${item.connection==='inverter'?invSelect:''}<label>${item.array?'Total capacity, each battery (kWh)':'Total capacity (kWh)'}<input data-comp="capacity" type="number" min="0.1" max="200" step="0.1" value="${item.capacity}"></label><p class="ha-linked">${place}</p><button type="button" class="button ha-remove" data-comp-remove="battery">Remove this battery</button></div>`;}
 if(partId.startsWith('source:'))return `<div class="ha-component"><h3>${esc(window.EnergyTopology.sourceTypes[item.type]||'Source')}</h3><label>Name<input data-comp="name" maxlength="30" value="${esc(item.name)}"></label><label>Connection<select data-comp="connection">${optionList([['ac','Home AC bus'],['inverter','Selected inverter']],item.connection)}</select></label>${item.connection==='inverter'?invSelect:''}<label>Demo generation (kW)<input data-comp="power" type="number" min="0" max="100" step="0.05" value="${item.power}"></label><button type="button" class="button ha-remove" data-comp-remove="source">Remove this source</button></div>`;
 return '';
}
function writeComponent(target){
 const field=target?.dataset?.comp;if(!field||!window.LMSEnergy.editComponent)return;
 preferLocal=true;
 window.LMSEnergy.editComponent(c=>{const item=currentItem(c);if(!item)return;item[field]=target.type==='checkbox'?target.checked:target.type==='number'?Number(target.value)||0:target.value;if(field==='array'){item.array=target.checked;if(item.array&&item.units<2)item.units=2;c.batteryArray=c.storage.some(v=>v.array);}if(field==='units'){item.array=true;item.units=Math.max(2,Number(target.value)||2);c.batteryArray=true;}});
 if(field==='array'||field==='connection')renderPart();
}
function addAndOpen(kind,linkId){
 let id=null;preferLocal=true;
 window.LMSEnergy.editComponent(c=>{const t=window.EnergyTopology;if(kind==='array')id=t.addArray(c,linkId);else if(kind==='inverter')id=t.addInverter(c,linkId);else if(kind==='battery')id=t.addBattery(c,linkId);else id=t.addSource(c,kind,linkId);});
 refreshAdd();if(!id)return;
 const prefix=kind==='array'?'array:':kind==='inverter'?'inverter:':kind==='battery'?'storage:':'source:';
 openPart(prefix+id);
}
function refreshAdd(){
 const bar=$('ha-add'),c=window.LMSEnergy?.getConfig?.();if(!bar||!c)return;
 const lock={array:(c.arrays||0)>=4,inverter:(c.inverters||[]).length>=4,battery:(c.batteries||0)>=6,wind:(c.sources||[]).length>=8};
 bar.querySelectorAll('[data-add]').forEach(button=>{const kind=button.dataset.add;button.disabled=kind==='wind'||kind==='hydro'||kind==='generator'||kind==='plugsolar'?lock.wind:!!lock[kind];});
}
function mappedLabel(key){const id=pending[key]?.entityId;if(!id)return 'Not mapped';return entities.find(e=>e.entity_id===id)?.attributes?.friendly_name||id;}
function renderPart(){
 $('ha-component').innerHTML=componentForm();
 const info=spec(partId);if(!info){$('ha-message').textContent='That part is no longer on the dashboard.';return;}
 $('ha-flyout-title').textContent=info.title;$('ha-blurb').textContent=info.blurb;
 if(!reading||!info.fields.some(f=>f.key===reading))reading=info.fields[0]?.key||'';
 const conflict=pending.grid&&(pending['grid.import']||pending['grid.export']);
 $('ha-message').textContent=demo?'SAMPLE CATALOGUE · Nothing here is saved to Home Assistant.':conflict?'Net grid and split import/export are both mapped. Keep one.':'Choose a reading for this part, then the sensor that feeds it.';
 $('ha-part-fields').innerHTML=info.fields.map(f=>`<button type="button" class="ha-reading ${f.key===reading?'selected':''}" data-ha-reading="${esc(f.key)}"><strong>${esc(f.label)}</strong><span>${esc(b.expected[f.role])} · ${esc(mappedLabel(f.key))}</span></button>`).join('')||'<p>Nothing to map on this part.</p>';
 $('ha-links').innerHTML=(info.links||[]).length?`<p>Also on this part</p>`+info.links.map(l=>`<button type="button" class="button" data-ha-jump="${esc(l.id)}">${esc(l.label)}</button>`).join(''):'';
 const accounting=$('ha-accounting');accounting.hidden=!info.accounting;if(info.accounting){$('ha-home-subtract').checked=!!layout().accounting?.homeIncludesEvAndCharging;$('ha-tolerance').value=layout().accounting?.toleranceKw??.05;}
 const tools=$('ha-cell-tools');tools.hidden=!info.pack;if(info.pack){$('ha-cell-count').value=cellsOf(info.pack.id).length||16;$('ha-series-hint').textContent=series?`Click the first ${series.label} sensor. The following numbered sensors map onto the cells in order.`:'';}
 renderCandidates();
}
function renderCandidates(){
 const queryBox=$('ha-search'),deviceBox=$('ha-device');if(queryBox)search=queryBox.value;if(deviceBox)device=deviceBox.value;
 const info=spec(partId),field=(info?.fields||[]).find(f=>f.key===reading);if(!field){$('ha-results').innerHTML='';$('ha-result-count').textContent='';return;}
 const role=series?.role||field.role;
 const all=b.candidates(entities,role,search,device,false);
 const selectedId=pending[reading]?.entityId;
 const matches=hideUnavailable?all.filter(e=>b.available(e)||e.entity_id===selectedId):all;
 const shown=matches.slice(0,240);
 const hidden=all.length-matches.length;
 $('ha-hide-unavailable').checked=hideUnavailable;
 $('ha-results').innerHTML=shown.map(e=>{const read=b.read({...pending[reading],entityId:e.entity_id},e,role);return `<button type="button" data-ha-entity="${esc(e.entity_id)}" class="ha-entity ${pending[reading]?.entityId===e.entity_id?'selected':''}"><strong>${esc(e.attributes?.friendly_name||e.entity_id)}</strong><code>${esc(e.entity_id)}</code><span>${esc(e.device_name||'No device')} · ${esc(e.area_name||'No area')}</span><b>${esc(e.state)} ${esc(e.attributes?.unit_of_measurement||'')} <small>${esc(read.quality==='good'?'→ '+read.value+' '+read.unit:read.quality)}</small></b></button>`;}).join('')||(hideUnavailable&&all.length?'<p>Every compatible sensor is unavailable. Turn off Hide unavailable to map one anyway.</p>':'<p>No compatible sensors. Try another device, or use a numbered series from the battery that owns these cells.</p>');
 $('ha-result-count').textContent=matches.length+' matching sensors'+(hidden?' · '+hidden+' unavailable hidden':'')+(matches.length>shown.length?' · first '+shown.length+' shown':'');
 const current=pending[reading]||{};$('ha-pointer').value=current.pointer||'';$('ha-unit').value=current.sourceUnit||'';$('ha-invert').checked=!!current.invert;
}
async function ensure(){if(entities.length&&state)return;const keep=preferLocal?window.LMSEnergy.getConfig():null;[entities,state]=await Promise.all([request('api/energy/entities'),request('api/energy/config')]);pending=structuredClone(state.mappings||{});window.EnergyLive.config=state;const layoutNow=keep||state.layout;state.layout=layoutNow;window.LMSEnergy.applyServerConfig(layoutNow);fillDevices(entities);}
async function openPart(id){
 partId=id;series=null;demo=false;window.EnergyLive?.hold?.(true);if(!$('ha-flyout').open)$('ha-flyout').showModal();$('ha-message').textContent='Loading sensors…';
 try{await ensure();renderPart();}catch(e){try{renderPart();}catch{} $('ha-message').textContent=e.message;}
}
function sample(){
 demo=true;const c=window.LMSEnergy.getConfig();
 state={revision:state?.revision||0,layout:c,cells:Object.fromEntries((c.storage||[]).map(v=>[v.id,Array.from({length:4},(_,i)=>'cell-'+(i+1))])),mappings:pending};
 const cell=(n,unit)=>({entity_id:'sensor.example_cell_voltage_'+n,state:String(3.3+n/100),device_id:'example-bms',device_name:'Example battery BMS',area_name:'Battery room',last_updated:new Date().toISOString(),attributes:{friendly_name:'Example cell voltage '+n,unit_of_measurement:unit}});
 entities=[{entity_id:'sensor.meter',state:'1200',device_id:'example-house',device_name:'Example house meter',area_name:'Utility',last_updated:new Date().toISOString(),attributes:{friendly_name:'Home meter',unit_of_measurement:'W'}},cell(1,'V'),cell(2,'V'),cell(3,'V'),cell(4,'V')];
 fillDevices(entities);
 renderPart();
}
function applySeries(entityId){
 const info=spec(partId);if(!info?.pack||!series)return;
 const ids=cellsOf(info.pack.id);if(!ids.length){$('ha-series-hint').textContent='Set the cell count first.';return;}
 const run=b.numberedSeries(entities,entityId,ids.length);
 run.forEach((entity,index)=>{if(!ids[index])return;pending['cells.'+info.pack.id+'.'+ids[index]+'.'+series.field]={entityId:entity.entity_id,registryId:entity.registry_id||undefined,pointer:pending[reading]?.pointer||'',sourceUnit:pending[reading]?.sourceUnit,invert:false,maxAgeSeconds:0};});
 $('ha-series-hint').textContent=`Mapped ${run.length} of ${ids.length} ${series.label} sensors, starting at ${entityId}. Review them, then save.`;
 series=null;renderPart();
}
function initialise(){
 document.querySelectorAll('.stats article').forEach((article,index)=>{const id=['solar','home','grid','battery'][index];article.classList.add('ha-part');if(!article.querySelector('[data-ha-edit]')){const button=document.createElement('button');button.type='button';button.className='ha-edit';button.dataset.haEdit=id;button.setAttribute('aria-label','Map '+id);button.textContent='✎';article.prepend(button);}});
 const history=document.querySelector('.history-panel .panel-top');
 if(history&&!history.querySelector('[data-ha-edit="tariff"]')){const button=document.createElement('button');button.type='button';button.className='ha-edit';button.dataset.haEdit='tariff';button.setAttribute('aria-label','Map tariff');button.textContent='✎';history.append(button);}
 const toggle=document.createElement('button');toggle.className='button';toggle.type='button';toggle.textContent='Edit mode';toggle.setAttribute('aria-pressed','false');
 toggle.onclick=()=>{const on=!document.body.classList.contains('ha-editing');document.body.classList.toggle('ha-editing',on);toggle.textContent=on?'Done':'Edit mode';toggle.setAttribute('aria-pressed',String(on));refreshAdd();window.LMSEnergy.render?.();};
 document.querySelector('header').append(toggle);
 const bar=document.createElement('div');bar.id='ha-add';bar.innerHTML='<span>Add</span><button type="button" class="button" data-add="array">Solar array</button><button type="button" class="button" data-add="inverter">Inverter</button><button type="button" class="button" data-add="battery">Battery</button><button type="button" class="button" data-add="wind">Wind</button><button type="button" class="button" data-add="hydro">Micro-hydro</button><button type="button" class="button" data-add="generator">Generator</button><button type="button" class="button" data-add="plugsolar">Plug-in solar</button>';
 document.querySelector('.topology-actions')?.prepend(bar);
 const dialog=document.createElement('dialog');dialog.id='ha-flyout';dialog.setAttribute('aria-labelledby','ha-flyout-title');
 dialog.innerHTML=`<div class="ha-map-head"><div><div class="eyebrow">THIS PART ONLY</div><h2 id="ha-flyout-title">Map sensors</h2></div><button id="ha-flyout-close" class="close" type="button" aria-label="Close">×</button></div><div class="ha-map-body"><aside class="ha-map-side"><div id="ha-component"></div><p id="ha-blurb"></p><p id="ha-message" role="status"></p><div id="ha-part-fields"></div><div id="ha-links" class="ha-links"></div><div id="ha-accounting" hidden><label class="ha-check"><input id="ha-home-subtract" type="checkbox"> This home meter includes EV charging and battery charging</label><label>Meter tolerance (kW)<input id="ha-tolerance" type="number" min="0" step="0.01" value="0.05"></label></div><div id="ha-cell-tools" hidden><h3>Cells</h3><p>Set the cell count, then map a numbered run. Picking cell voltage 1 maps voltage 1, 2, 3… onto cell 1, cell 2, cell 3… up to the cell count.</p><label>Number of cells<input id="ha-cell-count" type="number" min="1" max="512" value="16"></label><button id="ha-apply-cells" type="button" class="button">Use this cell count</button><div class="ha-series-actions"><button type="button" class="button" data-series="voltage" data-role="voltage">Map voltage series</button><button type="button" class="button" data-series="balanceResistance" data-role="resistance">Map resistance series</button><button type="button" class="button" data-series="soc" data-role="soc">Map cell SoC series</button></div><p id="ha-series-hint"></p><details><summary>One BMS entity with a cell array</summary><p>Use <code>{index}</code> in the pointer, for example <code>/attributes/cells/{index}/voltage</code>, then apply it to every cell.</p><button id="ha-pointer-series" type="button" class="button">Apply pointer to every cell</button></details></div></aside><div class="ha-map-main"><div class="ha-search"><label>Search<input id="ha-search" type="search" placeholder="Cell voltage, grid, inverter…"></label><label>Device<select id="ha-device"><option value="">All devices</option></select></label></div><div class="ha-transform"><label>Attribute pointer<input id="ha-pointer" placeholder="/attributes/voltage"></label><label>Source unit<input id="ha-unit" placeholder="V, mΩ, W"></label><label class="ha-check"><input id="ha-invert" type="checkbox"> Reverse sign</label></div><div class="ha-result-bar"><span id="ha-result-count"></span><label class="ha-check"><input id="ha-hide-unavailable" type="checkbox" checked> Hide unavailable</label></div><div id="ha-results"></div></div></div><div class="ha-actions"><button id="ha-save" class="primary" type="button">Save this part</button><p id="ha-save-note">Nothing reaches the diagram until you press Save this part.</p><button id="ha-clear" class="button" type="button">Clear selected reading</button><button id="ha-sample" class="button" type="button">Try sample sensors</button></div>`;
 document.body.append(dialog);
 $('ha-flyout-close').onclick=()=>dialog.close();
 dialog.addEventListener('close',()=>{window.EnergyLive?.hold?.(false);window.EnergyLive?.flushLayout?.();});
 function saveNote(text,bad){const note=$('ha-save-note');note.textContent=text;note.classList.toggle('bad',!!bad);const button=$('ha-save');button.classList.remove('ha-saved');button.textContent='Save this part';}
 function savedSummary(){const button=$('ha-save');button.classList.add('ha-saved');button.textContent='Saved';const pack=partId.startsWith('storage:')?window.LMSEnergy.getState().batteryUnits?.find(v=>v.id===partId.slice(8)):null;if(!pack)return 'Saved. The diagram is using these sensors.';const soc=Number.isFinite(pack.soc)?Math.round(pack.soc)+'%':'no state of charge yet';const energy=Number.isFinite(pack.storedKwh)&&Number.isFinite(pack.totalCapacity)?` · ${pack.storedKwh.toFixed(1)} / ${pack.totalCapacity.toFixed(1)} kWh`:'';const power=Number.isFinite(pack.power)?` · ${(pack.power>0?'+':'')+pack.power.toFixed(2)} kW`:'';return `Saved. ${pack.name} is now ${soc}${energy}${power}.`;}
 bar.onclick=event=>{const target=event.target.closest('[data-add]');if(!target||target.disabled)return;addAndOpen(target.dataset.add,'');};
 $('ha-component').addEventListener('input',event=>{if(event.target.dataset.comp)writeComponent(event.target);});
 const confirm=document.createElement('dialog');confirm.id='ha-confirm';confirm.setAttribute('aria-labelledby','ha-confirm-title');
 confirm.innerHTML='<h2 id="ha-confirm-title">Remove this?</h2><p id="ha-confirm-copy"></p><div class="ha-confirm-actions"><button type="button" class="button" id="ha-confirm-cancel">Cancel</button><button type="button" class="ha-danger" id="ha-confirm-yes">Remove</button></div>';
 document.body.append(confirm);
 let pendingRemove=null;
 function askRemove(part,name){pendingRemove={part,name:name||'this component'};$('ha-confirm-title').textContent='Remove '+pendingRemove.name+'?';$('ha-confirm-copy').textContent='This takes '+pendingRemove.name+' off the diagram.';if(!confirm.open)confirm.showModal();}
 function commitRemove(){const job=pendingRemove;pendingRemove=null;confirm.close();if(!job)return;const id=job.part.slice(job.part.indexOf(':')+1);preferLocal=true;window.LMSEnergy.editComponent(c=>{const t=window.EnergyTopology;if(job.part.startsWith('array:'))t.removeArray(c,id);else if(job.part.startsWith('inverter:'))t.removeInverter(c,id);else if(job.part.startsWith('storage:'))t.removeBattery(c,id);else if(job.part.startsWith('source:'))t.removeSource(c,id);else if(job.part.startsWith('charger:'))t.removeCharger(c,id);else if(job.part.startsWith('vehicle:'))t.removeVehicle(c,id);else if(job.part.startsWith('load:'))t.removeLoad(c,id);});refreshAdd();if($('ha-flyout')?.open&&partId===job.part)$('ha-flyout').close();}
 $('ha-confirm-cancel').onclick=()=>{pendingRemove=null;confirm.close();};
 $('ha-confirm-yes').onclick=commitRemove;
 confirm.addEventListener('click',event=>{if(event.target===confirm){$('ha-confirm-cancel').click();}});
 $('ha-component').addEventListener('click',event=>{const add=event.target.closest('[data-comp-add]');const remove=event.target.closest('[data-comp-remove]');if(add&&!add.disabled){const link=partId.startsWith('array:')?partId.slice(6):partId.startsWith('inverter:')?partId.slice(9):'';addAndOpen(add.dataset.compAdd,link);}if(!remove)return;const item=currentItem(layout());askRemove(partId,item?.name||'this component');});
 document.addEventListener('click',event=>{const remove=event.target.closest('[data-ha-remove]');if(remove){event.preventDefault();event.stopPropagation();askRemove(remove.dataset.haRemove,remove.dataset.haRemoveName);return;}const target=event.target.closest('[data-ha-edit]');if(!target||!document.body.classList.contains('ha-editing'))return;event.preventDefault();openPart(target.dataset.haEdit);});
 $('ha-part-fields').onclick=event=>{const target=event.target.closest('[data-ha-reading]');if(!target)return;reading=target.dataset.haReading;series=null;renderPart();};
 $('ha-links').onclick=event=>{const target=event.target.closest('[data-ha-jump]');if(target)openPart(target.dataset.haJump);};
 $('ha-search').oninput=event=>{search=event.target.value;renderCandidates();};
 $('ha-device').onchange=event=>{device=event.target.value;renderCandidates();};
 $('ha-hide-unavailable').onchange=event=>{hideUnavailable=event.target.checked;try{sessionStorage.setItem('lms-energy-hide-unavailable',hideUnavailable?'1':'0');}catch{}renderCandidates();};
 for(const id of ['ha-pointer','ha-unit','ha-invert'])$(id).onchange=()=>{if(!reading)return;pending[reading]={...(pending[reading]||{}),pointer:$('ha-pointer').value,sourceUnit:$('ha-unit').value||undefined,invert:$('ha-invert').checked};renderCandidates();};
 $('ha-results').onclick=event=>{const target=event.target.closest('[data-ha-entity]');if(!target)return;if(series){applySeries(target.dataset.haEntity);return;}const entity=entities.find(e=>e.entity_id===target.dataset.haEntity);pending[reading]={...(pending[reading]||{}),entityId:entity.entity_id,registryId:entity.registry_id||undefined};saveNote('Selected. Press Save this part to put it on the diagram.');renderPart();};
 $('ha-clear').onclick=()=>{delete pending[reading];renderPart();};
 $('ha-apply-cells').onclick=()=>{const info=spec(partId);if(!info?.pack)return;const count=Math.max(1,Math.min(512,Number($('ha-cell-count').value)||0));state.cells||={};state.cells[info.pack.id]=Array.from({length:count},(_,i)=>'cell-'+(i+1));const allowed=new Set(b.fields(layout(),state.cells).map(f=>f.key));for(const key of Object.keys(pending))if(key.startsWith('cells.'+info.pack.id+'.')&&!allowed.has(key))delete pending[key];$('ha-series-hint').textContent=count+' cells ready. Map a voltage or resistance series next.';renderPart();};
 dialog.onclick=event=>{const target=event.target.closest('[data-series]');if(!target)return;const info=spec(partId);if(!info?.pack)return;if(!cellsOf(info.pack.id).length)$('ha-apply-cells').click();series={field:target.dataset.series,role:target.dataset.role,label:target.textContent.replace('Map ','').replace(' series','')};reading='cells.'+info.pack.id+'.'+(cellsOf(info.pack.id)[0]||'cell-1')+'.'+series.field;renderPart();};
 $('ha-pointer-series').onclick=()=>{const info=spec(partId),binding=pending[reading];if(!info?.pack||!binding?.pointer?.includes('{index}')){$('ha-series-hint').textContent='Select a cell reading whose pointer contains {index}.';return;}const field=reading.split('.').at(-1);cellsOf(info.pack.id).forEach((id,index)=>{pending['cells.'+info.pack.id+'.'+id+'.'+field]={...binding,pointer:binding.pointer.replaceAll('{index}',String(index))};});$('ha-series-hint').textContent='Pointer applied to every cell. Save when the previews look right.';renderPart();};
 $('ha-sample').onclick=sample;
 $('ha-save').onclick=async()=>{const button=$('ha-save');if(demo){saveNote('Sample mapping preview only. Nothing was saved.',true);$('ha-message').textContent='Sample mapping preview only. No backend or live Home Assistant changes were made.';return;}button.disabled=true;button.textContent='Saving…';$('ha-save-note').classList.remove('bad');$('ha-save-note').textContent='Saving the sensors…';try{const revision=await window.EnergyLive.settle?.();const next=window.LMSEnergy.getConfig();if($('ha-accounting')&&!$('ha-accounting').hidden)next.accounting={homeIncludesEvAndCharging:$('ha-home-subtract').checked,toleranceKw:Number($('ha-tolerance').value)};state=await request('api/energy/config',{method:'PUT',headers:{'Content-Type':'application/json','X-Energy-Studio':'1'},body:JSON.stringify({layout:next,cells:state.cells||{},mappings:pending,expectedRevision:revision??state.revision})});window.EnergyLive.config=state;const readings=b.evaluate(pending,entities,b.fields(next,state.cells||{}));window.LMSEnergy.updateSnapshot({connection:'connected',revision:state.revision,observedAt:new Date().toISOString(),readings,residual:{value:null,quality:'pending'}});button.classList.add('ha-saved');button.textContent='Saved';$('ha-save-note').textContent=savedSummary();renderPart();window.EnergyLive.reloadHistories?.();}catch(e){saveNote(e.message,true);}finally{button.disabled=false;}};
}
initialise();
})();
