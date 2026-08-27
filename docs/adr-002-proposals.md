# ADR 002 — AI output lands as a proposal, never on the board

**Status:** accepted · **Date:** 2026-08-27 · **Supersedes:** nothing · **Related:** [ADR 001](adr-001-team-sprints.md)

## Context

Phase E adds PRD decomposition: a requirements document goes in, a hierarchy of work items comes
out. The question that decides the design is what happens to that hierarchy.

BoardSync's whole claim is that the board reflects reality without anyone maintaining it. That claim
is why the QA gate exists — automation moves work as far as *Resolved* and a human with
`workitem:verify` is the only thing that closes it. A decomposition feature that writes work items
directly would contradict the same principle from the other end: the board would gain work nobody
chose, sourced from a model, indistinguishable from work the team committed to.

## Decision

**The model proposes; a human accepts; only the acceptance writes.**

A decomposition produces a `Proposal` — a row in `intel.Proposals` holding a draft tree as JSON. It
has no authority and needs no permission to exist. Acceptance calls
`WorkItemService.CreateAsync`, the same method a person clicking "New work item" calls, with the
same validation, events, and history rows. There is no privileged path from this module into the
domain.

### Why a proposal is not a shadow work item tree

The draft is stored as JSON rather than as rows in `WorkItems` with a `IsDraft` flag. A flag would
mean every existing query over work items has to learn to exclude drafts, and the day one forgets
is the day a proposal appears on somebody's board. Drafts have no identity anything refers to and no
lifecycle beyond accept-or-reject, so they are not domain data.

### Selecting a node selects its ancestors

A reviewer may accept part of a draft. If they choose a User Story whose Feature they did not, the
story's parent would not exist.

Three options, and the reason for the choice:

- **Reject the selection** — makes the reviewer reconstruct the tree by hand to work out what else
  they must tick. The tool creates work instead of saving it.
- **Reparent to the top level** — silently changes what the item means. A story's parent is most of
  its context; "Download an invoice" under *Invoices* under *Billing* is a different thing from
  "Download an invoice" floating at the root.
- **Carry the ancestors in** ← chosen. A Feature nobody ticked but whose Story they did is still
  work the team took on. It is the only option that preserves the reviewer's intent without
  inventing structure.

**Selecting a node does not select its descendants.** Accepting an epic and silently getting forty
tasks nobody read is precisely the failure this ADR exists to prevent.

### The guard is separate from the prompt

`DecompositionGuard` checks the tree before a human ever sees it: the nesting rule
(`Epic → Feature → Story → Task/Bug`), a node cap, title and description limits, estimate range,
duplicate siblings.

This is deliberately *not* left to the schema or the prompt. Structured output constrains the JSON
shape and has no opinion about whether a Task may sit under an Epic — the domain does. A prompt is a
request; only the guard is a guarantee. Without it, an invalid nesting reaches acceptance and throws
partway through creating the tree, after part of it already exists on the board.

The guard normalizes what it safely can and rejects what it cannot, and the line between them is
whether the fix preserves the author's meaning. Trimming whitespace does. Reparenting a task under a
fabricated story does not.

**Estimates outside the accepted range are dropped, not clamped.** Clamping 9000 to 1000 keeps a
number nobody meant, and story points are read as a judgment about size — a wrong one is worse than
an absent one, which at least reads as "not estimated".

### Node ids are ours, not the model's

The model returns a nested structure and no identifiers. Ids are assigned by the guard as it walks
the tree. Asking a model for unique ids it must then reference consistently invites the one mistake
that makes the whole tree unusable, and the nesting is already carried by the shape of the response,
which it cannot get wrong.

### Decomposition runs as a job

Tens of seconds of model time. `POST .../decompose` returns `202` with a proposal id to poll. The
proposal id doubles as the job's idempotency key, and the handler stops if the proposal has left
`Pending` — so a worker that dies mid-call and is retried does not bill the organization twice for
the same document.

### Acceptance is one transaction

`CreateAsync` saves per item. Without an explicit transaction, a failure on the twentieth of forty
items leaves nineteen real work items on the board and a proposal still marked `Ready` — the board
gains half a plan, and re-accepting duplicates the half that worked.

### Permissions

Every endpoint requires `workitem:write` in the target project, resolved through
`ProposalScopeResolver`. Requesting a draft is gated as tightly as accepting one on purpose: a
decomposition spends the organization's money, so the permission to spend it belongs with the
permission to create the work it produces.

Scoping to the project rather than the organization is the tighter answer and the correct one — an
organization administrator with no standing in a project has no business accepting a plan into it.

## Consequences

- Nothing this module produces reaches the board without a person choosing it, item by item if they
  want.
- Every accept and reject is a labelled example of what this team considers a good breakdown.
  Proposals are kept after the decision for that reason.
- A reviewer must read the draft. That is the cost, and it is the point.
- **The guard checks that a tree is well-formed, not that it is correct.** A structurally perfect
  decomposition that misreads the PRD, omits a requirement, or splits work along the wrong seams
  passes every check. That judgment is the human's, which is why acceptance exists rather than
  a confidence threshold.

## What is not built

- **The model call is unexercised.** No API key in the build environment, so `ClaudeDecomposer` has
  never run against the real API. Everything around it — guard, selection, budget, job idempotency —
  is tested against a fake.
- **Prompt caching is not implemented.** §8.2 asks for the stable prefix to be cached. The system
  prompt is a constant so the prefix is byte-identical across calls, but no `cache_control` breakpoint
  is set, so nothing is actually cached yet.
- **Not streamed.** §8.2 specifies streaming for large documents; that snippet names
  `Messages.Stream(...)`, which is not this SDK version's API — it is `CreateStreaming`. The call is
  a plain `Create` for now, which is safe because it runs in a background job rather than a request
  thread, but a very large PRD could still hit the client timeout.
- **No frontend.** The endpoints exist and nothing calls them.
