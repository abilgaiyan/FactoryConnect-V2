// In-memory mock API handler for FactoryConnect Dashboard

export interface DashboardRuntimeSource {
  readonly machineId: string;
  readonly processorId: string;
  readonly siteId: string;
  readonly productionLineId: string;
  readonly displayName: string;
  readonly groupName: string | null;
  readonly displayOrder: number;
}

export const MOCK_SOURCES: DashboardRuntimeSource[] = [
  {
    machineId: "11111111-1111-1111-1111-111111111101",
    processorId: "processor-cnc-01",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-1",
    displayName: "CNC-01",
    groupName: "Line 1 (Milling)",
    displayOrder: 1,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111102",
    processorId: "processor-cnc-02",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-1",
    displayName: "CNC-02",
    groupName: "Line 1 (Milling)",
    displayOrder: 2,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111103",
    processorId: "processor-cnc-03",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-1",
    displayName: "CNC-03",
    groupName: "Line 1 (Milling)",
    displayOrder: 3,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111104",
    processorId: "processor-cnc-04",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-1",
    displayName: "CNC-04",
    groupName: "Line 1 (Milling)",
    displayOrder: 4,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111105",
    processorId: "processor-cnc-05",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-1",
    displayName: "CNC-05",
    groupName: "Line 1 (Milling)",
    displayOrder: 5,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111106",
    processorId: "processor-cnc-06",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-2",
    displayName: "CNC-06",
    groupName: "Line 2 (Turning)",
    displayOrder: 6,
  },
  {
    machineId: "11111111-1111-1111-1111-111111111107",
    processorId: "processor-cnc-07",
    siteId: "GAJRA-NEW",
    productionLineId: "LINE-2",
    displayName: "CNC-07",
    groupName: "Line 2 (Turning)",
    displayOrder: 7,
  },
];

export const MOCK_MACHINE_PROFILES = MOCK_SOURCES.map((s) => ({
  machineId: s.machineId,
  profileId: "FC-PROFILE-01",
  profileVersion: "1.0",
  integrationCategoryId: "MTCONNECT-01",
  approvalReference: "APP-GAJRA-2026",
  effectiveFrom: "2026-01-01T00:00:00Z",
  faultLabel: "Alarm" as const,
  stoppedLabel: "Machine OFF" as const,
  capabilities: [
    { key: "operational-state", support: "supported" as const, qualification: "qualified" as const },
    { key: "counter-reading", support: "supported" as const, qualification: "qualified" as const },
    { key: "production-rates", support: "supported" as const, qualification: "qualified" as const },
  ],
}));

export const MOCK_DASHBOARD_CONFIG = {
  reportingBasePath: "/",
  requestTimeoutMilliseconds: 30000,
  sources: MOCK_SOURCES,
  machineProfileSchemaVersion: 1 as const,
  machineProfiles: MOCK_MACHINE_PROFILES,
};

function parseJsonBody(bodyText: string): any {
  try {
    return JSON.parse(bodyText);
  } catch {
    return {};
  }
}

export function handleMockApiRequest(
  urlPath: string,
  method: string,
  bodyText: string,
): { status: number; contentType: string; body: string } | null {
  const pathname = urlPath.split("?")[0] ?? "";

  // 1. Health endpoints
  if (pathname === "/health" || pathname === "/health/live" || pathname === "/health/ready") {
    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ status: "ok", service: "FactoryConnect" }),
    };
  }

  // 2. Dashboard config
  if (pathname === "/dashboard/config") {
    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(MOCK_DASHBOARD_CONFIG),
    };
  }

  // 3. Commissioning snapshot
  if (pathname === "/api/commissioning/v1/snapshot") {
    const fields = MOCK_SOURCES.flatMap((source, idx) => [
      { path: `ProductionProcessing:${idx}:MachineId`, value: source.machineId },
      { path: `ProductionProcessing:${idx}:ActivityStreamKey`, value: `mtconnect:${source.displayName}` },
      { path: `ProductionProcessing:${idx}:ProductionStartsAt`, value: "2026-01-01T00:00:00Z" },
    ]);

    const processors = MOCK_SOURCES.map((source, idx) => ({
      rowId: idx + 1,
      machineId: source.machineId,
      processorId: `production-context:${source.machineId}`,
      streamKey: `mtconnect:${source.displayName}`,
      position: String(1000 + idx * 50),
      admissionBound: true,
      productionStartsAt: "2026-01-01T00:00:00Z",
    }));

    const snapshot = {
      readStartedAt: new Date().toISOString(),
      runtimeActivation: "Active (Edge Host: gajra-edge-01)",
      transportHealth: "Operational (MTConnect Agent v1.8.0)",
      checkpointFreshness: "Current (< 250ms latency)",
      configuration: {
        status: "Loaded",
        authority: "Local File Authority",
        fileModifiedAt: "2026-11-01T12:00:00.000Z",
        sha256: "b4c29571e22f2b3e8006bf26a575a0bf6c3fb8a7605cfbe368e7343e86c0c29f",
        fields,
      },
      durable: {
        status: "Healthy",
        authority: "SQL Ledger Authority",
        readCompletedAt: new Date().toISOString(),
        processors,
      },
    };

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(snapshot),
    };
  }

  // 4. Current machine state
  const currentStateMatch = /^\/api\/machines\/v1\/([^/]+)\/current-state$/.exec(pathname);
  if (currentStateMatch && currentStateMatch[1]) {
    const machineId = decodeURIComponent(currentStateMatch[1]);
    const source = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === machineId.toLowerCase());
    if (!source) {
      return {
        status: 400,
        contentType: "application/problem+json",
        body: JSON.stringify({
          type: "about:blank",
          title: "Unknown Machine",
          status: 400,
          detail: `Machine '${machineId}' is not configured.`,
        }),
      };
    }

    const machineIndex = MOCK_SOURCES.indexOf(source);
    // Alternate state across machines: running, idle, stopped, etc.
    const states = ["running", "running", "idle", "running", "fault", "running", "stopped"] as const;
    const machineState = states[machineIndex % states.length] ?? "running";

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        machineId: source.machineId,
        outcome: "evidence",
        coverage: "complete",
        evidence: {
          machineState,
          freshness: "current",
          usability: "current",
          readAsOf: new Date().toISOString(),
        },
      }),
    };
  }

  // 5. Activity report
  const activityMatch = /^\/api\/machines\/v1\/([^/]+)\/activity\/query$/.exec(pathname);
  if (activityMatch && activityMatch[1]) {
    const machineId = decodeURIComponent(activityMatch[1]);
    const source = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === machineId.toLowerCase());
    const req = parseJsonBody(bodyText);
    const fromInclusive = req.fromInclusive || "2026-11-02T00:00:00.000Z";
    const toExclusive = req.toExclusive || "2026-11-03T00:00:00.000Z";

    const fromMs = Date.parse(fromInclusive);
    const toMs = Date.parse(toExclusive);
    const durationSeconds = Math.max(0, Math.floor((toMs - fromMs) / 1000));
    const durationTicks = durationSeconds * 10000000;

    // Single interval covering the range with running state
    const interval = {
      position: "100",
      state: "running" as const,
      startedAtUtc: fromInclusive,
      endedAtUtc: toExclusive,
      sourceStartedAtUtc: fromInclusive,
      sourceEndedAtUtc: toExclusive,
      durationTicks,
      instanceId: "1",
      sequence: "1",
    };

    const totals = [
      { state: "unknown" as const, durationTicks: 0 },
      { state: "stopped" as const, durationTicks: 0 },
      { state: "idle" as const, durationTicks: 0 },
      { state: "running" as const, durationTicks },
      { state: "fault" as const, durationTicks: 0 },
      { state: "offline" as const, durationTicks: 0 },
    ];

    const report = {
      machineId: source?.machineId ?? machineId,
      stateProcessorId: "machine-state-activity",
      streamKey: `mtconnect:${source?.displayName ?? machineId}`,
      throughPosition: "100",
      fromInclusive,
      toExclusive,
      readAsOf: new Date().toISOString(),
      intervals: [interval],
      totals,
      unrepresentedTicks: 0,
    };

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(report),
    };
  }

  // 6. Counter report
  const counterMatch = /^\/api\/machines\/v1\/([^/]+)\/counter/.exec(pathname);
  if (counterMatch && counterMatch[1]) {
    const machineId = decodeURIComponent(counterMatch[1]);
    const source = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === machineId.toLowerCase());
    const req = parseJsonBody(bodyText);

    const report = {
      machineId: source?.machineId ?? machineId,
      streamKey: `mtconnect:${source?.displayName ?? machineId}`,
      throughPosition: "100",
      acquisitionRevision: "1",
      fromInclusive: req.fromInclusive || "2026-11-02T00:00:00.000Z",
      toExclusive: req.toExclusive || "2026-11-03T00:00:00.000Z",
      readAsOf: new Date().toISOString(),
      source: req.source || "MTConnect",
      address: req.address || "PartCount",
      availableSignals: [
        { source: "MTConnect", address: "PartCount" },
        { source: "MTConnect", address: "GoodPartCount" },
        { source: "MTConnect", address: "Execution" },
      ],
      samples: [],
    };

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(report),
    };
  }

  // 7. Production day metrics query
  if (pathname === "/api/reporting/v1/operational-metrics/production-days/query") {
    const req = parseJsonBody(bodyText);
    const sourcesToUse = Array.isArray(req.sources) && req.sources.length > 0
      ? req.sources
      : MOCK_SOURCES.map((s) => ({ machineId: s.machineId, processorId: s.processorId }));

    const businessDate = req.fromInclusive ? String(req.fromInclusive).slice(0, 10) : "2026-11-02";

    const items = sourcesToUse.flatMap((srcReq: any, sIdx: number) => {
      const src = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === String(srcReq.machineId).toLowerCase())
        || MOCK_SOURCES[sIdx % MOCK_SOURCES.length]!;

      const availabilityVal = 0.88 + (sIdx % 3) * 0.04;
      const performanceVal = 0.90 + (sIdx % 2) * 0.05;
      const qualityVal = 0.98 + (sIdx % 2) * 0.01;
      const oeeVal = availabilityVal * performanceVal * qualityVal;

      return [
        {
          scope: "production-day",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: null,
          productionDay: { siteId: src.siteId, businessDate },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "availability",
          definitionVersion: "1.0",
          status: "calculated",
          value: parseFloat(availabilityVal.toFixed(3)),
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
        {
          scope: "production-day",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: null,
          productionDay: { siteId: src.siteId, businessDate },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "performance",
          definitionVersion: "1.0",
          status: "calculated",
          value: parseFloat(performanceVal.toFixed(3)),
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
        {
          scope: "production-day",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: null,
          productionDay: { siteId: src.siteId, businessDate },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "quality",
          definitionVersion: "1.0",
          status: "calculated",
          value: parseFloat(qualityVal.toFixed(3)),
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
        {
          scope: "production-day",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: null,
          productionDay: { siteId: src.siteId, businessDate },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "oee",
          definitionVersion: "1.0",
          status: "calculated",
          value: parseFloat(oeeVal.toFixed(3)),
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
        {
          scope: "production-day",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: null,
          productionDay: { siteId: src.siteId, businessDate },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "utilization.elr",
          definitionVersion: "1.0",
          status: "calculated",
          value: parseFloat((availabilityVal * 0.95).toFixed(3)),
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
      ];
    });

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ items, continuationToken: null }),
    };
  }

  // 8. Shift metrics query
  if (pathname === "/api/reporting/v1/operational-metrics/shifts/query") {
    const req = parseJsonBody(bodyText);
    const sourcesToUse = Array.isArray(req.sources) && req.sources.length > 0
      ? req.sources
      : MOCK_SOURCES.map((s) => ({ machineId: s.machineId, processorId: s.processorId }));

    const businessDate = req.fromInclusive ? String(req.fromInclusive).slice(0, 10) : "2026-11-02";

    const items = sourcesToUse.flatMap((srcReq: any, sIdx: number) => {
      const src = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === String(srcReq.machineId).toLowerCase())
        || MOCK_SOURCES[sIdx % MOCK_SOURCES.length]!;

      return [
        {
          scope: "shift",
          processorId: src.processorId,
          machineId: src.machineId,
          shift: {
            siteId: src.siteId,
            shiftScheduleAssignmentId: "shift-assignment-1",
            shiftId: "SHIFT-1",
            businessDate,
            startsAtUtc: `${businessDate}T00:30:00.000Z`,
            endsAtUtc: `${businessDate}T08:30:00.000Z`,
          },
          productionDay: null,
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          metricKey: "availability",
          definitionVersion: "1.0",
          status: "calculated",
          value: 0.912,
          unit: "ratio",
          reasonCode: null,
          reasonOperandName: null,
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
        },
      ];
    });

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ items, continuationToken: null }),
    };
  }

  // 9. Production day shifts query
  if (pathname === "/api/reporting/v1/operational-metrics/production-day-shifts/query") {
    const req = parseJsonBody(bodyText);
    const sourcesToUse = Array.isArray(req.sources) && req.sources.length > 0
      ? req.sources
      : MOCK_SOURCES.map((s) => ({
          machineId: s.machineId,
          processorId: s.processorId,
          siteId: s.siteId,
          businessDate: "2026-11-02",
        }));

    const shifts = [
      { shiftId: "SHIFT-1", start: "00:30:00.000Z", end: "08:30:00.000Z" },
      { shiftId: "SHIFT-2", start: "08:30:00.000Z", end: "16:30:00.000Z" },
      { shiftId: "SHIFT-3", start: "16:30:00.000Z", end: "00:30:00.000Z" },
    ];

    const items = sourcesToUse.flatMap((srcReq: any, sIdx: number) => {
      const src = MOCK_SOURCES.find((s) => s.machineId.toLowerCase() === String(srcReq.machineId).toLowerCase())
        || MOCK_SOURCES[sIdx % MOCK_SOURCES.length]!;

      const businessDate = srcReq.businessDate || "2026-11-02";

      return shifts.map((shift, shiftIdx) => {
        const avail = 0.85 + ((sIdx + shiftIdx) % 4) * 0.03;
        const perf = 0.88 + ((sIdx + shiftIdx) % 3) * 0.04;
        const qual = 0.98 + (sIdx % 2) * 0.01;
        const oee = avail * perf * qual;

        return {
          processorId: src.processorId,
          machineId: src.machineId,
          productionDay: { siteId: src.siteId, businessDate },
          productionLineId: src.productionLineId,
          shift: {
            siteId: src.siteId,
            shiftScheduleAssignmentId: `assignment-${shift.shiftId}`,
            shiftId: shift.shiftId,
            businessDate,
            startsAtUtc: `${businessDate}T${shift.start}`,
            endsAtUtc: `${businessDate}T${shift.end}`,
          },
          context: { productionOrderId: null, operationId: null, partId: null, operatorId: null },
          sourceRevision: {
            processorId: src.processorId,
            machineId: src.machineId,
            streamKey: `mtconnect:${src.displayName}`,
            position: "1004",
          },
          metrics: [
            {
              metricKey: "availability",
              definitionVersion: "1.0",
              status: "calculated",
              value: parseFloat(avail.toFixed(3)),
              unit: "ratio",
              reasonCode: null,
              reasonOperandName: null,
            },
            {
              metricKey: "performance",
              definitionVersion: "1.0",
              status: "calculated",
              value: parseFloat(perf.toFixed(3)),
              unit: "ratio",
              reasonCode: null,
              reasonOperandName: null,
            },
            {
              metricKey: "quality",
              definitionVersion: "1.0",
              status: "calculated",
              value: parseFloat(qual.toFixed(3)),
              unit: "ratio",
              reasonCode: null,
              reasonOperandName: null,
            },
            {
              metricKey: "oee",
              definitionVersion: "1.0",
              status: "calculated",
              value: parseFloat(oee.toFixed(3)),
              unit: "ratio",
              reasonCode: null,
              reasonOperandName: null,
            },
            {
              metricKey: "utilization.elr",
              definitionVersion: "1.0",
              status: "calculated",
              value: parseFloat((avail * 0.94).toFixed(3)),
              unit: "ratio",
              reasonCode: null,
              reasonOperandName: null,
            },
          ],
        };
      });
    });

    return {
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ items, continuationToken: null }),
    };
  }

  return null;
}
