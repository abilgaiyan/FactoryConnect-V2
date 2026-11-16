#!/usr/bin/env python3
"""Compare schema-only V1/V2 column inventories exported from FC2-139.

Input is the FIRST result set of tools/migration/v1-v2-schema-inventory.sql,
exported as CSV independently from restored V1 and V2 development databases.
No database connections, business records, or writes are performed.
"""
import argparse
import csv
import json
from collections import Counter
from pathlib import Path

FIELDS = (
    "SchemaName", "TableName", "ColumnName", "SqlType", "MaxLengthBytes",
    "NumericPrecision", "NumericScale", "IsNullable", "IsIdentity",
    "IsComputed", "IsPrimaryKeyColumn",
)


def load(path):
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        if not reader.fieldnames or any(field not in reader.fieldnames for field in FIELDS):
            raise ValueError(f"{path}: expected first-result-set column CSV; missing required headers")
        rows = list(reader)
    result = {}
    databases = set()
    for row in rows:
        if not row.get("SchemaName") or not row.get("TableName") or not row.get("ColumnName"):
            raise ValueError(f"{path}: blank schema, table, or column")
        key = (row["SchemaName"], row["TableName"], row["ColumnName"])
        if key in result:
            raise ValueError(f"{path}: duplicate column key {key}")
        result[key] = {field: row[field].strip() for field in FIELDS[3:]}
        if row.get("DatabaseName"):
            databases.add(row["DatabaseName"].strip())
    if len(databases) > 1:
        raise ValueError(f"{path}: mixed database names")
    return result, next(iter(databases), "unknown")


def compare(source, destination):
    keys = sorted(set(source) | set(destination))
    differences = []
    counts = Counter()
    for schema, table, column in keys:
        key = (schema, table, column)
        if key not in source:
            status = "v2_only"
        elif key not in destination:
            status = "v1_only"
        elif source[key] != destination[key]:
            status = "definition_differs"
        else:
            status = "matching"
        counts[status] += 1
        if status != "matching":
            differences.append({
                "schema": schema, "table": table, "column": column,
                "status": status,
                "v1": source.get(key), "v2": destination.get(key),
            })
    return dict(sorted(counts.items())), differences


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v1", type=Path, required=True, help="Restored V1 schema columns CSV")
    parser.add_argument("--v2", type=Path, required=True, help="V2 development schema columns CSV")
    parser.add_argument("--output", type=Path, required=True, help="JSON schema diff report")
    args = parser.parse_args()
    if args.output.resolve() in (args.v1.resolve(), args.v2.resolve()):
        parser.error("output cannot overwrite either inventory")
    v1, v1_name = load(args.v1)
    v2, v2_name = load(args.v2)
    counts, differences = compare(v1, v2)
    report = {
        "source_database": v1_name, "destination_database": v2_name,
        "counts": counts, "differences": differences,
        "warning": "Schema comparison only. Matching columns do not authorize data migration.",
    }
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"v1_database": v1_name, "v2_database": v2_name, "counts": counts, "output": str(args.output)}))


if __name__ == "__main__":
    main()
