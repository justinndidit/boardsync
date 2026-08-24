# BoardSync

Project-tracking backend and client for organizations: boards, sprints, work items, role-based
access control, and an activity feed.

- ASP.NET Core API (.NET 10) — modular monolith
- PostgreSQL + EF Core (Npgsql)
- React 19 + Vite frontend
- Docker-based dev and production workflows

> Status: the API is the mature part of the codebase. `boardsync-ui` is still the Vite starter
> template — the client is not wired to the API yet. See `docs/activity-feed-frontend.md` for the
> planned frontend contract.

## 1) Local Development Setup

### Prerequisites

- .NET SDK 10
- Node.js 22+
- Yarn
- Docker + Docker Compose

### Start local infrastructure

```bash
make dev-infra
```

Starts PostgreSQL on port `7000`, Redis on `7001`, and MailHog (SMTP `1025`, web UI
http://localhost:8025) for inspecting confirmation and password-reset mail.

Redis is optional for a single instance — the API falls back to in-process caching and per-process
rate limits and says so at startup — but required before running more than one.

### Run API migrations

```bash
make migrate-db
```

The API also applies pending migrations on startup (`MigrateDatabaseAsync`), so this is mainly
useful for applying schema changes without booting the app.

### Run backend

```bash
make dev-backend
```

API: http://localhost:5022 · Swagger UI (Development only): http://localhost:5022/swagger

### Run frontend

```bash
make dev-frontend
```

Frontend default URL: http://localhost:5173

### Tear down

```bash
make down-all     # stops dev containers and drops volumes
```

## 2) Architecture

The API is a modular monolith. Each module under `server/BoardSync.Api/Modules` owns its
controllers, services, repositories, DTOs, and domain models; cross-module communication goes
through domain events on a **transactional outbox** — `IEventBus.Enqueue` stages the event on the
same unit of work as the change, and `OutboxDispatcher` delivers it after the commit. All data
access lives behind a repository per module; no controller or service touches `BoardSyncDbContext`.

| Module | Responsibility |
| --- | --- |
| `Modules/OrgProject` | Organizations, workspaces, projects, teams, memberships |
| `Modules/Sprints` | Boards, board columns, sprints, sprint backlogs |
| `Modules/WorkItems` | Work items, comments, history, links, tags |
| `Modules/Rbac` | Role assignments and permission checks |
| `Modules/Activity` | Activity log, built by subscribing to domain events |
| `Modules/Notifications` | The notification bell, derived from work item history |
| `Modules/Search` | Global search across organizations, projects, members, work items |
| `Shared/Auth` | Users, JWT issuance/refresh, password and email flows |
| `Modules/GitSync` | Git provider connections, webhook ingest, work item binding, git-driven transitions |
| `Shared/Kernel` | Outbox event bus and dispatcher, background job queue, rate limiting, typed configuration, domain exceptions |
| `Shared/Data` | `BoardSyncDbContext` and EF Core migrations |

### Domain model

An **Organization** owns **Teams** and **Projects**. A project is backed by one **Board** with
ordered **BoardColumns**, and holds **WorkItems** (with comments, history, links, and tags).
**Sprints** belong to a project and pull work items into an ordered sprint backlog. Every meaningful
mutation emits a domain event that the Activity module turns into an **ActivityLog** entry. Events
travel through the outbox, so the activity feed is **eventually consistent** — usually milliseconds
behind the write (a Postgres `NOTIFY` wakes the dispatcher), at worst one poll interval if that
listener has dropped.

### Roles

Every role belongs to one scope, and no name means two things at two scopes:

| `RoleScope` | `RoleType` |
| --- | --- |
| `Organization` | `OrgAdmin`, `Member` |
| `Team` | `TeamLead`, `ScrumMaster`, `ProductOwner`, `TeamMember`, `Tester`, `Viewer` |
| `Project` | `ProjectAdmin`, `Contributor`, `Tester`, `Viewer` |

`Viewer` and `Tester` are the two names deliberately held at more than one scope, because each means
the same thing at both — read-only, and testing — differing only in what it reaches.

Roles are bundles of named permissions (`org:admin`, `sprint:scope`, `workitem:write`, …) declared
in `RolePermissions`, and a user holding several roles at one scope gets the **union** of what they
permit — never a rank comparison, since a Scrum Master and a Product Owner are peers. Controllers
authorize by declaring the permission an endpoint needs, `[RequirePermission(Permissions.SprintManage,
From = "sprintId")]`, rather than by checking roles or raw claims.

### Git-driven transitions

A work item is referenced as `KEY-NUMBER` — `BS-142` — where the key belongs to the project and the
number is per project. Put it in a branch name once (`bs-142-fix-login`) and every commit on that
branch inherits it; a mention in a commit message or pull request text works too.

| Git event | Moves the item to |
| --- | --- |
| First commit on a referencing branch | `Active` |
| Pull request opened | `InReview` |
| Pull request merged **into the project's default branch** | `Resolved` |
| Pull request closed unmerged | `Active` |

Three invariants make it trustworthy: a git event never moves an item **backwards**; a **person who
changed the state after the event happened wins**; and `Resolved` is the ceiling — enforced because
the installation is a principal holding `RoleType.Integration`, which carries `workitem:write` and
deliberately not `workitem:verify`.

### Git providers

| Provider | Verification | What a verified delivery proves |
| --- | --- | --- |
| GitHub | HMAC-SHA256 over the raw body | Origin **and** that the payload was not altered |
| GitLab | `X-Gitlab-Token`, a shared secret | Origin only |
| Azure DevOps | HTTP Basic | Origin only — ADO cannot sign payloads at all |

The difference is real and is recorded on every delivery rather than inferred from the provider, so
an audit can answer what a given event was trusted on. For the two that cannot sign, the
high-entropy segment in the webhook URL is part of the credential.

One conformance suite runs the same scenarios against every adapter. It exists because the three
express the same events differently — GitHub sends `closed` for a merge *and* an abandonment with a
`merged` boolean to tell them apart, GitLab puts it in the action name, and Azure DevOps raises
`git.pullrequest.merged` for its speculative conflict check so only `status: completed` means the
pull request landed. Getting any of those backwards resolves work that was thrown away.

### Connecting a repository

1. An **organization admin** connects the git host: `POST /api/orgs/{orgId}/git/installations`. The
   response carries the webhook URL and secret — **once**. Neither is retrievable afterwards; a lost
   secret is rotated, not recovered.
2. They paste both into the provider's webhook configuration.
3. A **project admin** wires a repository to their project:
   `POST /api/projects/{projectId}/git/repositories`. That grant is what lets git move that board,
   and the installation must belong to the project's own organization.

`GET /api/git/installations/{id}/deliveries` shows what each delivery did, including when it
deliberately did nothing — an unhandled event, an unlinked repository, a branch naming no work item.
That is the difference between an integration that is quiet and one that is broken.

### The QA gate

Work items run `New → Active → InReview → Resolved → Closed`. `Resolved` means **merged, awaiting
test** — it is labelled "Awaiting QA" — and it is the only state from which `Closed` is reachable.

Every move out of `Resolved` or `Closed` requires `workitem:verify`, held by `Tester`, `TeamLead`,
`ProductOwner`, `ProjectAdmin` and `OrgAdmin` — and deliberately **not** by `Contributor`,
`TeamMember` or `ScrumMaster`. Everything before that needs only `workitem:write`. Nobody may certify
work assigned to them unless the project sets `AllowSelfCertification`.

That separation is what makes the planned git integration safe to trust: it will hold `workitem:write`
and never `workitem:verify`, so no amount of automation — and no bug in a webhook handler — can close
a work item. See `build_context.md` §4.

## 3) API Surface

All endpoints are under `/api` and require a bearer token unless noted. Swagger (Development) is
the authoritative reference; the table below is the map.

| Area | Routes |
| --- | --- |
| Auth (anonymous) | `POST /api/auth/{login,register,refresh-token,forgot-password,reset-password,confirm-email,resend-confirmation}` |
| Auth (authenticated) | `POST /api/auth/{logout,revoke-token,change-password}`, `GET /api/auth/me`, `GET|PUT /api/auth/profile` |
| Users | `GET /api/users/me`, `GET /api/users/{userId}`, `GET /api/users/by-email` |
| Metadata | `GET /api/metadata` — every enum the client renders, with labels and sort order; ETag/304 |
| Capabilities | `GET /api/me/capabilities?scope=project:{id}`, `POST /api/me/capabilities` (batch, max 50) |
| Search | `GET /api/search` |
| Workspace | `GET /api/workspace/{summary,activity}` |
| Notifications | `GET /api/notifications` (also served at `GET /api/workspace/notifications`) |
| Organizations | `GET|PUT /api/orgs/{orgId}`, `GET /api/orgs/by-slug/{slug}`, `GET|POST /api/orgs/{orgId}/members`, `DELETE /api/orgs/{orgId}/members/{userId}`, `PUT /api/orgs/{orgId}/members/{userId}/role`, `GET /api/orgs/{orgId}/activity` |
| Teams | `GET|POST /api/orgs/{orgId}/teams`, `GET|PUT|DELETE /api/teams/{teamId}`, `GET|POST /api/teams/{teamId}/members`, `GET|DELETE /api/teams/{teamId}/members/{userId}` |
| Projects | `GET|POST /api/orgs/{orgId}/projects`, `GET|PUT /api/projects/{projectId}`, `PUT /api/projects/{projectId}/team`, `GET|POST /api/projects/{projectId}/roles`, `DELETE /api/projects/{projectId}/roles/{userId}` |
| Boards | `GET /api/projects/{projectId}/board`, `GET|PUT /api/boards/{boardId}`, `POST /api/boards/{boardId}/columns`, `PUT|DELETE /api/boards/columns/{columnId}`, `PATCH /api/boards/{boardId}/columns/reorder` |
| Sprints | `GET|POST /api/teams/{teamId}/sprints`, `GET /api/teams/{teamId}/sprints/active`, `GET|PUT|DELETE /api/sprints/{sprintId}`, `PATCH /api/sprints/{sprintId}/status`, `GET|POST /api/sprints/{sprintId}/workitems`, `DELETE /api/sprints/{sprintId}/workitems/{workItemId}`, `PATCH /api/sprints/{sprintId}/workitems/reorder` |
| Work items | `GET|POST /api/projects/{projectId}/workitems`, `GET|PUT|DELETE /api/workitems/{workItemId}`, `PATCH /api/workitems/{workItemId}/state`, `GET|POST /api/workitems/{workItemId}/comments`, `PUT|DELETE /api/workitems/comments/{commentId}`, `GET /api/workitems/{workItemId}/history`, `GET|POST /api/workitems/{workItemId}/links`, `DELETE /api/workitems/links/{linkId}` |
| Sprint backlog move | `PATCH /api/sprints/{sprintId}/workitems/{workItemId}/move` — single-row drag-and-drop |
| Real-time hub | `WS /hubs/workspace` — see `docs/realtime-frontend.md` |
| Git connections | `GET\|POST /api/orgs/{orgId}/git/installations`, `POST /api/git/installations/{installationId}/rotate-secret`, `DELETE /api/git/installations/{installationId}`, `GET /api/git/installations/{installationId}/deliveries` |
| Git repositories | `GET\|POST /api/projects/{projectId}/git/repositories`, `DELETE /api/projects/{projectId}/git/repositories/{linkId}` |
| Git webhooks (anonymous) | `POST /api/git/{provider}/webhook/{endpointToken}` — verified by the provider's signature, not by a token |
| Health (anonymous) | `GET /healthz` |

Enums serialize as strings (`"OrgAdmin"`, not `10`) on every endpoint. Errors are returned as
RFC 7807 problem details via the global exception handler.

## 4) Security

- JWT bearer auth with refresh tokens; access tokens expire in 15 minutes and refresh tokens in
  7 days by default (`JwtSettings`).
- Passwords hashed with BCrypt. Email confirmation is required, and accounts lock after repeated
  failed sign-ins (`SecuritySettings`).
- Authorization policies for confirmed email and active user, plus a fallback policy that requires
  authentication on any endpoint without its own authorization metadata.
- Rate limiting with separate partitions for general API, auth, and password endpoints
  (`RateLimiting`); partitions are keyed on the authenticated user when present.
- Security headers, request logging, HTTPS redirection, and HSTS in production.
- Forwarded headers are honoured only when `ForwardedHeaders:KnownProxies` or `:KnownNetworks` is
  configured — otherwise the socket peer address is used, so client IPs cannot be forged.
- `JwtSettings:Secret` is deliberately absent from `appsettings.json`. Supply it via the
  `JwtSettings__Secret` environment variable; `appsettings.Development.json` carries a dev-only value.
- `AllowedOrigins` must be set explicitly in production — startup throws if it is empty.

## 4a) Operational Settings

| Setting | Default | Why you would change it |
| --- | --- | --- |
| `Database:AutoMigrate` | `true` outside Production, `false` in Production | Applies pending migrations at startup, under a Postgres advisory lock so concurrent instances cannot race. Leave off in real deployments and run `dotnet ef database update` as a release step; the local prod-like compose stack sets it to `true` because it has no separate migration step. |
| `Database:MaxPoolSize` | `20` | Per *instance*, so the ceiling that matters is this × instance count against Postgres `max_connections` (100 by default). Raise only with `max_connections` raised to match, or put pgbouncer in front. |
| `Database:MinPoolSize` | `2` | Connections held open when idle. |
| `ConnectionStrings:Redis` | unset (dev: `localhost:7001`) | Enables the distributed cache and shared rate-limit counters. **Unset means in-process only** — correct for one instance, wrong for a deployment: each instance would hold its own cache and enforce its own separate rate-limit budget. |
| `Outbox:Enabled` | `true` | Whether this instance drains the outbox. Turning it off everywhere means events are written but never delivered and the activity feed silently stops updating. |
| `Outbox:BatchSize` | `50` | Messages claimed per pass. |
| `Outbox:PollIntervalSeconds` | `5` | Fallback poll. Normal latency comes from Postgres `NOTIFY`; this is the safety net for a dropped listener. |
| `Outbox:MaxAttempts` | `5` | Delivery attempts before a message is left alone — still in the table, visible, not deleted. |
| `Jobs:Enabled` | `true` | Whether this instance runs queued work — webhook processing, and later backfills and AI jobs. Off everywhere means deliveries are accepted and never processed. |
| `Jobs:LeaseSeconds` | `300` | How long a claimed job is held before another worker may take it. The ceiling on how long a crashed worker's job stays stuck; raise it above the slowest handler's worst case. |
| `Jobs:MaxAttempts` | `5` | Attempts before a job is marked dead. It stays in `kernel.Jobs`, queryable and re-drivable. |
| `Telemetry:OtlpEndpoint` | unset | OTLP collector address, e.g. `http://localhost:4317`. Unset means OpenTelemetry is not registered at all — no spans built, nothing exported. `OTEL_EXPORTER_OTLP_ENDPOINT` works too. |
| `Realtime:Enabled` | `true` | Whether the hub is mapped. Off means clients cannot connect; the REST API is unaffected. |
| `Realtime:ReauthorizationIntervalSeconds` | `60` | How often live subscriptions are re-checked against current permissions. Revocations normally take effect immediately via a role-change event; this is the worst case when that path does not fire. |
| `Realtime:MaxReplayMessages` | `200` | How far a reconnecting client can be caught up before it is told to resync instead. |
| `Telemetry:ServiceName` | `boardsync-api` | `service.name` on exported traces and metrics. |

With a collector configured you get per-endpoint `http.server.request.duration` (bucketed by route),
`db.client.operation.duration`, connection pool saturation
(`db.client.connection.count` / `.max`), and .NET runtime counters. Health probes are excluded from
traces but still counted in metrics.

## 5) Production-Like Local Setup (Docker)

### Create runtime env file

```bash
cp .env.sample .env
```

Update values in `.env`, especially `POSTGRES_PASSWORD`, `JWT_SECRET`, and `APP_ORIGIN`.

### Build and run stack

```bash
make prod-build
make prod-up
```

Brings up PostgreSQL, the API (internal, port 8080), and the UI behind nginx, which proxies `/api`
to the API container.

App URL: http://localhost

### Stop stack

```bash
make prod-down
```

## 6) Production Readiness Checklist

- Use a managed PostgreSQL service or persistent encrypted volume backups.
- Store secrets in a secure secret manager (not in source control).
- Terminate TLS at an ingress/load balancer and force HTTPS.
- Restrict CORS (`AllowedOrigins`) to exact trusted domains.
- Set `ForwardedHeaders:KnownProxies`/`:KnownNetworks` when running behind a proxy.
- Point `EmailSettings` at a real SMTP provider (dev uses MailHog).
- Point `Telemetry:OtlpEndpoint` at a collector — instrumentation is in place but exports nothing
  until it is set.
- Configure readiness/liveness probing using `/healthz`.
- Enable CI checks for build, lint, and test before deployment.
- Run EF migrations as a release step. `Database:AutoMigrate` already defaults to off in Production;
  keep it that way so a failed migration stops the rollout instead of surfacing as crash-looping
  replicas.
- Keep `Database:MaxPoolSize` × instance count below Postgres `max_connections`, or front the
  database with pgbouncer.

## 7) Relevant Configuration Files

- API startup and pipeline: `server/BoardSync.Api/Program.cs`
- API settings: `server/BoardSync.Api/appsettings.json` (+ `.Development.json`, `.Production.json`)
- EF Core context and migrations: `server/BoardSync.Api/Shared/Data/`
- Dev compose (Postgres + MailHog): `docker-compose.dev.yaml`
- Production compose: `docker-compose.prod.yaml`
- API image: `server/BoardSync.Api/Dockerfile` (dev variant: `Dockerfile.dev`)
- Frontend image: `boardsync-ui/Dockerfile`
- Frontend reverse proxy: `boardsync-ui/nginx.conf`
- Environment template: `.env.sample`
- Common tasks: `Makefile`
- Design notes: `docs/`
