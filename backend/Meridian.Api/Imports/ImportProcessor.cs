using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Meridian.Api.Data;
using Meridian.Api.Storage;
using Meridian.Domain;
using Meridian.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Api.Imports;

// Processes one queued import. Called by ImportWorker.
//   pre-pass: count lines -> total_rows (ING-02)
//   phase 1:  stream the file, validate each row (ING-04), bulk-write all rows with COPY
//   phase 2:  per vote (ING-03), classify against approved data (ING-05/06) and write the result
// Memory stays bounded: the file is streamed, and phase 2 loads one batch of votes at a time.
public class ImportProcessor
{
    private const int CopyChunkRows = 10_000;
    private const int VotesPerBatch = 200;

    private const string CopySql =
        "COPY import_rows (import_id, line_no, raw_line, assembly_vote_id, session, vote_date, resolution_title, " +
        "country_code, country_name, ballot, published_at, classification, reason, normalization_note) " +
        "FROM STDIN (FORMAT BINARY)";

    private readonly MeridianDbContext _db;
    private readonly IFileStore _files;
    private readonly ILogger<ImportProcessor> _logger;

    public ImportProcessor(MeridianDbContext db, IFileStore files, ILogger<ImportProcessor> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    public async Task ProcessAsync(long importId, CancellationToken ct)
    {
        // background job: a batch statement on a big file may exceed Npgsql's 30 s default
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        var import = await _db.Imports.AsNoTracking().SingleAsync(i => i.Id == importId, ct);
        var timer = Stopwatch.StartNew();
        _logger.LogInformation("Import {ImportId} started: {FileName}, correlation {CorrelationId}", import.Id, import.FileName, import.CorrelationId);

        var members = new MemberList(await _db.MemberStates.AsNoTracking()
            .Select(m => new MembershipPeriod(m.CountryCode, m.MemberFrom, m.MemberTo))
            .ToListAsync(ct));
        var report = new ReportBuilder();

        var total = await CountDataLines(import.StoragePath, ct);
        await ImportsById(import.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.TotalRows, total), ct);

        _logger.LogInformation("Import {ImportId} pre-pass: {Rows} rows counted at {Seconds:F1}s", import.Id, total, timer.Elapsed.TotalSeconds);
        // step 1 of 2: progress = rows read
        await ImportsById(import.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Stage, ImportStage.Reading)
            .SetProperty(i => i.ProcessedRows, 0), ct);
        await StageRows(import, members, report, ct);
        _logger.LogInformation("Import {ImportId} phase 1 (read + validate + COPY) done at {Seconds:F1}s", import.Id, timer.Elapsed.TotalSeconds);
        // step 2 of 2: progress = rows with a final class (invalid rows already are)
        var invalidRows = report.InvalidRows;
        await ImportsById(import.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Stage, ImportStage.Classifying)
            .SetProperty(i => i.ProcessedRows, invalidRows), ct);
        await ClassifyVotes(import, members, report, ct);
        _logger.LogInformation("Import {ImportId} phase 2 (classify + write) done at {Seconds:F1}s", import.Id, timer.Elapsed.TotalSeconds);

        var earlier = await _db.Imports.AsNoTracking()
            .Where(i => i.FileSha256 == import.FileSha256 && i.Id < import.Id)
            .OrderBy(i => i.Id).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        if (earlier != null)
            report.FileNotes.Add($"Identical file to import #{earlier} (same SHA-256); re-processed, nothing is applied twice");

        var summaryJson = JsonSerializer.Serialize(report.Build(), ImportEndpoints.SummaryJson);
        await ImportsById(import.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, ImportStatus.Completed)
            .SetProperty(i => i.Stage, (ImportStage?)null)
            .SetProperty(i => i.CompletedAt, DateTime.UtcNow)
            .SetProperty(i => i.ProcessedRows, total)
            .SetProperty(i => i.Summary, summaryJson), ct);

        _logger.LogInformation("Import {ImportId} completed in {Seconds:F1}s: {Rows} rows, {NewConflicts} new conflicts, correlation {CorrelationId}",
            import.Id, timer.Elapsed.TotalSeconds, total, report.NewConflicts, import.CorrelationId);
    }

    private IQueryable<Import> ImportsById(long id) => _db.Imports.Where(i => i.Id == id);

    private Task<int> CountDataLines(string storagePath, CancellationToken ct)
    {
        using var reader = OpenText(storagePath);
        var lines = 0;
        while (reader.ReadLine() != null) lines++;
        return Task.FromResult(Math.Max(0, lines - 1));   // minus the header
    }

    // BOM detection off: we want to see the BOM so we can report it (ING-13)
    private StreamReader OpenText(string storagePath) =>
        new(_files.OpenRead(storagePath), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);

    // --- phase 1 ---

    private async Task StageRows(Import import, MemberList members, ReportBuilder report, CancellationToken ct)
    {
        using var reader = OpenText(import.StoragePath);

        // checked at upload too; checked again because the file is what we actually process
        var header = CsvFormat.CheckHeader(reader.ReadLine() ?? "");
        if (!header.IsValid) throw new InvalidDataException(header.Error);
        if (header.HadBom) report.FileNotes.Add("UTF-8 byte order mark (BOM) at the start of the file was removed");

        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
            var lineNo = 1;   // 1-based, header is line 1
            var endOfFile = false;

            // COPY in chunks, so progress can be saved between chunks.
            // Synchronous on purpose: ~14 values per row x 1M rows; an await per value cost more than the work.
            while (!endOfFile)
            {
                ct.ThrowIfCancellationRequested();
                var readInChunk = 0;
                var invalidInChunk = 0;
                using (var copy = connection.BeginBinaryImport(CopySql))
                {
                    for (var n = 0; n < CopyChunkRows; n++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) { endOfFile = true; break; }
                        lineNo++;
                        readInChunk++;

                        var validation = RowValidator.Validate(lineNo, line, members);
                        if (!validation.IsValid) invalidInChunk++;
                        if (validation.Row?.NormalizationNote != null) report.NormalizedRows++;
                        WriteRow(copy, import.Id, lineNo, line, validation);
                    }
                    copy.Complete();
                }

                report.Count(RowClassification.Invalid, invalidInChunk);
                await ImportsById(import.Id).ExecuteUpdateAsync(
                    s => s.SetProperty(i => i.ProcessedRows, i => i.ProcessedRows + readInChunk), ct);
            }
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static void WriteRow(NpgsqlBinaryImporter copy, long importId, int lineNo, string line, RowValidation validation)
    {
        // valid rows: the cleaned values (so grouping by vote id is exact); invalid rows: what the file had
        var row = validation.Row;
        var raw = validation.Fields?.Count == CsvFormat.Columns.Length ? validation.Fields : null;
        string?[] fields = row != null
            ? new[]
            {
                row.AssemblyVoteId, row.Session.ToString(), row.VoteDate.ToString("yyyy-MM-dd"), row.Title,
                row.CountryCode, row.CountryName, row.Ballot.ToString(), row.PublishedAt,
            }
            : Enumerable.Range(0, CsvFormat.Columns.Length).Select(i => raw?[i]).ToArray();

        copy.StartRow();
        copy.Write(importId, NpgsqlDbType.Bigint);
        copy.Write(lineNo, NpgsqlDbType.Integer);
        copy.Write(line, NpgsqlDbType.Text);
        foreach (var field in fields)
            WriteText(copy, field);
        WriteText(copy, validation.IsValid ? null : nameof(RowClassification.Invalid));
        WriteText(copy, validation.IsValid ? null : validation.Reason);
        WriteText(copy, row?.NormalizationNote);
    }

    private static void WriteText(NpgsqlBinaryImporter copy, string? value)
    {
        if (value == null) copy.WriteNull();
        else copy.Write(value, NpgsqlDbType.Text);
    }

    // --- phase 2 ---

    private async Task ClassifyVotes(Import import, MemberList members, ReportBuilder report, CancellationToken ct)
    {
        var voteIds = await _db.ImportRows
            .Where(r => r.ImportId == import.Id && r.Classification == null)
            .Select(r => r.AssemblyVoteId!)
            .Distinct()
            .OrderBy(v => v)
            .ToListAsync(ct);

        foreach (var batch in voteIds.Chunk(VotesPerBatch))
        {
            await ClassifyBatch(import, batch, members, report, ct);
            _db.ChangeTracker.Clear();   // keep memory flat across batches
        }
    }

    // One transaction per batch: approved data, conflicts, audit and row outcomes land together or not at all
    private async Task ClassifyBatch(Import import, string[] batch, MemberList members, ReportBuilder report, CancellationToken ct)
    {
        // time per stage, logged once per batch: shows where a slow import spends its time
        var stages = new List<string>();
        var stageTimer = Stopwatch.StartNew();
        void Stage(string name) { stages.Add($"{name} {stageTimer.Elapsed.TotalSeconds:F2}"); stageTimer.Restart(); }

        var rows = await _db.ImportRows.AsNoTracking()
            .Where(r => r.ImportId == import.Id && r.Classification == null && batch.Contains(r.AssemblyVoteId!))
            .Select(r => new { r.Id, r.LineNo, r.RawLine })
            .ToListAsync(ct);
        var rowIdByLine = rows.ToDictionary(r => r.LineNo, r => r.Id);

        // re-validate from the raw line: cheap, and gives the same typed row as phase 1
        var validRows = rows.Select(r => RowValidator.Validate(r.LineNo, r.RawLine, members).Row
            ?? throw new InvalidOperationException($"Line {r.LineNo} was valid in phase 1 but not in phase 2")).ToList();

        // what is approved right now for these votes
        var votes = await _db.Votes.Where(v => batch.Contains(v.AssemblyVoteId)).ToDictionaryAsync(v => v.AssemblyVoteId, ct);
        var voteDbIds = votes.Values.Select(v => v.Id).ToList();
        var ballots = (await _db.CountryVotes.AsNoTracking().Where(cv => voteDbIds.Contains(cv.VoteId)).ToListAsync(ct))
            .ToLookup(cv => cv.VoteId);
        var conflicts = (await _db.Conflicts.AsNoTracking().Where(c => voteDbIds.Contains(c.VoteId)).ToListAsync(ct))
            .ToLookup(c => c.VoteId);
        Stage("load");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;

        // 1. classify every vote (pure Domain), create new votes
        var results = new List<VoteResult>();
        foreach (var group in validRows.GroupBy(r => r.AssemblyVoteId))
        {
            ApprovedVote? approved = null;
            if (votes.TryGetValue(group.Key, out var existing))
            {
                approved = new ApprovedVote(existing.Session, existing.VoteDate, existing.Title,
                    ballots[existing.Id].ToDictionary(b => b.CountryCode, b => Enum.Parse<Ballot>(b.Ballot)),
                    conflicts[existing.Id].Select(c => new ExistingConflict(
                        c.Id, c.CountryCode, c.Field, c.ProposedValue, c.Status, c.DecidedBy, c.DecidedAt)).ToList());
            }

            var result = VoteClassifier.Classify(group.Key, group, approved);
            results.Add(result);

            if (result.VoteToCreate != null)
            {
                var vote = new Vote
                {
                    AssemblyVoteId = group.Key,
                    Session = result.VoteToCreate.Session,
                    VoteDate = result.VoteToCreate.VoteDate,
                    Title = result.VoteToCreate.Title,
                    SourceImportId = import.Id,
                };
                _db.Votes.Add(vote);
                votes[group.Key] = vote;
            }
        }
        await _db.SaveChangesAsync(ct);   // new votes get their ids
        Stage("classify+votes");

        // 2. add new ballots and open new conflicts. Never update an approved value (ING-08).
        var opened = new Dictionary<(string Vote, ConflictProposal Proposal), Conflict>();
        var newBallots = new List<(long VoteId, string Country, string Ballot)>();
        foreach (var result in results)
        {
            if (!votes.TryGetValue(result.AssemblyVoteId, out var vote))
                continue;   // vote not created and didn't exist: nothing to write but row outcomes

            if (result.VoteToCreate != null)
                _db.AuditEvents.Add(Audit(import, vote.Id, "VoteCreated", null,
                    $"{vote.Title} (session {vote.Session}, {vote.VoteDate:yyyy-MM-dd})", now));

            newBallots.AddRange(result.BallotsToAdd.Select(row => (vote.Id, row.CountryCode, row.Ballot.ToString())));
            if (result.BallotsToAdd.Count > 0)
                _db.AuditEvents.Add(Audit(import, vote.Id, "BallotsAdded", null, $"{result.BallotsToAdd.Count} ballots", now));

            foreach (var proposal in result.Conflicts.Where(p => p.IsNew))
            {
                // where the approved value came from (CON-02)
                var approvedSource = proposal.CountryCode == null
                    ? vote.SourceImportId
                    : ballots[vote.Id].Single(b => b.CountryCode == proposal.CountryCode).SourceImportId;

                var conflict = new Conflict
                {
                    VoteId = vote.Id,
                    CountryCode = proposal.CountryCode,
                    Field = proposal.Field,
                    ApprovedValue = proposal.ApprovedValue,
                    ApprovedSourceImportId = approvedSource,
                    ProposedValue = proposal.ProposedValue,
                    ProposedImportId = import.Id,
                    Status = ConflictStatus.Pending,
                    CreatedAt = now,
                };
                _db.Conflicts.Add(conflict);
                opened[(result.AssemblyVoteId, proposal)] = conflict;
            }
        }
        // New ballots in bulk: up to ~30K per batch for the historic file. COPY on the same connection,
        // so it is part of this batch's transaction (all or nothing). EF inserts were ~70% of the import time.
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        using (var copy = connection.BeginBinaryImport(
                   "COPY country_votes (vote_id, country_code, ballot, source_import_id) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (voteId, country, ballot) in newBallots)
            {
                copy.StartRow();
                copy.Write(voteId, NpgsqlDbType.Bigint);
                copy.Write(country, NpgsqlDbType.Varchar);
                copy.Write(ballot, NpgsqlDbType.Varchar);
                copy.Write(import.Id, NpgsqlDbType.Bigint);
            }
            copy.Complete();
        }

        await _db.SaveChangesAsync(ct);   // new conflicts get their ids
        Stage("ballots+conflicts");

        foreach (var ((voteKey, proposal), conflict) in opened)
        {
            var audit = Audit(import, conflict.VoteId, "ConflictOpened",
                $"{proposal.Field}: {proposal.ApprovedValue}", $"{proposal.Field}: {proposal.ProposedValue}", now);
            audit.CountryCode = proposal.CountryCode;
            audit.ConflictId = conflict.Id;
            _db.AuditEvents.Add(audit);
        }
        await _db.SaveChangesAsync(ct);
        Stage("audit");

        // 3. row outcomes, in one statement for the whole batch
        var ids = new List<long>();
        var classes = new List<string>();
        var reasons = new List<string?>();
        var conflictIds = new List<long?>();
        foreach (var result in results)
        {
            foreach (var outcome in result.Rows)
            {
                ids.Add(rowIdByLine[outcome.LineNo]);
                classes.Add(outcome.Classification.ToString());
                reasons.Add(outcome.Reason);
                conflictIds.Add(outcome.Conflict == null ? null
                    : outcome.Conflict.ExistingConflictId ?? opened[(result.AssemblyVoteId, outcome.Conflict)].Id);
            }
            report.Add(result);
        }

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE import_rows AS r SET classification = u.c, reason = u.reason, conflict_id = u.cid " +
            "FROM unnest(@ids, @classes, @reasons, @cids) AS u(id, c, reason, cid) WHERE r.id = u.id",
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids.ToArray() },
            new NpgsqlParameter("classes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = classes.ToArray() },
            new NpgsqlParameter("reasons", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = reasons.ToArray() },
            new NpgsqlParameter("cids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = conflictIds.ToArray() });

        await ImportsById(import.Id).ExecuteUpdateAsync(
            s => s.SetProperty(i => i.ProcessedRows, i => i.ProcessedRows + rows.Count), ct);

        Stage("row outcomes");
        await tx.CommitAsync(ct);
        Stage("commit");
        _logger.LogInformation("Import {ImportId} batch: {Votes} votes, {Rows} rows; seconds: {Stages}",
            import.Id, batch.Length, rows.Count, string.Join(", ", stages));
    }

    private static AuditEvent Audit(Import import, long voteId, string type, string? oldValue, string? newValue, DateTime at) => new()
    {
        VoteId = voteId,
        EventType = type,
        OldValue = oldValue,
        NewValue = newValue,
        ImportId = import.Id,
        Actor = import.UploadedBy,
        At = at,
        CorrelationId = import.CorrelationId,
    };

    // Collects the numbers for the report while the import runs
    private class ReportBuilder
    {
        private readonly Dictionary<RowClassification, int> _counts =
            Enum.GetValues<RowClassification>().ToDictionary(c => c, _ => 0);
        private readonly List<VoteOutcomeDto> _votes = new();
        private int _votesCreated, _ballotsAdded, _linkedConflicts;

        public int NewConflicts { get; private set; }
        public int NormalizedRows { get; set; }
        public List<string> FileNotes { get; } = new();

        public void Count(RowClassification c, int n) => _counts[c] += n;

        public int InvalidRows => _counts[RowClassification.Invalid];

        public void Add(VoteResult result)
        {
            var rows = Enum.GetValues<RowClassification>().ToDictionary(c => c, c => result.Rows.Count(r => r.Classification == c));
            foreach (var (c, n) in rows) _counts[c] += n;
            _votes.Add(new VoteOutcomeDto(result.AssemblyVoteId, result.DetailsOutcome, result.DetailsReason, rows));
            if (result.VoteToCreate != null) _votesCreated++;
            _ballotsAdded += result.BallotsToAdd.Count;
            NewConflicts += result.Conflicts.Count(c => c.IsNew);
            _linkedConflicts += result.Conflicts.Count(c => !c.IsNew);
        }

        public ImportSummary Build() => new(_counts, _votesCreated, _ballotsAdded, NewConflicts, _linkedConflicts,
            NormalizedRows, FileNotes, _votes.OrderBy(v => v.AssemblyVoteId).ToList());
    }
}
