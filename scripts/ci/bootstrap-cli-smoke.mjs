import assert from 'node:assert/strict';
import { constants, generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const directory = mkdtempSync(join(tmpdir(), 'fc2-bootstrap-cli-'));
const dotnet = process.env.FC2_DOTNET ?? 'dotnet';
let checks = 0;
function run(args, expected = 0) {
  const result = spawnSync(dotnet, ['run', '--project', 'tools/FactoryConnect.SecurityBootstrap', '--configuration', 'Release', '--no-build', '--', ...args], { encoding: 'utf8', timeout: 60000 });
  assert.equal(result.error, undefined);
  assert.equal(result.status, expected, `CLI exit ${result.status}: ${result.stderr}`);
  checks++;
}
try {
  // Synthetic private keys stay in this test process. The online tool receives only public keys and signatures.
  const first = generateKeyPairSync('rsa', { modulusLength: 3072 });
  const second = generateKeyPairSync('rsa', { modulusLength: 3072 });
  const issuer = 'https://idp.fixture/tenant';
  const key = (pair, subject) => ({ keyId: randomUUID(), custodianId: randomUUID(), issuer, subject,
    publicKeySpki: pair.publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
    validFrom: new Date(Date.now() - 86400000).toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() });
  const keys = [key(first, 'custodian-a'), key(second, 'custodian-b')];
  const manifest = join(directory, 'public-manifest.json');
  const payload = join(directory, 'payload.json');
  const canonical = join(directory, 'canonical.bin');
  const reproduced = join(directory, 'reproduced.bin');
  const signed = join(directory, 'signed.json');
  writeFileSync(manifest, JSON.stringify({ installationId: randomUUID(), allowedAdministratorIssuer: issuer, keys }));
  const prepare = ['prepare', manifest, randomUUID(), 'administrator', keys[0].keyId, keys[1].keyId, 'isolated CLI acceptance', payload, canonical];
  run(prepare);
  run(['canonicalize', payload, reproduced]);
  assert.deepEqual(readFileSync(canonical), readFileSync(reproduced)); checks++;
  const data = readFileSync(canonical);
  const proof = (privateKey) => sign('sha256', data, { key: privateKey, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 });
  const firstProof = join(directory, 'approver.sig'); const secondProof = join(directory, 'executor.sig');
  writeFileSync(firstProof, proof(first.privateKey)); writeFileSync(secondProof, proof(second.privateKey));
  run(['assemble', manifest, payload, firstProof, secondProof, signed]);
  const envelope = JSON.parse(readFileSync(signed, 'utf8'));
  assert.equal(envelope.signatures.length, 2); assert.equal(envelope.payload.profile, 'security-administrator-v1'); checks++;
  const before = readFileSync(payload);
  run(prepare, 1); assert.deepEqual(readFileSync(payload), before); checks++;
  const changed = join(directory, 'changed.json'); const invalid = join(directory, 'invalid.json');
  writeFileSync(changed, JSON.stringify({ ...envelope.payload, administratorSubject: 'changed' }));
  run(['assemble', manifest, changed, firstProof, secondProof, invalid], 1);
  assert.equal(existsSync(invalid), false); checks++;
  writeFileSync(changed, JSON.stringify({ ...envelope.payload, unknownAuthority: true }));
  run(['canonicalize', changed, invalid], 1); assert.equal(existsSync(invalid), false); checks++;
  const original = JSON.stringify(envelope.payload);
  const duplicate = original.replace('"administratorSubject":"administrator"', '"administratorSubject":"visible","administratorSubject":"administrator"');
  assert.notEqual(duplicate, original); writeFileSync(changed, duplicate);
  run(['canonicalize', changed, invalid], 1); assert.equal(existsSync(invalid), false); checks++;
  writeFileSync(changed, Buffer.from([0xff]));
  run(['canonicalize', changed, invalid], 1); assert.equal(existsSync(invalid), false); checks++;
  // Runtime credential enrollment: canonical payload is signed by both custodians
  // and independently by the runtime key holder. No database is contacted.
  const runtime = generateKeyPairSync('rsa', { modulusLength: 3072 });
  const runtimePublic = join(directory, 'runtime-public.pem');
  writeFileSync(runtimePublic, runtime.publicKey.export({ type: 'spki', format: 'pem' }));
  const runtimePayload = join(directory, 'runtime-payload.json');
  const runtimeCanonical = join(directory, 'runtime-canonical.bin');
  const runtimeEnvelope = join(directory, 'runtime-envelope.json');
  const runtimeSignatures = [join(directory, 'runtime-approver.sig'), join(directory, 'runtime-executor.sig'), join(directory, 'runtime-possession.sig')];
  run(['runtime-prepare', manifest, randomUUID(), 'administrator', randomUUID(), 'urn:fc2:dev',
    'simulator-runtime', runtimePublic, keys[0].keyId, keys[1].keyId, '1',
    'Local simulated acquisition runtime', runtimePayload, runtimeCanonical]);
  const runtimeBytes = readFileSync(runtimeCanonical);
  for (const [index, pair] of [first, second, runtime].entries()) {
    writeFileSync(runtimeSignatures[index], sign('sha256', runtimeBytes, {
      key: pair.privateKey, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32
    }));
  }
  run(['runtime-assemble', manifest, runtimePayload, ...runtimeSignatures, runtimeEnvelope]);
  const runtimeRequest = JSON.parse(readFileSync(runtimeEnvelope, 'utf8'));
  assert.equal(runtimeRequest.payload.action, 'enroll');
  assert.equal(runtimeRequest.signatures.length, 2);
  assert.ok(runtimeRequest.possessionSignature); checks++;
  const tamperedSignature = join(directory, 'tampered-possession.sig');
  writeFileSync(tamperedSignature, Buffer.alloc(384));
  run(['runtime-assemble', manifest, runtimePayload, runtimeSignatures[0], runtimeSignatures[1],
    tamperedSignature, join(directory, 'invalid-runtime-envelope.json')], 1);
  checks++;
  console.log(`PASS: ${checks} bootstrap CLI checks; no SQL access or private-key files.`);
} finally {
  rmSync(directory, { recursive: true, force: true });
}
