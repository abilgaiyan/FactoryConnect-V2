# Raw counter inspection

The dedicated `/machines/{machineId}/counter` page provides read-only counter inspection, linked from Machine status and Activity history. It is a quantity-discovery surface, not a production-admission processor or OEE repair.

## Inputs and read boundary

`POST /api/machines/v1/{machineId}/counter/query` accepts positive half-open UTC bounds of at most 31 days and an exact, case-sensitive source/address pair. The form defaults to today's IST calendar date. Opening the page automatically discovers signals for the default dates. Dropdowns restore the machine-specific browser preference when that exact pair is available; otherwise they select the first discovered source/address pair. Press **Load counter** explicitly to load rows. Discovery uses an internal `mtconnect` / `part_count` query whose samples are discarded; this probe designates no production quantity mapping. Returned Numeric and Enumeration signal names help select the deployed raw address, including Enumeration signals whose values are missing or nonnumeric. These types do not designate counter meaning; signals may also represent speed, feed or machine status.

The single configured machine owner selects the observation stream. A stable authority read pins its acquisition revision and raw accepted-through position. State evaluation does not select the raw boundary. The reader is the selected persistence provider's own observation store; no alternate provider or fixture reader is used in production.

Raw history is traversed in durable position order through the exact boundary, retaining signal predecessors outside the selected time window. Post-boundary observations are excluded. Missing authority returns 503; malformed selection returns 400; reader/integrity failures return operation failure, never empty results. Full-prefix traversal is capped at 1,000,000 observations and selected samples at 5,000. Exceeding either limit returns 422 without partial results. Narrowing dates does not reduce the full-prefix scan; a future indexed reader is needed for larger histories.

## Evidence and flags

Raw values are invariant strings, avoiding JavaScript integer precision loss. Rows retain quality, signal type, exact UTC timestamp, position, instance, sequence and predecessor position. IST formatting is presentation only.

Arithmetic differences are displayed only between adjacent observations of the exact signal with the same instance, strictly increasing observation time, Good quality and the same signal type. Numeric values must be nonnegative integral decimals. Enumeration values must be nonempty ASCII digit strings; signs, whitespace, decimal points, exponent forms and nonnumeric tokens are not parsed. Integer text is compared with arbitrary-precision integers, while its original text (including leading zeroes) and Enumeration type remain unchanged. Text signals are not implicitly interpreted as counters. Decreases are flagged `CounterDecreased` and receive no difference. This establishes a decrease, not its cause or a confirmed reset. Instance changes, signal-type changes (`SignalTypeChanged`), missing values, unusable predecessors and non-increasing timestamps receive explicit flags and no difference. An unusable sample breaks the arithmetic chain rather than allowing comparison with an older good sample.

Every row remains `ContinuityNotEstablished` and `AttributionNotEstablished`. No counter increment is admitted as production, and no shift/day quantity total is produced. A successful empty exact-signal selection is not zero production or proof of an acquisition gap. Sequence gaps between counter samples are not used to infer omissions: other signals share the stream.

Only table rows are paged (25/50/100, default 50), with reload and page-size reset. Failed, timed-out and superseded requests cannot publish partial or late results. Available signal names and loaded metadata remain bound to the completed report when form fields change.

## Quality policy boundary

Management requested that good quantity default to admitted produced quantity and rejected quantity to zero. That is an all-good policy assumption, distinct from measured good/rejected evidence. This report labels that distinction but does not apply the policy, alter stored evidence, or publish Quality/OEE. Missing or zero production does not become 100% Quality.

## Conformance

Added Enumeration regression cases matching the supplied `M01_p1_part_count_complete` integer-text representation (151 → 152), with no claim that the synthetic fixture reproduces all 404 snapshot samples. Coverage includes leading zeroes, arbitrary-precision values, invalid tokens, decreases, instance/type changes and poor-quality predecessors. Added Core tests for exact values, signal isolation, predecessor selection, decreases, instance changes, missing/poor-quality evidence, timestamp ordering and boundary integrity. Added API tests for raw boundary exclusion, missing authority, later-page failure, premature history end and invalid selection. Frontend tests check exact references, timestamp precision, signal binding, missing values, failed retrieval and cancellation. Full .NET/frontend/typecheck/build/SQL validation runs in remote CI; results must be recorded against the exact tested commit.

No migration, factory operation, schedule change, projection change or R3243 repair is included. R3243 remains Unproven.

## Separate machine pages

The machine status page at `/machines/{machineId}` links to activity history at `/machines/{machineId}/activity` and raw counter inspection at `/machines/{machineId}/counter`. Each page has the shared company/site header and links to all three machine views. Direct URLs and browser navigation are supported. Unconfigured machine identities do not mount report forms.

Only the status page retrieves current state. The report pages retain independent editable IST date selections, existing pagination, exact evidence and limitations. Leaving a report cancels its pending request; late results cannot populate another page. Navigating back to a report starts a fresh selection rather than implying retention of a prior loaded snapshot.

### Signal dropdowns

On opening the page, signal discovery runs automatically. The last source/address pair saved in browser storage for this machine is restored only if it occurs in the returned list; otherwise the first discovered pair is selected. Browser storage is optional, and an unavailable or corrupt preference falls back safely. This default is a convenience, not an approved quantity mapping. Choose IST dates and press **Find signals** to rediscover, review the selected exact pair, then press **Load counter**. Changing the source selects its first available address; changing dates invalidates discovery and cancels pending requests. Empty discovery and failed discovery remain distinct. Discovery uses the existing exact-query API (`mtconnect` / `part_count` as an internal probe); its samples are discarded, and the range-wide signal list alone populates the controls. Existing API limits apply; selecting a discovered signal does not certify counter meaning or admit production quantity. For the inspected M01 snapshot, select `mtconnect` / `M01_p1_part_count_complete`.

## Diagnostic summary and table filter

The summary counts every retained observation as exactly one of increasing comparison, unchanged comparison, or no arithmetic comparison. Increasing comparisons count observation pairs, never produced units; differences are not summed into production totals. Flag counts overlap and include baseline-only readings; a decrease is not a confirmed reset. Universal ContinuityNotEstablished/AttributionNotEstablished flags remain on every row but are excluded from the diagnostic-issue filter so they do not select all rows.

The optional table filter selects rows with additional diagnostic flags. It resets pagination and does not alter the full loaded summary. Page changes likewise preserve the summary; loading a new report restores the unfiltered first page. A filtered empty table is distinct from a successful report containing no observations. Failed reads still remove results. No quantity admission, OEE policy, persistence migration or R3243 repair is included.
