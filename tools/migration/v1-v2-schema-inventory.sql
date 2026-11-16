/*
FC2-139 / Issue #132 — read-only V1 and V2 schema inventory.
Run separately against a RESTORED, ISOLATED V1 database and a V2 development database.
This script reads SQL Server catalog metadata only; it never reads business table rows
and does not write, migrate, activate or commission anything.
Save each result set separately (V1 and V2) for mapping review.
*/
SET NOCOUNT ON;
SELECT DB_NAME() AS DatabaseName, SCHEMA_NAME(t.schema_id) AS SchemaName,
       t.name AS TableName, c.column_id AS ColumnOrdinal, c.name AS ColumnName,
       ty.name AS SqlType, c.max_length AS MaxLengthBytes,
       c.precision AS NumericPrecision, c.scale AS NumericScale,
       c.is_nullable AS IsNullable, c.is_identity AS IsIdentity,
       c.is_computed AS IsComputed,
       CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKeyColumn
FROM sys.tables AS t
JOIN sys.columns AS c ON c.object_id = t.object_id
JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
OUTER APPLY (
  SELECT ic.column_id FROM sys.indexes AS i
  JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
  WHERE i.object_id = t.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id
) AS pk
WHERE t.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(t.schema_id), t.name, c.column_id;

SELECT DB_NAME() AS DatabaseName,
       SCHEMA_NAME(parent.schema_id) AS FromSchema, parent.name AS FromTable,
       fk.name AS ForeignKeyName, pc.name AS FromColumn,
       SCHEMA_NAME(referenced.schema_id) AS ToSchema, referenced.name AS ToTable,
       rc.name AS ToColumn, fkc.constraint_column_id AS ColumnOrdinal
FROM sys.foreign_keys AS fk
JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables AS parent ON parent.object_id = fk.parent_object_id
JOIN sys.tables AS referenced ON referenced.object_id = fk.referenced_object_id
JOIN sys.columns AS pc ON pc.object_id = parent.object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns AS rc ON rc.object_id = referenced.object_id AND rc.column_id = fkc.referenced_column_id
ORDER BY FromSchema, FromTable, ForeignKeyName, ColumnOrdinal;

SELECT DB_NAME() AS DatabaseName, SCHEMA_NAME(t.schema_id) AS SchemaName,
       t.name AS TableName, i.name AS IndexName, i.is_unique AS IsUnique,
       i.is_primary_key AS IsPrimaryKey, ic.key_ordinal AS KeyOrdinal,
       c.name AS ColumnName, ic.is_included_column AS IsIncludedColumn
FROM sys.tables AS t
JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.index_id > 0
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = t.object_id AND c.column_id = ic.column_id
WHERE t.is_ms_shipped = 0
ORDER BY SchemaName, TableName, IndexName, KeyOrdinal, ColumnName;

SELECT DB_NAME() AS DatabaseName, SCHEMA_NAME(t.schema_id) AS SchemaName,
       t.name AS TableName, cc.name AS CheckConstraintName, cc.definition AS CheckDefinition
FROM sys.check_constraints AS cc
JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
WHERE t.is_ms_shipped = 0
ORDER BY SchemaName, TableName, CheckConstraintName;

SELECT DB_NAME() AS DatabaseName, SCHEMA_NAME(t.schema_id) AS SchemaName,
       t.name AS TableName, SUM(p.rows) AS ApproximateRowCount
FROM sys.tables AS t
JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0
GROUP BY t.schema_id, t.name
ORDER BY SchemaName, TableName;
