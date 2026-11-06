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
| FC2-003B configuration lifecycle | Pending | Authorized author/submit/review, immutable audit and operation replay; no self-approval |
| G2 security implementation | Increment 1 implemented for sealed authority, scoped reads and revocation; SQL acceptance pending. Bootstrap/OIDC/grant issuance/mTLS remain pending | Sealed dual-custodian bootstrap/recovery, OIDC/session/grant authority, delegation and revocation races, mTLS runtime trust |
| Activation and policy eligibility | Pending | Generation-bound acknowledgements, explicit effective intervals, policy revocation serialization, expiry/disablement and partial activation |
| FC2-003C roster and live dashboard | Pending | Healthy-empty versus unavailable SQL, independent commissioning discovery, exact existing decoder contract, mock-disabled integration |
| Acquisition | Pending | Reviewed protocol reuse with provenance/tests; durable ingestion, deduplication, checkpoint and restart/replay proof |
| Incremental reporting | Pending | Live/final summaries, late-data revision rules, explicit missing-evidence status, bounded API and scale acceptance |
| Supervisor/commissioning operations | Pending | Exact-event explanations, independent review, permissions and audit without evidence mutation |
| Isolated two-machine demo | Separate workstream | Label source period, simulated/live status and verification limitations; no V1 production writes |
| Deployment rehearsal | Pending | Installable package, explicit target/configuration, least privilege, restart/rollback and machine-data verification |
| Production cutover | Not ready | Actual target/identity provisioning and accepted commissioning evidence; no existing database replacement |

A green test for one row does not close another row. G1 stays frozen; G2/G3 engineering choices may proceed under the execution authorization, but security and runtime correctness require executable evidence before those features become available.
