# Supervisor Desk UI first implementation slice

Initial screen implementation authorized by Ajay on 29 October 2026, based on main
8061eab01f2c6d03b1b4b491fc69456b2a74683d. This PR implements a development-only screen
review. It does not approve the still-pending authentication/session authority,
canonical request encodings, rejection classification or transaction-aware resolver
SQL. No backend mutations, migration, production enablement or deployment is included.

## Review the screens

Use the repository-pinned Node version. From src/FactoryConnect.Dashboard/ClientApp:

```sh
npm ci
npm run build
npm run typecheck
npm test
npm run dev -- --host 127.0.0.1
```

Open http://127.0.0.1:5173/supervisor-preview (use the actual Vite port if occupied).
Direct development navigation mounts the sample desk without loading dashboard
configuration or contacting a .NET API. Navigation from the development dashboard
also exposes the preview. Production builds omit the sample component/data and
preview navigation; the route displays a non-enabled notice instead of an editor.
No environment/configuration change on Gajra is part of this review.

## Included behavior

- Shift start, Event explanations, Shift summary and a separate Reviewer queue.
- Two illustrative machine types with visibly distinct sources and sample identities.
- Three explicit sample shift occurrences including an overnight occurrence. These
  are not commissioned factory calendars or proof of production-day semantics.
- Original closed event identity and source bounds remain read-only. Overlaps are
  labelled and do not split or replace the source event. Open activity is read-only.
- Reference publication revision zero and a value beyond JavaScript's safe integer
  range remain strings; annotation revision is a separate value.
- Independent threads, separate event/thread counts, creator-only simulated edits,
  new local draft revisions, simulated submission and independent acceptance.
- Existing note field carries action taken; no structured-field amendment. Team
  involved/follow-up team permits Not established; cause need not be asserted.
- Retained sample evidence is labelled when simulated reads are unavailable or
  conflicting; creation/submission/acceptance then remain blocked.
- Stale-revision simulation preserves unsaved input. Unsaved input prevents internal
  event/shift/persona/view changes until saved locally or discarded.
- Historical local revisions remain visible; a successor draft inherits no approval.

All personas, permissions, timestamps and transitions are illustrative. The local
state model is not an authentication/authorization service, resolver, durable audit
store, operation ledger or production protocol implementation. No network mutation,
browser storage or server persistence is used. Leaving/reloading resets the entire
preview; no user should rely on it to record factory work. Permission revocation,
atomic retries, exact resolution and lost acknowledgements require the separately
reviewed backend, not this UI reducer.

Changes-requested interaction is shown as pending rather than inventing the unresolved
self-review permission rule. The model's status vocabulary includes Changes requested,
but this slice does not simulate that transition. Targets/achievement, quantity and
quality declarations, context assignment, general handover notes, reason-master
maintenance, machine control and OEE changes are excluded. No 100% default is invented.

## Validation

Build and typecheck precede targeted and full client tests. New tests cover independent
threads, original/overlap identity, overnight scope, exact revision strings, creator-only
editing, stale revisions, self-approval, unavailable evidence, no inherited approval,
unchanged sample evidence, no network/storage and unsaved-input handling. These are UI
tests; they do not constitute backend security, SQL or factory acceptance.

Perform Chromium review at 1440px and 390px for all four views, event editing, conflict
and evidence-unavailable behavior. Verify contained table scrolling and no page overflow.
Check the production bundle excludes sample fixture/editor strings. Remote CI remains
the authority for the complete current-main .NET/client/SQL validation.

Next approval gate: review the screens and finish the pending backend technical contract
before authorizing persistence, authenticated writes or production configuration changes.

## Preview correction review

Revision-saved, submitted and explanation-accepted timestamps are separate values.
Submission preserves the revision-saved time; acceptance preserves both earlier
times. A successor draft starts without submission/acceptance timestamps while
its predecessor retains them. All times are explicitly preview/browser-generated,
not authoritative server timestamps or durable audit evidence.

The note is optional. No minimum-content business rule is introduced by this
preview. Request changes remains visibly deferred; screen sign-off does not
approve the complete review workflow, backend contract or production use.


## Production Details extension (development preview only)

Open **Production Details** alongside the four explanation views and select one sample machine. Both sample personas can enter order/job, part and operation as free text, declared effective date/time with an explicit fixed UTC offset (India: +05:30), and an optional note. Blank text displays as Not provided. All declarations are labelled User-declared production details — not verified against master data.

Entries are append-only in page memory and survive internal shift/persona/view changes until reload or leaving the page. History retains author, browser-recorded time and original declared local time/offset and UTC equivalent. The context card explicitly shows the declaration at selected shift start, not a claimed live assignment. Future declarations remain in history until applicable. A later declared job change explicitly ends the previous context without modifying its stored entry. Same-time or earlier starts are blocked pending a separate correction workflow; a past start requires acknowledgement and never rewrites evidence attribution. These are preview rules for review, not a durable activation contract.

No authentication or actual machine-scope enforcement is implemented. No master records, approved assignments, production configuration, quantities, targets, counter qualification or OEE are changed. Entries disappear on reload. Production builds exclude this preview. Durable persistence, authorization and activation require separate approval.

### Temporary Shift Targets preview

The separate Shift Targets view accepts whole-shift target quantity, explicit unit and reason/note for one selected sample machine and exact shift occurrence. Only the illustrative supervisor persona may enter targets; no authentication or real permission enforcement is connected. Zero is valid; negative/invalid values and missing units are rejected. Revisions require a reason and append history without replacing the original entered draft. Unsaved input blocks context switching until saved or discarded.

Declared production details at shift start are displayed with missing values explicit and copied into each temporary target revision for context; these are unverified declarations, not master-data identities or quantity attribution. A mid-shift job change requires separate compatible comparison design. Targets are scoped to occurrences and do not carry across shifts.

Every entered revision remains a temporary draft, with browser author/time. Original entered target does not mean approved baseline. Approval, applicable revision selection, shift-start baseline sealing, actual entry and qualified actual feeds are deferred. Actual, source, qualification, cutoff and unit compatibility remain explicitly not established; variance/achievement remain unavailable. ZeroTarget and BaselineNotEstablished are descriptive limitations, not computed achievement. No target sum, automatic approval, historical attribution or OEE calculation is performed. Entries disappear on reload. Keep PR draft; production enablement remains separate.

## FC-DEMO-01 continuation

The development route now opens a workspace with explicit Real read-only and Sample preview modes. Choose Sample preview to review the original temporary workflows without a backend. Real read-only reuses configured reporting sources and exact shift/activity bounds; see `docs/features/fc-demo-01-readonly-supervisor.md`. Both modes remain excluded from production builds. The original sample reducer is not used to model real machine evidence or durable explanations.
