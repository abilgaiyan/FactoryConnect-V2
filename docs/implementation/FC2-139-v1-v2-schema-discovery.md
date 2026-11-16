# FC2-139 — V1 → V2 development migration: schema discovery

Related roadmap: [Issue #132](https://github.com/abilgaiyan/FactoryConnect-V2/issues/132).

## First usable deliverable

Run [v1-v2-schema-inventory.sql](../../tools/migration/v1-v2-schema-inventory.sql) **separately** against (1) a restored, isolated **V1 backup** and (2) the migrated **V2 development database**. In SSMS, use Results to File or export each result set to CSV, retaining separate V1 and V2 labels. This inventory reads SQL Server catalog metadata and partition row estimates only: columns/types/nullability/PK, foreign keys, indexes, checks, and approximate table row counts. It does not extract business records, credentials, operator names or raw observations.

**Safety boundary:** Never run migration writes against Gajra production or the V1 source. The inventory does not perform writes. It is not a migration runner and does not establish that V1 data satisfies V2 publication or commissioning contracts. Restrict shared output to schema-only metadata; review check constraint definitions for any unexpectedly embedded sensitive literals before sharing.

## Mapping worksheet — to complete after both inventories

| Domain | V1 source | V2 destination | Mapping decision | Required validation |
| --- | --- | --- | --- | --- |
| Site and production line | Discover | Discover | Pending schema comparison | Installation identity, site scope, unique IDs |
| Machine master | Discover | Discover | Pending | Identity and commissioning, avoid accidental activation |
| Shifts and excluded breaks | Discover | Discover | Pending | Clock boundaries, timezone, excluded breaks |
| Stop reason master | Discover | Discover | Pending | Codes and reason semantics |
| Raw observations / activity | Discover | Discover | Pending | Source provenance and publication invariants |
| Supervisor production counts | Discover | Discover | Pending | Quantity units, shift/order identity, corrections |
| OEE and reporting | Discover | Discover | Pending | Authoritative operands, missing evidence, no fabricated metrics |

After comparing both inventories, specify deterministic mappings, unsupported columns, dependency order, reject reasons, dry-run reconciliation and a **source read-only / destination identity guard** before implementing any importer. Historical records that cannot satisfy V2 evidence publication rules must remain clearly marked legacy/staging data.

## Local execution

Connect SSMS to the **restored V1 development copy**, select its database explicitly, execute the script and save the five result sets. Repeat on `FactoryConnect_V2_Dev`. Verify the database name shown in each result set. Do not connect to the live Gajra server for this task. No production deployment or database changes are part of this PR.
