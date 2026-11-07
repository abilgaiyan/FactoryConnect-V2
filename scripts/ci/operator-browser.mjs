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
  for (const next of ['bad-nonce', 'expired', 'valid']) {
    await login();
    await expect(page.locator('body')).toContainText('Operator identity verification failed.');
    await stage('invalid-denied', { next });
  }
  async function accepted() {
    await login();
    await page.getByLabel('Authorized machine', { exact: true }).selectOption(process.env.FC2_BROWSER_MACHINE);
    await expect(page.locator('tbody code').filter({ hasText: '00042' })).toHaveText(' 00042 ');
    await expect(page.locator('details').first()).toContainText('Source document SHA-256');
    const session = await page.evaluate(async () => (await fetch('/api/security/v2/session')).json());
    return session.bearer;
  }
  const bearer = await accepted();
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
  await writeFile('artifacts/ingestion/operator-browser.json', JSON.stringify({ controlledFixture: true, https: true, browser: 'Chromium', oidcCodePkce: true, invalidAudienceNonceExpiryDenied: true, literalValuePreserved: true, sessionRevocationDenied: true, logoutRevokesSqlSession: true, grantRevocationDenied: true }));
  await stage('complete');
} finally { await browser.close(); input.close(); }
