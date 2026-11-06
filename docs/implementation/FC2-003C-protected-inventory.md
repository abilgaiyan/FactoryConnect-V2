# FC2-003C — Protected SQL inventory read

The internal `SqlProtectedMachineInventoryReader` now uses the existing `SqlSecurityAuthority.ReadAsync` gate to authorize `configuration.read` at **site scope** and materialize that site's stable machine inventory on the same SQL connection and transaction. The shared security gate remains held through materialization, ordering the read against committed session/grant revocation. It rejects missing site identity and fails closed on absent grants or mismatched installation identity.

The read intentionally does not return an active roster or commissioning candidates, claim live connectivity, or create an HTTP endpoint. G3 eligibility requires authoritative activation/runtime/policy facts not yet supplied. A separate future commissioning permission and machine-scoped read are required; do not widen a machine grant into site inventory.

Acceptance adds real SQL checks for authorized site, unauthorized site and missing permission. The existing SecurityAcceptance suite covers revocation/read races on the shared authority gate; a subsequent increment must exercise those races against this concrete inventory reader and verify no cross-site leakage in concurrent assignment changes.

No migration, runtime worker, production data or deployment.
