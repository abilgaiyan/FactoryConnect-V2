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

-- Actual row counts (not partition metadata).
SELECT N'Principal' AS TableName, COUNT_BIG(*) AS [RowCount] FROM security.Principal
UNION ALL SELECT N'PermissionGrant', COUNT_BIG(*) FROM security.PermissionGrant
UNION ALL SELECT N'GrantIssuance', COUNT_BIG(*) FROM security.GrantIssuance
UNION ALL SELECT N'GrantRevocation', COUNT_BIG(*) FROM security.GrantRevocation
UNION ALL SELECT N'SecurityOperation', COUNT_BIG(*) FROM security.SecurityOperation;

-- Existing principal IDs and types; do not disclose identity issuer/subject bytes.
SELECT PrincipalId, Kind FROM security.Principal ORDER BY Kind, PrincipalId;

-- Existing grants, including root/parent relationship and usage/delegation flags.
SELECT g.GrantId, g.GranteeId, g.GrantorId, g.ParentGrantId, g.RootOrigin,
       g.Permission, g.ScopeKind, g.SiteId, g.MachineId,
       g.UseAllowed, g.CanDelegate, g.EffectiveFrom, g.ExpiresAt,
       CASE WHEN i.GrantId IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS HasIssuance,
       CASE WHEN rv.GrantId IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IsRevoked
FROM security.PermissionGrant AS g
LEFT JOIN security.GrantIssuance AS i ON i.GrantId = g.GrantId
LEFT JOIN security.GrantRevocation AS rv ON rv.GrantId = g.GrantId
ORDER BY g.Permission, g.ScopeKind, g.GrantId;

-- Security table check constraints and their expressions.
SELECT OBJECT_NAME(cc.parent_object_id) AS TableName, cc.name AS ConstraintName,
       cc.definition AS ConstraintDefinition
FROM sys.check_constraints AS cc
WHERE SCHEMA_NAME(OBJECTPROPERTYEX(cc.parent_object_id, 'SchemaId')) = N'security'
  AND OBJECT_NAME(cc.parent_object_id) IN (N'PermissionGrant',N'Principal',N'GrantIssuance')
ORDER BY TableName, ConstraintName;
