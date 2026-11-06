# FC2-003A increment 2 — Immutable package storage

Baseline: `main` `6cfca343c95f3892f9378487ce3bc89be02ddb39`, after squash merges of PR #3 and #4. User authorization permits autonomous development/testing and merging verified increments before the next branch.

## Implemented boundary

Migration `002-configuration-packages` adds G1-owned immutable shared timezone/schedule/break-policy revisions, package lineage/revisions, machine scope, assignments and exact signal mappings. It leaves migration 001 unchanged. There is no security credential, approval, activation, revocation workflow, runtime worker, HTTP mutation or SQL-backed roster in this increment.

`SqlPackageRepository.InsertSubmittedAsync` is a trusted persistence primitive, not an exposed authorization API. A future authorized operation must acquire operation/security/policy locks before calling it, own the same transaction, and roll back on any failure. This increment registers it nowhere in the API. Privileged SQL acceptance fixtures call it directly inside rollback-safe transactions.

The repository serializes once, hashes that snapshot, and derives normalized rows from those same bytes. It stores scope/mappings and seals the revision in a single caller-owned transaction. Draft staging inside that transaction is not a completed authoring workflow. `SubmittedAt` records sealing time and does not prove approval, authorization, activation or commit visibility.

PK/composite FKs enforce site ownership and exact policy revision/hash references. Submitted package rows and their child rows reject update/delete and later child insertion. Shared policy rows are immutable from insertion. The transition to submitted content requires matching scope/assignment/mapping counts. Unique revision numbers and exact binary semantic-key indexes prevent conflicting normalized records, including explicit handling of SQL text trailing-space comparison behavior. These checks do not claim to protect against a SQL owner deliberately disabling constraints/triggers; application SQL privileges arrive with G2.

Policy payload tables preserve canonical bytes/hash and timezone provenance/rules. The fixture uses explicitly synthetic payloads, not an executable tzdb engine. Full policy-shape validation, timezone resolver replay and policy revocation/eligibility acceptance remain roadmap items before activation. Database checksum checks alone do not prove policy semantic correctness.

## Canonical profile v1

- Compact UTF-8 JSON with fixed property order, explicit `schemaVersion=1` and `canonicalizationVersion=1`.
- GUIDs use lowercase D form; SHA-256 pins use lowercase hexadecimal; binary source addresses use padded standard Base64.
- Instants normalize to UTC with seven fractional digits and `Z`; nullable end time is explicitly null. Intervals are half-open, optionally open-ended.
- Machine and signal arrays sort by GUID D text using ordinal order. Capability intent sorts ordinally. Duplicate machines, mapping IDs, machine semantic keys and capabilities are rejected.
- Exact source strings are neither trimmed, case-folded nor Unicode-normalized. Ill-formed UTF-16 is rejected instead of silently replaced. Address bytes remain opaque with their declared field/protocol encoding.
- Capability intent is retained in canonical content. It is not a statement of qualified evidence or a roster capability profile.
- Existing submitted bytes and hashes must never be regenerated in place when serializer/runtime versions change. Golden fixtures must pass before any runtime upgrade; a changed encoding requires a new canonicalization version.

SQL mapping IDs identify mapping records within a package submission; new package revisions currently require new MappingIds. A later stable logical signal identity must be modeled separately rather than reusing a stored mapping row.

## Validation

Local: .NET Release build and canonical-only acceptance executable. CI: full real SQL acceptance with migration upgrade/fresh install, immutable records, wrong policy hash, cross-site/unknown-line rejection, rollback and exact-byte round trip. Missing SQL remains a failure in the full acceptance command; the explicit `--canonical-only` mode is only for pure local fixture checks.

Development installation follows the explicit installer instructions from increment 1. No production connection or database is used. This increment is not a deployable acquisition/reporting product.
