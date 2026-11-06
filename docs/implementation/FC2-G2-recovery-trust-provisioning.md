# G2 increment 6 — Provision separate recovery trust before losing access

Baseline: `main` `e5ac808`, following human lifecycle PR #10. This increment establishes the prerequisite trust for controlled recovery. It **does not execute administrator recovery**, create a root grant or reset an installation. No recovery endpoint, startup hook, private-key reader, trust-rotation command or default recovery material is registered.

## Establishing authority

`SqlSecurityAuthority.ProvisionRecoveryTrustAsync` requires all three authorities together:

- A current human session with installation-scoped `security.access.manage` use permission.
- Two distinct bootstrap custodians signing the exact versioned provisioning declaration with currently valid, unrevoked bootstrap keys loaded from the immutable SQL bootstrap manifest.
- Both new recovery custodians proving possession and consent by signing the same declaration with their new recovery keys.

The initial recovery manifest has exactly two independently bound custodians. Each key must be canonical DER RSA SPKI, 3072–8192 bits, exponent 65537. Bootstrap key material and key IDs cannot be reused. Duplicate key aliases/alternative encodings are already rejected by the shared tested public-key profile. Both recovery keys must be active at provisioning and cover the challenge expiry. The authorizing administrator's exact external identity cannot be a signing bootstrap or recovery custodian.

The same two real people may hold separately managed bootstrap and recovery keys; software checks declared distinct custodian identities and distinct public material, not physical independence. The installation owner must establish the actual people, identity aliases, independent custody, backup and readiness outside this server-side command. No key is shipped by the product.

The manifest pins installation and the same exact administrator issuer as bootstrap. A request cannot introduce another issuer or select its own bootstrap trust. Recovery trust has a distinct hash domain, `FactoryConnect-V2:recovery-trust:v1`.

## Signed declaration and atomic provisioning

The signed domain is `FactoryConnect-V2:recovery-trust-provision:v1`. Canonical compact UTF-8 JSON binds installation, operation, trust and actor IDs; exact actor issuer/subject; the two bootstrap signer IDs; SQL expected authority epoch; issued/expiry instants; 32-byte nonce; exact bootstrap/recovery manifest hashes; and reason. The recovery manifest hash identifies both new recovery keys. Canonical instants use UTC with seven fractional digits; hashes are lowercase hex and nonce is padded Base64. Golden fixtures fix the format. Challenges last at most 15 minutes, with SQL UTC sampled after gate acquisition.

Order remains operation-ID lock → exclusive installation security gate → fresh state/session/installation management → SQL bootstrap trust and exact actor binding → all custody/possession proofs and bootstrap revocations → exact completed result → expected epoch → initial-provisioning preconditions → trust revision, current pointer, public keys, four signatures, nonce hash, operation, audit and epoch → one commit. An audit failure rolls back the complete ceremony.

Migration 008 creates append-only trust revisions, public keys and the initial provisioning ceremony, plus a current-trust projection for future controlled rotation. This command creates only generation 1. Any existing provisioning, trust revision, pointer or recovery key prevents a second initial ceremony, even if a pointer is absent. Later host configuration cannot replace committed recovery trust. This increment provides no mutation API for the pointer.

Exact concurrent retries return one receipt and produce one epoch/audit change. Changed content under an existing operation rejects. Session, installation management, challenge validity, all four proofs and current bootstrap-custodian revocations are rechecked before completed replay. After challenge expiry, the existing authorized operation-result reader can retrieve the historical non-secret receipt; this does not establish custody for another action.

## Acceptance and next increment

Pure fixtures cover canonical hashing/domains, order/UTC stability, both signature thresholds, private-key possession, no self-approval, content/manifest binding, separate key IDs/material, nonce and expiry bounds. Disposable SQL fixtures begin with a real two-custodian bootstrap and check explicit installation/sealed state, actor identity, stale epochs, rollback, exact concurrent retries, immutable history, no fabricated account/grant/session and current authority/custody on replay. A barrier fixture observes actual SQL application-lock waits while a completed retry queues behind session revocation, then proves the retry is denied.

This is a server-side provisioning contract. Offline/operator tooling, actual recovery execution, lost-access evidence, administrator replacement, mandatory recovery-key rotation, authority renewal and recovery audit acceptance remain unfinished. The next increment must consume only committed recovery trust and prove atomic account replacement/rotation, retry and revocation behavior before recovery becomes available. Real OIDC/browser sign-in, runtime trust and deployment hardening remain separate roadmap work.
