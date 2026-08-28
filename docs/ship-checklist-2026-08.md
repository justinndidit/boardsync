# What is left to ship

**Date:** 2026-08-24
**Measured, not estimated:** route-by-route diff, both test suites, the phase
checkboxes, and the audit register.

---

## 0. Where this actually stands

| | Backend | Frontend |
| --- | --- | --- |
| Tests | **841** | **49** |
| API surface | 116 routes | **100 called (86%)** |
| Broken calls | — | **0** |

Three weeks ago the frontend called 65% of the API and the product's
differentiators — git-driven boards, the QA gate — had no UI at all. Both now
exist and are wired.

**Nothing on the critical path is a large piece of work.** What follows is
four items, and the largest is a day.

---

## 1. Ship blockers

### 1.1 No token refresh — *the only one every user hits*

`POST /Auth/refresh-token` and `revoke-token` are never called. The session
ends when the access token expires, mid-sentence, and the user is bounced to
login with `returnTo`. Nothing else on this list will be noticed as often.

The interceptor in `services/api.ts` already detects the expiry and already
distinguishes "the session ended" from "this request answered 401" — it just
has nowhere to go. The work is a refresh call plus a single-flight queue so a
burst of 401s produces one refresh rather than five.

**Half a day. Do this first.**

### 1.2 `ui/` is gitignored, so the repo cannot build itself

Every build path is now correct and the image builds and serves — verified by
running it. But `ui/` is ignored at the root, so a clone of the server
repository does not contain the frontend and `docker compose build ui` fails on
a fresh checkout.

This is a decision, not a defect: **submodule, monorepo, or two independent
pipelines.** Whoever owns deployment should pick. Everything else is done.

Worth settling alongside **open decision 8** (deployment target), which also
gates all of Phase F — they are the same conversation.

**An hour once decided.**

### 1.3 Three settings screens are non-functional shells

`SecuritySettings`, `ProfileSettings` and `NotificationSettings` render inputs
with no submit handler and no service call. Changing your password appears to
work and does nothing.

`POST /Auth/change-password` and `PUT /Auth/profile` exist and are uncalled.
**Notification preferences have no backend at all** — that screen should be
removed rather than wired, because there is nothing behind it and a toggle that
silently does nothing is worse than an absent one.

**Half a day**, most of it deletion.

### 1.4 Work cannot be added to the backlog

`POST /projects/{id}/backlog` is the one genuinely missing product call. The
backlog reads correctly now, and nothing can be put in it from the UI.

**An hour.**

---

## 2. Not blockers, but visible on day one

| | |
| --- | --- |
| **Two drawers, two edit surfaces** | `WorkItemDrawer` now carries an inline history/comments implementation *and* the extracted components. Both render. Pick one. |
| **`DrawerOverview` saves nothing** | Its status and priority pickers write to local state that is never persisted. Either wire it or make the drawer read-only. |
| **Cumulative Flow / Team Performance** | Both tabs say plainly that the API does not compute them. Honest, and fine to ship. |
| **`POST /sprints/{id}/close`** | Unused — the client closes sprints via `PATCH /status`. Confirm which is intended and delete the other. |

---

## 3. Backend: beyond v1, not inside it

Everything on the shipping path already has its endpoint, tested. What remains
on the server is genuinely *next*, not *missing*:

- **`Modules/Intelligence`** — the narrative layer. The largest unbuilt piece in
  the product, and the lowest-risk: §8.3 already settled the hard part, that the
  module computes nothing and cites only figures it is handed.
- **Outbound provider clients** — backfill on link, and Azure DevOps merge
  read-back. Three open items, one missing piece; they are one increment, not
  three.
- **Postgres FTS** (audit 9), **notification preferences**, **email delivery**,
  **GitLab signing tokens**, **Bitbucket**, **`@mentions`**. Each has a recorded
  reason for its position.
- **Git activity per work item** is *not computable* — binding is stateless and
  no `CommitLink` table exists. That was right for binding; it needs a
  deliberate decision, not a discovery mid-report.

**Phase F has not started**: pgbouncer, `ActivityLogs` partitioning, load
testing against webhook bursts, and a security review of webhook ingest and the
integration principal. All four change shape with the deployment target.

One piece of bookkeeping: the Phase B checkbox for **typed principals** is still
unticked and the work shipped in Phase C. The register says less than it should.

---

## 4. The honest risk

**Frontend test coverage is 49 tests against 39k lines**, and they cover pure
functions: figure shaping, branch names, history wording, sprint resolution.
Nothing renders a component, and nothing exercises a hook.

That is not an argument to delay shipping. It is an argument for knowing where
the thin ice is:

- capability batching and the identity-keyed cache
- the optimistic notification read markers
- `useStateTransitions`, which decides what the QA gate shows
- the wiring of `useSelectedSprint` into two pages — the resolver is tested,
  the wiring is not

All four shipped on typecheck and reading. The server's 841 tests caught three
defects that reading did not — a never-written `ProjectId`, six events never
delivered, a concurrency token broken at three points — which is the argument
for backfilling these once the release is out, not before it.

---

## 5. The order

1. **Token refresh** — half a day, and the only item every user hits daily.
2. **Decide the repository question** — an hour, unblocks deployment entirely.
3. **Backlog add** — an hour.
4. **Settings: wire two screens, delete one** — half a day.
5. **Collapse the duplicate drawer implementations** — half a day, and it stops
   the next person fixing a bug in the copy that is not rendering.

**That is roughly two days of work to a shippable v1.** Everything else in this
document is a next release.

The one thing I would not skip: **run through it end to end on a fresh browser
before shipping** — create an org, a project with a key, link a repository, push
a branch named after a work item, and watch the card move. Every part of that is
tested in isolation. None of it has been done in one sitting by a person.
