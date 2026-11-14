-- FC2 planning integrity: enforce the shift calendar at the persistence boundary.
-- Intervals are half-open [start,end); adjacent shifts/breaks are valid.
CREATE TRIGGER planning.TR_ShiftDefinition_NoOverlap
ON planning.ShiftDefinition AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM planning.ShiftDefinition a
        JOIN planning.ShiftDefinition b
          ON b.SiteId = a.SiteId AND b.ShiftId <> a.ShiftId
         AND b.IsRetired = 0
        WHERE a.IsRetired = 0
          AND EXISTS (SELECT 1 FROM inserted i WHERE i.ShiftId = a.ShiftId OR i.ShiftId = b.ShiftId)
          AND (
              (a.StartMinute < CASE WHEN b.EndMinute > b.StartMinute THEN b.EndMinute ELSE b.EndMinute + 1440 END
               AND b.StartMinute < CASE WHEN a.EndMinute > a.StartMinute THEN a.EndMinute ELSE a.EndMinute + 1440 END)
              OR
              (a.StartMinute < (CASE WHEN b.EndMinute > b.StartMinute THEN b.EndMinute ELSE b.EndMinute + 1440 END) - 1440
               AND b.StartMinute - 1440 < CASE WHEN a.EndMinute > a.StartMinute THEN a.EndMinute ELSE a.EndMinute + 1440 END)
              OR
              (a.StartMinute < (CASE WHEN b.EndMinute > b.StartMinute THEN b.EndMinute ELSE b.EndMinute + 1440 END) + 1440
               AND b.StartMinute + 1440 < CASE WHEN a.EndMinute > a.StartMinute THEN a.EndMinute ELSE a.EndMinute + 1440 END)
          )
    ) THROW 51041, 'Active shifts at the same site must not overlap.', 1;

    -- A shift edit must not strand an existing break outside its new duration.
    IF EXISTS (
        SELECT 1 FROM inserted s
        JOIN planning.ShiftBreak b ON b.ShiftId = s.ShiftId
        WHERE b.StartOffsetMinute + b.DurationMinute >
          CASE WHEN s.EndMinute > s.StartMinute THEN s.EndMinute - s.StartMinute
               ELSE 1440 - s.StartMinute + s.EndMinute END
    ) THROW 51042, 'Shift change would leave a break outside its shift.', 1;
END;
GO
CREATE TRIGGER planning.TR_ShiftBreak_NoOverlap
ON planning.ShiftBreak AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted a
        JOIN planning.ShiftBreak b ON b.ShiftId = a.ShiftId AND b.BreakId <> a.BreakId
        WHERE a.StartOffsetMinute < b.StartOffsetMinute + b.DurationMinute
          AND b.StartOffsetMinute < a.StartOffsetMinute + a.DurationMinute
    ) THROW 51043, 'Breaks within a shift must not overlap.', 1;
END;
GO
-- Revision history is immutable. New targets are represented by new rows.
CREATE TRIGGER planning.TR_ShiftTargetRevision_Immutable
ON planning.ShiftTargetRevision INSTEAD OF UPDATE, DELETE AS
BEGIN
    THROW 51044, 'Shift target revisions are immutable; create a new revision.', 1;
END;
GO
