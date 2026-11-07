# G3 policy safety revocation and approval withdrawal

Status: backend implementation qualified in PR #28, CI 42344254704. No production migration or host deployment.

## Commands and authority

A shared timezone, schedule or break policy can be revoked only by a site-scoped `policy.revoke` user proposing the exact immutable revision/hash and a different currently authorized user approving it. All revocations in this increment are explicitly safety-critical; ordinary policy replacement remains a separate configuration operation. The proposal retains its originating authenticated session, expires within 15 minutes, and is inspectable by another current policy revoker. Approval rechecks both users, original session, grant ancestry, exact policy canonical hash and fresh SQL time after resource acquisition.

An immediate proposal has no requested future instant. A scheduled proposal requires a future UTC instant when proposed. Approval stores the later of that instant and fresh SQL UTC time. Visibility still requires transaction commit: `RecordedAt` and `EffectiveFrom` are not commit tokens. Activation and acknowledgement check committed revocation facts directly under their shared serialization contract. No timer job is required. An earlier safety cut may be appended through a fresh independently approved proposal; a later cut or duplicate effect cannot postpone an existing cut. Earlier history remains immutable. There is no unrevocation.

Approval withdrawal requires current site-scoped `configuration.review`, exact approved revision/hash/review and an actor independent of both author and submitter. Any such reviewer may withdraw; the original reviewer need not remain available. Withdrawal is immediate on commit visibility. Review history remains approved as a historical decision, accompanied by a separate immutable withdrawal fact and exact provenance. A new revision and independent approval are required for a replacement. Editing, activation, suspension and security administration confer no implicit review/revocation authority.

All commands bind durable operation IDs to actor and exact canonical request bytes. Exact completed retries require current authority and return the original receipt without reapplying state. Private receipt reads require the originating actor and current action permission. A recorded withdrawal/revocation means database ineligibility; it is not proof that a physical runtime stopped or received a notification.

## Storage and ordering

Migration 016 adds immutable policy proposals, two-person revocation provenance and independent withdrawal provenance. It preserves existing facts, replacing the one-event policy uniqueness constraint with exact filtered lookup indexes so earlier acceleration events remain append-only. Current eligibility uses three indexed `EXISTS` probes for exact timezone/schedule/break revision and hash, in one SQL command. No raw machine observations or full activation history traversal are introduced.

Mutation order is operation-ID exclusive lock, installation security exclusive gate, ordinal policy resources, then ordinal machine resources. Single-policy revocation takes only that policy's exclusive resource; withdrawal uses the existing batched package resource list (shared policy resources followed by exclusive affected machine resources). Readers use the same security gate, so both commit orders are explicit. No lock upgrades, reverse resource acquisition, client timestamps as authority, or timeout increases.

The fact, provenance, operation result, audit and authority epoch commit in one transaction. Audit failure rolls all of them back. Schema checks retain exact site/hash references, human actor kinds, two distinct revokers and author/submitter exclusion. Metadata-only transaction diagnostics carry command/outcome/timing/roundtrip fields.

## Acceptance scope

The dedicated SQL suite exercises real authenticated command producers after dual-custodian bootstrap, scoped human enrollment/grants, certificate enrollment, actual machine enable approval, runtime assignment and mTLS handshake. Policy fixture revisions are seeded only because policy authoring is a separate pending feature. Revocation and withdrawal facts are produced through the new commands.

Coverage includes permission and site boundaries, exact hashes, proposer self-approval, original-session and grant revocation, audit rollback, concurrent exact retries, conflicting operation reuse, scheduled first-use blocking, immediate acceleration, no postponement, all three policy types, lock-wait expiry/time clamping, pending acknowledgement denial, both withdrawal/acknowledgement SQL commit orders, immutable provenance and a full 100-machine withdrawal transaction budget of 30 TDS roundtrips and five seconds in the disposable fixture. These are regression gates, not production capacity claims.

Active roster publication, commissioning discovery, notification delivery, browser/OIDC composition and runtime HTTP hosting remain separate work. No production database or machine runtime is changed.

## Qualification evidence

CI 42344254704 passed 794 combined acceptance checks (102 pure/transport checks plus 692 real SQL checks), with no skips, 463 dashboard tests and 15 bootstrap CLI checks. Both policy-revocation/acknowledgement and approval-withdrawal/acknowledgement commit orders passed using observed SQL lock waits. Local .NET build passed without warnings or errors; all 102 pure checks passed locally.

The complete 100-machine approval withdrawal took 11.92 ms and 20 TDS roundtrips; successful safety commands used at most 25 roundtrips. Existing regression measurements remained within their budgets: 100-machine submission 36.42 ms/25 roundtrips; selected-machine activation 13.30 ms/23 roundtrips; acknowledgement 9.65 ms/17 roundtrips. Inventory p99 was 4.29 ms at one reader and 11.21 ms at eight readers, with nine roundtrips and 384 logical reads per 128 samples. These disposable fixture results do not establish production capacity.
