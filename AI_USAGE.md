# AI usage

## Tools used

- **Claude chat:** reading the assignment PDFs, profiling the data files (data issues by line number, expected conflict counts after q1 → rev2), planning the work in 13 steps (the plan is in `CLAUDE.md`), writing `CLAUDE.md`, second opinions, and guidance in the Azure portal.
- **Claude Code** (Anthropic), in the Claude desktop app (Opus): planning each step, writing code and tests, running builds, tests and docker checks. I reviewed and approved every plan step and every decision before it was implemented. Rules in `CLAUDE.md`: show a plan and wait for my approval, a test for every ingestion rule, a commit at the end of each step, no secrets in code.
- **`CLAUDE.md` as shared memory** between sessions: when the context filled up, I opened a new session and it continued from there.

## Where AI output was rejected or corrected

### Demo positions for the historic data — rejected by me

**What the AI proposed.** To compute a snapshot over the full history (SNP-02, ~6,480 votes), every vote needs a position (POS-04). The AI proposed a Python script that would call the API as Lena (the Admin) and set a deterministic, made-up stance and weight on every vote without a position, directly in the regular docker database. The changes would go through the API (permissions, validation, audit), and the script's requests would carry a recognizable correlation id.

**Why I rejected it.** A position is a policy decision made by a person at the institute; a script cannot decide it. Running the script on the regular database would have filled it with 6,480 "decisions by Lena" that Lena never made: fake data, misleading audit history, and scores that look real but mean nothing. A correlation id doesn't fix that: the data itself would be wrong. I asked whether the real Lena would still have to decide them; the AI agreed the values were made up and would mean nothing as scores.

**What we did instead.** The full-history performance check runs only in an opt-in integration test against a disposable PostgreSQL (Testcontainers), where test positions are created and thrown away with the database. The regular docker database keeps only real decisions: a full-history snapshot there stays blocked by the POS-04 check until a person sets positions. For production, the right tool would be an Admin "positions import" from the institute's own records, with validation and audit (documented, not built).

**What it found afterwards.** The AI then flagged that during an earlier docker check, while building snapshots, it had already set positions on 41 votes the same way and published a snapshot of the 2024 Q1 votes. The reports were produced from a separate clean stack, and I approved resetting the main database.

### Azure resources — corrected by me

Both the chat and Claude Code first told me to create a new Resource Group and Storage Account. The assignment provides a ready Resource Group, and I had no permission to create resources. I corrected the plan to use only the existing resources.

### Storage access without keys — my call

The account keys were blocked. Instead of asking for keys, I checked my access in the portal and found *Storage Blob Data Contributor*. We switched to Entra ID (`DefaultAzureCredential`, no keys), time-boxed to 30 minutes. I insisted on one real check against Azure rather than the emulator only; it uploaded a file, which I verified in the portal. (On a laptop the test skips Managed Identity, which exists only inside Azure; production code still uses it.)

### Correlation ID in Application Insights — found missing by me

Claude Code set up Application Insights and gave me a request id to look for. I searched the portal and it wasn't there, although other telemetry was arriving: the id lived only in the log scope, which the exporter doesn't send. It was fixed to attach the id explicitly, and I found the new id in Logs.

### Deep paging — my prioritisation

Claude Code found that OFFSET paging takes 2.7 s at page 5,000. I chose to document it with a keyset fix rather than spend time on it, and gave the time to the frontend and the docs.

### Missing decisions in the docs — corrected by me

The AI's `DECISIONS.md` draft left out what happens when a previously rejected proposal arrives again. I asked for it to be added and verified against the code. I then asked whether a mistaken decision can be fixed; it can't, so I had that gap documented instead of hiding it.

### Requirements audit — requested by me

Before submitting I asked for a requirement-by-requirement audit. It found that the snapshot worker didn't carry the Correlation ID, and that two worker-restart behaviours had no tests. All three were fixed, each with a test shown to fail without the fix.

### Swagger — found by me

I opened `/swagger` myself and it failed to render ("does not specify a valid version field"). Claude Code confirmed a version mismatch (the bundled Swagger UI didn't recognise OpenAPI 3.0.4) and upgraded Swashbuckle.

### A failing check that was ignored — corrected by me

Claude Code compared its C# analysis of the CSV against a Python script; the Python script failed, and it carried on as if the check had passed. I told it never to skip a failing script: report it, fix it, rerun until it passes. Once it ran, the two outputs were compared, and that comparison exposed a silent bug in the C# code, which was fixed.

### "The UI is done" — rejected by me; my manual test found a real bug

**What the AI reported.** When the frontend was built, the AI considered the UI finished: its API tests passed, its Angular unit tests passed, and its own browser walkthrough checked each page's responses (imports, review with a 409 between two tabs, roles, field errors, snapshots, live progress). It noted only that uploading a file through the browser could not be tested by its browser tool.

**Why I didn't accept it.** I did not want to move on and approve the UI on the strength of the AI's own checks. I tested the UI manually in my browser: I selected **Dana** in the user switcher and uploaded `assembly_export_2024_q1_rev2.csv`. The AI then checked the backend and found the import recorded as uploaded by **Lena**. Confirmed bug: the screen showed one user while every request was sent as another, which affects both permissions (Lena is the Admin) and the audit trail.

**What caused it.** The selected user is kept in the browser's `localStorage`, and my browser still had Lena stored from earlier. On page load, the user switcher set its value (`<select [value]>`) before the list of users had arrived from the API, so there were no options to select yet. When the options arrived, the browser displayed the first one, Dana, while the app kept acting as the stored Lena. Picking "Dana", already the displayed option, fired no change event, so nothing changed. The AI's walkthrough always started from a fresh browser with nothing stored, so it never hit this.

**What was done to fix it.** Each option in the switcher now marks itself selected when it is the acting user (`[selected]`), so what is shown always matches what is sent, whenever the options arrive. A stored user that doesn't exist falls back to the first user, visibly. A new unit test (`app.spec.ts`) reproduces the case (Lena stored, options arriving late) and fails on the old code and passes on the fix. In the browser, with Lena stored, the switcher now shows Lena with the Admin editor visible, and switching to Dana hides it. Import #4 stays recorded as Lena's: the system never rewrites history.

**Checked again.** I repeated the manual test with a different user: I selected **Omer** and uploaded the same file. The backend recorded import #5 as uploaded by **omer**, so the fix is confirmed. After that I kept doing manual UI passes and comparing them with what the backend recorded.

## Guardrails I kept

- Keys never pasted into chat. `.env` was filled by me only; the AI was told not to read or print it.
- Before each commit: `git check-ignore .env`, `git log` on `.env` (0 commits), files staged by name (no `add -A`), and my approval of the file list.
- During `az login` I declined "allow my organization to manage my device".
- Destructive commands on my data (`docker compose down -v`) were not run without my approval; "from scratch" checks ran on a separate clean clone instead.
- Every AI-generated file that goes out in my name (`DECISIONS.md`, `AI_USAGE.md`) was reviewed by me before commit.

## Verifying AI output

- The AI's data analysis (expected classifications and conflict counts) became integration tests, so claims were checked by code, not trust.
- Azure claims were verified by me in the portal, not taken from the AI's report.
- New tests were mutation-checked: shown to fail without the fix.
