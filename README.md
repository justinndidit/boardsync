# BoardSync

Full-stack app with:

- ASP.NET Core API (.NET 10)
- PostgreSQL
- React + Vite frontend
- Docker-based dev and production workflows

## 1) Local Development Setup

### Prerequisites

- .NET SDK 10
- Node.js 22+
- Yarn
- Docker + Docker Compose

### Start local database

```bash
make dev-infra
```

### Run API migrations

```bash
make migrate-db
```

### Run backend

```bash
make dev-backend
```

### Run frontend

```bash
make dev-frontend
```

Frontend default URL: http://localhost:5173

## 2) Production-Like Local Setup (Docker)

### Create runtime env file

```bash
cp .env.sample .env
```

Update values in `.env`, especially `POSTGRES_PASSWORD` and `APP_ORIGIN`.

### Build and run stack

```bash
make prod-build
make prod-up
```

App URL: http://localhost

### Stop stack

```bash
make prod-down
```

## 3) Production Readiness Checklist

- Use a managed PostgreSQL service or persistent encrypted volume backups.
- Store secrets in a secure secret manager (not in source control).
- Terminate TLS at an ingress/load balancer and force HTTPS.
- Restrict CORS (`AllowedOrigins`) to exact trusted domains.
- Add centralized logging and metrics (OpenTelemetry + dashboard).
- Configure readiness/liveness probing using `/healthz`.
- Enable CI checks for build, lint, and test before deployment.
- Run EF migrations as part of controlled release workflow.

## 4) Relevant Configuration Files

- API startup: `server/BoardSync.Api/Program.cs`
- API production settings: `server/BoardSync.Api/appsettings.Production.json`
- Production compose: `docker-compose.prod.yaml`
- API image: `server/BoardSync.Api/Dockerfile`
- Frontend image: `boardsync-ui/Dockerfile`
- Frontend reverse proxy: `boardsync-ui/nginx.conf`
- Environment template: `.env.sample`
