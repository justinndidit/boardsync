# Frontend guide: the activity feed rewrite

**Date:** 2026-08-07
**Branch:** `fix/conflict`
**Audience:** whoever builds the workspace dashboard and the organization activity page

The two activity endpoints changed shape. This document is what you need to consume them.
There is one breaking change, one new error path, and one thing that deliberately did *not*
change and will look inconsistent until we get to it.

---

## 1. TL;DR

| | Before | Now |
|---|---|---|
| What the feed contained | work item **field changes** only | everything: work items, projects, teams, sprints, boards, membership, roles |
| Response body | bare array, capped at 30 / 50 | `PagedResult<ActivityResponse>` with `?page` / `?pageSize` |
| `type` field | inconsistent between the two endpoints | one closed vocabulary, identical on both |
| Entry shape | 8 fields | 16 fields, including ids for deep-linking |

Both endpoints now read the same table and return **identically shaped entries**. The only
difference between them is scope:

- `GET /api/orgs/{orgId}/activity` — one organization
- `GET /api/workspace/activity` — every organization the caller belongs to

You can render both with a single component. That was the point of the change.

---

## 2. The breaking change

### Before

```jsonc
// GET /api/workspace/activity
{
  "success": true,
  "message": "Activity retrieved.",
  "data": [                                  // <- bare array
    {
      "id": "…",
      "type": "WorkItemActive",              // <- and "State" on the org endpoint
      "title": "Migrate auth",
      "detail": "State: New → Active",
      "actorName": "ada",
      "organization": "Northwind",           // <- always "" on the org endpoint
      "project": "Apollo",
      "occurredAt": "2026-08-07T12:39:23Z"
    }
  ]
}
```

### Now

```jsonc
// GET /api/orgs/{orgId}/activity?page=1&pageSize=20
{
  "success": true,
  "message": "Activity retrieved.",
  "data": {                                  // <- object, not array
    "items": [
      {
        "id": "7e54f60b-aa61-4ae2-8082-1563a33aca97",
        "type": "WorkItem.Assigned",
        "entityType": "WorkItem",
        "verb": "Assigned",
        "entityId": "c7a533f4-73fc-411e-80cd-33ce448f564c",
        "title": "Migrate auth to OIDC",
        "detail": "Assignee: bob → cy",
        "actorId": "703795ea-8c04-454e-97ac-03b075747425",
        "actorName": "ada",
        "organizationId": "91b06cd3-2708-4e28-ad1a-7efa06cae619",
        "organization": "Northwind Traders",
        "projectId": "11594a84-b781-4e1c-9df7-81fa1263f9fb",
        "project": "Apollo II",
        "teamId": null,
        "team": null,
        "occurredAt": "2026-08-07T12:39:23.140613Z"
      }
    ],
    "totalCount": 26,
    "page": 1,
    "pageSize": 20,
    "totalPages": 2,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

`data` moved from `T[]` to `PagedResult<T>`. If you were doing `res.data.map(...)`, it is now
`res.data.items.map(...)`.

The old `WorkspaceActivityResponse` type is deleted. Nothing returns it any more.

### Types

```ts
type ActivityEntityType =
  | 'Organization' | 'Project' | 'Team' | 'WorkItem' | 'Comment' | 'Sprint' | 'Board';

type ActivityVerb =
  | 'Created' | 'Updated' | 'Deleted' | 'Archived' | 'StateChanged' | 'Assigned'
  | 'MemberAdded' | 'MemberRemoved' | 'RoleChanged' | 'Commented' | 'Linked';

interface ActivityResponse {
  id: string;
  type: string;                    // `${entityType}.${verb}` — see §4
  entityType: ActivityEntityType;
  verb: ActivityVerb;
  entityId: string;                // the subject — use for deep links
  title: string;                   // subject's name AT THE TIME (see §5)
  detail: string | null;           // pre-rendered, ready to display
  actorId: string;
  actorName: string;               // "Unknown" if the user row is gone
  organizationId: string;
  organization: string;
  projectId: string | null;
  project: string | null;
  teamId: string | null;
  team: string | null;
  occurredAt: string;              // ISO-8601 UTC
}

interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}
```

Both endpoints return `ApiResponse<PagedResult<ActivityResponse>>`, i.e. the usual
`{ success, message, data, errors }` envelope.

---

## 3. Pagination

`?page=` (1-based, default `1`) and `?pageSize=` (default `20`, **max 100** — larger values are
silently clamped, not rejected).

Ordering is newest-first by `occurredAt`, with `id` as a tiebreaker so paging is stable: entries
written in the same transaction share a timestamp to the microsecond, and without the tiebreaker
the same row could appear on two pages while another was skipped. Verified: 3 pages of 10 over
26 entries returns 26 rows, 26 unique.

Suggested sizes: `pageSize=10` for a dashboard card, `pageSize=50` with infinite scroll for a
full activity page.

---

## 4. The `type` vocabulary

`type` is `"{entityType}.{verb}"`. Switch on it to pick an icon and phrasing. Not every
combination occurs — this is the **complete** list of what the API emits today:

| `type` | Fired when |
|---|---|
| `Organization.Created` | org created |
| `Organization.Updated` | name / description / avatar changed |
| `Organization.MemberAdded` | user added to org |
| `Organization.MemberRemoved` | user removed from org |
| `Organization.RoleChanged` | member's org role changed |
| `Project.Created` | project created |
| `Project.Updated` | project name or description changed |
| `Project.Assigned` | project reassigned to a different team |
| `Project.RoleChanged` | project role granted or revoked |
| `Team.Created` | team created |
| `Team.Updated` | team name or description changed |
| `Team.Archived` | team archived |
| `Team.MemberAdded` | user added to team |
| `Team.MemberRemoved` | user removed from team |
| `WorkItem.Created` | work item created |
| `WorkItem.Updated` | title / description / priority / story points / team changed |
| `WorkItem.StateChanged` | state transition |
| `WorkItem.Assigned` | assignee changed |
| `WorkItem.Deleted` | work item soft-deleted |
| `WorkItem.Linked` | link added to another work item |
| `Comment.Commented` | comment posted on a work item |
| `Sprint.Created` | sprint created |
| `Sprint.Updated` | goal / dates changed, or a work item added or removed |
| `Sprint.StateChanged` | Planning → Active → Completed |
| `Sprint.Deleted` | sprint deleted |
| `Board.Updated` | board renamed, or a column added / updated / removed |

Treat this as open-ended anyway — **fall back to rendering `detail` verbatim for any `type` you
don't recognise** rather than dropping the entry. New types will be added without a version bump.

One field change produces one entry. Editing a work item's title *and* priority in a single
`PUT` yields two `WorkItem.Updated` rows, so the feed reads as a change log rather than
"someone touched this".

---

## 5. Field semantics — the parts that will bite you

**`title` is a snapshot; `organization` / `project` / `team` / `actorName` are live.**

`title` is frozen at the moment the action happened, so a work item renamed from "Migrate auth"
to "Migrate auth to OIDC" produces a feed where the older entries still say "Migrate auth". That
is intentional — it is what actually happened. Don't "fix" it by re-fetching the entity name.

The surrounding names are resolved at read time and always show the current value. So a rename
of a *project* updates retroactively across the feed, while a rename of the *subject* does not.

**`detail` is pre-rendered. Just display it.** The server formats it:

| Situation | Renders as |
|---|---|
| both old and new | `Priority: Medium → High` |
| only new | `Description: rewrite` |
| only old | `Column: Blocked removed` |
| membership / comment entries | just the member name or comment body |
| nothing to say | `null` — e.g. `Organization.Created` |

Blank strings are folded to null server-side, so you won't see `Description:  → rewrite`.
`detail` **can be null** — handle it.

**`entityId` + `entityType` are your deep link.** `projectId` / `teamId` give you the
breadcrumb. For `Comment.Commented`, `entityId` is the *comment* id while `title` is the work
item's title — link to the comment, label it with the work item.

**`actorName` is `"Unknown"`** if the acting user row no longer exists. It is never null.

**`teamId` is null on most entries** — only team, sprint, and project-reassignment entries carry
one. Same for `projectId` on org-level entries. Don't assume either is present.

---

## 6. Access and error codes

| | |
|---|---|
| `GET /api/orgs/{orgId}/activity` | **403** if the caller is not a member of that org. Also 403 for an org id that doesn't exist — it does not distinguish, by design. **200** with an empty page if the org has no activity yet. |
| `GET /api/workspace/activity` | **200** always for an authenticated caller; empty page if they belong to no organizations. |

**Every organization member can read the org feed.** The endpoint requires `Reader`, and
membership always grants at least that — there is no role that gets you into an organization but
locked out of its activity. If a member sees a 403, that is a bug, not a permissions setting.

Losing membership revokes access immediately: a removed member gets 403 on the org feed and
their workspace feed drops that org's entries.

---

## 7. Other change that affects you

**`POST /api/teams/{teamId}/members` now returns 400 for a non-org-member.**

```json
{
  "message": "User must be a member of the organization before being added to one of its teams.",
  "statusCode": 400
}
```

Previously this succeeded and quietly created someone who was on a team but in no organization —
who then saw nothing in either activity feed and didn't appear in the org member list. Team
membership now requires org membership first, matching the rule project roles already enforced.

**UI implication:** in an "add member to team" picker, source the candidate list from
`GET /api/orgs/{orgId}/members`, not from a global user search. If you keep a global search,
handle the 400 with a prompt to add them to the organization first.

---

## 8. What did *not* change

`GET /api/workspace/summary` and `GET /api/workspace/notifications` are untouched — same routes,
same shapes, same bare-array response for notifications.

Be aware that **notifications is now inconsistent with activity**: it still reads work item
history directly, so it only knows about work item field changes and still emits the old
`"WorkItemActive"` / `"WorkItemUpdated"` type strings. It should eventually be rebuilt on the
activity log too. Until then, don't share a rendering component between the bell and the feed —
the `type` vocabularies are different.

---

## 9. Migration checklist

- [ ] `res.data` → `res.data.items` on both activity endpoints
- [ ] Add `?page` / `?pageSize`; wire up `hasNextPage` / `totalCount`
- [ ] Replace the old `type` switch with the `§4` vocabulary, with a passthrough default
- [ ] Handle `detail === null`
- [ ] Handle `projectId` / `teamId` / `project` / `team` being null
- [ ] Deep-link from `entityId` + `entityType`
- [ ] Delete the `WorkspaceActivityResponse` type; it no longer exists server-side
- [ ] Team member picker: scope to org members, or handle the new 400

---

## 10. Backend notes, for context

Entries come from a new `activity.ActivityLogs` table, written by event handlers subscribed to
the domain event bus. Two consequences worth knowing:

- **Recording is best-effort.** Handlers run after the originating transaction commits and their
  failures are swallowed, so a write never fails because the audit trail was unavailable. In
  exchange, an entry can very occasionally be missing. Don't build anything that assumes the feed
  is a complete ledger — it is a feed.
- **History before this deploy is partial.** The migration backfills existing work item history,
  so state changes and edits survive. Nothing else does: there was no record of project, team,
  sprint, board, or membership activity before the table existed. Backfilled rows are also
  thinner — assignee changes show raw GUIDs instead of names, and they carry the work item's
  *current* title rather than a snapshot. Entries recorded from this deploy onward are complete.
