# DP.3B.2B.2 — Second-encoder vector verification

## Formal project sign-off — 17 October 2026

Ajay Bilgaiyan explicitly approved DP.3B.2B.2 as DESIGN FROZEN in the project conversation at 10:49 IST, on reviewed baseline a783ade3b81e6e5df9ef6c223f02d225b535ff70. This records design approval only. PR #115 must remain draft and unmerged; implementation requires a separately reviewed scope and authorization. No Established promotion, DP.3C wiring, Availability activation or deployment is authorized. The 40 scenarios remain specifications, not passed tests. The next permitted work is preparing the response-completeness receipt implementation scope.


Status: vector reproduction completed; DESIGN FROZEN by explicit project sign-off on 17 October 2026.

Verified specification: PR 115 at 284fbe2aee19218c7c9e2d7427f4e7a33bdb3533, [V1 encoding appendix](RR03-DP3B2B2-v1-encoding.md).

A separately written JavaScript encoder reconstructed all target/result bytes from the appendix field tables and synthetic fixture description. It did not call, import or reuse the Python generator. The expected byte arrays and hashes were read from the published appendix only after the independent construction functions were defined. This is independence of implementation/language, not an independent human review: both were prepared by the same assistant. Formal reviewer sign-off remains required.

Runtime: Node.js v24.19.0. SHA-256 of the exact verifier source below: `72941396B4637ED310C035500C06A9F9E66ACA57767AE6AC8CB7290427BD9E37`.

| Vector | Length | SHA-256 | Comparison |
| --- | --- | --- | --- |
| A.target | 826 | `4D26F4050701BF69D3A4FBFB0123FD3527373D084A402D6B4C2452310722D0BA` | All bytes, length and hash match |
| B.target | 826 | `6074DFB221CC8E2F2914680D99EE08C876611EAE57E9B290078BAF8D1B02E6CA` | All bytes, length and hash match |
| A.result | 903 | `7BE91D4A6F30AF6BF80E4F3774549329233DA0DDB270E2F3504683C0BB3E0CAB` | All bytes, length and hash match |

The second encoder also confirms that changing the pinned temporal policy version/digest changes the target hash. No vector correction was needed. The canonical target/result bytes and documented hashes are unchanged.

## Reproduction

Save the following source as a temporary `verify115-second.mjs`, then run:

```sh
node verify115-second.mjs docs/features/RR03-DP3B2B2-v1-encoding.md
```

It must exit 0 and report three exact matches. Any byte, length or digest difference raises an assertion. The source is a fixture encoder for this verification only; it is not a general decoder, continuity evaluator or production codec.

```javascript
import fs from 'node:fs';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const hash = b => crypto.createHash('sha256').update(b).digest();
class Writer {
  parts = [];
  bytes(b) { this.parts.push(Buffer.from(b)); }
  integer(n, size, signed = false) {
    const b = Buffer.alloc(size);
    if (size === 8) b[signed ? 'writeBigInt64BE' : 'writeBigUInt64BE'](BigInt(n));
    else b[signed ? 'writeIntBE' : 'writeUIntBE'](n, 0, size);
    this.bytes(b);
  }
  string(s) { const b = Buffer.from(s, 'utf8'); this.integer(b.length, 4); this.bytes(b); }
  finish() { return Buffer.concat(this.parts); }
}
const guid = Buffer.from('00000000000000000000000000000001', 'hex');
function buildTarget(policyVersion, policyDigestByte) {
  const w = new Writer();
  const fields = [
    ['bytes', Buffer.from('FCCT')], ['u16', 1], ['u16', 1], ['bytes', guid],
    ['string', 'obs'], ['string', 'S'], ['i32', 0], ['string', 'a'], ['string', 's'],
    ['i64', 0], ['i64', 300], ['i64', 100], ['i64', 200],
    ['string', '1'], ['string', '1'], ['string', 'UTC'], ['string', '1'],
    ['bytes', Buffer.alloc(32, 0xaa)], ['i64', 1], ['bytes', Buffer.alloc(32, 0xbb)],
    ['byte', 1], ['byte', 0], ['byte', 0], ['string', 'agg'], ['bytes', guid],
    ['string', 'metric'], ['u64', 1]
  ];
  for (const [type, value] of fields) {
    if (type === 'bytes') w.bytes(value);
    else if (type === 'string') w.string(value);
    else w.integer(value, type === 'byte' ? 1 : Number(type.slice(1)) / 8, type.startsWith('i'));
  }
  for (let kind = 1; kind <= 14; kind++) {
    w.integer(kind, 2);
    w.integer(kind === 4 ? 1 : 2, 1);
    if (kind === 4) { w.string('P'); w.string(policyVersion); w.bytes(Buffer.alloc(32, policyDigestByte)); }
    else { w.string(`k${String(kind).padStart(2, '0')}`); w.bytes(Buffer.alloc(32)); }
  }
  for (let i = 0; i < 3; i++) w.string('1');
  return w.finish();
}
function buildResult(target) {
  const w = new Writer();
  w.bytes(Buffer.from('FCCR')); w.integer(1, 2);
  w.integer(target.length, 4); w.bytes(target); w.bytes(hash(target));
  w.integer(1, 1); w.integer(0, 1); w.integer(0, 1); // Missing, axes NotEvaluated
  w.integer(0, 1); w.integer(0, 1); // absent durations
  w.integer(10, 2); w.integer(1, 4); // primary reason, diagnostic count
  w.integer(10, 2); w.integer(5, 2); w.string('k05'); w.integer(0, 1);
  for (let i = 0; i < 3; i++) w.integer(0, 4);
  return w.finish();
}
const text = fs.readFileSync(process.argv[2], 'utf8');
const pattern = /### (A\.target|B\.target|A\.result)\n\nLength: (\d+) bytes\. SHA-256: `([A-F0-9]+)`\.\n\n```text\n([A-F0-9\n]+)\n```/g;
const expected = new Map([...text.matchAll(pattern)].map(m => [m[1], { length: Number(m[2]), digest: m[3], bytes: Buffer.from(m[4].replaceAll('\n', ''), 'hex') }]));
assert.equal(expected.size, 3);
const a = buildTarget('1', 0x11), b = buildTarget('2', 0x22);
const generated = new Map([['A.target', a], ['B.target', b], ['A.result', buildResult(a)]]);
const report = [];
for (const [name, actual] of generated) {
  const wanted = expected.get(name);
  assert.equal(actual.length, wanted.length);
  assert.deepEqual(actual, wanted.bytes);
  assert.equal(hash(actual).toString('hex').toUpperCase(), wanted.digest);
  report.push({ name, bytes: actual.length, sha256: wanted.digest, exactMatch: true });
}
assert.notDeepEqual(hash(a), hash(b));
console.log(JSON.stringify({ runtime: process.version, results: report }, null, 2));
```

## Limits and approval boundary

This verifies three positive byte vectors by a second encoding implementation. It does not execute the six rejection vectors, implement all primitive validation rules, run the 40 continuity scenarios, verify real authority admissions, prove SQL publication conformance or establish acquisition/state coverage. No production implementation or Established promotion is authorized.

The design-freeze gate now has reproducible second-encoder evidence for review. Only explicit sign-off can freeze the contract; this report and green documentation CI cannot substitute for that decision. Prerequisite receipt, historical mapping/state authority and boundary publication contracts remain separate work.
