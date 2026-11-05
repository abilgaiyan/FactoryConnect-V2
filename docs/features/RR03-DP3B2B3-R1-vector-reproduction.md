# DP.3B.2B.3 R1 — Canonical vectors and reproduction

Status: PROPOSED — R1 NOT FROZEN. All data is synthetic codec-fixture data, not factory measurements, approvals or compatibility evidence.

The [JSON vector set](RR03-DP3B2B3-R1-golden-vectors.json) supplies the complete typed fixture, full canonical hex bytes, byte counts and SHA-256 digests. It includes source, definition, admission, receipt target, receipt payload and V2 envelope records, plus the two digest preimages required to reproduce the receipt. UInt64/tick values that could lose JSON numeric precision must be read from decimal strings; fixture observation ticks are a decimal string.

The V2 fixture uses a predetermined 32-byte EE legacy-envelope digest to isolate its wrapper encoding. It does not claim an existing valid acquisition publication. SQL conformance must separately recompute the real legacy digest from actual acquisition payload/revision/position fixtures.

| Vector | Bytes | SHA-256 |
| --- | --- | --- |
| Source | 569 | `655F0040FD8EF3182F4BD1D925E0BA7CB9979F0DB61B40832ADD88AF73637D63` |
| Definition | 317 | `EA6A4721266BA70F17B1BAA2D2CEE5F93222117F71CCF5396ABD86933942BAA1` |
| Admission | 127 | `82BF6548EC1564A781BC1FA14FB2EE0B06B676E6658ED19FDDA644178965DA57` |
| ReceiptTarget | 239 | `7605645680C927D853EDECE046DE87CAFFCD35B558DBE8D76B89A7D6C9262DEF` |
| ElementFingerprintPreimage | 164 | `8DF7629FC4B804DB0868F4FBC660BAA9863AB37FB39D3F3BEE64302F303496EE` |
| ObservationContentPreimage | 89 | `C91498AFACB20F29C4776885385732A33BEF10F782227651EF977145E5152DA1` |
| ReceiptPayload | 261 | `0279CD0E2AB9C5DFA64E6EF6059FAD61085A2D2D011B75ACD4FD2B3492FECE28` |
| EnvelopeV2 | 102 | `EA0CB21465F44A174C4138A42956FB36A96C4883A5460196C55EB17E39D146CC` |

## Executed verification and limits

A Python standard-library fixture encoder generated the bytes. A separately written Node.js v24.19.0 encoder reconstructed all eight records from the fixture and matched their complete bytes, lengths and hashes. Both were authored by the same assistant: this is cross-implementation evidence, not independent human sign-off. The source XML was also parsed with Python ElementTree to confirm its seven-element preorder and well-formedness; that check is not a production XML/schema or receipt verifier.

The JavaScript check additionally reconstructed all ten mutation recipes and matched their mutated byte counts/hashes. This confirms the rejection test INPUTS, not rejection by a production decoder. There is no production receipt implementation, SQL execution or factory acquisition in this deliverable.

## Rejection vectors

Each JSON rejection entry specifies its positive base vector, zero-based byte offset, bytes to delete, replacement hex (which may be empty), final byte length/digest, and required eventual rejection outcome. Apply exactly that patch; other fixture/dependency records remain unchanged. Each entry explicitly names its validation context. N01/N10 test isolated version dispatch; N02–N04 test isolated codec/invariants; N05–N09 use the full linked fixture with unchanged original dependencies. This prevents a parent hash mismatch from being confused with isolated unsupported-version dispatch.

- N01: Source — UnsupportedEncoding.
- N02: Source — Corrupt: truncated declared entity bytes.
- N03: Source — Corrupt: trailing bytes.
- N04: Definition — Corrupt: resource maximum must be positive.
- N05: ReceiptPayload — Corrupt: exact target identity mismatch against ReceiptTarget.
- N06: ReceiptPayload — Corrupt: expected raw row position 1 disagrees; original V2 payload commitment also mismatches.
- N07: ReceiptPayload — Corrupt: source ordinal is 1-based and must resolve to included source element.
- N08: EnvelopeV2 — Corrupt: committed receipt target mismatch.
- N09: EnvelopeV2 — Corrupt: required payload digest missing/truncated.
- N10: EnvelopeV2 — UnsupportedEncoding.

## Reproduction

Copy the following sources to temporary files named generate-r1-vectors.py and verify-r1-vectors.mjs. Run:

```sh
python generate-r1-vectors.py ./r1-fixture-output
node verify-r1-vectors.mjs ./r1-fixture-output/RR03-DP3B2B3-R1-golden-vectors.json
```

Compare the regenerated JSON vector bytes/digests with this PR's JSON, not only the generator's summary. The expected Node result is vectorByteComparisons=8, mutationRecipeComparisons=10, productionDecoderTests=0. These programs are fixture encoders/checkers; they do not implement general record validation, admission, response completeness or publication.

Python source SHA-256: `A2FC2C8383A4EEA0360E49806ED01F5D435D13843AED3290EE42918F15AE64A5`.

```python
import struct,hashlib,json,sys
from pathlib import Path
out=Path(sys.argv[1] if len(sys.argv)>1 else '.')
out.mkdir(parents=True,exist_ok=True)
H=lambda b:hashlib.sha256(b).digest()
class W:
 def __init__(self,domain):self.b=bytearray(domain.encode()+struct.pack('>H',1));self.offsets={}
 def mark(self,k):self.offsets[k]=len(self.b)
 def u8(self,x):self.b+=struct.pack('>B',x)
 def u16(self,x):self.b+=struct.pack('>H',x)
 def u32(self,x):self.b+=struct.pack('>I',x)
 def u64(self,x):self.b+=struct.pack('>Q',x)
 def i64(self,x):self.b+=struct.pack('>q',x)
 def string(self,s):v=s.encode('utf8');self.u32(len(v));self.b+=v
 def optionalstring(self,s):self.u8(s is not None);self.string(s) if s is not None else None
 def optionalu32(self,x):self.u8(x is not None);self.u32(x) if x is not None else None
 def context(self):self.b+=bytes.fromhex('00000000000000000000000000000001');self.string('S');self.string('obs')
 def done(self):return bytes(self.b)
ns='urn:mtconnect.org:MTConnectStreams:2.5'
time='1970-01-01T00:00:00Z';ticks=621355968000000000
body=f'<MTConnectStreams xmlns="{ns}"><Header creationTime="2026-10-17T00:00:00Z" sender="fixture" version="2.5.0" bufferSize="8" instanceId="1" firstSequence="1" lastSequence="1" nextSequence="2"/><Streams><DeviceStream name="D" uuid="U"><ComponentStream component="Controller" name="C" componentId="c"><Events><Execution dataItemId="e" sequence="1" timestamp="{time}">ACTIVE</Execution></Events></ComponentStream></DeviceStream></Streams></MTConnectStreams>'
entity=body.encode();records={};offsets={}
def save(name,w):records[name]=w.done();offsets[name]=w.offsets
w=W('FCRS');w.u16(1);w.u16(200);w.string('application/xml');w.optionalstring(None);w.string('utf-8');w.optionalstring(None);w.string('http-entity/1');w.u32(len(entity));w.u32(len(body));w.u32(len(entity));w.b+=entity;save('Source',w)
w=W('FCRD')
for s in ['fc-mtconnect-traversal/1','1','ep','http://fixture.invalid/','uri-exact/1']:w.string(s)
w.u8(1);w.string('D');w.string(ns);w.b+=bytes([0x11])*32
for s in ['parser/1','iso-utc/1','sql-value/1','ascii-trim-token/1','sample-from/1']:w.string(s)
w.mark('MaxEntityBytes')
for n in [4096,4096,16,128,16,256,65536]:w.u32(n)
w.u32(1);w.string('e');w.string('Execution');w.u8(2);w.u8(2);w.string('execution/1');w.u32(0);save('Definition',w)
w=W('FCRA');w.b+=H(records['Definition']);w.context();w.i64(0);w.u8(0);w.string('test-only/authority');w.u32(1);w.string('fixture-instruction/1');save('Admission',w)
w=W('FCRT');w.context();w.b+=bytes([0x44])*32;w.b+=H(records['Source'])+H(records['Definition'])+H(records['Admission']);w.string('ep');w.string('http://fixture.invalid/sample?from=1');w.string('GET');w.u64(1);w.optionalu32(None);w.optionalstring(None);w.string('verifier/1');save('ReceiptTarget',w)
w=W('FCEL');w.u32(7);w.string(ns);w.string('Execution');w.u32(3)
for name,value in [('dataItemId','e'),('sequence','1'),('timestamp',time)]:w.string('');w.string(name);w.string(value)
w.string('ACTIVE');save('ElementFingerprintPreimage',w)
w=W('FCOV');w.context();w.u64(1);w.u64(1);w.string('mtconnect');w.string('e');w.u8(2);w.u8(1);w.i64(ticks);w.optionalstring('ACTIVE');save('ObservationContentPreimage',w)
w=W('FCRP');w.b+=H(records['ReceiptTarget'])
for n in [1,1,1,2]:w.u64(n)
w.optionalstring('D');w.optionalstring('U');w.u32(7);w.u32(1);w.mark('AccountingSourceOrdinal');w.u32(7);w.string('Execution');w.u8(2);w.string('e');w.u64(1);w.string(time);w.b+=H(records['ElementFingerprintPreimage']);w.u8(1);w.string('e');w.u32(1);w.u32(7);w.u64(1);w.mark('OutputRawPosition');w.u64(1);w.u8(2);w.u8(1);w.i64(ticks);w.optionalstring('ACTIVE');w.b+=H(records['ObservationContentPreimage']);w.u8(1);save('ReceiptPayload',w)
envelope=b'FCAR'+struct.pack('>H',2)+bytes([0xee])*32+H(records['ReceiptTarget'])+H(records['ReceiptPayload']);records['EnvelopeV2']=envelope
fixture={'purpose':'SYNTHETIC CODEC FIXTURE ONLY; no factory measurement or admission','context':{'machine':'00000000-0000-0000-0000-000000000001','site':'S','stream':'obs'},'source':{'entityUtf8':body,'representation':1,'httpStatus':200,'mediaType':'application/xml','declaredCharset':None,'effectiveCharset':'utf-8','originalContentEncoding':None,'decodingRule':'http-entity/1'},'definition':{'profileId':'fc-mtconnect-traversal/1','profileVersion':'1','endpointIdentity':'ep','baseUri':'http://fixture.invalid/','uriRule':'uri-exact/1','matchMode':1,'selector':'D','namespace':ns,'parserArtifactHash':'11'*32,'parserVersion':'parser/1','timestampRule':'iso-utc/1','valueRule':'sql-value/1','trimRule':'ascii-trim-token/1','requestProtocolRule':'sample-from/1','maxima':[4096,4096,16,128,16,256,65536],'bindings':[{'dataItemId':'e','localName':'Execution','category':2,'outputType':2,'semantics':'execution/1'}],'exclusions':[]},'admission':{'effectiveStartTicks':0,'effectiveEndTicks':None,'authority':'test-only/authority','sourceReferences':['fixture-instruction/1']},'receiptTarget':{'publicationIdentity':'44'*32,'endpointIdentity':'ep','uri':'http://fixture.invalid/sample?from=1','method':'GET','requestedCursor':1,'count':None,'path':None,'verifier':'verifier/1'},'receiptPayload':{'header':[1,1,1,2],'selectedName':'D','selectedUuid':'U','traversedElements':7,'accountingSourceOrdinal':7,'bindingReference':'e','outputRawPosition':1,'observationUtcTicks':str(ticks),'outcome':1},'envelopeV2':{'legacyEnvelopeDigest':'EE'*32,'note':'Predetermined fixture digest to isolate V2 encoding; does not assert an existing V1 publication. Runtime must independently recompute this digest from the actual acquisition fields.'}}
negative=[]
def reject(name,base,offset,delete,insert,expected):
 raw=records[base];mutated=raw[:offset]+insert+raw[offset+delete:];negative.append({'id':name,'baseVector':base,'offsetZeroBased':offset,'deleteByteCount':delete,'insertHex':insert.hex().upper(),'mutatedLength':len(mutated),'mutatedSha256':H(mutated).hex().upper(),'validationContext': ('Isolated record version dispatch' if name in ['N01','N10'] else 'Isolated record codec/invariants' if name in ['N02','N03','N04'] else 'Full linked receipt fixture with unchanged original dependencies'), 'expected':expected})
reject('N01','Source',4,2,b'\x00\x02','UnsupportedEncoding')
reject('N02','Source',len(records['Source'])-1,1,b'','Corrupt: truncated declared entity bytes')
reject('N03','Source',len(records['Source']),0,b'\x00','Corrupt: trailing bytes')
reject('N04','Definition',offsets['Definition']['MaxEntityBytes'],4,bytes(4),'Corrupt: resource maximum must be positive')
reject('N05','ReceiptPayload',6,32,bytes(32),'Corrupt: exact target identity mismatch against ReceiptTarget')
reject('N06','ReceiptPayload',offsets['ReceiptPayload']['OutputRawPosition'],8,struct.pack('>Q',2),'Corrupt: expected raw row position 1 disagrees; original V2 payload commitment also mismatches')
reject('N07','ReceiptPayload',offsets['ReceiptPayload']['AccountingSourceOrdinal'],4,bytes(4),'Corrupt: source ordinal is 1-based and must resolve to included source element')
reject('N08','EnvelopeV2',38,32,bytes(32),'Corrupt: committed receipt target mismatch')
reject('N09','EnvelopeV2',len(envelope)-32,32,b'','Corrupt: required payload digest missing/truncated')
reject('N10','EnvelopeV2',4,2,b'\x00\x03','UnsupportedEncoding')
result={'fixture':fixture,'vectors':[{ 'name':k,'byteCount':len(v),'sha256':H(v).hex().upper(),'canonicalHex':v.hex().upper()} for k,v in records.items()],'rejectionVectors':negative}
(out/'RR03-DP3B2B3-R1-golden-vectors.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({k:{'bytes':len(v),'sha256':H(v).hex().upper()} for k,v in records.items()},indent=2))
```

JavaScript source SHA-256: `58B5D68687B5724FFBF90EE99DF73FACC20A155E70961D09A5CDC3167983628E`.

```javascript
import fs from 'node:fs';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const data=JSON.parse(fs.readFileSync(process.argv[2],'utf8')),f=data.fixture;
const sha=b=>crypto.createHash('sha256').update(b).digest();
class Writer {
 p=[];
 constructor(domain,version=1){this.p.push(Buffer.from(domain));this.n(version,2);}
 n(v,size,signed=false){const b=Buffer.alloc(size);if(size===8)b[signed?'writeBigInt64BE':'writeBigUInt64BE'](BigInt(v));else b[signed?'writeIntBE':'writeUIntBE'](v,0,size);this.p.push(b);}
 raw(v){this.p.push(Buffer.from(v));}
 str(v){const b=Buffer.from(v,'utf8');this.n(b.length,4);this.raw(b);}
 optstr(v){this.n(v==null?0:1,1);if(v!=null)this.str(v);}
 opt32(v){this.n(v==null?0:1,1);if(v!=null)this.n(v,4);}
 context(){this.raw(Buffer.from(f.context.machine.replaceAll('-',''),'hex'));this.str(f.context.site);this.str(f.context.stream);}
 done(){return Buffer.concat(this.p);}
}
const output=new Map();function save(name,w){output.set(name,w.done());}
let w=new Writer('FCRS'),s=f.source,body=Buffer.from(s.entityUtf8,'utf8');
w.n(s.representation,2);w.n(s.httpStatus,2);w.str(s.mediaType);w.optstr(s.declaredCharset);w.str(s.effectiveCharset);w.optstr(s.originalContentEncoding);w.str(s.decodingRule);w.n(body.length,4);w.n(s.entityUtf8.length,4);w.n(body.length,4);w.raw(body);save('Source',w);
let d=f.definition;w=new Writer('FCRD');
for(const x of [d.profileId,d.profileVersion,d.endpointIdentity,d.baseUri,d.uriRule])w.str(x);
w.n(d.matchMode,1);w.str(d.selector);w.str(d.namespace);w.raw(Buffer.from(d.parserArtifactHash,'hex'));
for(const x of [d.parserVersion,d.timestampRule,d.valueRule,d.trimRule,d.requestProtocolRule])w.str(x);
for(const x of d.maxima)w.n(x,4);w.n(d.bindings.length,4);
for(const b of d.bindings){w.str(b.dataItemId);w.str(b.localName);w.n(b.category,1);w.n(b.outputType,1);w.str(b.semantics);}w.n(0,4);save('Definition',w);
let a=f.admission;w=new Writer('FCRA');w.raw(sha(output.get('Definition')));w.context();w.n(a.effectiveStartTicks,8,true);w.n(0,1);w.str(a.authority);w.n(a.sourceReferences.length,4);for(const x of a.sourceReferences)w.str(x);save('Admission',w);
let t=f.receiptTarget;w=new Writer('FCRT');w.context();w.raw(Buffer.from(t.publicationIdentity,'hex'));for(const name of ['Source','Definition','Admission'])w.raw(sha(output.get(name)));w.str(t.endpointIdentity);w.str(t.uri);w.str(t.method);w.n(t.requestedCursor,8);w.opt32(t.count);w.optstr(t.path);w.str(t.verifier);save('ReceiptTarget',w);
const ordinal=7,timestamp='1970-01-01T00:00:00Z',ticks=BigInt(f.receiptPayload.observationUtcTicks);
w=new Writer('FCEL');w.n(ordinal,4);w.str(d.namespace);w.str('Execution');w.n(3,4);
for(const [name,value] of [['dataItemId','e'],['sequence','1'],['timestamp',timestamp]]){w.str('');w.str(name);w.str(value);}w.str('ACTIVE');save('ElementFingerprintPreimage',w);
w=new Writer('FCOV');w.context();w.n(1,8);w.n(1,8);w.str('mtconnect');w.str('e');w.n(2,1);w.n(1,1);w.n(ticks,8,true);w.optstr('ACTIVE');save('ObservationContentPreimage',w);
w=new Writer('FCRP');w.raw(sha(output.get('ReceiptTarget')));for(const x of f.receiptPayload.header)w.n(x,8);w.optstr('D');w.optstr('U');w.n(7,4);w.n(1,4);w.n(ordinal,4);w.str('Execution');w.n(2,1);w.str('e');w.n(1,8);w.str(timestamp);w.raw(sha(output.get('ElementFingerprintPreimage')));w.n(1,1);w.str('e');w.n(1,4);w.n(ordinal,4);w.n(1,8);w.n(1,8);w.n(2,1);w.n(1,1);w.n(ticks,8,true);w.optstr('ACTIVE');w.raw(sha(output.get('ObservationContentPreimage')));w.n(1,1);save('ReceiptPayload',w);
w=new Writer('FCAR',2);w.raw(Buffer.from(f.envelopeV2.legacyEnvelopeDigest,'hex'));w.raw(sha(output.get('ReceiptTarget')));w.raw(sha(output.get('ReceiptPayload')));save('EnvelopeV2',w);
for(const v of data.vectors){const actual=output.get(v.name);assert.equal(actual.length,v.byteCount);assert.equal(actual.toString('hex').toUpperCase(),v.canonicalHex);assert.equal(sha(actual).toString('hex').toUpperCase(),v.sha256);}
for(const n of data.rejectionVectors){const b=output.get(n.baseVector),m=Buffer.concat([b.subarray(0,n.offsetZeroBased),Buffer.from(n.insertHex,'hex'),b.subarray(n.offsetZeroBased+n.deleteByteCount)]);assert.equal(m.length,n.mutatedLength);assert.equal(sha(m).toString('hex').toUpperCase(),n.mutatedSha256);}
console.log(JSON.stringify({runtime:process.version,vectorByteComparisons:output.size,mutationRecipeComparisons:data.rejectionVectors.length,productionDecoderTests:0}));
```

No vectors from the frozen assessment V1 contract were changed. Receipt golden vectors, SQL closure and operational gates still require review before R1 freeze and separate implementation authorization.
