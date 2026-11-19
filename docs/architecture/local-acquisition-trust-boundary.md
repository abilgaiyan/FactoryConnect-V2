# Local MTConnect acquisition — simplified trust boundary (design gate)

Status: proposed; **not implemented**. Functionality First: first simulator observation through SQL to reporting.

## Current implementation

`AcquisitionWorker` opens `LocalRuntimeTransport` before reading the SQL-backed
acquisition context. That transport creates a loopback TCP listener and mutually
authenticates the runtime using X.509 certificates. `RuntimeMtlsChannel` is passed
to `SqlSecurityAuthority.ReadMtConnectAcquisitionContextAsync` and
`ReceiveMtConnectAsync`. The mTLS channel is therefore an **authorization
identity**, not just an encryption wrapper. Removing certificate checks from
`HostOptions` or changing the loopback listener to unauthenticated TCP would
not be a safe simplification.

## Target for single-server deployments

- The MTConnect agent can be contacted over loopback HTTP. Its responses are
  untrusted input, not proof of machine authorization.
- A local acquisition worker is a separately authorized application identity.
  Prefer OS-bound process/service identity with least-privilege SQL access for
  single-host deployments. No operator-managed per-machine certificate ceremony.
- Keep site, machine, stream and activation-acknowledgement checks in the
  authoritative SQL operation. Preserve cursor monotonicity, bounded batches,
  fail-closed behavior and evidence provenance.
- Keep the existing mTLS runtime channel for deployments that need it until
  an alternative local channel is implemented and tested. Never downgrade
  remotely accessible runtime endpoints to unauthenticated transport.

## Implementation acceptance gates

1. Identify every `RuntimeMtlsChannel` and `SqlSecurityAuthority` authorization
   dependency. Specify a local identity abstraction whose identity cannot be
   supplied by an untrusted caller.
2. Add a separate local-only transport that binds to a verified OS identity
   (e.g. Windows named pipe ACLs / service identity), or an equivalent secure
   in-process boundary. Reject remote callers and unrecognized principals.
3. Maintain the same SQL activation/assignment authorization and immutable
   evidence checks as the existing mTLS path. Test denied and revoked identities,
   wrong site/machine/stream, replay, cursor regression and failed SQL commit.
4. Provide a minimal ignored development configuration with no private keys,
   and a non-mutating preflight. Never silently switch an existing mTLS
   configuration to local mode.
5. Verify one simulated MTConnect batch is committed and read back from the
   isolated `FactoryConnect_V2_Dev` database; then verify activity/reporting.
   Do not claim OEE is valid until performance and quality inputs are available.

## Non-goals

No production deployment, no automatic machine activation, no disabling
authorization or audit, no use of the V1 production database, and no requirement
to replace Keycloak user authentication.
