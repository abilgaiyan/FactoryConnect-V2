# FC2-132 — V1 historical reporting migration: first slice

## Goal
Use the restored V1 snapshot for V2 reporting and OEE acceptance without claiming that legacy rows are V2 acquisition or publication evidence.

## Confirmed schema differences (2026-11-16 exported schema)
- V1 `dbo.MachineObservation`: MachineId, StreamKeyBinary, InstanceId, Sequence, ObservationValue, Quality, ObservedAt. V2 `acquisition.MtConnectObservation` additionally requires BatchId, MappingId, AddressBytes, SourceTimestamp, CanonicalBytes, ContentHash and site-scoped identity. **Do not directly insert.**
- V1 `dbo.MachineActivityPeriodHistory`: MachineId, MachineState (tinyint), StartedAt, EndedAt, InstanceId, Sequence. V2 `activity.ClosedPeriod` requires OperationId, MappingId, QualificationHash and evidence-qualified interval identity. **Do not directly insert or invent hashes.**
- V1 `dbo.ShiftMetricAggregate`: MachineId, SiteId (nvarchar), ShiftId (nvarchar), shift UTC bounds, MetricInputKey, AggregateValue, Unit. V2 `planning.ShiftDefinition` has GUID ShiftId and GUID SiteId. **Explicit mapping required.**
- V1 `dbo.OperationalMetricProjection`: MetricKey, DefinitionVersion, Status, MetricValue, ReasonCode, shift/day keys. **Preserve metric status; null/unknown is not zero.**
- V2 `master.Site` and `master.Machine` already contain configured entities. **Never overwrite by matching display name alone.**

## First deliverable
`Export-LegacyHistoryInventory.ps1` performs read-only, aggregate-only inventory by V1 MachineId and date range and enumerates V2 site/machine GUIDs. It neither creates a migration table nor writes to either database.

Run from Windows PowerShell with SqlServer module:
```powershell
.\tools\legacy-history\Export-LegacyHistoryInventory.ps1 -ServerInstance '.\FCINVENTORY' -SourceDatabase 'FactoryConnect_P0_20261105' -TargetDatabase 'FactoryConnect_V2_Dev'
```
Review the two generated CSVs locally; do not commit factory identifiers or machine data.

## Next implementation slice
1. Agree explicit V1→V2 machine/site mapping using inventory and V2 commissioning status.
2. Add isolated `legacy_reporting` schema with import batch, machine mapping, historical activity, shift aggregates, metric projections and source keys; no writes to V2 `acquisition` or `activity` publication tables.
3. Build dry-run + explicit destination-write approval, idempotent bounded import, reconciliation, and read-only report endpoints.
4. Show real history in management and shift reports; calculate OEE only when evidence supports each component.

No production installation or production SQL changes are authorized.


## Historical reporting staging import (development only)

The second script `Import-LegacyHistory.ps1` uses the confirmed Gajra V1 source machine IDs and inserts historical activity periods, shift metric aggregates, and metric projections into **new** `legacy_reporting` tables in the selected V2 development database. It does not modify the V1 database, V2 `master`, `acquisition`, `activity`, or existing reporting tables. Import uses one transaction and skips existing source keys on reruns.

Dry run (no writes):

```powershell
.\tools\legacy-history\Import-LegacyHistory.ps1 -ServerInstance '.\FCINVENTORY' -SourceDatabase 'FactoryConnect_P0_20261105' -TargetDatabase 'FactoryConnect_V2_Dev'
```

**Do not run write mode until the dry-run and SQL script have been reviewed.** When approved, apply explicitly:

```powershell
.\tools\legacy-history\Import-LegacyHistory.ps1 -ServerInstance '.\FCINVENTORY' -SourceDatabase 'FactoryConnect_P0_20261105' -TargetDatabase 'FactoryConnect_V2_Dev' -Apply -ConfirmTargetDatabase 'FactoryConnect_V2_Dev'
```

This is an initial staging slice, **not yet connected to V2 reporting APIs or UI**. Values remain source values; the importer does not reinterpret state codes, metric status, shifts, or OEE semantics. It does not map V1 machine GUIDs to V2 machine GUIDs or mark them commissioned. Reconciliation and API/UI integration are follow-up work. Run against a disposable copy of V2 development data first; SQL execution has not been tested against the user's instance.
