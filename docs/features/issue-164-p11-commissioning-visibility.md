# Issue 164 P1.1 — commissioning visibility

Implementation authorized by Ajay on 28 October 2026. The authorization permits
development and review; it does not establish completion of stabilization gates
or authorize merge, deployment, configuration activation or recovery. PR 165
recovery remains complete; no rerun required.

## Surface and authorities

The existing dashboard shell adds `/commissioning`; there was no pre-existing
administration page. GET `/api/commissioning/v1/snapshot` returns a bounded snapshot.
No mutation route is added. No migration, binding, replay or history scan is used.

The optional captured Edge JSON is a *file capture*, not effective Edge runtime
configuration. It is not loaded into host configuration or activated. File mtime
is explicitly file metadata; SHA256 identifies the bytes read. Revision and
runtime acknowledgement remain unknown. Configuration layering/environment
overrides cannot be inferred from this capture. MTConnect machine/device identity,
stream mappings/normalization, production assignments, schedule timezone/effective
dates, planned windows/breaks and admission are projected through a closed schema.
Endpoint URLs, credentials, arbitrary keys and logs are withheld. Suspicious values
in otherwise permitted fields are redacted too. Do not use this projection as a
general-purpose secrets scanner or publish captured production files.

SQL reads only production-context processor bindings and optional checkpoints,
including quantity processor bindings without treating quantity as commissioned.
Machine, processor, stream and row identities are preserved. Nullable checkpoints
remain null; UInt64 positions use decimal strings. Reads never invoke the normal
checkpoint API, because that API can bind admission policy. Read completion time
is not last-progress time. Other processor checkpoints and transport health are
explicitly unavailable here; existing machine status remains the contact surface.
Admission comparison requires exact machine/activity processor/stream ownership.
Null bound admission means unbounded, not unbound. Approval/activation/recovery
remain separate operations. A configuration mismatch supplies no recovery action.

## Access and limits

Both API and dashboard independently require `Commissioning:Enabled=true`, default
false, a loopback remote address, and no Forwarded/X-Forwarded headers. Neither
setting is added to production templates. This is a development/local review
policy, not administrator authentication. Do not enable it behind a reverse proxy,
port forward, shared hosting or a remote public/LAN listener. Read access policy
and production acceptance must be reviewed before any release enabling diagnostics.

Only API needs the optional `Commissioning:CapturedEdgeConfigurationPath` and
`Commissioning:ReadOnlyConnectionString`. Use an isolated capture and disposable
database for review. The SQL credential should have SELECT permission only on
the two head tables; no fallback to runtime credentials is made. Protect these
settings as secrets. No factory invocation/activation procedure is authorized here.

API permits one snapshot at a time (otherwise 429), a five-second read budget,
three-second SQL connect/command timeout and 128 processor rows (129 rejects the
whole durable result). Config input is at most 1 MiB, depth 16, 128 items per array
and at most 2049 projected fields. No NOLOCK, isolation change or integrity-lock
removal is introduced. Ordinary read-committed SQL semantics apply; the file and
database are independent reads, not an atomic cross-authority cut. No health or
historical correctness is inferred from their agreement.

The browser makes one request plus manual refresh, cancels after ten seconds and
on unmount, discards previous data on refresh/failure, and discloses the snapshot
time and lack of automatic refresh. Responses use no-store. Failed configuration
and SQL reads are separately visible; errors expose no exception text or path.

## Validation and release gates

Build before tests. CI is the implementation validation authority. Local .NET
execution is unavailable in the editing environment; no local build/test pass is
claimed. Added tests cover access restrictions at both hosts, closed projection,
normalization/scoped fields, redaction, duplicate capture rejection, absent sources,
admission comparison, null/maximum checkpoints, read-only SQL binding preservation
and oversized head inventory. Existing CI builds .NET before ordinary/SQL tests.

Before pilot enablement: complete the separate recovery evidence archive and
historical-boundary/report/latency/restart acceptance; review access policy; inspect
the page with isolated two-machine evidence, failures and absent checkpoints.
No production screenshots, operation IDs, backup references or acceptance results
are fabricated by this change. Writable P1 requires authenticated roles, revisions,
audit, validation and separately acknowledged activation in the same slice.
