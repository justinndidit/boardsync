.PHONY: dev-infra migrate-init migrate-db dev-backend dev-frontend down-all prod-build prod-up prod-down

# Spin up local database infrastructure
dev-infra:
	docker compose -f docker-compose.dev.yaml up -d

# Build and create initial EF Core migration
migrate-init:
	cd server/BoardSync.Api && dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# Run database schema updates
migrate-db:
	cd server/BoardSync.Api && dotnet ef database update

# Boot up the entire local ecosystem
dev-backend:
	cd server && dotnet watch --project BoardSync.Api/

dev-frontend:
	cd boardsync-ui && yarn dev

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