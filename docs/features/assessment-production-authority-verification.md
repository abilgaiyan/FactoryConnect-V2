# Assessment-production authority verification — first implementation slice

Baseline: `76d4f4a657c7e18c7d0277583cca6bbf4a572c08` (merged main).
Semantic and concrete API decisions are frozen. This implementation adds typed immutable inputs, exact resolution, deterministic verification and synthetic conformance tests. It provides the historical-analytics foundation for the 12 October demo.

## Admission boundary

The verifier issues admission for one exact claim, its complete aggregation checkpoint A, retained immutable inputs and exact policy. Admission is not a coverage classification, evidence-quality ranking, durable publication or factory authority designation. A later authority claim can describe A, but its contribution positions cannot exceed A. Unknown machine state is an accounted fragment; missing duration alone does not establish a gap. Completion requires explicit authorized fragments/gaps accounting for the period, with its boundary exactly equal to that period’s end. Schedule completeness is explicit; an authorized incomplete schedule claim cannot establish empty expected production intervals.

There are no production readers, trust-anchor registrations, producer/evaluator wiring, migrations, backfills or operational metric changes in this slice. Existing I1/I2 storage remains unchanged. R3243 remains **Unproven**. Synthetic references and positions used by tests establish verifier behavior only.

## Contracts and API

| Surface | Contract |
| --- | --- |
| Typed references | Domain, identity and opaque exact revision; separate claim, authorization, designation, revocation, completeness and policy reference types. No inferred revision ordering. |
| Scope/grants | Explicit company/site, line or machine variants. One correlated grant must cover claim kind, scope, period and historical issuance; unrelated grants cannot be combined into expanded permissions. |
| Intervals | Existing positive half-open UTC tick intervals; ordered fragments remain distinct. |
| Claims | Closed schedule, authoritative fragment, explicit gap and completion content variants; kind derived from content. |
| `ResolveExactAsync(request, token)` | Reads only the explicitly selected references through `IAssessmentAuthorityExactReader`. Returns `Resolved`, `Unavailable` or `Failed`; cancellation propagates. |
| Established absence | Exact requested reference plus successful `AuthorityExactLookup.Absent`; omitted lookups and unresolved dependencies are validation failures. |
| `Verify(inputs, token)` | Synchronous deterministic operation over retained inputs only. Validates snapshot consistency at entry; no I/O, clock or mutable registry lookup. |
| Results | Only `Authorized` carries admission. `Rejected`, `NotEstablished`, `Unavailable` and `Failed(OperationError)` do not. The pure supported verifier does not manufacture availability failures; adapters can represent them explicitly. |
| Errors | Typed malformed request, corrupt content, reference mismatch and unsupported representation errors, with diagnostic code and optional exact reference. Unexpected reader exceptions propagate; reader adapters must explicitly identify retrieval unavailability. |

Collections are defensive immutable copies with exact ordered equality. Strings use ordinal .NET equality and retain UTF-16 code units, including unpaired surrogates. No durable input codec is introduced. An admission cannot be externally constructed; `Matches` checks exact claim content, checkpoint, retained snapshot and policy. The `Resolved` constructor conveys no trust.

## V1 verification policy

The supported implementation identity is `FactoryConnect.AssessmentAuthorityVerifier/V1`, representation version `1`. Both and the exact policy revision/content are retained. Changes to these rules require a new supported implementation/representation rather than rewriting V1 semantics. Reproduction requires retaining these immutable values, exact successful absence outcomes, verification time and the corresponding V1 implementation binary/source revision.

The policy pins the complete designation values that an external business/commissioning authority has explicitly selected. Merely resolving a designation does not trust it. Authenticity and designation of those roots belong to that external process and eventual reader/configuration adapters; this pure verifier validates their supplied exact binding and permissions, without claiming cryptographic or factory verification. No actual root or revocation source is appointed here.

Delegation is checked at issuance, through an immutable chain terminating at a pinned designation. Every child grant and issuance window must narrow a permitting parent; historical issuance and further delegation require explicit permission. Expiry at verification time does not invalidate claims issued within their authorization window. Authorization, claim and decision issuance cannot occur after the retained verification time.

V1 requires revocation completeness through the retained verification time for each used delegation link, applicable scope and period, from the designation's explicit revocation source. Completeness cannot claim a horizon later than its issuance, and issuance cannot follow verification. Thus this strict V1 uses an exact as-of completeness horizon; an older horizon produces `NotEstablished`. A future bounded/stale-horizon policy requires an explicitly supported different implementation. Claims issued earlier can be verified at this retained as-of time.

A completeness manifest lists exact decision revisions. Known selected decisions affecting a covered link through its horizon cannot be omitted. Prospective decisions cannot become effective before issuance. Retrospective decisions require the separate retrospective permission. Relevant authorized decisions apply at the authorization's use time, including delegation issuance. Any positive period overlap and intersecting explicit scope prevents admission of the whole claim; a narrower machine/line revocation also affects a broader containing scope. Adjacent half-open periods and unrelated scopes do not overlap. Revocation findings are deferred until decision/completeness issuers' own dependencies are established. Unestablished or disqualified supporting issuer authority cannot establish rejection of the assessed claim.

All selected immutable references must resolve exactly once or have established absence. Conflicting content, reference substitution, malformed snapshots and static dependency cycles fail distinctly. Authority/completeness proof dependencies must also be finite and acyclic; self-issued completeness cannot bootstrap its own authorization. Different applicable claims for the same subject/kind, scope, period and A with different content remain `NotEstablished` under V1's conservative conflict policy. Distinct fragment identities do not conflict merely because they share a period. No automatic evidence preference or reassessment occurs.

## Conformance and local verification

Synthetic tests cover direct/delegated authority, narrowing, issuance/expiry, historical permission, explicit revocation effects, incomplete horizons/chains/sources, manifests, cycles, established absence, malformed or substituted snapshots, unsupported policies, conflict handling, exact strings, fragmentation, completion accounting, post-A rejection, cancellation and retained-input reproduction.

Run `tests/deployment/Invoke-AssessmentAuthorityConformance.ps1 -ExpectedSha <exact-implementation-SHA>` in a clean checkout with .NET 10. It checks the exact SHA/worktree, diff whitespace, Release solution build, full Core suite and integration tests excluding `Category=SqlServerIntegration`. It performs no SQL or factory operations. Review the complete diff against the baseline separately. No independent review or factory authority evidence is claimed by these tests.

Implementation verification in the remote workspace (.NET SDK 10.0.401, Release):

| Check | Evidence |
| --- | --- |
| Solution build | PASS, 0 warnings / 0 errors |
| Core | 656/656 PASS, 0 skipped (52 new authority cases) |
| Non-SQL integration | 681/681 PASS, 0 skipped |
| Diff review | Implementer reviewed the final contracts, resolver, verifier, synthetic tests and documentation; independent review remains a separate acceptance action. |

Package restoration required rebuilding this workspace's incomplete NuGet cache from public packages; repository dependency versions and configuration were not changed. No real-SQL or factory operations were executed. The PowerShell acceptance runner still requires execution on the local Windows checkout.

Review corrections: partial-period and narrower-scope revocation now block whole-claim admission; completion cannot claim a boundary beyond its accounted period. Five regression cases cover both blockers and non-overlapping controls.
