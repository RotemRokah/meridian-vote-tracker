# Meridian Vote Tracker

Imports assembly voting files, keeps an audited history of every approved ballot, lets analysts resolve conflicts,
and computes alignment-score snapshots that never change once saved.

Design decisions and trade-offs: [`DECISIONS.md`](DECISIONS.md). How AI was used: [`AI_USAGE.md`](AI_USAGE.md).

## What you need

| To | You need |
|---|---|
| Run the system | Docker Desktop (with `docker compose`) |
| Run the tests | Docker running, .NET 8 SDK (8.0.4xx or later, see `backend/global.json`), Node.js 24 + npm |

Ports 5432, 8080 and 4200 must be free (change them in `.env` if not).

## 1. Put the data files in `data/`

Copy the assignment's `candidate_data` files into a `data/` folder at the repository root:

```
data/
  member_states.csv
  assembly_export_2024_q1.csv
  assembly_export_2024_q1_rev2.csv
  assembly_historic.csv        (103 MB: too big for git, so it is not in the repo)
  voting_sheets_pdfs/
```

`data/` is not in git. The system doesn't read it by itself: these are the files you upload. Two things are committed
so the system and the tests work even without `data/`: the member list the API seeds at startup
(`backend/Meridian.Api/Seed/member_states.csv`) and copies of the two 2024 files for the tests
(`backend/Meridian.Tests/TestData/`, also handy for the quick demo below).

## 2. Run everything

```bash
cp .env.example .env
```

Open `.env` and set `POSTGRES_PASSWORD` to any value. Leave the Azure settings empty: everything then runs locally.

```bash
docker compose up --build
```

The first build takes a few minutes. When the API logs `Now listening on: http://[::]:8080`:

- UI: http://localhost:4200
- API documentation (Swagger): http://localhost:8080/swagger
- PostgreSQL: localhost:5432

On startup the API applies the database migrations and seeds the three users and the member list.

To start again from an empty database (this deletes all imported data and uploaded files):

```bash
docker compose down -v
```

## 3. Quick demo: the assignment's worked example (section 7.2)

Users: **Dana** and **Omer** are Analysts, **Lena** is the Admin. Pick who you are in the top-right corner of the UI.
The API checks every role itself (403); the UI only hides buttons.

1. **Imports** → upload `assembly_export_2024_q1.csv`. Progress shows live; then open the report: 5,731 New,
   151 Contradiction, 11 Invalid, 3 Duplicate, and one normalized code (`deu` → `DEU`).
2. Upload `assembly_export_2024_q1_rev2.csv`: 378 New, 5,576 Unchanged, 6 ballot conflicts, 2 vote-detail conflicts.
3. Upload rev2 **again**: 0 New, 0 new conflicts (the 8 are linked to the ones already open).
4. **Review** → filter by `MA/2024/003` → approve BRA (Y → A) as Dana. Switch to Omer and decide the same conflict:
   you get a message saying Dana already decided it, with the current value.
5. **Votes** → `MA/2024/003` → the timeline shows Y from q1, the conflict from rev2, and Dana's approval.
   As Lena, set a position: weight 7 gives an error under the field; For / 4 saves.
6. **Snapshots** → create one for `MA/2024/003`. The breakdown shows BRA voted A (not counted, so score N/A).
   Selecting a vote without a position is refused, with the list of such votes.

The same through the API (`X-User-Id` header, or "Authorize" in Swagger):

```bash
curl -H "X-User-Id: dana" -F "file=@data/assembly_export_2024_q1.csv" http://localhost:4200/api/imports
```

```bash
curl -H "X-User-Id: dana" "http://localhost:4200/api/conflicts?vote=MA%2F2024%2F003"
```

```bash
curl -H "X-User-Id: dana" -H "Content-Type: application/json" -d '{"decision":"Approve","comment":"checked"}' http://localhost:4200/api/conflicts/4/decision
```

An upload returns `202` with the import id at once; a background worker processes the file. Follow it at
`GET /api/imports/{id}` and read the rows behind the report at `GET /api/imports/{id}/rows?classification=Invalid`.
Other endpoints: `GET /api/votes/{id}/timeline?country=BRA`, `PUT /api/votes/{id}/position` (Admin),
`POST /api/snapshots`, `GET /api/snapshots/{id}/countries/BRA`, `POST /api/snapshots/{id}/publish` (Admin).
All of them are in Swagger.

## 4. Volume: the historic file

Upload `data/assembly_historic.csv` (962,171 rows) the same way. Measured in docker on a Windows laptop:

| | Result |
|---|---|
| Upload (`202`) | 4.8 s |
| Import, end to end | 286–350 s; progress moves every second ("Reading", then "Classifying") |
| API memory during the import | peak 278 MB |
| Full-history snapshot (6,480 votes, 962,109 country-votes) | 14.5 s |
| Vote list, vote details, timeline, import rows by class | 0.04–0.32 s |

Expected report: 962,109 New, 32 Invalid, 30 Duplicate. A full-history snapshot needs every vote to have a position,
set by the Admin; the volume test does this in its own throwaway database.

## 5. Run the tests

Docker must be running: the integration tests start their own PostgreSQL 16 (Testcontainers), separate from the
`docker compose` database.

```bash
dotnet test backend/Meridian.sln
```

```bash
cd frontend && npm ci && npm test
```

- Backend: unit tests for every ingestion rule and the worked example, plus integration tests against real PostgreSQL
  (the 2024 files end to end, conflict and position races, snapshot immutability, 403s, Blob storage on Azurite).
- `HistoricImportTests` runs only when `data/assembly_historic.csv` exists (or `MERIDIAN_HISTORIC_CSV` points to it);
  otherwise it is reported as Skipped. Its import must finish within 8 minutes; it was measured at 422 s. On a busy or
  slow machine it can go over that budget; then run it alone:

  ```bash
  dotnet test backend/Meridian.sln --filter HistoricImportTests
  ```
- `AzureBlobLiveTests` runs only with `MERIDIAN_AZURE_BLOB_ACCOUNT_URL` set and an `az login` session (see below).
- `npm test` clears Vitest's cache first: without that, a change to a component's inline template could run against a
  stale build.

## 6. Azure (optional)

Everything runs locally by default. To use Azure, put these in `.env` (never in a committed file) and run
`docker compose up -d --build api`:

| Setting | Effect |
|---|---|
| `STORAGE_PROVIDER=AzureBlob` + `AZURE_STORAGE_ACCOUNT_URL` | Uploaded files go to Azure Blob Storage (container `imports`) through Entra ID (`DefaultAzureCredential`: Managed Identity in Azure, `az login` on a developer machine), with no key. Needs the *Storage Blob Data Contributor* role. |
| `STORAGE_PROVIDER=AzureBlob` + `AZURE_STORAGE_CONNECTION_STRING` | Same, with an account key (Azurite, or accounts that allow keys). A missing or wrong setting stops the API at startup with a clear message. |
| `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` + `AZURE_DOCUMENT_INTELLIGENCE_KEY` | Turns on the bonus PDF extraction (Review > PDF extractions): a scanned voting sheet is read by Document Intelligence, checked, reviewed and approved into the normal import. Without them the tab says the feature is off. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Logs, HTTP requests and PostgreSQL calls go to Application Insights, each with `CorrelationId` as a field, so one upload can be followed from the browser request to the background import. |

Inside docker on a laptop there is no Entra ID sign-in, so keep `STORAGE_PROVIDER=Local` there. To check a real
storage account from your machine after `az login`:

```bash
MERIDIAN_AZURE_BLOB_ACCOUNT_URL=https://<account>.blob.core.windows.net dotnet test backend/Meridian.sln --filter AzureBlobLiveTests
```

## 7. Development without docker (API + Angular)

The connection string is never in the repo. Set it once with user-secrets (stored outside the repo), using the
password from your `.env`:

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=<from .env>" --project backend/Meridian.Api
```

```bash
docker compose up -d db
dotnet run --project backend/Meridian.Api
cd frontend && npm start
```

The API listens on http://localhost:8080; the Angular dev server on http://localhost:4200 proxies `/api` to it.

The API client in `frontend/src/app/api/api-client.ts` is generated from the API's OpenAPI document, never edited by
hand. After an API change, with the API running on port 8080:

```bash
cd frontend && npm run gen-api
```

New migration: `dotnet tool restore`, then
`dotnet ef migrations add <Name> --project backend/Meridian.Api --output-dir Data/Migrations`.

## Repository layout

```
backend/
  Meridian.Api/      endpoints, auth, background workers, EF Core DbContext + migrations, file store, Azure clients
  Meridian.Domain/   pure logic: row validation, classification, scoring (no database, no framework)
  Meridian.Tests/    unit tests (Domain) and integration tests (Testcontainers)
frontend/            Angular app; src/app/api/ is generated
data/                assignment input files (not in git)
```
