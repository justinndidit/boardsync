# Auth & Permission Changes — What They Mean for the Frontend

Status: §1–§4 shipped and pushed (`edf1133`); §5–§8 implemented, **not yet committed** ·
Scope: `server/BoardSync.Api` · Companion to `docs/permissions-model.md`

---

## TL;DR

**No route was renamed and no existing response body changed shape.** But the permission model was
rebuilt underneath, and five things need frontend work:

| | Change | Impact |
| --- | --- | --- |
| 1 | **Denials are now 404 when you cannot see the resource**, 403 when you can | ⚠️ **§5.1 — affects every error path.** Read this first. |
| 2 | **Team members automatically get access to their team's projects** | §1 — screens gate on less than they used to. |
| 3 | **New team positions**: Team Lead, Scrum Master, Product Owner | §6 — new endpoints, new UI. |
| 4 | **Every role renamed to its scope**: `Reader` splits into `Member` / `Viewer`, project `TeamMember` becomes `Contributor` | ⚠️ **§7.4 — every role string you send or display changes.** |
| 5 | **`GET /api/users/by-email` now needs member-management rights** | ⚠️ §7.2 — breaks mention/assignee pickers if used there. |

Smaller behaviour changes worth knowing: §2 (last OrgAdmin), §3 (cascade), §4 (reassignment),
§7.3 (work item type validation), §8 (bug fixes you may have coded around).

**On deployment state:** everything in §1–§4 is on `origin/fix/conflict` now. Everything from §5
onward — the permission vocabulary, team positions, the endpoint filter and the `by-email`
restriction — is implemented and tested locally but **not yet pushed**, so do not build against
§5–§8 until that lands. The 404 change in §5.1 in particular is not live yet.

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

| Action | Before | Now |
| --- | --- | --- |
| Create / update / start / complete / delete a sprint | OrgAdmin only, in practice | Scrum Master, Product Owner, Team Lead, OrgAdmin |
| Add / remove team members; rename or archive the team | OrgAdmin only, in practice | Team Lead, OrgAdmin |
| Add / remove sprint backlog items | any team member | Scrum Master, Product Owner, Team Lead, OrgAdmin — **plus the exception below** |
| Move / reorder within a sprint | any team member | unchanged |
| Create work items | any team member | unchanged |

"OrgAdmin only, in practice" is not a typo: those endpoints asked for a role that nothing ever
assigned, so only the org-admin fallback satisfied them. Sprint management genuinely was unreachable
for anyone else.

### The exception that affects the board UI

A plain team member **can** add a work item to the sprint when **its parent is already in that
sprint**. Breaking down committed work is not a scope change; committing new work is.

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

- No routes added beyond §6, none removed or renamed.
- No existing request or response body changed shape.
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
