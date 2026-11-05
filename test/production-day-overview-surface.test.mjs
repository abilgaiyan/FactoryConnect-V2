import assert from "node:assert/strict";
import test from "node:test";

import React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ProductionDayOverviewSurface } from "../src/application/ProductionDayOverviewSurface.ts";

const productionDay = "2026-09-01";

function binding(state, overrides = {}) {
  return {
    state,
    lastSuccessfulRetrieval: null,
    async refresh() {},
    ...overrides,
  };
}

function successModel() {
  const missing = (metricKey) => ({ kind: "missing", metricKey, definitionVersion: "1.0" });
  return {
    productionDay,
    groups: [{
      groupName: "Line 1",
      machines: [{
        machineId: "11111111-1111-1111-1111-111111111111",
        processorId: "operational-metrics",
        displayName: "Machine 1",
        groupName: "Line 1",
        displayOrder: 10,
        metrics: {
          availability: missing("availability"),
          utilization: missing("utilization.elr"),
          performance: missing("performance"),
          quality: missing("quality"),
          oee: missing("oee"),
        },
      }],
    }],
  };
}

function surface(overview, onProductionDayChange = () => {}) {
  return ProductionDayOverviewSurface({ productionDay, overview, onProductionDayChange });
}

function childrenOf(element) {
  const children = element?.props?.children;
  if (children === undefined || children === null) return [];
  return Array.isArray(children) ? children.flat(Infinity).filter(Boolean) : [children];
}

function findElement(element, predicate) {
  if (React.isValidElement(element) && predicate(element)) return element;
  for (const child of childrenOf(element)) {
    if (React.isValidElement(child)) {
      const found = findElement(child, predicate);
      if (found !== null) return found;
    }
  }
  return null;
}

function render(overview) {
  return renderToStaticMarkup(React.createElement(ProductionDayOverviewSurface, {
    productionDay,
    overview,
    onProductionDayChange() {},
  }));
}

test("surface renders the labelled selected date-only input and routes valid changes through its boundary", () => {
  const changes = [];
  const element = surface(binding({ kind: "idle" }), (day) => changes.push(day));
  const input = findElement(element, (candidate) => candidate.type === "input");
  const label = findElement(element, (candidate) => candidate.type === "label");

  assert.ok(input);
  assert.ok(label);
  assert.equal(input.props.type, "date");
  assert.equal(input.props.value, productionDay);
  assert.equal(label.props.htmlFor, input.props.id);
  assert.equal(childrenOf(label).join(""), "Production day");

  input.props.onChange({ currentTarget: { value: "2026-09-02" } });
  input.props.onChange({ currentTarget: { value: "not-a-date" } });
  input.props.onChange({ currentTarget: { value: productionDay } });
  assert.deepEqual(changes, ["2026-09-02"]);
});

test("surface refresh control invokes binding and is disabled with aria-busy while loading", () => {
  let refreshCalls = 0;
  const overview = binding(
    { kind: "loading" },
    { async refresh() { refreshCalls++; } },
  );
  const element = surface(overview);
  const button = findElement(element, (candidate) => candidate.type === "button");

  assert.ok(button);
  assert.equal(childrenOf(button).join(""), "Refresh");
  assert.equal(button.props.disabled, true);
  assert.equal(element.props["aria-busy"], "true");
  button.props.onClick();
  assert.equal(refreshCalls, 1);

  const html = render(overview);
  assert.match(html, /aria-busy="true"/);
  assert.match(html, /role="status"[^>]*>Loading production-day reporting…<\/p>/);
});

test("request reporting and presentation failures are visible alerts", () => {
  for (const state of [
    { kind: "request-invalid", message: "Invalid production day." },
    { kind: "reporting-failed", message: "Reporting request failed." },
    { kind: "presentation-failed", message: "Reporting data violated the overview contract." },
  ]) {
    const html = render(binding(state));
    assert.match(html, /role="alert"/);
    assert.match(html, new RegExp(state.message.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("successful surface renders the matrix without repeating a single group heading", () => {
  const html = render(binding({ kind: "success", model: successModel() }));

  assert.doesNotMatch(html, /<h2>Line 1<\/h2>/);
  assert.match(html, /class="production-day-overview-controls"/);
  assert.match(html, /Machine 1/);
  assert.match(html, />Availability<\/th>/);
  assert.match(html, />OEE<\/th>/);
  assert.match(html, /— Missing/);
});

test("last successful production day and retrieval timestamp are visible", () => {
  const retrievedAt = new Date("2026-09-01T08:05:00Z");
  const html = render(binding(
    { kind: "success", model: successModel() },
    { lastSuccessfulRetrieval: { productionDay, retrievedAt } },
  ));

  assert.match(html, /Last loaded for 01\/09\/2026:/);
  assert.ok(html.includes("01/09/2026 13:35:00 IST"));
});

test("multiple report groups retain distinct labels", () => {
  const model = successModel();
  model.groups.push({ ...model.groups[0], groupName: "Line 2" });
  const html = render(binding({ kind: "success", model }));
  assert.match(html, /<h2>Line 1<\/h2>/);
  assert.match(html, /<h2>Line 2<\/h2>/);
});
