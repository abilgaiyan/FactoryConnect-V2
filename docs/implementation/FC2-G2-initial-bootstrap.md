# G2 increment 2 — Two-custodian initial bootstrap

Baseline: `main` `30896c333668af0041127a2504c3a2268ef39c13`, following squash merge of PR #6. Earlier migrations remain unchanged; migration 004 adds immutable public trust, custodian key/revocation and single-use initial-ceremony records.

## Authority and delivered scope

A new installation starts sealed. `SqlBootstrapAuthority` can initialize it only with two valid RSA-PSS/SHA-256 signatures over the same canonical request, using different keys, declared custodians and exact external identity bindings from a separately provisioned public-key manifest. The administrator may not be either signing custodian's declared external identity. Software validates these bindings; the installation owner must independently verify that the custodians are two actual people with independent key custody, including any aliases across identity providers.

The trusted manifest is an installation input, not part of the submitted request's authority. It pins installation UUID, administrator HTTPS issuer, canonical DER public RSA SPKI keys (3072–8192 bits, exponent 65537), custodian identities and key validity intervals. No private key, default account, production tenant or custodian is supplied by the product. Duplicate public keys, alternative encodings of the same key, or inconsistent identity aliases are rejected.

The first committed ceremony pins the canonical manifest/hash and key bindings in SQL. Later host configuration cannot overwrite that trust. Current committed custodian revocation is rechecked under the security gate even for an exact completed retry. This increment validates revocation records but does not expose a custodian enrollment/rotation/revocation administration API.

The initial-bootstrap command is **not recovery**. An initialized installation cannot use it again, including after a privileged fixture reseals the state and resets its epoch. Controlled recovery, separately held recovery material, post-recovery rotation, OIDC/session issuance, delegated grant commands and runtime enrollment remain later G2 increments. No HTTP bootstrap route or application startup auto-bootstrap is added.

## Signed request profile

`BootstrapTrustManifest.Canonicalize` emits versioned compact UTF-8 JSON, fixed field order, UTC timestamps with seven fractional digits, GUID D form, padded Base64 nonce and lowercase hex manifest hash. The signed domain is `FactoryConnect-V2:initial-bootstrap:v1`; profile is `security-administrator-v1`.

Signatures bind installation/operation/principal IDs, exact issuer/subject, profile, named approver/executor key IDs, issued/expiry instants, administrator authority expiry, expected epoch, 32-byte nonce, trust-manifest hash and reason. No trimming, case folding or lossy identity conversion occurs. Golden canonical bytes/hash fixtures protect compatibility.

Ceremony lifetime is at most 15 minutes and is checked against SQL server UTC **after locks are acquired**. Both keys must cover that interval. Administrator authority must expire within 90 days of issuance. The preparation command chooses a 10-minute ceremony and 30-day administrator authority. Machines are not activated by bootstrap, and no login session is fabricated.

The fixed administrator profile explicitly allows security access management and audit reading. Other currently known business permissions are delegation authority only, with `UseAllowed=false`; configuration editing/review/activation is not implicitly granted. Future account-lifecycle/renewal must be completed before production use of this bounded initial authority. Ordinary delegation cannot extend an ancestor's expiry ceiling.

## Atomic SQL behavior

Target checks require an existing explicit V2 database/catalog, exact SQL server name, product marker and installation UUID. No migration, database creation or legacy adoption occurs in the bootstrap command.

Transaction order: operation-ID lock → exclusive installation security gate → target/state/server-time checks → current custody proof validation → exact completed-result check → initial-state preconditions → trust/principal/grants/operation/ceremony/audit/sealed-state transition → one commit. First initialization requires sealed epoch zero and an otherwise empty human security authority. No independent nested commit consumes a nonce or leaves partial trust after failure.

Exact retries return the original receipt only while custody authorization and the signed challenge remain eligible; fingerprint changes reject. Parallel requests cannot create two administrators. Proofs and nonce hash are immutable. An injected audit failure must roll back all effects and preserve sealed epoch zero. Recorded timestamps are not commit-visibility proof.

## Offline operator workflow

These commands prepare or inspect public artifacts. Real issuer/subject bindings, public trust file and independent signing providers are explicit deployment inputs. Keep the connection string in an external environment variable; do not put it in source or arguments. Do not use production targets for development acceptance.

```powershell
# Prepare a fresh public payload and the exact bytes to review/sign. Output paths must not exist.
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- prepare '<public-manifest.json>' '<administrator-principal-guid>' '<exact-IdP-subject>' '<approver-key-guid>' '<executor-key-guid>' 'Initialize isolated V2 installation' payload.json canonical.bin

# Each custodian can independently regenerate canonical bytes from the reviewed payload.
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- canonicalize payload.json reviewed.bin

# After each custodian signs those bytes independently, assemble the two raw signature files.
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- assemble '<public-manifest.json>' payload.json approver.sig executor.sig signed-request.json

# Apply to the explicitly selected, already migrated and sealed development installation.
dotnet run --project tools/FactoryConnect.SecurityBootstrap -- FC2_SQL_CONNECTION FactoryConnect_V2_Dev '<exact-server-name>' '<installation-guid>' '<public-manifest.json>' signed-request.json
```

Each offline signing provider must implement the equivalent of `RSA.SignData(canonicalBytes, SHA256, Pss)` with a digest-length PSS salt. Private keys remain under independent operator custody; the online executable reads only public manifest, payload and signatures. The cross-runtime smoke test signs with Node's RSA-PSS implementation and verifies with .NET; it is not a production signer or key-enrollment tool.

JSON files use camelCase record properties. The public manifest shape is `installationId`, `allowedAdministratorIssuer`, `keys`; each key carries `keyId`, `custodianId`, `issuer`, `subject`, Base64 `publicKeySpki`, `validFrom`, `expiresAt`. Files must be valid UTF-8 (an optional UTF-8 BOM is accepted). Unknown and duplicate properties, malformed encoding and artifacts over 1 MiB are rejected. Preparation/assembly never overwrite existing files. A failure can leave a newly prepared public artifact; it does not imply SQL initialization.

Clock alignment is required; there is no permissive expired-challenge fallback. Success prints a non-secret receipt with operation, principal, management-grant and profile identities. The receipt is not a login credential. Failed commands do not print connection strings, SQL exception payloads or signing material.

## Acceptance boundary

Pure fixtures cover signature threshold, self-approval, wrong/weak/duplicate keys, exact identity and manifest binding, canonicalization and expiry. CLI fixtures keep synthetic private keys in memory and check independent RSA-PSS interoperability, artifact overwrite protection and strict parsing.

Real SQL fixtures cover target identity, no trust binding on invalid proof, atomic rollback, exact retries, competing first requests, bounded explicit administrator permissions, no fabricated session, immutable trust/history, reseal rejection and fresh custodian-revocation checks. Synthetic identities/keys live only in disposable tests. Passing these checks does not close controlled recovery, real IdP provisioning, application SQL-role hardening or production readiness.
