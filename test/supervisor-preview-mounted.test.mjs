import assert from "node:assert/strict";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";
import React, { act } from "react";
import { createServer } from "vite";
import { mountInDom } from "./dom-test-harness.mjs";
const vite = await createServer({ root: fileURLToPath(new URL("../", import.meta.url)), server: { middlewareMode: true }, appType: "custom", logLevel: "silent" });
const { default: SupervisorDeskPreview } = await vite.ssrLoadModule("/src/presentation/SupervisorDeskPreview.tsx");
after(() => vite.close());
function button(h, text) { return [...h.document.querySelectorAll("button")].find(b => b.textContent === text); }
function select(h, text) { return [...h.document.querySelectorAll("label")].find(l => l.textContent.startsWith(text)).querySelector("select"); }
async function choose(h, text, value) { await act(async () => { const element = select(h, text); element.value = value; element.dispatchEvent(new h.window.Event("change", { bubbles: true })); }); }
test("four views use sample data only and no network or browser storage", async t => {
  const original = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => { calls++; throw Error("Preview must not fetch"); };
  t.after(() => { globalThis.fetch = original; });
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  assert.match(h.document.body.textContent, /Sample data only/);
  assert.match(h.document.body.textContent, /No authentication, server writes/);
  await h.click(button(h, "Event explanations"));
  await h.click(button(h, "Inspect event 1"));
  assert.match(h.document.body.textContent, /Original publication revision/);
  assert.match(h.document.body.textContent, /9007199254740993/);
  await h.click(button(h, "Create independent preview thread"));
  await h.click(button(h, "Save preview revision"));
  await h.click(button(h, "Shift summary"));
  assert.match(h.document.body.textContent, /Events and explanations are separate counts/);
  await h.click(button(h, "Reviewer queue"));
  assert.equal(button(h, "Simulate explanation acceptance").disabled, true);
  assert.equal(calls, 0);
  assert.equal(h.window.localStorage.length, 0);
});
test("unsaved note cannot silently attach to another event or shift and discard retains saved content", async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await h.click(button(h, "Event explanations")); await h.click(button(h, "Inspect event 1"));
  await h.click(button(h, "Create independent preview thread"));
  const note = h.document.querySelector("textarea"); await act(async () => {
    Object.getOwnPropertyDescriptor(h.window.HTMLTextAreaElement.prototype, "value").set.call(note, "Observed vibration; no cause established");
    note.dispatchEvent(new h.window.Event("input", { bubbles: true }));
  });
  assert.equal(button(h, "Inspect event 2").disabled, true);
  assert.equal(select(h, "Shift occurrence").matches(":disabled"), true);
  assert.equal(button(h, "Reviewer queue").disabled, true);
  assert.equal(button(h, "Simulate submission of saved draft").disabled, true);
  await h.click(button(h, "Discard unsaved input"));
  assert.equal(h.document.querySelector("textarea").value, "");
  assert.equal(button(h, "Inspect event 2").disabled, false);
});
test("unavailable evidence blocks creation and displays retained evidence honestly", async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await h.click(button(h, "Event explanations")); await h.click(button(h, "Inspect event 1"));
  await choose(h, "Simulate evidence read", "unavailable");
  assert.match(h.document.body.textContent, /retained sample evidence, not a fresh resolved result/);
  assert.equal(button(h, "Create independent preview thread").disabled, true);
});
test("review persona accepts only a submitted independent explanation and input remains creator-only", async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await choose(h, "Preview persona", "sample-reviewer"); await h.click(button(h, "Reviewer queue"));
  await h.click(button(h, "Inspect evidence and revision"));
  assert.equal(h.document.querySelector("textarea").matches(":disabled"), true);
  await h.click(button(h, "Reviewer queue")); await h.click(button(h, "Simulate explanation acceptance"));
  assert.match(h.document.body.textContent, /No submitted sample revisions/);
  await h.click(button(h, "Shift summary"));
  assert.match(h.document.body.textContent, /1 Explanation accepted/);
});

test("history labels distinguish saved, submission and acceptance times; note is optional", async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await h.click(button(h, "Event explanations")); await h.click(button(h, "Inspect event 1"));
  await h.click(button(h, "Create independent preview thread"));
  assert.match(h.document.body.textContent, /Note \(optional\)/);
  assert.equal(h.document.querySelector("textarea").value, "");
  await h.click(button(h, "Simulate submission of saved draft"));
  assert.match(h.document.body.textContent, /Submitted \(preview\):/);
  await choose(h, "Preview persona", "sample-reviewer");
  await h.click(button(h, "Reviewer queue"));
  const accept = [...h.document.querySelectorAll("article")].find(a => a.textContent.includes("preview-thread-4"))
    .querySelectorAll("button")[1];
  await h.click(accept);
  await h.click(button(h, "Event explanations"));
  await h.click([...h.document.querySelectorAll("button")].find(b => b.textContent.startsWith("preview-thread-4 ·")));
  assert.match(h.document.body.textContent, /Revision saved \(preview\):/);
  assert.match(h.document.body.textContent, /Submitted \(preview\):/);
  assert.match(h.document.body.textContent, /Explanation accepted \(preview\):/);
  assert.match(h.document.body.textContent, /Browser memory is not durable history/);
});

test('production form is separate, temporary and machine-specific; history continues across shifts', async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await h.click(button(h, 'Production Details'));
  assert.match(h.document.body.textContent, /Select one machine/);
  await choose(h, 'Machine scope', '11111111-1111-1111-1111-111111111111');
  assert.match(h.document.body.textContent, /not verified against master data/);
  assert.match(h.document.body.textContent, /Not provided/);
  const machineInput = [...h.document.querySelectorAll('label')].find(l => l.textContent === 'Machine').querySelector('input');
  assert.equal(machineInput.readOnly, true);
  async function enter(label, value) {
    const el = [...h.document.querySelectorAll('label')].find(l => l.textContent.startsWith(label)).querySelector('input');
    await act(async () => {
      Object.getOwnPropertyDescriptor(h.window.HTMLInputElement.prototype, 'value').set.call(el, value);
      el.dispatchEvent(new h.window.Event('input', { bubbles: true }));
    });
  }
  await enter('Production order / job number', 'JOB-01');
  await enter('Effective from', '2026-10-28T07:30');
  assert.equal(select(h, 'Machine scope').matches(':disabled'), true);
  await h.click(button(h, 'Save temporary production details'));
  assert.match(h.document.body.textContent, /Backdated declaration/);
  await h.click(h.document.querySelector('input[type=checkbox]'));
  await h.click(button(h, 'Save temporary production details'));
  assert.match(h.document.body.textContent, /saved in browser memory only/);
  await choose(h, 'Shift occurrence', 'sample-s2');
  assert.match(h.document.body.textContent, /JOB-01/);
  assert.match(h.document.body.textContent, /Declaration history · 1 entries/);
  await choose(h, 'Preview persona', 'sample-reviewer');
  assert.match(h.document.body.textContent, /Declaration history · 1 entries/);
  await choose(h, 'Machine scope', '22222222-2222-2222-2222-222222222222');
  assert.match(h.document.body.textContent, /No temporary entries/);
  assert.equal(h.window.localStorage.length, 0);
});

test('shift targets preserve draft revisions, scope and missing actual without invented achievement', async t => {
  const h = await mountInDom(React.createElement(SupervisorDeskPreview)); t.after(() => h.dispose());
  await h.click(button(h, 'Shift Targets'));
  assert.match(h.document.body.textContent, /Select one machine/);
  await choose(h, 'Machine scope', '11111111-1111-1111-1111-111111111111');
  assert.equal(button(h, 'Save temporary target revision').matches(':disabled'), true);
  await choose(h, 'Preview persona', 'sample-reviewer');
  assert.match(h.document.body.textContent, /Target not provided/);
  async function enter(label, value, tag = 'input') {
    const el = [...h.document.querySelectorAll('label')].find(l => l.textContent.startsWith(label)).querySelector(tag);
    await act(async () => {
      const prototype = tag === 'textarea' ? h.window.HTMLTextAreaElement.prototype : h.window.HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(prototype, 'value').set.call(el, value);
      el.dispatchEvent(new h.window.Event('input', { bubbles: true }));
    });
  }
  await enter('Target quantity', '-1');
  await h.click(button(h, 'Save temporary target revision'));
  assert.match(h.document.body.textContent, /nonnegative target/);
  assert.equal(select(h, 'Shift occurrence').matches(':disabled'), true);
  await enter('Target quantity', '400');
  await enter('Quantity unit', '');
  await h.click(button(h, 'Save temporary target revision'));
  assert.match(h.document.body.textContent, /explicit quantity unit/);
  await enter('Quantity unit', 'pieces');
  await h.click(button(h, 'Save temporary target revision'));
  assert.match(h.document.body.textContent, /Original entered target/);
  assert.match(h.document.body.textContent, /400 pieces/);
  await enter('Target quantity', '0');
  await h.click(button(h, 'Save temporary target revision'));
  assert.match(h.document.body.textContent, /Explain the target revision/);
  await enter('Reason / note', 'Revised plan', 'textarea');
  await h.click(button(h, 'Save temporary target revision'));
  assert.match(h.document.body.textContent, /Revised whole-shift goal/);
  assert.match(h.document.body.textContent, /400 pieces/);
  assert.match(h.document.body.textContent, /ZeroTarget/);
  assert.match(h.document.body.textContent, /Actual not established/);
  assert.match(h.document.body.textContent, /Variance: — · Achievement: —/);
  assert.doesNotMatch(h.document.body.textContent, /100%/);
  await choose(h, 'Shift occurrence', 'sample-s2');
  assert.match(h.document.body.textContent, /Target not provided/);
  await choose(h, 'Shift occurrence', 'sample-s1');
  assert.match(h.document.body.textContent, /2 revision\(s\)/);
  await choose(h, 'Machine scope', '22222222-2222-2222-2222-222222222222');
  assert.match(h.document.body.textContent, /Target not provided/);
  assert.equal(h.window.localStorage.length, 0);
});
