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

The API applies migrations and seeds users + member states on startup.
Users (send as `X-User-Id` header, or use "Authorize" in Swagger): `dana`, `omer` (Analyst), `lena` (Admin).

## Import a CSV

```bash
curl -H "X-User-Id: dana" -F "file=@data/assembly_export_2024_q1.csv" http://localhost:4200/api/imports
```

Returns `202` with the import id right away; a background worker processes it. Follow progress and read the
report at `GET /api/imports/{id}`, and the rows behind it at `GET /api/imports/{id}/rows?classification=Invalid`.
Uploaded files are kept in the `file-storage` docker volume.

## Review conflicts

```bash
curl -H "X-User-Id: dana" "http://localhost:4200/api/conflicts?vote=MA%2F2024%2F003"
```

```bash
curl -H "X-User-Id: dana" -H "Content-Type: application/json" -d '{"decision":"Approve","comment":"checked"}' http://localhost:4200/api/conflicts/4/decision
```

A second decision on the same conflict gets `409` with who decided, when, and the current state.

## Run tests

Docker must be running: integration tests start their own PostgreSQL 16 (Testcontainers).
`backend/Meridian.Tests/TestData/` holds copies of the two 2024 assignment files, so the real-file import test
runs anywhere (the 103 MB historic file stays out of git).

```bash
dotnet test backend/Meridian.sln
cd frontend && npm ci && npx ng test --watch=false
```

## Local development without docker (API + Angular)

The connection string is never in the repo. Set it once with user-secrets (stored outside the repo),
using the password from your `.env`:

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=<from .env>" --project backend/Meridian.Api
```

```bash
docker compose up -d db
dotnet run --project backend/Meridian.Api      # http://localhost:8080
cd frontend && npm start                        # http://localhost:4200, /api proxied to 8080
```

New migration: `dotnet tool restore`, then
`dotnet ef migrations add <Name> --project backend/Meridian.Api --output-dir Data/Migrations`.
