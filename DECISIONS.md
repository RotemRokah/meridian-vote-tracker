# Decisions

> Work in progress: this file is completed in step 12. Until then the full decision log is the draft section of `CLAUDE.md`.

## Azure

Everything Azure is optional and off by default: with an empty `.env`, `docker compose up` runs fully locally
(files on a docker volume, logs on the console). Azure is switched on by configuration only; no keys or connection
strings in code or git.

**Verified against the assignment's Azure resources (resource group `dotnetcandidate1-rg`):**

- **Application Insights (`dotnetcandidate1-appi`).** A request made from the UI was found in the portal by its
  correlation id in `customDimensions.CorrelationId`, on the `requests` row and on the `traces` row, with the
  PostgreSQL calls as `dependencies` under the same `operation_Id`. The first attempt failed: the id lived only in the
  log scope, which the exporter did not send. It is now attached explicitly (a tag on the request's Activity, and a
  parameter of the request and import log lines), so UI → API → background import can be followed in one query.
- **Blob Storage (`dotnetcandidate1st`).** Account keys are blocked in this environment (`AuthorizationFailed`); access
  is through Entra ID (my account has *Storage Blob Data Contributor*). A one-time opt-in test (`AzureBlobLiveTests`)
  used the production `AzureBlobFileStore` with the account URL and `DefaultAzureCredential` (signed in with
  `az login`): it uploaded `assembly_export_2024_q1_rev2.csv` (644,386 bytes) to container `imports`, read it back
  byte for byte, and the stored SHA-256 matched. On a laptop the test excludes Managed Identity from the credential
  chain, because its failed probe stops the chain before it reaches the `az login` credential.

**Verified only against Azurite (the Azure Storage emulator), and why:** the full API path in Blob mode (upload →
blob → the import worker reads it back) runs in `BlobStorageTests` against Azurite in Testcontainers, using a
connection string. Running the whole API against the real account would need an Entra ID sign-in inside the docker
container, which a laptop setup doesn't have, so `docker compose` stays on local storage.

**In production:** the API runs in Azure with a **Managed Identity** that has *Storage Blob Data Contributor* on the
account. Configuration is only `Storage:AzureBlob:AccountUrl`; `DefaultAzureCredential` picks the Managed Identity,
so there is no storage key anywhere. The remaining secret (the Application Insights connection string) would come from
Key Vault via app settings references.

**Available for the bonus:** Document Intelligence (`dotnetcandidate1-di`), layout model: tables and selection marks
with a confidence per cell, which is what the PDF voting sheets need. Not used yet.

**Not used, and why:**
- **Azure OpenAI / Claude for PDF extraction:** non-deterministic output and no per-cell confidence; Document
  Intelligence returns structured cells with confidence that can be validated and flagged.
- **Azure Database for PostgreSQL:** reviewers must be able to run everything with `docker compose up`, without the
  assignment's Azure window; the connection string is configuration, so moving is a deployment change, not a code change.
- **Cosmos DB:** the data is relational, with foreign keys, partial unique indexes and multi-table transactions
  (conflict decisions, snapshots).
- **Key Vault:** local development would still need a credential to reach it; in production it belongs together with
  Managed Identity.
