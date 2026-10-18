# Decisions

## 1. Ingestion: report, don't guess

- **Order of checks:** each row is validated alone (ING-04) before consistency checks (ING-05) across valid rows, so
  one broken row (a comma in a title shifting the columns) can't make a whole vote look inconsistent.
- **Minimal, visible normalization (ING-13):** trim, uppercase codes (`deu` → `DEU`), strip a byte-order mark (an
  invisible marker at the start of some files), each shown in the report. `Yes`, `0`, `21/02/2024` are **rejected, not
  guessed**: a wrong guess is worse than a rejected row that the next file can bring correctly.
- **Two titles for one vote in one file:** all its rows there are Contradiction (compared exactly: a double space counts).
- **Re-uploads change nothing (ING-12):** a partial unique index (unique only among `Pending` conflicts) stops a second
  open conflict for the same proposal.
- **A proposal already rejected** (same country, field, value) is reported as Conflict, linked to that decision ("already
  rejected on <date> by <user>"); no new conflict opens, so a human's decision isn't reopened by a file.
- **One import at a time**, queued in a database table (survives a restart); a second file waits.

## 2. Concurrency

- **Conflict decisions (CON-05):** `UPDATE … WHERE id = @id AND status = 'Pending'`, in one transaction with the data
  change and the audit row. The database locks the row, so a simultaneous second decision waits, then updates 0 rows
  and gets **409** with who decided, when, and the current value.
- **Positions:** the version is PostgreSQL's `xmin` (a hidden column that changes on every update); stale version → 409.

## 3. Scoring

Where a country voted **Y or N**, the weight is added to *counted*, and to *matched* if the ballot agrees
with the stance (Y on For, N on Against). `score = round(100 × matched / counted, 2)`.
- **A and X count nothing** (shown as counts). **No Y/N at all → N/A, unranked**: 0 would mean total disagreement.
  Ties share a rank (90, 75, 75 → 1, 2, 2).
- **POS-04:** a vote without a position blocks creation; the creator may exclude such votes explicitly, and the
  exclusion is stored in the snapshot's rules.

## 4. Database and history

**Current state + append-only log.** `country_votes` holds only the approved ballot and its source file;
`audit_events` records each change (who, when, old, new, file) in the **same transaction**, so no change exists without
its record. Database triggers reject UPDATE/DELETE on the log and on snapshot results. `import_rows` keeps every line of
every file with its class and reason. History answers: *why BRA's ballot changed* → the vote's timeline filtered to BRA;
*a file from a month ago* → its report and rows; *who changed a weight* → `PositionSet` events with old/new; *what a
snapshot used* → `snapshot_inputs`, a **frozen copy** of every ballot, stance and weight (SNP-04).

`snapshot_inputs` has **no foreign keys**, on purpose: PostgreSQL's query timing report showed the full-history snapshot
spent 120 of its 128 seconds checking them row by row. The rows are copied from tables already checked and can never
change, so the keys protected nothing. Now 14.5 s.

## 5. Azure

Off by default; switched on by `.env` only.
- **Blob Storage** (file storage) behind the same interface as local disk. Keys are blocked on the account, so access is
  by **Entra ID** (Azure sign-in: `az login` on a laptop, the app's own Managed Identity in Azure). Verified once on the
  real account; automated tests use Azurite, a local emulator.
- **Application Insights** (hosted logs): a UI request was found in the portal by its correlation id.
- **Not used:** Azure OpenAI for PDFs (non-deterministic, no per-cell confidence), Azure PostgreSQL (reviewers run
  everything locally), Cosmos DB (relational data), Key Vault (secret store; comes with Managed Identity).

## 6. Bonus: PDF extraction

**Document Intelligence (layout), not a language model (PDF-02):** cells and checkboxes come with a confidence each; a
language model can invent a ballot silently. **Unticked boxes always get confidence 0.10**, so only a ticked box's
confidence counts. Flagged: 0 or 2+ ticks, a tick under 0.8, bad or repeated codes, totals mismatch.
**Built:** upload, background read (2-min timeout, retry), these checks, ballot edit, approve (a normal import named
after the PDF, same rules as CSV), reject. Not built: editing codes or header, removing a row. **Quality:** hand-label the six sheets;
count wrong ballots, and wrong ones *not* flagged.

## 7. Before production

**Import speed** (~5 min, target 3): an unlogged (no crash log) staging table. **Keyset pagination**: `OFFSET` reads
every skipped row (page 5,000: 2.7 s). **A mistaken decision can't be fixed today:** add an Admin Reopen/Override with
a required comment and an audit event (only a person, on record).

## 8. Next phase with 4 developers

By feature, each owning a slice from database to UI: (1) ingestion speed; (2) PDF extraction up to Review;
(3) snapshots and reporting; (4) platform: auth, CI/CD, Azure deployment, end-to-end tests.
**Standards first:** one error format (ProblemDetails, the HTTP standard); a generated API client (CI fails if stale);
every write with its audit row in one transaction; every rule tested, endpoints on real PostgreSQL; correlation id in
every log line.
