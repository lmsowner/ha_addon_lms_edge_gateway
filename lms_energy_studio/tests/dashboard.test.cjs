const {JSDOM}=require('jsdom');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.join(__dirname,'../src/HA.LMS.EnergyStudio/wwwroot');
const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://localhost/nested/energy/',runScripts:'outside-only'});
const w=dom.window;w.structuredClone=structuredClone;w.matchMedia=()=>({matches:true,addEventListener(){}});w.setInterval=()=>0;
w.HTMLDialogElement.prototype.showModal=function(){this.open=true};w.HTMLDialogElement.prototype.close=function(){this.open=false};
let config={revision:0,layout:{},cells:{},mappings:{}},stream,requests=[];
w.fetch=async(url,options={})=>{requests.push({url:String(url),options});const p=new URL(url).pathname;if(p.endsWith('/healthz'))return {ok:true,json:async()=>({version:'test'})};assert.ok(p.startsWith('/nested/energy/api/energy/'));
 if(p.endsWith('/config')){if(options.method==='PUT'){const body=JSON.parse(options.body);assert.equal(options.headers['X-Energy-Studio'],'1');config={...body,revision:config.revision+1};delete config.expectedRevision;}return {ok:true,json:async()=>structuredClone(config)}}
 if(p.endsWith('/entities'))return {ok:true,json:async()=>[{entity_id:'sensor.meter',registry_id:'meter-identity',state:'1200',last_updated:new Date().toISOString(),attributes:{unit_of_measurement:'W',friendly_name:'Home meter'}}]};
 if(p.endsWith('/history'))return {ok:true,json:async()=>({points:[],intervals:[],totalKwh:0,coverageSeconds:0,costMinor:null})};
 throw Error('Unexpected request '+url);
};
w.EventSource=class{constructor(url){assert.equal(new URL(url).pathname,'/nested/energy/api/energy/live');stream=this;}addEventListener(name,fn){this.listener=fn}close(){}};
for(const file of ['topology.js','battery-analysis.js','energy-history.js','app.js','ha-bindings.js','ha-picker.js','live.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));
(async()=>{
 await new Promise(r=>setTimeout(r,30));assert.equal(config.revision,1);assert.equal(config.layout.tariff.confirmed,false);assert.ok(config.layout.vehicles.every(v=>v.id));
 const snapshot={connection:'connected',revision:config.revision,observedAt:new Date().toISOString(),readings:{home:{value:1.2,quality:'good'},grid:{value:null,quality:'unavailable'},['arrays.'+config.layout.arrayRoutes[0].id]:{value:2,quality:'good'}},residual:{value:null,quality:'incomplete'}};
 stream.onmessage({data:JSON.stringify(snapshot)});stream.listener({data:JSON.stringify(snapshot)});
 assert.equal(w.document.getElementById('home-value').textContent,'1.20 kW');assert.equal(w.document.getElementById('grid-value').textContent,'—');assert.ok(w.document.getElementById('scene').textContent.includes('2.00 kW'));
 assert.ok(!w.document.getElementById('scene').textContent.includes('NaN'));assert.ok(!w.document.getElementById('load-cards').textContent.includes('NaN'));
 assert.ok(!w.LMSEnergy.getState().batteryAnalysis[config.layout.storage[0].id].cells.length);
 assert.ok(w.document.getElementById('energy-mini-origin').textContent.includes('Recorded history'));
 const picker=[...w.document.querySelectorAll('header button')].find(b=>b.textContent==='Home Assistant entities');picker.click();await new Promise(r=>setTimeout(r,20));assert.ok(w.document.getElementById('ha-mappings').open);assert.ok(w.document.getElementById('ha-field').options.length>10);
 w.document.querySelector('[data-ha-entity="sensor.meter"]').click();w.document.getElementById('ha-save').click();await new Promise(r=>setTimeout(r,20));assert.equal(config.mappings.home.entityId,'sensor.meter');assert.equal(config.mappings.home.registryId,'meter-identity');w.document.getElementById('ha-sample').click();w.document.getElementById('ha-save').click();assert.ok(w.document.getElementById('ha-message').textContent.includes('Sample mapping preview only'));
 assert.equal(requests.filter(r=>r.options.method==='PUT').length,2);
 w.LMSEnergy.resumeDemo();assert.ok(w.document.querySelector('header + p').textContent.includes('DEMO'));
 dom.window.close();console.log('PASS: production startup, partial rendering, no fictional BMS/history, stable IDs, nested relative URLs and sample write isolation.');
})().catch(e=>{console.error(e);dom.window.close();process.exitCode=1});
