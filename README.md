# Meridian Vote Tracker

Home assignment. Full docs come later (see `DECISIONS.md`, `AI_USAGE.md`).

## Run everything

```bash
cp .env.example .env      # then set POSTGRES_PASSWORD
docker compose up --build
```

- Frontend: http://localhost:4200
- API + Swagger: http://localhost:8080/swagger
- PostgreSQL: localhost:5432

## Run tests

```bash
dotnet test backend/Meridian.sln
cd frontend && npm ci && npx ng test --watch=false
```

## Local development without docker (API + Angular)

```bash
docker compose up -d db
dotnet run --project backend/Meridian.Api      # http://localhost:8080
cd frontend && npm start                        # http://localhost:4200, /api proxied to 8080
```
