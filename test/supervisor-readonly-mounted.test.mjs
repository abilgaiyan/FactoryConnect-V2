import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), server: { middlewareMode: true }, appType: 'custom', logLevel: 'silent' });
const { default: Workspace } = await vite.ssrLoadModule('/src/presentation/SupervisorWorkspace.tsx');
const { ReportingNetworkFailure } = await vite.ssrLoadModule('/src/api/reporting/reporting-transport-failures.ts');
const { App } = await vite.ssrLoadModule('/src/App.tsx');
after(() => vite.close());
const source = { machineId: '677b030b-1ede-4a67-82ae-6a494791357f', processorId: 'operational-metrics', siteId: 'SITE', productionLineId: 'LINE', displayName: 'Configured CNC', groupName: null, displayOrder: 0 };
const moxa = { ...source, machineId: 'bfa0dc97-9a68-45ed-b189-1dacb7d863f0', displayName: 'Configured conventional', productionLineId: 'LINE-2' };
const day = '2026-10-28';
const first = { siteId: 'SITE', shiftScheduleAssignmentId: 'original-a', shiftId: 'SHIFT-3', startsAtUtc: '2026-10-28T18:30:00Z', endsAtUtc: '2026-10-29T02:00:00Z' };
const second = { ...first, shiftScheduleAssignmentId: 'original-b', startsAtUtc: '2026-10-29T02:00:00Z', endsAtUtc: '2026-10-29T03:00:00Z' };
const states = ['unknown', 'stopped', 'idle', 'running', 'fault', 'offline'];
function metric(key, status, value, reason = null) { return { metricKey: key, definitionVersion: '1.0', status, value, unit: 'Ratio', reasonCode: reason, reasonOperandName: null }; }
function page(request, shifts = [first, second], availability = '0') {
  const selectedSource = [source, moxa].find(s => s.machineId === request.sources[0].machineId);
  return { items: shifts.map(shift => ({ machineId: selectedSource.machineId, processorId: selectedSource.processorId, productionLineId: selectedSource.productionLineId,
    productionDay: { siteId: 'SITE', businessDate: request.sources[0].businessDate }, shift,
    context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
    sourceRevision: { machineId: selectedSource.machineId, processorId: 'metric-input', streamKey: 'metric-input', position: '9007199254740993' },
    metrics: [metric('availability','calculated', availability), metric('utilization.elr','calculated','0.25'), metric('performance','insufficient-evidence',null,'MissingReferenceTime'), metric('quality','unavailable',null,'MissingGoodQuantity'), metric('oee','insufficient-evidence',null,'DependencyUnavailable')] })), continuationToken: null };
}
function runtime(query = async r => page(r), sources = [source, moxa]) {
  const calls = [];
  return { calls, value: { configuration: { sources, reportingBasePath: '/', requestTimeoutMilliseconds: 1000 }, reportingClient: {
    async queryProductionDayShiftMetrics(request, options) { calls.push({request,options}); return query(request, options); },
  } } };
}
function activity(range, id = source.machineId) {
  const end = new Date(Date.parse(range.fromInclusive) + 60_000).toISOString();
  return { machineId: id, stateProcessorId: 'machine-state-activity', streamKey: 'mtconnect:factory-source', throughPosition: '9007199254740993', ...range,
    readAsOf: '2026-10-29T05:00:00Z', unrepresentedTicks: (Date.parse(range.toExclusive) - Date.parse(range.fromInclusive)) * 10000 - 600000000,
    intervals: [{ position: '9007199254740992', state: 'stopped', startedAtUtc: range.fromInclusive, endedAtUtc: end,
      sourceStartedAtUtc: new Date(Date.parse(range.fromInclusive) - 60_000).toISOString(), sourceEndedAtUtc: end, durationTicks: 600000000, instanceId: '9', sequence: '9007199254740993' }],
    totals: states.map(state => ({state, durationTicks: state === 'stopped' ? 600000000 : 0})) };
}
function text(h) { return h.document.body.textContent; }
function button(h, label) { return [...h.document.querySelectorAll('button')].find(b => b.textContent === label); }
function control(h, label, tag = 'select') { return [...h.document.querySelectorAll('label')].find(l => l.textContent.startsWith(label)).querySelector(tag); }
async function choose(h, label, value) { await act(async () => { const el = control(h,label); el.value = value; el.dispatchEvent(new h.window.Event('change',{bubbles:true})); }); }
async function load(h) { await choose(h,'Machine / reporting processor','0'); await h.changeInput(control(h,'Production business date','input'),day); await h.click(button(h,'Load shift occurrences')); }
async function selectOccurrence(h, index = 1) { const select = control(h,'Exact shift occurrence'); await choose(h,'Exact shift occurrence',select.options[index].value); }
function mockFetch(t, fetcher) { const previous = globalThis.fetch; globalThis.fetch = fetcher; t.after(() => { globalThis.fetch = previous; }); }
function deferred() { let resolve, reject; const promise = new Promise((r,j) => {resolve=r;reject=j;}); return {promise,resolve,reject}; }

test('real mode reuses canonical reporting and exact overnight activity; inspection retains provenance', async t => {
  const r = runtime(); const requests = [];
  mockFetch(t, async (url, options) => { requests.push({url,options}); return Response.json(activity(JSON.parse(options.body))); });
  const h = await mountInDom(React.createElement(Workspace,{runtime:r.value})); t.after(() => h.dispose());
  assert.match(text(h), /Real read-only mode/); assert.doesNotMatch(text(h), /Sample operator|sample-event/);
  await load(h); assert.equal(r.calls.length,1);
  assert.deepEqual(r.calls[0].request.sources,[{machineId:source.machineId,processorId:source.processorId,siteId:'SITE',businessDate:day}]);
  assert.equal(r.calls[0].request.metrics.length,5); assert.equal(r.calls[0].request.context.unpartitionedOnly,true);
  assert.equal(requests.length,0);
  assert.equal(control(h,'Exact shift occurrence').options.length,3);
  await selectOccurrence(h);
  assert.equal(requests.length,1);
  assert.match(requests[0].url,new RegExp(source.machineId));
  assert.deepEqual(JSON.parse(requests[0].options.body),{fromInclusive:first.startsAtUtc,toExclusive:first.endsAtUtc});
  assert.match(text(h), /MissingReferenceTime/); assert.match(text(h), /MissingGoodQuantity/); assert.match(text(h), /DependencyUnavailable/);
  assert.match(h.document.querySelector('.performance-kpis').textContent,/0%/);
  assert.match(text(h),/9007199254740993/); assert.match(text(h),/mtconnect:factory-source/);
  assert.ok([...h.document.querySelectorAll('time')].some(el=>el.dateTime==='2026-10-29T05:00:00Z')); assert.match(text(h),/Unrepresented coverage/);
  assert.match(text(h),/2026-10-28T18:29:00.000Z/);
  assert.match(text(h),/not a replacement exact-event key/);
  assert.equal(h.document.querySelector('textarea'),null);
  assert.equal(button(h,'Production Details'),undefined);
  await selectOccurrence(h,2);
  assert.deepEqual(JSON.parse(requests[1].options.body),{fromInclusive:second.startsAtUtc,toExclusive:second.endsAtUtc});
  assert.match(text(h), /Assignment original-b/);
});

test('above-100 availability stays conflicting and source context mismatch fails closed', async t => {
  let conflict = false;
  const r = runtime(async request => { const p = page(request,[first],'1.2'); if(conflict)p.items[0].productionLineId='WRONG'; return p; });
  mockFetch(t,async (url,options) => Response.json(activity(JSON.parse(options.body))));
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await selectOccurrence(h);
  assert.match(text(h),/Evidence conflict/);assert.match(text(h),/Reported 120%/);
  conflict=true;await h.click(button(h,'Load shift occurrences'));
  assert.match(text(h),/violated the Shift Performance contract/);assert.equal(controlSafe(h,'Exact shift occurrence'),null);
  assert.equal(h.document.querySelector('.performance-kpis'),null);assert.doesNotMatch(text(h),/Sample operator/);
});
function controlSafe(h,label) { return [...h.document.querySelectorAll('label')].find(l=>l.textContent.startsWith(label))??null; }

test('late activity responses cannot cross occurrence, machine, day or mode changes', async t => {
  const r=runtime(); const pending=[];
  mockFetch(t,(url,options)=>{const d=deferred();pending.push({url,options,...d});return d.promise;});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await selectOccurrence(h);
  await selectOccurrence(h,2);assert.equal(pending[0].options.signal.aborted,true);
  await act(async()=>pending[0].resolve(Response.json(activity(JSON.parse(pending[0].options.body)))));
  assert.doesNotMatch(text(h),/Activity read succeeded|Position 9007199254740992/);
  await choose(h,'Machine / reporting processor','1');assert.equal(pending[1].options.signal.aborted,true);
  assert.equal(h.document.querySelector('.performance-kpis'),null);
  await h.click(button(h,'Load shift occurrences'));await selectOccurrence(h);
  assert.match(pending[2].url,new RegExp(moxa.machineId));
  await h.changeInput(control(h,'Production business date','input'),'2026-10-29');assert.equal(pending[2].options.signal.aborted,true);
  await h.click(button(h,'Load shift occurrences'));await selectOccurrence(h);
  await h.click(button(h,'Sample preview'));assert.equal(pending[3].options.signal.aborted,true);
  await act(async()=>pending[3].resolve(Response.json(activity(JSON.parse(pending[3].options.body),moxa.machineId))));
  assert.match(text(h),/Sample preview — synthetic/);assert.doesNotMatch(text(h),/mtconnect:factory-source/);
});

test('late reporting response is cancelled on source change and never restores old selectors', async t=>{
  const old=deferred();const r=runtime((request)=>request.sources[0].machineId===source.machineId?old.promise:Promise.resolve(page(request,[])));
  mockFetch(t,async()=>{throw Error('No activity expected');});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await choose(h,'Machine / reporting processor','1');assert.equal(r.calls[0].options.signal.aborted,true);
  await h.click(button(h,'Load shift occurrences'));
  await act(async()=>old.resolve(page(r.calls[0].request)));
  assert.match(text(h),/No authoritative shift occurrences/);assert.equal(control(h,'Exact shift occurrence').options.length,1);
});

test('reporting failure never substitutes samples; sample mode is explicit and resets on exit',async t=>{
  const r=runtime(async()=>{throw new ReportingNetworkFailure(Error('unavailable'));});
  mockFetch(t,async()=>{throw Error('No activity expected');});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);assert.match(text(h),/reporting is unavailable/);assert.doesNotMatch(text(h),/Sample operator/);
  await h.click(button(h,'Sample preview'));assert.match(text(h),/Sample operator/);
  await h.click(button(h,'Event explanations'));await h.click(button(h,'Inspect event 1'));await h.click(button(h,'Create independent preview thread'));
  assert.match(text(h),/preview-thread-4/);
  await h.click(button(h,'Real read-only'));assert.equal(h.document.querySelector('textarea'),null);assert.equal(control(h,'Production business date','input').value,'');
  await h.click(button(h,'Sample preview'));assert.doesNotMatch(text(h),/preview-thread-4/);
  assert.equal(h.window.localStorage.length,0);
});

test('activity failure clears old evidence and retry never fabricates samples',async t=>{
  let calls=0;const r=runtime();
  mockFetch(t,async (url,options)=>{calls++;return calls===1?Response.json(activity(JSON.parse(options.body))):new Response('',{status:503});});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await selectOccurrence(h);assert.match(text(h),/Position 9007199254740992/);
  await selectOccurrence(h,2);assert.match(text(h),/Activity read unavailable/);assert.doesNotMatch(text(h),/Position 9007199254740992/);
  await h.click(button(h,'Retry activity'));assert.match(text(h),/No sample fallback/);assert.doesNotMatch(text(h),/Sample operator/);
});

test('missing config has no implicit sample mode, and an explicit sample switch works offline',async t=>{
  mockFetch(t,async()=>new Response('',{status:503}));
  const h=await mountInDom(React.createElement(Workspace));t.after(()=>h.dispose());
  assert.match(text(h),/Reporting configuration is unavailable/);assert.doesNotMatch(text(h),/Sample operator/);
  await h.click(button(h,'Sample preview'));assert.match(text(h),/Sample operator/);
});

test('dashboard route passes runtime to workspace without starting sample-mode requests',async t=>{
  const r=runtime();mockFetch(t,async()=>{throw Error('Unexpected request');});
  const h=await mountInDom(React.createElement(App,{runtime:r.value}),'http://factory-dashboard/supervisor-preview');t.after(()=>h.dispose());
  // React.lazy may need a turn to resolve the development module.
  await act(async()=>{await new Promise(resolve=>setImmediate(resolve));});
  assert.match(text(h),/Real read-only mode/);assert.match(text(h),/Configured CNC/);
  assert.equal(r.calls.length,0);
});

test('missing metrics and empty activity retain unavailable coverage without invented zero or achievement',async t=>{
  const r=runtime(async request=>{const p=page(request,[first]);p.items[0].metrics=[];return p;});
  mockFetch(t,async (url,options)=>{const data=activity(JSON.parse(options.body));data.intervals=[];data.totals=states.map(state=>({state,durationTicks:0}));data.unrepresentedTicks=270000000000;return Response.json(data);});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await selectOccurrence(h);
  const kpis=h.document.querySelector('.performance-kpis');assert.equal(kpis.querySelectorAll('article').length,5);
  assert.doesNotMatch(kpis.textContent, /0%|100%/);assert.equal((kpis.textContent.match(/—/g)??[]).length,5);
  assert.match(text(h),/No closed stored intervals returned/);assert.match(text(h),/Unrepresented coverage/);
  assert.equal(h.document.querySelector('textarea'),null);
});

test('mismatched activity machine never discloses its returned evidence in the selected scope',async t=>{
  const r=runtime();mockFetch(t,async(url,options)=>Response.json(activity(JSON.parse(options.body),moxa.machineId)));
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  await load(h);await selectOccurrence(h);
  assert.match(text(h),/Activity read unavailable or invalid/);assert.doesNotMatch(text(h),/mtconnect:factory-source|Position 9007199254740992/);
});

test('no configured sources does not create synthetic machines or reporting requests',async t=>{
  const r=runtime(undefined,[]);mockFetch(t,async()=>{throw Error('No request expected');});
  const h=await mountInDom(React.createElement(Workspace,{runtime:r.value}));t.after(()=>h.dispose());
  assert.match(text(h),/No configured reporting sources/);assert.doesNotMatch(text(h),/Configured CNC|Sample operator/);
  await h.click(button(h,'Load shift occurrences'));assert.equal(r.calls.length,0);
});
