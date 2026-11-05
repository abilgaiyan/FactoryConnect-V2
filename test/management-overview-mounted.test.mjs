import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({root: fileURLToPath(new URL('../', import.meta.url)), server:{middlewareMode:true}, appType:'custom', logLevel:'silent'});
const {App} = await vite.ssrLoadModule('/src/App.tsx');
after(() => vite.close());
const cnc = {machineId:'11111111-1111-1111-1111-111111111111', processorId:'p1', siteId:'site', productionLineId:'line', displayName:'CNC', groupName:null, displayOrder:0};
const digital = {...cnc, machineId:'22222222-2222-2222-2222-222222222222', processorId:'p2', displayName:'Conventional', productionLineId:'line2'};
const response = (id, state='stopped', freshness='current') => ({machineId:id, outcome:'evidence', coverage:'complete', evidence:{machineState:state, freshness, usability:freshness, readAsOf:'2026-10-27T00:00:00Z'}});
const metric = (source, day, key='availability', status='calculated', value='0.00', reasonCode=null) => ({scope:'production-day', processorId:source.processorId, machineId:source.machineId, shift:null, productionDay:{siteId:source.siteId,businessDate:day},context:{partId:null,operatorId:null,productionOrderId:null,operationId:null},metricKey:key,definitionVersion:'1.0',status,value,unit:'ratio',reasonCode,reasonOperandName:null,sourceRevision:{processorId:'aggregation',machineId:source.machineId,streamKey:'stream',position:'534'}});
function runtime() {return {configuration:{sources:[cnc,digital],machineProfileSchemaVersion:1,machineProfiles:[{machineId:digital.machineId,profileId:'digital-state',profileVersion:'1',integrationCategoryId:'digital',approvalReference:'signal-meaning-confirmed',effectiveFrom:'2026-10-01T00:00:00Z',stoppedLabel:'Machine OFF',capabilities:[{key:'operational-state',support:'supported',qualification:'qualified'},{key:'quantity',support:'unsupported',qualification:'not-assessed'}]}]},currentStateClient:{read:async id=>response(id)},reportingClient:{queryProductionDayMetrics:async req=>{const s=[cnc,digital].find(s=>s.machineId===req.sources[0].machineId); return {items:[metric(s,req.fromInclusive),metric(s,req.fromInclusive,'utilization.elr','unavailable',null,'MissingReferenceTime')],continuationToken:null};}}};}
const button = (h,text)=>[...h.document.querySelectorAll('button')].find(b=>b.textContent===text);
test('both machine types show statuses, calculated zero and contextual links', async t=>{
    const h=await mountInDom(React.createElement(App,{runtime:runtime()}),'http://factory-dashboard/overview?day=2026-10-16&site=site');t.after(()=>h.dispose());
    const body=h.document.body.textContent;
    assert.match(body,/2 selected machines/);assert.match(body,/Machine OFF/);assert.match(body,/Not assessed/);assert.match(body,/calculated: 0%/);assert.match(body,/unavailable: — · MissingReferenceTime/);assert.match(body,/percentages withheld/);
    assert.equal(h.document.querySelectorAll('.overview-machines article').length,2);
    const url=new URL([...h.document.querySelectorAll('a')].find(a=>a.textContent==='Supporting historical report').href);
    assert.equal(url.searchParams.get('from'),'2026-10-16');assert.equal(url.searchParams.get('machine'),cnc.machineId);assert.equal(url.searchParams.get('overview'),'?day=2026-10-16&site=site');
});
test('stale stopped and failed communications never render usable Machine OFF',async t=>{
    const r=runtime();let fail=false;r.currentStateClient.read=async id=>{if(fail)throw Error('Communication unavailable');return response(id,'stopped','stale');};
    const h=await mountInDom(React.createElement(App,{runtime:r}),'http://factory-dashboard/overview');t.after(()=>h.dispose());
    assert.ok([...h.document.querySelectorAll('.overview-state')].every(e=>e.textContent==='No usable current state'));
    assert.match(h.document.body.textContent,/freshness: stale/);
    fail=true;await h.click(button(h,'Refresh overview'));
    assert.match(h.document.body.textContent,/Communication unavailable/);assert.match(h.document.body.textContent,/Previous evidence retained/);
    assert.ok([...h.document.querySelectorAll('.overview-state')].every(e=>e.textContent==='No usable current state'));
});
test('search navigation and Back restore selection; old responses cannot replace new day',async t=>{
    const r=runtime();const pending=[];
    r.reportingClient.queryProductionDayMetrics=req=>new Promise(resolve=>pending.push({req,resolve}));
    const h=await mountInDom(React.createElement(App,{runtime:r}),'http://factory-dashboard/overview?day=2026-10-16');t.after(()=>h.dispose());
    assert.equal(pending.length,2);
    await h.changeInput(h.document.querySelector('input[type=date]'),'2026-10-17');
    assert.equal(h.window.location.search,'?day=2026-10-17');assert.equal(pending.length,4);
    await act(async()=>{for(const p of pending.slice(0,2)){const s=[cnc,digital].find(s=>s.machineId===p.req.sources[0].machineId);p.resolve({items:[metric(s,'2026-10-16','availability','calculated','0.99')],continuationToken:null});}});
    assert.doesNotMatch(h.document.body.textContent,/99%/);
    await act(async()=>{for(const p of pending.slice(2,4)){const s=[cnc,digital].find(s=>s.machineId===p.req.sources[0].machineId);p.resolve({items:[metric(s,'2026-10-17','availability','calculated','0.25')],continuationToken:null});}});
    assert.match(h.document.body.textContent,/25%/);
    await h.popstate('/overview?day=2026-10-16&machine='+cnc.machineId);
    assert.match(h.document.body.textContent,/1 selected machines/);assert.equal(h.document.querySelector('input[type=date]').value,'2026-10-16');
});
test('supporting report initializes machine/day and return context',async t=>{
    const query=new URLSearchParams({from:'2026-10-16',through:'2026-10-16',machine:digital.machineId,overview:'?day=2026-10-16&line=line2'});
    const h=await mountInDom(React.createElement(App,{runtime:runtime()}),'http://factory-dashboard/reports/range?'+query);t.after(()=>h.dispose());
    assert.deepEqual([...h.document.querySelectorAll('input[type=date]')].map(i=>i.value),['2026-10-16','2026-10-16']);
    assert.equal(h.document.querySelectorAll('input[type=checkbox]:checked').length,1);
    assert.equal([...h.document.querySelectorAll('a')].find(a=>a.textContent==='Back to selected overview').getAttribute('href'),'/overview?day=2026-10-16&line=line2');
    await h.submit(h.document.querySelector('form'));
    assert.match(h.document.body.textContent,/processor p2/);assert.doesNotMatch(h.document.body.textContent,/processor p1/);
});
test('invalid filters fail without machine requests',async t=>{
    const r=runtime();let calls=0;r.currentStateClient.read=async()=>{++calls;throw Error('unexpected');};
    const h=await mountInDom(React.createElement(App,{runtime:r}),'http://factory-dashboard/overview?machine=unknown');t.after(()=>h.dispose());
    assert.match(h.document.querySelector('[role=alert]').textContent,/does not match/);assert.equal(calls,0);
});
test('machine detail views preserve overview context across nested navigation', async t => {
    const context='?day=2026-10-16&machine='+cnc.machineId;
    const h=await mountInDom(React.createElement(App,{runtime:runtime()}),'http://factory-dashboard/machines/'+cnc.machineId+'?'+new URLSearchParams({overview:context}));t.after(()=>h.dispose());
    for(const link of h.document.querySelectorAll('[aria-label="Machine views"] a')) assert.equal(new URL(link.href).searchParams.get('overview'),context);
    await h.click([...h.document.querySelectorAll('a')].find(a=>a.textContent==='Back to selected overview'));
    assert.equal(h.window.location.pathname,'/overview');assert.equal(h.window.location.search,context);
    assert.match(h.document.body.textContent,/1 selected machines/);
});
test('report failures are isolated from live state and other machines',async t=>{
    const r=runtime();const read=r.reportingClient.queryProductionDayMetrics;
    r.reportingClient.queryProductionDayMetrics=async req=>{if(req.sources[0].machineId===digital.machineId)throw Error('History unavailable');return read(req);};
    const h=await mountInDom(React.createElement(App,{runtime:r}),'http://factory-dashboard/overview?day=2026-10-16');t.after(()=>h.dispose());
    assert.match(h.document.querySelector('[aria-label=CNC]').textContent,/calculated: 0%/);
    const card=h.document.querySelector('[aria-label=Conventional]');assert.match(card.textContent,/History unavailable/);assert.equal(card.querySelector('.overview-state').textContent,'Machine OFF');
});
test('metric renderer keeps unavailable, insufficient, absent and zero distinct', async () => {
    const {MetricStatusValue}=await vite.ssrLoadModule('/src/presentation/ManagementOverviewPage.tsx');
    for(const [items, expected] of [
        [[metric(cnc,'2026-10-16','availability','unavailable',null,'RunningOutsidePlannedProduction')], /unavailable: — · RunningOutsidePlannedProduction/],
        [[metric(cnc,'2026-10-16','availability','insufficient-evidence',null,'MissingReferenceTime')], /insufficient-evidence: — · MissingReferenceTime/],
        [[], /No reporting result/],
        [[metric(cnc,'2026-10-16')], /calculated: 0%/],
    ]) {
        const h=await mountInDom(React.createElement(MetricStatusValue,{label:'Availability',items}));
        try {assert.match(h.document.body.textContent,expected);if(items[0]?.status!=='calculated')assert.doesNotMatch(h.document.body.textContent,/0%/);}
        finally {await h.dispose();}
    }
});
test('approved fault label renders Alarm; unknown signal is not relabeled OFF', async () => {
    for (const [state,label] of [['fault','Alarm'],['unknown','unknown'],['idle','idle'],['running','running']]) {
        const r=runtime();r.configuration.machineProfiles[0].faultLabel='Alarm';r.currentStateClient.read=async id=>response(id,state);
        const h=await mountInDom(React.createElement(App,{runtime:r}),'http://factory-dashboard/overview?machine='+digital.machineId);
        try {assert.equal(h.document.querySelector('.overview-state').textContent,label);}
        finally {await h.dispose();}
    }
});
