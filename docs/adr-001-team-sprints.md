# ADR 001 — Sprints belong to teams, not projects

**Date:** 2026-08-27
**Status:** accepted, implemented
**Supersedes:** `Stage4_SprintBelongsToProject` (2026-08-20), which moved them
the other way.

---

## The problem

A team may serve several projects. Sprints belonged to projects with one active
each, so a team on three projects ran **three concurrent sprints**: three
backlogs, three burndowns, three velocity charts.

A team has one capacity and one standup. Splitting its throughput across three
charts that cannot be summed does not describe anything real, and the numbers
get worse as the team spreads wider — which is exactly when somebody would go
looking at them.

The permission model already conceded the point: sprint authority is a **team**
appointment (Scrum Master, Product Owner) that reached projects through the
team → project edge. The authority was team-shaped while the object was not.

## The decision

**A sprint belongs to a team and may contain work from any project that team
serves.**

- `Sprint.TeamId` replaces `Sprint.ProjectId`.
- **One active sprint per team**, where it was one per project.
- Sprint numbers run per team.
- Sprint permissions resolve at **team scope**, not project scope.

## What follows, and why

### Sprint permissions move to team scope

`sprint:read`, `sprint:manage`, `sprint:scope` and `sprint:order` were project
permissions reached through the team edge. They are now held at team scope
directly, which is what they always described.

**A project role no longer grants sprint access.** Someone with a direct
`Contributor` grant on a project, who is not on the owning team, keeps the
board, the backlog and the work items — all project-scope objects — and does not
see the team's sprint. That is the honest reading: they contribute to a project;
they are not part of the team planning it.

### Project boards stay project-scoped

A board still belongs to a project and shows that project's work. What changed
is where its cards come from: **the team's active sprint, filtered to this
project.** Three projects served by one team show three boards over one sprint,
which is what a team working across three codebases actually looks like.

Keeping the board per-project matters — a project's flow is still a real thing
to look at, and a merged board of three codebases would be unreadable.

### Velocity becomes a team measure

It has to. A sprint now spans projects, so its completed points are the team's,
not any one project's. `GET /api/teams/{teamId}/reports/velocity` is the real
route.

`GET /api/projects/{projectId}/reports/velocity` **stays**, and resolves through
the project's team. It answers "how fast does the team building this move",
which is the question somebody on a project page is actually asking. It is a
convenience over the same data, not a second measure.

### The sprint report gains a per-project breakdown

A sprint across three projects with one set of totals hides where the work went.
The summary stays team-wide — that is the point of the change — and `byProject`
carries the split, so "we finished 40 points" and "34 of them were in one
project" are both answerable.

### Project sprint routes stay, as reads

`GET /api/projects/{projectId}/sprints` and `.../sprints/active` resolve through
the project's team. The board needs "the active sprint for this project" on
every load, and making every client resolve the team first would be a round trip
for something the server can answer directly.

Writes moved: creating a sprint is `POST /api/teams/{teamId}/sprints`, because a
sprint is the team's.

## Migration

Two things need deciding rather than computing:

**Number collisions.** Two projects of one team each had a Sprint 1. Renumbered
per team by start date, then id — so the sequence is chronological and stable,
and no team has two sprints with the same number.

**Two active sprints for one team.** Also possible, and now illegal. The most
recently started stays `Active`; the others become `Completed`. Not deleted, and
not left active: a completed sprint keeps its history and its velocity point,
and picking the newest matches what a team would say they are working on.

Both are recorded here because neither is recoverable from the data afterwards.

## What this costs

- A project cannot have its own sprint cadence any more. A team serving two
  products on different rhythms now has to choose one, or be split into two
  teams. **That is the intended pressure** — the alternative was three sprints
  nobody could add up.
- Velocity history from before the change is per-project and becomes per-team on
  migration. The numbers do not change; what they are attributed to does.

## What implementation turned up

Three things worth recording, because none was visible from the design.

**The permission move is the largest part of the change.** Sprint permissions had
to leave every project-scope role block and reappear at team scope, the sprint
scope resolver had to resolve to a team, the realtime topic authorizer had to
ask at team scope, and two in-action checks — adding a work item to a sprint,
and moving backlog items into one — were still asking the project. Missing any
one of those would have produced a 403 that looked like a bug in something else.

**Sprint events now broadcast on the team topic.** They carried a project id;
with a sprint spanning several, there is no one project to send to, and picking
one would leave everybody watching the others silently stale. Sprint activity
entries carry no project id at all for the same reason.

**A failed migration was being swallowed.** Outside Production the startup
migration logged and continued, so a broken migration in this change surfaced as
twenty unrelated integration tests reporting "a database error occurred" against
a half-applied schema. The test factory now runs as `Testing` and fails fast: a
run against a schema that did not build is not a run. That was a pre-existing
hazard this change happened to trip.

## Rejected

**Team sprints *and* project sprints.** Two kinds of sprint, two sets of rules,
and every report needing to say which it meant. The complexity lands on every
reader of every chart to serve a case nobody has yet described.

**Leaving it as it was, and telling people not to span teams across projects.**
A modelling opinion the schema does not enforce is a modelling opinion that gets
ignored.
