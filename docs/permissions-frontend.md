# Auth & Permission Changes — What They Mean for the Frontend

Status: §1–§11 shipped · **§12–§16 are new (2026-08-23) and are where you should start** ·
Scope: `server/BoardSync.Api` · Companions: `docs/permissions-model.md`, `build_context.md`

---

> ## 🔴 Read this first — update, 2026-08-23
>
> A second wave of changes landed after the August permission rebuild below. **Two of them give you
> endpoints that remove work you are currently doing by hand**, and three change behaviour you may
> have coded around.
>
> | | Change | Where |
> |---|---|---|
> | **A** | **`GET /api/metadata` exists.** Stop hardcoding roles, priorities, states, types, link types and sprint statuses — all eight vocabularies are served, with labels and sort order. | **§12** |
> | **B** | **`GET /api/me/capabilities` exists.** The "what may I do here" endpoint §10 said was missing. Stop deriving it. | **§13** |
> | **C** | ⚠️ **A new work item state, `InReview`, and a QA gate.** Five states now, and `Closed` is reachable only from `Resolved` by someone holding `workitem:verify`. | **§14** |
> | **D** | ⚠️ **A new role, `Tester`**, valid at team *and* project scope. | **§14.3** |
> | **E** | ⚠️ **Work item activity now appears in the feed, and boards update live.** It never did before — that was a bug, not a design. | **§15** |
> | **F** | Search, the notification bell and the workspace summary now return **less** for some users, and the bell returns **more** for everyone. | **§16** |
> | **G** | **`PATCH /api/workitems/{id}` exists**, and `expectedVersion` is now honoured — it was accepted and ignored before. | **§17** |
> | **H** | Git webhook ingest has landed. Nothing visible yet, but §18 says what changes when binding does. | **§18** |
>
> If you only change one thing this week, make it **A** — everything you build against hardcoded
> constants has to be rewritten once you adopt it.

---

## TL;DR

**Three sprint routes were renamed and the sprint response body changed one field.** Everything else
kept its shape, but the permission model was rebuilt underneath. Six things need frontend work:

| | Change | Impact |
| --- | --- | --- |
| 1 | **Denials are now 404 when you cannot see the resource**, 403 when you can | ⚠️ **§5.1 — affects every error path.** Read this first. |
| 2 | **Team members automatically get access to their team's projects** | §1 — screens gate on less than they used to. |
| 3 | **New team positions**: Team Lead, Scrum Master, Product Owner | §6 — new endpoints, new UI. |
| 4 | **Every role renamed to its scope**: `Reader` splits into `Member` / `Viewer`, project `TeamMember` becomes `Contributor` | ⚠️ **§7.4 — every role string you send or display changes.** |
| 5 | **`GET /api/users/by-email` now needs member-management rights** | ⚠️ §7.2 — breaks mention/assignee pickers if used there. |
| 6 | **Sprints moved from teams to projects**: 3 routes renamed, `teamId` → `projectId` | ⚠️ **§7.5, and §11 for the full route table.** Team-scoped sprint routes now 404. |

Smaller behaviour changes worth knowing: §2 (last OrgAdmin), §3 (cascade), §4 (reassignment),
§7.3 (work item type validation), §8 (bug fixes you may have coded around).

**On deployment state** (as of 2026-08-20):

- **§1–§7.4 are merged and pushed** — the permission vocabulary, the role rename, team positions,
  the endpoint filter, the `by-email` restriction and the 404/403 split. These are live on
  `fix/conflict`; build against them.
- **§7.5 and §11 — the sprint re-scoping — are committed on the branch but the fixes to it are not
  yet.** The route rename itself landed with the sprint work; the authorization layer that makes
  those routes usable is in review. Until it lands, sprint endpoints deny every caller, so a client
  switched to the project routes will see 403s that are **not** a permission problem on your side.
  Coordinate the switch rather than shipping it blind.
- No migration has been applied to any shared environment yet.

---

## 1. Team membership now grants project access

### Before

Access to a project required an explicit project-scope role assigned through
`POST /api/projects/{projectId}/roles`, or being an OrgAdmin. Being on the team the project was
assigned to granted **nothing** — a developer added to the team still got denied on the project's
board and work items until someone separately granted them a project role.

### Now

Membership of `Project.AssignedTeamId` confers **Contributor** on that project:

| Action | Team member? |
| --- | --- |
| Read the project, its board, its work items | yes |
| Create, edit, transition and comment on work items | yes |
| Reorder items within the team's sprints | yes |
| Rename or archive the project | **no** — needs a project admin |
| Delete a work item | **no** |
| Manage project roles | **no** |

### What to change

- **Onboarding can drop its second step.** Adding someone to a team is now enough to give them
  working access to that team's projects. Granting a project role is only needed to give them *more*
  than Contributor.
- **Empty states fire less often.** "You don't have access to any projects" is now unreachable for
  anyone who is on a team.
- **`GET /api/projects/{id}/roles` no longer describes everyone with access.** It returns *direct*
  project-scope grants only — it always did, but that used to be the whole picture. A team member
  with access will not appear. If you render it as "who can see this project", pair it with
  `GET /api/teams/{teamId}/members`.

---

## 2. The last OrgAdmin cannot be removed

`DELETE /api/orgs/{orgId}/members/{userId}` previously succeeded unconditionally. It now returns
**400** when the target is the only remaining `OrgAdmin`:

```json
{ "success": false, "message": "Cannot remove the last OrgAdmin of an organization.", "data": null, "errors": null }
```

Same guard `PUT /api/orgs/{orgId}/members/{userId}/role` already had for demotion, so you can route
both through one handler.

Disable the remove action for the only admin in the list *and* still handle the 400 — two admins
removing each other concurrently will race, and the guard runs inside the transaction precisely so
one of them loses.

---

## 3. Removing someone from an organization now cascades

`DELETE /api/orgs/{orgId}/members/{userId}` now also deletes, in the same transaction:

- their membership of **every team** in that organization
- their role assignments at **every team and project** in it

Previously only org-scope roles were revoked, leaving a removed member still holding `ProjectAdmin`
on projects in an organization that had ejected them — and still receiving that project's realtime
feed.

**What to change:** invalidate cached team-membership and project-role state for that user after the
call. And note removal is **not reversible by re-adding** — memberships are deleted, not
soft-marked, so re-adding someone does not restore their teams. If your UI implies otherwise, reword
it.

---

## 4. Reassigning a project's team moves access with it

`PUT /api/projects/{projectId}/team` (body `{ "assignedTeamId": "..." }`) now has a permission
consequence: everyone on the **previous** team loses access to the project and everyone on the
**new** team gains it, without any role assignment changing.

Live clients on the previous team receive `SubscriptionRevoked` for `project:{projectId}` — same
message and handling as in `realtime-frontend.md`. Clients on the new team are not pushed anything;
they pick it up on their next fetch.

Nothing new to build if you already handle `SubscriptionRevoked`. If the reassignment is triggered
from your own UI, refetch project lists for both teams rather than assuming only the project record
changed.

---

## 5. ⚠️ Denials: 404 when you cannot see it, 403 when you can

**This is the change most likely to break assumptions, because it affects every endpoint.**

Authorization now runs *before* the handler, and what it returns depends on whether you can see the
thing at all:

| Situation | Status |
| --- | --- |
| The resource does not exist | **404** |
| You exist but cannot read the scope | **404** — deliberately identical to the above |
| You can read the scope but lack this specific permission | **403** |

### Why

Previously an outsider got `403` for a real id and `404` for a fake one, which let anyone with an
account probe which project, team and work item ids exist. Answering `404` to people who cannot see
the resource closes that.

The split matters: answering `404` to *everyone* would be wrong. Someone who can see a project but
lacks `project:admin` knows perfectly well it exists, and "not found" on their rename attempt would
be unexplainable.

### What to change

- **Do not treat 404 as "deleted".** On a project or board route it now also means "you no longer
  have access" — for instance right after being removed from a team. Prefer wording like "This is no
  longer available" over "This was deleted", and route the user back to a list rather than showing a
  broken-link state.
- **403 still means what it always did**, and is now reliably actionable: the user can see the thing
  and needs a higher permission. That is the case worth explaining in the UI ("You need to be a
  project admin to rename this").
- Both bodies keep the standard shape: `{ "success": false, "message": "…" }` — `"Resource not
  found"` or `"Access forbidden"`.

---

## 6. Team positions — Team Lead, Scrum Master, Product Owner

Three named positions per team, **one holder each**, transferable.

### Endpoints

```
GET    /api/teams/{teamId}/positions              list all three and their holders
PUT    /api/teams/{teamId}/positions/{position}   appoint or transfer   { "userId": "..." }
DELETE /api/teams/{teamId}/positions/{position}   vacate
```

`{position}` is `TeamLead`, `ScrumMaster` or `ProductOwner`. The list always returns all three, with
`userId: null` for a vacancy — a legitimate state, so render it as "unassigned" rather than hiding
the row:

```json
{ "success": true, "message": "Positions retrieved.", "data": [
  { "position": "TeamLead",     "userId": "…"  },
  { "position": "ScrumMaster",  "userId": null },
  { "position": "ProductOwner", "userId": "…"  } ] }
```

`PUT` is appointment *and* transfer in one call — there is no revoke step, and no moment where the
position is half-transferred. It returns **400** if the target is not a team member ("User must be a
member of the team before holding one of its positions") or if `{position}` is not one of the three.

**Who may change them:** anyone with `team:role:assign` — a Team Lead or an org admin — **or the
current holder**, so a planned handover needs no admin involvement. Reading the list needs only team
read.

### What the positions actually change

> **One row below is superseded by §7.5.** When sprints moved from teams to projects, **Team Lead
> lost sprint authority** — it belongs to the Scrum Master and Product Owner, who keep it across
> every project their team serves. The rest of this table still holds. §11.1 is the current,
> authoritative version; where the two disagree, §11.1 wins.

| Action | Before | Now |
| --- | --- | --- |
| Create / update / start / complete / delete a sprint | OrgAdmin only, in practice | Scrum Master, Product Owner, OrgAdmin — ~~Team Lead~~ (§7.5), plus `ProjectAdmin` |
| Add / remove team members; rename or archive the team | OrgAdmin only, in practice | Team Lead, OrgAdmin |
| Add / remove sprint backlog items | any team member | Scrum Master, Product Owner, OrgAdmin, `ProjectAdmin` — **plus the exception below** |
| Move / reorder within a sprint | any team member | unchanged |
| Create work items | any team member | unchanged |

"OrgAdmin only, in practice" is not a typo: those endpoints asked for a role that nothing ever
assigned, so only the org-admin fallback satisfied them. Sprint management genuinely was unreachable
for anyone else.

### The exception that affects the board UI

Anyone with `sprint:order` — a plain team member, or a project contributor — **can** add a work item
to the sprint when **its parent is already in that sprint**. Breaking down committed work is not a
scope change; committing new work is.

So "may I drag this into the sprint?" is not answerable from the user's role alone — it depends on
the item's parent. Treat the 403 as the answer and surface its message, which explains the rule:

> Changing what a sprint commits to requires the Product Owner, Scrum Master or Team Lead. Breaking
> down work already in the sprint does not.

Creating work items is never gated. If your UI hides "new task" for non-privileged users, that was
never required and still isn't.

---

## 7. Breaking changes

### 7.1 Organization roles narrowed to `OrgAdmin` and `Reader` (see §7.4 — `Reader` is now `Member`)

`PUT /api/orgs/{orgId}/members/{userId}/role` previously accepted `ProjectAdmin` and `TeamMember`.
It now returns **400** for them.

Those two granted nothing beyond organization read — only `OrgAdmin` ever inherited downwards — so
they were a trap: setting someone to "ProjectAdmin" at organization level gave them no authority
over any project. Project authority is a project-scope grant; team authority is now a position.

**What to change:** remove both from the org-role dropdown. Existing rows were rewritten to `Reader`
by the migration — exactly the access they already had, so nobody gained or lost anything, but a
member who displayed as "ProjectAdmin" will now display as "Reader".

### 7.2 `GET /api/users/by-email` requires member-management rights

It previously answered for any authenticated caller. It now returns **403** unless the caller can
manage organization members somewhere — in practice an OrgAdmin of at least one organization.

Left open it was a cross-tenant directory: anyone with an account could confirm whether a given
address belonged to a user here and read their name.

**What to change:** the invite flow is unaffected, since inviting already requires OrgAdmin. But if
you call this lookup from a **mention picker, assignee search, or "share with" box**, those calls
will now 403 for ordinary members. Use the scoped member listings instead —
`GET /api/orgs/{orgId}/members` or `GET /api/teams/{teamId}/members` — which are available to anyone
who can read the org or team.

The denial is 403 for a real and a non-existent address alike, so infer nothing from it.

`GET /api/users/{userId}` is **unchanged** and still open to any authenticated caller; ids appear
throughout the API as assignees, authors and members, so name rendering keeps working.

### 7.3 Unknown work item types are now rejected

`POST /api/projects/{projectId}/workitems` silently created an **Epic** when `type` was not
recognised — the parse result was discarded. It now returns **422**:

```json
{ "message": "'Story' is not a valid work item type. Valid types: Epic, Feature, UserStory, Task, Bug.", "statusCode": 422 }
```

**Check your type strings.** The enum value is `UserStory`, not `Story` — if you have been sending
`"Story"`, it was being stored as an Epic and will now fail outright.

---

### 7.4 ⚠️ Role names are now specific to their scope

`RoleType` values changed. A role name now tells you which scope the grant is on, which was not true
before: `Reader` meant "organization member" at org scope and "read-only" at the other two, and
`TeamMember` at project scope named a team relationship that a project grant does not have.

| Scope | Was | Now |
| --- | --- | --- |
| Organization | `Reader` | **`Member`** |
| Team | `Reader` | **`Viewer`** |
| Project | `Reader` | **`Viewer`** |
| Project | `TeamMember` | **`Contributor`** |
| Team | `TeamMember` | `TeamMember` — unchanged, the name is accurate here |
| — | `User` | **deleted.** Assigned by no code path, granted nothing. |

`OrgAdmin`, `ProjectAdmin`, `TeamLead`, `ScrumMaster` and `ProductOwner` are unchanged.

The full vocabulary, which is now the complete list of what each endpoint accepts:

| Scope | Roles |
| --- | --- |
| Organization | `OrgAdmin`, `Member` |
| Team | `TeamLead`, `ScrumMaster`, `ProductOwner`, `TeamMember`, `Viewer` |
| Project | `ProjectAdmin`, `Contributor`, `Viewer` |

**What to change.** Roles are serialized as strings, so this touches both directions:

- **Sending.** `PUT /api/orgs/{orgId}/members/{userId}/role` takes `OrgAdmin` or `Member`.
  `POST /api/projects/{projectId}/roles` takes `ProjectAdmin`, `Contributor` or `Viewer`. Anything
  else is a 400 whose message lists the roles that scope accepts.
- **Displaying.** Any label map keyed on `Reader`, project-scope `TeamMember`, or `User` needs
  updating. A member who displayed as "Reader" now arrives as "Member" at org scope and "Viewer" on
  a team or project.
- **Comparing.** If anything still orders roles or compares them numerically, stop — the numeric
  values were meaningless before this change and are still meaningless. Ask about the specific
  capability instead.

Existing rows were migrated in place, so nobody's access changed: an org `Reader` is a `Member` with
exactly the permissions they had, and a project `TeamMember` is a `Contributor` with exactly the
permissions they had. This is a renaming, not a regrant.

---

### 7.5 ⚠️ Sprints are now scoped to projects, not teams

A sprint belongs to a project. Three things follow.

**Who may do what changed.** Sprint permissions are now project permissions:

| Action | Was | Now |
| --- | --- | --- |
| View sprints, reorder the sprint backlog | any team member | anyone contributing to the project — which includes the team, via team → project |
| Create, update, start, complete, delete a sprint | Team Lead, Scrum Master, Product Owner | Scrum Master or Product Owner of the project's team, `ProjectAdmin` on the project, or OrgAdmin |
| Decide what the sprint commits to | Team Lead, Scrum Master, Product Owner | Scrum Master or Product Owner of the project's team, `ProjectAdmin` on the project, or OrgAdmin |

A Scrum Master and a Product Owner keep sprint authority over every project their team serves, so a
UI gating sprint controls on those positions stays correct. **§11.1 has the full matrix**, and §11
the route table. Two things did change: a **Team Lead** no
longer runs sprints unless they also hold `ProjectAdmin`, and a **project administrator who is on no
team** now can. Sprint authority stops at the sprint — neither position gains any power to rename the
project, configure its board, delete work items or grant roles on it.

**Realtime moved.** Sprint events were published to the sprint's **team** topic and are now published
to its **project** topic (`Topic.Sprint` is unchanged). If you subscribed to a team topic to catch
sprint changes, subscribe to the project topic instead. Sprint activity-feed rows are likewise filed
under the project now rather than the team, matching how board rows always were.

**Sprint payloads carry a project.** Sprint domain events exposed `teamId` and now expose
`projectId`. `GET /api/projects/{projectId}/sprints` and `…/sprints/active` are unchanged in shape.

---

## 8. Bug fixes you may have coded around

**Cross-organization work items could be pulled into a sprint.** `POST /api/sprints/{id}/workitems`
authorized the sprint but never the work item, so any user could add *any* work item id in the
system to their own sprint and read its title, assignee and points off their own board. It now
returns **404** for anything outside the sprint's team. Sibling projects of the same team are still
allowed — that is the normal case.

**A project's board showed its sibling projects' cards.** A sprint is team-scoped and a team can
hold several projects, so a sprint legitimately contains items belonging to other boards. The board
query now filters to its own project. If you were de-duplicating or filtering cards client-side to
work around this, you can stop.

---

## 9. What did *not* change

- ~~No routes added beyond §6, none removed or renamed.~~ **No longer true.** Three sprint routes
  were renamed when sprints moved from teams to projects — see §7.5 and the table in §11. Verified
  by diffing every controller route across the change: those three are the *only* renames, and
  nothing anywhere was removed.
- ~~No existing request or response body changed shape.~~ One did: `SprintResponse.teamId` is now
  `projectId` (§11). Every other request and response is unchanged.
- Sprint work-item, board, backlog and org/team/project routes are all untouched.
- Organization membership (`Member`, formerly `Reader`) still grants nothing inside the
  organization. An org member on no team holding no project role still sees no projects.
- OrgAdmin still implicitly satisfies every check inside their organization.
- Realtime message shapes, `SubscriptionRevoked` handling and replay semantics are unchanged.

---

## 10. Still missing: a "what may I do here" endpoint

Capability is still not queryable. Keep deriving it from the role and membership data you already
fetch, and treat 403/404 as authoritative.

This is now much easier to add than it was — permissions are named server-side, so an endpoint
returning the caller's permission set for a given scope is a small piece of work. If the UI is doing
much guessing about what to show, ask for it; the guessing will get worse as positions and the
sprint-scope rule land.

---

## 11. Sprint route reference

The three renamed routes, which is the part that breaks a running client:

| Was (now **404**) | Is |
| --- | --- |
| `GET /api/teams/{teamId}/sprints` | `GET /api/projects/{projectId}/sprints` |
| `GET /api/teams/{teamId}/sprints/active` | `GET /api/projects/{projectId}/sprints/active` |
| `POST /api/teams/{teamId}/sprints` | `POST /api/projects/{projectId}/sprints` |

**No team-scoped sprint route answers any more.** They were not kept as aliases. Anything still
calling `/api/teams/{teamId}/sprints…` gets a 404 from routing — not from the permission layer, so
the 404/403 rules in §5.1 do not apply and no amount of permission will make it succeed.

Every other sprint route is unchanged. The full surface, with the permission each one requires:

| Method | Route | Requires |
| --- | --- | --- |
| GET | `/api/projects/{projectId}/sprints` | `sprint:read` |
| GET | `/api/projects/{projectId}/sprints/active` | `sprint:read` |
| POST | `/api/projects/{projectId}/sprints` | `sprint:manage` |
| GET | `/api/sprints/{sprintId}` | `sprint:read` |
| PUT | `/api/sprints/{sprintId}` | `sprint:manage` |
| PATCH | `/api/sprints/{sprintId}/status` | `sprint:manage` |
| DELETE | `/api/sprints/{sprintId}` | `sprint:manage` |
| POST | `/api/sprints/{sprintId}/close` | `sprint:manage` |
| GET | `/api/sprints/{sprintId}/workitems` | `sprint:read` |
| POST | `/api/sprints/{sprintId}/workitems` | `sprint:scope`, or `sprint:order` if the item's parent is already in the sprint |
| DELETE | `/api/sprints/{sprintId}/workitems/{workItemId}` | same as above |
| PATCH | `/api/sprints/{sprintId}/workitems/{workItemId}/move` | `sprint:order` |
| PATCH | `/api/sprints/{sprintId}/workitems/reorder` | `sprint:order` |

Related, and unchanged: `POST /api/projects/{projectId}/backlog/move-to-sprint` and
`…/return-from-sprint` both require `workitem:write` on the route's project *and* `sprint:scope` on
the target sprint's project — the two need not be the same project. `GET /api/projects/{projectId}/board`
returns the project's active sprint cards and requires `board:read`.

### 11.1 Who holds each sprint permission

This is the table to drive UI gating from. "Team" means a role held on the team the project is
assigned to, which reaches the project through the team → project edge (§1).

| | `sprint:read` | `sprint:order` | `sprint:manage` / `sprint:scope` |
| --- | :---: | :---: | :---: |
| **Project** `Viewer` | ✅ | — | — |
| **Project** `Contributor` | ✅ | ✅ | — |
| **Project** `ProjectAdmin` | ✅ | ✅ | ✅ |
| **Team** `Viewer` | ✅ | — | — |
| **Team** `TeamMember` | ✅ | ✅ | — |
| **Team** `TeamLead` | ✅ | ✅ | — |
| **Team** `ScrumMaster` | ✅ | ✅ | ✅ |
| **Team** `ProductOwner` | ✅ | ✅ | ✅ |
| `OrgAdmin` | ✅ | ✅ | ✅ |

Two things worth reading off it. A **Scrum Master or Product Owner runs sprints on every project
their team serves** — that is the appointment, and it needs no per-project grant. A **Team Lead does
not**: they lead the people, and running the sprint is the other two positions' job, so a Team Lead
who also needs it holds `ProjectAdmin` on the project. Sprint authority stops at the sprint in every
case: none of these positions can rename the project, configure its board, delete work items or grant
roles on it.

### 11.2 Response shape

`SprintResponse.teamId` is now **`projectId`**. That is the one response-body change in this whole
note, and it affects `sprintApi.types.ts`. `SprintSummaryResponse` — what the list endpoint returns —
never carried either field and is unchanged:

```
SprintResponse         { id, projectId, number, goal, startDate, endDate, status,
                         workItemCount, completedCount, totalStoryPoints,
                         completedStoryPoints, createdAt }

SprintSummaryResponse  { id, number, goal, startDate, endDate, status, workItemCount }
```

Sprint **domain events** likewise carry `projectId` where they carried `teamId`, and are published to
the project topic rather than the team topic (§7.5).

---
---

# Update — 2026-08-23

Everything above describes the August permission rebuild. What follows is the wave after it.

---

## 12. `GET /api/metadata` — stop hardcoding

**This is the one that removes work.** Eight vocabularies only the server knew are now served, so
the arrays you are maintaining by hand can go.

Enums cross the wire as bare strings, which is why this was needed: the server knows `Critical`
sorts above `Low` because the enum is numbered, and `"Critical"` carries none of that. So every
client ended up with its own copy of the ordering, the valid-roles-per-scope map, the legal
transitions, and five other lists — copies with no test behind them and no migration to update them.

```
GET /api/metadata          (authenticated · ETag · Cache-Control: private, max-age=300)
```

Fetch it once at boot. Send `version` back as `If-None-Match` to revalidate; you get a `304` when
nothing changed. `version` is a hash of the content, so it changes exactly when the vocabulary does
— use it as your cache key.

Every entry carries the same three fields:

| Field | Use it for |
| --- | --- |
| `value` | What you send and receive on the wire |
| `label` | What you show a person |
| `order` | Sort ascending. **This is the field that was impossible to derive.** |

### What's in it

```jsonc
{
  "version": "11a4a15524238ccf",

  "roles": [
    { "value": "ScrumMaster", "label": "Scrum Master", "order": 40, "scope": "Team",
      "isPosition": true,
      "permissions": ["team:read"],
      "inheritedProjectPermissions": ["sprint:manage", "sprint:scope", "workitem:write", "…"],
      "description": "Runs the sprint lifecycle on the team's projects." }
  ],

  "permissions":       [ { "value": "workitem:verify", "label": "Certify work as done",
                           "order": 210, "group": "Work items" } ],
  "workItemTypes":     [ { "value": "UserStory", "label": "User Story", "order": 30,
                           "allowedChildren": ["Task", "Bug"] } ],
  "workItemStates":    [ /* see §14 — includes transitionsTo and requiresPermission */ ],
  "priorities":        [ { "value": "Critical", "label": "Critical", "order": 10 } ],
  "sprintStatuses":    [ { "value": "Active", "label": "Active", "order": 20 } ],
  "workItemLinkTypes": [ { "value": "Blocks", "label": "Blocks", "inverse": "Blocked by",
                           "order": 10 } ],
  "teamPositions":     ["TeamLead", "ScrumMaster", "ProductOwner"]
}
```

### Three things to notice

**Roles are one entry per (role, scope) pair, not per role.** `value` is *not* unique across the
list — `Viewer` appears twice, and so does `Tester`, because both are valid at team and project
scope and permit different things at each. Key your lookups on `(value, scope)`. To populate a role
picker, filter by the scope you are granting at.

**`inheritedProjectPermissions` is only on team-scope roles**, and it is the part of the model you
could not work out client-side: it is what a team role additionally permits on every project the
team is assigned to. It is why a Scrum Master can run sprints on a project they hold no project role
on. If you gate sprint controls, this is what to gate on.

**`isPosition`** marks the singular team appointments. Those are assigned through
`PUT /api/teams/{teamId}/positions/{position}` (§6), not the general role endpoints — `Tester` is
deliberately *not* one, because a team can have several testers.

### Link types

`inverse` is what the relationship is called from the other item's side: a link stored as `Blocks`
displays as "Blocks" on the source and "Blocked by" on the target. One row, two wordings, and you
cannot derive the second.

---

## 13. `GET /api/me/capabilities` — the endpoint §10 said was missing

§10 told you to keep deriving capability from role and membership data. **Stop.** Deriving it
correctly now means reimplementing three inheritance routes, the team → project edge with its Scrum
Master / Product Owner exception, and OrgAdmin's reach — and that reimplementation drifts
*permissive*, because a button that 403s gets reported and a button wrongly hidden does not.

```
GET  /api/me/capabilities?scope=project:{guid}
POST /api/me/capabilities          { "scopes": ["project:…", "team:…", "org:…"] }   // max 50
```

Scope references are `org:{guid}`, `team:{guid}` or `project:{guid}` — the same spelling as the
realtime topics, deliberately.

```jsonc
// GET
{ "scope": "project:8f3e…",
  "permissions": ["project:read", "board:read", "workitem:write", "sprint:order"] }

// POST — keyed on exactly the strings you sent
{ "project:8f3e…": ["project:read", "…"], "team:2a11…": ["team:read"] }
```

**An unknown scope and a forbidden one both return an empty list.** That is deliberate: the endpoint
must not become a way to discover that an id names something real, which is the same reason denials
are 404 (§5.1). Do not treat empty as "not found".

The permission strings are the `value`s from `metadata.permissions`, so you can render a
capabilities response with labels without a second lookup.

---

## 14. ⚠️ The QA gate: a new state, a new role, and a new permission

**The product's premise is that the board updates itself from git.** Push, pull request and merge
will drive work forward on their own. That is only trustworthy if the automation stops somewhere,
and the place it stops is `Closed`.

### 14.1 Five states, not four

```
New ──► Active ──► InReview ──► Resolved ──► Closed
          ▲           │            │  │
          └───────────┘            │  │        Resolved → Active  (QA rejects)
                                   │  └──────► Closed   → Active  (reopen)
          └────────────────────────┘
```

| State | `label` | Means |
| --- | --- | --- |
| `New` | New | Created, not started |
| `Active` | Active | Being worked on |
| **`InReview`** | **In Review** | **New.** A pull request is open |
| `Resolved` | **"Awaiting QA"** | ⚠️ **Merged, waiting to be tested** — *not* "done" |
| `Closed` | Closed | Verified and finished |

⚠️ **`Resolved` now displays as "Awaiting QA".** The enum value is unchanged, so nothing you send
breaks — but if you have a hardcoded label map, it is wrong. Read the label from `/api/metadata`.

⚠️ **`Active → Closed` no longer exists.** It was removed because every transition was gated on
`workitem:write`, which every contributor holds, so that edge let whoever did the work also declare
it finished. Attempting it returns **422** with the valid next states listed.

### 14.2 Which transitions need what

`metadata.workItemStates[].transitionsTo` gives you this per state, so **build your "Move to…" menu
from it** rather than from a hardcoded graph:

```jsonc
{ "value": "Resolved", "label": "Awaiting QA", "order": 40, "category": "Review",
  "transitionsTo": [
    { "state": "Closed", "requiresPermission": "workitem:verify" },
    { "state": "Active", "requiresPermission": "workitem:verify" }
  ] }
```

Cross `requiresPermission` against `/api/me/capabilities` for the project and you can disable the
option before anyone clicks it. **Every move out of `Resolved` and out of `Closed` needs
`workitem:verify`**; everything before that needs only `workitem:write`.

Both edges out of `Resolved` are guarded, not just the one into `Closed` — otherwise the author
could pull their own item back to `Active` and quietly take it out of QA's queue with no rejection
recorded.

`category` (`Pending` / `InProgress` / `Review` / `Done`) lets you colour and group lanes without
switching on state names.

### 14.3 A new role: `Tester`

| Scope | Roles now |
| --- | --- |
| Organization | `OrgAdmin`, `Member` |
| Team | `TeamLead`, `ScrumMaster`, `ProductOwner`, `TeamMember`, **`Tester`**, `Viewer` |
| Project | `ProjectAdmin`, `Contributor`, **`Tester`**, `Viewer` |

`Tester` is valid at both team and project scope — the second name after `Viewer` to be so — and it
is **not** a position: a team can have several. A Tester contributes as well as certifies; they are
not a read-only role with one extra power.

**Who can certify (`workitem:verify`):**

| Role | Certifies? |
| --- | --- |
| `Tester` (team or project) | ✅ |
| `TeamLead` | ✅ |
| `ProductOwner` | ✅ — in Scrum the PO accepts the increment |
| `ProjectAdmin`, `OrgAdmin` | ✅ |
| **`ScrumMaster`** | ❌ — runs the sprint, does not sign work off |
| `Contributor`, `TeamMember`, `Viewer` | ❌ — the point of the gate |

Don't hardcode that table either — it is `metadata.roles[].permissions` and
`inheritedProjectPermissions`.

### 14.4 Two different refusals, and they mean different things

| Situation | Status | Why |
| --- | --- | --- |
| Caller lacks `workitem:verify` | **403** `"Access forbidden"` | A permission gap. Generic message by design — refusals never describe what you lack |
| Caller **is the assignee** of the item | **422** with an explanation | ⚠️ **Not a permission problem.** They hold `workitem:verify`; the rule is about whose work it is |
| Transition is not in the graph at all | **422** listing valid next states | Not a permission answer either |

**Do not tell the user to ask for access on a 422.** The self-certification message explains itself
and is safe to surface directly.

Self-certification is off by default and settable per project:

```jsonc
// GET /api/projects/{id}  →  ProjectResponse now includes:
"allowSelfCertification": false

// PUT /api/projects/{id}  →  optional; omitting it leaves the setting unchanged
{ "name": "…", "allowSelfCertification": true }
```

⚠️ **`allowSelfCertification` is nullable on the request on purpose.** A screen that edits only the
project name must not silently switch the QA separation off by not mentioning it — send it only when
the user actually changed it.

Project administrators are exempt from the rule, since they can flip the setting themselves anyway.

### 14.5 Boards gained a column

New projects get **five** default columns:

| Position | Name | `mappedState` |
| --- | --- | --- |
| 0 | To Do | `New` |
| 1 | In Progress | `Active` |
| 2 | **In Review** | **`InReview`** |
| 3 | **Awaiting QA** | `Resolved` |
| 4 | Done | `Closed` |

⚠️ **"In Review" used to map to `Resolved`.** Existing boards were migrated: a new `InReview` column
is inserted before the `Resolved` one, and a column still named "In Review" that maps to `Resolved`
is renamed to "Awaiting QA". A board a user renamed themselves keeps its name.

If you render lanes from the board's columns you need no change. If you hardcoded four lanes, you
now drop cards on the floor.

---

## 15. ⚠️ Work item activity now actually arrives

**Every work item domain event was being dropped before it was written.** Created, Assigned,
StateChanged, Deleted, CommentAdded and Linked — all six were staged on the request's unit of work
*after* it had already been committed, so they were discarded silently. No exception, no log.

Three consequences you may have designed around, all now fixed:

- **The activity feed showed no work item activity at all.** Organization, team, project, sprint and
  board entries appeared; work items never did. If you filtered them out, or built an empty state
  around "work item activity doesn't show up here", remove it — `WorkItem` entries now arrive with
  verbs `Created`, `StateChanged`, `Assigned`, `Commented`, `Deleted`, `Linked`.
- **Boards never updated live.** Real-time subscribers received nothing when a card moved, so any
  polling you added as a workaround can go. The realtime contract is unchanged (`docs/realtime-frontend.md`);
  it simply had nothing to deliver.
- **Cached board reads were never invalidated by a work item change.**

Note on the comment entry: its `entityId` is the **comment's** id so you can deep-link to it, while
its `title` is the work item's. That was already true; it matters more now that the entries arrive.

**Delivery latency also dropped from 0–5s to milliseconds.** The outbox's `NOTIFY` wake-up was
firing and waking nothing, so everything fell back to a 5-second poll. The feed is still eventually
consistent by design — just no longer by seconds.

---

## 16. Search, notifications and the workspace summary changed shape

Three scope-spanning reads were treating **organization membership as access to everything in the
organization** — which the permission model explicitly says it is not (§9). An org `Member` on no
team, holding no project role, could read the title of every work item in the organization through
search while `GET /api/projects/{id}` correctly answered 404 for the same project.

They are now scoped by the permission each result would need to open:

| Endpoint | Scoped by |
| --- | --- |
| `GET /api/search` → organizations, members | `org:read` |
| `GET /api/search` → projects | `project:read` |
| `GET /api/search` → work items | `workitem:read` |
| `GET /api/notifications` | `workitem:read` |
| `GET /api/workspace/summary` | per counter — `org:read`, `project:read`, `workitem:read` |
| `GET /api/workspace/activity` | `org:read` |

**What to expect.** Results shrink for users who were seeing things they could not open. Nothing
shrinks for anyone who genuinely had access. A hit in search can now always be opened — which it
could not before, so if you built error handling for "search result 404s when clicked", it is dead
code.

⚠️ **The workspace summary's `projects` and `activeWorkItems` counters will drop** for org members
with no team or project grant — from "everything in the org" to zero. That is the correct number.
`organizations` is unchanged.

**The notification bell returns entries for the first time.** It was filtering on a column nothing
ever wrote, so it returned an empty list to *everybody*, including users who could see everything.
If you hid the bell because it was always empty, unhide it. Shape is unchanged (§4 of
`docs/repository-refactor-context.md`).

---

## 17. `PATCH /api/workitems/{id}`, and `expectedVersion` now works

Both items §17 previously listed as missing have shipped.

### 17.1 Partial updates

```
PATCH /api/workitems/{workItemId}     (requires workitem:write)
```

Only the fields you send are changed. `PUT` still exists and is still a full replace — use it for a
full-form save, and use `PATCH` for everything else.

```jsonc
{ "title": "Renamed" }                       // everything else untouched
{ "assigneeId": null }                       // unassign
{ "tags": [] }                               // clear the tags
{ "expectedVersion": 42, "priority": "High" }
```

⚠️ **Omitting a field and sending it as `null` are different.** Omitted means "leave it alone";
explicit `null` clears it. That distinction is why `PATCH` exists — under `PUT` you cannot unassign
an item without also resending five fields you may have loaded before someone else changed them.

Fields: `title`, `description`, `priority`, `assigneeId`, `teamId`, `storyPoints`, `tags`. Sending
`{}` is valid and does nothing.

**`state` is not settable here.** It moves through `PATCH /api/workitems/{id}/state`, which enforces
the workflow and the QA gate (§14). Including `state` in a `PATCH` body is ignored, not rejected —
the item does not move.

Reassignment still requires the assignee to be a member of the owning team, and a blank `title` is
refused — both **422** with an explanation.

### 17.2 `expectedVersion` is now honoured

⚠️ **This changes behaviour for anyone already sending it.** It was accepted and ignored; it now
does what the field always claimed.

Read `version` from any work item response and send it back on `PUT`, `PATCH`, or
`PATCH .../state`:

```jsonc
{ "expectedVersion": 42, "title": "…" }   →  409 if someone else wrote first
```

**On 409:** re-read the item and re-apply your change to the current version. The error carries no
state deliberately — you need the whole item to merge against, and a `GET` returns it in the shape
you already parse.

**Omitting it keeps last-write-wins**, so nothing breaks if you are not ready. But start sending it:
the git integration will be writing to these items concurrently with your users, so lost updates
stop being a rare race and become a routine event.

> ⚠️ **`version` used to always come back as `0`.** The field was never populated on the response,
> so if you cached or asserted on it, that value was meaningless. It now carries the real row
> version. Treat it as opaque — compare only, never compute with it or assume it increments by one.

---

## 18. Git integration — nothing for you yet, but here is the shape

Webhook ingest has landed: `POST /api/git/{provider}/webhook/{endpointToken}` accepts deliveries
from GitHub, verifies them, and queues them. **It changes nothing you can see.** Deliveries are
normalized and recorded; binding a commit to a work item is the next increment.

Two things to know so you can plan around them:

**A repository connection is not yet self-service.** Installation and repository-link rows are
created directly in the database today, so there are no settings screens to build against. Those
endpoints come with the binding work.

**When binding lands, work items will start moving on their own.** A developer branching
`bs-142-fix-login`, committing, opening a pull request and merging will walk the item
`New → Active → InReview → Resolved` with nobody touching the board. Consequences for the client:

- **State changes will arrive over the realtime channel from no user action of yours.** The contract
  is unchanged (`docs/realtime-frontend.md`), but a board that only re-renders on local interaction
  will look stale. Handle inbound `WorkItemStateChanged` for items nobody on this client touched.
- **Activity entries will be attributed to the integration**, not to a person — with the commit
  author carried alongside as attribution. Rendering an actor name will need to handle "GitHub (Ada
  Lovelace)" as well as a plain user.
- **`expectedVersion` stops being optional in practice.** A webhook worker writing while your user
  edits is routine, not a rare race. §17.2 is the reason to start sending it now.

Nothing in this list requires work today. It is here so none of it is a surprise.

---

## 19. Still missing

Nothing outstanding from the frontend contract's point of view.
