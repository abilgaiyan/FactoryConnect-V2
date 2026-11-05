# GG-003 revision-534 SQL historical-reader acceptance

Status: dedicated SQL test implemented; execution/acceptance pending GitHub CI.
Prerequisite: Stage A merged as da28fc51. Stage B, durable witness publication,
production activation and recovery remain held. No production code is changed.

## Evidence and fixture boundary

`Gg003SqlHistoricalReaderAcceptanceTests` embeds the existing sanitized 34-operand
contribution fixture and its manifest by reference, verifies the canonical digest
and uses the unchanged aliases, positions, decimal values, intervals and source
relationships. Original factory-export reconciliation was accepted separately;
this test does not replace or independently repeat that reconciliation.

The newly created `SqlServerTestDatabaseFixture` database is required. The test
uses the normal SQL input transaction writer to allocate positions 1–741, then
the normal SQL aggregation store to commit revisions 534, 535 and 741. It never
connects to the factory or a restored RCA database and never creates an
operational projection. Padding at unrepresented positions through 534 is
explicitly synthetic and is not incorporated into the baseline aggregates.
Only the 34 reconciled operand contributions are incorporated at revision 534.
This is an operand regression fixture, not a reconstructed complete 167-fact
factory ledger or a replay of factory aggregation batches.

All 207 later contributions at positions 535–741 are explicitly synthetic,
seconds-valued running/planned facts in the same shift/day. Their purpose is to
make leakage observable in both operands. They are not claimed as the unseen
factory records at those positions. No raw factory exports or identity mapping
are added to the repository.

## Required observations

Before and after committing the later contributions, both historical shift/day
snapshots at exactly 534 must retain:

- Running: 2683.614157 seconds, 31 contributions.
- Planned production: 2452.013355 seconds, 3 contributions.
- Witness: position 380, rca-fact-380, rca-eligibility-380,
  rca-activity-380, 02:32:25.791360–02:36:45.428970 UTC,
  259.637610 seconds, eligibility=false, no planned assignment.
- The full aggregate metadata and exact revision/source/period bindings.

A reopened SQL store must produce the same result after the durable aggregation
checkpoint reaches 741. A boundary control at committed revision 535 must include
that position, giving Running 2684.614157 seconds. Revision 741 must include all
207 later contributions, giving Running 2787.614157 seconds / 135 inputs and
planned production 2555.013355 seconds / 106 inputs. These differing snapshots
prove the later contributions exist and would affect evaluation if the 534 cut
were ignored. A direct SQL count independently requires 207 later contribution
rows. The test does not manipulate checkpoints or current aggregates directly.

Opt-in evaluation uses the real SQL historical reader pinned to 534 and must
return null Availability / RunningOutsidePlannedProduction with the identical
witness. Legacy requests must return the same original operand aggregates with
no witness. The operational projection table must remain empty.

## CI verification

The SQL Server integration job runs a named, focused acceptance step before the
existing required full SQL regression suite. The focused step logs this test's
name and outcome independently; full CI is still required. A focused pass proves
this fixture-based SQL historical-reader contract, not Stage B codec, persistence,
API processor selection, schedule correctness, recovery or deployment readiness.
Record the final commit and run URL in the PR after verification. No local .NET
execution is claimed in the remote-first environment.
