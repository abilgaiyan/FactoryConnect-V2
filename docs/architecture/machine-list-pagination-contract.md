# FC2-045 — Machine discovery above 100: pagination contract

Status: implemented on PR #48; qualification in progress. No production deployment approval.

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
- Bind continuation tokens to the authenticated session, installation,
  site filter and deterministic ordering. Revalidate SQL session and grant
  ancestry on every page so revocation and changed scope cannot expose stale authorization.
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

Implementation and disposable-SQL qualification are on PR #48. Production deployment requires separate authorization.

## Implementation status (PR #48)

The HTTP endpoint now supports opt-in HMAC-SHA256 signed seek cursors when
`Evidence:MachineDiscoveryCursorKey` is configured as a persistent base64 secret
containing at least 32 random bytes. Keep the key out of source control, share
it across API replicas, and rotate it to invalidate previously issued cursors.

A cursor binds installation ID, site ID, the SHA-256 session bearer hash, last
returned machine ID and a five-minute expiration. Forged, expired, cross-session
or cross-installation cursors are rejected before SQL page retrieval. SQL
rechecks current session validity and permission ancestry for each page.
`nextCursor` is returned only if SQL finds an additional authorized row.
With no configured key, continuation remains disabled and incomplete first
pages remain visibly incomplete.

**CI-qualified:** SQL keyset discovery for 1, 100, 101 and 1,200 authorized
machines; 1,200 machines traverse exactly twelve 100-machine pages. Authenticated
TestServer HTTP traverses 101 machine-scoped grants over two signed pages,
including terminal completeness and no unauthorized IDs. Signed-cursor tamper,
cross-session/site/installation, session-revocation and malformed-token tests
pass. Dashboard mounted tests cover two-page accumulation and incomplete
first-page messaging. Latest green SQL run measured 94.93 ms total for twelve
1,200-machine discovery pages in disposable CI SQL (not a production SLA).

**Remaining qualification:** explicit expired-token test; grant-scope change
between pages; sparse 1-of-1,200 HTTP disclosure and latency acceptance;
1,200-machine HTTP end-to-end measurement and browser behavior under permission
changes. A valid signed cursor does not freeze authorization: each page
revalidates current SQL permissions, so permission changes may change the
visible result set. This is not snapshot isolation across pages and must not
be represented as one. Production deployment remains unapproved.
