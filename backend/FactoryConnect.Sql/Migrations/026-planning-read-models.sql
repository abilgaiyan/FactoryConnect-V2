-- FC2 planning read models for live administration and supervisor reporting.
-- Breaks are stored as offsets from the shift start so midnight crossings are unambiguous.
CREATE VIEW planning.vShiftCalendar
AS
SELECT s.SiteId, s.ShiftId, s.DisplayName, s.StartMinute, s.EndMinute, s.IsRetired,
       CASE WHEN s.EndMinute > s.StartMinute
            THEN CONVERT(int,s.EndMinute) - CONVERT(int,s.StartMinute)
            ELSE 1440 - CONVERT(int,s.StartMinute) + CONVERT(int,s.EndMinute)
       END AS ShiftDurationMinutes,
       COALESCE(b.ExcludedBreakMinutes, 0) AS ExcludedBreakMinutes,
       CASE WHEN s.EndMinute > s.StartMinute
            THEN CONVERT(int,s.EndMinute) - CONVERT(int,s.StartMinute)
            ELSE 1440 - CONVERT(int,s.StartMinute) + CONVERT(int,s.EndMinute)
       END - COALESCE(b.ExcludedBreakMinutes, 0) AS PlannedProductionMinutes
FROM planning.ShiftDefinition s
OUTER APPLY (
    SELECT SUM(CONVERT(int, DurationMinute)) AS ExcludedBreakMinutes
    FROM planning.ShiftBreak
    WHERE ShiftId = s.ShiftId AND ExcludeFromPlannedProduction = 1
) b;
GO
CREATE VIEW planning.vLatestShiftTarget
AS
SELECT TargetRevisionId, SiteId, MachineId, ShiftId, BusinessDate, Revision,
       TargetQuantity, QuantityUnit, ProductionOrder, PartNumber, Operation,
       RevisionReason, RecordedAtUtc, RecordedByPrincipalId
FROM (
    SELECT t.*, ROW_NUMBER() OVER (
        PARTITION BY t.SiteId, t.MachineId, t.ShiftId, t.BusinessDate
        ORDER BY t.Revision DESC
    ) AS RevisionRank
    FROM planning.ShiftTargetRevision t
) ranked
WHERE RevisionRank = 1;
GO
