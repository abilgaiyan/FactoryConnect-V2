import assert from "node:assert/strict";
import test from "node:test";

import { createReportingRequestExecutor } from "../src/api/reporting/reporting-request-executor.ts";
import { reportingRoutes } from "../src/api/reporting/reporting-routes.ts";
import {
  ReportingCancellationFailure,
  ReportingNetworkFailure,
  ReportingTimeoutFailure,
} from "../src/api/reporting/reporting-transport-failures.ts";

function createTimerScheduler() {
  const entries = [];
  return {
    entries,
    schedule(callback, delayMilliseconds) {
      const entry = { callback, delayMilliseconds, cleared: false };
      entries.push(entry);
      return entry;
    },
    clear(handle) {
      handle.cleared = true;
    },
    fire(index = 0) {
      entries[index].callback();
    },
  };
}

function createCallerSignal() {
  const listeners = new Set();
  const signal = {
    aborted: false,
    reason: undefined,
    addCount: 0,
    removeCount: 0,
    addEventListener(type, listener) {
      if (type === "abort") {
        this.addCount += 1;
        listeners.add(listener);
      }
    },
    removeEventListener(type, listener) {
      if (type === "abort") {
        this.removeCount += 1;
        listeners.delete(listener);
      }
    },
    abort(reason) {
      if (this.aborted) {
        return;
      }
      this.aborted = true;
      this.reason = reason;
      for (const listener of [...listeners]) {
        listener.call(this, { type: "abort", target: this });
      }
    },
  };
  return signal;
}

function createPendingTransport() {
  const calls = [];
  return {
    calls,
    post(route, request, signal) {
      calls.push({ route, request, signal });
      return new Promise((resolve, reject) => {
        calls.at(-1).resolve = resolve;
        calls.at(-1).reject = reject;
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    },
  };
}

function createExecutor(transport, scheduler, timeoutMilliseconds = 1000) {
  return createReportingRequestExecutor({
    transport,
    timeoutMilliseconds,
    timerScheduler: scheduler,
  });
}

test("returns a successful response before timeout and clears resources", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const response = new Response(null, { status: 200 });
  const transport = { post: async () => response };
  const executor = createExecutor(transport, scheduler);

  const actual = await executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal });

  assert.equal(actual, response);
  assert.equal(scheduler.entries[0].cleared, true);
  assert.equal(callerSignal.addCount, 1);
  assert.equal(callerSignal.removeCount, 1);
});

test("pre-aborted caller signal does not invoke transport", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const reason = new Error("navigation changed");
  callerSignal.abort(reason);
  let calls = 0;
  const executor = createExecutor({ post: async () => { calls += 1; return new Response(); } }, scheduler);

  await assert.rejects(
    executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal }),
    (failure) => failure instanceof ReportingCancellationFailure && failure.cause === reason,
  );

  assert.equal(calls, 0);
  assert.equal(scheduler.entries.length, 0);
});

test("caller cancellation while pending is classified and retains its reason", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const transport = createPendingTransport();
  const executor = createExecutor(transport, scheduler);
  const reason = { kind: "navigation", destination: "/machines/2" };

  const pending = executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal });
  callerSignal.abort(reason);

  await assert.rejects(
    pending,
    (failure) => failure instanceof ReportingCancellationFailure && failure.cause === reason,
  );
  assert.equal(transport.calls[0].signal.aborted, true);
  assert.equal(scheduler.entries[0].cleared, true);
  assert.equal(callerSignal.removeCount, 1);
});

test("timeout while pending is classified separately", async () => {
  const scheduler = createTimerScheduler();
  const transport = createPendingTransport();
  const executor = createExecutor(transport, scheduler, 2500);

  const pending = executor.execute(reportingRoutes.shiftQuery, {});
  scheduler.fire();

  await assert.rejects(
    pending,
    (failure) => failure instanceof ReportingTimeoutFailure && failure.timeoutMilliseconds === 2500,
  );
  assert.equal(transport.calls[0].signal.aborted, true);
  assert.equal(scheduler.entries[0].cleared, true);
});

test("caller cancellation wins when it occurs before timeout", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const transport = createPendingTransport();
  const executor = createExecutor(transport, scheduler);
  const reason = "caller-first";

  const pending = executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal });
  callerSignal.abort(reason);
  scheduler.fire();

  await assert.rejects(
    pending,
    (failure) => failure instanceof ReportingCancellationFailure && failure.cause === reason,
  );
});

test("timeout wins when it occurs before caller cancellation", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const transport = createPendingTransport();
  const executor = createExecutor(transport, scheduler);

  const pending = executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal });
  scheduler.fire();
  callerSignal.abort("caller-late");

  await assert.rejects(pending, ReportingTimeoutFailure);
});

test("independent transport rejection becomes network failure and preserves arbitrary cause", async () => {
  const scheduler = createTimerScheduler();
  const cause = { socket: "reset", retryable: false };
  const executor = createExecutor({ post: async () => { throw cause; } }, scheduler);

  await assert.rejects(
    executor.execute(reportingRoutes.productionDayQuery, {}),
    (failure) => failure instanceof ReportingNetworkFailure && failure.cause === cause,
  );
  assert.equal(scheduler.entries[0].cleared, true);
});

test("fulfilled HTTP 400 remains an uninterpreted response", async () => {
  const scheduler = createTimerScheduler();
  const response = new Response("problem", { status: 400 });
  const executor = createExecutor({ post: async () => response }, scheduler);

  assert.equal(await executor.execute(reportingRoutes.shiftQuery, {}), response);
});

test("fulfilled HTTP 500 remains an uninterpreted response", async () => {
  const scheduler = createTimerScheduler();
  const response = new Response("failure", { status: 500 });
  const executor = createExecutor({ post: async () => response }, scheduler);

  assert.equal(await executor.execute(reportingRoutes.shiftQuery, {}), response);
});

test("a response resolved after caller cancellation still reports cancellation", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  let resolveTransport;
  const transport = {
    post: (_route, _request, _signal) => new Promise((resolve) => { resolveTransport = resolve; }),
  };
  const executor = createExecutor(transport, scheduler);
  const pending = executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal });

  callerSignal.abort("superseded");
  resolveTransport(new Response(null, { status: 200 }));

  await assert.rejects(pending, ReportingCancellationFailure);
});

test("a response resolved after timeout still reports timeout", async () => {
  const scheduler = createTimerScheduler();
  let resolveTransport;
  const transport = {
    post: (_route, _request, _signal) => new Promise((resolve) => { resolveTransport = resolve; }),
  };
  const executor = createExecutor(transport, scheduler, 2750);
  const pending = executor.execute(reportingRoutes.shiftQuery, {});

  scheduler.fire();
  resolveTransport(new Response(null, { status: 200 }));

  await assert.rejects(
    pending,
    (failure) => failure instanceof ReportingTimeoutFailure && failure.timeoutMilliseconds === 2750,
  );
});

test("caller listener is removed after transport failure", async () => {
  const scheduler = createTimerScheduler();
  const callerSignal = createCallerSignal();
  const executor = createExecutor({ post: async () => { throw new Error("offline"); } }, scheduler);

  await assert.rejects(
    executor.execute(reportingRoutes.shiftQuery, {}, { signal: callerSignal }),
    ReportingNetworkFailure,
  );
  assert.equal(callerSignal.removeCount, 1);
  assert.equal(scheduler.entries[0].cleared, true);
});

test("concurrent requests own isolated controllers and completion does not affect the other", async () => {
  const scheduler = createTimerScheduler();
  const transport = createPendingTransport();
  const executor = createExecutor(transport, scheduler);

  const first = executor.execute(reportingRoutes.shiftQuery, { id: 1 });
  const second = executor.execute(reportingRoutes.shiftQuery, { id: 2 });

  assert.notEqual(transport.calls[0].signal, transport.calls[1].signal);
  transport.calls[0].resolve(new Response(null, { status: 200 }));
  await first;

  assert.equal(transport.calls[1].signal.aborted, false);
  assert.equal(scheduler.entries[0].cleared, true);
  assert.equal(scheduler.entries[1].cleared, false);

  scheduler.entries[1].callback();
  await assert.rejects(second, ReportingTimeoutFailure);
});
