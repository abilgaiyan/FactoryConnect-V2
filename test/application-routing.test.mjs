import assert from "node:assert/strict";
import test from "node:test";

import {
  applicationBasePath,
  parseApplicationRoute,
  routePath,
} from "../src/routing/application-route.ts";
import { createBrowserRouter } from "../src/routing/browser-router.ts";
import { shouldHandleApplicationNavigation } from "../src/routing/navigation-policy.ts";
import {
  isShiftPerformanceProductionDaySelection,
  shiftPerformancePath,
} from "../src/application/shift-performance-navigation.ts";

const machineId = "11111111-1111-1111-1111-111111111111";

test("application routing is explicitly root-hosted", () => {
  assert.equal(applicationBasePath, "/");
});

test("parses the closed application route set exactly", () => {
  assert.deepEqual(parseApplicationRoute("/"), { kind: "productionDayOverview" });
  assert.deepEqual(parseApplicationRoute("/production-days/2026-08-30"), {
    kind: "productionDayDetail",
    productionDay: "2026-08-30",
  });
  assert.deepEqual(parseApplicationRoute("/production-days/2026-08-30/shifts"), {
    kind: "shiftPerformance",
    productionDay: "2026-08-30",
  });
  assert.deepEqual(parseApplicationRoute(`/machines/${machineId}`), {
    kind: "machineDetail",
    machineId,
  });
  assert.deepEqual(parseApplicationRoute("/production-days/2026-08-30/report"), {
    kind: "dailyReport",
    productionDay: "2026-08-30",
  });
});

test("rejects prefixes, missing identifiers, and trailing segments", () => {
  for (const path of [
    "/production-days",
    "/production-days/2026-08-30/",
    "/production-days/2026-08-30/shifts/",
    "/production-days/2026-08-30/shifts/extra",
    "/production-days/2026-08-30/Shifts",
    "/production-days/2026-08-30/report/extra",
    "/machines",
    `/machines/${machineId}/extra`,
    "/production-days-extra/2026-08-30",
    "/factoryconnect/production-days/2026-08-30",
  ]) {
    assert.deepEqual(parseApplicationRoute(path), { kind: "notFound", path });
  }
});

test("malformed URI escapes produce notFound while valid escapes stay presentation parameters", () => {
  const malformed = "/machines/%ZZ";
  assert.deepEqual(parseApplicationRoute(malformed), { kind: "notFound", path: malformed });
  assert.deepEqual(parseApplicationRoute("/machines/Machine%20A"), {
    kind: "machineDetail",
    machineId: "Machine A",
  });
  assert.deepEqual(parseApplicationRoute("/production-days/not-a-date"), {
    kind: "productionDayDetail",
    productionDay: "not-a-date",
  });
  assert.deepEqual(parseApplicationRoute("/production-days/not-a-date/shifts"), {
    kind: "shiftPerformance",
    productionDay: "not-a-date",
  });
});

test("route path encoding round-trips presentation parameters without interpreting them", () => {
  const route = { kind: "machineDetail", machineId: "Line 1 / Machine A" };
  const path = routePath(route);
  assert.equal(path, "/machines/Line%201%20%2F%20Machine%20A");
  assert.deepEqual(parseApplicationRoute(path), route);

  const shiftRoute = { kind: "shiftPerformance", productionDay: "2026-08-30" };
  assert.equal(routePath(shiftRoute), "/production-days/2026-08-30/shifts");
  assert.deepEqual(parseApplicationRoute(routePath(shiftRoute)), shiftRoute);
});

test("shift performance production-day selection uses calendar identity without timezone conversion", () => {
  for (const valid of ["0001-01-01", "2000-02-29", "2026-08-30", "9999-12-31"]) {
    assert.equal(isShiftPerformanceProductionDaySelection(valid), true);
    assert.equal(shiftPerformancePath(valid), `/production-days/${valid}/shifts`);
  }

  for (const invalid of [
    "",
    "2026-2-03",
    "2026-02-30",
    "1900-02-29",
    "0000-01-01",
    "10000-01-01",
    " 2026-08-30",
    "2026-08-30 ",
  ]) {
    assert.equal(isShiftPerformanceProductionDaySelection(invalid), false);
  }
});

test("browser routing uses pathname only and normalizes query and fragment away from routing", () => {
  const browser = createFakeBrowser("https://factory.example/production-days/2026-08-30?tab=all#top");
  const router = createBrowserRouter(browser.port);

  assert.deepEqual(router.current(), {
    kind: "productionDayDetail",
    productionDay: "2026-08-30",
  });

  const next = router.navigate("https://factory.example/production-days/2026-08-30/shifts?mode=detail#status");
  assert.deepEqual(next, { kind: "shiftPerformance", productionDay: "2026-08-30" });
  assert.deepEqual(browser.pushes, ["/production-days/2026-08-30/shifts"]);
});

test("search changes create history entries and exact repeated selections do not", () => {
  const browser = createFakeBrowser("https://factory.example/machines/M-1?first=true");
  const router = createBrowserRouter(browser.port);

  assert.deepEqual(router.navigate("/machines/M-1?second=true#ignored"), {
    kind: "machineDetail",
    machineId: "M-1",
  });
  assert.deepEqual(browser.pushes, ["/machines/M-1"]);
  router.navigate("/machines/M-1?second=true");
  assert.deepEqual(browser.pushes, ["/machines/M-1"]);
});

test("popstate subscription reacts and cleanup removes the exact listener", () => {
  const browser = createFakeBrowser("https://factory.example/");
  const router = createBrowserRouter(browser.port);
  let notifications = 0;

  const dispose = router.subscribe(() => notifications++);
  browser.raisePopState("/production-days/2026-08-31/shifts");
  assert.equal(notifications, 1);
  assert.deepEqual(router.current(), { kind: "shiftPerformance", productionDay: "2026-08-31" });

  dispose();
  browser.raisePopState("/production-days/2026-08-31");
  assert.equal(notifications, 1);
});

test("external application navigation is rejected by the history adapter", () => {
  const browser = createFakeBrowser("https://factory.example/");
  const router = createBrowserRouter(browser.port);

  assert.throws(
    () => router.navigate("https://other.example/machines/M-1"),
    /dashboard origin/,
  );
  assert.deepEqual(browser.pushes, []);
});

test("only unmodified primary same-origin links are intercepted", () => {
  const primary = click();
  const internal = anchor("https://factory.example/machines/M-1");

  assert.equal(shouldHandleApplicationNavigation(primary, internal, "https://factory.example"), true);
  assert.equal(shouldHandleApplicationNavigation(click({ ctrlKey: true }), internal, "https://factory.example"), false);
  assert.equal(shouldHandleApplicationNavigation(click({ metaKey: true }), internal, "https://factory.example"), false);
  assert.equal(shouldHandleApplicationNavigation(click({ shiftKey: true }), internal, "https://factory.example"), false);
  assert.equal(shouldHandleApplicationNavigation(click({ altKey: true }), internal, "https://factory.example"), false);
  assert.equal(shouldHandleApplicationNavigation(click({ button: 1 }), internal, "https://factory.example"), false);
  assert.equal(shouldHandleApplicationNavigation(click({ defaultPrevented: true }), internal, "https://factory.example"), false);
  assert.equal(
    shouldHandleApplicationNavigation(primary, anchor("https://other.example/machines/M-1"), "https://factory.example"),
    false,
  );
  assert.equal(
    shouldHandleApplicationNavigation(primary, anchor("https://factory.example/file.csv", { hasDownload: true }), "https://factory.example"),
    false,
  );
  assert.equal(
    shouldHandleApplicationNavigation(primary, anchor("https://factory.example/machines/M-1", { target: "_blank" }), "https://factory.example"),
    false,
  );
});

function click(overrides = {}) {
  return {
    defaultPrevented: false,
    button: 0,
    metaKey: false,
    ctrlKey: false,
    shiftKey: false,
    altKey: false,
    ...overrides,
  };
}

function anchor(href, overrides = {}) {
  return {
    href,
    target: "",
    hasDownload: false,
    ...overrides,
  };
}

function createFakeBrowser(initialHref) {
  let current = new URL(initialHref);
  let popStateListener = null;
  const pushes = [];

  const location = {};
  Object.defineProperties(location, {
    href: { get: () => current.href },
    origin: { get: () => current.origin },
    pathname: { get: () => current.pathname },
  });

  const port = {
    location,
    history: {
      pushState(_data, _unused, url) {
        const target = new URL(String(url), current.href);
        current = target;
        pushes.push(target.pathname);
      },
    },
    addEventListener(type, listener) {
      assert.equal(type, "popstate");
      assert.equal(popStateListener, null);
      popStateListener = listener;
    },
    removeEventListener(type, listener) {
      assert.equal(type, "popstate");
      assert.equal(popStateListener, listener);
      popStateListener = null;
    },
  };

  return {
    port,
    pushes,
    raisePopState(pathname) {
      current = new URL(pathname, current.origin);
      popStateListener?.();
    },
  };
}

test("machine report routes round-trip exact identities and reject malformed paths", () => {
  for (const [kind, suffix] of [["machineActivity", "activity"], ["machineCounter", "counter"]]) {
    for (const machineId of ["11111111-1111-1111-1111-111111111111", "Line 1 / Machine A"]) {
      const route = { kind, machineId };
      assert.equal(routePath(route), `/machines/${encodeURIComponent(machineId)}/${suffix}`);
      assert.deepEqual(parseApplicationRoute(routePath(route)), route);
    }
    for (const path of [`/machines//${suffix}`, `/machines/%ZZ/${suffix}`, `/machines/x/${suffix}/`, `/machines/x/${suffix}/extra`, `/machines/x/${suffix.toUpperCase()}`]) {
      assert.deepEqual(parseApplicationRoute(path), { kind: "notFound", path });
    }
  }
});
