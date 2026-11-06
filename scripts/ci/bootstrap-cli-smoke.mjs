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
  console.log(`PASS: ${checks} bootstrap CLI checks; no SQL access or private-key files.`);
} finally {
  rmSync(directory, { recursive: true, force: true });
}
