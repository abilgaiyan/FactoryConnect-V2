# GG-002-M1.3 — Edge-only restart and read-only recovery assessment

Production acceptance remains **HELD** until the executable recovery fixtures and
actual release-payload verification pass on the reviewed commit. This document
and the assessment are evidence collection, not a state-repair operation.

## Interruption states

| Interruption boundary | Durable evidence | Possible process state | Required interpretation |
| --- | --- | --- | --- |
| Termination requested, before `Absent` publication | `EdgeRestarting`, old identity, termination phase | Old Edge may be alive or absent | Transition does not prove termination. Observe the exact old PID/path/start time. Do not launch a replacement. |
| `Absent` published, launch intent written | Old Edge absent; unresolved Edge launch intent | Replacement may be absent or live | Intent is a barrier. Enumerate candidates; a matching path alone does not establish launch provenance. |
| Replacement launched, before ownership publication | Intent retained; Edge ownership absent | Live unrecorded replacement possible | Discover PID/path/start time. Keep it a candidate unless durable ownership independently establishes that exact instance. |
| Ownership publication failed | Intent retained; last successfully published runtime state | Replacement may remain live | A diagnostic `replacementObservation.record` verifies an observation, not ownership. Publication-failure warnings may mean even failure evidence could not be saved. |
| Ownership published, before intent clear | Durable replacement identity; intent retained | Replacement may be verifiably owned | Recheck all identity components and intent correlation. Assessment does not clear the barrier. |
| Intent cleared, final verification or `Succeeded` publication failed | `Failed` or last `EdgeRestarting`; durable replacement identity; intent absent | Replacement may be verifiably owned | Process restart can have completed while operation completion is blocked. Lack of intent does not authorize retry or imply success. |

Failure handlers do not terminate a replacement or clear its intent. A second
restart rejects unresolved intent, `Failed`, or `EdgeRestarting`. Neither the
assessment nor these fixtures changes that policy.

## Read-only procedure

Run with Windows PowerShell 5.1 and enough permission to inspect process paths
and exact start times. Substitute the installation root under review:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Get-FactoryConnectEdgeRestartAssessment.ps1 -InstallRoot <installation-root>
```

The script opens the **existing** `deployment.lock` exclusively for read. Lock
contention or a missing lock rejects the assessment. It reads runtime/intent
JSON, verifies release selection, enumerates Edge executable identities twice,
and verifies durable and diagnostic PID/path/UTC-start-time records. It writes
no files, launches/stops no processes, invokes no HTTP endpoint or SQL operation,
and never deletes an intent or changes a runtime status.

| Classification | Meaning | Next boundary |
| --- | --- | --- |
| `AbsentReplacement` | Stable, accessible inventory contains no replacement at the selected executable; relevant exact records are absent or identify the old Edge | Inspect `identityStates.oldEdge`: an old Edge still alive and an old Edge already stopped are different recovery situations. A separate authorized repair is required. |
| `VerifiablyOwnedReplacement` | One candidate exactly matches the durably published replacement PID/path/start time; the exact old Edge is confirmed absent; release and any surviving intent agree | The existing process is identified. Repair of durable completion/intent state remains a separate authorized operation; this report does not restore `Succeeded`. |
| `AmbiguousOwnership` | Simultaneously live old/replacement Edge, unpublished candidate, conflicting identity, multiple candidates, inaccessible/unstable inventory, foreign intent, or release mismatch | Preserve evidence and investigate. Do not adopt by executable name/path, infer origin from parent PID, or launch another Edge. |

`candidates` provides observed identities for investigation. A diagnostic record
may yield `diagnosticReplacement = Verified` while classification remains
`AmbiguousOwnership`: exact observation and durable ownership are distinct.
When the exact old Edge and a replacement candidate are both live, `oldAndReplacementLive = true`, the reason includes `OldAndReplacementLive`, and classification remains `AmbiguousOwnership` even if replacement ownership or `Succeeded` was previously recorded. An unproven or mismatched old identity also prevents `VerifiablyOwnedReplacement`. Inventory/access errors never become absence proof. Reports are point-in-time
observations, not durable guarantees of future liveness. API/Dashboard identity
states are reported; their HTTP health is not assessed by this read-only tool.

**No mutating recovery operation is supplied by this follow-up.** Do not edit
`runtime.json`, delete an intent, or call whole-stack startup to bypass a held
restart. In particular, existing whole-stack startup can return `AlreadyRunning`
without repairing failed Edge-restart metadata, and cannot discover an
unrecorded replacement safely. An explicit repair design must bind any later
mutation to newly verified exact identities under the deployment lock.

## Development verification

From a clean checkout of the feature branch, on Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\deployment\Invoke-EdgeRestartConformance.ps1 -Executable
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\deployment\Invoke-EdgeRestartAssessmentConformance.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\deployment\Invoke-RebootStartupReleasePackageConformance.ps1
```

The deterministic E01–E12 cases control boundary outcomes. The executable suite
also runs real fixture processes, then exercises:

- R01: kill the restart host after exact old-Edge termination, before absence publication.
- R02: kill the restart host after the real replacement launch, before ownership publication.
- R03: make the runtime file read-only and hold a handle denying replacement before ownership publication; exercise the real atomic writer failure.
- R04: make the runtime file read-only and hold a handle denying replacement after intent clear, before final publication; exercise the real writer and failure-writer failures.
- R05: inject a final-verification exception after intent clear; verify a durable `Failed` state with the owned replacement preserved.
- R06: kill the restart host after ownership publication, before intent clear; verify ownership while preserving the correlated intent.
- R07: let an interrupted, unrecorded fixture replacement exit; distinguish absence while retaining its unresolved intent.
- R08: keep the exact old and replacement Edge instances simultaneously live; explicitly report the ownership conflict without changing either process or evidence.

Fault hooks exist only in temporary instrumented copies made by the test
harness. The repository operation has no production fault-injection switch.
All processes, junctions and evidence in executable tests belong to disposable
development fixtures. The suite verifies API/Dashboard preservation and that
assessment preserves evidence bytes and process instances.

## Release payload

`Build-FactoryConnectRelease.ps1` requires and stages both
`Restart-FactoryConnectEdge.ps1` and
`Get-FactoryConnectEdgeRestartAssessment.ps1` beside the existing startup and
termination scripts. `release.json` identifies both, and the builder's complete
payload enumeration includes them in `MANIFEST.sha256` before archive creation.

Static contract checks do not prove an actual release has been produced. After
normal artifact production from a clean exact-commit checkout, verify the
resulting release directory with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\deployment\Invoke-RebootStartupReleasePackageConformance.ps1 -ReleaseDirectory <produced-release-directory>
```

This checks both scripts against source bytes, metadata, exact manifest entries
and PowerShell parsing, as well as the existing release requirements. Production
acceptance remains held until this actual-payload proof and the new executable
fixture results are reviewed. No factory restart or deployment is part of these
commands.

## Patched self-contained runtime requirement

The `fd4384b…` artifact's API Pkcs owner was ASP.NET Core runtime pack 10.0.7.
It remains held and immutable. Provenance alone does not remediate the affected
Pkcs assembly; see Microsoft's [CVE-2026-70162 advisory](https://github.com/dotnet/announcements/issues/454)
and [CVE-2026-84008 advisory](https://github.com/dotnet/announcements/issues/468).

Release production now requires stable SDK 10.0.402 or newer in the 10.0 series
and explicitly requests runtime framework 10.0.13. Before final publication,
`FactoryConnect.PkcsPayload.ps1` checks all five staged applications. It requires
patched runtime-pack identities, selects the actual Pkcs runtime asset owner,
accepts patched NuGet 9.0.21+ or 10.0.13+ and ASP.NET Core 10.0.13+ owners in
those supported series, and checks DLL identity/product version plus manifest
coverage. A package library entry without a selected runtime asset is not a
second owner. Optional native-only dependency entries are handled under strict
mode. Missing/ambiguous ownership, affected versions and inconsistent DLLs fail
before the immutable release directory/archive is created.

Run the security cases on the new exact commit before producing its artifact:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\deployment\Invoke-PkcsPayloadConformance.ps1 -Executable
```

This Windows PowerShell 5.1 mode compiles disposable PE assembly fixtures;
without `-Executable`, identity observations are controlled fixtures. Neither
mode proves an actual application publish. A new artifact, its actual-payload
conformance result, SHA256 and clean exact-source evidence remain required.
