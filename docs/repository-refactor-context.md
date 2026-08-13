# Backend Changes — What They Mean for the Frontend

Status: merged to `fix/conflict` · Scope: `server/BoardSync.Api` · Backend-only

Covers two pieces of work that landed together: the **repository refactor** (§1–§2) and the
**transactional outbox** from Phase 1 of the scaling plan (§3).

---

## TL;DR for the frontend team

**No route, request shape or response shape changed.** Two optional additions, and **one real
behaviour change** you do need to know about:

| | What | Action |
| --- | --- | --- |
| 1 | **The activity feed is now eventually consistent** | ⚠️ **Read §3.** Do not assume an entry appears the instant a write returns 200. |
| 2 | `GET /api/notifications` — a new, shorter route for the bell | Optional. `GET /api/workspace/notifications` still works and returns a byte-identical body. |
| 3 | `?limit=` on the bell (default 20, max 50) | Optional. Omit it and you get exactly what you got before. |

Item 1 is the only one that can bite you. Everything else is additive.

---

## 1. What actually changed

All data access moved out of controllers and services into a repository layer, one per module.
Before this, controllers were running EF Core queries directly and services were mixing business
rules with query construction.

### Before

```
Controller ──▶ DbContext          (4 controllers queried the database directly)
Service    ──▶ DbContext          (7 services built their own EF queries)
```

### After

```
Controller ──▶ Service ──▶ Repository ──▶ DbContext
```

`BoardSyncDbContext` is now referenced by exactly three kinds of file: repositories, the DbContext
itself, and startup. No controller and no service touches it.

### Repositories added

| Module | Repository | Owns |
| --- | --- | --- |
| Sprints | `ISprintRepository` | `plan.Sprints`, `plan.SprintWorkItems` |
| Sprints | `IBoardRepository` | `plan.Boards`, `plan.BoardColumns` |
| Rbac | `IRoleAssignmentRepository` | `iam.RoleAssignments` |
| Activity | `IActivityRepository` | `activity.ActivityLogs` + feed name lookups |
| Notifications | `INotificationRepository` | bell reads over `work.WorkItemHistory` |
| OrgProject | `IWorkspaceRepository` | cross-organization dashboard counters |
| Search | `ISearchRepository` | the four global-search queries |
| Shared/Auth | `IUserRepository` | `public.Users`, `public.RefreshTokens` |

These join the four that already existed (`IOrganizationRepository`, `IProjectRepository`,
`ITeamRepository`, `ITeamMembershipRepository`) and `IWorkItemRepository`.

### New modules

**`Modules/Notifications`** — the bell was previously an action on `WorkspaceController` that read
work item history inline. It is now a module with its own controller, service, repository and DTOs.

**`Modules/Search`** — global search was an entire EF query set living in `SearchController`. Now a
repository plus a thin service; the controller only validates the term and shapes the envelope.

---

## 2. API surface: what moved, what did not

### Changed — additive only

**`GET /api/notifications`** (new route)

Serves the notification bell. The original `GET /api/workspace/notifications` is still registered on
the same action and returns the same body — verified byte-identical in testing. Migrate when
convenient, or never.

```
GET /api/notifications                    ← new, preferred
GET /api/workspace/notifications          ← original, still works
```

**`?limit=` on the bell**

```
GET /api/notifications?limit=10
```

Default `20` (unchanged from before), maximum `50`. Out-of-range values are **clamped, not
rejected** — `?limit=999` returns 50 with a 200, consistent with how `?pageSize` already behaves on
the paginated endpoints.

### Unchanged

Everything else. Specifically, and deliberately:

- `GET /api/workspace/summary` — same route, same four counters, same shape.
- `GET /api/workspace/activity` and `GET /api/orgs/{orgId}/activity` — same routes, same
  `PagedResult<ActivityResponse>`, same `?page` / `?pageSize` / `?cursor` behaviour.
- `GET /api/search` — same route, same `GlobalSearchResponse`, same 10-per-category cap, same 400
  for a term under 2 characters.
- `GET /api/users/me`, `/api/users/{id}`, `/api/users/by-email` — same routes, same `UserProfile`.
- Every board, sprint, work item, project, team, organization and auth endpoint.

The notification `type` vocabulary is also unchanged: `WorkItemUpdated`, and `WorkItem{State}` for
state changes. If you already switch on those strings, keep doing so.

---

## 3. The activity feed is now eventually consistent

This is the one change that can break an assumption, so it gets its own section.

### What changed

Domain events used to be handled inline, on the same request that caused them. A work item write and
its activity entry happened back to back, so by the time the write returned 200 the feed entry was
already there.

Events now go through a **transactional outbox**: the event is written in the same database
transaction as the change, and a background dispatcher delivers it immediately afterwards.

### What that means for you

**A feed read immediately after a write may not show that write yet.** In practice the gap is
milliseconds — the dispatcher is woken by a Postgres notification the moment the transaction
commits — but it is not zero, and under load or during a restart it can stretch to a few seconds.

Concretely, this pattern is now a race:

```ts
await createWorkItem(...);                     // 200 OK
const feed = await getActivity(orgId);         // may not contain it yet
expect(feed.items[0].entityId).toBe(newId);    // ⚠️ flaky
```

### What to do instead

- **Don't refetch the feed to confirm your own write.** The write's own 200 response is the
  confirmation. If you need the new entity on screen, use the response body you already have.
- **Render optimistically.** You know what the user just did; show it, and let the feed catch up on
  its next natural refresh.
- **If you poll, keep polling.** A missing entry is not an error — the next poll will have it. Do
  not treat one empty result as "the write failed".
- **In tests, don't assert on the feed straight after a write.** Assert on the write's own response,
  or poll the feed with a short retry.

Endpoints affected: `GET /api/orgs/{orgId}/activity` and `GET /api/workspace/activity`.

**Not affected:** everything else. Work items, boards, sprints, projects, teams and the notification
bell are all still read-your-own-writes — they read the tables you just wrote directly, with no
dispatcher in between. Only the activity feed is built from events.

### Why it is worth the trade

The old design could **silently lose entries**. The write committed, then the activity row was
written in a separate transaction by a handler whose exceptions were swallowed — so a crash, a
timeout or a thrown handler left a hole in history that nobody was told about. The event and the
change are now atomic: no commit, no event; commit, guaranteed event. A few milliseconds of lag buys
a feed that cannot quietly go wrong.

It is also the delivery path the real-time work depends on. When boards go live, the push you
receive over the socket and the entry you see in the feed will come from this same ordered log,
which is what will let them agree.

---

## 4. Things worth knowing about the bell

Moving notifications into their own module made its current limitations explicit rather than
incidental. All three predate this refactor — none of them are new — but they are now written down
in the module itself and worth knowing before you build UI on top:

1. **There is no read/unread state.** Notifications are derived from work item history at read time,
   not stored per user. There is nothing to mark as read, and no unread count that can be
   decremented. A badge showing "3 new" would have to be a client-side comparison against the last
   timestamp you saw.
2. **It only knows about work item field changes.** Project, team, sprint, board and membership
   activity do **not** appear in the bell, even though all of them appear in the activity feed.
3. **Its `type` vocabulary differs from the activity feed's.** The bell emits `WorkItemUpdated`;
   the feed emits `WorkItem.Updated` (`EntityType.Verb`). **Do not share a rendering component
   between the bell and the feed** — the strings will not line up.

Rebuilding the bell on the activity log would fix all three at once. That is the obvious next step
for this module and is not scheduled yet. If the product wants read state or a genuine unread count,
raise it — that is a schema change, not a UI tweak.

---

## 5. One bug found and fixed during this work

`GET /api/users/me` and `/api/users/by-email` briefly returned **400** with a LINQ translation error.
The cause was mine: a repository helper applied the `UserProfile` projection before the `WHERE`, so
the filter was being built against the projected record rather than the entity, which EF cannot
translate to SQL.

It compiled cleanly and only failed when the endpoint was actually called. It was caught by running
the endpoints against a live database, and is fixed. Worth flagging because it is the failure mode
this kind of refactor produces: **a green build proves nothing about EF query translation.**

---

## 6. Why this was worth doing

Three reasons, in the order they will matter to you:

**Response shapes are now defined in one place per module.** When the bell or the feed changes
shape, there is exactly one file to change and one place to look. Previously the notification shape
was assembled inline in a controller action, which is why it drifted from the activity feed's
vocabulary in the first place.

**The real-time work in `docs/scaling-realtime-caching.md` needs this.** Phase 2 pushes deltas over
SignalR built from the same data the REST endpoints return. That is only sane if there is one query
per concept rather than one per call site — otherwise the pushed payload and the fetched payload
drift apart, and the client sees two versions of the same thing.

**Caching has somewhere to go.** Phase 1 puts a cache in front of the hot reads. With queries spread
across controllers there was no seam to put it in; with repositories there is exactly one.

---

## 7. Verification

Backend was exercised end to end against a live database, not just compiled:

- Auth chain through the new `UserRepository` — register → confirm → login → authenticated call.
- Both notification routes, confirmed byte-identical.
- `users/me`, `users/by-email`, `workspace/summary`, `workspace/activity`, `search`, `orgs` — all 200.
- Full write path through the new repositories: create org → team → project → 6 work items →
  sprint → add backlog → activate sprint → change work item state.
- Board render with an active sprint: 6 cards distributed across columns, tags populated on all 6.
- Activity paging: cursor page 2 identical to offset page 2, zero overlap with page 1, malformed
  cursor falls back to 200.

For the outbox specifically (§3), the properties that matter to you were proven rather than assumed:

- **Nothing is lost.** A rejected write (duplicate slug → 409) left zero queued events; a successful
  one was delivered and its feed entry written.
- **Nothing is duplicated.** A message forced into redelivery produced no second feed entry.
- **Nothing stalls.** A full run drained 23 messages with 0 pending and 0 failed attempts, and the
  feed showed all 18 resulting entries within seconds.

Build is clean: 0 warnings, 0 errors.

---

## 8. What we need from the frontend team

One thing that matters, then the optional ones:

- [ ] **Check nothing refetches the activity feed to confirm its own write** (§3). This is the only
      item that can produce a real bug — everything below is housekeeping.
- [ ] Point the bell at `GET /api/notifications` (optional, cosmetic)
- [ ] Decide whether you want `?limit` other than the default 20
- [ ] Confirm you are **not** sharing a component between the bell and the activity feed
- [ ] Tell us if read/unread state on notifications is a product requirement — it is a backend
      schema change and we would rather know before Phase 2 than after
