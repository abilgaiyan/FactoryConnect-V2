# FC-030 P0-A — Historical reporting retention amendment

Entry baseline: `7e921e5d50480e7ed7cb1cb41b08bca67f0dcfdd`.

Status: implementation candidate; local build and real-SQL conformance pending. No factory deployment or historical recovery is included.

## Authority separation

The checkpoint is the highest successfully processed aggregation revision. Its manifest is the exact latest committed evaluation batch, not the historical reporting universe. The projection table retains the latest published value for each evaluation key, with its original source revision and complete evidence.

An advancing commit atomically upserts incoming projections/evidence, replaces latest-batch membership, and advances the checkpoint. Unrelated rows/evidence are unchanged. Empty batches advance the checkpoint and clear membership without deleting retained projections. Omission never authorizes retirement.

## Coherence and replay

All incoming projections belong to the proposed revision. Replay validates only the exact latest batch and is non-mutating. Added, omitted, or changed batch keys/evidence fail replay.

Reporting reads retained projections. Each row preserves exact identity/source binding and its supporting revision, which cannot exceed the checkpoint. Latest-batch members must still match the checkpoint revision. Reads retain bounded checkpoint-stability verification. Different periods may have different revisions; one period report continues to reject mixed revisions.

The affected-period evaluator continues to evaluate the complete definition set for each affected period. P0-A does not introduce partial within-period metric updates.

## Persistence

Existing migration 005/006 tables support this amendment. No DDL, schema descriptor, codec, HTTP, dashboard, acquisition, aggregation, or reference-time prerequisite changes are required.

The serializable lock order remains processor, checkpoint, retained projection range, latest-batch manifest, evidence. Evidence for omitted rows is never rewritten. Old membership is removed independently of retained rows.

## Supersession

This amendment supersedes FC-030.2C.1 complete-processor replacement and related closure statements about omitted rows becoming obsolete. Earlier documents/tests remain historical evidence; their old retention semantics are not the current contract. In-memory overlay behavior is now intentional.

## Acceptance

Verify cross-day reports at distinct revisions; unchanged historical row IDs, values, revisions, and full evidence; incoming-key replacement; empty batches; checkpoint restoration; exact/changed replay; CAS/concurrency; rollback at every stage; stable reporting reads; future/corrupt revision rejection; SQL/in-memory parity; and continued mixed-revision period rejection.

## Local verification

```powershell
dotnet test tests/FactoryConnect.Core.Tests/FactoryConnect.Core.Tests.csproj
dotnet test tests/FactoryConnect.Edge.Tests/FactoryConnect.Edge.Tests.csproj
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj --filter "FullyQualifiedName~OperationalMetric"
dotnet test tests/FactoryConnect.Integration.Tests/FactoryConnect.Integration.Tests.csproj
```

Use the existing real-SQL fixture configuration. A passing source review is not a substitute for executing these checks.

Historical recovery/backfill, explicit retirement, and A2 remain separate and unauthorized.
