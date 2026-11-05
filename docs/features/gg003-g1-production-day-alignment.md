# GG-003 G1 development change

Baseline: `4ab392752380f4fe206b5fcf736a0eec8a9b965f`.

## Configuration authority

The default Edge configuration carries operational defaults only. Machine acquisition
examples and production assignments reside in `appsettings.Demo.json`, selected by
`DOTNET_ENVIRONMENT=Demo`. Production must supply acquisition inventory, contexts,
planned production, shared shifts and their timezone explicitly. The production
template is a commissioning skeleton; it cannot start without completed assignments.
Deployment and rehearsal scripts already providing complete assignments remain valid.

## Business dates and physical intervals

Shift assignments and planned windows accept `StartDayOffset` 0 or 1 (default 0).
The offset changes local timestamp construction; effectiveness, active weekdays,
overrides, `FactoryDate`, production-day identities and roster ownership use the
original business date. `IsOvernight` retains its existing meaning.

Edge supports either legacy scalar planned times or `PlannedWindows[]`, and rejects
mixed representations. Break windows support the same offset. An omitted break
offset is inferred only when exactly one placement (0 or 1) belongs to one ordinary
planned window. Explicit 0 is distinct from omission for break placement. Overlapping
breaks and ambiguous ownership fail. Replacement overrides inherit resolved ordinary
breaks, ignore nonintersecting breaks and clip intersecting breaks once; a break
intersecting multiple replacement windows fails.

Resolvers inspect two neighboring business dates on each side and reject physical
collisions before returning only requested business dates. Planned collisions are
checked before break subtraction. Shift collection validation includes neighboring
effective assignments even when their business-date effective ranges do not overlap.
The upstream UTC date padding remains a bounded assumption for the Gajra schedule;
this change does not claim universal timezone support for arbitrary offset schedules.

## Review and validation

New tests cover offset bounds; three-shift gross/net duration; midnight and 07:30
adjacency; final effective business date; offset-1 overnight collisions at transitions;
replacement break clipping/exclusion; legacy inference; ambiguous/overlapping breaks;
Edge array binding; effective Production/Demo host configuration; and actual activity
and quantity publication through the roster ownership guard for a historical date.
Existing regression and SQL integration suites remain required gates.

Local validation: `git diff --check` passes. No .NET SDK is installed in the authoring
workspace, so compilation and all test execution are **pending CI**, not reported as
passing. CI completion is managed by the user; do not merge based on this document.

Suggested focused commands:

```powershell
dotnet test tests/FactoryConnect.Core.Tests --filter FullyQualifiedName~ProductionDayOffsetTests
dotnet test tests/FactoryConnect.Edge.Tests --filter "FullyQualifiedName~EdgeConfigurationContractTests|FullyQualifiedName~ProductionConfigurationCompositionTests"
dotnet test FactoryConnect.sln
```

Merge, fresh V2 database creation and production deployment are held. GG-003
acquisition and existing SQL remain unchanged. Confirm the proposed 07:30 business-day
convention with Gajra before cutover.

## PR #146 review corrections

Removed CA1805's redundant zero initializer. Shift collection checks now intersect
both effective ranges for each relative business-date displacement (-2 through +2),
then inspect one full weekday cycle. This includes final old occurrences at adjacent
or one-day-separated transitions, independent of insertion order.

Resolver lookaround dates clamp to the DateOnly calendar; the original nonempty,
ordered requested-range check remains. An actual offset interval extending beyond
the representable calendar fails explicitly rather than overflowing AddDays.
Planned gross overlap comparisons carry the selected line scope (including fallback
site authority projected onto a requested line). Other line authorities do not
participate in that resolution. Tests cover site-only reads, selected line precedence,
and fallback across effective dates. Ordinary break ownership remains mandatory even
when a shortened replacement would exclude an invalid break.

Review validation: whitespace checks pass. An independent exhaustive arithmetic
comparison against brute-force occurrence pairs passed 3,136 effective-range,
weekday and offset combinations. This is supplementary evidence, not a .NET test
result. The local environment still has no SDK; updated .NET and SQL results remain
pending the new CI run. Merge and deployment remain held.

## Calendar-limit review policy

A requested owner whose physical interval exceeds DateOnly.MaxValue still fails
explicitly. An unrepresentable occurrence examined only on a lookaround business
date is skipped; representable sibling windows remain in collision validation.
This avoids rejecting a valid requested range for an unrelated future occurrence.
A focused regression covers both resolvers, break subtraction, successful requested
ownership and rejection when the invalid owner itself is requested.

The CI run for 464ee19c failed during build on CA1859 in ReadPlannedActiveDays.
Its private return type is now HashSet<DayOfWeek>, as required by the analyzer.
Updated .NET validation remains pending CI; no local SDK is available.

## Provider-boundary regression repair

The non-SQL CI test run compiled and reported three existing Core provider-boundary
failures: out-of-request shift assignments, shift overrides and planned assignments
were accepted after widening the provider request. The original rejection tests are
unchanged. Both resolvers now first read and validate the caller's exact date range,
then independently read and validate the padded collision-check range. Neighboring
assignments and overrides remain available for collision resolution without widening
the original provider admission contract. Planned provider validation is shared by
both reads. The prior calendar-limit policy remains unchanged.

The supplied run passed the other 2,055 non-SQL tests (including 871 Core tests),
with 3 failures. This is not SQL integration evidence. Whitespace checks pass for
the repair; execution of the updated .NET tests remains pending CI.
