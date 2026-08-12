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

Starts PostgreSQL on port `7000` and MailHog (SMTP `1025`, web UI http://localhost:8025) for
inspecting confirmation and password-reset mail.

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
through domain events on an in-process bus (`IEventBus` / `InMemoryEventBus`).

| Module | Responsibility |
| --- | --- |
| `Modules/OrgProject` | Organizations, workspaces, projects, teams, memberships |
| `Modules/Sprints` | Boards, board columns, sprints, sprint backlogs |
| `Modules/WorkItems` | Work items, comments, history, links, tags |
| `Modules/Rbac` | Role assignments and permission checks |
| `Modules/Activity` | Activity log, built by subscribing to domain events |
| `Shared/Auth` | Users, JWT issuance/refresh, password and email flows |
| `Shared/Kernel` | Event bus, typed configuration, domain exceptions |
| `Shared/Data` | `BoardSyncDbContext` and EF Core migrations |

### Domain model

An **Organization** owns **Teams** and **Projects**. A project is backed by one **Board** with
ordered **BoardColumns**, and holds **WorkItems** (with comments, history, links, and tags).
**Sprints** belong to a team and pull work items into an ordered sprint backlog. Every meaningful
mutation emits a domain event that the Activity module turns into an **ActivityLog** entry.

### Roles

`RoleType` — `OrgAdmin`, `ProjectAdmin`, `TeamMember`, `Reader`, `User` — assigned at a
`RoleScope` of `Organization`, `Project`, or `Team`. `RbacService` resolves the effective role for
a user against a scope; controllers authorize through it rather than through raw claims.

## 3) API Surface

All endpoints are under `/api` and require a bearer token unless noted. Swagger (Development) is
the authoritative reference; the table below is the map.

| Area | Routes |
| --- | --- |
| Auth (anonymous) | `POST /api/auth/{login,register,refresh-token,forgot-password,reset-password,confirm-email,resend-confirmation}` |
| Auth (authenticated) | `POST /api/auth/{logout,revoke-token,change-password}`, `GET /api/auth/me`, `GET|PUT /api/auth/profile` |
| Users | `GET /api/users/me`, `GET /api/users/{userId}`, `GET /api/users/by-email` |
| Search | `GET /api/search` |
| Workspace | `GET /api/workspace/{summary,notifications,activity}` |
| Organizations | `GET|PUT /api/orgs/{orgId}`, `GET /api/orgs/by-slug/{slug}`, `GET|POST /api/orgs/{orgId}/members`, `DELETE /api/orgs/{orgId}/members/{userId}`, `PUT /api/orgs/{orgId}/members/{userId}/role`, `GET /api/orgs/{orgId}/activity` |
| Teams | `GET|POST /api/orgs/{orgId}/teams`, `GET|PUT|DELETE /api/teams/{teamId}`, `GET|POST /api/teams/{teamId}/members`, `GET|DELETE /api/teams/{teamId}/members/{userId}` |
| Projects | `GET|POST /api/orgs/{orgId}/projects`, `GET|PUT /api/projects/{projectId}`, `PUT /api/projects/{projectId}/team`, `GET|POST /api/projects/{projectId}/roles`, `DELETE /api/projects/{projectId}/roles/{userId}` |
| Boards | `GET /api/projects/{projectId}/board`, `GET|PUT /api/boards/{boardId}`, `POST /api/boards/{boardId}/columns`, `PUT|DELETE /api/boards/columns/{columnId}`, `PATCH /api/boards/{boardId}/columns/reorder` |
| Sprints | `GET|POST /api/teams/{teamId}/sprints`, `GET /api/teams/{teamId}/sprints/active`, `GET|PUT|DELETE /api/sprints/{sprintId}`, `PATCH /api/sprints/{sprintId}/status`, `GET|POST /api/sprints/{sprintId}/workitems`, `DELETE /api/sprints/{sprintId}/workitems/{workItemId}`, `PATCH /api/sprints/{sprintId}/workitems/reorder` |
| Work items | `GET|POST /api/projects/{projectId}/workitems`, `GET|PUT|DELETE /api/workitems/{workItemId}`, `PATCH /api/workitems/{workItemId}/state`, `GET|POST /api/workitems/{workItemId}/comments`, `PUT|DELETE /api/workitems/comments/{commentId}`, `GET /api/workitems/{workItemId}/history`, `GET|POST /api/workitems/{workItemId}/links`, `DELETE /api/workitems/links/{linkId}` |
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
| `Telemetry:OtlpEndpoint` | unset | OTLP collector address, e.g. `http://localhost:4317`. Unset means OpenTelemetry is not registered at all — no spans built, nothing exported. `OTEL_EXPORTER_OTLP_ENDPOINT` works too. |
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
