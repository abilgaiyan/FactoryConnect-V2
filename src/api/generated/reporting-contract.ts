// Generated from the FactoryConnect.Api OpenAPI contract.
// Do not edit manually.

export interface paths {
    "/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/machines/v1/{machineId}/current-state": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetCurrentMachineState"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/reporting/v1/operational-metrics/shifts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["QueryShiftOperationalMetrics"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/reporting/v1/operational-metrics/production-days/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["QueryProductionDayOperationalMetrics"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/reporting/v1/operational-metrics/production-day-shifts/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["QueryProductionDayShiftOperationalMetrics"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        CurrentMachineStateEvidenceResponse: {
            /** @enum {string} */
            machineState: "unknown" | "stopped" | "idle" | "running" | "fault";
            /** @enum {string} */
            freshness: "current" | "stale" | "indeterminate";
            /** @enum {string} */
            usability: "current" | "stale" | "indeterminate" | "behind";
            /** Format: date-time */
            readAsOf: string;
        };
        CurrentMachineStateResponse: {
            /** Format: uuid */
            machineId: string;
            /** @enum {string} */
            outcome: "evidence" | "no-evidence";
            /** @enum {string} */
            coverage: "complete" | "behind" | "indeterminate";
            evidence: null | components["schemas"]["CurrentMachineStateEvidenceResponse"];
        };
        MetricSourceRevisionResponse: {
            processorId: string;
            /** Format: uuid */
            machineId: string;
            streamKey: string;
            /** Format: uint64 */
            position: number | string;
        };
        OperationalMetricContextRequest: {
            productionOrderId: null | string;
            operationId: null | string;
            partId: null | string;
            operatorId: null | string;
            /** @default false */
            unpartitionedOnly: boolean;
        };
        OperationalMetricContextResponse: {
            productionOrderId: null | string;
            operationId: null | string;
            partId: null | string;
            operatorId: null | string;
        };
        OperationalMetricDefinitionRequest: {
            metricKey: string;
            version: string;
        };
        OperationalMetricItemResponse: {
            /** @enum {string} */
            scope: "shift" | "production-day";
            processorId: string;
            /** Format: uuid */
            machineId: string;
            shift: null | components["schemas"]["ShiftPeriodResponse"];
            productionDay: null | components["schemas"]["ProductionDayPeriodResponse"];
            context: components["schemas"]["OperationalMetricContextResponse"];
            metricKey: string;
            definitionVersion: string;
            /** @enum {string} */
            status: "calculated" | "unavailable" | "insufficient-evidence";
            /** Format: double */
            value: null | number | string;
            unit: string;
            reasonCode: null | string;
            reasonOperandName: null | string;
            sourceRevision: components["schemas"]["MetricSourceRevisionResponse"];
            /** Format: int32 */
            publicationFormatVersion?: null | number | string;
            evaluationPolicyId?: null | string;
            resultId?: null | string;
            conflictWitnessId?: null | string;
        };
        OperationalMetricPageResponse: {
            items: components["schemas"]["OperationalMetricItemResponse"][];
            continuationToken: null | string;
        };
        ProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number | string;
            detail?: null | string;
            instance?: null | string;
        };
        ProductionDayOperationalMetricQueryRequest: {
            sources: components["schemas"]["ReportingSourceRequest"][];
            /** Format: date */
            fromInclusive: string;
            /** Format: date */
            toExclusive: string;
            metrics: null | components["schemas"]["OperationalMetricDefinitionRequest"][];
            context: null | components["schemas"]["OperationalMetricContextRequest"];
            statuses: null | ("calculated" | "unavailable" | "insufficient-evidence")[];
            /** @enum {string} */
            order: "period-ascending" | "period-descending";
            /** Format: int32 */
            pageSize: number | string;
            continuationToken: null | string;
        };
        ProductionDayPeriodResponse: {
            siteId: string;
            /** Format: date */
            businessDate: string;
        };
        ProductionDayShiftMetricResponse: {
            metricKey: string;
            definitionVersion: string;
            /** @enum {string} */
            status: "calculated" | "unavailable" | "insufficient-evidence";
            /** Format: double */
            value: null | number | string;
            unit: string;
            reasonCode: null | string;
            reasonOperandName: null | string;
            /** Format: int32 */
            publicationFormatVersion?: null | number | string;
            evaluationPolicyId?: null | string;
            resultId?: null | string;
            conflictWitnessId?: null | string;
        };
        ProductionDayShiftOperationalMetricPageResponse: {
            items: components["schemas"]["ProductionDayShiftOperationalMetricResponse"][];
            continuationToken: null | string;
        };
        ProductionDayShiftOperationalMetricQueryRequest: {
            sources: components["schemas"]["ProductionDayShiftReportingSourceRequest"][];
            context: null | components["schemas"]["OperationalMetricContextRequest"];
            metrics: null | components["schemas"]["OperationalMetricDefinitionRequest"][];
            statuses: null | ("calculated" | "unavailable" | "insufficient-evidence")[];
            /** Format: int32 */
            pageSize: number | string;
            continuationToken: null | string;
        };
        ProductionDayShiftOperationalMetricResponse: {
            processorId: string;
            /** Format: uuid */
            machineId: string;
            productionDay: components["schemas"]["ProductionDayPeriodResponse"];
            productionLineId: string;
            shift: components["schemas"]["ShiftPeriodResponse"];
            context: components["schemas"]["OperationalMetricContextResponse"];
            sourceRevision: null | components["schemas"]["MetricSourceRevisionResponse"];
            metrics: components["schemas"]["ProductionDayShiftMetricResponse"][];
        };
        ProductionDayShiftReportingSourceRequest: {
            /** Format: uuid */
            machineId: string;
            processorId: string;
            siteId: string;
            /** Format: date */
            businessDate: string;
        };
        ReportingSourceRequest: {
            /** Format: uuid */
            machineId: string;
            processorId: string;
        };
        ShiftOperationalMetricQueryRequest: {
            sources: components["schemas"]["ReportingSourceRequest"][];
            /** Format: date-time */
            startsAtOrAfterUtc: string;
            /** Format: date-time */
            startsBeforeUtc: string;
            metrics: null | components["schemas"]["OperationalMetricDefinitionRequest"][];
            context: null | components["schemas"]["OperationalMetricContextRequest"];
            statuses: null | ("calculated" | "unavailable" | "insufficient-evidence")[];
            /** @enum {string} */
            order: "period-ascending" | "period-descending";
            /** Format: int32 */
            pageSize: number | string;
            continuationToken: null | string;
        };
        ShiftPeriodResponse: {
            siteId: string;
            shiftScheduleAssignmentId: string;
            shiftId: string;
            /** Format: date-time */
            startsAtUtc: string;
            /** Format: date-time */
            endsAtUtc: string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    GetCurrentMachineState: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                machineId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CurrentMachineStateResponse"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    QueryShiftOperationalMetrics: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ShiftOperationalMetricQueryRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OperationalMetricPageResponse"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Implemented */
            501: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    QueryProductionDayOperationalMetrics: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ProductionDayOperationalMetricQueryRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["OperationalMetricPageResponse"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Implemented */
            501: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
    QueryProductionDayShiftOperationalMetrics: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ProductionDayShiftOperationalMetricQueryRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ProductionDayShiftOperationalMetricPageResponse"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Internal Server Error */
            500: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Not Implemented */
            501: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/problem+json": components["schemas"]["ProblemDetails"];
                };
            };
        };
    };
}
