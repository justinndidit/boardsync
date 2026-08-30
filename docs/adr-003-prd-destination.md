# ADR 003 — A PRD does not create its own project

**Status:** rejected · **Date:** 2026-08-30 · **Supersedes:** nothing ·
**Related:** [ADR 001](adr-001-team-sprints.md), [ADR 002](adr-002-proposals.md)

## The question

Decomposition runs inside a project: the route is
`/organizations/:slug/projects/:projectId/decompose` and `Proposal.ProjectId` is required. A PRD is
often a *new* initiative, so should decomposition be able to create the project it lands in —
chosen at acceptance, the way the sprint already is?

## Rejected, and why

**The order of operations already answers it: an org admin creates the project, and the team plans
into it.** Those are two acts by two people with two different permissions, and both are already
supported. Folding the first into the second buys one person a navigation step and costs:

- a nullable `Proposal.ProjectId` and its migration;
- an org-scoped request path beside the project-scoped one;
- an org-scoped proposal listing, because a proposal with no project appears in no project's list —
  reintroducing, in a new shape, the "written but unreadable" trap that §1.2 of
  `outstanding-2026-08-30.md` was opened to close;
- a permission that depends on the request body — `workitem:write` to accept into a project,
  `org:admin` to create one — which no `[RequirePermission]` attribute can express, so it moves
  into the action.

That is a lot of moving structure for a navigation step, and every piece is somewhere the
destination and the permission can come to disagree.

## What is already true

**The project and the sprint are connected through the team, and that is the design.**
`GET /api/projects/{projectId}/sprints` is a read-through to the assigned team's sprints — ADR 001:
"there is no such thing as a project's own sprints". Acceptance creates the sprint on
`proposal.TeamId`, which is the project's `AssignedTeamId` unless the caller named another. So the
work accepted into a project and the sprint planned in the same moment both surface on that
project's pages, with nothing attached to anything.

They used to be able to diverge, and it was worse than a missing listing. A caller who passed an
explicit `teamId` that was not the project's own got, depending on whether they asked for a sprint:

- **with a sprint** — `AddWorkItemAsync` rejecting a work item created seconds earlier and
  reporting it as *not found*, rolling the whole acceptance back on a nonsense error;
- **without one** — no complaint at all, and work items sitting in the project tagged to a team
  with no relationship to it.

`RequestAsync` now refuses a team that is not the project's assigned one, which is the earliest
point it is knowable and the only one where the message can be honest. The UI always sent the
project's team, so this was reachable from the API directly.

## If the navigation step is worth removing later

The cheap version, needing none of the above: a "New project" affordance on the decompose page for
callers holding `org:admin`, posting to the existing `POST /api/orgs/{orgId}/projects` and then
routing to that project's decompose page. Same two acts, same two permissions, one less trip
through the sidebar — and no schema, listing, or authorization change.
