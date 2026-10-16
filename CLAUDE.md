# CLAUDE.md: Meridian Vote Tracker

Home assignment for a **Senior .NET Full-Stack Lead** role. 3-day window: **day 1 = 2026-10-15**, submission end of day 3 (2026-10-17).
Full spec (Hebrew): `data/README-Home-Assignment-dot-net.pdf`; example screens: `data/Meridian_Vote_Tracker_*.pdf`. The requirements in section 7 of the spec win over the example screens.

## How to work with me (the user)

- Talk to me in **Hebrew**. Code, comments, commits, docs: **English**.
- I come from Python/QA automation and I'm new to ASP.NET Core and Angular. I will be asked in an interview to **explain and change any line**. So: keep it simple, no clever abstractions, explain each non-obvious choice in 2–3 lines when you finish a step.
- Code comments: short and human ("skip rows already approved"), not tutorial prose.
- **Work one plan step at a time.** Before coding a step: show a short plan and wait for my OK. At the end of a step: run the tests, summarize what was built and which requirement IDs it covers, then commit.
- If a requirement is ambiguous, don't silently pick: propose a decision, and once I agree, add it to `DECISIONS.md` (draft section at the bottom of this file until that file exists).
- Prefer the spec's wording over your instincts. "Partial with clear reasoning beats complete and shallow."

## Hard rules

1. **Approved data is never overwritten automatically** by any process (ING-08). Only a human conflict decision changes it.
2. **Every change is recorded** (who, when, old, new, source). Nothing is deleted or overwritten without a trace.
3. **Saved snapshots never change** (SNP-04).
4. **No secrets in code or git.** Config via env vars / `.env` (gitignored). Commit `.env.example` with placeholders only.
5. **`data/` is gitignored.** `assembly_historic.csv` is 103 MB and exceeds GitHub's 100 MB file limit. Never commit it.
6. **Every ingestion rule has a test.** Classification logic is pure code with unit tests; at least one integration test against real PostgreSQL (Testcontainers).
7. Permissions are enforced **in the API** (403), not only by hiding buttons.
8. Uniform error format: RFC 7807 ProblemDetails everywhere. Validation errors carry field names. Concurrency conflicts → 409 with the current state.
9. Commit at the end of every step with a meaningful message (the commit history is graded).

## Stack

- Backend: ASP.NET Core 8, EF Core + Npgsql (migrations, constraints), raw SQL/`COPY` where volume needs it (bulk staging, snapshot calc).
- DB: PostgreSQL 16 (docker-compose for dev and for reviewers).
- Frontend: Angular (current), **API client generated from OpenAPI** (NSwag, `npm run gen-api`). Never hand-write API client code.
- Background work: DB-backed job queue + `BackgroundService` (status/progress persisted in DB → survives page refresh and restart).
- Progress in UI: polling (~1s). SignalR is a "would do in prod" note.
- Tests: xUnit, Testcontainers for PostgreSQL.
- `docker compose up` starts everything: db, api (applies migrations + seeds users and member states), frontend.

## Project structure

```
backend/
  Meridian.Api/      endpoints, auth, OpenAPI, ProblemDetails, correlation middleware,
                     EF DbContext + migrations, workers, file store, Azure clients
  Meridian.Domain/   pure logic: row validation, classification, scoring. No DB, no framework deps.
  Meridian.Tests/    unit (Domain) + integration (Testcontainers)
frontend/            Angular app; src/app/api/ is generated
data/                input files (gitignored)
docker-compose.yml  .env.example  README.md  DECISIONS.md  AI_USAGE.md  CLAUDE.md
```
Three projects on purpose: isolate the logic that matters for testing, without layers I can't justify.

## Users and auth (simplified on purpose)

Seeded users: **Dana** (Analyst), **Omer** (Analyst), **Lena** (Admin). Client sends `X-User-Id`; a custom auth handler maps it to a principal with a role; policies `Analyst` (Analyst or Admin) and `Admin`. UI: user switcher in the header.

| Action | Analyst | Admin |
|---|---|---|
| Upload, view reports, decide conflicts, create snapshots | ✓ | ✓ |
| Set position/weight (POS-01), mark snapshot published (SNP-05) | ✗ (403) | ✓ |

---

## Requirements (IDs from spec section 7)

### 7.1 CSV ingestion
- **ING-01** Upload request returns immediately; processing continues in background.
- **ING-02** Live progress: rows processed / total. Survives page refresh.
- **ING-03** Group rows by `assembly_vote_id`; rows of the same vote are not contiguous.
- **ING-04** Per-row checks: valid structure (column count, CSV format), required fields, country code exists in member list, ballot ∈ {Y,N,A,X}, date `yyyy-mm-dd`, country was a member on the vote date.
- **ING-05** In-file consistency: same vote details (session, date, title) on all rows of a vote; one value per country per vote.
- **ING-06** Each row gets **exactly one** classification:

| Class | When | Action |
|---|---|---|
| New | vote or country-vote doesn't exist yet | ingest as approved |
| Unchanged | identical to approved | nothing |
| Duplicate | row identical to another row in the same file | ignore copy, count in report |
| Invalid | fails an ING-04 check | reject; report line no., reason, original value |
| Contradiction | same file: same country+vote with different values, or vote details differ between rows | pick nothing, report |
| Conflict | differs from existing approved data | don't touch approved; open conflict for a human |

- **ING-07** An invalid row never fails the whole file.
- **ING-08** Approved data is never auto-overwritten.
- **ING-09** A vote missing from a new file is not deleted or changed.
- **ING-10** A vote can arrive in parts across files. Missing countries added later = **New**, not Conflict.
- **ING-11** Country identity is the code. Different name for same code (Turkey/Türkiye) is not a conflict.
- **ING-12** Re-uploading the same or overlapping file creates no duplicates and does not reopen a conflict that is already pending.
- **ING-13** Problems are reported, not silently fixed. Any normalization is documented and shown in the report.
- **ING-14** Report at end of each import: counts per class, what happened per vote, list of rejected/flagged rows with line no., reason, original value.
- **ING-15** Report is persisted and can be opened days later.

### 7.2 Worked example (must be reproducible in tests)
Original file → rows: BRA/101 Y new; IND/101 Y new; JPN/102 Y new; JPN/102 Y duplicate; FRA/103 Y + FRA/103 N contradiction (neither ingested); FRN/104 invalid (unknown code); NZL/104 invalid (not a member on date).
Revised file → BRA/101 A conflict (approved Y stays); IND/101 Y unchanged; FRA/103 N new; FRA/104 X new; BRA/105 Y new (new vote); vote 101 title changed → conflict on vote details; vote 102 absent → no action.
Decision: Dana approves BRA/101=A → approved becomes A; Y kept in the timeline with source file and approver; the published snapshot still shows Y; a new snapshot uses A. Omer deciding the same conflict at the same moment → only one decision lands, he gets a clear message. Re-upload of the revised file → 0 new, 0 new conflicts.

### 7.3 Conflicts
- **CON-01** List of pending conflicts, filter by vote.
- **CON-02** Approved vs proposed value side by side, with source of each: file, uploader, time.
- **CON-03** Conflict can be on a country-vote or on vote details (date, title, session).
- **CON-04** Approve → proposed becomes approved. Reject → approved stays. Each decision records who, when, optional comment.
- **CON-05** Two analysts deciding the same conflict at once → exactly one decision lands; the other gets a clear message and sees current state. Nothing silently overwritten.
- **CON-06** Decided conflicts remain viewable with the decision.

### 7.4 History and audit
Must answer anytime: why BRA's ballot in vote X changed, from which file, who approved; what was in a file uploaded a month ago and what happened to each row; who changed a vote's weight and the previous value; exactly which data the last published snapshot was based on.
- **HIS-01** Every import saved: file, uploader, time, report, per-row outcome.
- **HIS-02** Every approved value knows its source; previous values kept.
- **HIS-03** Every conflict decision and position/weight change saved: who, when, old, new.
- **HIS-04** Vote screen shows a full timeline of everything that happened to it.

### 7.5 Positions and weights
- **POS-01** Admin sets per vote: stance (For/Against) and weight 1–5. Analyst can't; enforced in API.
- **POS-02** Validation errors (e.g. weight 7) shown next to the field.
- **POS-03** Every change logged (HIS-03).
- **POS-04** A vote without a position never silently enters a snapshot.

### 7.6 Snapshots
- **SNP-01** Select votes by filter (date range or session) and/or explicit list.
- **SNP-02** Computation runs in background over full history (~1M country-votes); user not blocked, sees status.
- **SNP-03** Result: score 0–100 per country, rank, per-country breakdown with each vote's contribution.
- **SNP-04** Saved snapshot never changes. Data fixes, conflict decisions, weight changes don't affect it. Opening it a year later shows identical numbers.
- **SNP-05** Admin marks one snapshot per quarter as published.

Formula: for each country, every vote in the snapshot where the country voted Y or N adds its weight to the denominator; if the ballot matches the institute's stance, the weight is added to the numerator too. `score = 100 × matched / counted`. Example: Y on weight-4 vote (stance For) + N on weight-2 vote (stance For) → 100×4/6 = 66.7.

### 7.7 Frontend
Minimal, 4 screens: **Imports** (upload, live progress, history, report), **Review** (pending conflicts; tabs, PDF extractions in bonus), **Votes** (list + detail: country votes, position/weight for admin, timeline), **Snapshots** (create, list, scores, per-country breakdown, publish). Design is not graded; integration is. Generated API client. Field-level validation errors. Long jobs show status, never freeze. Concurrency conflicts shown as a clear message, not a generic error.

### 8 Bonus: PDF extraction (only if core is done, ≥4–5h left on day 3)
- **PDF-01** Analyst uploads PDF; extraction status shown while it runs in background.
- **PDF-02** Uses Azure AI services (choice explained in DECISIONS).
- **PDF-03** Model output is untrusted: validate against expected structure and domain rules (unknown code, country twice, totals not matching the printed totals) before review.
- **PDF-04** Extracted data goes to Review; analyst can edit/approve/reject; low-confidence values and detected issues clearly flagged.
- **PDF-05** Nothing extracted becomes approved without review. Approved extraction goes through the same ingestion rules as CSV (new / unchanged / conflict).
- **PDF-06** AI failures shown to the user; jobs never hang forever; retry possible.

### 9 Technical requirements
ASP.NET Core 8+, PostgreSQL, Angular. `docker compose up` runs all components + seed data (members, users); components connect to Azure via config. OpenAPI documented, uniform error format. Schema via migrations with integrity constraints. At least one integration test against real PostgreSQL. **Correlation ID** traceable in logs from UI request → API → AI service calls. No keys/connection strings in code. Out of scope: deploying to Azure, design, user management, production auth.

### 11 Deliverables
Git repo with meaningful commit history; `README.md` (run system + tests); `DECISIONS.md` (≤2 pages: key decisions and trade-offs, scoring rules, DB/history design, Azure usage and why not others, bonus extraction-quality measurement, what to change before production, how to split next phase among 4 devs, standards to set first); `AI_USAGE.md` (tools used, where AI output was rejected/fixed).

---

## Data files (`data/`)

| File | Content |
|---|---|
| `member_states.csv` | 151 rows, 150 codes. `country_code,country_name,member_from,member_to`; empty `member_to` = still member. ISL has 2 rows (left 2019-12-31, rejoined 2022-01-01). NZL left 2021-06-30. Seed at startup. |
| `assembly_export_2024_q1.csv` | 5,896 rows, 40 votes (MA/2024/001–040), published 2024-04-02. First import. |
| `assembly_export_2024_q1_rev2.csv` | 5,960 rows, 40 votes, published 2024-04-20. Upload after q1. **MA/2024/040 absent** (ING-09), **MA/2024/041 new**. |
| `assembly_historic.csv` | 962,171 rows, 6,480 votes, 2015–2023, unsorted, rows of a vote not contiguous. Volume/perf test. No overlap with 2024. |
| `voting_sheets_pdfs/` | 6 scanned PDFs (image-only, no text layer), 2 pages each, pre-2015. Includes `MA_2011_042_corrigendum.pdf` (a correction of MA_2011_042). Bonus only. |

CSV columns: `assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at`.

### Known data problems (found by profiling; line numbers are 1-based incl. header)

**q1 (`assembly_export_2024_q1.csv`):**
- Lines 1045–1047: exact duplicates of 896–898 → Duplicate.
- Line 2143: country `FRN` (MA/2024/015) → Invalid, unknown code.
- Line 2594: country `deu` (MA/2024/018) → normalize to `DEU` and **report the normalization** (decision below).
- Line 2842: ballot `Yes` (MA/2024/020, ARG) → Invalid.
- Line 2846: ballot empty (MA/2024/020, AZE) → Invalid, required field.
- Line 3139: 5 columns, `AGO;N` (semicolon delimiter) → Invalid, structure.
- Lines 3880–3885: date `21/02/2024` (MA/2024/027) → Invalid date. The rest of MA/2024/027 has `2024-02-21`.
- MA/2024/012 FRA: lines 1695 (Y) and 1793 (N) → Contradiction.
- MA/2024/025: three title variants on its rows: normal, double space (`Nuclear  Non-Proliferation…`), and `… (draft)` → vote-details inconsistency.
- MA/2024/033 NZL: not a member on vote date → Invalid.

**rev2 vs q1 (expected outcomes after q1 then rev2):**
- Ballot conflicts (approved in q1, different in rev2): MA/2024/003 BRA Y→A, MA/2024/003 IND Y→N, MA/2024/010 JPN Y→X, MA/2024/019 ZAF Y→N, MA/2024/028 CAN Y→N, MA/2024/028 MEX Y→A.
- Values that were not ingested from q1 (invalid/contradiction) and now come clean → New: MA/2024/020 ARG (A), MA/2024/020 AZE (A), MA/2024/012 FRA (A).
- Vote-detail conflicts: MA/2024/009 title `… in Coastal Zones` → `… in Coastal Zones: Revised`; MA/2024/014 date 2024-02-05 → 2024-02-06.
- ~72 country rows that were missing from existing votes in q1 appear in rev2 → New (ING-10).
- No valid q1 country row is absent from rev2 (besides MA/2024/040); only the invalid q1 rows FRN/015 and NZL/033 don't reappear → no action either way (ING-09). (Corrected 2026-10-16: an earlier note said "4 rows"; a per-vote comparison of both files shows 0.)
- TUR appears as both `Turkey` and `Türkiye` across files → not a conflict (ING-11).
- Re-upload rev2 → 0 new, 0 new conflicts.

**historic:**
- 11 rows with 7 columns (e.g. lines 134746, 335875, 366927, …) → Invalid, structure.
- 4 rows where an unquoted comma in the title shifted the columns (lines 521760, 687843, 736765, 922345): country_code becomes ` Including Capacity Building` / ` Navigation and Trade`, ballot empty → Invalid. **Important:** these malformed rows must not make the whole vote look "inconsistent". Run ING-04 per-row validation first; run ING-05 consistency checks only across valid rows.
- `XXX` country code on 10 rows → Invalid.
- Ballots `0` (3), `YES` (1), `?` (1), empty → Invalid.
- 30 exact duplicate rows → Duplicate.

---

## Data model

Principle: **current-state tables + append-only audit log**, written in the same transaction. Snapshots copy their inputs.

| Table | Key columns | Notes |
|---|---|---|
| `users` | id, name, role | 3 seeded rows |
| `member_states` | PK (code, member_from), name, member_to | multiple periods per code |
| `imports` | id, file_name, file_sha256, storage_path, uploaded_by, uploaded_at, status (Queued/Running/Completed/Failed), total_rows, processed_rows, summary jsonb, correlation_id, error | also acts as the job queue |
| `import_rows` | id, import_id, line_no, raw_line, parsed fields (nullable), classification, reason, normalization_note, conflict_id | per-row outcome (HIS-01, ING-14) |
| `votes` | id, assembly_vote_id UNIQUE, session, vote_date, title, source_import_id | current approved details |
| `country_votes` | PK (vote_id, country_code), ballot CHECK in (Y,N,A,X), source_import_id, approved_by (null = ingested), approved_at | **current approved value only** |
| `conflicts` | id, vote_id, country_code NULL (=vote details), field (Ballot/Title/Date/Session), approved_value, approved_source_import_id, proposed_value, proposed_import_id, status (Pending/Approved/Rejected), decided_by, decided_at, comment | partial UNIQUE (vote_id, coalesce(country_code,''), field, proposed_value) WHERE status='Pending' |
| `vote_positions` | PK vote_id, stance (For/Against), weight CHECK 1..5, updated_by, updated_at | |
| `audit_events` | id, vote_id, country_code NULL, event_type, old_value, new_value, import_id NULL, conflict_id NULL, actor, at, correlation_id | **INSERT only.** Feeds the timeline (HIS-02..04) |
| `snapshots` | id, name, filter jsonb, rules jsonb, status, created_by, created_at, completed_at, quarter, is_published, published_by, published_at | partial UNIQUE (quarter) WHERE is_published |
| `snapshot_inputs` | snapshot_id, vote_id, country_code, ballot, stance, weight | **frozen copy** of exactly what was used |
| `snapshot_scores` | snapshot_id, country_code, score NULL, rank NULL, matched_weight, counted_weight, counted_votes, abstain_count, absent_count | |

Immutability: DB trigger rejects UPDATE/DELETE on `snapshot_inputs`, `snapshot_scores`, `audit_events` (snapshots table: only status/publish columns may change).

### Key mechanisms
- **Conflict decision concurrency (CON-05):** `UPDATE conflicts SET status=@s, decided_by=…, decided_at=now() WHERE id=@id AND status='Pending'` inside a transaction that also updates `country_votes`/`votes` and writes `audit_events`. 0 rows affected → 409 ProblemDetails with current state (who decided, when, value).
- **No reopened conflicts (ING-12):** partial unique index above; ingestion links the row to the existing pending conflict instead of inserting.
- **Previously rejected proposal re-arrives:** link to the existing decided conflict, don't open a new one; classify as Conflict with reason "already rejected on <date> by <user>". (Agreed.)
- **Imports run one at a time** (single worker, `FOR UPDATE SKIP LOCKED` pick) → no two imports touch the same vote concurrently.
- **Large files:** phase 1 streams the CSV and bulk-writes raw rows to `import_rows` (Npgsql binary COPY), updating `processed_rows`; phase 2 reads `import_rows` ordered by `assembly_vote_id`, processes one vote at a time (load its approved data, classify via Domain, write results) in batched transactions. Memory stays bounded.
- **Raw file storage:** `IFileStore` abstraction; local disk by default, Azure Blob when configured.
- **Correlation ID:** Angular interceptor sets `X-Correlation-ID` → API middleware (generate if missing, log scope, echo in response) → stored on import/snapshot jobs and restored in the worker's log scope → forwarded to Azure AI calls. Optional Azure Monitor/App Insights exporter via config.

---

## Decisions (draft; move to DECISIONS.md)

- **Normalization (ING-13):** trim whitespace; uppercase country codes (`deu`→`DEU`), flagged in the report as "normalized". Ballot variants (`Yes`, `YES`, `0`, `?`) and non-ISO dates are **rejected, not guessed**. Critical data: rejecting is safer than guessing.
- **Vote details inconsistent inside a file:** all valid rows of that vote in that file are Contradiction; vote not ingested from this file. Can't know which title is right.
- **Title comparison (agreed):** exact match after trim; internal whitespace is NOT collapsed, so q1 MA/2024/025 (double-space variant) is a Contradiction. We report problems, we don't fix them.
- **Same country + same ballot twice, rows not identical (agreed):** e.g. Turkey/Türkiye or different `published_at`. First row is classified normally, the rest are Duplicate with a reason naming the differing fields. Not a Contradiction: the value agrees. Doesn't occur in the given files.
- **Vote created only if ≥1 ballot is ingested (agreed):** a vote whose rows are all invalid/contradictory isn't created empty (outcome `NotCreated`); it's created by the first file that brings a usable row.
- **Required fields (agreed):** all except `country_name` and `published_at` (informational, not used in any decision).
- **Invalid rows report every issue (agreed),** not only the first: field, reason, original value.
- **BOM (agreed):** stripped and noted in the import report as a file-level normalization. Note: .NET `StreamReader` strips it silently by default, so the importer must detect it explicitly.
- **Wrong header (agreed):** the whole import fails with a clear error; no row of such a file is trusted.
- **Order of checks:** per-row validation (ING-04) → exact duplicates → vote-details consistency (ING-05) → one ballot per country (ING-05) → compare with approved (New/Unchanged/Conflict). Malformed rows never reach the consistency check.
- **Vote-details conflict doesn't block ballots:** country rows are still classified on their own ballot (7.2: IND/101 Unchanged while 101's title is in conflict).
- **Scoring:** A and X don't count (formula counts only Y/N), shown in breakdown. Non-member votes can't exist (rejected at ingestion). Country with zero counted votes → score N/A, unranked (0 would be a lie). Vote without position → snapshot creation is blocked with the list of such votes; admin can explicitly exclude them, recorded in `rules`. Rules stored per snapshot (jsonb) and shown in its breakdown.
- **Ranking ties:** same score → same rank (competition ranking 1,2,2,4).
- **Quarter for publishing:** derived from the snapshot's vote date range, or chosen on publish. Re-publishing a different snapshot for the same quarter: admin must unpublish first (audited).
- **Azure:** use Document Intelligence (bonus; layout model gives tables + selection marks with per-cell confidence), Blob Storage (raw files, behind interface), Application Insights (if time). Don't use: Azure OpenAI/Claude (non-deterministic, no per-cell confidence), Azure PostgreSQL (compose must run for reviewers without depending on the assignment window; connection string is configurable), Key Vault (local dev would still need credentials; prod = Managed Identity + Key Vault), Cosmos DB (data is relational with cross-table constraints).
- **Data access:** EF Core for CRUD + migrations + constraints; raw SQL for COPY and the set-based snapshot calculation over ~1M rows.
- **Seed file in git (agreed):** `member_states.csv` (4 KB) is copied to `backend/Meridian.Api/Seed/` and committed; without it `docker compose up` can't seed on a reviewer's machine. The large `data/` files stay out.
- **User ids (agreed):** readable strings `dana`, `omer`, `lena`, used in `X-User-Id` and as `actor` in audit rows.
- **403 test timing (agreed):** step 2 tests the policies directly (Analyst denied `Admin`); the HTTP 403 test is written in step 7 against the first real Admin endpoint, instead of adding a fake endpoint.
- **Migrations at API startup:** simple for compose and reviewers; in production they'd be a separate deploy step.
- **Enums stored as text** with a CHECK constraint per column (readable in SQL, safe against typos).
- **Test fixtures (agreed):** q1 and rev2 (~1.2 MB) are committed under `backend/Meridian.Tests/TestData/` (marked `binary` in `.gitattributes`, so the BOM and bytes stay exact). The real-file test must run for reviewers; historic stays out.
- **Header checked at upload (agreed):** wrong header → 400 on the `file` field, no import created. The worker checks it again before processing.
- **Same file twice (agreed):** processed normally; the report notes "Identical file to import #X" (by SHA-256). ING-12 makes it a no-op.
- **Interrupted import (agreed):** an import still `Running` when the API starts is marked `Failed` with a clear message; not resumed. Re-upload is safe (idempotent classification). Assumes one API instance runs the worker.
- **Audit granularity (agreed):** per vote per import (`VoteCreated`, `BallotsAdded` with a count) and per conflict (`ConflictOpened`). Not one event per ingested ballot (~1M rows for historic); a ballot's origin is `country_votes.source_import_id` + `import_rows`.
- **Phase 2 writes via EF (agreed),** one transaction per batch of 200 votes; measure in step 9 and switch to COPY only if needed. Row outcomes are written with one `UPDATE … FROM unnest(arrays)` per batch.
- **Progress meaning:** `total_rows` = data lines (counted in a pre-pass); `processed_rows` = rows with a final class (invalid rows while reading, valid rows per vote batch). Small files jump in one step; historic moves in ~33 steps.
- **Malformed requests → 4xx, not 500:** `BadRequestExceptionHandler` maps ASP.NET's `BadHttpRequestException` to its own status (400 broken form, 413 upload over 200 MB) as ProblemDetails.
- **Conflict list filter (agreed):** `?vote=` takes the assembly vote id users see (`MA/2024/003`), not the DB id; `?status=` defaults to `Pending` (decided ones via `Approved`/`Rejected`, CON-06).
- **Stale approved value (agreed):** approve only applies if the approved value is still the one the conflict was opened against (`UPDATE … WHERE ballot = @approvedValue`); otherwise 409 "Approved value has changed" with the current state, and nothing changes. Typical case: two pending proposals (A and N) for one ballot; approving A makes N stale, a human must reject or re-review it. Reject is always allowed.
- **One audit event per decision (agreed):** `ConflictApproved`/`ConflictRejected` with old → new, `import_id` = the proposed file, actor, correlation id. The comment lives on the conflict row (the event points to it via `conflict_id`).
- **Approved ballot gets the new source (agreed):** on approve, `country_votes.source_import_id` = proposed file, `approved_by`/`approved_at` = the decider (the "changed by decision" marker for step 6).
- **Vote details have one source (agreed, simplification):** approving a title/date/session conflict sets `votes.source_import_id` to the proposed file; per-field history is in the audit log.
- **Comment optional, ≤1000 chars (agreed);** `decision` is text (`Approve`/`Reject`, case-insensitive) so a wrong value is a field error.
- **Decision concurrency, how it works:** one transaction; the claim `UPDATE conflicts … WHERE id = @id AND status = 'Pending'` takes a row lock, so a simultaneous second decision waits, then matches 0 rows → 409 "Conflict already decided" with who/when/current state. Tested with 10 simultaneous Approve/Reject pairs, and the test was confirmed to fail when the `status = 'Pending'` guard is removed.
- **Vote route uses the DB id (agreed):** `/api/votes/{id}`; the assembly id has slashes. Find a vote with `GET /api/votes?search=MA/2024/003`.
- **Timeline = audit + file rows (agreed):** `audit_events` give what changed; `import_rows` give every file that touched the vote, even with no change ("rev2.csv: 148 Unchanged, 1 Conflict", "q1.csv: 149 Contradiction"). No extra audit row per unchanged row. File entries are placed at the import's start time, so they come before the changes that import made.
- **Timeline `?country=` (agreed):** that country's row from each file (line, value, class, reason) plus its events. Answers "why did BRA's ballot change, from which file, who approved" in one call.
- **Country name from the member list (agreed):** the latest membership period's name, not the file's (ING-11).
- **Vote list (agreed):** paged (default 50, max 500), newest first; filters `search` (id or title, ILIKE), `session`, `from`/`to`; `from > to` → 400 on `from`.
- **Index for the timeline (agreed):** `import_rows (assembly_vote_id, country_code)`, migration `AddImportRowsVoteIndex`.
- **Log scopes as message templates:** `BeginScope("CorrelationId:{CorrelationId}", id)`. A `Dictionary` scope printed only its type name on the console, so the correlation id was invisible in logs until step 4 (fixed, covered by a test that captures log lines).

---

## Plan (one step at a time; commit at the end of each)

### Day 1: data path
1. **Scaffold.** `git init`, `.gitignore` (incl. `data/`, `.env`, bin/obj/node_modules), solution with 3 projects, Angular app, `docker-compose.yml` (postgres, api, frontend), `.env.example`. API: health endpoint, OpenAPI/Swagger, ProblemDetails, correlation-id middleware. ✅ when `docker compose up` serves Swagger and Angular hello page.
2. **Schema + seed.** EF entities + first migration (all tables above, constraints, partial unique indexes, immutability triggers). Seed users and `member_states.csv` at startup. Auth handler + policies. Integration test: DB up, seeds present, analyst gets 403 on an admin endpoint.
3. **Domain: validation + classification.** Pure functions. Unit tests reproduce section 7.2 row by row, plus each ING rule (04, 05, 06, 09, 10, 11, 12, 13) and the historic edge case (malformed row must not taint vote consistency).
4. **Import pipeline.** `POST /imports` (store file, enqueue, return 202 + id), worker (phase 1 COPY, phase 2 per-vote), progress fields, report (`GET /imports`, `GET /imports/{id}`, `GET /imports/{id}/rows?classification=`). Integration test with real files: q1 → rev2 → rev2 again; assert the expected outcomes listed under "rev2 vs q1".

### Day 2: decisions, history, snapshots
5. **Conflicts.** `GET /conflicts?status=&voteId=`, `GET /conflicts/{id}`, `POST /conflicts/{id}/decision` (approve/reject + comment) with atomic update and 409. Test: two parallel decisions → one 200, one 409.
6. **History.** Audit events from imports, decisions, positions. `GET /votes`, `GET /votes/{id}` (details, country votes with "changed by decision" marker, position, open conflicts), `GET /votes/{id}/timeline`.
7. **Positions.** `PUT /votes/{id}/position` (Admin only, validation → field errors, audit old/new). Includes the HTTP test: Analyst gets 403 ProblemDetails (moved here from step 2).
8. **Snapshots.** `POST /snapshots` (filter/list, missing-position check), background calc (set-based SQL into `snapshot_inputs` then `snapshot_scores`), `GET /snapshots`, `GET /snapshots/{id}`, `GET /snapshots/{id}/countries/{code}` (per-vote contribution), `POST /snapshots/{id}/publish` (Admin). Test: snapshot → approve conflict → snapshot unchanged; new snapshot uses new value.
9. **Historic import.** Run `assembly_historic.csv`, measure time, fix bottlenecks; snapshot over full history.

### Day 3: UI and wrap-up
10. **Frontend.** NSwag generation script, user switcher + `X-User-Id`/correlation interceptor, 4 screens, polling progress, field errors, 409 message.
11. **Azure (config-driven, optional).** Blob file store, App Insights exporter. Keys only in `.env`.
12. **Docs.** README (run + tests from scratch: `docker compose down -v && docker compose up`), DECISIONS.md, AI_USAGE.md.
13. **Bonus PDF** only if ≥4–5h left: Document Intelligence layout → parse rows + selection marks → validate (codes, duplicates, totals vs printed totals) → Review tab → approve through the same ingestion path.

**Cut order if behind:** bonus → App Insights → Blob (keep local disk) → timeline polish → list filters. **Never cut:** no-overwrite, conflict concurrency, snapshot immutability, import report, integration test.
