# MOXA production admission boundary

MOXA commissioning history begins before its agreed production start. The
configured production-context assignments and shift schedules start at
2026-10-25 07:30 IST (2026-10-25T02:00:00Z). Do not backdate schedules, delete
observations/activity history, or manually advance a SQL checkpoint to recover.

Opt in only for the MOXA production-processing machine entry:

```json
"ProductionStartsAt": "2026-10-25T07:30:00+05:30"
```

This explicit instant is independent of context assignment dates and planned
calendar dates. It is not inferred from the first shift or missing context.
GG003 omits it and retains its existing processing semantics.

The activity production-context runtime consumes closed source periods in
position order. A period ending at or before the boundary produces no contextual,
eligibility or metric outputs. A crossing period retains source processor,
position, stream, instance and sequence but allocates only its admitted tail.
The original durable source and raw observations are unchanged. Periods starting
at the boundary are admitted. Missing shift coverage within admitted time remains
an error. The existing allocator conserves the admitted duration; it does not
attribute excluded duration to a shift or invent an Unknown/Stopped period.

The existing serializable production-context transaction publishes outputs and
advances the checkpoint together, including an empty-output batch. A failed
commit does not advance the in-process checkpoint, so retry consumes the same
sources. Source history, immutable policy binding and the durable checkpoint
allow exclusion decisions to be reconstructed; there is no separate per-period
exclusion receipt in this version.

Migration 022 adds the durable admission binding to ProductionContextProcessor.
The first checkpoint read binds the policy (including the unbounded/null policy)
under the processor identity lock. Every commit checks it again. A changed or
removed boundary is rejected. An existing checkpoint cannot acquire a boundary;
legacy unbounded processing can continue. A processor without a checkpoint can
opt in only before its policy has been bound by the new runtime. If it was first
bound unbounded, operator review and a separate recovery design are required;
do not edit the binding to bypass the check. Concurrent initial different policy
requests serialize and only one policy wins.

Deploy through the normal migration/release workflow. Migration 022 changes the
schema compatibility target; do not run the new runtime on an unmigrated database
or assume an older release can run against the migrated database. This is not a
factory-side SQL repair script. MOXA was observed with no production-context
processor/checkpoint row, but recheck immediately before activation.

This boundary applies only to activity-derived production processing. Quantity
admission is not added; MOXA has no commissioned quantity source. It does not
change contact/freshness, current-state processing, source acquisition, raw OFF
interpretation, or GG003's still-pending break schedule correction.

Acceptance requires core before/crossing/after, restart and failure tests; SQL
policy-binding/concurrency/checkpoint-only commit tests; schema verification; and
a factory run proving production-context advancement with no outputs before the
boundary. A passing build alone does not establish production acceptance.
