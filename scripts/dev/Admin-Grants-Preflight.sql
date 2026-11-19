-- Local development administrator grant preflight. READ ONLY.
-- No grants, principals, or authority epochs are modified.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51000, 'This inspection is restricted to FactoryConnect_V2_Dev.', 1;

SELECT DB_NAME() AS DatabaseName, N'grant-preflight-read-only' AS Mode;

SELECT s.name AS SchemaName,t.name AS TableName,c.column_id AS Ordinal,
       c.name AS ColumnName,ty.name AS SqlType,c.is_nullable AS Nullable,
       c.is_identity AS IdentityColumn
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
JOIN sys.columns AS c ON c.object_id=t.object_id
JOIN sys.types AS ty ON ty.user_type_id=c.user_type_id
WHERE s.name=N'security'
  AND t.name IN (N'Principal',N'PermissionGrant',N'GrantIssuance',N'GrantRevocation',
                 N'SecurityOperation',N'GrantAncestry',N'AuthorityState')
ORDER BY t.name,c.column_id;

SELECT s.name AS SchemaName,t.name AS TableName,k.name AS ConstraintName,
       k.type_desc AS ConstraintType
FROM sys.key_constraints AS k
JOIN sys.tables AS t ON t.object_id=k.parent_object_id
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
WHERE s.name=N'security' AND t.name IN (N'Principal',N'PermissionGrant',N'GrantIssuance',N'SecurityOperation')
ORDER BY t.name,k.name;

SELECT s.name AS SchemaName,t.name AS TableName, fk.name AS ForeignKeyName,
       COL_NAME(fkc.parent_object_id,fkc.parent_column_id) AS FromColumn,
       OBJECT_SCHEMA_NAME(fkc.referenced_object_id)+N'.'+OBJECT_NAME(fkc.referenced_object_id) AS ToTable,
       COL_NAME(fkc.referenced_object_id,fkc.referenced_column_id) AS ToColumn
FROM sys.foreign_keys AS fk
JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id=fk.object_id
JOIN sys.tables AS t ON t.object_id=fk.parent_object_id
JOIN sys.schemas AS s ON s.schema_id=t.schema_id
WHERE s.name=N'security' AND t.name IN (N'Principal',N'PermissionGrant',N'GrantIssuance',N'SecurityOperation')
ORDER BY t.name,fk.name,fkc.constraint_column_id;

SELECT s.name AS SchemaName,t.name AS TableName,COUNT_BIG(*) AS RowCount
FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id=t.schema_id
JOIN sys.partitions AS p ON p.object_id=t.object_id AND p.index_id IN (0,1)
WHERE s.name=N'security' AND t.name IN (N'Principal',N'PermissionGrant',N'GrantIssuance',N'GrantRevocation',N'SecurityOperation')
GROUP BY s.name,t.name
ORDER BY t.name;
