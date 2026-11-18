-- Development-only, opt-in reconciliation of two imported V1 machines.
-- Not a schema migration, not a commissioning or acquisition activation step.
-- Run against FactoryConnect_V2_Dev only. Preview by default.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Apply bit = 0; -- Change to 1 only after reviewing preview.
DECLARE @ExpectedDatabase sysname = N'FactoryConnect_V2_Dev';
DECLARE @SourceDatabase nvarchar(256) = N'FactoryConnect_P0_20261105';
DECLARE @SiteId uniqueidentifier = '54981C9D-D569-4C9F-A977-78E66D02C1F2';

IF DB_NAME() <> @ExpectedDatabase
    THROW 51200, 'This reconciliation is restricted to FactoryConnect_V2_Dev.', 1;
IF NOT EXISTS (SELECT 1 FROM master.Site WHERE SiteId = @SiteId AND IsRetired = 0)
    THROW 51201, 'Expected active development site is missing.', 1;

DECLARE @Expected table (SourceMachineId uniqueidentifier PRIMARY KEY, DeviceKey nvarchar(200) NOT NULL);
INSERT @Expected VALUES
 ('BFA0DC97-9A68-45ED-B189-1DACB7D863F0', N'DEWAS-SR-MOXA-001'),
 ('677B030B-1EDE-4A67-82AE-6A494791357F', N'DEWAS-SR-GG-003');

IF EXISTS (
 SELECT 1 FROM @Expected e
 LEFT JOIN legacy_reporting.Machine l ON l.SourceDatabase = @SourceDatabase
   AND l.SourceMachineId = e.SourceMachineId AND l.DeviceKey = e.DeviceKey
 WHERE l.SourceMachineId IS NULL
)
 THROW 51202, 'Expected imported machine identity is missing or changed.', 1;

IF EXISTS (
 SELECT 1 FROM legacy_reporting.Machine l
 JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId
 WHERE l.SourceDatabase = @SourceDatabase AND l.DeviceKey <> e.DeviceKey
)
 THROW 51203, 'Imported machine device key mismatch.', 1;

-- Prevent accidental duplicate registration where a similarly named machine exists.
-- Already mapped rows are allowed only when their target belongs to the intended site.
IF EXISTS (
 SELECT 1 FROM legacy_reporting.Machine l
 JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId
 LEFT JOIN master.Machine m ON m.MachineId = l.V2MachineId
 WHERE l.SourceDatabase = @SourceDatabase AND l.V2MachineId IS NOT NULL
 AND (m.MachineId IS NULL OR m.SiteId <> @SiteId OR m.IsRetired = 1)
)
 THROW 51204, 'Existing historical machine mapping is invalid; review manually.', 1;

IF EXISTS (
 SELECT 1 FROM @Expected e
 JOIN master.Machine m ON m.SiteId = @SiteId AND m.IsRetired = 0
   AND m.DisplayName = e.DeviceKey
 LEFT JOIN legacy_reporting.Machine l ON l.SourceDatabase = @SourceDatabase
   AND l.SourceMachineId = e.SourceMachineId
 WHERE l.V2MachineId IS NULL OR l.V2MachineId <> m.MachineId
)
 THROW 51205, 'Machine name already registered without the expected mapping; reconcile manually.', 1;

SELECT l.SourceDatabase, l.SourceMachineId, l.DeviceKey, l.V2MachineId,
 CASE WHEN l.V2MachineId IS NULL THEN 'Will register and map' ELSE 'Already mapped' END AS PlannedAction,
 @SiteId AS DestinationSiteId
FROM legacy_reporting.Machine l
JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId AND e.DeviceKey = l.DeviceKey
WHERE l.SourceDatabase = @SourceDatabase
ORDER BY l.DeviceKey;

IF @Apply = 0
BEGIN
 PRINT 'PREVIEW ONLY. No changes made. Set @Apply = 1 after review.';
 RETURN;
END;

BEGIN TRY
 BEGIN TRANSACTION;
 -- Serialize reconciliation and recheck under locks to avoid concurrent duplicate imports.
 DECLARE @LockResult int;
 EXEC @LockResult = sys.sp_getapplock
   @Resource = N'FC2-dev-V1-machine-reconciliation', @LockMode = N'Exclusive',
   @LockOwner = N'Transaction', @LockTimeout = 10000;
 IF @LockResult < 0 THROW 51206, 'Could not obtain reconciliation lock.', 1;

 IF EXISTS (
   SELECT 1 FROM legacy_reporting.Machine l WITH (UPDLOCK, HOLDLOCK)
   JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId
   WHERE l.SourceDatabase = @SourceDatabase AND l.DeviceKey <> e.DeviceKey
 ) THROW 51207, 'Imported identities changed during reconciliation.', 1;
 IF EXISTS (
   SELECT 1 FROM legacy_reporting.Machine l WITH (UPDLOCK, HOLDLOCK)
   JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId
   JOIN master.Machine m WITH (UPDLOCK, HOLDLOCK)
     ON m.SiteId = @SiteId AND m.DisplayName = e.DeviceKey
   WHERE l.SourceDatabase = @SourceDatabase
     AND (l.V2MachineId IS NULL OR l.V2MachineId <> m.MachineId)
 ) THROW 51208, 'A machine with this name was registered concurrently.', 1;

 DECLARE @NewMappings table (SourceMachineId uniqueidentifier PRIMARY KEY, V2MachineId uniqueidentifier NOT NULL);
 INSERT @NewMappings (SourceMachineId, V2MachineId)
 SELECT e.SourceMachineId, NEWID()
 FROM @Expected e
 JOIN legacy_reporting.Machine l WITH (UPDLOCK, HOLDLOCK)
   ON l.SourceDatabase = @SourceDatabase AND l.SourceMachineId = e.SourceMachineId
 WHERE l.V2MachineId IS NULL;

 INSERT master.Machine (MachineId, SiteId, DisplayName, IsRetired)
 SELECT n.V2MachineId, @SiteId, e.DeviceKey, 0
 FROM @NewMappings n JOIN @Expected e ON e.SourceMachineId = n.SourceMachineId;

 UPDATE l SET V2MachineId = n.V2MachineId
 FROM legacy_reporting.Machine l
 JOIN @NewMappings n ON n.SourceMachineId = l.SourceMachineId
 WHERE l.SourceDatabase = @SourceDatabase AND l.V2MachineId IS NULL;

 IF @@ROWCOUNT <> (SELECT COUNT(*) FROM @NewMappings)
   THROW 51209, 'Historical mapping update count mismatch.', 1;

 COMMIT TRANSACTION;
 PRINT 'Reconciliation committed. No commissioning records or live acquisition settings changed.';
END TRY
BEGIN CATCH
 IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;

SELECT l.SourceMachineId, l.DeviceKey, l.V2MachineId, m.SiteId, m.IsRetired
FROM legacy_reporting.Machine l
JOIN @Expected e ON e.SourceMachineId = l.SourceMachineId
JOIN master.Machine m ON m.MachineId = l.V2MachineId
WHERE l.SourceDatabase = @SourceDatabase;
