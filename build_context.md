# BoardSync — Build Context

**Replaces the previous `build_context.md`**, which described a generic Azure DevOps Boards clone on
an undecided stack and a seven-phase plan whose first five phases are already shipped in .NET. It
described a product BoardSync is no longer trying to be.

Companion documents:
[`docs/audit-2026-08.md`](docs/audit-2026-08.md) (defect register) ·
[`docs/permissions-model.md`](docs/permissions-model.md) (authoritative permission design) ·
[`docs/scaling-realtime-caching.md`](docs/scaling-realtime-caching.md) (outbox, realtime, caching) ·
`README.md` (operations)

---

## 1 · What BoardSync is

A task management system for software teams running Agile that **keeps itself up to date from the
work developers are already doing.**

Every other board in this category has the same failure mode: the board is a second system of record
that a human has to remember to update. Standup becomes "let me drag my cards," reports describe what
people remembered to record rather than what happened, and management's view of the project is a
lagging, optimistic fiction. Teams do not abandon these tools because the tools are bad — they
abandon them because keeping them honest is unpaid work.

BoardSync's answer: **git is the source of truth about what is being built, so the board is derived
from git.** A developer branches, commits, opens a PR, and merges. Each of those is a webhook. Each
webhook moves the card. Nobody drags anything.

Three things follow, and they are the product:

1. **Git-driven board state.** Push, PR, and merge events bind to work items and advance them
   through the workflow. The developer's only obligation is a branch name.
2. **A QA gate that git cannot cross.** Automation moves work up to *merged and awaiting test*.
   Only a human with testing authority certifies it Done. This is not a policy check bolted on top —
   it is enforced by the permission system, because the git integration is a principal that
   structurally lacks the permission to close anything. See §4.
3. **Intelligence over a board that is actually true.** A PRD becomes a proposed sprint plan; a
   sprint becomes a report. Both are only worth building because (1) and (2) make the underlying
   data trustworthy. A report generator over a board nobody updates is a confident lie generator.

### Non-goals

Source control hosting, CI/CD, wiki, test case management, a marketplace. BoardSync **integrates
with** git hosts; it does not become one. The moment it stores code it inherits their problems and
loses its only advantage, which is that it is cheap to adopt alongside what a team already uses.

---

## 2 · Where the system actually stands

The backend is substantially further along than the old build plan claimed, and the plan below starts
from what exists rather than from a phase number.

**Shipped and solid:**

| Area | State |
|---|---|
| Modular monolith, .NET 10 + Postgres + EF Core | 212 files, 18.3k lines, clean build |
| Auth | JWT + refresh, BCrypt, email confirmation, lockout, rate limiting with Redis-shared counters |
| RBAC | Named permissions, scope tree, snapshot caching with generation-counter invalidation. The best-designed part of the system — see §6 |
| Org / Team / Project | Full CRUD, memberships, team positions, slug uniqueness |
| Work items | CRUD, hierarchy, comments, history, links, tags |
| Sprints & boards | Project-scoped sprints, fractional-rank ordering, board columns, WIP limits |
| Backlog | Ranked product backlog, move-to-sprint |
| Kernel | Transactional outbox with `FOR UPDATE SKIP LOCKED`, multi-instance safe |
| Realtime | SignalR + Redis backplane, per-subscription authorization, resume protocol, presence |
| Observability | OpenTelemetry traces and metrics, no-op until an OTLP endpoint is set |

**Missing entirely:**

- **Git integration.** Not one line. `grep -i git` across the module tree returns only the word
  "commit" in prose about sprint commitments.
- **The frontend.** `boardsync-ui` is referenced by the Makefile, the production compose file, and
  the README, and does not exist. `make prod-build` cannot succeed.
- **AI.** No provider dependency, no module, no schema.
- **Notifications, in the sense the name implies.** See audit finding 10.

**Thirteen defects**, three of them S1, are catalogued in
[`docs/audit-2026-08.md`](docs/audit-2026-08.md). Three matter enough to restate here because they
gate everything below:

- **Search and notifications leak work items across the permission boundary** — org membership is
  treated as project access, which the permission model explicitly says it is not.
- **Optimistic concurrency is fully plumbed and completely inert** — `SetOriginalVersion` exists and
  is never called. Git sync makes concurrent writes routine rather than rare.
- **Every internal constant is hardcoded on the frontend** because no endpoint publishes it. This is
  §5, and it is the single highest-leverage fix in the document.

---

## 3 · Architecture: what to keep

**Keep the modular monolith.** It is the right shape and the reasons are specific, not stylistic:

- One deployable, one transaction boundary. The board update, the history row, and the outbox event
  commit together or not at all. Split this across services and that atomicity becomes a saga.
- The module seams are real. Each module owns its controllers, services, repositories, DTOs, and
  models; nothing reaches into another module's tables; cross-module traffic goes through domain
  events on the outbox. `IBacklogSprintLink` exists precisely so Backlog can talk to Sprints without
  taking a dependency on it. That discipline is what makes extraction possible later — and what makes
  it unnecessary now.
- The scaling ceiling is nowhere near. Postgres with correct indexes, Redis for cache and backplane,
  and N stateless API instances will carry this product well past the point where its business model
  is proven.

**Two new modules**, following the same rules as the existing ones:

```
Modules/
  GitSync/          §7 — provider port, webhook ingest, binding, transitions
  Intelligence/     §8 — PRD decomposition, report generation
```

**One new shared concern:**

```
Shared/Metadata/    §5 — the vocabulary endpoint that ends clientside hardcoding
```

**Schema-per-module continues:** `org`, `work`, `plan`, `iam`, `activity`, `kernel` → add `git` and
`ai`.

---

## 4 · The QA gate

*Decision: git may drive work as far as "merged, awaiting test." Only a human with testing authority
certifies Done.*

This reshapes both the state machine and the permission model, and it is the constraint that makes
the automation trustworthy. Full autonomy is what makes these systems unusable — a board that closes
tickets on merge is lying about anything that merged broken.

### 4.1 The state machine

Current (`WorkItemService.ValidateStateTransition`), with the hole from audit finding 3:

```
New → Active → Resolved → Closed
       └──────────────────┘         Active → Closed skips review entirely
```

Replacement — five states, each one a state a git signal can identify:

```
                 ┌──────────────── reopened (verify) ─────────────────┐
                 ↓                                                     │
   New ──────► Active ──────► InReview ──────► Resolved ──────► Closed
    ▲            ▲   ▲            │                │  │
    │            │   └────────────┘                │  │
    │            │      PR closed unmerged         │  │
    │            └───────────────────────────────── ┘  │
    │                   QA failed (verify)             │
    └──────────────────────────────────────────────────┘
                     reopened (verify)
```

| Transition | Trigger | Required permission |
|---|---|---|
| `New → Active` | first commit on a bound branch, or manual pickup | `workitem:write` |
| `Active → InReview` | pull request opened | `workitem:write` |
| `InReview → Active` | PR closed without merging, or changes requested | `workitem:write` |
| `InReview → Resolved` | PR merged into the project's default branch | `workitem:write` |
| `Active → Resolved` | manual — work needing no PR | `workitem:write` |
| **`Resolved → Closed`** | **QA certifies** | **`workitem:verify`** |
| **`Resolved → Active`** | **QA rejects** | **`workitem:verify`** |
| **`Closed → Active`** | **reopen** | **`workitem:verify`** |

`Active → Closed` is gone. Nothing reaches `Closed` except through `Resolved`, and nothing crosses
that edge without `workitem:verify`.

`Resolved` now means one specific thing — *merged, awaiting test* — rather than the vague "done-ish"
it means today. Say so in the UI. The label matters more than the enum name.

### 4.2 The `workitem:verify` permission

```csharp
/// <summary>
/// Certify that finished work meets its acceptance criteria, or send it back.
/// The only permission that reaches Closed.
/// </summary>
/// <remarks>
/// Deliberately not part of workitem:write. Writing the code and declaring it correct are different
/// authorities, and the whole value of the git integration rests on that separation: the integration
/// principal holds write and never holds this, so no amount of automation can close anything.
/// </remarks>
public const string WorkItemVerify = "workitem:verify";
```

### 4.3 Who holds it

A new role, `Tester`, valid at **team and project scope** — the second name held at two scopes, with
the same justification `Viewer` already has: testing a team's work and testing one project's work are
the same idea applied to different things.

`Tester` is a plain grant, **not** a `TeamPosition`. Positions are singular appointments (one Scrum
Master per team); a team can and should have several testers.

| Role | Scope | `workitem:verify`? | Reasoning |
|---|---|---|---|
| `Tester` | Team, Project | **yes** | The role exists for this |
| `TeamLead` | Team | **yes** | Higher authority in the team, as specified |
| `ProductOwner` | Team | **yes** | In Scrum the PO accepts the increment. Acceptance *is* this permission |
| `ProjectAdmin` | Project | **yes** | Administers the project; already holds `workitem:delete` |
| `OrgAdmin` | Org | **yes** | Holds `Everything` by definition |
| `ScrumMaster` | Team | **open — see §11** | Owns the process, not acceptance. Recommend **no** |
| `Contributor`, `TeamMember` | | **no** | The point. A developer cannot certify their own work |
| `Viewer` | | **no** | Read-only |

`Tester` at team scope carries `ProjectContributor + workitem:verify` onto the team's projects
through the existing `TeamToProject` edge — the same mechanism that gives Scrum Master and Product
Owner sprint authority over their team's projects. No new inheritance machinery.

### 4.4 Self-certification

A `Tester` who is also the assignee can currently certify their own work, because the permission
check does not know who wrote it.

**Recommendation:** block it by default. Add `Project.AllowSelfCertification` (default `false`), and
reject `Resolved → Closed` when `certifierId == item.AssigneeId` unless the caller holds
`project:admin`. Record the certifier in `WorkItemHistory` either way — "who signed this off" must be
answerable six months later, and it is the single most valuable field the audit trail can carry.

The escape hatch matters for small teams. A three-person startup where everyone is a Tester should be
able to switch it off knowingly, rather than route around it by granting everyone `ProjectAdmin`.

### 4.5 What this costs

- A migration adding `InReview` to `WorkItemState` and `Tester` to `RoleType` (both stored as names,
  so additive), plus `Project.AllowSelfCertification`.
- Board column seeding gains a Review column.
- `RolePermissions` gains one permission and one role in three tables.
- The frontend gains a state — which it will read from the metadata endpoint (§5) rather than
  hardcode, which is the whole point of building §5 first.

---

## 5 · Metadata and capability endpoints

*The flagged frontend problem, and the fix.*

Audit finding 4 catalogues eight vocabularies the client currently hardcodes. The priority case shows
why it is structural rather than sloppy: `WorkItemPriority` is `Critical=1 … Low=4`, and the numbering
is the ordering. Enums serialize as strings, so the client receives four unordered strings and has no
choice but to hardcode the sort. That array is a second source of truth with no test and no migration
behind it.

`RolePermissions.AssignableAt` already fixed exactly this problem *inside* the server, and its remarks
say why: "two copies of which roles belong at project scope is one copy too many… a hand-maintained
third list is the one that silently falls behind — as the organization list did." The frontend is
currently that third list, eight times over.

### 5.1 `GET /api/metadata`

Anonymous. `ETag` + `Cache-Control: public, max-age=300`. One round trip at app boot.

```jsonc
{
  "version": "2026-08-22.1",

  "roles": [
    { "value": "OrgAdmin", "label": "Organization Admin", "scope": "Organization",
      "order": 10, "assignable": true, "isPosition": false,
      "description": "Administers the entire organization and everything inside it.",
      "permissions": ["org:read", "org:admin", "..."] }
  ],

  "permissions": [
    { "value": "workitem:verify", "label": "Certify work", "group": "Work items",
      "description": "Certify that finished work meets its acceptance criteria." }
  ],

  "workItemTypes": [
    { "value": "UserStory", "label": "User Story", "order": 3,
      "allowedChildren": ["Task", "Bug"] }
  ],

  "workItemStates": [
    { "value": "Resolved", "label": "Awaiting QA", "order": 4, "category": "Review",
      "transitionsTo": [
        { "state": "Closed", "requiresPermission": "workitem:verify" },
        { "state": "Active", "requiresPermission": "workitem:verify" }
      ] }
  ],

  "priorities": [
    { "value": "Critical", "label": "Critical", "order": 1, "colorToken": "danger" }
  ],

  "sprintStatuses":  [ { "value": "Active", "label": "Active", "order": 2 } ],
  "workItemLinkTypes": [ { "value": "Blocks", "label": "Blocks", "inverse": "BlockedBy" } ],
  "teamPositions":   [ { "value": "ScrumMaster", "label": "Scrum Master", "order": 2 } ]
}
```

**The three fields that end the hardcoding:** `value` (the wire string), `label` (what a human reads),
`order` (the sort key that the enum numbering carries and the string does not). Every vocabulary gets
all three.

**How it stays honest.** Project it from the same declarations the evaluator reads — `RolePermissions`
for roles and their permission sets, `Enum.GetValues<T>()` plus the underlying numeric value for
`order`, `ValidateStateTransition` for the transition graph, `ValidateHierarchy` for
`allowedChildren`. Then a test in the style of `EndpointAuthorizationCoverageTests`: **every member of
every published enum must appear in the document.** Adding an enum value without a label fails the
build, which is the only mechanism that reliably prevents drift.

Labels and descriptions are the one part that cannot be derived. Put them in a
`[DisplayMetadata("User Story", Order = 3)]` attribute on the enum member, so the label lives beside
the value it names and the test can assert its presence.

### 5.2 `GET /api/me/capabilities?scope=project:{id}`

Closes the gap `docs/permissions-frontend.md` §10 records.

```jsonc
{ "scope": "project:8f3e…", "permissions": ["project:read", "board:read", "workitem:write", "sprint:order"] }
```

Batch form for dashboards, so a project list is one call not N:

```jsonc
// POST /api/me/capabilities   { "scopes": ["project:8f3e…", "team:2a11…", "org:c4d…"] }
{ "project:8f3e…": ["project:read", "..."], "team:2a11…": ["team:read"], "org:c4d…": ["org:read"] }
```

Thin — the access snapshot is already resolved and memoized per request; this enumerates
`Permissions`' constants against `AccessEvaluator` for the named scopes. Cap the batch at ~50 scopes.

Without it the client must reimplement `AccessEvaluator` in TypeScript: three inheritance routes, the
team→project edge with its Scrum Master / Product Owner exception, and OrgAdmin's reach. That
reimplementation drifts, and it drifts *permissive*, because a button that 403s gets reported and a
button wrongly hidden does not.

### 5.3 Generate the client, do not write it

Swagger is already configured with XML comments. Add [NSwag](https://github.com/RicoSuter/NSwag) or
`openapi-typescript` to CI, emit a typed TypeScript client into the frontend, and fail the build when
the checked-in client differs from the generated one. Then a renamed field is a compile error rather
than a runtime `undefined`.

This is the structural fix. §5.1 and §5.2 remove the *need* to hardcode; generation removes the
*ability* to.

---

## 6 · Role and permission design

*Requested: a deep look. The verdict is that the model is right, and the work is extending it to
principals that are not people.*

### 6.1 What is already correct

The permission rebuild (`docs/permissions-model.md`, shipped across Stages 0–4) landed on a design
worth defending:

- **Named permissions, not a rank ladder.** The ladder could express "at least a TeamMember" and could
  not express "may start a sprint" when the people who may — Scrum Master, Product Owner — are peers.
  The numeric enum values are explicitly meaningless now, and `Role.cs` says so.
- **Union, never comparison.** Holding several roles at one scope grants the union. Correct, because
  the roles are genuinely unordered.
- **Scope-specific vocabulary.** A role name tells you which scope it grants on. `Reader` used to mean
  "org member" at one scope and "read-only" at two others; splitting it into `Member` / `Viewer` was
  right.
- **Grants, not consequences.** `AccessSnapshot` stores what the user was granted and expands
  inheritance at question time. The snapshot grows with the size of the user's access, not the size of
  the organization, so a new project does not invalidate every admin's cache.
- **A pure evaluator.** `AccessEvaluator` is static, does no I/O, and is exhaustively unit-tested. The
  cached and uncached paths share one definition of the rules.
- **Enforcement by attribute, with a reflection test.** `[RequirePermission(Permissions.SprintManage,
  From = "sprintId")]`, with a test that fails for any action carrying neither it nor an explicit
  `[NoPermissionRequired]` justification. That test catches endpoints nobody thought to write a case
  for, which is the only kind of authorization test that scales.
- **Downward-only inheritance.** A project role never reaches the project's team, because a team serves
  several projects.

Do not redesign this. Extend it.

### 6.2 Extension one — `workitem:verify` and `Tester`

§4. One permission, one role, three table entries, no new machinery.

### 6.3 Extension two — typed principals

This is the important one, and `docs/permissions-model.md` §10.4 already staked out the position:
*"The full typed-principal model should wait for the git integration that needs it… The §4.4 split —
authority belongs to the integration, attribution is metadata — is the part worth holding onto."*

Git sync is that integration. Build it now.

A `RoleAssignment.UserId` is a bare `Guid` today, and every actor is assumed to be a person. A webhook
worker acting on a merge is not a person. Two wrong ways to model it:

- **Act as the commit author.** Requires resolving a git email to a BoardSync user, which fails for
  external contributors and bots — and worse, it means the integration inherits whatever that person
  can do. A merge authored by a Tester could then close the item, defeating §4 entirely.
- **Act as a superuser and skip the checks.** Every automated transition becomes unauditable, and the
  QA gate degrades to an `if` statement someone will eventually delete.

The right model:

```csharp
public enum PrincipalType { User, Integration }

public sealed record Principal(PrincipalType Type, Guid Id)
{
    public static Principal User(Guid userId) => new(PrincipalType.User, userId);
    public static Principal Integration(Guid installationId) => new(PrincipalType.Integration, installationId);
}
```

- `RoleAssignment` gains `PrincipalType` (default `User`, so existing rows migrate untouched) and
  `UserId` widens in meaning to `PrincipalId`.
- A `GitProviderInstallation` **is** a principal. When a repository is linked to a project, the
  installation gets a project-scope `RoleAssignment` with a new role, `Integration`, permitting
  exactly: `project:read`, `workitem:read`, `workitem:write`, `workitem:comment`, `board:read`,
  `sprint:read`.
- **`Integration` does not carry `workitem:verify`, `workitem:delete`, or anything administrative.**

The consequence is the design's best property: **the QA gate is not a rule the git worker follows, it
is a permission the git worker does not have.** A bug in the webhook handler, a malicious payload, or
a future contributor "simplifying" the transition logic cannot close a work item, because the same
`PermissionAuthorizationFilter` that guards every HTTP endpoint denies it. Security that survives
being forgotten about.

**Attribution stays separate.** `WorkItemHistory` gains `ActorType` and an optional
`AttributedToUserId`, so the feed reads *"moved to In Review by GitHub (Ada Lovelace)"* — authority
from the integration, attribution from the commit author, resolved by email when it maps to a known
user and left as a display string when it does not.

### 6.4 Extension three — an honest `[NoPermissionRequired]`

Audit finding 1 is a permission bug that passed the permission test, because the justification string
—"scoped to the organizations the caller belongs to" — described the bug as the defence.

Tighten the contract: `[NoPermissionRequired]` must name the resolver it delegates scoping to, and
the coverage test asserts that the named resolver is a real, registered type. "Scoped to the caller"
stops being an acceptable answer.

### 6.5 Deliberately not doing

- **Custom roles per organization.** Enterprise checkbox, large migration, no demand. The named
  permissions make it *possible* later; that is enough.
- **Field-level permissions.** No use case.
- **Deny rules.** Union-of-grants is comprehensible; grant/deny precedence is not, and the day someone
  cannot explain why a user lacks access is the day the model has failed.
- **Ordering roles again.** It was wrong the first time.

---

## 7 · The GitSync module

*Decision: multiple providers, including Azure DevOps.*

### 7.1 Shape

```
Modules/GitSync/
  Controllers/     GitWebhookController, GitInstallationsController, RepositoryLinksController
  Providers/       IGitProvider, GitHubProvider, GitLabProvider, AzureDevOpsProvider, BitbucketProvider
  Ingest/          WebhookVerifier, DeliveryLedger, RawDeliveryStore
  Domain/          NormalizedGitEvent, WorkItemReference, BindingResolver
  Services/        GitEventProcessor, InstallationService, RepositoryLinkService
  Repositories/    IGitRepository + implementation
  Models/          GitProviderInstallation, RepositoryLink, WebhookDelivery, CommitLink, PullRequestLink
  Events/          CommitLinked, PullRequestOpened, PullRequestMerged, BranchBound
```

Schema `git`.

### 7.2 The provider port

Since Azure DevOps is in scope from the start, the port is load-bearing rather than defensive.

```csharp
public interface IGitProvider
{
    string Key { get; }                                   // "github" | "gitlab" | "azuredevops" | "bitbucket"

    Task<VerificationResult> VerifyAsync(HttpRequest request, GitProviderInstallation installation, CancellationToken ct);
    bool TryNormalize(RawDelivery delivery, out NormalizedGitEvent evt);

    Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderInstallation i, CancellationToken ct);
    Task<string> GetDefaultBranchAsync(GitProviderInstallation i, string repoExternalId, CancellationToken ct);
    Task<IReadOnlyList<RemoteCommit>> ListCommitsAsync(GitProviderInstallation i, string repoExternalId, string branch, DateTime since, CancellationToken ct);
}
```

Everything downstream of `TryNormalize` is provider-agnostic. `NormalizedGitEvent` is the module's
domain type and the only shape the rest of BoardSync sees:

```csharp
public sealed record NormalizedGitEvent(
    GitEventKind Kind,                 // Push | PullRequestOpened | PullRequestMerged | PullRequestClosed | BranchCreated
    string RepositoryExternalId,
    string? BranchName,
    string? TargetBranch,              // PR base — compared to default branch for merge semantics
    IReadOnlyList<CommitInfo> Commits,
    PullRequestInfo? PullRequest,
    ActorInfo Actor,                   // login + email; resolved to a BoardSync user when it maps
    DateTimeOffset OccurredAt,
    string ProviderDeliveryId);
```

### 7.3 Webhook verification varies by provider — model that, do not paper over it

This is the sharpest constraint and it must be explicit in the schema, because Azure DevOps is
materially weaker than the others.

| Provider | Mechanism | Integrity? |
|---|---|---|
| **GitHub App** | `X-Hub-Signature-256`, HMAC-SHA256 over the raw body | ✅ payload integrity |
| **GitLab** | signing token (HMAC-SHA256) preferred; legacy `X-Gitlab-Token` is a plaintext bearer | ✅ / ⚠️ bearer only |
| **Bitbucket** | `X-Hub-Signature`, HMAC-SHA256 | ✅ payload integrity |
| **Azure DevOps** | **no HMAC of any kind.** Service Hooks offer Basic auth and custom headers over HTTPS | ⚠️ bearer only |

Azure DevOps Service Hooks cannot sign a payload. Anyone who obtains the endpoint URL and the Basic
credential can post an arbitrary body, and nothing about the request proves it came from Azure.

Consequences, all of which belong in the design rather than in a comment:

1. `WebhookDelivery` records `VerificationMethod` (`HmacSha256` | `SharedSecret` | `BasicAuth`) on
   every row. What a delivery was trusted on is auditable forever.
2. Bearer-only providers get compensating controls: a **high-entropy per-installation path segment**
   (`/api/git/azuredevops/webhook/{installationSecret}`), a distinct Basic credential per
   installation, HTTPS enforced, and IP allowlisting where the customer's egress is stable.
3. **A bearer-only provider may never be the sole authority for a transition into `Resolved`.**
   Recommend requiring that an ADO merge event be corroborated by a commit reachable on the default
   branch — one API read-back against the ADO REST API, which is cheap and turns a spoofable POST
   into a claim the provider itself confirms.
4. Surface it in the product. The installation settings page says what verification a connection uses.
   "We verify GitHub payloads cryptographically; Azure DevOps does not offer that, so we verify by
   shared secret and confirm merges by reading back" is a sentence that builds trust rather than
   spending it.

Constant-time comparison everywhere, no exceptions, including the bearer paths.

**Auth model per provider.** GitHub: a **GitHub App**, not an OAuth App — installation-level webhooks
covering every repo the app can access, fine-grained permissions, short-lived installation tokens
(JWT → 1-hour token), rate limits that scale with the installation, and — the one that matters for a
product — the integration keeps working when the person who installed it leaves the org. GitLab:
group or project access tokens. Azure DevOps: a PAT initially, Entra ID app registration when
enterprise customers ask.

### 7.4 Ingest: accept fast, process durably

```
POST /api/git/{provider}/webhook          [AllowAnonymous — verified by the provider verifier]
  │
  ├─ 1. Read raw body once, buffered. Signatures are over exact bytes; re-serializing invalidates them
  ├─ 2. Resolve installation from route/headers → provider.VerifyAsync  → 401 on failure, no detail
  ├─ 3. INSERT INTO git."WebhookDeliveries" (ProviderDeliveryId, …)
  │       UNIQUE (Provider, ProviderDeliveryId) → conflict means already seen → 200, done
  ├─ 4. Persist the raw payload
  ├─ 5. Enqueue a ProcessGitDelivery job (§9)
  └─ 6. 202 Accepted  ← target < 100ms, always
```

The rules that make this survive production:

- **Idempotency on the provider's delivery id.** GitHub's `X-GitHub-Delivery` GUID is stable across
  manual redeliveries, which is exactly the dedupe you want. A unique index does the work; a duplicate
  is a 200, never an error.
- **Return 2xx for anything you have durably accepted or deliberately ignored.** A non-2xx for an
  event type you do not handle teaches the provider to retry it forever and, on some providers, to
  disable the hook.
- **Never process inline.** Verification and one insert, then 202. Ingest latency must not depend on
  how many work items a 300-commit force-push touches.
- **Keep raw payloads** for a bounded window (30 days). Replay is how you fix a binding bug without
  asking customers to re-push, and it is the only way to debug a provider's actual wire format.

### 7.5 Binding a commit to a work item

*Decision: branch name primary, commit token fallback.*

Each project gets a short human key (`BS`, `PAY`) and work items get a per-project sequential number,
so `BS-142` is what people type. **Never expose GUIDs for this** — nobody types a GUID into a branch
name, and a system that asks them to will not be used.

Resolution order, first match wins:

1. **Branch name** — `bs-142-fix-login`, `feature/BS-142`, `BS-142`. Case-insensitive, matched by
   `(?i)\b([a-z][a-z0-9]{1,9})-(\d+)\b`. On the *first* commit of a branch this creates a
   `BranchBinding`, and **every subsequent commit on that branch inherits it.** The developer types
   the id once, at branch creation, which is the only moment they are already thinking about which
   ticket they are on.
2. **Commit message token** — `BS-142` anywhere in the subject or body. Overrides the branch binding
   for that commit, which is how you attribute a drive-by fix on a feature branch to its own ticket.
3. **PR title or description** — same pattern. A PR may reference several items; all of them bind.
4. **No match** → record the commit as unbound. **Do not guess.** Surface unbound commits in a
   project view so the team can see the gap and bind them manually; that view is also the honest
   measure of how well the convention is landing.

Multiple references in one commit bind to all of them. A commit's transition applies to every bound
item.

**Guardrails, learned from how these integrations fail:**

- The referenced item must belong to the **project the repository is linked to.** A repo cannot move
  another project's work, whatever the message says.
- Force-push and rebase rewrite history. Bind on **commit SHA with the branch as context**, keep both
  the pre- and post-rewrite SHA, and never regress a state because a SHA disappeared.
- Cap per delivery. A 500-commit push binds the first N and records the rest as a summary; nobody
  needs 500 history rows.
- Merge commits are not authorship. Skip them for binding unless they carry an explicit token.

### 7.6 Applying the transition

The processor resolves the target state from the event and calls the existing
`IWorkItemService.UpdateStateAsync` **as the integration principal** — the same service, the same
validation, the same history and events as a human transition. No back door.

| Event | Target | Notes |
|---|---|---|
| First commit on a bound branch | `Active` | Only from `New`. Never regresses |
| PR opened targeting default branch | `InReview` | From `Active` |
| PR closed unmerged | `Active` | From `InReview` |
| **PR merged into default branch** | **`Resolved`** | **The ceiling. QA takes it from here** |
| Any event, item already ahead | *no-op* | Never move backwards |

Three invariants:

1. **Monotonic.** A git event never moves an item backwards. Late webhooks arrive out of order
   routinely, and reordering must be a no-op rather than a regression.
2. **Human wins.** If a human transitioned the item after the git event's `OccurredAt`, the git event
   is recorded in history and does not transition. The board is derived from git, but a person who
   deliberately overrode it knew something git did not.
3. **`Resolved` is the ceiling** — enforced by the integration principal lacking `workitem:verify`
   (§6.3), not by a check in this method.

Every transition emits `WorkItemStateChanged` through the outbox as usual, so the board updates live,
the activity feed records it, and notifications fire. **The realtime layer needs no changes at all**
— which is the payoff for the outbox architecture already being right.

### 7.7 Backfill

Linking a repo to an existing project should not start from zero. On link, walk the last 90 days of
commits on the default branch, bind what matches, and record links without transitioning anything —
history, not state changes. This is what makes the first report meaningful on day one instead of in a
quarter, and it is a bounded, resumable job (§9).

---

## 8 · The Intelligence module

*Decision: after git sync, as its own module.*

Two capabilities, one hard boundary.

### 8.1 The boundary

**The AI proposes; a human accepts; only the acceptance writes to the board.**

Every generated artifact lands as a `Proposal` — a draft sprint plan, a decomposed epic, a report — that
a human reviews and accepts, wholly or item by item. Acceptance is what calls the existing services
and creates real work items, through the same permission checks as any other write.

The reasons are the same ones behind §4, and they are not squeamishness:

- Model output is probabilistic. A board that silently gains eleven hallucinated tasks is worse than
  no AI at all, and the trust never comes back.
- The permission model has no way to reason about an actor with unbounded scope. A proposal has no
  authority, so it needs no permission; the accepting human already has one.
- Every proposal-and-acceptance is a labelled training signal about what this team considers a good
  breakdown. Autonomous writes throw that away.

### 8.2 PRD decomposition

```
POST /api/projects/{projectId}/intelligence/decompose
  body: { source: "text" | "fileId", content | fileId, targetSprintLength: 14, teamSize: 5 }
  → 202 { proposalId }        [requires workitem:write]

GET  /api/intelligence/proposals/{id}          → status + the draft
POST /api/intelligence/proposals/{id}/accept   → { include: [nodeIds] } → creates real work items
```

The output is a typed object, not prose to parse. Use **structured outputs** so the model is
constrained to the schema:

```csharp
using Anthropic;
using Anthropic.Models.Messages;

AnthropicClient client = new();

var response = await client.Messages.Create(new MessageCreateParams
{
    Model = "claude-opus-5",
    MaxTokens = 16000,
    Thinking = new() { Type = ThinkingType.Adaptive },
    OutputConfig = new OutputConfig
    {
        Format = new JsonOutputFormat { Schema = DecompositionSchema }   // epics → features → stories → tasks
    },
    Messages = [ new() { Role = Role.User, Content = prompt } ],
});
```

Notes that matter for this specific job:

- **`claude-opus-5`**, adaptive thinking. Decomposing a PRD into a coherent hierarchy with dependency
  ordering and estimates is a reasoning task; the cheap model produces a flat list of restated
  requirements.
- **Stream** it (`client.Messages.Stream(...)` + `.FinalMessage()`) — a large PRD with a large
  structured response will otherwise sit near the HTTP timeout.
- **Prompt-cache the stable prefix.** The system prompt, the schema, and the project's existing
  conventions are identical across every decomposition; put the PRD itself after the last cache
  breakpoint. Verify with `usage.cache_read_input_tokens` — if it is zero across repeated calls,
  something volatile leaked into the prefix.
- The schema mirrors the real hierarchy (`Epic → Feature → UserStory → Task/Bug`) and the real
  `WorkItemPriority`, so acceptance is a direct map with no interpretation layer.
- Do **not** truncate a long PRD. Chunk by section with the outline held in the prefix, and say so in
  the UI.

### 8.3 Report generation

The genuinely differentiated one, because BoardSync knows things a board fed by hand does not: real
commit activity, real cycle time from first commit to QA certification, and where work actually sat
versus where the board said it was.

```
GET /api/sprints/{sprintId}/report          [requires sprint:read]
GET /api/projects/{projectId}/reports/status
```

Build it in two layers, and keep them separate:

1. **A deterministic metrics layer** — burndown, velocity, CFD, cycle time, commit volume per item,
   the gap between merge and certification, items with no git activity at all. Plain SQL over
   `ActivityLogs`, `WorkItemHistory`, and `CommitLink`. **These numbers must never come from a
   model.** They are facts, they must be identical every time they are computed, and they are what
   management will make decisions on.
2. **A narrative layer** — Claude summarizing the metrics into prose, flagging risk, comparing against
   previous sprints. It receives the computed metrics as input and is explicitly instructed to cite
   only those numbers.

That split is the entire trick. A model asked to both compute and narrate will produce plausible
numbers, and nobody downstream can tell which numbers were computed.

Cache reports by `(sprintId, sprintUpdatedAt)`. A completed sprint's report never changes, so it is
generated once and served forever.

### 8.4 Operational

- `ANTHROPIC_API_KEY` from the environment; never in `appsettings`.
- Per-organization monthly token budget with a hard stop. This is the one dependency that can generate
  an unbounded bill.
- Log token usage per call against org, project, and feature. You will be asked what this costs.
- The whole module is behind a feature flag, and a disabled or unconfigured Intelligence module must
  degrade to hidden UI, never to a 500.
- Decomposition and report generation are long-running, retryable, and expensive: they run as jobs
  (§9), never in a request thread.

---

## 9 · Message broker: not yet, and here is the trigger

*Requested: a verdict on RabbitMQ.*

### The verdict: no RabbitMQ. Add a jobs table instead.

**What the outbox already gives you**, and it is most of what a broker is for: atomicity with the
business write (the event and the state change commit together, so the dual-write problem does not
exist), at-least-once delivery, ordering by `Sequence`, concurrent multi-instance draining via
`FOR UPDATE SKIP LOCKED`, retry with attempt counting, and a dead-letter equivalent — messages that
exhaust `MaxAttempts` stay in the table, visible and queryable, rather than vanishing into a DLQ
nobody has a dashboard for.

**A broker does not replace that.** The standard pattern is outbox → broker: RabbitMQ sits
*downstream* of the outbox, which stays. So adopting it adds a second delivery guarantee to reason
about and a second failure mode (broker up, database down, and back to dual-write) without removing
the first.

**What RabbitMQ would actually buy:** competing consumers on separate hardware, per-queue
backpressure, and fan-out to systems that are not this application. There is one deployable and no
external consumer. None of those are load-bearing today.

**What it would cost:** a stateful service to run in HA, a new operational skill on the team, another
thing that can be down at 3am — and the outbox stays anyway.

### What to build instead: `kernel.Jobs`

Git backfill, PRD decomposition, and report generation are a genuinely different kind of work from
domain events. They are minutes-long rather than milliseconds, expensive to redo, and want per-type
concurrency limits. Running them through the outbox would let one 90-day backfill starve the activity
feed behind it.

So split the lanes:

| Lane | Table | Carries | Latency target |
|---|---|---|---|
| **Events** | `kernel.OutboxMessages` | Domain events → activity, notifications, realtime | < 100ms (once audit finding 6 is fixed) |
| **Jobs** | `kernel.Jobs` | Webhook processing, git backfill, AI decomposition, reports | seconds to minutes |

`kernel.Jobs` reuses the pattern that already works — `FOR UPDATE SKIP LOCKED` claiming, attempt
counting, `LISTEN/NOTIFY` wake — and adds what long work needs: a `VisibleAt` for exponential backoff,
a lease with a timeout so a crashed worker's job is reclaimed, per-`JobType` concurrency caps, and a
priority column so an interactive decomposition outranks a backfill.

That is one table and roughly 200 lines against infrastructure already running in every environment.
It gets ~90% of what a broker would give this system, today.

### Revisit RabbitMQ when — concretely

1. **A second deployable exists.** A git-sync worker scaled independently of the API is the honest
   trigger, and it is the one most likely to arrive.
2. **Sustained webhook ingest above ~200 events/s**, or a burst profile (monorepo force-push,
   org-wide backfill) that makes `kernel.Jobs` the hottest table in the database.
3. **A consumer outside BoardSync** wants the event stream — a customer's data warehouse, a
   third-party integration.

Until one of those is a number on a dashboard rather than a hypothetical, RabbitMQ is complexity
bought on credit. The module boundaries and the `IEventBus` abstraction mean introducing it later is
an infrastructure change behind an interface that already exists, not a rewrite — which is exactly why
it is safe to defer.

**One caveat, held honestly:** if the deployment target is already a Kubernetes cluster where a
managed RabbitMQ or a cloud queue is a checkbox rather than a service to operate, the cost side of
this analysis shrinks a lot. The recommendation stands on the *benefit* side being near zero today —
but revisit it the moment the second deployable appears, not later.

---

## 10 · Build phases

Ordered by dependency and by what unblocks other people. Every phase ends in something demoable.

### Phase A — Stabilize and unblock ✅ **shipped** · *the frontend cannot start without this*

- [x] Audit finding 3: drop `Active → Closed`.
- [x] Audit finding 1: visibility resolved from the access snapshot as `ProjectVisibility` — grants,
      not expanded project ids — and pushed into SQL as a predicate. Search, Notifications and the
      workspace summary all route through it. `[NoPermissionRequired]` justifications now name their
      resolver.
- [x] Audit finding 6: the outbox `NOTIFY` wakes the dispatcher.
- [x] **§5.1 `GET /api/metadata`** with the drift test.
- [x] **§5.2 `GET /api/me/capabilities`**, single and batch.
- [x] Integration test harness — Testcontainers + `WebApplicationFactory`.
- [ ] Audit finding 7: decide the frontend question and make the repo tell the truth. **Still open.**

*Found along the way, all fixed:* `WorkItemHistory.ProjectId` was never written, so the notification
bell returned nothing to anybody (finding 14); and every work item domain event was enqueued after
its save, so **not one had ever been delivered** — no work item activity, no live board updates
(finding 15). Both were invisible to unit tests and to reading the code; the harness found the
second on its first run.

*Exit: a frontend developer can build every screen's gating and every dropdown without hardcoding a
single constant. No endpoint returns data the caller cannot read.*

### Phase B — The QA gate · **core shipped**, typed principals and concurrency outstanding

- [x] `InReview` state; new transition table; `Active → Closed` gone.
- [x] `workitem:verify`; `Tester` role at team and project scope; carried onto the team's projects
      through the `TeamToProject` edge.
- [x] `Project.AllowSelfCertification` and the self-certification guard.
- [x] Board seeding gains the Review lane, and existing boards are migrated — a card in a state no
      column claims simply does not render, so the lane had to be inserted rather than left to
      whoever noticed work had vanished.
- [ ] Typed principals (§6.3): `PrincipalType` on `RoleAssignment`, `Integration` role,
      `WorkItemHistory.ActorType` + `AttributedToUserId`. **Moved into Phase C.**
      `docs/permissions-model.md` §10.4 argues this should wait for the integration that needs it,
      and it is right: with only one kind of principal in existence, the shape would be a guess.
      It lands alongside `GitProviderInstallation`, which is the second principal.
- [x] Audit findings 2 and 8: optimistic concurrency is real (it was inert at three separate points);
      `PATCH /api/workitems/{id}` added, with `Patch<T>` to tell an omitted field from an explicit null.

*Note on enforcement:* which permission a transition needs depends on the states being moved
between, and the target arrives in the request body — so it cannot live in a `[RequirePermission]`
attribute and is invisible to the endpoint-coverage test. It is checked in `WorkItemService`, with
the endpoint still declaring `workitem:write` as the floor, and `QaGateEndpointTests` is what stands
behind it.

*Exit: work reaches Done only through a human holding `workitem:verify`. The principal model that git
sync depends on exists and is tested.*

### Phase C — Git sync, GitHub first · **usable end to end**; backfill outstanding

- [x] `kernel.Jobs` (§9) with `IJobQueue`, `JobWorker`, leases, exponential backoff and a dead-row
      state that stays queryable.
- [x] `Modules/GitSync` skeleton, `IGitProvider`, `NormalizedGitEvent`, `git` schema.
- [x] GitHub App: HMAC-SHA256 verification over the raw body, constant-time compared; push and
      pull-request normalization.
- [x] Ingest pipeline: verify → dedupe → persist → 202 → job.
- [x] Project keys and work item numbers (`BS-142`), with existing rows backfilled.
- [x] Binding resolver: branch, commit messages and pull request text, unioned; merge commits skipped.
- [x] Typed principals — `PrincipalType`, the `Integration` role, and `WorkItemHistory.ActorType` +
      `AttributedToUserId`.
- [x] Transition application as the integration principal, with all three invariants.
- [x] Installation and repository-link management endpoints — connect, rotate, disconnect, link,
      unlink, and a delivery history. Connecting is `org:admin`; linking a repository to a project is
      `project:admin`, and cross-organization links are refused.
- [x] Delivery history endpoint, which is the "is the integration working?" view — a quiet
      integration and a broken one are otherwise identical from the board.
- [ ] Backfill on link — walk the last 90 days so the first report is meaningful on day one. Needs
      the provider REST clients (installation-token exchange for GitHub), which is the first piece of
      this module that talks *out* rather than only receiving.
- [ ] A per-project view of unbound commits. The delivery history answers it at organization scope;
      a team wants it filtered to their own project, which needs deliveries to carry the projects
      they touched rather than only a prose outcome.

*Split deliberately at the ingest/binding boundary.* Verification, idempotency, durability and the
job pipeline are provably working before any of it is used to change a board — which is the half
that is hard to retrofit confidence into, since the endpoint is the only anonymous write surface in
the product.

*Exit: a developer branches `bs-142-fix-login`, commits, opens a PR, merges — and the card moves
New → Active → InReview → Resolved with nobody touching the board. It stops there, waiting for QA.*

### Phase D — Providers 2–4, and notifications that notify · **GitLab, Azure DevOps and notifications shipped**

- [x] GitLab (shared token) and Azure DevOps (Service Hooks, Basic auth).
- [x] Provider conformance test suite: one set of scenarios run against every adapter, which is what
      keeps three hosts that disagree about naming from putting three vocabularies into the domain.
- [ ] Azure DevOps merge read-back (§7.3 control 3). **Not the correctness bug this line implied** —
      the adapter already gates on `status == "completed"` rather than the event name, and maps an
      abandoned pull request to closed, so a speculative merge does not resolve anything. What is
      outstanding is defence in depth: ADO cannot sign payloads, so a forged body claiming
      completion is the residual risk, and reading the pull request back confirms it independently.
      That needs outbound credentials per installation, which the model has no place for yet —
      the same missing piece as backfill-on-link.

      Original note: ADO cannot sign payloads and raises
      `git.pullrequest.merged` for its speculative conflict check, so `status: completed` is what the
      adapter trusts. Corroborating a merge against the REST API before it resolves anything is the
      remaining control, and it needs the same outbound provider clients as Phase C's backfill.
- [ ] GitLab signing tokens. GitLab now offers an HMAC over the payload, which would put it level
      with GitHub. Deliberately not guessed at: the header name and digest encoding want confirming
      against a real delivery, and a subtly wrong signature check is worse than an honest shared
      secret.
- [ ] Bitbucket.
- [x] Audit finding 10: a real `Notification` entity, recipient resolution off the outbox, read
      state, watching. The QA-lane notification needed a reverse permission lookup — who holds
      `workitem:verify` here — which is the inverse of every question the evaluator answered before.
- [ ] Notification preferences. Everyone currently gets everything they are entitled to; the escape
      hatch is unwatching an item. Per-type opt-outs are the next thing people will ask for.
- [ ] `@mentions` in comments. Deliberately deferred: matching a name or email in free text is
      ambiguous enough to want a real mention syntax and a picker, not a regex.
- [ ] Email delivery. The bell is in-app only.
- [x] Audit finding 9: Postgres FTS for search. A generated `tsvector` over title and description
      with a GIN index, ranked by `ts_rank`, replacing `LOWER(title) LIKE '%term%'` — which no index
      could serve and which ordered by creation date, so the best match and the newest coincided
      only by accident. **It also now matches the reference**: `BS-142`, `142`, and a bare number
      below the minimum term length, because an exact number is an indexed lookup rather than the
      prefix scan that minimum exists to prevent. Searching for the string every other surface
      displays returned nothing before.

*Exit: an Azure DevOps shop can adopt BoardSync. QA gets told when something needs testing.*

### Phase E — Intelligence · **metrics, narrative and decomposition shipped; CFD outstanding**

- [x] Deterministic metrics — burndown, velocity, cycle time, and the merge-to-certification gap.
      **Shipped as `Modules/Reporting`, not `Modules/Intelligence`.** §8.3's argument is that a model
      asked to both compute and narrate produces plausible numbers nobody downstream can audit;
      putting the computed figures in a module named for AI would blur that boundary before the AI
      exists. Reporting computes, Intelligence will narrate over it, and the module structure is what
      keeps the two from merging later.
- [x] `Modules/Intelligence`, proposal model, acceptance flow, budget enforcement — see
      `docs/adr-002-proposals.md`. A decomposition lands as a `Proposal` with no authority; accepting
      it calls the same `WorkItemService.CreateAsync` a person clicking "New work item" calls.
      Selecting a node carries its ancestors (a story cannot be created under a feature that was
      not) and does **not** carry its descendants (accepting an epic must not silently create forty
      tasks nobody read). Acceptance runs in one transaction, because `CreateAsync` saves per item
      and a failure partway leaves half a plan on the board.
- [x] PRD decomposition with structured outputs — `POST /api/projects/{id}/intelligence/decompose`,
      `202` with a proposal id to poll, run as a job because it is tens of seconds of model time.
      `DecompositionGuard` checks the tree before a human sees it: the nesting rule, a 150-node
      review cap, title and estimate limits, duplicate siblings. **The schema cannot express the
      nesting rule** — structured output constrains the JSON shape and has no opinion about whether
      a Task may sit under an Epic, so the prompt asks and the guard enforces.

      **Unexercised against the real API** — no key in the build environment. 22 tests cover the
      guard and the selection rule against a fake. Prompt caching and streaming are both specified
      in §8.2 and both unimplemented: the system prompt is a constant so the prefix is stable, but no
      `cache_control` breakpoint is set, and §8.2's `Messages.Stream(...)` is not this SDK version's
      API (it is `CreateStreaming`).
- [x] Narrative report layer — `Modules/Intelligence`, `GET /api/sprints/{id}/report/narrative`.
      Receives a `SprintReport`, computes nothing, and is **checked afterwards** rather than
      trusted: `NarrativeGuard` verifies every figure in the prose appears in the report it was
      handed, and prose that fails is withheld with the offending sentences returned. Structured
      output, `claude-opus-5`, adaptive thinking at medium effort, a per-organization daily token
      allowance checked *before* the call and charged whether or not the answer survives.

      **Unexercised against the real API** — no key in the build environment. Everything except the
      model call is tested against a fake: 12 tests on the guard, 7 on the rules around it.

      Original note: Narrative report layer, which receives a `SprintReport` and is instructed to cite only its
      figures.
- [ ] Cumulative flow diagram. Needs a state-count-per-day series, which the same history
      reconstruction can produce — deferred only for size.
- [ ] Git activity per work item — commits per item, items with no git activity. **Not currently
      computable:** binding is stateless (no `CommitLink` table), which was the right call for
      binding and means commit counts have nowhere to come from. Recording links is a real cost with
      a real benefit; worth deciding deliberately rather than discovering when a report needs it.

*Exit: a PRD becomes a reviewable sprint plan. A completed sprint produces a report whose numbers are
computed and whose prose cites them.*

### Phase F — Scale and harden

- The rest of the audit register.
- Instance count > 1: pgbouncer, `ActivityLogs` partitioning and retention.
- Load testing against realistic webhook bursts.
- Security review focused on webhook ingest and the integration principal.

### Parallel track — the frontend

Not a phase; it runs alongside from Phase A. Depends on §5 landing first, and on the generated
TypeScript client (§5.3) so the two do not drift.

---

## 11 · Open decisions

| # | Question | Recommendation |
|---|---|---|
| 1 | ~~Does `ScrumMaster` hold `workitem:verify`?~~ | **Decided: no.** Shipped. In Scrum the Product Owner accepts the increment; the Scrum Master owns the process. `QaGateTests.ScrumMasterRunsTheSprintButDoesNotCertify` asserts both halves, so reversing it is a deliberate act rather than a drift. One line in `RolePermissions` if a team wants it. |
| 2 | Project key format and collision policy | 2–10 uppercase alphanumerics, unique **per organization**, derived from the project slug and editable at creation only. Renaming a key orphans every branch name in flight. |
| 3 | Does work item numbering restart per project? | **Yes** — `BS-1`, `PAY-1`. Global numbering makes the key decorative. A per-project sequence needs a counter row, not a database sequence, so it commits in the same transaction. |
| 4 | Retention for raw webhook payloads | 30 days. Long enough to replay a binding bug, short enough not to accumulate customer source metadata indefinitely. |
| 5 | What happens when a repo is unlinked? | Keep `CommitLink` rows (history stays true), stop processing, revoke the integration principal's role assignment. Do not reverse any transition. |
| 6 | Can one repository link to several projects? | **Not in v1.** Monorepos will want it. Model `RepositoryLink` as many-to-many from the start so it is a validation change later, not a migration. |
| 7 | Do AI-generated reports inherit `project:read`? | Inherit initially (this was already open as §7.4 of `permissions-model.md`). Revisit when reports aggregate across projects, where the union of readers is not the union of the inputs' readers. |
| 8 | Deployment target | Genuinely open and it changes §9's cost side. pgbouncer, Redis HA, and hub scale-out look very different on managed Kubernetes versus two VMs. Worth settling before Phase F. |
| 9 | Self-hosted git (GitHub Enterprise Server, self-managed GitLab) | Defer. The provider port supports it via a configurable base URL; the work is egress and network policy, not protocol. |

---

## 12 · Sources

Research grounding the git-integration and provider decisions:

- [Validating webhook deliveries — GitHub Docs](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries)
- [Deciding when to build a GitHub App — GitHub Docs](https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/deciding-when-to-build-a-github-app)
- [Differences between GitHub Apps and OAuth apps — GitHub Docs](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/differences-between-github-apps-and-oauth-apps)
- [Webhooks with Azure DevOps — Microsoft Learn](https://learn.microsoft.com/en-us/azure/devops/service-hooks/services/webhooks?view=azure-devops)
- [How to add an HMAC signature in a webhook via the Azure DevOps REST API — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5864996/how-to-add-a-hmac-signature-in-webhook-via-devops)
- [Webhooks — GitLab Docs](https://docs.gitlab.com/user/project/integrations/webhooks/)
- [Webhook authentication learnings for GitHub, GitLab, and Bitbucket — Release](https://release.com/blog/webhook-authentication-learnings)
- [Linking your GitHub commits with Azure Boards — Microsoft Azure Blog](https://azure.microsoft.com/en-us/blog/linking-your-github-commits-with-azure-boards/)
- [Process work items with smart commits — Atlassian Support](https://support.atlassian.com/jira-software-cloud/docs/process-issues-with-smart-commits/)
- [Webhook idempotency and deduplication — Hooklistener](https://www.hooklistener.com/learn/webhook-idempotency-and-deduplication)
- [Implementing the outbox pattern for reliable messaging in .NET modular monoliths — Mehmet Ozkaya](https://mehmetozkaya.medium.com/implementing-the-outbox-pattern-for-reliable-messaging-in-net-modular-monoliths-architecture-8fa1a68835b0)
