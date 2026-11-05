import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite = await createServer({root: fileURLToPath(new URL('../', import.meta.url)), server: {middlewareMode:true}, appType:'custom', logLevel:'silent'});
const { MachineCounterReportPage } = await vite.ssrLoadModule('/src/presentation/MachineCounterReportPage.tsx');
after(() => vite.close());
const machineId = 'de2fd552-9bc5-45ed-9a7c-0c4a2cd3e9ed';
function fixture(selection, count = 121) {
  return {machineId, streamKey:'mtconnect:M01', throughPosition:'200', acquisitionRevision:'2', ...selection,
    readAsOf:'2026-10-11T00:00:00Z', availableSignals:[{source:'mtconnect',address:'part_count'},{source:'mtconnect',address:'M01_p1_part_count_complete'},{source:'other',address:'other_count'}],
    samples:Array.from({length:count}, (_,i) => ({position:String(i+1), instanceId:'1',sequence:String(i+1),
      observedAtUtc:new Date(Date.parse('2026-10-06T01:00:00Z')+i*1000).toISOString(), signalType:'Enumeration',quality:'Good',
      rawValue:String(i + 151),previousPosition:i ? String(i) : null,arithmeticDifference:i ? '1' : null,
      flags:['ContinuityNotEstablished','AttributionNotEstablished',...(i ? [] : ['BaselineOnly'])]}))};
}
function button(h, label) { return [...h.document.querySelectorAll('button')].find(b => b.textContent === label); }
function field(h, label) { return [...h.document.querySelectorAll('label')].find(l => l.textContent.startsWith(label)).querySelector('select'); }
async function choose(h, label, value) { const select=field(h,label); await act(async () => {
  Object.getOwnPropertyDescriptor(h.window.HTMLSelectElement.prototype, 'value').set.call(select,value);
  select.dispatchEvent(new h.window.Event('change',{bubbles:true}));
}); }
async function discover(h) { await h.click(button(h,'Find signals')); }
async function selectSignal(h, source='mtconnect', address='part_count') { await choose(h,'Exact source',source); await choose(h,'Exact address',address); }

test('counter UI pages only retained rows; defaults do not load rows; failed reads remove results', async t => {
  const original = globalThis.fetch; t.after(() => {globalThis.fetch=original;});
  let failed=false;let count=121;let calls=0;let sent;
  globalThis.fetch=async (_, options) => {calls++;sent=JSON.parse(options.body);return failed ? new Response('',{status:503}) : Response.json(fixture(sent,count));};
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000}));t.after(() => h.dispose());
  const dates=h.document.querySelectorAll('input[type=date]'); assert.equal(dates[0].value,dates[1].value);
  await h.changeInput(dates[0],'2026-10-06');await h.changeInput(dates[1],'2026-10-06');await discover(h);await selectSignal(h);await h.submit(dates[0].form);
  assert.equal(sent.address,'part_count');assert.equal(h.document.querySelectorAll('tbody tr').length,50);
  const summary=h.document.querySelector('[aria-labelledby="counter-summary-title"]');
  assert.match(summary.textContent,/Stored observations121/);
  assert.match(summary.textContent,/Increasing comparisons120/);
  assert.match(summary.textContent,/No arithmetic comparison1/);
  const next=() => [...h.document.querySelectorAll('button')].find(b=>b.textContent==='Next counter page');
  await h.click(next());await h.click(next());assert.equal(h.document.querySelectorAll('tbody tr').length,21);assert.equal(next().disabled,true);assert.equal(calls,3);
  const issueFilter=h.document.querySelector('input[type=checkbox]');
  await h.click(issueFilter);
  assert.equal(h.document.querySelectorAll('tbody tr').length,1);
  assert.match(h.document.body.textContent,/Rows 1–1 of 1 · Page 1 of 1/);
  assert.match(summary.textContent,/Stored observations121/);
  await h.click(issueFilter);
  assert.equal(h.document.querySelectorAll('tbody tr').length,50);
  assert.equal(calls,3);
  assert.match(h.document.body.textContent,/Numeric and Enumeration signals are listed/);
  assert.match(h.document.body.textContent,/Enumeration/);
  assert.match(h.document.body.textContent,/M01_p1_part_count_complete/);
  assert.match(h.document.body.textContent,/policy assumption/);assert.match(h.document.body.textContent,/06\/10\/2026/);
  assert.equal(h.document.querySelector('[aria-label="Exact raw counter observations"]').tabIndex,0);
  await choose(h,'Exact address','M01_p1_part_count_complete');
  assert.match(h.document.body.textContent,/Loaded signal: mtconnect \/ part_count/);assert.equal(calls,3);
  count=0;await h.submit(dates[0].form);assert.equal(sent.address,'M01_p1_part_count_complete');assert.equal(h.document.querySelectorAll('tbody tr').length,0);
  assert.match(h.document.body.textContent,/does not establish zero production/);assert.equal(next().disabled,true);
  failed=true;await h.submit(dates[0].form);assert.match(h.document.querySelector('[role=alert]').textContent,/503/);assert.equal(h.document.querySelector('table'),null);
});

test('automatic discovery defaults the pair and source changes never combine unrelated addresses', async t => {
  const original=globalThis.fetch;t.after(()=>{globalThis.fetch=original;});
  let sent;let calls=0;
  globalThis.fetch=async (_,options)=>{sent=JSON.parse(options.body);calls++;return Response.json(fixture(sent,0));};
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000}));t.after(()=>h.dispose());
  assert.equal(calls,1);
  assert.equal(field(h,'Exact source').value,'mtconnect');
  assert.equal(field(h,'Exact address').value,'part_count');
  assert.equal(button(h,'Load counter').disabled,false);
  assert.equal(h.document.querySelector('table'),null);
  await choose(h,'Exact source','mtconnect');
  assert.deepEqual([...field(h,'Exact address').options].map(o=>o.value),['','part_count','M01_p1_part_count_complete']);
  await choose(h,'Exact address','M01_p1_part_count_complete');
  await choose(h,'Exact source','other');
  assert.equal(field(h,'Exact address').value,'other_count');
  assert.deepEqual([...field(h,'Exact address').options].map(o=>o.value),['','other_count']);
  assert.equal(button(h,'Load counter').disabled,false);
  await choose(h,'Exact address','other_count');
  assert.equal(calls,1);
  await h.submit(h.document.querySelector('form'));
  assert.equal(sent.source,'other');assert.equal(sent.address,'other_count');
});

test('empty or failed discovery keeps selection disabled and date changes discard old choices', async t => {
  const original=globalThis.fetch;t.after(()=>{globalThis.fetch=original;});
  let mode='populated';
  globalThis.fetch=async (_,options)=>mode==='failed' ? new Response('',{status:503}) :
    Response.json({...fixture(JSON.parse(options.body),0),...(mode==='empty'?{availableSignals:[]}: {})});
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000}));t.after(()=>h.dispose());
  await discover(h);await selectSignal(h);
  assert.equal(button(h,'Load counter').disabled,false);
  const dates=h.document.querySelectorAll('input[type=date]');
  await h.changeInput(dates[0],'2026-10-01');
  await h.changeInput(dates[1],'2026-10-01');
  assert.equal(field(h,'Exact source').disabled,true);
  assert.equal(field(h,'Exact address').value,'');
  assert.equal(button(h,'Load counter').disabled,true);
  mode='empty';await discover(h);
  assert.match(h.document.body.textContent,/No Numeric or Enumeration signals returned/);
  assert.match(h.document.body.textContent,/does not establish zero production/);
  mode='failed';await discover(h);
  assert.match(h.document.querySelector('[role=alert]').textContent,/503/);
  assert.doesNotMatch(h.document.body.textContent,/No Numeric or Enumeration signals returned/);
  assert.equal(field(h,'Exact source').disabled,true);
});

test('date changes cancel discovery and ignore its late result', async t => {
  const original=globalThis.fetch;t.after(()=>{globalThis.fetch=original;});
  let resolve;let sent;let signal;
  globalThis.fetch=async (_,options)=>{sent=JSON.parse(options.body);signal=options.signal;return new Promise(r=>{resolve=r;});};
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000}));t.after(()=>h.dispose());
  await discover(h);
  await h.changeInput(h.document.querySelector('input[type=date]'),'2026-10-01');
  assert.equal(signal.aborted,true);
  await act(async()=>{resolve(Response.json(fixture(sent,0)));});
  assert.equal(field(h,'Exact source').disabled,true);
  assert.equal(h.document.querySelector('[role=alert]'),null);
  assert.doesNotMatch(h.document.body.textContent,/discovery succeeded/);
});


 test('rediscovery restores only a still-available saved pair and otherwise defaults to first discovered pair', async t => {
  const original=globalThis.fetch; t.after(()=>{globalThis.fetch=original;});
  let signals=[{source:'mtconnect',address:'part_count'},{source:'other',address:'other_count'}];
  globalThis.fetch=async (_,options)=>Response.json({...fixture(JSON.parse(options.body),0),availableSignals:signals});
  const h=await mountInDom(React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000})); t.after(()=>h.dispose());
  await choose(h,'Exact source','other');
  const dates=h.document.querySelectorAll('input[type=date]');
  await h.changeInput(dates[0],'2026-10-01');
  await h.changeInput(dates[1],'2026-10-01');
  await discover(h);
  assert.equal(field(h,'Exact source').value,'other'); assert.equal(field(h,'Exact address').value,'other_count');
  assert.equal(h.document.querySelector('table'),null);
  signals=[{source:'mtconnect',address:'part_count'}]; await discover(h);
  assert.equal(field(h,'Exact source').value,'mtconnect'); assert.equal(field(h,'Exact address').value,'part_count');
  assert.deepEqual(JSON.parse(h.window.localStorage.getItem(`factoryconnect:counter-selection:${machineId}`)),{source:'mtconnect',address:'part_count'});
 });

test('opening the page restores the machine-specific browser preference without loading counter rows', async t => {
  const original=globalThis.fetch; t.after(()=>{globalThis.fetch=original;}); let calls=0;
  globalThis.fetch=async (_,options)=>{calls++;return Response.json(fixture(JSON.parse(options.body),0));};
  function SeededPage() {
    window.localStorage.setItem(`factoryconnect:counter-selection:${machineId}`,JSON.stringify({source:'other',address:'other_count'}));
    return React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000});
  }
  const h=await mountInDom(React.createElement(SeededPage)); t.after(()=>h.dispose());
  assert.equal(calls,1); assert.equal(field(h,'Exact source').value,'other'); assert.equal(field(h,'Exact address').value,'other_count');
  assert.equal(h.document.querySelector('table'),null); assert.equal(button(h,'Load counter').disabled,false);
});

test('blocked browser storage does not prevent automatic discovery and default selection', async t => {
  const original=globalThis.fetch; t.after(()=>{globalThis.fetch=original;});
  globalThis.fetch=async (_,options)=>Response.json(fixture(JSON.parse(options.body),0));
  function BlockedStoragePage() {
    Object.defineProperty(window,'localStorage',{configurable:true,get(){throw new Error('storage denied');}});
    return React.createElement(MachineCounterReportPage,{machineId,timeoutMilliseconds:1000});
  }
  const h=await mountInDom(React.createElement(BlockedStoragePage)); t.after(()=>h.dispose());
  assert.equal(field(h,'Exact source').value,'mtconnect'); assert.equal(field(h,'Exact address').value,'part_count');
  assert.equal(h.document.querySelector('[role=alert]'),null);
});
