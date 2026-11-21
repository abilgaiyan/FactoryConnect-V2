-- Local development only. Read-only preflight for policy bootstrap.
-- Execute against FactoryConnect_V2_Dev; refuses any other database.
SET NOCOUNT ON;
IF DB_NAME() <> N'FactoryConnect_V2_Dev'
    THROW 51040, 'Policy preflight is restricted to FactoryConnect_V2_Dev.', 1;

DECLARE @site uniqueidentifier='54981C9D-D569-4C9F-A977-78E66D02C1F2';
IF NOT EXISTS(SELECT 1 FROM [master].Site WHERE SiteId=@site AND IsRetired=0)
    THROW 51041, 'Expected active development site is missing.', 1;
IF (SELECT COUNT(*) FROM planning.ShiftDefinition WHERE SiteId=@site AND IsRetired=0)<>3
    THROW 51042, 'Expected exactly three active shifts.', 1;
IF EXISTS(
    SELECT 1 FROM planning.ShiftDefinition s
    LEFT JOIN planning.ShiftBreak b ON b.ShiftId=s.ShiftId
    WHERE s.SiteId=@site AND s.IsRetired=0
    GROUP BY s.ShiftId
    HAVING COUNT(b.BreakId)<>1
        OR MIN(CONVERT(int,b.ExcludeFromPlannedProduction))<>1
        OR MIN(b.DurationMinute)<>30 OR MAX(b.DurationMinute)<>30)
    THROW 51043, 'Each shift must have exactly one excluded 30-minute break.', 1;
IF NOT EXISTS(SELECT 1 FROM planning.ShiftDefinition WHERE SiteId=@site AND IsRetired=0 AND StartMinute=450 AND EndMinute=960)
 OR NOT EXISTS(SELECT 1 FROM planning.ShiftDefinition WHERE SiteId=@site AND IsRetired=0 AND StartMinute=960 AND EndMinute=0)
 OR NOT EXISTS(SELECT 1 FROM planning.ShiftDefinition WHERE SiteId=@site AND IsRetired=0 AND StartMinute=0 AND EndMinute=450)
    THROW 51044, 'Shift boundaries differ from the validated local commissioning schedule.', 1;
IF EXISTS(
    SELECT 1 FROM planning.ShiftDefinition s
    JOIN planning.ShiftBreak b ON b.ShiftId=s.ShiftId
    WHERE s.SiteId=@site AND s.IsRetired=0
      AND b.StartOffsetMinute<>CASE s.StartMinute WHEN 0 THEN 210 ELSE 240 END)
    THROW 51045, 'Break offsets differ from the validated local commissioning schedule.', 1;

SELECT s.ShiftId,s.DisplayName,s.StartMinute,s.EndMinute,
       b.BreakId,b.StartOffsetMinute,b.DurationMinute,b.ExcludeFromPlannedProduction
FROM planning.ShiftDefinition s
JOIN planning.ShiftBreak b ON b.ShiftId=s.ShiftId
WHERE s.SiteId=@site AND s.IsRetired=0
ORDER BY s.StartMinute,s.ShiftId;

SELECT 'timezone' AS PolicyKind,COUNT_BIG(*) AS ExistingRevisions FROM configuration.SiteTimezoneRevision WHERE SiteId=@site
UNION ALL SELECT 'schedule',COUNT_BIG(*) FROM configuration.SharedScheduleRevision WHERE SiteId=@site
UNION ALL SELECT 'break',COUNT_BIG(*) FROM configuration.SharedBreakPolicyRevision WHERE SiteId=@site;

PRINT 'PASS: local policy bootstrap preflight; no writes performed.';
