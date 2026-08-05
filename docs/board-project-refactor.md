# Audit: Tying Boards to Projects & Completing the Team Module

**Date:** 2026-08-05
**Branch:** `refactor/modules`
**Baseline commit:** `5662748` — *Refactor orgProj module to make team belong to organization rather than project*

This document records a health check of the BoardSync API, the defects it surfaced,
the reasoning behind each fix, and how the result was verified.

---

## 1. Context: what the refactor was trying to do

Commit `5662748` moved `Team` from belonging to a **Project** to belonging to an
**Organization**. That change was only half-landed across the codebase, which is the
root of most of what follows.

The intended domain model, reconstructed from the model classes and `build_context.md`:

```
Organization
├── Team          (many)   — Team.OrganizationId
└── Project       (many)   — Project.OrganizationId
     └── AssignedTeam (exactly one)  — Project.AssignedTeamId → Team
          └── Sprint (many)          — Sprint.TeamId
Board  ── one per Project ──  Board.ProjectId
```

The two key consequences, which the code did not yet reflect:

- **A team can serve several projects**, and a project has exactly one team.
  Moving teams up to the org level is what makes this possible — under the old model a
  team was trapped inside a single project.
- **Boards are project-scoped, sprints are team-scoped.** The board's cards therefore
  come from the active sprint of *the project's assigned team*. That hop is the join
  between the two halves of the model.

> **Note on the PRD.** `build_context.md` §3.6 specifies boards as per-team
> (`GET /teams/:id/board`, "BoardSettings (per team)"). That predates the
> Team→Organization refactor. The direction taken here — project-scoped boards — follows
> the current model and the explicit instruction to tie boards to projects. The PRD
> section is now stale and should be updated to match.

---

## 2. Method

1. Read every model, service, repository, and controller in the affected modules.
2. Compile the project — which immediately exposed that it *did not* compile.
3. Diff the EF Core model snapshot against the migration history.
4. Apply the full migration chain to a throwaway Postgres database to see what the schema
   actually becomes, rather than trusting the migration files.
5. Reproduce each suspected defect before fixing it.
6. Re-verify: compile, migrate a clean database, boot the app, inspect the route table.

A scratch database (`boardsync_healthcheck`) was used for all destructive testing and
dropped afterwards. The developer database was only read from until the final,
verified migration was applied.

---

## 3. Defects found

### 3.1 The project did not compile — `BoardSyncDbContext.cs:305`

```csharp
entity.HasIndex(b => b.TeamId).IsUnique(); // one board per team
```

`Board` has no `TeamId`; it has `ProjectId`. This is a hard compile error.

It went unnoticed because `dotnet build` on an unchanged tree is a no-op that reports
"Build succeeded" from cached output, and every `dotnet ef` invocation used `--no-build`,
which loads the last successfully-compiled assembly. **Every tool in the loop was
reporting on a stale binary.**

**Fix:** indexed `ProjectId` instead, and added the missing FK to `org.Projects`.

### 3.2 Fetching a board threw `NotImplementedException`

`BoardService` contained the correct, complete, project-scoped implementation —
`GetOrCreateForProjectAsync` — but it was **not on the interface**. `IBoardService`
instead declared `GetOrCreateForTeamAsync`, which the class satisfied with:

```csharp
public Task<BoardResponse> GetOrCreateForTeamAsync(...) => throw new NotImplementedException();
```

`BoardsController` called the stub. Every `GET .../board` request threw. The working
method was unreachable dead code.

**Fix:** put `GetOrCreateForProjectAsync` on the interface, deleted the stub and the
block of commented-out variants below it, and pointed the controller at it.

### 3.3 Board authorization checked the wrong scope

```csharp
await RequireTeamRoleAsync(projectId, RoleType.Reader, ct);   // a project ID…
// → _rbac.HasRoleAsync(userId, minimum, RoleScope.Team, teamId, ct)   // …used as a team ID
```

Project IDs were passed into `RoleScope.Team` permission checks, in the board endpoint and
in the column helper (which resolved `column → board → ProjectId` and then called it
`teamId`). Because role rows are keyed by scope column, these checks matched nothing and
fell through to the `OrgAdmin` escalation path — so board access silently became
*OrgAdmin-only*, and the intended Reader/ProjectAdmin distinction never applied.

**Fix:** all board authorization now uses `RoleScope.Project` with the board's real
`ProjectId`.

### 3.4 The board could never find its sprint

```csharp
.Where(s => s.TeamId == board.ProjectId && s.Status == SprintStatus.Active)
```

A project ID compared against a team ID. `activeSprint` would be null in practice, so the
board always rendered with empty columns.

**Fix:** resolve the project's assigned team first, then find that team's active sprint —
the project → team → sprint hop described in §1.

### 3.5 `Project.AssignedTeamId` was wired to nothing

The property existed on the model and was configured as a **required, restricting FK**,
but appeared in no DTO, no service, and no endpoint. `ProjectService.CreateAsync` never
set it, so every insert would have sent `Guid.Empty` and been rejected by the foreign key.
**Project creation was going to fail outright** once the pending migration landed.

Relatedly, `ProjectResponse.TeamCount` was hardcoded to `1` with the real query commented
out — a leftover from when a project owned many teams.

**Fix:** `AssignedTeamId` is now a required field on `CreateProjectRequest`, validated to
be an active team *in the same organization* (a 404 rather than a foreign-key 500, and it
blocks cross-organization assignment). `ProjectResponse` now carries
`AssignedTeamId`/`AssignedTeamName` instead of the meaningless count. Added
`PUT /api/projects/{projectId}/team` to reassign.

### 3.6 Teams were created against the wrong parent

`TeamsController.Create` was routed `POST /api/projects/{projectId}/teams` and passed
`projectId` straight into `TeamService.CreateAsync(Guid orgId, …)`, which assigned it to
`Team.OrganizationId`. **Every team created this way would have stored a project ID in its
organization column** — silent data corruption, and the permission check was
project-scoped for what is now an organization-level resource.

**Fix:** moved to `POST /api/orgs/{orgId}/teams`, requiring `OrgAdmin` on the organization,
and the service now verifies the organization exists.

### 3.7 Team names collided across organizations

```csharp
var existing = await _teamRepo.GetByNameAsync(name, ct);   // no org filter
if (existing != null) throw new ConflictException($"A team named '{name}' already exists in this project.");
```

A global name lookup. One organization naming a team "Platform" would block **every other
organization** from doing the same — a cross-tenant leak, and the error message still said
"in this project".

**Fix:** `GetByNameInOrgAsync(orgId, name)`, filtered to the organization and to active
teams, with a corrected message.

### 3.8 Every team reported zero members

`MapToResponseAsync` read `t.Members.Count`, but `GetActiveByIdAsync` loads a team without
including its memberships, so the collection was always empty. A repository method that
counts memberships properly (`GetMemberCountAsync`) already existed but was absent from the
interface and unused.

**Fix:** promoted it to the interface and used it in the mapper.

### 3.9 Service methods that no endpoint could reach

| Method | State before |
|---|---|
| `TeamService.GetByOrgIdAsync` | Implemented, **missing from `ITeamService`** — unreachable |
| `ITeamService.IsMember` | On the interface, no endpoint (one internal caller in `WorkItemService`) |
| `ITeamRepository.Delete` | No caller anywhere — no way to remove a team |
| `TeamRepository.GetMemberCountAsync` | Implemented, missing from the interface |

**Fix:** all four are now reachable. See §4 for the endpoints.

### 3.10 Duplicate `MemberAddedToTeam` events

`AddMemberAsync` published the event unconditionally, including on the idempotent path
where the membership already existed. Subscribers saw a join that never happened.

**Fix:** the event is published only when a membership row is actually created.

### 3.11 Assignee validation returned a useless error

```csharp
if(!await _teamService.IsMember(...)) throw new InvalidOperationException("Assigned member does not belong to team");
```

`InvalidOperationException` maps to a bare `400 "Invalid operation"` in
`ExceptionHandlingMiddleware`, discarding the message. **Fix:** `BusinessRuleException`,
which maps to `422` and preserves the reason, now naming the user and team.

---

## 4. Endpoints added or changed

| Method | Route | Status | Auth |
|---|---|---|---|
| `GET` | `/api/projects/{projectId}/board` | **Fixed** — was `/api/project/…` (singular) and threw `NotImplementedException` | Reader on project |
| `GET` | `/api/orgs/{orgId}/teams` | **New** — wires the orphaned `GetByOrgIdAsync` | Reader on org |
| `POST` | `/api/orgs/{orgId}/teams` | **Moved** from `/api/projects/{projectId}/teams` | OrgAdmin on org |
| `DELETE` | `/api/teams/{teamId}` | **New** — archives (soft-deletes) a team | ProjectAdmin on team |
| `GET` | `/api/teams/{teamId}/members/{userId}` | **New** — wires `IsMember` | Reader on team |
| `PUT` | `/api/projects/{projectId}/team` | **New** — reassign a project's team | ProjectAdmin on project |
| `POST` | `/api/orgs/{orgId}/projects` | **Changed** — request now requires `assignedTeamId` | OrgAdmin on org |

Archiving is a soft delete (`IsActive = false`), not `ITeamRepository.Delete`. Projects
hold a **restricting** FK to their assigned team, so a hard delete would either fail or
orphan projects. The endpoint refuses with `422` when the team still has active projects
assigned, naming the count.

### Breaking API changes for clients

- `POST /api/orgs/{orgId}/projects` now **requires** `assignedTeamId`. Create the team first.
- `ProjectResponse.teamCount` is **replaced** by `assignedTeamId` + `assignedTeamName`.
- `BoardResponse.teamId` **no longer means the board's scope**. The board is identified by
  the new `projectId`; `teamId` is the assigned team the sprint was resolved through.
- `POST /api/projects/{projectId}/teams` is **gone** — use `POST /api/orgs/{orgId}/teams`.
- `GET /api/project/{projectId}/board` (singular) is **gone** — use `/api/projects/…`.

---

## 5. Migrations

### The pre-existing failure

The migration `20260805192051_HardenRoleAssignmentAndOrgMembership` was desynchronized
from its own snapshot. Its `Designer.cs` and `BoardSyncDbContextModelSnapshot.cs` described
the full new model, but its `Up()` contained only three statements — all dropping a column
`OrganizationId1` that **no migration in the history ever created**. Applied to a clean
database it failed hard:

```
ERROR: constraint "FK_Projects_Organizations_OrganizationId1" of relation "Projects" does not exist
```

`--idempotent` did not help: the guard checks `__EFMigrationsHistory`, not column existence.

### Root cause — and how to avoid it

**`dotnet ef migrations add --no-build` diffs the model against the snapshot compiled into
the stale assembly, not the `.cs` snapshot on disk.** Run `add` twice without an
intervening build and the second run cannot see what the first one wrote — it re-emits the
same diff while the on-disk snapshot marches ahead. The model and the migration history
drift apart silently, and `has-pending-model-changes` then reports "no changes" because it
is reading the same stale snapshot.

This was reproduced during this audit: a scratch migration generated with `--no-build`
came out byte-identical to the one added immediately before it.

> **Rule: always `dotnet build` before any `dotnet ef migrations add` or `remove`, and do
> not pass `--no-build` to them.** Reserve `--no-build` for read-only commands like
> `script`. Never delete a migration file by hand — use `dotnet ef migrations remove`,
> which reverts the snapshot too.

### Current state

`HardenRoleAssignmentAndOrgMembership` was regenerated (as `20260805194114`) and applied.
It correctly performs the `Team.ProjectId → OrganizationId` move, splits
`RoleAssignment.ScopeId` into the three nullable scope columns, and adds the
`CK_RoleAssignment_ExactlyOneScope` check constraint.

It also hand-writes three **partial** unique indexes in raw SQL:

```sql
CREATE UNIQUE INDEX "IX_RoleAssignments_Unique_Org"
  ON iam."RoleAssignments" ("UserId","Role","OrganizationId") WHERE "OrganizationId" IS NOT NULL;
-- and _Project, _Team likewise
```

This is the right way to enforce the constraint. A plain composite unique index over the
three nullable columns would be **inert**, because Postgres treats `NULL`s as distinct and
exactly two of the three columns are always `NULL` — such an index accepts unlimited
duplicates. That is why the composite `HasIndex` is commented out in `BoardSyncDbContext`;
a comment now records the reason so it is not "restored" by mistake.

One new migration was added by this work:

**`20260805205038_TieBoardsToProjects`**
- renames `plan.Boards.TeamId` → `ProjectId` (carrying its unique index across)
- adds `FK_Boards_Projects_ProjectId` → `org.Projects(Id)`, `ON DELETE CASCADE`

Because the unique index survives the rename, the "one board per project" invariant is
enforced by the database.

---

## 6. Verification performed

| Check | Result |
|---|---|
| `dotnet build` from a clean state | Succeeds, 0 warnings |
| `dotnet ef migrations has-pending-model-changes` (after a real build) | No changes — snapshot and model agree |
| Full 6-migration chain applied to an empty database | `psql` exit 0, no errors |
| Resulting schema | `plan.Boards` has `ProjectId` + FK + unique index; `org.Teams` has `OrganizationId`; `org.Projects` has `AssignedTeamId`; `iam.RoleAssignments` has all three scope columns and the check constraint |
| Duplicate role assignment rejected | `ERROR: duplicate key value violates unique constraint "IX_RoleAssignments_Unique_Org"`, 1 row retained (this insert succeeded twice before the fix) |
| Developer database migrated | Now at `20260805205038_TieBoardsToProjects` |
| Application boots | Yes — DI graph resolves, `swagger.json` returns `200` |
| Route table | All seven endpoints in §4 present and correctly shaped |

The developer database was empty (0 rows in every table), so no data backfill was needed
and the column rename was safe. **A production database with existing rows would need a
backfill** for `Teams.ProjectId → OrganizationId`, for populating `Projects.AssignedTeamId`,
and for fanning `RoleAssignments.ScopeId` out into the three scope columns before the check
constraint could be added.

### Not verified

An end-to-end functional run (register → org → team → project → board) was started but
stopped before completion. The chain is verified structurally — it compiles, the schema is
correct, the routes register, and DI resolves — but **no board has been fetched through a
live HTTP request**. That is the recommended next check.

---

## 7. Open items

1. **No automated tests exist in this repository.** Every defect above would have been
   caught by a single integration test per endpoint. This is the highest-value follow-up.
2. **`build_context.md` §3.6 is stale** — it still specifies per-team boards and
   `GET /teams/:id/board`. It should be updated to match the project-scoped model.
3. **`ProjectAdmin` is never assigned at `RoleScope.Team`.** Team update, member management,
   and archiving all require it, so in practice only an `OrgAdmin` (via the escalation path
   in `RbacService.IsOrgAdminForScopeAsync`) can perform them. Either assign `ProjectAdmin`
   at team scope to team creators, or lower these checks to `TeamMember`. Left as-is
   because it is a policy decision, not a bug.
4. **Overlapping cascade paths.** Deleting an `Organization` cascades to both `Teams` and
   `Projects`, while `Projects.AssignedTeamId` is `RESTRICT`. Organization deletion may
   therefore fail depending on evaluation order. There is no delete-organization endpoint
   today, so this is currently unreachable — but it will bite when one is added.
5. **`CreateWorkItemRequest.AssigneeId` is a non-nullable `Guid`**, so an omitted assignee
   arrives as `Guid.Empty` and is rejected by the team-membership check with a confusing
   message. If unassigned work items should be legal, make it `Guid?` and skip the check
   when null.
