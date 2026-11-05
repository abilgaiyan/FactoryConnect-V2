# Gajra R1 — Equipment Performance, first presentation slice

Tracks #122. This is a partial implementation, not milestone acceptance.

## Source and mapping

Baseline: Gajra `gajra-reports-phase-1(2).pdf`, page 1, “Report on equipment performance”. The original contains machine, line and group rows, OEE/A/P/Q, APT/POT and a comparison chart. Its printed definitions are A = APT/POT, P = NOT/APT, Q = good/manufactured parts and OEE = A × P × Q. These definitions require semantic mapping, not matching labels alone.

| Original field | Existing source / presentation | Status |
| --- | --- | --- |
| Manufacturing unit / machine | Configured group, machine display name, site and production line | Machine rows supported; configured groups are headings, not totals |
| A | `availability` v1.0, exact shift occurrence | Show existing result and evidence state; above 100% is a visible evidence conflict |
| P | `performance` v1.0 | Existing result only; no client calculation or reference-time default |
| Q | `quality` v1.0 | Existing canonical result only; missing/unavailable states retained |
| OEE | `oee` v1.0 | Existing result only; a calculated result paired with above-100% Availability is marked conflicting |
| APT | No APT field in this presentation model | Unresolved: confirm authoritative duration definition, units and scope before adding |
| POT | No POT field in this presentation model | Unresolved: confirm planned-time/break semantics, units and scope before adding |
| NOT | No reference-time field in this presentation model | Unresolved; do not infer from P or APT |
| Produced / defect quantities | Not present in shift presentation model | Not implemented in this slice |
| Line / group totals | No authoritative aggregate contract used here | Not implemented; never average machine percentages |
| Comparison chart | Original A/P/Q/OEE comparison | Pending; cards and the existing exact-shift table are the first slice |
| Assumed Quality / provisional OEE | Canonical `PresentedMetric` has no policy-assessment provenance | Pending an explicit existing-assessment API trace/integration; do not label canonical Quality as assumed or measured by inference |

Utilization (`utilization.elr`) is retained as an existing additional metric; it is not substituted for APT/POT.

## Visual baseline

Reuse the existing light dashboard shell: white surfaces, navy text, pale blue controls, blue accents, amber evidence gaps and red conflicts. Machine/shift controls operate on already-loaded authoritative occurrences. Date selection and request lifecycle remain owned by the existing route. KPI cards appear only for one exact occurrence, with machine, shift and interval shown immediately above them. Multiple occurrences remain separate table rows. Native select controls, visible focus, keyboard-scrollable tables and native evidence disclosures support keyboard use. Cards collapse into fewer columns on narrow screens; tables scroll horizontally.

Evidence drill-down preserves machine/processor, site/line, exact UTC interval and the returned source revision. Source revision `position` is not relabeled as an aggregation revision number.

No new API, DTO, calculation engine, SQL migration, historical rewrite or server activation is included.

## Validation and acceptance

- Automated: existing dashboard conformance suite plus mounted machine/shift filtering, reset, empty selection, KPI scope and conflict tests.
- Conflict fixtures exercise GG-003-shaped data and source position 534, including decimal precision just above one. These are **synthetic UI regression fixtures**, not proof against actual aggregation revision 534.
- Existing unavailable OEE reasons remain visible even when Availability conflicts. Reported conflicting values remain evidence; they are never clamped or multiplied in the browser.
- Pending before milestone acceptance: restore real GG-003 history in a nonproduction environment; verify production-day/date and exact revision-534 evidence; validate activity/quantities against persisted evidence; finish APT/POT mapping, policy/provisional integration, activity timeline and comparison chart; review desktop/mobile screenshots and CI.
- The focused PR must remain unmerged until explicit review. Gajra deployment and migration 021 registration remain outside this work.

## Review follow-up — shift filter scope

The control is labeled **Shift identifier** with an accessible description stating that it selects all occurrences sharing that identifier. A mounted regression test uses two different assignments/intervals with the same ID on one machine: both rows and evidence records remain, and KPI cards stay hidden. This is explicitly an identifier filter, not an exact-occurrence selector.

Restored-database validation remains outstanding. The existing `Gg003SqlHistoricalReaderAcceptanceTests` uses a sanitized operand fixture in a newly created SQL database; even a CI pass must not be described as testing the restored GG-003 reporting screen.

## Restored LocalDB evaluator probe

`Gg003RestoredHistoricalAcceptanceTests.RestoredShift534ReturnsUnavailableWithOriginalWitness` is opt-in using `FACTORYCONNECT_GG003_RESTORED_CONNECTION_STRING`; absent configuration is a skip, never a pass against real history. It accepts only `(localdb)` and database `FactoryConnect_GG003_RCA_20261016`. It constructs the real GG-003 machine/site/schedule SHIFT-1 request and pins both component and evaluator reads to revision 534. It checks the 31/3 contribution counts, exact duration operands and original position-380 fact identity, then requires null/unavailable Availability with `RunningOutsidePlannedProduction` and the same witness. The checkpoint is compared before and after.

The probe invokes read methods only: no fixture creation, input seeding, migrations, recovery or projection publication. `ApplicationIntent=ReadOnly` is a connection hint, not a SQL permissions boundary. It is not an end-to-end API/UI acceptance test. Run against the isolated restored database with no ingestion process writing to it.

```powershell
$env:FACTORYCONNECT_GG003_RESTORED_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Database=FactoryConnect_GG003_RCA_20261016;Integrated Security=True;TrustServerCertificate=True'
try {
    dotnet test tests\FactoryConnect.Integration.Tests\FactoryConnect.Integration.Tests.csproj --filter 'FullyQualifiedName~Gg003RestoredHistoricalAcceptanceTests.RestoredShift534ReturnsUnavailableWithOriginalWitness' --logger 'console;verbosity=normal'
} finally {
    Remove-Item Env:\FACTORYCONNECT_GG003_RESTORED_CONNECTION_STRING
}
```

No restored acceptance pass is claimed until this named test executes successfully with the actual restored database. A skipped run does not close this gate.
