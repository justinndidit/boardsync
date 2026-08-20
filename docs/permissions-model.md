# Permissions: Current State and Target Model

Status: **Stages 0–3 shipped**, Stage 4 proposed · Scope: `server/BoardSync.Api` ·
Companion to `docs/scaling-realtime-caching.md` (§5.2 specifies the RBAC cache this builds on)

This document records what the permission system did before this work, the defects the audit
surfaced, the model that replaces it, and the order to get there. §1 and §3 describe the state
**before** Stages 0–1; §9 records what actually shipped and how it was verified.

Decisions taken on the open questions in §7: team memberships are **deleted outright** on
organization removal, and team membership confers **flat `Contributor`** on the team's projects.

---

## 1. Where permissions stand today

### 1.1 The model

`RoleAssignment` (`Modules/Rbac/Models/RoleAssignment.cs`) is the only permission record in the
system:

```csharp
Guid    UserId
RoleType Role            // OrgAdmin=10, ProjectAdmin=20, TeamMember=30, Reader=40, User=50
RoleScope Scope          // Organization | Project | Team
Guid?   OrganizationId   // exactly one of these three is non-null,
Guid?   ProjectId        // enforced by CK_RoleAssignment_ExactlyOneScope
Guid?   TeamId           // (BoardSyncDbContext.cs:171-176)
```

`RoleType` is an ordinal ladder where **lower means more privileged**. Uniqueness of
(user, role, scope target) is enforced by three partial unique indexes created in raw SQL by the
`HardenRoleAssignmentAndOrgMembership` migration — deliberately not modelled in EF, because a plain
composite index over three nullable columns would accept unlimited duplicates under Postgres NULL
semantics (`BoardSyncDbContext.cs:178-184`).

### 1.2 The decision path

`RbacService.HasRoleAsync` (`Modules/Rbac/Services/Implementations/RbacService.cs:60`) answers one
question: *does this user hold at least this rank at this scope?*

1. Load the roles the user holds at that exact scope.
2. Compare in memory against every rank that satisfies the requirement (`:144`).
3. If no direct match, test org-admin inheritance via `IsOrgAdminForScopeAsync`
   (`:79` → `RoleAssignmentRepository.cs:57`).

The in-memory comparison at step 2 is **correct and must stay that way**. `RoleType` is persisted
with `HasConversion<string>()` (`BoardSyncDbContext.cs:193-197`), so a database-side `<=` would
compare *names*: `'TeamMember' <= 'Reader'` is false and `'Reader' <= 'TeamMember'` is true, which
would simultaneously deny team members read access and let readers perform team-member writes. The
existing comment on `IRoleAssignmentRepository.GetRolesAtScopeAsync` documents this; preserve it
through any rewrite.

### 1.3 The caching chain

Three layers, registered outermost-first in `Program.cs:159-172`:

| Layer | Component | Lifetime | Invalidation |
|---|---|---|---|
| L0 | `MemoizingRbacService` | per request | `Clear()` on any write |
| L1/L2 | `CachingRbacService` (HybridCache → Redis) | 30s local / 5m distributed | version stamp per user |
| — | `RbacService` | — | source of truth |

The version-stamp design is sound and worth keeping. Each user has a Redis counter folded into every
decision key (`CachingRbacService.cs:97`); bumping it orphans every cached decision about that user
atomically, and the orphans expire on their own. Three deliberate choices in there are correct and
should survive the rewrite:

- **Bypass inside transactions** (`:78`) — HybridCache invokes its factory outside the ambient
  execution-strategy scope, and EF refuses to start a retriable operation while a user transaction
  is open, so a cache miss in that position throws rather than querying.
- **Fail to the database when the version read fails** (`:88-94`) — without a readable version there
  is no way to know whether a cached decision is current.
- **Do not swallow invalidation failures** — a failed bump means a revoked user keeps access.

### 1.4 Enforcement

Every guard is a hand-written first line in a controller action:

```csharp
await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
```

There are roughly 60 such call sites across `WorkItems`, `Sprints`, `Boards`, `Projects`, `Teams` and
`Organizations`, each with a private `RequireXRoleAsync` helper that throws `ForbiddenException`
(`WorkItemsController.cs:259`, `TeamsController.cs:151, :157`, and the equivalents). `Modules/Sprints`
has the only extracted version, `AuthHelpers` (`Modules/Sprints/Domain/Helpers/AuthHelper.cs`).

**There is no test project in the repository.** `find` over the tree returns one `.csproj`.

### 1.5 Realtime

`TopicAuthorizer` (`Shared/Realtime/TopicAuthorizer.cs`) gates subscriptions at `Reader` against the
topic's scope, resolving sprint topics through `Sprint.TeamId`. The uncommitted work on
`fix/conflict` adds `SubscriptionAuditor` + `AccessChangeNotifier`, which re-check live subscriptions
on a Redis pub/sub announcement and on a periodic sweep. The design is right — revocation has to
reach instance-side code, because asking a client to give up its own access is no boundary at all.

---

## 2. The domain model, as the schema actually defines it

This is the part most worth reading, because the permission model has to follow it and currently
does not.

> **Amended by `Stage4_SprintBelongsToProject`.** Sprints were moved from teams to projects. The
> tree below and the second consequence under it describe the model as it stood when this document
> was written; the current shape is:
>
> ```
> Organization
> ├── OrganizationMembership              org.OrganizationMemberships
> └── Team                    (N per org) Team.OrganizationId
>     ├── TeamMembership                  org.TeamMemberships
>     └── Project             (N per team) Project.AssignedTeamId  ← required, RESTRICT
>         ├── Board           (1 per project)
>         ├── Sprint          (N per project) Sprint.ProjectId  ← FK added by Stage 4
>         └── WorkItem        (N per project)
> ```
>
> `sprint:read` / `sprint:order` are now part of project contribution and `sprint:manage` /
> `sprint:scope` part of project administration, so a Scrum Master or Product Owner reaches a sprint
> through the team → project edge but needs `ProjectAdmin` on the project to run it. See §11.

```
Organization
├── OrganizationMembership              org.OrganizationMemberships
└── Team                    (N per org) Team.OrganizationId
    ├── TeamMembership                  org.TeamMemberships
    ├── Sprint              (N per team) Sprint.TeamId
    └── Project             (N per team) Project.AssignedTeamId  ← required, RESTRICT
        ├── Board           (1 per project)
        └── WorkItem        (N per project)
```

`Project.AssignedTeamId` is a **required, restricting foreign key**
(`BoardSyncDbContext.cs:265-268`), validated on create against the same organization
(`ProjectService.cs:52`) and re-validated on reassignment (`:154`). `Team.AssignedProjects` is the
inverse collection.

So: **a project belongs to exactly one team; a team can hold many projects.** The scope graph is a
tree, not a DAG. `Project.OrganizationId` is a denormalization of `Team.OrganizationId` and is not an
independent edge.

Two consequences the permission model currently ignores:

- **Team membership is the natural carrier of project access.** The edge from a person to a project
  already exists in the schema; RBAC just does not read it.
- **Sprints are team-scoped and shared across the team's projects.** Anything that lets one
  project's administrator manage sprints is a lateral escalation into that team's *other* projects.
  *(No longer true — see the amendment above. A sprint now belongs to one project, so a project
  administrator managing it reaches nothing outside that project, and the escalation this warned
  about cannot arise.)*

---

## 3. Defects

Ordered by severity. Each is confirmed against the code, not inferred.

### 3.1 Removing a member from an organization leaves their project and team grants intact

**Security hole.** `OrganizationService.RemoveMemberAsync` clears org-scope rows only:

```csharp
await _rbac.RemoveAllRolesAsync(userId, RoleScope.Organization, orgId, token);  // :206
```

Their `ProjectAdmin` on projects in that org, their `TeamMember` on its teams, and their
`TeamMembership` rows all survive. Because `TopicAuthorizer` checks project scope directly, the
`MemberRemovedFromOrg` → audit path does not drop their realtime subscriptions either — they keep
receiving the live feed of a project in an organization that ejected them.

### 3.2 `ProjectAdmin` at team scope is unreachable, so sprint management is OrgAdmin-only

`SprintsController` gates create/start/complete/update on
`RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin)` (`:92, :110, :132, :146`), and
`TeamsController` gates update, archive and add/remove member the same way (`:79, :95, :118, :145`).

Nothing in the codebase ever assigns `ProjectAdmin` at `RoleScope.Team`. Team-scope assignments only
ever create `TeamMember` (`TeamService.cs:84, :186`); `ProjectAdmin` is only ever assigned at project
scope (`ProjectService.cs:76`). The only way those endpoints pass is the `IsOrgAdminForScopeAsync`
fallback. **A project administrator cannot manage sprints on their own project's team.**

### 3.3 Only OrgAdmin inherits, and the Team → Project edge is not modelled

`IsOrgAdminForScopeAsync` is the sole inheritance rule. Consequences:

- An org-level `Reader` can read the organization and nothing inside it.
- Adding an org member grants `Reader` at org scope (`OrganizationService.cs:188`) — which by itself
  grants access to no team, no project, no board and no work item.
- Team membership grants nothing on the team's projects.

Project access therefore requires a direct project-scope assignment or OrgAdmin, full stop.

### 3.4 Role and scope are orthogonal, with nothing enforcing compatibility

`AssignRoleAsync(user, RoleType.OrgAdmin, RoleScope.Team, teamId)` is accepted by the service and by
the check constraint, which only enforces that exactly one scope column is populated. The resulting
row satisfies every team-scope rank check while being semantically meaningless. `ProjectsController`
guards against this at one endpoint with an `AssignableProjectRoles` allowlist (`:29`, checked at
`:159`), which is the right instinct applied in exactly one place.

### 3.5 Enforcement is convention, with no test coverage

A new endpoint that omits its `RequireXRoleAsync` line is silently reachable by any authenticated
user in the system. Nothing detects it: not the compiler, not a policy, not a test. With ~60 manual
call sites and no test project, this is the defect most likely to produce the *next* hole.

### 3.6 Fetch-then-authorize leaks resource existence

```csharp
var item = await _workItemService.GetByIdAsync(workItemId, ct);   // :75
await RequireProjectRoleAsync(item.ProjectId, RoleType.Reader, ct);
```

The same shape repeats at `:91, :108, :122, :138` and in the Boards and Sprints equivalents. An
unauthorized caller gets 403 for an existing work item and 404 for a nonexistent one — an oracle for
which ids exist. The full DTO is also materialized and discarded on denial.

### 3.7 The cache stores decisions rather than effective roles

The key is `rbac:{v}:{userId}:{version}:{scope}:{scopeId}:{minimumRole}`
(`CachingRbacService.cs:97`) — up to five entries per (user, scope), one per rank asked.
`docs/scaling-realtime-caching.md:346` specifies `rbac:v1:{userId}:{scope}:{scopeId}`, the effective
role, with the comparison in memory. The implementation drifted from its own design.

Every check is also two Redis round trips on an L1 miss: the version `GET`, then the HybridCache L2
`GET`.

### 3.8 The subscription auditor fails open and sweeps serially

`SubscriptionAuditor` leaves a subscription in place when re-authorization throws (`:76-88`).
Defensible for a transient database error — a blip must not disconnect everyone — but it is the
opposite posture to `CachingRbacService`, which fails closed, and the asymmetry should be a recorded
decision rather than an accident. The sweep is also `connections × topics` sequential awaits on a
single scope.

### 3.9 A principal is a bare `Guid`, and the safe default is accidental

`CurrentUserContext.UserId` returns `Guid.Empty` when the claim is absent or unparseable
(`Shared/Auth/CurrentUserContext.cs:19-22`). That fails closed today only because `Guid.Empty`
happens to hold no role assignments. Nothing expresses the intent, and nothing prevents a future
seed or migration from creating a row for it.

---

### 3.10 Sprint membership is not authorized against the work item — cross-organization read leak

**Confirmed by exploit, severity high — now FIXED (see §9.3).** Found while checking whether a
sprint can span several of a team's projects. It could span *anything*.

`SprintsController.AddWorkItem` guards on the **sprint's** team
(`RequireTeamRoleAsync(sprint.TeamId, RoleType.TeamMember)`, `:182`) and never authorizes the
**work item** being added. `SprintService.AddWorkItemAsync` checks only that the sprint is not
completed and that the item is not already present (`:197-204`) — it never compares the work item's
project to the sprint's team. `BoardRepository.GetCardsForSprintAsync` then selects on
`sw.SprintId == sprintId` with no project filter.

So any authenticated user can:

1. Create their own organization, team, project and sprint — all self-service.
2. `POST /api/sprints/{theirSprint}/workitems` with **any** work item id in the system.
3. Read that item's title, type, priority, assignee, story points and tags from their own sprint
   backlog and their own board.

Verified end to end: an attacker in organization B correctly got `403` reading a victim
organization's work item both directly and through the project listing, then added it to their own
sprint (`201`) and read its title off their own board. Organization boundaries are bypassed
completely.

Exploiting it requires knowing the target work item's GUID, which is not enumerable — so this is
targeted disclosure rather than mass scraping. GUIDs do leak, though: URLs, screenshots, logs,
support tickets, and eventually the git integration.

**This is the defect that reframes the model.** The role check on that endpoint is present and it
passes correctly; it simply authorizes the wrong object. No refinement of the role vocabulary would
have prevented it, which is why §4.5 now treats object-level authorization as a first-class concern
rather than something the scope check implies.

**Fix:** validate the work item's project against the sprint's team in `AddWorkItemAsync` — the
project's `AssignedTeamId` must equal `sprint.TeamId` — and defend in depth by filtering
`GetCardsForSprintAsync` to the board's project. Both are small; neither depends on Stage 2.

---

## 4. Target model

### 4.1 Access derivation

Replace "direct assignment at the exact scope, plus OrgAdmin" with derivation over the tree in §2.
Effective access for user *U* on project *P*:

| # | Rule | Grants |
|---|---|---|
| 1 | `OrgAdmin` at `P.OrganizationId` | full |
| 2 | Direct role assignment at project `P` | that role |
| 3 | `TeamLead` at `P.AssignedTeamId` | project admin |
| 4 | **`TeamMembership` in `P.AssignedTeamId`** | `Contributor` |
| 5 | otherwise | **no access** |

Rule 4 is the new edge and the whole of the "projects are visible to the team working on them"
requirement. It needs no new table and no new column — `Project.AssignedTeamId` already carries it.

Rule 5 is the important one: **there is no org-wide read.** A stakeholder who needs to see a project
without joining its team gets a direct project `Viewer` grant, which `ProjectsController.AssignRole`
already supports. Resist adding a broad org-level viewer role until you find yourself issuing the
same direct grant repeatedly — at which point you will know its shape. This matters more once the
inference layer generates reports, because "who can see this report" should have a narrow answer.

Inheritance runs **down** the tree only. A project role never reaches the project's team.

### 4.2 Scope-typed roles, and team positions

Make the role/scope pairing structural rather than conventional:

| Scope | Roles | Meaning |
|---|---|---|
| Organization | `OrgAdmin`, `Member` | administers the org and everything under it / belongs to it |
| Team | `TeamLead`, `ScrumMaster`, `ProductOwner`, `TeamMember`, `Viewer` | see below |
| Project | `ProjectAdmin`, `Contributor`, `Viewer` | administers one project / writes / reads |

**Shipped as `Stage3_ScopedRoleNames`.** `Member` and the team-scope `Viewer` were added during
implementation: organization membership needed a name of its own (`Reader` was being auto-granted to
every joiner, so it named membership rather than a reading permission), and read-only access to a
team's sprints needed somewhere to land. `Viewer` is the one name held at two scopes, deliberately —
read-only on a team and read-only on a project are the same idea applied to different things.

Enforce the pairing with a check constraint alongside `CK_RoleAssignment_ExactlyOneScope`, which
makes §3.4 unrepresentable rather than merely discouraged.

**`TeamLead`, `ScrumMaster` and `ProductOwner` are positions, not ranks.** This is the point where
the ordinal ladder stops working and has to go. A Scrum Master and a Product Owner are not more or
less privileged than one another — they hold different authority over different things, and the same
person may hold both. Three consequences:

- `RoleType`'s numeric ordering becomes meaningless for team scope, so `HasRoleAsync(minimumRole)`
  cannot express these questions at all. §4.3 is no longer optional.
- `AccessSnapshot` must hold a **set** of roles per scope rather than the single most-privileged one
  it holds today. `AccessEvaluator` already centralises the comparison, so the change is contained
  to it and to `AccessResolver.KeepMostPrivileged`.
- Positions are **singular and transferable**: one holder per position per team, handed over as an
  explicit act. See §4.2.1.

`TeamMember` stays a plain grant, held by anyone on the team, and remains what team membership
confers. Holding a position does not replace membership — it is layered on top, and appointing
someone who is not a team member is rejected, matching the precedent in
`ProjectsController.AssignRole` where a project role requires organization membership first.

Migration note: existing `ProjectAdmin` rows at team scope should not exist (§3.2 proves none are
created), but the migration must assert that rather than assume it. `TeamMember` at team scope maps
across unchanged. `Reader` and `User` retire — `Reader` becomes `Member` at organization scope and
`Viewer` at team and project scope, `TeamMember` at project scope becomes `Contributor`, and `User`
is not a grant at all.

#### 4.2.1 Appointment and transfer

One holder per position per team, enforced by a partial unique index on `(TeamId, Role)` filtered to
the position roles — the same technique the existing uniqueness indexes use, and for the same reason
(Postgres treats NULLs as distinct, so an unfiltered index would not constrain anything).

```
GET    /api/teams/{teamId}/roles              list positions and their holders
PUT    /api/teams/{teamId}/roles/{role}       appoint or transfer  { "userId": "..." }
DELETE /api/teams/{teamId}/roles/{role}       vacate
```

`PUT` is deliberately one operation rather than revoke-then-assign: transferring is a single atomic
act, and doing it as two calls leaves a window with no Scrum Master and a half-finished handover if
the second call fails. It emits `TeamRoleTransferred(teamId, role, fromUserId, toUserId, actedBy)`.

**Who may appoint:** anyone holding `team:role:assign` — `TeamLead` and `OrgAdmin`. The current
holder may also transfer their own position, which covers a planned handover; **OrgAdmin is the
backstop for the unplanned case**, since someone who is unavailable cannot hand over their own role.
That is the requirement that makes org-admin reassignment load-bearing rather than a convenience.

**Vacancy is allowed.** Removing someone from the team or the organization vacates any position they
held — Stage 0's cascade already deletes the rows. Deliberately *not* guarded the way the last
OrgAdmin is: a vacant Scrum Master is recoverable by any TeamLead or OrgAdmin, whereas an
organization with no OrgAdmin is unrecoverable. Blocking a removal because of a position would be
the worse failure. Emit `TeamRoleVacated` so the UI can prompt someone to fill it.

### 4.3 Permission vocabulary

The root cause behind §3.2, §3.4 and the positions above is that **a rank is being used to answer a
capability question**. `RoleType.ProjectAdmin` at team scope is what it looks like when a call site
wants to say "may manage sprints" and only has a ladder to say it with.

Introduce named permissions between roles and call sites, and let roles be bundles of them. Grounded
in what the controllers actually gate today:

| Permission | OrgAdmin | TeamLead | ScrumMaster | ProductOwner | TeamMember |
|---|:--:|:--:|:--:|:--:|:--:|
| `team:read`, `sprint:read` | ✔ | ✔ | ✔ | ✔ | ✔ |
| `sprint:contribute` — add/remove/move/reorder backlog items | ✔ | ✔ | ✔ | ✔ | ✔ |
| `sprint:manage` — create, update, start/complete, delete | ✔ | ✔ | ✔ | ✔ | |
| `team:manage` — rename, archive | ✔ | ✔ | | | |
| `team:member:manage` — add/remove members | ✔ | ✔ | | | |
| `team:role:assign` — appoint and transfer positions | ✔ | ✔ | | | |

Project scope keeps its own set — `project:read`, `project:admin`, `project:member:manage`,
`board:read`, `board:configure`, `workitem:read`, `workitem:write`, `workitem:delete`,
`workitem:comment` — with `Contributor` holding the read/write/comment subset and `ProjectAdmin`
holding all of it.

Two things worth being explicit about, because they are easy to get wrong:

**`sprint:contribute` stays at `TeamMember`.** Today any team member can reorder a sprint backlog
(`SprintsController.cs:182, :199, :224, :251`). Restricting prioritisation to the Product Owner would
be a silent tightening that breaks a working flow. If exclusive prioritisation is wanted it should be
a deliberate, separately-decided change — not a side effect of naming the role.

#### 4.3.1 What a team member may still put into a sprint

Two operations are easy to conflate and must not be:

| Operation | Endpoint | Gate |
|---|---|---|
| Create a work item in a project | `POST /api/projects/{projectId}/workitems` | `workitem:write` — **any team member, unchanged** |
| Commit a work item to a sprint | `POST /api/sprints/{sprintId}/workitems` | see below |

Creating work is never gated on the Product Owner. Anyone on the team can write tasks into their
project's backlog exactly as they do today; that is authorship, not commitment.

Entering a **sprint** is commitment, and the rule is:

> A team member may add a work item to a sprint when that item's parent is already in the sprint.
> Anything else requires `sprint:scope` — Product Owner, Scrum Master, Team Lead or OrgAdmin.

**Decomposition is not a scope change.** If Story S is committed and a developer adds Task T with
`ParentId = S`, the sprint's commitment has not moved — they have broken down work the team already
agreed to, which is precisely their job and precisely what a Product Owner gate should not touch.
Adding a top-level item, or one whose parent is not in the sprint, genuinely is new scope and is
what the gate is for.

`WorkItem.ParentId` already models the Epic → Feature → Story → Task hierarchy, so this needs no
schema change.

Reordering within the sprint (`sprint:order` — move, reorder) stays open to every team member.
What is *in* a sprint is a commitment; how it is *ordered* is execution.

**The known rough edge:** a bug found mid-sprint has no parent, so under this rule it needs a
Product Owner or Scrum Master. That is arguably correct — an unplanned bug *is* a scope change — but
it is the case most likely to generate friction. The recommendation is to ship without an exemption
and let real use decide, because adding a `Bug`-type exemption later is trivial and removing one
after people rely on it is not.

**`ProductOwner` otherwise has no permission that `ScrumMaster` lacks.** Beyond `sprint:scope` there
is no endpoint today that maps to product-owner authority specifically — no acceptance gate, no
prioritisation right distinct from reordering. Naming the position is still worth doing now, because
it records the structure and makes it transferable, but its remaining distinct permissions arrive
when the endpoints that need them do. Inventing permissions it does not yet enforce would be worse
than saying so.

Organization scope keeps `org:read`, `org:admin` and `org:member:manage`. Call sites ask
`Can(principal, "sprint:manage", RoleScope.Team, teamId)` — the question becomes readable where it is
asked, and adding a capability becomes a new permission rather than a new rung.

### 4.4 Principals

Widen the subject from "user id" to a typed principal before the git integration ships:

```csharp
enum PrincipalType { User, Integration, Agent }
```

**Git sync.** Committers already belong to the team assigned to the project, so the integration does
not need to impersonate them. Split the two concerns:

- **Authority** belongs to an `Integration` principal granted `workitem:read` + `workitem:write` on
  one project. Deterministic, revocable, auditable — and it does not break when a commit arrives from
  an email that maps to no user.
- **Attribution** is metadata. Resolve the committer email to a user, record them as the actor on the
  activity entry, and let the permission check ignore it entirely.

Conflating these is what forces on-behalf-of token downscoping, which is significant machinery for no
gain here.

**Inference layer.** An `Agent` principal's permissions are **intersected with the requesting user's**
at call time, so an agent can never widen its caller's reach. Design sprint planning as *proposals*:
the agent reads, writes a proposal record, a human approves, and the approval executes under the
human's authority. The agent then needs read permissions only, and "the LLM moved my cards" becomes
structurally impossible rather than prompt-dependent.

This is the payoff from §4.3. "Read work items and propose plans, never mutate the board" is
expressible as a permission set. It is not expressible as a rank.

---

## 5. Sequencing

Five stages, each independently shippable, each leaving the system working. The value is concentrated
in Stages 0 and 1.

### Stage 0 — Cascade organization removal

**Ship this first, on its own.** Widen `OrganizationService.RemoveMemberAsync` to delete, in the same
transaction: team memberships for every team in the org, role assignments at every team and project
in the org, and the existing org-scope rows. Bump the user's RBAC version stamp once at the end.

Independent of everything below, closes §3.1, roughly half a day.

### Stage 1 — `IAccessResolver` behind the existing interface

One component computes a user's entire effective access in a single pass — org memberships, team
memberships, role assignments, joined through `Project.AssignedTeamId` — and returns an immutable
`AccessSnapshot`. Reimplement `HasRoleAsync` on top of it.

Why this ordering: **all ~60 call sites stay untouched**, the §4.1 derivation lands in one place, and
caching collapses from one key per (user, scope, rank) to one key per user, reusing the version-stamp
machinery already in `CachingRbacService`. Closes §3.2, §3.3, §3.7, and the auditor's sweep cost in
§3.8.

Keep the in-memory rank comparison (§1.2) and all three `CachingRbacService` behaviours listed there.

### Stage 2 — Permission vocabulary and team positions

Introduce the §4.3 permission constants and the role→permission map. `AccessSnapshot` moves from one
role per scope to a set. Add `HasPermissionAsync`; migrate call sites module by module; delete
`HasRoleAsync` when the last one moves. The three positions, their appointment and transfer endpoints
(§4.2.1) and the scope-typed check constraint land here. Closes §3.2 and §3.4.

**Merge this with Stage 3.** They were separate on the assumption that the vocabulary could land
before the enforcement mechanism, but both rewrite the same ~60 call sites, and migrating each one
twice — first to `HasPermissionAsync`, then to `[RequirePermission]` — is pure waste. Doing them
together also means the reflection test exists at the moment the call sites move, which is exactly
when a missed guard is most likely.

### Stage 3 — Declarative enforcement, and the test that matters

An endpoint filter driven by an attribute:

```csharp
[RequirePermission("workitem:write", From = "projectId")]
```

Two things fall out. The scope resolves *before* the handler runs, so authorization precedes the
fetch and both denial and absence return 404 — closing §3.6. And a reflection test can assert that
every non-`[AllowAnonymous]` action carries the attribute, which is the permanent answer to §3.5 and
worth more than any number of hand-written authorization cases.

This stage creates the test project the repo currently lacks.

### Stage 4 — Principals

`PrincipalType`, integration tokens, and the agent intersection rule from §4.4. Required before git
sync ships, not before it is designed. Closes §3.9.

---

## 6. Cache invalidation under the new model

The existing per-user version stamp stays correct for role and membership changes. Stage 1 adds one
case it does not cover.

**`Project.AssignedTeamId` reassignment invalidates the snapshot of everyone on both teams.**
`ProjectService.AssignTeamAsync` already emits `ProjectTeamAssigned` carrying both `previousTeamId`
and `teamId` (`:163`) — the payload the hook needs is already there.

Two options:

1. Bump each affected member's stamp on reassignment. Reassignment is rare and the loop is bounded by
   team size. **Recommended.**
2. Fold a per-team stamp into the key. Cheaper on reassignment, but every check then reads two
   counters, which is the wrong trade for the ratio involved.

Related, and currently missing: `AccessChangeHandlers` does **not** handle `ProjectTeamAssigned`. Add
it in Stage 1, or reassigning a project's team will leave the previous team's members subscribed to
its realtime topic until the periodic sweep catches them.

---

## 7. Open decisions

| # | Question | Status |
|---|---|---|
| 1 | On org removal, delete team memberships outright or soft-mark for restore on re-add? | **Decided: delete.** Shipped in Stage 0. |
| 2 | Does team membership confer `Contributor` (write) on *all* the team's projects? | **Decided: yes, flat `Contributor`.** Shipped in Stage 1. |
| 3 | Should the auditor keep failing open (§3.8)? | **Decided: yes**, with the visibility changes in §10.3. |
| 4 | Do reports produced by the inference layer inherit project permissions, or get their own? | Open. Inherit `project:read` initially; revisit when reports aggregate across projects. |

Open before Stage 2 can start:

| # | Question | Status |
|---|---|---|
| 5 | Which scope do Scrum Master and Product Owner sit at? | **Decided: team scope**, both. Project-scope Product Owner was proposed and rejected — see the note below. |
| 6 | Does `sprint:scope` gate everything entering a sprint? | **Decided: no** — decomposition of committed work stays open to team members. See §4.3.1. |

**Consequence of team-scoped Product Owner.** One team holds several projects and runs one sprint,
so a single Product Owner owns the backlogs of every project that team works on. That is the
intended model. The case it cannot express is two products under one team needing different backlog
owners; if that arises the answer is to split the team, not to add a second Product Owner, because
the sprint they would share is a single team-level commitment either way.

Still open before Stage 2 can start:

| # | Question | Recommendation |
|---|---|---|
| 7 | One holder per team position, or several? | **One**, with transfer as an atomic act (§4.2.1). "Transferable when someone is unavailable" implies a handover, and single-holder makes "who is the Scrum Master" answerable. Easy to relax later; hard to tighten. |
| 8 | Can a position be held by someone who is not a team member? | **No** — reject with 400, matching the org-membership precedent in `ProjectsController.AssignRole`. |
| 9 | Are there positions beyond Team Lead, Scrum Master and Product Owner? | Needs an answer before the enum is fixed — adding one later is a migration. |

---

## 8. Checklist

**Stage 0** — shipped
- [x] `RemoveMemberAsync` cascades team memberships, team roles and project roles
- [x] Single version-stamp bump after the cascade
- [x] Last-OrgAdmin guard (found during implementation, see §9.1)

**Stage 1** — shipped
- [x] `IAccessResolver` + `AccessSnapshot`, two indexed queries per user
- [x] §4.1 derivation rules, including the team-membership → project edge
- [x] `HasRoleAsync` reimplemented over the snapshot; call sites untouched
- [x] Cache key collapses to one entry per user; all three cache behaviours preserved
- [x] `ProjectTeamAssigned` handled in `AccessChangeHandlers`

**Stage 2**
- [ ] Permission constants and role→permission map
- [ ] `TeamLead`; `Reader`/`User` retired
- [ ] Scope/role check constraint
- [ ] Call sites migrated module by module; `HasRoleAsync` deleted

**Stage 2** — shipped
- [x] Permission constants and role→permission map; `AccessSnapshot` holds role sets
- [x] `TeamLead`, `ScrumMaster`, `ProductOwner`; appointment, transfer and vacancy endpoints
- [x] Scope/role check constraint and one-holder-per-position index
- [x] All 56 call sites migrated; `HasRoleAsync` deleted

**Stage 3** — shipped
- [x] `[RequirePermission]` endpoint filter, scope resolved pre-handler
- [x] Scope resolver registry for ids that are not scopes
- [x] 404 when the caller cannot see the scope, 403 when they can (§9.5)
- [x] Test project created — 357 tests
- [x] Reflection test: every action declares its authorization, with justified exemptions

**Stage 4**
- [ ] `PrincipalType`; `Guid.Empty` made explicitly invalid
- [ ] Integration principals and tokens
- [ ] Agent permission intersection; proposal-and-approve flow for planning

---

## 9. What shipped in Stages 0 and 1

### 9.1 Deviations from the plan above

**A last-OrgAdmin guard was added to `RemoveMemberAsync`.** Not in the §3 defect list — it surfaced
while writing the cascade. `SetMemberRoleAsync` already refused to demote the last OrgAdmin, but
`RemoveMemberAsync` had no equivalent guard, so removing that person outright left the organization
permanently unadministerable. The guard runs inside the transaction, matching `SetMemberRoleAsync`,
so two concurrent removals cannot each see the other's admin and both succeed.

**Positions in the scope tree are not cached, and §6 is therefore moot.** The plan assumed the
snapshot would need invalidating when a project moved to another team. It does not: a snapshot holds
*grants*, and reassigning a project changes nobody's grants — it changes the tree they are
interpreted against. That left only the question of caching the tree itself, which was tried and
removed. A project's parent is a primary-key lookup already collapsed by the per-request memo, and
caching it distributed would buy very little at the price of a real hole: every other instance would
keep serving the old parent out of its own L1 until that copy expired, with no way to reach in and
evict it. A grant is safe to cache because its generation counter can be advanced atomically; the
tree has no such counter, so it is not cached.

Neither option offered in §6 was therefore needed. What *is* needed is the realtime half, which
shipped: `AccessChangeHandlers` now handles `ProjectTeamAssigned` and announces for the members of
both teams, since a project moving out from under them revokes nothing they hold and would otherwise
go unnoticed.

**`CachingRbacService` and `MemoizingRbacService` were replaced rather than modified.** Caching moved
down a layer, onto the grants themselves, so the decorators now wrap `IAccessResolver`:

```
IRbacService     InvalidatingRbacService → RbacService
IAccessResolver  MemoizingAccessResolver → CachingAccessResolver → AccessResolver → repository
```

`InvalidatingRbacService` keeps the write-side responsibility the old decorator had — advance the
generation, drop the per-request memo — and is registered in **both** configurations, with a
`NullAccessCacheVersion` when Redis is absent. Without that, a write in a Redis-less deployment would
leave the request answering from a memo of grants it had just changed.

The three behaviours §1.3 flagged as load-bearing all carried over to `CachingAccessResolver`:
transaction bypass, fall back to the database when the generation cannot be read, and never swallow
an invalidation failure.

### 9.2 Verification

Against a live stack — Postgres and Redis on `docker-compose.dev.yaml`, real HTTP through the API.

| Property | How it was proven |
| --- | --- |
| Team membership grants project access | A team member reads and creates work items on the team's project (**the new edge**) |
| Membership is contribution, not administration | The same member gets 403 renaming the project |
| Organization membership alone grants nothing | An org member not on the team gets 403 on the project |
| Outsiders denied | A user in no organization gets 403 |
| OrgAdmin inheritance intact | The owner reads the project and administers the team |
| Removal cascades | After org removal: 403 on the project, 403 on the team, membership row gone |
| Revocation is immediate | That 403 landed on the request after removal, against a 5-minute cache TTL |
| Last OrgAdmin protected | The sole admin cannot remove themselves — 400 |
| Access follows the tree | Reassigning a project moved access from team 1 to team 2 **with no grant touched** |
| Cache shape changed | Redis holds one `rbac:v3:snap:{user}:{gen}` per user, no per-decision keys |
| No handler regressions | Zero undispatched outbox rows after every scenario |

17 checks, all passing. Scripts in the session scratchpad (`verify.sh`, `verify-reassign.sh`).

**Not covered:** the subscription-revocation path was exercised only as far as the announcement —
the tests hold no live SignalR connections, so no subscription was actually dropped. To be verified
against the frontend.

### 9.3 The §3.10 fix

Two changes, both small, neither dependent on Stage 2.

**`SprintService.AddWorkItemAsync` now authorizes the work item, not just the sprint.** The item's
project must be assigned to the sprint's team. A sibling project of the same team is legitimate — one
team, one sprint, several projects — anything else is not. Rejected as `NotFoundException` rather
than `ForbiddenException`: the caller cannot see this work item, and answering "forbidden" would
confirm the id names something real. This is §3.6's principle applied at the one place it currently
matters most.

**`GetCardsForSprintAsync` is filtered to the board's project.** Defence in depth against the same
class of bug — but also a correctness fix in its own right. A sprint is team-scoped and a board is
project-scoped, so with several projects per team a sprint legitimately holds items belonging to
other boards. Before this, every project's board rendered its siblings' cards.

Verified with the original exploit, which now fails at step one:

| Step | Before | After |
| --- | --- | --- |
| Attacker reads victim's work item directly | 403 | 403 |
| Attacker lists victim's project work items | 403 | 403 |
| Attacker adds victim's item to their own sprint | **201** | **404** |
| Victim's title on attacker's sprint backlog | **leaked** | absent |
| Victim's title on attacker's board | **leaked** | absent |

The Stage 0 and Stage 1 suites were re-run against the same build: 12/12 and 5/5, no regressions.
22 checks green in total.

### 9.4 Stage 2 — what shipped

`HasRoleAsync` is gone. All 56 guards now name a permission, `RolePermissions` is the single table
saying who may do what, and the three positions exist and transfer.

**Deviations worth recording:**

*`Reader` was not renamed and `User` was not removed.* Both were slated to retire in §4.2. Renaming
`Reader` to `Viewer` is churn across 30 call sites and a data migration, for a word; `User` is
declared but never assigned, so removing it only risks an old row failing to parse. Both stay, with
`User` documented as legacy. The permission table now carries the meaning, so the names matter less
than they did.

*Organization-scope `ProjectAdmin` and `TeamMember` are no longer assignable — a breaking change.*
`AssignableOrgRoles` accepted them and they granted nothing beyond organization read, because only
OrgAdmin ever inherited downwards. Under a permission table an unmapped pairing grants nothing
*silently*, which turns a misleading option into a trap: an administrator sets someone to
"ProjectAdmin" at org level and hands them nothing. The migration rewrites existing such rows to
`Reader` — exactly the power they actually had — and a check constraint now rejects them.

*The scaffolder wanted to add an `xmin` column.* It is Postgres' own system column, mapped as a
concurrency token and deliberately backed by no column of ours. Left out of the migration; the model
snapshot was corrected so it stops being re-scaffolded.

**Found in passing, not fixed:** `WorkItemService.cs:50` calls `Enum.TryParse` for the work item type
and discards the result, so an unrecognised type silently becomes `Epic` (value 0) rather than being
rejected. Outside this work's scope, but it is a validation hole that will produce confusing data.

**Verified**, against Postgres and Redis with real HTTP — 44 checks, all passing:

| Property | How it was proven |
| --- | --- |
| Positions start vacant | All three listed with no holder on a new team |
| Appointment requires membership | Appointing a non-member → 400 |
| Bogus positions rejected | `PUT .../positions/Wizard` → 400 |
| **Scrum Master can run sprints** | Was OrgAdmin-only (§3.2); a plain member 403s, the appointed Scrum Master creates and starts one |
| Positions are not ranks | A plain team member still 403s while the Scrum Master succeeds |
| Transfer is atomic and singular | Bob → Carol: Carol gains, Bob loses, **exactly one row** in the database |
| A holder may hand over | Carol transfers her own position back without `team:role:assign` |
| Vacating works | Position cleared, holder loses sprint management, OrgAdmin unaffected |
| **Creating work is never gated** | A plain member still creates work items freely |
| `sprint:scope` holds | A plain member cannot commit a loose task to the sprint → 403 |
| **Decomposition is exempt** | The same member adds a child of a committed story → 201 |
| No regressions | Stages 0/1, reassignment and the §3.10 exploit suites all still green |

### 9.5 Stage 3 — declarative enforcement and the test project

Guards moved from the first line of an action body onto its signature:

```csharp
[HttpPut("api/sprints/{sprintId:guid}")]
[RequirePermission(Permissions.SprintManage, From = "sprintId")]
```

**The scope resolver registry** is what made this possible. `From` names a route parameter, and an
`IScopeResolver` turns it into a scope — `workItemId → its project`, `sprintId → its team`,
`commentId`/`linkId`/`columnId` one hop further out. Each resolver is registered by the module that
owns the data it walks, so this did not become a place where every module's queries accumulate.
`orgId`, `teamId` and `projectId` resolve to themselves.

**On 403 versus 404.** §3.6 asked for 404 on both denial and absence. Applied bluntly that is wrong:
someone who can see a project but lacks `project:admin` knows perfectly well it exists, and answering
"not found" to their rename makes the UI unexplainable. The rule shipped splits on **visibility**:

| Situation | Answer |
| --- | --- |
| Scope does not exist | 404 |
| Caller cannot read the scope | 404 — indistinguishable from the above, which is the disclosure this closes |
| Caller can read it but lacks the permission | 403 — true, and actionable |

**The coverage test is the durable part.** `EveryEndpointDeclaresItsAuthorization` enumerates the
controller surface by reflection and fails for any action that declares nothing, so it covers
endpoints nobody thought to write a case for. Three more tests keep the declarations honest:
permissions must be real constants (a typo is a permission nobody holds — it fails closed, silently,
and looks like a broken feature), the named route parameter must actually be in the route, and every
exemption must state a reason.

Exemptions are two attributes, deliberately not one. `[NoPermissionRequired]` means no check is
needed — self-scoped endpoints like `GetMe`, `GetMyOrgs`, or `Search`, whose results are scoped
inside the query to the caller's own organizations. `[PermissionCheckedInAction]` means a real check
exists that an attribute cannot express: the sprint-scope rule, which depends on the item's parent,
and position assignment, which permits the current holder. Conflating them would make the exemption
list useless for telling which endpoints still deserve scrutiny.

**What the test found on first run:** eight endpoints with no authorization declared at all —
`AuthController`'s six self-scoped actions and `WorkspaceController`'s two aggregates. All were
genuinely fine (each scopes to the caller's own id), but none of them said so, and nothing would have
noticed if one had been wrong. That is the point.

**Verified:** 357 unit tests and 44 integration checks, all passing. The integration suites needed
six expectations changed from 403 to 404 — every one a caller who could not see the scope at all
(outsiders, revoked users, org members on no team). No case where a caller who *could* see the
resource wrongly received 404, and the "visible but insufficient" 403s still pass unchanged.

### 9.6 §3.11 — user lookup by email, restricted

`UsersController.GetByEmail` returned a `UserProfile` for **any** email to **any** authenticated
caller. It documented itself as "used by OrgAdmins when inviting users" and checked for no such
thing. The effect was a cross-tenant directory: anyone with an account could confirm whether
`someone@a-competitor.com` was a user here and read their name.

It now requires `org:member:manage` — the people who can actually invite.

**Why an "anywhere" check.** There is no organization to check against: the person being looked up is,
by definition, not yet in yours, and adding an `orgId` parameter would prove nothing extra, since an
admin of org A looking up a stranger is exactly the legitimate case. So the question the endpoint can
answer is "does this caller administer members somewhere", and
`[RequirePermissionAnywhere(Permissions.OrgMemberManage)]` asks precisely that.

This is a **weaker** guarantee than every other check in the system, and the attribute's own remarks
say so. It narrows the audience from every authenticated account to organization administrators; it
does not give tenant isolation, because the lookup's whole purpose is finding people outside your
tenant. Reach for it only where no scope exists.

Denial is 403 whether or not the address belongs to a real user, so a refused caller learns nothing —
verified.

**`GetById` was left open.** Resolving a user id to a profile is different in kind: ids are not
guessable and they appear throughout the API as assignees, authors and members, so every screen that
renders a name depends on it. It keeps `[NoPermissionRequired]` naming this section.

**Verified**, 6 checks: an org admin may look up (invite flow intact); a user belonging to nothing may
not; a plain team member may not; **a Team Lead may not** — they manage team members, not organization
members, and the two permissions must not blur; promoting that same person to OrgAdmin grants it
immediately; and a real address and a fake one return the same 403.

---

## 10. Recommendations on the remaining defects

§3.1, §3.2 (partly), §3.3 and §3.7 are closed. What is left, and what to do about each.

### 10.1 §3.5 — enforcement by convention, no tests · **closed in Stage 3, see §9.5**

The highest-value remaining item, and the one most likely to produce the *next* hole rather than to
be one today. The fix is the reflection test described in Stage 3: enumerate every controller action,
assert each non-`[AllowAnonymous]` one carries a permission attribute. That test is worth more than
any number of hand-written authorization cases, because it fails for endpoints nobody thought to
write a case for.

It cannot land before Stage 2 — there is no attribute to assert on yet — which is the main reason for
merging the two stages.

### 10.2 §3.6 — fetch-then-authorize leaks existence · **fold into Stage 2/3, low urgency**

Real but minor: it distinguishes "exists but forbidden" from "does not exist" for anyone already
authenticated. It falls out for free once `[RequirePermission(..., From = "projectId")]` resolves the
scope before the handler runs, so it needs no separate work — just the decision to return `404` for
both denial and absence when that lands. Worth doing at the same time because changing it later is a
breaking change for clients that switch on the status code.

### 10.3 §3.8 — auditor fails open, sweeps serially · **keep the behaviour, add visibility**

Do **not** change fail-open. A database blip must not mass-disconnect every client from every topic,
and the periodic sweep re-checks shortly afterwards. But it is currently indistinguishable from
working: a persistent failure logs a warning per connection per sweep and nothing else.

Three small changes, in order of value:

1. Record the choice in the class remarks as a decision rather than leaving it to be inferred — it
   is the opposite posture to `CachingAccessResolver`, which fails closed, and the asymmetry is
   deliberate.
2. Emit a counter for re-authorization failures alongside the existing revocation count, so a sweep
   that is failing rather than finding nothing is visible on a dashboard.
3. Parallelise the sweep with bounded concurrency. **Not yet** — it is `connections × topics`
   sequential awaits, which is fine at current connection counts and would be premature to optimise
   before there is a number to point at.

### 10.4 §3.9 — a principal is a bare `Guid` · **minimal part done, rest with git sync**

The dangerous half is already closed: `AccessResolver.GetSnapshotAsync` now returns
`AccessSnapshot.Empty` for `Guid.Empty` explicitly, so the safe default is stated rather than an
accident of no rows matching.

The full typed-principal model should wait for the git integration that needs it. Building
`PrincipalType` now, with only one kind of principal in existence, means guessing at the shape of the
integration and agent cases before either exists. The §4.4 split — authority belongs to the
integration, attribution is metadata — is the part worth holding onto, and it is a design commitment
rather than code.

### 10.5 Sequencing

```
Stage 2+3 merged   permission vocabulary, team positions, [RequirePermission], test project
   └─ closes §3.2 fully, §3.4, §3.5, §3.6
Stage 4            typed principals, when git sync starts
   └─ closes §3.9
ongoing            §3.8 visibility (items 1–2 now, item 3 when connection counts justify it)
```

---

## 11. Amendment: sprints belong to projects (`Stage4_SprintBelongsToProject`)

Sprints moved from team scope to project scope. What changed, and why the authorization layer had to
change with it:

| | Before | After |
|---|---|---|
| Owner | `Sprint.TeamId` | `Sprint.ProjectId`, with a foreign key to `org.Projects` |
| Scope resolved for `sprintId` | `RoleScope.Team` | `RoleScope.Project` |
| `sprint:read`, `sprint:order` | team roles | project contribution (and so team roles, via the team → project edge) |
| `sprint:manage`, `sprint:scope` | `TeamLead`, `ScrumMaster`, `ProductOwner` at team scope | project administration, **and** `ScrumMaster` / `ProductOwner` via the team → project edge |
| Sprint realtime topics | `Topic.Team` | `Topic.Project` |
| Sprint activity rows | filed under `teamId` | filed under `projectId`, as board rows already were |

**Where sprint authority lives.** `ScrumMaster` and `ProductOwner` grant only `team:read` at team
scope — a sprint is not a team-scope object any more, so there is nothing else for them to hold
there. Their authority travels down the team → project edge instead: both carry `sprint:manage` and
`sprint:scope` onto *every project their team serves*, so running the sprint follows the appointment
rather than requiring a separate grant per project.

This is the one place the team → project edge is deliberately not flat, and it stays narrow: sprint
authority, never project administration. A Scrum Master cannot rename the project, reconfigure its
board, delete work items, or grant anyone a role on it — `RunningTheSprintIsNotAdministeringTheProject`
in `AccessEvaluatorTests` pins that boundary, and
`ScrumMasterOfOneTeamRunsNothingOnAnotherTeamsProject` pins that it reaches only their own team's
projects.

`TeamLead` was left as a plain contributor on the team's projects. Under the old team-scoped model it
carried the sprint rights too, on the reasoning that a lead standing in for an absent Scrum Master is
ordinary rather than an escalation; that reasoning still applies, so if you want it back it is the
same one-line change applied to `TeamToProject[TeamLead]`.

**The migration is self-checking.** `AddSprintProjectId` renamed the column without translating its
values, so any environment holding sprints from before it now has team ids in `ProjectId` — which
resolves to a project nobody has a grant on and denies everyone. Stage 4 remaps those rows, refuses
to guess when a team serves anything other than exactly one active project, and only then adds the
foreign key, so an incomplete remap fails the deploy instead of silently disabling the module.
