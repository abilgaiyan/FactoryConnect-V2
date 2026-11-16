# FC2-140 — Compare restored V1 and V2 SQL schema inventories

This is a **schema-only offline comparison**, not a data migration. Run the read-only
`tools/migration/v1-v2-schema-inventory.sql` script from FC2-139 against the
restored **V1 development copy** and separately against `FactoryConnect_V2_Dev`.
Export the **first result set** (column inventory) of each execution as a CSV
with its column headers, for example `v1-columns.csv` and `v2-columns.csv`.

Run with Python 3:

```powershell
python tools/migration/compare_v1_v2_schema.py --v1 v1-columns.csv --v2 v2-columns.csv --output schema-diff.json
```

The resulting JSON identifies V1-only columns, V2-only columns and same-name
columns with different SQL types, lengths, precision, scale, nullability,
identity, computed or primary-key status. Exact matches are counted, not
listed individually. The output is **not** a mapping: renames, domain semantics,
foreign-key relationships and business data remain to be reviewed manually.

Before sharing schema inventories or diff files, inspect database names and
schema metadata for sensitive information. Do not use a live factory database,
copy production records or run any write operation. This script never connects
to SQL Server and cannot modify either database.

## Next implementation gate

Review the generated differences and the FC2-139 FK/index/check inventories
to map site, machine, shift, break, stop reason, production quantity and activity
domains. Specify deterministic identity mapping, V2 installation preflight,
read-only source enforcement and dry-run reconciliation before writing a guarded
development-only importer. Do not forge V2 publication or commissioning evidence.
