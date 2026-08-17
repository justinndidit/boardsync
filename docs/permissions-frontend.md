# Permission Changes — What They Mean for the Frontend

Status: shipped on `fix/conflict` · Scope: `server/BoardSync.Api` ·
Companion to `docs/permissions-model.md` (§9 records what shipped and how it was verified)

---

## TL;DR

**No route, request shape or response shape changed.** Three behaviour changes, one of which needs
new error handling:

| | What | Action |
| --- | --- | --- |
| 1 | **Team members can now see and edit their team's projects** | ⚠️ **Read §1.** Screens that assumed a project role was required will now show more. |
| 2 | **Removing the last OrgAdmin now fails with 400** | ⚠️ **Read §2.** New error to surface. |
| 3 | Removing someone from an org now also removes them from every team in it | Refetch team membership after the call. |
| 4 | Reassigning a project's team moves access with it | Handle `SubscriptionRevoked` (already specified in `realtime-frontend.md`). |

Items 1 and 2 are the ones that can bite. Everything else is either additive or already handled.

---

## 1. Team membership now grants project access

### Before

Access to a project required an explicit project-scope role — `ProjectAdmin`, `TeamMember` or
`Reader` assigned through `POST /api/projects/{projectId}/roles` — or being an OrgAdmin. Being on
the team that the project was assigned to granted **nothing**. A developer added to the team still
got `403` on the project's board and work items until someone separately granted them a project
role.

### Now

Membership of `Project.AssignedTeamId` confers **Contributor** on that project. In terms of what the
API allows, that is the same level the old `TeamMember` project role gave:

| Action | Team member? |
| --- | --- |
| Read the project, its board, its work items | yes |
| Create, edit, transition and comment on work items | yes |
| Add/remove/reorder items in the team's sprints | yes |
| Rename or archive the project | **no** — still `ProjectAdmin` or OrgAdmin |
| Delete a work item | **no** — still `ProjectAdmin` or OrgAdmin |
| Manage project roles | **no** — still `ProjectAdmin` or OrgAdmin |

### What to change

- **Onboarding flows can drop the second step.** Adding someone to a team is now sufficient to give
  them working access to that team's projects. If your UI walks an admin through "add to team, then
  grant a project role", the second step is now optional and only needed to grant *more* than
  Contributor.
- **Empty states will fire less often.** Anywhere you render "you don't have access to any projects"
  for a user who is on a team, that branch is now unreachable.
- **Project role lists no longer describe everyone with access.** `GET /api/projects/{id}/roles`
  returns *direct* project-scope grants only — it always did, but that used to be the whole picture.
  A team member with access will not appear in it. If you render that list as "who can see this
  project", it now under-reports; pair it with `GET /api/teams/{teamId}/members`.

There is **no new endpoint** for "what can I do here" — that arrives with the permission vocabulary
in Stage 2 (see `permissions-model.md` §4.3). Until then, keep deriving capability from the role you
already fetch, and treat `403` as authoritative.

---

## 2. The last OrgAdmin cannot be removed

`DELETE /api/orgs/{orgId}/members/{userId}` previously succeeded unconditionally. It now returns
**400** when the target is the only remaining `OrgAdmin`:

```json
{
  "success": false,
  "message": "Cannot remove the last OrgAdmin of an organization.",
  "data": null,
  "errors": null
}
```

This matches the guard `PUT /api/orgs/{orgId}/members/{userId}/role` already had for demotion, so if
you already handle that 400 you can route this through the same path.

**Suggested handling:** disable the remove action in the members list when the row is the only
`OrgAdmin`, and still handle the 400 — two admins removing each other concurrently will race, and
the guard runs inside the transaction precisely so one of them loses.

---

## 3. Organization removal now cascades

`DELETE /api/orgs/{orgId}/members/{userId}` now also deletes, in the same transaction:

- the user's membership of **every team** in that organization
- their role assignments at **every team and project** in that organization

Previously only organization-scope roles were revoked, which left the removed member still holding
`ProjectAdmin` on projects in an org they had been ejected from — and still receiving that project's
realtime feed.

**What to change:** after removing an org member, invalidate any cached team-membership or
project-role state for that user. If your members screen shows "member of 3 teams", it is stale.

Removal is **not reversible by re-adding** — memberships are deleted, not soft-marked, so re-adding
someone to the organization does not restore their teams. If your UI implies otherwise ("remove"
next to a "restore"), reword it.

---

## 4. Reassigning a project's team moves access

`PUT /api/projects/{projectId}/team` (body `{ "assignedTeamId": "..." }`) now has a permission
consequence: everyone on the **previous** team loses access to the project, and everyone on the
**new** team gains it, without any role assignment changing.

Live clients on the previous team receive `SubscriptionRevoked` for `project:{projectId}` — the same
message and handling already described in `realtime-frontend.md`. Clients on the new team are not
pushed anything; they pick the project up on their next fetch.

**What to change:** nothing new if you already handle `SubscriptionRevoked`. If the reassignment is
triggered from your own UI, refetch the project list for the affected teams rather than assuming the
change only affected the project record.

---

## 5. What did *not* change

- No routes added, removed or renamed.
- No request or response bodies changed.
- `403` still means forbidden and `404` still means not found, with the same bodies. (The plan to
  return `404` for both — so the API stops confirming which ids exist — is Stage 3, and will be
  called out separately when it lands.)
- Organization-level `Reader` still grants nothing inside the organization. An org member who is on
  no team and holds no project role still sees no projects; that was true before and remains true.
- OrgAdmin still implicitly satisfies every check inside their organization.

---

## 6. Coming next, so you can plan

Stage 2 introduces named team positions — **Team Lead, Scrum Master, Product Owner** — that are
appointed per team and transferable, plus a permission vocabulary that replaces the current role
ladder. That one *will* change the API surface: new endpoints for appointing and transferring
positions, and most likely a "what may I do here" endpoint so the UI stops inferring capability from
role names. Design is in `permissions-model.md` §4.2–§4.3; nothing to build against yet.
