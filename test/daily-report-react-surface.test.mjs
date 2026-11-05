import assert from "node:assert/strict";
import { register } from "node:module";
import test from "node:test";

import { JSDOM } from "jsdom";
import React, { act } from "react";

const dom = new JSDOM("<!doctype html><html><body><div id=\"root\"></div></body></html>", {
  url: "http://factory.example/",
});

globalThis.window = dom.window;
globalThis.document = dom.window.document;
globalThis.HTMLElement = dom.window.HTMLElement;
globalThis.Node = dom.window.Node;
globalThis.Event = dom.window.Event;
globalThis.MouseEvent = dom.window.MouseEvent;
globalThis.IS_REACT_ACT_ENVIRONMENT = true;

register("./tsx-test-loader.mjs", import.meta.url);

const { createRoot } = await import("react-dom/client");
const { DailyReportPage } = await import("../src/presentation/DailyReportPage.tsx");

const day = "2026-09-09";
const model = {
  productionDay: day,
  groups: [
    {
      groupName: "Line A",
      machines: [
        {
          machineId: "M1",
          processorId: "P1",
          siteId: "site-a",
          productionLineId: "line-a",
          displayName: "Machine 1",
          groupName: "Line A",
          displayOrder: 10,
          productionDayCells: [
            calculated("availability", "0.82"),
            missing("utilization.elr"),
            unavailable("performance", "missing-reference-time", "ReferenceTime"),
            insufficientEvidence("quality", "missing-counts", null),
            calculated("oee", "0.61"),
          ],
          shifts: [
            {
              shift: {
                siteId: "site-a",
                shiftScheduleAssignmentId: "assignment-a",
                shiftId: "Shift A",
                startsAtUtc: "2026-09-09T00:00:00Z",
                endsAtUtc: "2026-09-09T08:00:00Z",
              },
              productionLineId: "line-a",
              sourceRevision: null,
              cells: [
                calculated("availability", "0.75"),
                missing("utilization.elr"),
                unavailable("performance", "missing-cycle-time", "ReferenceCycleTime"),
                insufficientEvidence("quality", "missing-counts", "GoodCount"),
                calculated("oee", "0.49"),
              ],
            },
          ],
        },
      ],
    },
  ],
};

test("successful Daily Report renders hierarchy and metric states and wires Print", async () => {
  let printCalls = 0;
  const rendered = await renderPage(
    { kind: "success", productionDay: day, model, canPrint: true },
    { print: () => { printCalls++; } },
  );

  try {
    const article = rendered.container.querySelector("article.daily-report-document");
    assert.ok(article);
    assert.equal(article.querySelector("h2")?.textContent, "Daily Report");
    assert.equal(article.querySelector("h3"), null);
    assert.equal(article.querySelector(".daily-report-group")?.getAttribute("aria-label"), "Line A");
    assert.equal(article.querySelector("h4")?.textContent, "Machine 1");
    assert.equal(article.querySelector("h6")?.textContent, "Shift A");
    assert.equal(article.querySelector("time")?.getAttribute("datetime"), day);

    const captions = [...article.querySelectorAll("caption")].map(node => node.textContent);
    assert.deepEqual(captions, ["Machine 1 production-day metrics", "Shift A metrics"]);

    const text = article.textContent ?? "";
    for (const expected of [
      "Availability",
      "Utilization",
      "Performance",
      "Quality",
      "OEE",
      "Calculated",
      "Unavailable",
      "Insufficient evidence",
      "Missing",
      "0.82 Ratio",
      "0.75 Ratio",
      "Reason: missing-reference-time · ReferenceTime",
      "Reason: missing-cycle-time · ReferenceCycleTime",
      "Reason: missing-counts",
    ]) {
      assert.match(text, new RegExp(escapeRegExp(expected)));
    }

    const printButton = button(rendered.container, "Print");
    assert.equal(printButton.disabled, false);

    await act(async () => {
      printButton.click();
    });
    assert.equal(printCalls, 1);
  } finally {
    await rendered.dispose();
  }
});

test("refreshing keeps the previous report visible, marks it stale, and disables Print", async () => {
  let printCalls = 0;
  const rendered = await renderPage(
    { kind: "refreshing", productionDay: day, previous: model, canPrint: false },
    { print: () => { printCalls++; } },
  );

  try {
    assert.ok(rendered.container.querySelector("article.daily-report-document"));
    assert.match(
      rendered.container.querySelector('[role="status"]')?.textContent ?? "",
      /previous successful retrieval and cannot be printed/i,
    );

    const printButton = button(rendered.container, "Print");
    assert.equal(printButton.disabled, true);
    printButton.click();
    assert.equal(printCalls, 0);
  } finally {
    await rendered.dispose();
  }
});

test("reporting failure keeps the previous report visible as stale and disables Print", async () => {
  const rendered = await renderPage({
    kind: "reporting-failure",
    productionDay: day,
    failure: new Error("offline"),
    previous: model,
    canPrint: false,
  });

  try {
    assert.ok(rendered.container.querySelector("article.daily-report-document"));
    assert.match(
      rendered.container.querySelector('[role="alert"]')?.textContent ?? "",
      /displayed report is stale and cannot be printed/i,
    );
    assert.equal(button(rendered.container, "Print").disabled, true);
  } finally {
    await rendered.dispose();
  }
});

test("loading, invalid selection, and failure without a previous model render no report and cannot print", async t => {
  const cases = [
    {
      name: "loading",
      state: { kind: "loading", productionDay: day, canPrint: false },
    },
    {
      name: "invalid selection",
      state: { kind: "invalid-selection", productionDay: "not-a-date", canPrint: false },
    },
    {
      name: "reporting failure without previous model",
      state: {
        kind: "reporting-failure",
        productionDay: day,
        failure: new Error("offline"),
        previous: undefined,
        canPrint: false,
      },
    },
  ];

  for (const current of cases) {
    await t.test(current.name, async () => {
      const rendered = await renderPage(current.state);
      try {
        assert.equal(rendered.container.querySelector("article.daily-report-document"), null);
        assert.equal(button(rendered.container, "Print").disabled, true);
      } finally {
        await rendered.dispose();
      }
    });
  }
});

async function renderPage(state, overrides = {}) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);

  await act(async () => {
    root.render(React.createElement(DailyReportPage, {
      productionDay: state.productionDay ?? day,
      state,
      refresh: async () => {},
      print: overrides.print ?? (() => {}),
    }));
  });

  return {
    container,
    async dispose() {
      await act(async () => {
        root.unmount();
      });
      container.remove();
    },
  };
}

function button(container, label) {
  const match = [...container.querySelectorAll("button")]
    .find(candidate => candidate.textContent?.trim() === label);
  assert.ok(match, `${label} button was not rendered.`);
  return match;
}

function calculated(metricKey, value) {
  return { metricKey, version: "1.0", state: "calculated", value, unit: "Ratio" };
}

function unavailable(metricKey, reasonCode, reasonOperandName) {
  return {
    metricKey,
    version: "1.0",
    state: "unavailable",
    reasonCode,
    reasonOperandName,
  };
}

function insufficientEvidence(metricKey, reasonCode, reasonOperandName) {
  return {
    metricKey,
    version: "1.0",
    state: "insufficient-evidence",
    reasonCode,
    reasonOperandName,
  };
}

function missing(metricKey) {
  return { metricKey, version: "1.0", state: "missing" };
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
