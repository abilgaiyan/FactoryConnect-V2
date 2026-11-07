# FactoryConnect V2 — Delivery roadmap

This is the V2 execution tracker. `FC-STAB-master-roadmap.md` is inherited V1 incident/stabilization history and does not establish V2 implementation completion.

Operating sequence: implement one reviewable increment → validate .NET/React and required real SQL behavior → resolve failures → squash merge to main → branch the next increment from main. The user authorized routine engineering decisions and this merge workflow on 6 November 2026. No production data is used by development or CI.

| Milestone | State and evidence | Completion condition |
| --- | --- | --- |
| FC2-001 architecture | Merged PR #1 | Independent V2 database and selective import rules |
| FC2-002 backend shell | Merged PR #2 | Preserved dashboard, .NET shell and combined CI |
| FC2-003 G1 design | Frozen `5e170d1`; design/history merged PR #3 at `4a6cf56` | Schema/package ownership and reproducibility baseline |
| FC2-003A increment 1 | Merged PR #4 at `6cfca34`; 21 SQL checks, 463 dashboard tests; final CI 42198792815 PASS | Explicit installation identity, atomic migrations, stable master IDs |
| FC2-003A increment 2 | Merged PR #5 at `6fa1a1f`; final CI 42200129572 PASS (51 acceptance checks including 12 canonical fixtures) | Golden canonical fixtures, pinned policies, submitted-content immutability and real SQL constraints |
| FC2-003B configuration lifecycle | Submission/review merged PR #20 at `2354332`; CI 42252488377 PASS (425 acceptance checks). Activation/approval withdrawal and broader authoring remain pending | Authorized author/submit/review, immutable audit and operation replay; no self-approval |
| G2 security implementation | Kernel merged PR #6 at `30896c3`; final CI 42204306784 PASS (97 combined checks). Initial bootstrap merged PR #7 at `2a222c4`; final CI 42208376879 PASS (147 acceptance checks and 15 CLI checks). Human session authority merged PR #8 at `586c66a`; CI 42209485430 PASS (172 acceptance checks). Scoped child grants merged PR #9 at `5c24588`; CI 42211129859 PASS (217 acceptance checks). Human identity lifecycle merged PR #10 at `e5ac808`; CI 42212886069 PASS (276 acceptance checks). Separate recovery trust provisioning implemented in this increment; actual recovery/rotation/tooling, authority renewal, real OIDC/browser sign-in and mTLS remain pending | Sealed dual-custodian bootstrap/recovery, OIDC/session/grant authority, delegation and revocation races, mTLS runtime trust |
| G2 runtime signing-key authority | Controlled public signing-key enrollment, rotation and revocation merged in PR #22 at `d3a4732`; CI 42259809906 PASS (527 acceptance checks); exact custody/possession binding, atomic audit and current permission checks. Runtime mTLS/X.509 identity verification, assignment authority and runtime acknowledgement remain pending | Dedicated grant plus independent custody, durable generation changes and revocations; production trust requires separately proven transport authentication |
| G2 runtime assignment authority | Scoped assignment/replacement/revocation and protected receipts merged in PR #23 at `91fc893`; CI 42261973213 PASS (572 acceptance checks). No mTLS runtime authentication or acknowledgement | Exact runtime and assignment generation, current enrolled credential precondition, independent rotation, atomic audit and revocation ordering |
| G2 runtime X.509 registration | Controlled certificate/CA pinning with new credential generations merged in PR #24 at `ce24dd5`; CI 42265580284 PASS (614 acceptance checks). Offline registration only; no authenticated runtime transport or external CA-revocation check | Exact certificate/anchor/custody/key/interval binding, atomic generation+audit and real SQL acceptance; mTLS remains a separate gate |
| G2 runtime mTLS verification | Real owned TLS 1.2/1.3 handshake, pinned direct CA/online CRL validation and exact current SQL assigned-machine read merged in PR #25 at `26da8a5`; CI 42270250093 PASS (650 acceptance checks); no default listener or HTTP composition | Real handshake/private-key possession, CA outage/revocation denial, independent current generations and both SQL revocation commit orders; deployment PKI qualification remains pending |
| G3 runtime acknowledgement | Durable exact per-machine acknowledgement and protected runtime receipts merged in PR #26 at `7d1dd3d`; CI 42274468347 PASS (696 acceptance checks). No HTTP/runtime host or active roster publication | Current approved revision/hash, distinct generation binding, idempotent atomic audit, expiry and both revocation commit orders; machine enable/suspend and full eligibility remain pending |
| G3 machine operational authority | Explicit unknown/enabled/suspended state, independent enable approval and operational-generation fencing implemented in this increment; real SQL acceptance required | Current scoped operator grants, original session/proposer revalidation, atomic audit, suspension ordering and no automatic acknowledgement revival; no physical stop claim |
| G3 safety revocation authority | Authenticated two-person policy revocation and independent approval withdrawal qualified in PR #28; CI 42344254704 PASS (794 combined checks, no skips). 100-machine withdrawal 11.92 ms/20 TDS roundtrips; active roster reader remains next | Exact revision/hash, current independent actors, scheduled/immediate cuts, atomic audit and ordering against activation/acknowledgement; no runtime-stop claim |
| Activation and policy eligibility | PR #19 contract merged at `1e4fbc2`; PR #20 supplies authenticated approval. Durable pending activation requests and intent revocation merged in PR #21 at `adc582a`; CI 42256521474 PASS (492 acceptance checks). Runtime producer trust, acknowledgement, machine disable/suspend authority and roster eligibility remain pending | Generation-bound acknowledgements, explicit effective intervals, policy revocation serialization, expiry/disablement and partial activation |
| FC2-003C roster and live dashboard | Pending | Healthy-empty versus unavailable SQL, independent commissioning discovery, exact existing decoder contract, mock-disabled integration |
| FC2-003C pre-roster internal inventory reader | Draft implementation; SQL-backed, installation-verified master machine inventory. Not an HTTP endpoint, active roster, commissioning authority, or connectivity indicator. | Exact stable machine/site identities, retirement truth, mismatched installation fail-closed and disposable SQL acceptance; publication only after G2/G3 checks |
| V1 performance regression prevention | PR #17 merged at `10719d2`; full protected inventory instrumentation, SQL budgets, cancellation and CI artifact implemented in this increment | Enforce [workload-specific gates](../implementation/FC2-V1-performance-regression-prevention.md); inventory evidence does not certify reporting/ingestion/HTTP or 100-machine production capacity |
| Acquisition | First MTConnect HTTP/mTLS -> raw evidence/cursor -> protected read implemented on `fc2/first-machine-ingestion`; final acceptance tracked in its PR | Reviewed protocol reuse with provenance/tests; durable ingestion, deduplication, checkpoint and restart/replay proof |
| Incremental reporting | Pending | Live/final summaries, late-data revision rules, explicit missing-evidence status, bounded API and scale acceptance |
| Supervisor/commissioning operations | Pending | Exact-event explanations, independent review, permissions and audit without evidence mutation |
| Isolated two-machine demo | Separate workstream | Label source period, simulated/live status and verification limitations; no V1 production writes |
| Deployment rehearsal | Pending | Installable package, explicit target/configuration, least privilege, restart/rollback and machine-data verification |
| Production cutover | Not ready | Actual target/identity provisioning and accepted commissioning evidence; no existing database replacement |

A green test for one row does not close another row. G1 stays frozen; G2/G3 engineering choices may proceed under the execution authorization, but security and runtime correctness require executable evidence before those features become available.

Trusted SQL roster reader: merged PR #29 at `56b340a`; CI 42345481289 PASS (829 combined checks, no skips; 463 dashboard tests). Complete 100-machine read used 9 TDS roundtrips; 128-sample p99 was 15.90 ms at concurrency one and 382.54 ms at eight. Logical-read attribution remains unqualified. HTTP composition remains pending.

Commissioning discovery: backend implementation on `fc2/commissioning-discovery`, with separate site-scoped permission, pre-enable/pre-ack discovery, exact requested pins, blocked/incomplete states and bounded read-order/performance acceptance. Final SQL qualification is recorded in the PR; HTTP/dashboard composition and real sign-in/runtime hosting remain pending.

## Delivery priority — 7 November 2026

The user reaffirmed the goal: a working factory system, not only a security framework. Finish commissioning discovery, then prioritize selective V1 protocol reuse and the [first machine ingestion slice](../implementation/FC2-first-machine-ingestion-slice.md) before further roster/dashboard framework completion. The next demonstration must fetch one machine's protocol observations, commit raw evidence and its checkpoint atomically, then return the saved values through a bounded protected read. Restart/replay and measured ingestion cost are required. Existing configuration, assignment, acknowledgement and revocation checks remain preconditions; no additional broad security workstream is introduced.

Commissioning discovery merged PR #30 at `1e94c91`; CI 42347061759 PASS (870 combined checks, no skips). The next implementation is now the [first machine ingestion slice](../implementation/FC2-first-machine-ingestion-implementation.md), using selective V1 protocol reuse and fresh V2 evidence storage. Production deployment remains separate.

First machine ingestion merged PR #31 at `fcfb902`; CI 42349877871 passed 920 combined checks, 463 dashboard tests and 15 CLI checks. Real HTTP/mTLS/SQL readback and retained-cursor reconstruction are qualified in a controlled fixture; 1000-observation fetch/transfer/commit took 271.83 ms and 19 ingestion-transaction TDS roundtrips.

The [single-machine acquisition host](../implementation/FC2-single-machine-acquisition-host.md) now implements serial polling, cancellation, bounded backpressure and whole-child-process restart acceptance, plus an authenticated operator console read. Final qualification is recorded in its PR. Production/fleet rollout, source-instance recovery and browser presentation remain pending; activity/OEE processing follows accepted-data visibility.

## Operator evidence delivery — PR #33

PR #32 merged at `1cca250`: continuous machine-scoped polling and whole-process
restart qualification are complete in controlled fixtures. PR #33 composes trusted
OIDC code/PKCE login, SQL-authorized machine discovery, retained evidence HTTP reads
and React display. Its acceptance includes actual HTTPS Kestrel, a signed-token
identity provider and Chromium, plus session/grant revocation and durable logout.
Final CI and merge identity are recorded in PR #33. Production IdP/PKI provisioning
and commissioning remain separate; this does not close activity, quantity or OEE.

The next product increment is accepted-evidence activity processing and an operator
activity screen, followed by production quantities and incremental shift metrics.
Broader administration, supervisor operations, deployment rehearsal and production
cutover remain open. Earlier table entries describe their historical slice scope;
later merged milestones do not imply production commissioning.


## FC2-034 evidence-processing qualification — draft PR #34

Approved mapping authority, accepted source-cut binding and immutable checkpoint ancestry are CI-qualified at `b81f30cd` ([CI #42397602730](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42397602730), all three jobs passed). The next narrow implementation adds real child-process kill/restart acceptance around the existing accepted-evidence writer; its SQL qualification is pending. It preserves same-batch links and explicit cross-batch breaks, without adding a production worker, activity endpoint or display. After this gate, qualify larger retained histories and bounded SQL work before activity API/browser delivery. Cross-batch physical completeness, production commissioning and merge readiness remain open; PR #34 stays draft.

FC2-034 restart harness `03ac8cc5` is fully CI-qualified ([CI #42399116377](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42399116377)). The next measurement-first increment uses up to 1,024 retained 100-observation batches, equivalent serial tail cuts, separate normal/profiled trials, actual SQL access counters, exact-output digests and deep-history negatives. SQL measurements and review of actual logical-read/latency/memory envelopes remain pending. No processing optimization, activity API/UI, merge or production enablement is implied.

The measurement increment is CI accepted at `0a12d431` ([CI #42401790013](https://github.com/abilgaiyan/FactoryConnect-V2/actions/runs/42401790013)): 178 dedicated history checks and a separate 1,126-check SQL regression with no skips. Equivalent serial tail cuts through 104,032 retained observations used constant 21 command / 95 statement completions, four reader and 15 apply roundtrips, and 212 observation rows read. Exact output/receipts and deep-history negatives passed. **The performance gate remains open:** batch metadata plans scanned all 34/146 retained batches at smaller depths and used seeks at 1,042; process working set also rose and remains unattributed. Next work is metadata plan stability and isolated worker-memory attribution, not premature API/UI delivery or production enablement. The implementation contract records actual profiles, timings and limitations.

## FC2-034 completion scope — 8 November 2026

Finish the evidence-processing PR before developer-machine setup. The remaining increment stabilizes bounded batch-ID seek plans across cached/refreshed statistics and isolates 128 warmup + 512 steady cuts in a fresh SQL worker process. After measured acceptance and final CI/review, close and squash merge this slice. Activity HTTPS/API, authenticated display, production worker hosting and commissioning remain separate product increments; they do not silently become completed by this merge. No local setup or new UI work is introduced here.
