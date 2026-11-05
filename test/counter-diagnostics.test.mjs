import assert from 'node:assert/strict';
import test from 'node:test';
import { hasCounterComparisonIssue, summarizeCounterDiagnostics } from '../src/application/counter-diagnostics.ts';
const common=['ContinuityNotEstablished','AttributionNotEstablished'];
const sample=(difference,flags=[])=>({arithmeticDifference:difference,flags:[...common,...flags]});
test('comparison categories reconcile without summing diagnostic differences as production',()=>{
  const rows=[sample('1'),sample('900719925474099300000'),sample('0'),sample(null,['BaselineOnly']),
    sample(null,['CounterDecreased']),sample(null,['MissingValue','NonGoodEvidence']),
    sample(null,['InstanceChanged','PreviousEvidenceUnusable'])];
  const s=summarizeCounterDiagnostics(rows);
  assert.deepEqual([s.sampleCount,s.increases,s.unchanged,s.notComparable,s.issueRows],[7,2,1,4,4]);
  assert.equal(s.increases+s.unchanged+s.notComparable,s.sampleCount);
  assert.equal(s.flags.find(f=>f.flag==='CounterDecreased').count,1);
  assert.equal(s.flags.find(f=>f.flag==='MissingValue').count,1);
  assert.equal(s.flags.find(f=>f.flag==='NonGoodEvidence').count,1);
  assert.equal(s.flags.some(f=>common.includes(f.flag)),false);
  assert.equal('producedQuantity' in s,false);
});
test('universal uncertainty flags do not select every row as a comparison issue',()=>{
  assert.equal(hasCounterComparisonIssue(sample('1')),false);
  assert.equal(hasCounterComparisonIssue(sample(null,['BaselineOnly'])),true);
  for(const flag of ['SignalTypeChanged','InstanceChanged','NonIncreasingTimestamp','PreviousEvidenceUnusable','NotUsableAsCounter'])
    assert.equal(hasCounterComparisonIssue(sample(null,[flag])),true);
});
test('empty input has zero observation counts without establishing zero production',()=>{
  const s=summarizeCounterDiagnostics([]);
  assert.deepEqual([s.sampleCount,s.increases,s.unchanged,s.notComparable,s.issueRows],[0,0,0,0,0]);
  assert.ok(s.flags.every(f=>f.count===0));
});
