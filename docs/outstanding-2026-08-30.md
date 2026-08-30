# Everything still outstanding

**Date:** 2026-08-30 · **§1 closed the same day** — see below.
**Excludes** the Anthropic API key and the first real model call — known, and yours to do.
**Method:** verified against the code, the database and both test suites rather than recalled.
Supersedes the inventory in `pitch-checklist-2026-08-28.md`, which has stale entries.

**Where things stand:** 965 backend tests, 113 frontend, both builds green, 46 lint errors and 35
build warnings (both unchanged baselines). Git-driven transitions, realtime boards, the sprint
lifecycle, backlog planning, work item hierarchy and the cumulative flow diagram all work and are
covered.

---

## 1. Closed — the two traps, and what they were hiding

Both were open doors that stranded data quietly rather than failing loudly. Closing them turned up a
third gap neither list had.

### 1.1 `PATCH /sprints/{id}/status` no longer completes a sprint

It permitted `Active → Completed` and then only flipped the status — the exact behaviour that
stranded unfinished work before `POST /close` was wired. It now refuses `Completed` and says why.

`ValidateTransition` still lists that move as legal, because it is: `CloseAsync` makes it. What is
refused is *reaching* it through a bare status change.

### 1.2 Proposals can be found again

`GET /projects/{id}/intelligence/proposals` — newest first, summaries rather than drafts, gated on
`workitem:write` like decomposing itself. An **Earlier proposals** list on the Decompose page shows
status, date, node count and a preview of the source document; Ready ones reopen, the rest are a
record.

The failed ones are the point. A failed proposal has no draft to navigate back into, so a list of
successes would hide exactly the ones whose reason somebody needs.

### 1.3 What closing 1.1 revealed: the sprint lifecycle had no UI

Shutting the bypass showed that nothing else was using the front door either.

- **Nothing started a sprint.** No client called `updateStatus` at all, so sprints sat in `Planning`
  indefinitely — and only an `Active` sprint can be closed.
- **`setIsCompleteSprintModalOpen(true)` was never called**, so the close dialog — the thing that
  decides where unfinished work goes — could not be opened from anywhere.

Both are now actions on each sprint row: **Start** on a Planning sprint, **Complete** on an Active
one. The client's `updateStatus` is narrowed to `"Active"` so the removed case cannot be called by
accident.

**Worth keeping in mind.** Two features shipped this month were unreachable from the product — the
close dialog here, and the bulk *Add to backlog* action whose button a merge had dropped. Both had
handlers, state and tests, and no way in. A route or a button is the cheapest part of a feature and
the easiest one to leave out.

---

## 2. Blocks a real deployment

### 2.1 `ui/` is gitignored — audit finding 7, still open

`.gitignore:477`. Every build path is correct and the image builds, but a clean clone of the server
repository does not contain the frontend, so `docker compose build ui` fails on a fresh checkout.

A decision, not a defect: **submodule, monorepo, or two independent pipelines.** Worth settling in
the same conversation as **open decision 8 — the deployment target** — because pgbouncer, Redis HA
and hub scale-out all look different on managed Kubernetes versus two VMs, and decision 8 gates all
of Phase F below.

### 2.2 Phase F has not started

- Instance count > 1: pgbouncer, `ActivityLogs` partitioning and retention
- Load testing against realistic webhook bursts
- A security review focused on webhook ingest and the integration principal

All four change shape with the deployment target.

### 2.3 The token budget is in memory

`TokenBudget.cs` — per instance, reset daily, forgiven on restart. With several instances the
effective ceiling is the limit times the instance count.

An acceptable shape for a runaway-loop guard and an unacceptable one for a quota somebody has paid
for. Already written down in the class remarks; it becomes real work the moment narration is billed.

---

## 3. Built on one side only

### 3.1 The outbound provider client — one increment, three items

Everything here waits on the same missing piece: an authenticated client per provider. Ingest is
inbound-only today.

- **Backfill on link** — walk the last 90 days when a repository is connected, so the first report
  means something on day one
- **Azure DevOps merge read-back** — ADO raises `git.pullrequest.merged` for any completion,
  including abandon-with-merge; without reading the PR back an abandoned PR can move work to
  Awaiting QA
- **Per-project unbound commits** — "why did my commit not move the card?" is answerable only at
  organization scope today. This is the support-load item.

### 3.2 Git activity per work item — not computable

Commits per item, and items with no git activity. Binding is stateless and there is no `CommitLink`
table, so commit counts have nowhere to come from.

That was the right call for binding. Recording links is a real cost with a real benefit and wants a
deliberate decision rather than a discovery mid-report.

### 3.3 Notification preferences — no backend at all

Everyone gets everything they are entitled to; the only escape hatch is unwatching, which is not the
same thing. The settings screen for this was removed rather than wired, correctly — a toggle that
silently does nothing is worse than an absent one.

### 3.4 Team Performance — the last empty report tab

Two of five were empty; the cumulative flow diagram closed one. This one needs per-person figures,
which means first deciding **what the product is willing to show about individuals**. That is a
product decision, not an engineering one, and it should be made before the chart is built rather
than implied by it.

---

## 4. Deferred by decision, and correctly

Each has a recorded reason in `build_context.md` §10. These are scope, not incompleteness.

| Item | Why it waits |
| --- | --- |
| GitLab signing tokens | Would put GitLab level with GitHub on verification |
| Bitbucket | No adapter |
| Email delivery | The bell is in-app only |
| `@mentions` in comments | Matching names in free text is its own problem |
| Self-hosted git | The port supports it; the work is egress and network policy |

---

## 5. Intelligence, beyond the key

- **Prompt caching is not implemented.** The system prompt is a constant so the prefix is
  byte-stable, but no `cache_control` breakpoint is set — every call pays full input price.
- **Not streamed.** The call is a plain `Create`; §8.2's `Messages.Stream(...)` is not this SDK
  version's API. Safe because it runs in a background job, but a very large PRD could still hit the
  client timeout.
- ~~No proposal list~~ — **done**, see §1.2.
- **The remaining allowance is not visible to anybody.** `ITokenBudget.RemainingAsync` is called
  once, at `DecomposePrd.cs:97`, and only to write a log line — no endpoint returns it. So an
  organization can exhaust its daily budget with the only visible symptom being narratives that
  stop appearing and decompositions that fail with a reason nobody was warned about. The proposal
  list now shows spend per proposal; what is missing is the total and what is left of it.

---

## 6. Frontend debt

### 6.1 Two `backlogService` exports

`features/boards/services/backlog.service.ts` (2 methods) and
`features/boards/backlog/services/backlog.service.ts` (6). Both POST the same endpoint from
different screens, with different item shapes and different paging.

Collapsing them means picking a canonical shape — `BacklogItem` or `BacklogItemApi` — and touching
four call sites.

### 6.2 Two drawer implementations

`TaskDetailsDrawer` on the board, `WorkItemDrawer` (2,008 lines) on Work Items. The same object
opens two different ways depending on which page you came from, and a bug fixed in one does not
appear in the other.

**Now more than cosmetic:** the parent/children panel was added to only one of them, so hierarchy is
visible from one page and not the other.

### 6.3 46 lint errors, mostly one decision

| Count | Rule | What it is |
| --- | --- | --- |
| 30 | `react-hooks/set-state-in-effect` | Hand-rolled fetch hooks with no cache, dedup or cancellation |
| 8 | `@typescript-eslint/no-explicit-any` | Mostly auth page error handlers |
| 7 | `react-refresh/only-export-components` | Constants exported beside components |
| 2 | `react-hooks/incompatible-library` | React Hook Form's `watch()` |

The 30 are one problem. Fixing it properly means adopting a query library — TanStack Query is the
obvious choice — which would delete most of ~36 hooks. That is a dependency decision, still not
made. Navigating between pages refetches everything already held, and a slow response can land after
the user has moved on.

### 6.4 Five files over 1,000 lines

`BoardsPage` 2,231 · `WorkItemDrawer` 2,008 · `SprintPage` 1,742 · `WorkItemsPage` 1,549 ·
`OverviewPage` 1,118. The first two are where new board behaviour lands.

### 6.5 Test coverage is thin, and the thin parts are known

113 tests against ~52k lines. Render tests exist for `Modal`, `ProposalTree` and
`CompleteSprintModal` only; everything else is hooks, services and pure functions.

Untested and load-bearing: capability batching and the identity-keyed cache, the optimistic
notification read markers, `useStateTransitions` (which decides what the QA gate shows), and the
realtime delta application in `useBoard` — the pure delta functions are tested, the wiring is not.

### 6.6 No generated TypeScript client

`build_context.md` §5.3 asked for one. Types are hand-written on both sides of every boundary. It
has held because the alignment work was done deliberately; three type lies found this month —
`InReview` missing from a state union, `storyPoints` typed non-nullable against an `int?`, and
`parentId` absent entirely — are what it costs.

---

## 7. Smaller, and worth a pass

- **`PATCH /workitems/{id}` does not accept `parentId`.** Re-parenting works through `PUT` only, so
  a partial update cannot move an item in the hierarchy.
- **`AwaitingVerificationItems` is current-state** while completed items are now measured at the
  sprint boundary. Deliberate and commented, but it is the one figure on that summary that still
  moves after a sprint ends.
- **35 backend build warnings**, all XML doc-comment mismatches.
- **Audit register**: finding 7 (§2.1), 11b (per-service coverage), 13 (config edges), 17 (orphaned
  history index — build the view or drop the index).
- **Phase B bookkeeping**: the typed-principals checkbox is still unticked though the work shipped
  in Phase C.

---

## 8. The rehearsal — still entirely unticked

Nobody has run the product end to end in one sitting: create an organization, a team, a project with
a key, connect a git installation, copy the webhook URL and secret (**shown once**), link a
repository, push a branch named for a work item, watch it reach Active, open a pull request, merge
it, watch it stop at Awaiting QA, close it as a human, then read the reports.

Every part of that is now proven in isolation. None of it has been done in one go, and the seed
script means a botched attempt costs one command.

**Run it twice.** The second run is where the timing problems show up.

---

## What I would do next, in order

1. ~~**Close the two traps**~~ (§1) — **done.**
2. **Rehearse** (§8) — an afternoon, and the last real unknown after the model call. Now more
   valuable than it was: the sprint lifecycle only became reachable today, so the one path nobody
   has walked end to end has just changed underneath.
3. **Decide the repository question and the deployment target** (§2.1) — an hour of deciding,
   then Phase F becomes plannable.
4. **Collapse the two drawers and the two backlog services** (§6.1, §6.2) — a day, and it stops the
   next hierarchy-shaped feature landing in only one of them.
5. **Sweep for other unreachable features** — §1.3 found two in one month. Worth an hour with the
   route table and the API surface side by side, asking of each endpoint: what in the UI calls this?

Everything else is a next release.
