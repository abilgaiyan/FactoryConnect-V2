import assert from "node:assert/strict";
import { after, test } from "node:test";

import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createServer } from "vite";

const vite = await createServer({
  appType: "custom",
  logLevel: "silent",
  server: { middlewareMode: true },
});
const { ShiftPerformanceOverviewView } = await vite.ssrLoadModule(
  "/src/presentation/ShiftPerformanceOverviewView.tsx",
);

after(async () => {
  await vite.close();
});

const day = "2026-09-02";

function render(overview) {
  return renderToStaticMarkup(createElement(ShiftPerformanceOverviewView, { overview }));
}

function metric(metricKey, state, overrides = {}) {
  if (state === "calculated") {
    return { metricKey, version: "1.0", state, value: "0.80", unit: "Ratio", ...overrides };
  }
  if (state === "missing") {
    return { metricKey, version: "1.0", state };
  }
  return {
    metricKey,
    version: "1.0",
    state,
    reasonCode: "missing-input",
    reasonOperandName: "Input",
    ...overrides,
  };
}

function shift(overrides = {}) {
  return {
    shift: {
      siteId: "site-a",
      shiftScheduleAssignmentId: "assignment-a",
      shiftId: "SHIFT-BETA",
      startsAtUtc: "2026-09-02T08:00:00.0000000Z",
      endsAtUtc: "2026-09-02T16:00:00+00:00",
    },
    productionLineId: "line-a",
    sourceRevision: null,
    availability: metric("availability", "missing"),
    utilization: metric("utilization.elr", "missing"),
    performance: metric("performance", "missing"),
    quality: metric("quality", "missing"),
    oee: metric("oee", "missing"),
    ...overrides,
  };
}

function machine(machineId, displayName, shifts = []) {
  return {
    machineId,
    processorId: `processor-${machineId}`,
    siteId: "site-a",
    productionLineId: "line-a",
    displayName,
    shifts,
  };
}

function overview(groups) {
  return { productionDay: day, groups };
}

test("renders the empty configured factory distinctly", () => {
  const html = render(overview([]));
  assert.match(html, /No configured machines\./);
  assert.doesNotMatch(html, /No authoritative shift occurrences returned\./);
});

test("renders a configured machine with no authoritative occurrences distinctly", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One")] }]));
  assert.match(html, /Machine One/);
  assert.doesNotMatch(html, /<h3>/);
  assert.match(html, /aria-label="Line A"/);
  assert.match(html, /No authoritative shift occurrences returned\./);
  assert.doesNotMatch(html, /No configured machines\./);
});

test("renders a zero-evidence occurrence with five accessible missing metric cells", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  assert.match(html, /SHIFT-BETA/);
  for (const metricKey of ["Availability", "Utilization", "Performance", "Quality", "OEE"]) {
    assert.match(html, new RegExp(`aria-label="${metricKey} missing"`));
  }
  assert.equal((html.match(/>—<\/td>/g) ?? []).length, 5);
});

test("preserves configured group and machine order", () => {
  const html = render(overview([
    { groupName: "Line B", machines: [machine("M2", "Machine Two"), machine("M4", "Machine Four")] },
    { groupName: "Line A", machines: [machine("M1", "Machine One"), machine("M3", "Machine Three")] },
  ]));

  assert.match(html, /<h3>Line B<\/h3>/);
  assert.match(html, /<h3>Line A<\/h3>/);
  assert.ok(html.indexOf("Line B") < html.indexOf("Line A"));
  assert.ok(html.indexOf("Machine Two") < html.indexOf("Machine Four"));
  assert.ok(html.indexOf("Machine One") < html.indexOf("Machine Three"));
});

test("displays authoritative shift identity and UTC wire timestamps verbatim", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  assert.match(html, /SHIFT-BETA/);
  assert.match(html, /2026-09-02T08:00:00\.0000000Z/);
  assert.match(html, /2026-09-02T16:00:00\+00:00/);
  assert.doesNotMatch(html, /Shift 1/);
});

test("renders precision-preserving calculated ratios and authoritative inconsistent OEE", () => {
  const renderedShift = shift({
    availability: metric("availability", "calculated", { value: "0.8000" }),
    performance: metric("performance", "calculated", { value: "0.5000" }),
    quality: metric("quality", "calculated", { value: "0.9000" }),
    oee: metric("oee", "calculated", { value: "0.3700" }),
  });
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [renderedShift])] }]));

  assert.match(html, />80%<\/td>/);
  assert.match(html, />50%<\/td>/);
  assert.match(html, />90%<\/td>/);
  assert.match(html, />37%<\/td>/);
  assert.doesNotMatch(html, />36%<\/td>/);
});

test("renders unavailable and insufficient-evidence as distinct visible states with reason evidence", () => {
  const renderedShift = shift({
    utilization: metric("utilization.elr", "unavailable", {
      reasonCode: "missing-power-on",
      reasonOperandName: "PowerOn",
    }),
    performance: metric("performance", "insufficient-evidence", {
      reasonCode: "missing-reference-time",
      reasonOperandName: "ReferenceTime",
    }),
  });
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [renderedShift])] }]));

  assert.match(html, /<strong>Unavailable<\/strong>/);
  assert.match(html, /missing-power-on \/ PowerOn/);
  assert.match(html, /<strong>Insufficient evidence<\/strong>/);
  assert.match(html, /missing-reference-time \/ ReferenceTime/);
  assert.doesNotMatch(html, /title="missing-power-on/);
});

test("does not manufacture current machine state vocabulary", () => {
  const html = render(overview([{ groupName: "Line A", machines: [machine("M1", "Machine One", [shift()])] }]));
  for (const state of ["Running", "Idle", "Fault", "Offline", "Shutdown"]) {
    assert.doesNotMatch(html, new RegExp(`\\b${state}\\b`, "i"));
  }
});

test("over-100 Availability is a visible conflict and cannot present calculated OEE as valid", () => {
  for (const value of ["1.01", "1.0000000000000000001", "101e-2", "10e0"]) {
    const renderedShift = shift({
      sourceRevision: { machineId: "GG-003", processorId: "metrics", streamKey: "history", position: "534" },
      availability: metric("availability", "calculated", { value }),
      oee: metric("oee", "calculated", { value: "0.8" }),
    });
    const html = render(overview([{ groupName: "Line A", machines: [machine("GG-003", "GG-003", [renderedShift])] }]));
    assert.match(html, /Evidence conflict/);
    assert.match(html, /Availability exceeds 100%/);
    assert.match(html, /Reported OEE 80%/);
    assert.match(html, /534/);
    assert.doesNotMatch(html, /<td data-state="calculated">80%<\/td>/);
  }
});

test("valid Availability boundary and zero remain calculated; unavailable OEE reason is preserved", () => {
  for (const value of ["0", "1", "1.000", "10e-1", "0.9999999999999999999"]) {
    const html = render(overview([{ groupName: null, machines: [machine("M1", "One", [shift({
      availability: metric("availability", "calculated", { value }),
      oee: metric("oee", "unavailable", { reasonCode: "dependency-invalid", reasonOperandName: "Availability" }),
    })])] }]));
    assert.doesNotMatch(html, /Evidence conflict/);
    assert.match(html, /dependency-invalid \/ Availability/);
  }
});

test("filters select one exact occurrence, retain state distinctions and reset without requesting new data", async t => {
  const { act } = await import("react");
  const { mountInDom } = await import("./dom-test-harness.mjs");
  const data = overview([{ groupName: "Line A", machines: [
    machine("M1", "One", [shift({ availability: metric("availability", "calculated", { value: "0.75" }) })]),
    machine("M2", "Two", [shift({ shift: { ...shift().shift, shiftId: "SHIFT-OTHER" } })]),
  ] }]);
  const h = await mountInDom(createElement(ShiftPerformanceOverviewView, { overview: data }));
  t.after(() => h.dispose());
  assert.equal(h.document.querySelector(".performance-kpis"), null);
  const [machineSelect, shiftSelect] = h.document.querySelectorAll("select");
  async function select(control, value) {
    await act(async () => { control.value = value; control.dispatchEvent(new h.window.Event("change", { bubbles: true })); });
  }
  await select(machineSelect, machineSelect.options[1].value);
  assert.equal(h.document.querySelectorAll(".performance-kpi").length, 5);
  assert.match(h.document.querySelector(".performance-kpis").textContent, /75%/);
  assert.equal(h.document.querySelectorAll("tbody tr").length, 1);
  await select(shiftSelect, "SHIFT-OTHER");
  assert.match(h.document.querySelector("[role=status]").textContent, /No shift occurrences match/);
  assert.equal(h.document.querySelector(".performance-kpis"), null);
  await h.click(h.document.querySelector("button"));
  assert.equal(h.document.querySelectorAll("tbody tr").length, 2);
  assert.equal(h.document.querySelector(".performance-kpis"), null);
});

test("a recurring shift identifier retains every exact occurrence and suppresses ambiguous KPI cards", async t => {
  const { act } = await import("react");
  const { mountInDom } = await import("./dom-test-harness.mjs");
  const first = shift();
  const second = shift({ shift: { ...first.shift, shiftScheduleAssignmentId: "assignment-b", startsAtUtc: "2026-09-02T16:00:00Z", endsAtUtc: "2026-09-02T20:00:00Z" } });
  const data = overview([{ groupName: null, machines: [machine("M1", "One", [first, second])] }]);
  const h = await mountInDom(createElement(ShiftPerformanceOverviewView, { overview: data }));
  t.after(() => h.dispose());
  const control = h.document.querySelector('select[aria-describedby="shift-filter-scope"]');
  assert.match(control.closest("label").textContent, /Shift identifier/);
  assert.match(h.document.getElementById("shift-filter-scope").textContent, /every occurrence/);
  await act(async () => {
    control.value = "SHIFT-BETA";
    control.dispatchEvent(new h.window.Event("change", { bubbles: true }));
  });
  assert.equal(h.document.querySelectorAll("tbody tr").length, 2);
  assert.equal(h.document.querySelector(".performance-kpis"), null);
  assert.match(h.document.querySelector("details").textContent, /assignment-a/);
  assert.match(h.document.querySelector("details").textContent, /assignment-b/);
});
