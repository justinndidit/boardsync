# Getting BoardSync to pitch level

**Date:** 2026-08-28 · **Tier 1 built the same day** — see the section for what that did and did
not settle.
**Question this answers:** not "is the code done" but "can somebody sit in front of this for
twenty minutes and come away believing the claim".
**Method:** built both sides, ran both suites, and checked each item against the code rather
than against the previous status docs.

---

## 0. What the pitch actually claims, and whether it can be shown

The product makes four claims. They are not equally demonstrable today.

| Claim | Demoable now? |
| --- | --- |
| The board updates itself from git — no card dragging | **Yes**, with a public webhook URL |
| The QA gate means "done" was verified by a person, not by a merge | **Yes** |
| Delivery metrics are computed from history, so they cannot be wrong | **Yes**, with seeded history |
| AI decomposes a PRD and narrates a sprint — and can never write to the board | **Reachable, unproven.** The UI is built; the model call has still never run. |

Three of four are real and reachable, and were before this document was written.

The fourth changed shape on 2026-08-28 rather than being finished. It had no interface at all; it
now has one, and the screens are the part that was never in doubt. **What is still unproven is the
part underneath: no decomposition and no narrative has ever been produced by the real API**, because
there is no key in the build environment. Every screen has been exercised against the shapes the C#
DTOs declare, which is a check on the client and not on the integration.

**So the asymmetry this checklist was organised around has moved.** It used to be "the backend is
ahead of the frontend". It is now "everything is built and one link in the chain has never carried
current". Tier 0 is the pitch. Tier 1 is done. Everything below them is polish.

---

## Tier 0 — the demo does not run without these

### 0.1 Plumb the Anthropic API key through

`ClaudeNarrator.cs:41` and `ClaudeDecomposer.cs:48` both read `Intelligence:AnthropicApiKey`,
falling back to the `ANTHROPIC_API_KEY` environment variable. **Neither appears in `.env.sample`
nor in `docker-compose.prod.yaml`**, so there is no way to get a key into the running container
without editing the compose file by hand.

The failure is silent by design — an unconfigured client is `null` and the endpoint returns no
narrative rather than an error. That is the right behaviour in production and exactly the wrong
behaviour ten minutes before a pitch, because nothing tells you the key did not arrive.

- [x] Add `ANTHROPIC_API_KEY=` to `.env.sample`
- [x] Add `Intelligence__AnthropicApiKey: ${ANTHROPIC_API_KEY}` to the `api` service in
      `docker-compose.prod.yaml`
- [x] Log once at startup whether narration and decomposition are configured, the way Redis
      already announces its fallback. Silence is fine in production; silence is a trap in a demo.

### 0.2 Make one real model call

Both services have **never run against the real API** — no key in the build environment. Every
test around them uses a fake. What is unproven is not the logic but the integration: the SDK
surface, the structured-output schema round trip, the shape of what actually comes back.

- [ ] Run one decomposition against a real PRD end to end
- [ ] Run one narration against a real closed sprint
- [ ] Confirm `DecompositionGuard` and `NarrativeGuard` behave on real output, not fixture output

This is the single highest-risk unknown in the product. It is also probably an hour. Do it first,
because everything in Tier 1 assumes it works.

### 0.3 A public webhook URL

GitHub cannot reach `localhost`. The demo's central moment — push a branch, watch the card move —
requires the provider to reach the API.

- [ ] Stand up a tunnel (`cloudflared tunnel` or ngrok) or deploy the stack somewhere reachable
- [ ] Set `APP_BASE_URL` to it so the installation response hands back a webhook URL that works
- [ ] Register the webhook against a real throwaway repository and confirm a delivery lands in
      `GET /api/git/installations/{id}/deliveries`

### 0.4 Seed data — **built 2026-08-28** (`scripts/seed_demo.sql`, `make seed-demo`)

`scripts/init_db.sql` creates the database and two extensions. That is all. **There is no seed
script anywhere in the repository**, so a fresh stack presents an empty board, and an empty board
demonstrates nothing.

The subtlety that will cost an afternoon if it is discovered live: `ReportingService.cs:191`
reconstructs every metric from `WorkItemHistory`. **Seeding current states produces flat charts.**
Burndown, velocity and cycle time all need backdated history rows, or the reports tab — one of the
strongest parts of the pitch — shows three empty graphs.

- [x] Write a seed: one organization, one team, one project with a key, ~20 work items spread
      across all five states
- [x] **Backdate `WorkItemHistory` rows** so burndown has a curve and cycle time has a median
- [x] Two or three closed sprints, so velocity has more than one bar
- [x] One active sprint mid-flight, so the live board has somewhere to move a card to
- [x] Several items sitting in `Resolved` — "Awaiting QA" is the number that makes the QA gate
      argument concrete, and it needs items in it to be non-zero
- [x] Make it re-runnable, so a botched rehearsal costs one command and not a rebuild

---

## Tier 1 — the differentiator has an interface · **built 2026-08-28**

Was: five routes existing, tested, and called by nothing.

Now: `ui/boardsync/src/features/intelligence/` — 14 files, ~2,800 lines. A service against all five
routes, the decomposition review screen at `/organizations/:slug/projects/:projectId/decompose`
(sidebar entry beside Reports), and the narrative panel on the reports Overview tab. 15 new tests,
typecheck clean, no new lint errors. The routes:

```
POST /api/projects/{id}/intelligence/decompose      202 + proposal id to poll
GET  /api/intelligence/proposals/{id}
POST /api/intelligence/proposals/{id}/accept
POST /api/intelligence/proposals/{id}/reject
GET  /api/sprints/{id}/report/narrative
```

### 1.1 Decomposition — paste a PRD, get a reviewable plan

- [x] `intelligence.service.ts` against the five routes
- [x] A PRD input surface, a `202`, and polling on the proposal id
- [x] The draft tree with per-node checkboxes
- [x] **Selecting a node selects its ancestors and not its descendants** — this is ADR 002's
      central rule and the thing to demonstrate deliberately, not a detail to get right quietly.
      Tick a story, watch its feature tick itself; tick an epic, watch forty tasks *not* appear
- [x] Accept → the items land on the board through the same path as a person clicking "New work
      item", with history rows and events
- [x] Reject, so the "the model proposes, a human disposes" line has both halves on screen

### 1.2 Narrative — prose beside the figures it cites

- [x] Render the narrative on the sprint report page, next to the computed numbers
- [x] **Show the withheld state.** `NarrativeGuard` checks every figure in the prose against the
      report it was handed and withholds prose that fails, returning the offending sentences. A
      viewer who has sat through a year of AI demos will assume the numbers are invented — being
      able to show the mechanism that catches an invented one is the most credible thing in the
      product. Do not hide it behind a generic error state.

### 1.3 What to say about the guard, in one line

*The module that narrates computes nothing. It is handed the figures and checked afterwards
against them.* That sentence, backed by a visible rejection, is the demo.

### 1.4 Two things the build changed, and one it did not

Both were found by reading the server while wiring the client, and neither was in this document's
original plan:

- **An empty selection could not be sent.** The API reads an empty `include` as *the whole draft*,
  so an unticked screen passing one through would have created everything the reviewer had just
  declined — the exact inversion of the feature. The UI refuses to submit an empty selection and
  says that rejecting is how you decline. There is a test pinning it.
- **The narrative cannot auto-fetch.** `NarrativeService` holds no cache, so every
  `GET .../report/narrative` reaches the model and charges the allowance. Loading one on tab open
  would spend money on a page being scrolled past and would reword itself under a team
  mid-conversation. It is a button, and the panel says why.
- **The `NotGrounded` reason code is unreachable.** An ungrounded answer comes back from
  `NarrativeService.cs:107` as a *successful* result carrying `grounded: false`, empty prose and the
  offending sentences — not as `NarrativeUnavailable.NotGrounded`. The panel branches on `grounded`
  first. Left as it is: the enum value costs nothing and the path that runs is the one handled.

**What the build did not settle: 0.2.** These screens have only ever rendered against the shapes the
DTOs declare. Until one real decomposition and one real narrative have been through them, the demo
in Tier 4 is the first time anybody will find out.

---

## Tier 2 — a viewer will notice these

- [ ] **Two of five report tabs are empty.** `CumulativeFlowReport.tsx` and
      `TeamPerformanceReport.tsx` both say plainly that the API does not compute them. Honest, and
      still two empty tabs out of five in the section you are pitching. Either build the CFD — it
      is computable from the same history reconstruction and was deferred only for size — or hide
      both tabs for the pitch and put them back after.
- [x] **Column reorder emits no domain event** (`BoardService.cs:156`, audit finding 16). If the
      demo shows realtime by opening two browsers, dragging a column will not propagate. Either fix
      it — it is one `EnqueueAsync` call next to the ones already in that file — or do not drag a
      column on stage.
- [ ] **Two live drawer implementations.** `TaskDetailsDrawer` renders on `BoardsPage.tsx:2182`,
      `WorkItemDrawer` (2003 lines) on `WorkItemsPage.tsx:1105`. The same object opens two
      different ways depending on which page you came from, which reads as unfinished to anyone
      clicking around unaccompanied.
- [x] **`POST /sprints/{id}/close` is dead** — the client closes via `PATCH /status`. Delete one.

Not for the pitch: the 46 lint errors. They are ~26 instances of one class
(`react-hooks/set-state-in-effect`) across ~36 hand-rolled fetch hooks, and fixing them properly
means adopting a query library. Invisible to a viewer. Deliberately deferred.

---

## Tier 3 — answers to have ready if anyone looks closely

Not work, mostly. Know these before being asked.

- [ ] **`ui/` is gitignored** (`.gitignore:477`). Every build path is correct and the image builds,
      but a clean clone of this repository does not contain the frontend. If the pitch ends with
      "can we have the repo", that is the first thing they hit. The fix is the submodule /
      monorepo / two-pipelines decision, and it is an hour once decided — but it is a decision.
- [ ] **The token budget is in-memory** (`TokenBudget.cs`) — per instance, reset daily, forgiven on
      restart. Correct as a runaway-loop guard, wrong as a quota somebody has paid for. Already
      written down in the class remarks; the honest answer is "it is a cost guard, not billing".
- [ ] **Prompt caching is not implemented.** The system prompt is a constant so the prefix is
      byte-stable, but no `cache_control` breakpoint is set.
- [ ] **Not streamed.** The call is a plain `Create`; §8.2's `Messages.Stream(...)` is not this SDK
      version's API. Safe because it runs in a background job, but a very large PRD could hit the
      client timeout.
- [ ] **Frontend test coverage is 96 tests against ~49k lines** — 81 before the Intelligence work,
      which added 15. Only `Modal` and `ProposalTree` have render tests; everything else is hooks,
      services and pure functions. The backend's 917 are the counterweight, and they caught three
      defects reading did not.

---

## Tier 4 — the rehearsal

Every part of the demo path is tested in isolation. **None of it has been done in one sitting by a
person.** That is the largest remaining risk and it costs an afternoon to retire.

- [ ] Fresh browser, fresh database. Create an organization, a team, a project with a key
- [ ] Connect a git installation, copy the webhook URL and secret — **they are shown once**, and
      fumbling this on stage is the most likely way the demo dies
- [ ] Link a repository to the project
- [ ] Create a work item, note its reference (`BS-142`)
- [ ] Push a branch named `bs-142-...`, watch the card reach **Active**
- [ ] Open a pull request, watch **InReview**
- [ ] Merge it, watch **Resolved / Awaiting QA** — and say out loud that it stopped there because
      the integration principal holds `workitem:write` and deliberately not `workitem:verify`
- [ ] Close it as a human with `workitem:verify`. That contrast is the QA gate argument
- [ ] Open the reports tab: burndown, velocity, cycle time, median verification wait
- [ ] Generate the narrative over those figures
- [ ] Paste a PRD, decompose, accept part of the tree, watch the ancestors come with it
- [ ] **Run the whole thing twice.** The second run is where the timing problems show up

---

## The order I would work in

1. ~~**Key plumbing**~~ (0.1) — **done.** **One real model call (0.2) is still open**, and it
   remains the only thing in the product whose outcome is genuinely unknown.
2. ~~**Seed data with backdated history**~~ (0.4) — **done 2026-08-28.** Velocity reads 21 / 24 / 28
   across three completed sprints; median verification wait is 20h on 19 measured items.
3. ~~**Tunnel and a real repository**~~ (0.3) — **done.** Deliveries arrive, verify, bind and move
   work. The git-driven board is proven end to end.
4. ~~**Intelligence UI** (1.1, 1.2)~~ — **done 2026-08-28**, and the reason it is no longer the
   long pole. It is unproven rather than unbuilt: step 1 is what proves it.
5. **Hide or build the two empty report tabs; fix the reorder event** (Tier 2) — half a day.
6. **Rehearse twice** (Tier 4) — an afternoon.

**Roughly two days to a pitch that supports all four claims** — down from four to five, because
the largest item on that estimate is built.

The shape of the remaining work has changed with it. It was *build the thing*; it is now *seed it,
plug in a key, and rehearse*. That is a better position to be in and a worse one to be casual
about: none of what is left is hard, and all of it is the kind of thing that is discovered at the
wrong moment when it is skipped.

Cutting the AI half is still available and now costs less than it saves — the screens exist, so
dropping them from the pitch means not demonstrating something already built. If the real API call
in 0.2 goes badly, that is the fallback, and the remaining three claims are a real product on their
own.

---

## What is deliberately not on this list

Phase F in its entirety (pgbouncer, `ActivityLogs` partitioning, load testing, the webhook
security review), the outbound provider clients, backfill on link, Azure DevOps merge read-back,
notification preferences, email delivery, `@mentions`, GitLab signing tokens, Bitbucket, the
generated TypeScript client, and the query-library migration.

None of them changes what a viewer sees. Several of them gate a real deployment, and open
decision 8 — the deployment target — gates most of those in turn. That is the conversation after
the pitch, not before it.
