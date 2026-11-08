import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import React, { act } from 'react';
import { createServer } from 'vite';
import { mountInDom } from './dom-test-harness.mjs';
const vite=await createServer({server:{middlewareMode:true},appType:'custom',logLevel:'silent'});
after(()=>vite.close());
const {readActivityHistory}=await vite.ssrLoadModule('/src/api/evidence/activity-history-client.ts');
const {PublishedMachineActivity}=await vite.ssrLoadModule('/src/presentation/PublishedMachineActivity.tsx');
const machine='11111111-1111-1111-1111-111111111111', mapping='22222222-2222-2222-2222-222222222222';
const other='33333333-3333-3333-3333-333333333333', base='https://localhost/';
const envelope={machineId:machine,siteId:other,streamKey:'mtconnect:fixture',evidenceKind:'historical-derived-activity',currentConnectivity:null,verification:'immutable-publication-receipts',continuity:'publication-profile-not-yet-verified'};
const series={mappingId:mapping,sourceAddress:'execution',qualificationSha256:'A'.repeat(64),generation:'18446744073709551615',lastObservedAt:'2026-11-07T10:00:00Z',packageRevisionId:other,acknowledgementId:other};
const row={effectId:other,publicationOperationId:other,state:'Running',startedAt:'2026-11-07T09:00:00Z',endedAt:'2026-11-07T09:00:01Z',durationTicks:'10000000',instanceId:'18446744073709551615',startSequence:'18446744073709551614',endSequence:'18446744073709551615',startRawValue:'ACTIVE',endRawValue:'READY'};
const page={...envelope,series,rows:[row],nextCursor:'older-cut'};
test('activity protocol preserves UInt64 strings and rejects mismatched scope, overflow and live-state claims',async()=>{
  const fetcher=async()=>Response.json(page);
  const result=await readActivityHistory(base,machine,mapping,'session',null,undefined,fetcher);
  assert.equal(result.rows[0].endSequence,'18446744073709551615');
  for(const bad of [{...page,machineId:other},{...page,currentConnectivity:'online'},{...page,series:{...series,mappingId:other}},{...page,rows:[{...row,endSequence:'18446744073709551616'}]},{...page,rows:[row,row]}])
    await assert.rejects(readActivityHistory(base,machine,mapping,'session',null,undefined,async()=>Response.json(bad)),e=>e.status===502);
  await assert.rejects(readActivityHistory('http://localhost/',machine,mapping,'session',null,undefined,fetcher),e=>e.status===403);
});
test('mounted history pages bounded cuts, preserves provenance, and hides old machine/session data',async t=>{
  const previous=globalThis.fetch;t.after(()=>globalThis.fetch=previous);
  const calls=[];
  globalThis.fetch=async(url,options)=>{
    calls.push({url:String(url),options});
    if(String(url).endsWith('/activity'))return Response.json({...envelope,status:'published-history',series:[series]});
    return Response.json(String(url).includes('?cursor=')?{...page,rows:[{...row,state:'Idle'}],nextCursor:null}:page);
  };
  const view=(machineId=machine,sessionBearer='session')=>React.createElement(PublishedMachineActivity,{machineId,sessionBearer,baseAddress:base});
  const h=await mountInDom(view(),base);t.after(()=>h.dispose());
  assert.match(h.document.querySelector('tbody').textContent,/Running/);
  assert.match(h.document.querySelector('details').textContent,/18446744073709551615/);
  assert.match(h.document.body.textContent,/1 s/);
  await act(async()=>[...h.document.querySelectorAll('button')].find(x=>x.textContent==='Older activity periods').click());
  assert.match(h.document.querySelector('tbody').textContent,/Idle/);
  assert.ok(calls.at(-1).url.endsWith('?cursor=older-cut'));
  assert.ok(calls.every(x=>x.options.cache==='no-store'&&x.options.headers.Authorization==='Bearer session'));
  globalThis.fetch=()=>new Promise(()=>{});
  await h.rerender(view(other));assert.equal(h.document.querySelector('tbody'),null);
  await h.rerender(view(other,null));assert.equal(h.document.querySelector('tbody'),null);
  assert.match(h.document.querySelector('[role=alert]').textContent,/not authorized/);
});
test('mounted history distinguishes empty publication, denied and unavailable authority',async t=>{
  const previous=globalThis.fetch;t.after(()=>globalThis.fetch=previous);
  for(const [status,text] of [[200,/No published activity history/],[401,/not authorized/],[503,/unavailable/]]){
    globalThis.fetch=async()=>status===200?Response.json({...envelope,status:'no-published-history',series:[]}):new Response('',{status});
    const h=await mountInDom(React.createElement(PublishedMachineActivity,{machineId:machine,sessionBearer:'session',baseAddress:base}),base);
    try{assert.match(h.document.body.textContent,text);assert.equal(h.document.querySelector('tbody'),null);}finally{await h.dispose();}
  }
});
