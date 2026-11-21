# Local development policy bootstrap

This tool is restricted to the known `FactoryConnect_V2_Dev` database on `.\\FCINVENTORY`, using Windows integrated authentication. It never changes shift or break planning rows.

From the repository root:

```powershell
dotnet run --project scripts/dev/PolicyBootstrap -- preview
dotnet run --project scripts/dev/PolicyBootstrap -- write
```

Run `preview` first. `write` creates or reuses immutable schedule and break policy revisions from the three validated shifts and their excluded breaks. Re-running the same input reuses the existing revision IDs.

**Not yet complete:** This tool deliberately does not write a timezone revision. The `Asia/Kolkata` IANA zone name is known, but the authoritative TZDB rules bytes, TZDB release, resolver version, and gap/fold policy must be sourced and approved rather than guessed. No configuration package is submitted or activated by this tool. Do not use on the pilot production server.

## Explicit timezone rules (separate approval step)

The timezone writer accepts a **trusted, independently sourced IANA TZif v2/v3/v4 file** for `Asia/Kolkata`. Obtain the file from a verified TZDB distribution; do not copy a Windows timezone registry entry or generate placeholder rules. Record the actual distribution release and the consuming resolver implementation/version. The command validates the TZif header and independently supplied SHA-256 digest, but **does not prove the file belongs to Asia/Kolkata or that the release label is authentic**; those are operator verification responsibilities.

```powershell
$tzif = 'C:\path\to\verified\zoneinfo\Asia\Kolkata'
$expectedHash = (Get-FileHash -Algorithm SHA256 $tzif).Hash.ToLowerInvariant()
# Prefer comparing this hash with a separately published trusted distribution manifest.
dotnet run --project scripts/dev/PolicyBootstrap -- timezone-preview $tzif $expectedHash '<verified-tzdb-release>' 'fc2-pinned-tzif-v1'
# Only after confirming provenance, review, and preview:
dotnet run --project scripts/dev/PolicyBootstrap -- timezone-write $tzif $expectedHash '<verified-tzdb-release>' '<verified-resolver-version>'
```

The example computes a local digest for convenience, **not** an independent authenticity check. Gap/fold policies are explicitly `reject/reject`. A pinned TZif snapshot alone does not establish that the runtime resolver consumes these bytes; verify that separately before activation. The write remains restricted to the local development SQL instance/database.

The `fc2-pinned-tzif-v1` implementation parses pinned TZif 64-bit transition tables and fixed-offset POSIX trailers, rejecting recurring DST trailers. This establishes a concrete resolver for UTC instants; wiring all production shift and reporting paths to this resolver remains a separate integration step. A successful bootstrap does not imply those paths already use it.
