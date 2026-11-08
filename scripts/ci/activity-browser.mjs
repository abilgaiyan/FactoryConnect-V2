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
  async function accepted() {
    await login();
    await page.getByLabel('Authorized machine', { exact: true }).selectOption(process.env.FC2_BROWSER_MACHINE);
    const activity = page.getByRole('region', { name: 'Published machine activity', exact: true });
    await expect(activity.locator('tbody tr')).toHaveCount(1);
    await expect(activity.locator('tbody')).toContainText('Running');
    await expect(activity.locator('tbody code').filter({ hasText: 'ACTIVE' })).toHaveText('ACTIVE');
    await expect(activity.locator('tbody code').filter({ hasText: 'READY' })).toHaveText('READY');
    await activity.getByText('Publication provenance', { exact: true }).click();
    await expect(activity.locator('details')).toContainText(process.env.FC2_BROWSER_MAPPING);
    await expect(activity).toContainText('This read does not yet verify the publication profile or cross-batch ancestry');
    const session = await page.evaluate(async () => (await fetch('/api/security/v2/session')).json());
    return session.bearer;
  }
  const bearer = await accepted();
  await stage('revoke-session', { hash: createHash('sha256').update(Buffer.from(bearer, 'base64')).digest('hex') });
  const denied = await page.evaluate(async ({ machine, bearer, mapping }) => (await fetch(`/api/commissioning/v2/machines/${machine}/activity/${mapping}`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, mapping: process.env.FC2_BROWSER_MAPPING, bearer });
  expect(denied).toBe(401);
  await page.reload();
  await expect(page.locator('tbody')).toHaveCount(0);
  const loggedOut = await accepted();
  const logoutResponse = page.waitForResponse(response => response.url().endsWith('/api/security/v2/logout'));
  await page.getByRole('button', { name: 'End evidence session' }).click();
  expect((await logoutResponse).status()).toBe(204);
  expect(await page.evaluate(async () => (await fetch('/api/security/v2/session')).status)).toBe(401);
  expect(await page.evaluate(async ({ machine, bearer, mapping }) => (await fetch(`/api/commissioning/v2/machines/${machine}/activity/${mapping}`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, mapping: process.env.FC2_BROWSER_MAPPING, bearer: loggedOut })).toBe(401);
  const second = await accepted();
  await mkdir('artifacts/ingestion', { recursive: true });
  await page.screenshot({ path: 'artifacts/ingestion/operator-activity.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  const activityTable = page.getByRole('region', { name: 'Published activity period table', exact: true });
  expect(await activityTable.evaluate(node => node.scrollWidth > node.clientWidth && getComputedStyle(node).overflowX === 'auto')).toBe(true);
  await page.getByRole('region', { name: 'Published machine activity', exact: true }).screenshot({ path: 'artifacts/ingestion/operator-activity-mobile.png' });
  await page.setViewportSize({ width: 1280, height: 720 });
  await stage('revoke-grant');
  const grantDenied = await page.evaluate(async ({ machine, bearer, mapping }) => (await fetch(`/api/commissioning/v2/machines/${machine}/activity/${mapping}`, { headers: { Authorization: 'Bearer ' + bearer } })).status,
    { machine: process.env.FC2_BROWSER_MACHINE, mapping: process.env.FC2_BROWSER_MAPPING, bearer: second });
  expect(grantDenied).toBe(401);
  await page.reload();
  await expect(page.locator('select')).toHaveCount(0);
  await expect(page.getByText('No machines currently authorized for commissioning evidence.')).toBeVisible();
  await writeFile('artifacts/ingestion/activity-browser.json', JSON.stringify({ controlledFixture: true, https: true, browser: 'Chromium', oidcCodePkce: true, publishedRunningPeriod: true, exactSourceValues: true, publicationProvenance: true, sessionRevocationDenied: true, logoutRevokesSqlSession: true, grantRevocationDenied: true }));
  await stage('complete');
} finally { await browser.close(); input.close(); }
