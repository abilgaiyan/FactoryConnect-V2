import { chromium, expect } from '@playwright/test';
import { createHash } from 'node:crypto';
import { createInterface } from 'node:readline';
import { mkdir, writeFile } from 'node:fs/promises';
const input = createInterface({ input: process.stdin });
const pending = [];
input.on('line', line => pending.shift()?.(line));
async function stage(name, extra = {}) {
  const continued = new Promise(resolve => pending.push(resolve));
  process.stdout.write(JSON.stringify({ stage: name, ...extra }) + '\n');
  await continued;
}
const browser = await chromium.launch({ args: ['--ignore-certificate-errors-spki-list=' + process.env.FC2_BROWSER_SPKI] });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  const url = process.env.FC2_BROWSER_URL;
  async function login() {
    await page.goto(url + '/operator-evidence');
    await page.getByRole('link', { name: 'Sign in to view machine evidence' }).click();
    await page.getByLabel('Password').fill(process.env.FC2_BROWSER_PASSWORD);
    await page.getByRole('button', { name: 'Provider sign in' }).click();
  }
  for (const next of ['bad-nonce', 'expired', 'bad-signature', 'bad-issuer', 'valid']) {
    await login();
    await expect(page.locator('body')).toContainText('Operator identity verification failed.');
    await stage('invalid-denied', { next });
  }
  async function accepted() {
    await login();
    await page.getByLabel('Authorized machine', { exact: true }).selectOption(process.env.FC2_BROWSER_MACHINE);
    await expect(page.getByText(/^No published activity history for this machine/)).toBeVisible();
    await expect(page.locator('tbody code').filter({ hasText: '00042' })).toHaveText(' 00042 ');
    await expect(page.locator('details').first()).toContainText('Source document SHA-256');
    const session = await page.evaluate(async () => (await fetch('/api/security/v2/session')).json());
    return session.bearer;
  }
  const bearer = await accepted();
  // The bounded machine list must disclose incomplete coverage conservatively.
  // Authenticated continuation requests must fail explicitly, never replay page one.
  const discovery = await page.evaluate(async bearer => {
    const headers = { Authorization: 'Bearer ' + bearer };
    const first = await fetch('/api/commissioning/v2/evidence-machines', { headers });
    const payload = await first.json();
    const forged = [];
    for (const key of ['cursor', 'continuationToken']) {
      const response = await fetch('/api/commissioning/v2/evidence-machines?' + key + '=forged', { headers });
      forged.push({ status: response.status, payload: await response.json() });
    }
    return { status: first.status, payload, complete: first.headers.get('x-result-complete'), forged };
  }, bearer);
  expect(discovery.status).toBe(200);
  expect(Array.isArray(discovery.payload.machines)).toBe(true);
  expect(typeof discovery.payload.isComplete).toBe('boolean');
  expect(discovery.payload.continuationSupported).toBe(false);
  expect(discovery.payload.maximumReturned).toBe(100);
  expect(discovery.complete).toBe(String(discovery.payload.isComplete));
  if (discovery.payload.machines.length < 100) expect(discovery.payload.isComplete).toBe(true);
  expect(discovery.payload.machines.length).toBeLessThanOrEqual(100);
  for (const response of discovery.forged) {
    expect(response.status).toBe(400);
    expect(response.payload.error).toContain('continuation is not yet supported');
  }
  await stage('revoke-session', { hash: createHash('sha256').update(Buffer.from(bearer, 'base64')).digest('hex') });
  const denied = await page.evaluate(async ({ machine, bearer }) => (await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, bearer });
  expect(denied).toBe(401);
  await page.reload();
  await expect(page.locator('tbody')).toHaveCount(0);
  const loggedOut = await accepted();
  const logoutResponse = page.waitForResponse(response => response.url().endsWith('/api/security/v2/logout'));
  await page.getByRole('button', { name: 'End evidence session' }).click();
  expect((await logoutResponse).status()).toBe(204);
  expect(await page.evaluate(async () => (await fetch('/api/security/v2/session')).status)).toBe(401);
  expect(await page.evaluate(async ({ machine, bearer }) => (await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, bearer: loggedOut })).toBe(401);
  // Force a real SQL connection failure after OIDC sign-in. The API must still
  // expire its encrypted local cookie and must not claim durable revocation.
  const outageBearer = await accepted();
  await stage('sql-outage-start');
  const outageDiagnostic = await page.evaluate(async ({ machine, bearer }) => {
    const response = await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, {
      headers: { Authorization: 'Bearer ' + bearer }
    });
    return { status: response.status, body: await response.json() };
  }, { machine: process.env.FC2_BROWSER_MACHINE, bearer: outageBearer });
  expect(outageDiagnostic.status).toBe(503);
  expect(outageDiagnostic.body.traceId).toBeTruthy();
  await stage('verify-authority-diagnostic', { traceId: outageDiagnostic.body.traceId });
  const outageLogout = await page.evaluate(async () => (await fetch('/api/security/v2/logout', { method: 'POST' })).status);
  expect(outageLogout).toBe(503);
  expect(await page.evaluate(async () => (await fetch('/api/security/v2/session')).status)).toBe(401);
  await stage('sql-outage-end');
  expect(await page.evaluate(async () => (await fetch('/api/security/v2/session')).status)).toBe(401);
  // The bearer may still be valid on the SQL server: local logout is not revocation.
  const stillValid = await page.evaluate(async ({ machine, bearer }) =>
    (await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, {
      headers: { Authorization: 'Bearer ' + bearer }
    })).status, { machine: process.env.FC2_BROWSER_MACHINE, bearer: outageBearer });
  expect(stillValid).toBe(200);
  await stage('revoke-outage-session', { hash: createHash('sha256').update(Buffer.from(outageBearer, 'base64')).digest('hex') });
  const outageRevoked = await page.evaluate(async ({ machine, bearer }) =>
    (await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, {
      headers: { Authorization: 'Bearer ' + bearer }
    })).status, { machine: process.env.FC2_BROWSER_MACHINE, bearer: outageBearer });
  expect(outageRevoked).toBe(401);
  const second = await accepted();
  await mkdir('artifacts/ingestion', { recursive: true });
  await page.screenshot({ path: 'artifacts/ingestion/operator-evidence.png', fullPage: true });
  await stage('revoke-grant');
  const grantDenied = await page.evaluate(async ({ machine, bearer }) => (await fetch(`/api/commissioning/v2/machines/${machine}/evidence`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, bearer: second });
  expect(grantDenied).toBe(401);
  await page.reload();
  await expect(page.locator('select')).toHaveCount(0);
  await expect(page.getByText('No machines currently authorized for commissioning evidence.')).toBeVisible();
  // Spoofed X-Forwarded-Proto must not influence HTTPS security decisions.
  const spoofed = await page.evaluate(async () => {
    const response = await fetch('/api/security/v2/session', { headers: { 'X-Forwarded-Proto': 'http' } });
    return { status: response.status, hsts: response.headers.get('strict-transport-security'),
      csp: response.headers.get('content-security-policy') };
  });
  // Revoked machine grant does not revoke the still-valid operator SQL session.
  expect(spoofed.status).toBe(200);
  expect(spoofed.hsts).toContain('max-age=');
  expect(spoofed.csp).toContain("object-src 'none'");
  // Burst unauthenticated requests to a SQL-backed route; 429 must occur
  // before any authority execution, regardless of forged forwarded IPs.
  const burst = await page.evaluate(async () => {
    const codes = [];
    for (let i = 0; i < 135; i++) {
      const response = await fetch('/api/commissioning/v2/evidence-machines', {
        headers: { 'X-Forwarded-For': `203.0.113.${i % 250}` }
      });
      codes.push(response.status);
    }
    return codes;
  });
  expect(burst).toContain(429);
  await writeFile('artifacts/ingestion/operator-browser.json', JSON.stringify({ controlledFixture: true, https: true, browser: 'Chromium', oidcCodePkce: true, invalidAudienceNonceExpirySignatureIssuerDenied: true, literalValuePreserved: true, sessionRevocationDenied: true, logoutRevokesSqlSession: true, outageLogoutClearsLocalCookieWithoutSqlRevocation: true, grantRevocationDenied: true }));
  await stage('complete');
} finally { await browser.close(); input.close(); }
