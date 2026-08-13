# Real-Time — Frontend Contract

Status: shipped · Hub: `/hubs/workspace` · Transport: SignalR over WebSockets

This is what you need to build live boards and a live activity feed. Nothing here is required —
the REST API is unchanged and polling still works. This replaces polling when you want it.

---

## 1. Connect

```ts
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/workspace', {
    accessTokenFactory: () => yourJwt,
    skipNegotiation: true,
    transport: signalR.HttpTransportType.WebSockets,
  })
  .withAutomaticReconnect()
  .build();

connection.on('Message', handleMessage);
await connection.start();
```

**Use `skipNegotiation` with WebSockets.** It skips a round trip and, more importantly, removes any
need for sticky sessions at the load balancer — with several API instances behind one address, a
negotiated connection can otherwise land on a different instance than the one it negotiated with.

The token goes in the query string, not a header. Browsers cannot set headers on a WebSocket
handshake, so SignalR appends `?access_token=`. The server only accepts query-string tokens on the
hub path; every other endpoint still requires the header.

---

## 2. Subscribe to what you are looking at

A topic names **what the user is looking at**, not a kind of event. Subscribe when a view opens,
unsubscribe when it closes.

| Topic | Subscribe when | Carries |
| --- | --- | --- |
| `user:{userId}` | automatic on connect | notifications, assignment to you, your role changing |
| `org:{orgId}` | org dashboard or activity page is open | activity feed, membership and role changes |
| `project:{projectId}` | a project or its board is open | work items, comments, board columns |
| `team:{teamId}` | a team page is open | team membership, that team's sprints |
| `sprint:{sprintId}` | a sprint or backlog is open | backlog changes, sprint status |

```ts
const result = await connection.invoke('SubscribeAsync', `project:${projectId}`, null);

if (!result.subscribed) {
  // Not permitted, or no such topic — deliberately indistinguishable.
  return;
}

lastSequence[topic] = result.currentSequence;
```

**There is no `board:` topic.** A board is one-to-one with its project, so a board topic would
always have the same subscribers as the project topic. Board changes arrive on `project:`.

Subscriptions are authorized individually, and a denial does not say why. "Not permitted" and "does
not exist" return the same answer on purpose — otherwise the hub becomes a way to discover which
projects exist.

---

## 3. Receiving messages

```ts
type RealtimeMessage = {
  sequence: number;      // global, increasing — your resume point
  topic: string;         // which subscription this arrived on
  type: string;          // e.g. "WorkItemStateChanged"
  payload: unknown;      // the event itself
  occurredAt: string;    // ISO-8601 UTC
};
```

Payloads are **deltas, not invalidations**. `WorkItemStateChanged` carries the work item, project,
old state and new state — enough to move a card without refetching anything. That is deliberate: if
every message meant "go refetch", one person's write would trigger a read from everyone else
watching, which is the worst possible behaviour exactly when a board is busy.

Always record the highest `sequence` you have processed, per topic. It is the only thing that makes
reconnection safe.

```ts
function handleMessage(m: RealtimeMessage) {
  lastSequence[m.topic] = Math.max(lastSequence[m.topic] ?? 0, m.sequence);
  apply(m);
}
```

### Event types you will receive

| `type` | On topic | What to do |
| --- | --- | --- |
| `WorkItemCreated` | `project:` | add the card |
| `WorkItemUpdated` | `project:` | patch the named field |
| `WorkItemStateChanged` | `project:` | move the card between columns |
| `WorkItemAssigned` | `project:`, `user:` | swap the avatar; notify the new assignee |
| `WorkItemDeleted` | `project:` | remove the card |
| `WorkItemCommentAdded` | `project:` | bump the comment count |
| `SprintWorkItemAdded` / `Removed` | `sprint:`, `team:` | add/remove from the backlog |
| `SprintStatusChanged` | `sprint:`, `team:`, `org:` | the active sprint changed — refetch the board |
| `BoardChanged` | `project:`, `org:` | columns changed — refetch the board |
| `ProjectCreated` / `Updated` | `org:`, `project:` | project list and header |
| `TeamCreated` / `Updated` / `Archived` | `org:`, `team:` | team list |
| `MemberAddedToOrg` / `RemovedFromOrg` | `org:`, `user:` | member list; **re-check your own access** |
| `OrgMemberRoleChanged` / `ProjectRoleChanged` | `org:`, `user:` | **re-check your own access** |

The two structural ones — `SprintStatusChanged` and `BoardChanged` — are the exceptions to "never
refetch". The board's shape changed, not one card in it, so one refetch is the right answer.

---

## 4. Reconnecting without going stale

**This is the part that is easy to get wrong.** The dangerous failure is not the disconnect — it is
the reconnect that misses messages and leaves the UI looking correct while showing stale data.

On reconnect, resubscribe **with your last sequence**:

```ts
connection.onreconnected(async () => {
  for (const topic of activeTopics) {
    const result = await connection.invoke('SubscribeAsync', topic, lastSequence[topic] ?? null);

    if (result.resync) {
      await refetchEverythingFor(topic);   // the gap was too large to replay
    }

    lastSequence[topic] = result.currentSequence;
  }
});
```

What happens on the server:

```
gap ≤ 200 messages   →  the missed messages are replayed to you, in order, before this returns.
                        resync: false. Do nothing special.

gap > 200 messages   →  resync: true. Nothing is replayed. Refetch over REST.
   or too old            The deltas that follow are valid from currentSequence onward.
```

Passing `null` means "fresh subscription" — no replay, start from now. Use it the first time you
open a view; use the stored sequence every time after.

**If you ignore `resync` you will show stale data**, and nothing will look broken until a user
notices the board disagrees with reality.

---

## 5. Drag-and-drop: use the move endpoint

There are two ways to reorder a sprint backlog. For anything a second person might be dragging at
the same time, only one of them is correct.

**Use this:**

```
PATCH /api/sprints/{sprintId}/workitems/{workItemId}/move
{ "afterWorkItemId": "…", "beforeWorkItemId": "…" }
```

Name only the card that moved and the two it landed between. `afterWorkItemId: null` means the top
of the list; `beforeWorkItemId: null` means the end. The response returns the item's new `rank`.

**Not this, when concurrent editing is possible:**

```
PATCH /api/sprints/{sprintId}/workitems/reorder
{ "workItemIds": [ …the entire ordering… ] }
```

Sending a whole ordering means sending a view of the list computed *before* the other person's move
existed. Whoever saves second silently reverts the first. The endpoint still works and is fine for
a single editor, but it is last-writer-wins across the entire backlog.

With the move endpoint, two people dragging different cards write different rows and cannot collide
at all. Two people dragging the *same* card resolve as last-write-wins on one row, which is both
correct and what users expect. **Verified:** two simultaneous moves of different cards both survived.

### About `rank`

Backlog items now carry a `rank` — a fractional sort key. Order by it ascending. **Treat it as
opaque:** compare ranks, never compute with them or assume they are contiguous. The values are
deliberately sparse so a card can be inserted between any two without touching anything else.

---

## 6. Suggested shape

```ts
// One place that owns sequences and resubscription.
const activeTopics = new Set<string>();
const lastSequence: Record<string, number> = {};

async function subscribe(topic: string) {
  const result = await connection.invoke('SubscribeAsync', topic, lastSequence[topic] ?? null);
  if (!result.subscribed) return false;

  if (result.resync) await refetchEverythingFor(topic);

  activeTopics.add(topic);
  lastSequence[topic] = result.currentSequence;
  return true;
}

async function unsubscribe(topic: string) {
  activeTopics.delete(topic);
  await connection.invoke('UnsubscribeAsync', topic);
}
```

Keep `lastSequence` in memory only. It is a within-session resume point; a page reload fetches fresh
state anyway, so persisting it across reloads buys nothing and risks resuming from a position that
has aged out.

---

## 7. Things to know

- **The activity feed is eventually consistent.** Both over REST and over the socket. A change is
  visible milliseconds after the write, not instantly. Do not refetch the feed to confirm your own
  write — see `docs/repository-refactor-context.md` §3.
- **Real-time is an optimisation, never the source of truth.** Every message describes a change that
  is already committed and already readable over REST. If the socket drops, the app must keep
  working on REST alone.
- **A role change can revoke a subscription's basis.** When you receive `OrgMemberRoleChanged` or
  `ProjectRoleChanged` on your own `user:` topic, re-check your access. The server re-authorizes
  periodically, but your UI should not wait to find out.
- **Without Redis configured, the hub is single-instance only.** Development is fine; a deployment
  needs it or a client connected to one instance will not see changes written on another. The API
  logs a warning at startup when the backplane is missing.

---

## 8. Presence — who else is here

```ts
// Who is watching, right now.
const userIds: string[] = await connection.invoke('GetPresenceAsync', topic);

// Told whenever that changes.
connection.on('PresenceChanged', ({ topic, userIds }) => showAvatars(topic, userIds));

// Keep yourself counted. Every ~30 seconds while a view is open.
setInterval(() => connection.invoke('HeartbeatAsync'), 30_000);
```

You are added to a topic's presence automatically when you subscribe, and removed when you
unsubscribe or disconnect. `PresenceChanged` fires on arrivals and departures — not on heartbeats,
so it will not flood.

**Send the heartbeat.** Presence entries expire after 90 seconds without one. That is deliberate: a
tab that is closed, crashes, or loses wifi never sends a goodbye, and without expiry that person
would show as present forever. Stop heartbeating and you fade out on your own.

`GetPresenceAsync` is authorized like a subscription — a topic you cannot read returns an empty
list rather than an error.

---

## 9. Editing the same work item at the same time

Work items now carry an opaque `version`. Send it back and you get told about conflicts instead of
silently overwriting whoever edited while your form was open.

```ts
const item = await getWorkItem(id);            // item.version

const res = await fetch(`/api/workitems/${id}`, {
  method: 'PUT',
  body: JSON.stringify({ ...changes, expectedVersion: item.version }),
});

if (res.status === 409) {
  // Someone saved between your read and your write. Reload and reapply.
}
```

Works on `PUT /api/workitems/{id}` and `PATCH /api/workitems/{id}/state`.

**Omitting `expectedVersion` keeps the old behaviour** — last write wins, no conflict signal. That
is for compatibility, not a recommendation: if two people can open the same work item, send it.

Treat `version` as opaque. Compare it, never compute with it — it comes from the database's own row
version and does not increment by one.

---

## 10. What is not built yet

- **Field-level merge on conflict.** A `409` tells you someone else saved; it does not tell you
  *what* they changed. The client has to reload and let the user reapply. Good enough for now, and
  the alternative is real merge UI.
- **Presence detail.** You get user ids, not "who is dragging which card". Cursor- or card-level
  presence would be a further step.

If either matters for the UI you are building, say so — the priority should come from the product
rather than from the order I happened to build things in.
