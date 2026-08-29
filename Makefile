.PHONY: dev-infra migrate-init migrate-db seed-demo dev-backend dev-frontend down-all prod-build prod-up prod-down

# Spin up local database infrastructure
dev-infra:
	docker compose -f docker-compose.dev.yaml up -d

# Build and create initial EF Core migration
migrate-init:
	cd server/BoardSync.Api && dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# Run database schema updates
migrate-db:
	cd server/BoardSync.Api && dotnet ef database update

# Seed a demonstration organization: 4 sprints, 30 work items, and the
# backdated history the reports are reconstructed from. Re-runnable — it
# removes its own organization first. Needs one registered user to build around.
seed-demo:
	docker exec -i boardsync-postgres-dev psql -U postgres -d boardsync_dev \
		-v ON_ERROR_STOP=1 < scripts/seed_demo.sql

# Boot up the entire local ecosystem
dev-backend:
	cd server && dotnet watch --project BoardSync.Api/

dev-frontend:
	cd ui/boardsync && npm run dev

# Kill all background infrastructure volumes
down-all:
	docker compose -f docker-compose.dev.yaml down -v

# Build production images
prod-build:
	docker compose -f docker-compose.prod.yaml build

# Start production stack locally
prod-up:
	docker compose -f docker-compose.prod.yaml up -d

# Stop production stack
prod-down:
	docker compose -f docker-compose.prod.yaml down