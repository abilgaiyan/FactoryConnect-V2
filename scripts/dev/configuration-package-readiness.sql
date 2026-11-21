-- Read-only readiness for the first local simulator configuration package.
-- No authorization bypass, no package submission, no machine activation.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51050, 'Package readiness is restricted to FactoryConnect_V2_Dev.', 1;

DECLARE @site uniqueidentifier='54981C9D-D569-4C9F-A977-78E66D02C1F2';
DECLARE @machine uniqueidentifier='49BEFB26-0AC8-4BDF-87FE-A4FF9E6F3BFD';
DECLARE @line uniqueidentifier='E0C48C4B-A48D-4D34-A830-BCA7A599BD1D';
DECLARE @timezone uniqueidentifier='A71C15B1-5EC1-49F5-A038-9E430D3C342A';
DECLARE @timezoneHash binary(32)=CONVERT(binary(32),'A5547DC0B1A6FAABF40BEA0914ACC52FB7C31ECB1CCA40E80A09DAC7D4FD0CC1',2);
DECLARE @scheduleHash binary(32)=CONVERT(binary(32),'3CA8364A62044979CC5D4CB80923452C2F571E1C3442B1A99EDDCC6ACDB2C1AE',2);
DECLARE @breakHash binary(32)=CONVERT(binary(32),'080E255B11B73DA83AB209A7A98EC4AADC945A0D8A716367E5209567940C4B00',2);

IF NOT EXISTS(SELECT 1 FROM [master].Site WHERE SiteId=@site AND IsRetired=0)
    THROW 51051, 'Expected active development site is missing.',1;
IF NOT EXISTS(SELECT 1 FROM [master].Machine WHERE SiteId=@site AND MachineId=@machine AND IsRetired=0)
    THROW 51052, 'Expected active local simulator machine is missing.',1;
IF NOT EXISTS(SELECT 1 FROM [master].ProductionLine WHERE SiteId=@site AND ProductionLineId=@line)
    THROW 51053, 'Expected production line is missing for this site.',1;
IF NOT EXISTS(SELECT 1 FROM configuration.SiteTimezoneRevision
              WHERE SiteId=@site AND TimezoneRevisionId=@timezone AND ContentHash=@timezoneHash
                AND IanaZoneId=N'Asia/Kolkata' AND TzdbRelease='2025b'
                AND ResolverVersion=N'fc2-pinned-tzif-v1'
                AND GapPolicy='reject' AND FoldPolicy='reject'
                AND RulesSha256=HASHBYTES('SHA2_256',RulesBytes))
    THROW 51054, 'Timezone revision does not match the verified immutable pin.',1;
IF (SELECT COUNT_BIG(*) FROM configuration.SharedScheduleRevision WHERE SiteId=@site AND ContentHash=@scheduleHash)<>1
    THROW 51055, 'Expected exactly one matching schedule revision.',1;
IF (SELECT COUNT_BIG(*) FROM configuration.SharedBreakPolicyRevision WHERE SiteId=@site AND ContentHash=@breakHash)<>1
    THROW 51056, 'Expected exactly one matching break revision.',1;

SELECT 'timezone' AS PolicyKind,TimezoneRevisionId AS RevisionId,CONVERT(varchar(64),ContentHash,2) AS ContentHash
FROM configuration.SiteTimezoneRevision WHERE SiteId=@site AND TimezoneRevisionId=@timezone
UNION ALL
SELECT 'schedule',ScheduleRevisionId,CONVERT(varchar(64),ContentHash,2)
FROM configuration.SharedScheduleRevision WHERE SiteId=@site AND ContentHash=@scheduleHash
UNION ALL
SELECT 'break',BreakPolicyRevisionId,CONVERT(varchar(64),ContentHash,2)
FROM configuration.SharedBreakPolicyRevision WHERE SiteId=@site AND ContentHash=@breakHash;

SELECT s.SiteId,s.DisplayName AS SiteName,m.MachineId,m.DisplayName AS MachineName,
       l.ProductionLineId,l.DisplayName AS LineName
FROM [master].Site s
JOIN [master].Machine m ON m.SiteId=s.SiteId
JOIN [master].ProductionLine l ON l.SiteId=s.SiteId
WHERE s.SiteId=@site AND m.MachineId=@machine AND l.ProductionLineId=@line;

SELECT r.PackageRevisionId,r.RevisionNumber,r.SubmittedAt,
       CONVERT(varchar(64),r.ContentHash,2) AS ContentHash
FROM configuration.ConfigurationPackageRevision r
JOIN configuration.PackageMachineScope s ON s.PackageRevisionId=r.PackageRevisionId
WHERE r.SiteId=@site AND s.MachineId=@machine
ORDER BY r.SubmittedAt DESC;

PRINT 'PASS: local simulator package policy and master-data readiness; no writes performed.';
PRINT 'NOT CHECKED: signal mappings, effective dates, operator permissions, independent reviewer, runtime activation.';
