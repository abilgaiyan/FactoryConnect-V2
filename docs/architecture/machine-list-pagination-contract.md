# FC2-045 — Machine discovery above 100: pagination contract

Status: design proposal; no API behavior change.

## Problem

Commissioning evidence machine discovery currently has a bounded 100-machine
result. An authorized fleet larger than the bound must not be silently
presented as the complete fleet.

## Proposed HTTP contract

- Preserve the existing authorization and machine eligibility checks.
- Return a stable, bounded page and an explicit continuation indicator/token
  whenever more authorized machines remain.
- Do not expose unauthorized machine identifiers, counts, or ordering through
  pagination metadata or timing-dependent page skips.
- Bind continuation tokens to the authenticated principal, installation,
  effective permission/grant revision, filters and deterministic ordering.
  Expire or reject tokens after authority revocation or scope changes.
- Enforce maximum page size on the server. A client-supplied cursor is never
  SQL text, an arbitrary offset into unfiltered rows, or a grant bypass.
- Avoid count queries over the entire unauthorized machine population.
- Preserve compatibility with the current dashboard until it explicitly
  supports continuation; a first-page-only view must be visibly labeled
  incomplete rather than silently claiming all authorized machines.

## Qualification gates

1. 1, 100, 101 and 1,200 eligible machines: no missing or duplicated authorized
   entries across pages, stable order and explicit end-of-list.
2. Sparse authorization: 1 authorized of 1,200 unauthorized machines cannot
   reveal hidden IDs, counts or relative positions.
3. Revocation between pages invalidates the continuation rather than leaking
   stale authorization.
4. Forged, cross-user, cross-installation and expired cursors fail closed.
5. SQL roundtrips and latency remain bounded under sparse access.
6. Browser shows incomplete result state until all pages have been fetched.

Implementation, schema changes and production deployment require separate
qualification.
