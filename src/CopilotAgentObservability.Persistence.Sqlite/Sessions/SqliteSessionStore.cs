using CopilotAgentObservability.Telemetry.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SessionIdentityConflictException : InvalidOperationException
{
    internal SessionIdentityConflictException()
        : base("Session source identity is already owned by another session.")
    {
    }
}

public sealed class SqliteSessionStore : ISessionStore, IClassifiedSessionStore, ICurrentSessionEligibilityStore, IEffectCurrentUseStore
{
    private const int VersionTenSchemaVersion = 10;
    private const int VersionElevenSchemaVersion = 11;
    private const int VersionTwelveSchemaVersion = 12;
    private const int VersionThirteenSchemaVersion = 13;
    private const int CurrentSchemaVersion = 14;
    private const string SchemaVersionSql = """
        CREATE TABLE IF NOT EXISTS schema_version (
            component TEXT PRIMARY KEY,
            version INTEGER NOT NULL
        );
        """;
    private static readonly string[] VersionElevenProvenanceColumns = ["source_application_version", "adapter_version", "schema_fingerprint", "normalization_version"];
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? comparisonCheckpoint;
    private readonly Action<string>? writeCheckpoint;
    private readonly SQLitePCL.strdelegate_trace? statementObserver;
    private readonly RetentionCatalogContext? retentionContext;
    private readonly int busyTimeoutMilliseconds = 5000;

    public SqliteSessionStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SqliteSessionStore(string databasePath, RetentionCatalogContext retentionContext, TimeProvider? timeProvider = null)
        : this(databasePath, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(retentionContext);
        if (!string.Equals(Path.GetFullPath(databasePath), retentionContext.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Retention catalog context belongs to a different database.", nameof(retentionContext));
        }

        this.retentionContext = retentionContext;
    }

    internal SqliteSessionStore(string databasePath, Action<string> comparisonCheckpoint)
        : this(databasePath)
    {
        this.comparisonCheckpoint = comparisonCheckpoint ?? throw new ArgumentNullException(nameof(comparisonCheckpoint));
    }

    internal SqliteSessionStore(string databasePath, int busyTimeoutMilliseconds)
        : this(databasePath)
    {
        if (busyTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        }
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    internal SqliteSessionStore(
        string databasePath,
        TimeProvider timeProvider,
        Action<string> writeCheckpoint)
        : this(databasePath, timeProvider)
    {
        this.writeCheckpoint = writeCheckpoint ?? throw new ArgumentNullException(nameof(writeCheckpoint));
    }

    internal SqliteSessionStore(
        string databasePath,
        RetentionCatalogContext retentionContext,
        TimeProvider timeProvider,
        Action<string> writeCheckpoint)
        : this(databasePath, retentionContext, timeProvider)
    {
        this.writeCheckpoint = writeCheckpoint ?? throw new ArgumentNullException(nameof(writeCheckpoint));
    }

    internal SqliteSessionStore(
        string databasePath,
        RetentionCatalogContext retentionContext,
        TimeProvider timeProvider,
        Action<string> writeCheckpoint,
        Action<string> statementObserver)
        : this(databasePath, retentionContext, timeProvider, writeCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(statementObserver);
        this.statementObserver = (_, statement) => statementObserver(statement);
    }

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open(enforceForeignKeys: false);
        try
        {
            ValidateSchemaBeforeInitialization(connection);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using var transaction = connection.BeginTransaction(deferred: false);
            InitializeSchema(connection, transaction, timeProvider.GetUtcNow());
            transaction.Commit();
        }
        finally
        {
            Execute(connection, "PRAGMA foreign_keys=ON;");
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys;";
            if (Convert.ToInt64(foreignKeys.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Session schema initialization did not restore foreign-key enforcement.");
        }
    }

    internal static void InitializeSchema(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset migrationNow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaVersionSql;
        command.ExecuteNonQuery();

        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT version FROM schema_version WHERE component = 'session';";
        var existingVersion = versionCommand.ExecuteScalar();
        var version = existingVersion is null ? (int?)null : Convert.ToInt32(existingVersion);
        if (version is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidOperationException("Unsupported Session schema version.");
        }

        if (existingVersion is null)
        {
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = HumanEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ImprovementProposalSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ProposalApplySchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"INSERT INTO schema_version(component,version) VALUES('session',{CurrentSchemaVersion});");
        }
        else if (Convert.ToInt32(existingVersion) == 1)
        {
            command.CommandText = HumanEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ImprovementProposalSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ProposalApplySchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 2)
        {
            command.CommandText = ImprovementProposalSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ProposalApplySchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 3)
        {
            command.CommandText = ProposalApplySchemaSql;
            command.ExecuteNonQuery();
            AddProposalRevisionColumns(connection, transaction);
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 4)
        {
            AddColumnIfMissing(connection, transaction, "proposal_apply_drafts", "updated_at", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00'");
            command.CommandText = ProposalApplyPendingSchemaSql;
            command.ExecuteNonQuery();
            AddProposalRevisionColumns(connection, transaction);
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 5)
        {
            command.CommandText = ProposalApplyPendingSchemaSql;
            command.ExecuteNonQuery();
            AddProposalRevisionColumns(connection, transaction);
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 6)
        {
            AddProposalRevisionColumns(connection, transaction);
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 7)
        {
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 8)
        {
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 9)
        {
            command.CommandText = "ALTER TABLE effect_comparison_sessions ADD COLUMN effective_quality TEXT NULL CHECK (effective_quality IS NULL OR effective_quality IN ('pass','fail','missing')); ALTER TABLE effect_comparison_sessions ADD COLUMN severe_failure INTEGER NOT NULL DEFAULT 0 CHECK (severe_failure IN (0,1)); ALTER TABLE effect_comparison_evidence ADD COLUMN human_verdict TEXT NULL CHECK (human_verdict IS NULL OR human_verdict IN ('expected','problem'));";
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTenSchemaVersion} WHERE component='session';");
        }
        else if (version == VersionTenSchemaVersion)
        {
            RepairKnownStampedVersionTenShape(connection, transaction, command);
        }

        if (version is <= VersionTenSchemaVersion)
        {
            MigrateToVersionEleven(connection, transaction, command);
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionElevenSchemaVersion} WHERE component='session';");
        }

        if (version is <= VersionElevenSchemaVersion)
        {
            MigrateToVersionTwelve(connection, transaction);
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionTwelveSchemaVersion} WHERE component='session';");
        }

        if (version is <= VersionTwelveSchemaVersion)
        {
            MigrateToVersionThirteen(connection, transaction);
            Execute(connection, transaction, $"UPDATE schema_version SET version={VersionThirteenSchemaVersion} WHERE component='session';");
        }

        if (version is <= VersionThirteenSchemaVersion)
        {
            MigrateToVersionFourteen(connection, transaction, migrationNow);
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }

        EnsureForeignKeysValid(connection, transaction);
        if (!IsCurrentSchemaValid(connection, transaction))
            throw new InvalidOperationException("Unsupported incomplete Session schema version 14.");
    }

    public void SaveProposalApplyDraft(ProposalApplyDraftMetadata draft, IReadOnlyList<(string BaseSha256, string ReplacementSha256)> files, IReadOnlyList<(string HunkId, bool Selected, string ReplacementSha256)> hunks, ProposalApplyRevisionMetadata revision)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO proposal_apply_drafts(draft_id,proposal_id,proposal_revision,root_id,selection_revision,approval_digest,state,created_at,updated_at) VALUES($id,$proposal,$proposal_revision,$root,$revision,$digest,$state,$created,$updated);",
            ("$id", Id(draft.DraftId)), ("$proposal", Id(draft.ProposalId)), ("$proposal_revision", draft.ProposalRevision), ("$root", Id(draft.RootId)), ("$revision", draft.SelectionRevision), ("$digest", draft.ApprovalDigest), ("$state", ApplyState(draft.State)), ("$created", Timestamp(draft.CreatedAt)), ("$updated", Timestamp(draft.UpdatedAt)));
        for (var i = 0; i < files.Count; i++) Execute(connection, transaction, "INSERT INTO proposal_apply_files(draft_id,file_order,base_sha256,replacement_sha256) VALUES($id,$order,$base,$replacement);", ("$id", Id(draft.DraftId)), ("$order", i), ("$base", files[i].BaseSha256), ("$replacement", files[i].ReplacementSha256));
        foreach (var hunk in hunks) Execute(connection, transaction, "INSERT INTO proposal_apply_hunks(draft_id,hunk_id,selected,replacement_sha256) VALUES($id,$hunk,$selected,$replacement);", ("$id", Id(draft.DraftId)), ("$hunk", hunk.HunkId), ("$selected", hunk.Selected ? 1 : 0), ("$replacement", hunk.ReplacementSha256));
        Execute(connection, transaction, "INSERT INTO proposal_apply_revisions(draft_id,selection_revision,approval_digest,approved_at) VALUES($id,$revision,$digest,$approved);", ("$id", Id(revision.DraftId)), ("$revision", revision.SelectionRevision), ("$digest", revision.ApprovalDigest), ("$approved", Timestamp(revision.ApprovedAt)));
        transaction.Commit();
    }

    public ProposalApplyDraftMetadata? GetProposalApplyDraft(Guid draftId)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT draft_id,proposal_id,proposal_revision,root_id,selection_revision,approval_digest,state,(SELECT COUNT(*) FROM proposal_apply_files WHERE draft_id=d.draft_id),created_at,updated_at FROM proposal_apply_drafts d WHERE draft_id=$id;"; command.Parameters.AddWithValue("$id", Id(draftId)); using var reader = command.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), Guid.Parse(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5), ParseApplyState(reader.GetString(6)), reader.GetInt32(7), DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)) : null;
    }

    public ProposalApplyImmutableMetadata? GetProposalApplyImmutableMetadata(Guid draftId)
    {
        var draft = GetProposalApplyDraft(draftId);
        if (draft is null) return null;
        using var connection = Open();
        using var revisionCommand = connection.CreateCommand();
        revisionCommand.CommandText = "SELECT approval_digest,approved_at FROM proposal_apply_revisions WHERE draft_id=$id AND selection_revision=$revision;";
        revisionCommand.Parameters.AddWithValue("$id", Id(draftId)); revisionCommand.Parameters.AddWithValue("$revision", draft.SelectionRevision);
        using var revisionReader = revisionCommand.ExecuteReader();
        if (!revisionReader.Read()) return null;
        var revision = new ProposalApplyRevisionMetadata(draftId, draft.SelectionRevision, revisionReader.GetString(0), revisionReader.IsDBNull(1) ? null : DateTimeOffset.Parse(revisionReader.GetString(1), CultureInfo.InvariantCulture));
        using var fileCommand = connection.CreateCommand(); fileCommand.CommandText = "SELECT base_sha256,replacement_sha256 FROM proposal_apply_files WHERE draft_id=$id ORDER BY file_order;"; fileCommand.Parameters.AddWithValue("$id", Id(draftId));
        using var fileReader = fileCommand.ExecuteReader(); var files = new List<(string, string)>(); while (fileReader.Read()) files.Add((fileReader.GetString(0), fileReader.GetString(1)));
        using var hunkCommand = connection.CreateCommand(); hunkCommand.CommandText = "SELECT hunk_id,selected,replacement_sha256 FROM proposal_apply_hunks WHERE draft_id=$id ORDER BY hunk_id;"; hunkCommand.Parameters.AddWithValue("$id", Id(draftId));
        using var hunkReader = hunkCommand.ExecuteReader(); var hunks = new List<(string, bool, string)>(); while (hunkReader.Read()) hunks.Add((hunkReader.GetString(0), hunkReader.GetInt32(1) != 0, hunkReader.GetString(2)));
        return new ProposalApplyImmutableMetadata(draft, revision, files, hunks);
    }

    public bool TryMigrateProposalApplyDigest(Guid draftId, int proposalRevision, int selectionRevision, string expectedOldDigest, string newDigest)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var current = connection.CreateCommand(); current.Transaction = transaction;
        current.CommandText = "SELECT d.approval_digest,r.approval_digest FROM proposal_apply_drafts d JOIN proposal_apply_revisions r ON r.draft_id=d.draft_id AND r.selection_revision=d.selection_revision WHERE d.draft_id=$id AND d.proposal_revision=$proposal_revision AND d.selection_revision=$selection_revision;";
        current.Parameters.AddWithValue("$id", Id(draftId)); current.Parameters.AddWithValue("$proposal_revision", proposalRevision); current.Parameters.AddWithValue("$selection_revision", selectionRevision);
        using var reader = current.ExecuteReader();
        if (!reader.Read()) return false;
        var draftDigest = reader.GetString(0); var revisionDigest = reader.GetString(1);
        if (draftDigest == newDigest && revisionDigest == newDigest) { transaction.Commit(); return true; }
        if (draftDigest != expectedOldDigest || revisionDigest != expectedOldDigest) return false;
        var draftRows = Execute(connection, transaction, "UPDATE proposal_apply_drafts SET approval_digest=$new WHERE draft_id=$id AND proposal_revision=$proposal_revision AND selection_revision=$selection_revision AND approval_digest=$old;", ("$new", newDigest), ("$id", Id(draftId)), ("$proposal_revision", proposalRevision), ("$selection_revision", selectionRevision), ("$old", expectedOldDigest));
        var revisionRows = Execute(connection, transaction, "UPDATE proposal_apply_revisions SET approval_digest=$new WHERE draft_id=$id AND selection_revision=$selection_revision AND approval_digest=$old;", ("$new", newDigest), ("$id", Id(draftId)), ("$selection_revision", selectionRevision), ("$old", expectedOldDigest));
        if (draftRows != 1 || revisionRows != 1) return false;
        transaction.Commit(); return true;
    }

    public void UpdateProposalApplyDraft(ProposalApplyDraftMetadata draft, IReadOnlyList<(string BaseSha256, string ReplacementSha256)> files, IReadOnlyList<(string HunkId, bool Selected, string ReplacementSha256)> hunks, ProposalApplyRevisionMetadata revision)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE proposal_apply_drafts SET selection_revision=$revision,approval_digest=$digest,state=$state,updated_at=$updated WHERE draft_id=$id;", ("$revision", draft.SelectionRevision), ("$digest", draft.ApprovalDigest), ("$state", ApplyState(draft.State)), ("$updated", Timestamp(draft.UpdatedAt)), ("$id", Id(draft.DraftId)));
        Execute(connection, transaction, "DELETE FROM proposal_apply_files WHERE draft_id=$id; DELETE FROM proposal_apply_hunks WHERE draft_id=$id;", ("$id", Id(draft.DraftId)));
        for (var i = 0; i < files.Count; i++) Execute(connection, transaction, "INSERT INTO proposal_apply_files(draft_id,file_order,base_sha256,replacement_sha256) VALUES($id,$order,$base,$replacement);", ("$id", Id(draft.DraftId)), ("$order", i), ("$base", files[i].BaseSha256), ("$replacement", files[i].ReplacementSha256));
        foreach (var hunk in hunks) Execute(connection, transaction, "INSERT INTO proposal_apply_hunks(draft_id,hunk_id,selected,replacement_sha256) VALUES($id,$hunk,$selected,$replacement);", ("$id", Id(draft.DraftId)), ("$hunk", hunk.HunkId), ("$selected", hunk.Selected ? 1 : 0), ("$replacement", hunk.ReplacementSha256));
        Execute(connection, transaction, "INSERT INTO proposal_apply_revisions(draft_id,selection_revision,approval_digest,approved_at) VALUES($id,$revision,$digest,$approved);", ("$id", Id(revision.DraftId)), ("$revision", revision.SelectionRevision), ("$digest", revision.ApprovalDigest), ("$approved", Timestamp(revision.ApprovedAt))); transaction.Commit();
    }

    public IReadOnlyList<ProposalApplyDraftMetadata> ListActiveProposalApplyDrafts()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT draft_id FROM proposal_apply_drafts WHERE state IN ('draft','approved');"; using var reader = command.ExecuteReader(); var ids = new List<Guid>(); while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0))); return ids.Select(GetProposalApplyDraft).OfType<ProposalApplyDraftMetadata>().ToArray();
    }

    public void SaveProposalApplyApproval(Guid draftId, ProposalApplyRevisionMetadata revision)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE proposal_apply_drafts SET state='approved',updated_at=$updated WHERE draft_id=$id AND selection_revision=$revision;", ("$updated", Timestamp(revision.ApprovedAt)), ("$id", Id(draftId)), ("$revision", revision.SelectionRevision));
        Execute(connection, transaction, "UPDATE proposal_apply_revisions SET approved_at=$approved WHERE draft_id=$id AND selection_revision=$revision;", ("$approved", Timestamp(revision.ApprovedAt)), ("$id", Id(draftId)), ("$revision", revision.SelectionRevision)); transaction.Commit();
    }

    public void SaveProposalApplyOutcome(ProposalApplyOutcome outcome, Guid proposalId, Guid rootId, int fileCount, string? errorCode)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO proposal_applies(apply_id,draft_id,proposal_revision,state,created_at) SELECT $apply,$draft,proposal_revision,$state,$time FROM proposal_apply_drafts WHERE draft_id=$draft ON CONFLICT(apply_id) DO UPDATE SET state=excluded.state;", ("$apply", Id(outcome.ApplyId)), ("$draft", Id(outcome.DraftId)), ("$state", ApplyState(outcome.State)), ("$time", Timestamp(outcome.RecordedAt)));
        Execute(connection, transaction, "UPDATE proposal_apply_drafts SET state=$state,updated_at=$time WHERE draft_id=$draft;", ("$state", ApplyState(outcome.State)), ("$time", Timestamp(outcome.RecordedAt)), ("$draft", Id(outcome.DraftId)));
        Execute(connection, transaction, "INSERT INTO proposal_apply_audit(apply_id,draft_id,proposal_id,root_id,actor_kind,state,error_code,file_count,recorded_at) VALUES($apply,$draft,$proposal,$root,'local_user',$state,$error,$count,$time);", ("$apply", Id(outcome.ApplyId)), ("$draft", Id(outcome.DraftId)), ("$proposal", Id(proposalId)), ("$root", Id(rootId)), ("$state", ApplyState(outcome.State)), ("$error", errorCode), ("$count", fileCount), ("$time", Timestamp(outcome.RecordedAt))); transaction.Commit();
    }

    public void SaveProposalApplyPending(ProposalApplyPendingOperation pending)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO proposal_apply_pending(apply_id,draft_id,proposal_id,root_id,actor_kind,file_count,operation_kind,recorded_at) VALUES($apply,$draft,$proposal,$root,'local_user',$count,$kind,$time);", ("$apply", Id(pending.ApplyId)), ("$draft", Id(pending.DraftId)), ("$proposal", Id(pending.ProposalId)), ("$root", Id(pending.RootId)), ("$count", pending.FileCount), ("$kind", pending.OperationKind), ("$time", Timestamp(pending.RecordedAt))); transaction.Commit();
    }
    public bool TryAuthorizeProposalApply(ProposalApplyPendingOperation pending, int proposalRevision)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var rows = Execute(connection, transaction, "INSERT INTO proposal_apply_pending(apply_id,draft_id,proposal_id,root_id,actor_kind,file_count,operation_kind,recorded_at) SELECT $apply,$draft,$proposal,$root,'local_user',$count,'apply',$time WHERE EXISTS(SELECT 1 FROM improvement_proposals WHERE proposal_id=$proposal AND revision=$revision) AND NOT EXISTS(SELECT 1 FROM proposal_apply_pending WHERE proposal_id=$proposal AND operation_kind='apply');", ("$apply", Id(pending.ApplyId)), ("$draft", Id(pending.DraftId)), ("$proposal", Id(pending.ProposalId)), ("$root", Id(pending.RootId)), ("$count", pending.FileCount), ("$time", Timestamp(pending.RecordedAt)), ("$revision", proposalRevision)); transaction.Commit(); return rows == 1;
    }

    public IReadOnlyList<ProposalApplyPendingOperation> ListProposalApplyPending()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT apply_id,draft_id,proposal_id,root_id,file_count,operation_kind,recorded_at FROM proposal_apply_pending ORDER BY recorded_at;"; using var reader = command.ExecuteReader(); var result = new List<ProposalApplyPendingOperation>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        return result;
    }

    public IReadOnlyList<ProposalApplyLinkage> ListAppliedProposalApplyLinkages()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.apply_id,a.draft_id,d.proposal_id,a.proposal_revision,d.root_id,(SELECT COUNT(*) FROM proposal_apply_files WHERE draft_id=d.draft_id),d.selection_revision,d.approval_digest FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE a.state='applied' ORDER BY a.created_at;";
        using var reader = command.ExecuteReader(); var result = new List<ProposalApplyLinkage>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetInt32(3), Guid.Parse(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7)));
        return result;
    }

    public IReadOnlyList<ProposalApplyLinkage> ListProposalApplyLinkages(Guid proposalId) => ListAppliedProposalApplyLinkages().Where(item => item.ProposalId == proposalId).ToArray();

    public IReadOnlyList<ProposalApplicationReceipt> ListApplicationReceipts(Guid proposalId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.apply_id,a.draft_id,d.proposal_id,a.proposal_revision,d.selection_revision,a.created_at,(SELECT COUNT(*) FROM proposal_apply_files WHERE draft_id=d.draft_id),a.state FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE d.proposal_id=$proposal ORDER BY a.created_at;";
        command.Parameters.AddWithValue("$proposal", Id(proposalId)); using var reader = command.ExecuteReader(); var result = new List<ProposalApplicationReceipt>();
        while (reader.Read()) { var state = reader.GetString(7); result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetInt32(3), reader.GetInt32(4), ParseTimestamp(reader.GetString(5)), reader.GetInt32(6), state, state == "applied" ? "pending" : state == "rolled_back" ? "rolled_back" : "pending")); }
        return result;
    }

    public bool TryStartProposalApplyRollback(ProposalApplyPendingOperation pending)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO proposal_apply_pending(apply_id,draft_id,proposal_id,root_id,actor_kind,file_count,operation_kind,recorded_at) SELECT $apply,$draft,$proposal,$root,'local_user',$count,'rollback',$time WHERE EXISTS(SELECT 1 FROM proposal_applies WHERE apply_id=$apply AND draft_id=$draft AND state='applied') AND NOT EXISTS(SELECT 1 FROM proposal_apply_pending WHERE apply_id=$apply);";
        command.Parameters.AddWithValue("$apply", Id(pending.ApplyId)); command.Parameters.AddWithValue("$draft", Id(pending.DraftId)); command.Parameters.AddWithValue("$proposal", Id(pending.ProposalId)); command.Parameters.AddWithValue("$root", Id(pending.RootId)); command.Parameters.AddWithValue("$count", pending.FileCount); command.Parameters.AddWithValue("$time", Timestamp(pending.RecordedAt));
        var started = command.ExecuteNonQuery() == 1; transaction.Commit(); return started;
    }

    public void CompleteProposalApplyPending(ProposalApplyOutcome outcome, Guid proposalId, Guid rootId, int fileCount, string? errorCode)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var pending = connection.CreateCommand(); pending.Transaction = transaction; pending.CommandText = "SELECT operation_kind FROM proposal_apply_pending WHERE apply_id=$apply;"; pending.Parameters.AddWithValue("$apply", Id(outcome.ApplyId)); var operationKind = pending.ExecuteScalar() as string;
        if (operationKind is null) { transaction.Commit(); return; }
        if (operationKind == "apply" || outcome.State == ProposalApplyState.RolledBack)
        {
            Execute(connection, transaction, "INSERT INTO proposal_applies(apply_id,draft_id,proposal_revision,state,created_at) SELECT $apply,$draft,proposal_revision,$state,$time FROM proposal_apply_drafts WHERE draft_id=$draft ON CONFLICT(apply_id) DO UPDATE SET state=excluded.state;", ("$apply", Id(outcome.ApplyId)), ("$draft", Id(outcome.DraftId)), ("$state", ApplyState(outcome.State)), ("$time", Timestamp(outcome.RecordedAt)));
            Execute(connection, transaction, "UPDATE proposal_apply_drafts SET state=$state,updated_at=$time WHERE draft_id=$draft;", ("$state", ApplyState(outcome.State)), ("$time", Timestamp(outcome.RecordedAt)), ("$draft", Id(outcome.DraftId)));
        }
        Execute(connection, transaction, "INSERT INTO proposal_apply_audit(apply_id,draft_id,proposal_id,root_id,actor_kind,state,error_code,file_count,recorded_at) VALUES($apply,$draft,$proposal,$root,'local_user',$state,$error,$count,$time);", ("$apply", Id(outcome.ApplyId)), ("$draft", Id(outcome.DraftId)), ("$proposal", Id(proposalId)), ("$root", Id(rootId)), ("$state", ApplyState(outcome.State)), ("$error", errorCode), ("$count", fileCount), ("$time", Timestamp(outcome.RecordedAt)));
        Execute(connection, transaction, "DELETE FROM proposal_apply_pending WHERE apply_id=$apply;", ("$apply", Id(outcome.ApplyId)));
        transaction.Commit();
    }

    public void Write(SessionWriteBatch batch)
        => WriteCore(batch, [], []);

    void IClassifiedSessionStore.WriteClassified(
        SessionWriteBatch batch,
        IReadOnlyList<SessionTerminalFact> terminalFacts,
        IReadOnlyList<SessionReplayContentCandidate>? replayContentCandidates)
        => WriteCore(batch, terminalFacts, replayContentCandidates ?? []);

    private void WriteCore(
        SessionWriteBatch batch,
        IReadOnlyList<SessionTerminalFact> terminalFacts,
        IReadOnlyList<SessionReplayContentCandidate> replayContentCandidates)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(terminalFacts);
        ArgumentNullException.ThrowIfNull(replayContentCandidates);
        if ((batch.Content.Count != 0 || replayContentCandidates.Count != 0) && retentionContext is null)
        {
            throw new RetentionCatalogUnavailableException();
        }
        writeCheckpoint?.Invoke("before-session-write");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        RetentionCatalogStore? catalog = retentionContext is null ? null : new RetentionCatalogStore(retentionContext, timeProvider);
        if (catalog is not null) catalog.InitializeForWrite(connection, transaction);
        var replayComparisonNow = replayContentCandidates.Count == 0
            ? default
            : timeProvider.GetUtcNow();
        ValidateBatch(connection, transaction, batch);
        var orderedRuns = OrderRuns(batch.Detail.Runs);
        var orderedEvents = OrderEvents(batch.Detail.Events);
        var exactOtelSurfaceByTrace = orderedEvents
            .Where(IsExactOtelEvent)
            .Select(item => item.TraceId)
            .Where(static traceId => !string.IsNullOrWhiteSpace(traceId))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                traceId => traceId!,
                traceId => ReadExactOtelSourceSurface(
                    connection,
                    transaction,
                    traceId!),
                StringComparer.Ordinal);
        var exactOtelSurfaceByRun = orderedEvents
            .Where(item => IsExactOtelEvent(item)
                && item.RunId is not null
                && !string.IsNullOrWhiteSpace(item.TraceId))
            .GroupBy(item => item.RunId!.Value)
            .ToDictionary(
                group => group.Key,
                group => exactOtelSurfaceByTrace[group.First().TraceId!]);
        var canonicalEventIds = ResolveCanonicalEventIds(connection, transaction, batch.Detail.Events);
        if (replayContentCandidates.Any(candidate =>
                !canonicalEventIds.ContainsKey(candidate.EventId)
                || string.IsNullOrWhiteSpace(candidate.ContentKind)
                || candidate.ContentJson is null))
        {
            throw new InvalidOperationException("Invalid Session replay content candidates.");
        }
        var terminalOutcomes = ValidateTerminalFacts(orderedEvents, terminalFacts);
        var newEventIds = new HashSet<Guid>();
        WriteSession(connection, transaction, batch.Detail.Session);

        foreach (var nativeId in batch.Detail.NativeIds)
        {
            Execute(connection, transaction, """
                INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES($session_id,$source_surface,$native_session_id,$binding_kind,$observed_at)
                ON CONFLICT(source_surface,native_session_id) DO NOTHING;
                """,
                ("$session_id", Id(nativeId.SessionId)), ("$source_surface", SessionWire.ToWire(nativeId.SourceSurface)),
                ("$native_session_id", nativeId.NativeSessionId), ("$binding_kind", SessionWire.ToWire(nativeId.BindingKind)),
                ("$observed_at", Timestamp(nativeId.ObservedAt)));
        }

        foreach (var run in orderedRuns)
        {
            Execute(connection, transaction, """
                INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
                VALUES($run_id,$session_id,$source_surface,$native_run_id,$trace_id,$parent_run_id,$model,$started_at,$ended_at,$input_tokens,$output_tokens,$total_tokens,$status)
                ON CONFLICT(run_id) DO NOTHING;
                """,
                ("$run_id", Id(run.RunId)), ("$session_id", Id(run.SessionId)),
                ("$source_surface", exactOtelSurfaceByRun.TryGetValue(run.RunId, out var exactSurface)
                    ? exactSurface
                    : run.SourceSurface is null ? null : SessionWire.ToWire(run.SourceSurface.Value)),
                ("$native_run_id", run.NativeRunId), ("$trace_id", run.TraceId), ("$parent_run_id", run.ParentRunId is null ? null : Id(run.ParentRunId.Value)),
                ("$model", run.Model), ("$started_at", Timestamp(run.StartedAt)), ("$ended_at", Timestamp(run.EndedAt)),
                ("$input_tokens", run.InputTokens), ("$output_tokens", run.OutputTokens), ("$total_tokens", run.TotalTokens),
                ("$status", SessionWire.ToWire(run.Status)));
        }

        foreach (var item in orderedEvents)
        {
            var eventId = canonicalEventIds[item.EventId];
            var parentEventId = item.ParentEventId is not null && canonicalEventIds.TryGetValue(item.ParentEventId.Value, out var canonicalParentEventId)
                ? canonicalParentEventId
                : item.ParentEventId;
            var sourceSurface = IsExactOtelEvent(item) && !string.IsNullOrWhiteSpace(item.TraceId)
                ? exactOtelSurfaceByTrace[item.TraceId!]
                : item.SourceSurface is null ? null : SessionWire.ToWire(item.SourceSurface.Value);
            if (IsExactEventReplay(
                connection, transaction, item, eventId, parentEventId, sourceSurface, terminalOutcomes[item.EventId]))
            {
                continue;
            }
            Execute(connection, transaction, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind,terminal_outcome,terminal_policy_version)
                VALUES($event_id,$session_id,$run_id,$source_surface,$parent_event_id,$trace_id,$status,$source_adapter,$source_event_id,$type,$occurred_at,$content_state,$source_application_version,$adapter_version,$schema_fingerprint,$normalization_version,$match_kind,$terminal_outcome,$terminal_policy_version)
                ON CONFLICT(source_adapter,source_event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$session_id", Id(item.SessionId)), ("$run_id", item.RunId is null ? null : Id(item.RunId.Value)),
                ("$source_surface", sourceSurface),
                ("$parent_event_id", parentEventId is null ? null : Id(parentEventId.Value)), ("$trace_id", item.TraceId), ("$status", item.Status),
                ("$source_adapter", item.SourceAdapter), ("$source_event_id", item.SourceEventId), ("$type", item.Type),
                ("$occurred_at", CanonicalEventTimestamp(item.OccurredAt)), ("$content_state", SessionWire.ToWire(item.ContentState)),
                ("$source_application_version", item.SourceApplicationVersion), ("$adapter_version", item.AdapterVersion),
                ("$schema_fingerprint", item.SchemaFingerprint), ("$normalization_version", item.NormalizationVersion),
                ("$match_kind", MatchKind(item.MatchKind)),
                ("$terminal_outcome", terminalOutcomes[item.EventId]),
                ("$terminal_policy_version", terminalOutcomes[item.EventId] is null ? null : 1));
            newEventIds.Add(eventId);
        }

        foreach (var candidate in replayContentCandidates)
        {
            var eventId = canonicalEventIds[candidate.EventId];
            if (newEventIds.Contains(eventId))
                throw new InvalidOperationException("Invalid Session replay content candidate.");
            var comparison = catalog!.CompareSessionEventContentForReplay(
                connection,
                transaction,
                Id(eventId),
                candidate.ContentKind,
                candidate.ContentJson,
                replayComparisonNow);
            if (comparison == RetentionSessionEventContentReplayComparison.Conflict)
            {
                throw new InvalidOperationException("Session content capture conflict.");
            }
        }

        foreach (var content in batch.Content)
        {
            var eventId = canonicalEventIds[content.EventId];
            if (!newEventIds.Contains(eventId)
                && !Exists(connection, transaction,
                    "SELECT 1 FROM session_event_content WHERE event_id=$event;", ("$event", Id(eventId))))
            {
                continue;
            }
            var ownerToken = RandomNumberGenerator.GetBytes(32);
            Execute(connection, transaction, """
                INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
                VALUES($event_id,$content_kind,$content_json,$captured_at,$expires_at,$retention_owner_token)
                ON CONFLICT(event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$content_kind", content.ContentKind), ("$content_json", content.ContentJson),
                ("$captured_at", Timestamp(content.CapturedAt)), ("$expires_at", Timestamp(content.ExpiresAt)),
                ("$retention_owner_token", ownerToken));

            var source = ReadSessionContentForRegistration(connection, transaction, eventId);
            if (source.ContentKind != content.ContentKind
                || !string.Equals(source.ContentJson, content.ContentJson, StringComparison.Ordinal)
                || source.CapturedAt != content.CapturedAt
                || source.ExpiresAt != content.ExpiresAt)
            {
                throw new InvalidOperationException("Session content capture conflict.");
            }
            writeCheckpoint?.Invoke("after-session-content-source");
            if (catalog is not null)
                catalog.RegisterSessionEventContent(connection, transaction, source.EventId, source.ContentKind,
                    source.CapturedAt, source.ExpiresAt, source.SessionId, source.RunId, source.SourceAdapter,
                    source.SourceEventId, source.OwnerToken);
            writeCheckpoint?.Invoke("after-session-content-catalog");
        }

        ReduceSessionOutcomeAndCompleteness(connection, transaction, batch.Detail.Session.SessionId);

        transaction.Commit();
    }

    private static bool IsExactOtelEvent(ObservedSessionEvent item) =>
        string.Equals(item.SourceAdapter, "otel-exact", StringComparison.Ordinal)
        && string.Equals(item.Type, "otel.span", StringComparison.Ordinal);

    private static bool IsExactEventReplay(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ObservedSessionEvent item,
        Guid eventId,
        Guid? parentEventId,
        string? sourceSurface,
        string? terminalOutcome)
    {
        using (var collision = connection.CreateCommand())
        {
            collision.Transaction = transaction;
            collision.CommandText = "SELECT source_adapter,source_event_id FROM session_events WHERE event_id=$event;";
            Add(collision, "$event", Id(eventId));
            using var reader = collision.ExecuteReader();
            if (reader.Read()
                && (!string.Equals(reader.GetString(0), item.SourceAdapter, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1), item.SourceEventId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Session event replay conflict.");
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                   occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind,
                   terminal_outcome,terminal_policy_version
            FROM session_events
            WHERE source_adapter=$adapter COLLATE BINARY AND source_event_id=$source_event_id COLLATE BINARY;
            """;
        Add(command, "$adapter", item.SourceAdapter);
        Add(command, "$source_event_id", item.SourceEventId);
        using var persisted = command.ExecuteReader();
        if (!persisted.Read()) return false;
        var exact = string.Equals(persisted.GetString(0), Id(eventId), StringComparison.Ordinal)
            && string.Equals(persisted.GetString(1), Id(item.SessionId), StringComparison.Ordinal)
            && NullableTextEquals(persisted, 2, item.RunId is null ? null : Id(item.RunId.Value))
            && NullableTextEquals(persisted, 3, sourceSurface)
            && NullableTextEquals(persisted, 4, parentEventId is null ? null : Id(parentEventId.Value))
            && NullableTextEquals(persisted, 5, item.TraceId)
            && NullableTextEquals(persisted, 6, item.Status)
            && string.Equals(persisted.GetString(7), item.SourceAdapter, StringComparison.Ordinal)
            && string.Equals(persisted.GetString(8), item.SourceEventId, StringComparison.Ordinal)
            && string.Equals(persisted.GetString(9), item.Type, StringComparison.Ordinal)
            && string.Equals(CanonicalEventTimestamp(ParseTimestamp(persisted.GetString(10))), CanonicalEventTimestamp(item.OccurredAt), StringComparison.Ordinal)
            && string.Equals(persisted.GetString(11), SessionWire.ToWire(item.ContentState), StringComparison.Ordinal)
            && NullableTextEquals(persisted, 12, item.SourceApplicationVersion)
            && NullableTextEquals(persisted, 13, item.AdapterVersion)
            && NullableTextEquals(persisted, 14, item.SchemaFingerprint)
            && NullableTextEquals(persisted, 15, item.NormalizationVersion)
            && NullableTextEquals(persisted, 16, MatchKind(item.MatchKind))
            && NullableTextEquals(persisted, 17, terminalOutcome)
            && (terminalOutcome is null ? persisted.IsDBNull(18) : !persisted.IsDBNull(18) && persisted.GetInt64(18) == 1)
            && !persisted.Read();
        if (!exact) throw new InvalidOperationException("Session event replay conflict.");
        return true;
    }

    private static bool NullableTextEquals(SqliteDataReader reader, int ordinal, string? expected) =>
        expected is null ? reader.IsDBNull(ordinal)
        : !reader.IsDBNull(ordinal) && string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static IReadOnlyDictionary<Guid, string?> ValidateTerminalFacts(
        IReadOnlyList<ObservedSessionEvent> events,
        IReadOnlyList<SessionTerminalFact> facts)
    {
        if (facts.Any(fact => fact.PolicyVersion != 1)
            || facts.Select(fact => fact.EventId).Distinct().Count() != facts.Count
            || facts.Any(fact => events.All(item => item.EventId != fact.EventId)))
            throw new InvalidOperationException("Invalid Session terminal facts.");
        var byEvent = facts.ToDictionary(fact => fact.EventId);
        var result = new Dictionary<Guid, string?>();
        foreach (var item in events)
        {
            var allowed = AllowedTerminalOutcomes(item);
            if (!byEvent.TryGetValue(item.EventId, out var fact))
            {
                if (item.SourceAdapter == "copilot-sdk-stream"
                    && item.SourceSurface == SessionSourceSurface.CopilotSdk
                    && item.Type == "session.task_complete")
                {
                    result.Add(item.EventId, "clean");
                    continue;
                }
                if (allowed is not null) throw new InvalidOperationException("Missing Session terminal fact.");
                result.Add(item.EventId, null);
                continue;
            }
            var outcome = fact.Outcome switch
            {
                SessionTerminalOutcome.Clean => "clean",
                SessionTerminalOutcome.Failed => "failed",
                SessionTerminalOutcome.Neutral => "neutral",
                _ => throw new InvalidOperationException("Invalid Session terminal outcome."),
            };
            if (allowed is null || !allowed.Contains(outcome, StringComparer.Ordinal))
                throw new InvalidOperationException("Contradictory Session terminal fact.");
            result.Add(item.EventId, outcome);
        }
        return result;
    }

    private static string[]? AllowedTerminalOutcomes(ObservedSessionEvent item) =>
        item.SourceAdapter == "copilot-sdk-stream" && item.SourceSurface == SessionSourceSurface.CopilotSdk && item.Type == "session.task_complete"
            ? ["clean"]
            : item.SourceAdapter == "copilot-sdk-stream" && item.SourceSurface == SessionSourceSurface.CopilotSdk && item.Type == "session.shutdown"
                ? ["clean", "failed", "neutral"]
                : item.SourceAdapter == "copilot-compatible-hook" && item.SourceSurface is SessionSourceSurface.CopilotCli or SessionSourceSurface.VisualStudioCode or SessionSourceSurface.HookUnknown && item.Type == "SessionEnd"
                    ? ["clean", "failed", "neutral"]
                    : item.SourceAdapter == "claude-code-hook" && item.SourceSurface == SessionSourceSurface.ClaudeCode && item.Type == "SessionEnd"
                        ? ["clean", "neutral"]
                        : null;

    private static string? ClassifyMigrationTerminalOutcome(
        VersionThirteenMigrationEvent item,
        SessionEventContent? content)
    {
        if (item.SourceAdapter == "copilot-sdk-stream"
            && item.SourceSurface == SessionSourceSurface.CopilotSdk)
        {
            if (item.Type == "session.task_complete") return "clean";
            if (item.Type == "session.shutdown")
            {
                return TryReadRootString(content, "shutdownType", out var shutdownType)
                    ? shutdownType switch { "routine" => "clean", "error" => "failed", _ => "neutral" }
                    : "neutral";
            }
        }

        if (item.SourceAdapter == "copilot-compatible-hook"
            && item.SourceSurface is SessionSourceSurface.CopilotCli or SessionSourceSurface.VisualStudioCode or SessionSourceSurface.HookUnknown
            && item.Type == "SessionEnd")
        {
            return TryReadRootString(content, "reason", out var reason)
                ? reason switch
                {
                    "complete" or "user_exit" => "clean",
                    "error" or "timeout" => "failed",
                    _ => "neutral",
                }
                : "neutral";
        }

        if (item.SourceAdapter == "claude-code-hook"
            && item.SourceSurface == SessionSourceSurface.ClaudeCode
            && item.Type == "SessionEnd")
        {
            if (!TryReadRootString(content, "reason", out var reason))
            {
                return "neutral";
            }
            return reason is "clear" or "resume" or "logout" or "prompt_input_exit" ? "clean" : "neutral";
        }

        return null;
    }

    private static bool TryReadRootString(SessionEventContent? content, string propertyName, out string value)
    {
        value = string.Empty;
        if (content is null || content.ContentKind != "application/json") return false;
        try
        {
            using var document = JsonDocument.Parse(content.ContentJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement found = default;
            var count = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal)) continue;
                found = property.Value;
                count++;
            }
            if (count != 1 || found.ValueKind != JsonValueKind.String) return false;
            value = found.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ReduceSessionOutcomeAndCompleteness(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        var hasClean = false;
        var hasFailed = false;
        var hasNeutral = false;
        DateTimeOffset? endedAt = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT terminal_outcome,occurred_at FROM session_events WHERE session_id=$session AND terminal_outcome IS NOT NULL ORDER BY event_id;";
            Add(command, "$session", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                switch (reader.GetString(0))
                {
                    case "clean": hasClean = true; break;
                    case "failed": hasFailed = true; break;
                    case "neutral": hasNeutral = true; break;
                    default: throw new InvalidOperationException("Invalid Session terminal outcome.");
                }
                var occurredAt = ParseTimestamp(reader.GetString(1));
                if (endedAt is null || occurredAt > endedAt) endedAt = occurredAt;
            }
        }

        var status = hasFailed ? ObservedSessionStatus.Failed
            : hasClean ? ObservedSessionStatus.Completed
            : hasNeutral ? ObservedSessionStatus.Unknown
            : ObservedSessionStatus.Active;
        var completeness = CalculateDurableCompleteness(connection, transaction, sessionId, endedAt is not null);
        Execute(connection, transaction,
            "UPDATE sessions SET status=$status,ended_at=$ended_at,completeness=$completeness WHERE session_id=$session;",
            ("$status", SessionWire.ToWire(status)),
            ("$ended_at", endedAt is null ? null : CanonicalEventTimestamp(endedAt.Value)),
            ("$completeness", SessionWire.ToWire(completeness)),
            ("$session", Id(sessionId)));
    }

    private static SessionCompleteness CalculateDurableCompleteness(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        bool hasTerminal)
    {
        var hasNative = Exists(connection, transaction,
            "SELECT 1 FROM session_native_ids WHERE session_id=$session LIMIT 1;", ("$session", Id(sessionId)));
        var hasStart = false;
        var hasInstruction = false;
        var hasGap = false;
        var hasUnsupported = false;
        var hasExactOtel = false;
        var hasEvidence = false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,status,content_state,source_adapter,match_kind FROM session_events WHERE session_id=$session;";
        Add(command, "$session", Id(sessionId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hasEvidence = true;
            var type = reader.GetString(0);
            hasStart |= type is "session.start" or "SessionStart";
            hasInstruction |= type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted";
            hasGap |= type == "capture.started" && !reader.IsDBNull(1) && reader.GetString(1) == "gap_before_capture";
            hasUnsupported |= reader.GetString(2) == "unsupported";
            var adapter = reader.GetString(3);
            var matchKind = reader.IsDBNull(4) ? null : reader.GetString(4);
            hasExactOtel |= adapter == "otel-exact"
                || adapter == "claude-code-otel" && matchKind is "exact_native" or "explicit_link" or "trace_continuity" or "conversation_id";
        }
        return SessionCompletenessCalculator.Calculate(new(
            HasNativeId: hasNative,
            HasLifecycleStart: hasStart,
            HasUserInstruction: hasInstruction,
            HasSdkHookOrOtelEvidence: hasEvidence,
            HasTerminalEvidence: hasTerminal,
            HasExactLinkedOtelEnrichment: hasExactOtel,
            HasAllSurfaceRequiredEvidence: hasStart && hasInstruction && hasTerminal,
            HasUnsupportedVersion: hasUnsupported,
            HasIngestGap: !hasStart || hasGap));
    }

    private static bool ValidateCurrentSessionRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT source_adapter,source_surface,type,terminal_outcome,terminal_policy_version,typeof(terminal_outcome),typeof(terminal_policy_version),occurred_at,typeof(occurred_at) FROM session_events ORDER BY event_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var adapter = reader.GetString(0);
                var surface = reader.IsDBNull(1) ? null : reader.GetString(1);
                var type = reader.GetString(2);
                var outcomeType = reader.GetString(5);
                var policyType = reader.GetString(6);
                if (reader.IsDBNull(3) || reader.IsDBNull(4))
                {
                    if (!reader.IsDBNull(3)
                        || !reader.IsDBNull(4)
                        || !string.Equals(outcomeType, "null", StringComparison.Ordinal)
                        || !string.Equals(policyType, "null", StringComparison.Ordinal))
                        return false;
                }
                else if (!string.Equals(outcomeType, "text", StringComparison.Ordinal)
                    || !string.Equals(policyType, "integer", StringComparison.Ordinal))
                {
                    return false;
                }

                var outcome = reader.IsDBNull(3) ? null : reader.GetString(3);
                var policy = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
                var allowed = adapter == "copilot-sdk-stream" && surface == "copilot-sdk" && type == "session.task_complete"
                    ? outcome == "clean"
                    : adapter == "copilot-sdk-stream" && surface == "copilot-sdk" && type == "session.shutdown"
                        ? outcome is "clean" or "failed" or "neutral"
                        : adapter == "copilot-compatible-hook" && surface is "copilot-cli" or "vscode" or "hook-unknown" && type == "SessionEnd"
                            ? outcome is "clean" or "failed" or "neutral"
                            : adapter == "claude-code-hook" && surface == "claude-code" && type == "SessionEnd"
                                ? outcome is "clean" or "neutral"
                                : outcome is null;
                if (!allowed || (outcome is null) != (policy is null) || outcome is not null && policy != 1) return false;
                if (outcome is not null)
                {
                    if (!string.Equals(reader.GetString(8), "text", StringComparison.Ordinal)) return false;
                    var occurredAtText = reader.GetString(7);
                    if (!DateTimeOffset.TryParseExact(
                            occurredAtText,
                            "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var occurredAt)
                        || !string.Equals(occurredAtText, CanonicalEventTimestamp(occurredAt), StringComparison.Ordinal))
                        return false;
                }
            }
        }

        var sessionIds = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessionIds.Add(Guid.Parse(reader.GetString(0)));
        }
        foreach (var sessionId in sessionIds)
        {
            var hasClean = false;
            var hasFailed = false;
            var hasNeutral = false;
            DateTimeOffset? endedAt = null;
            using (var facts = connection.CreateCommand())
            {
                facts.Transaction = transaction;
                facts.CommandText = "SELECT terminal_outcome,occurred_at FROM session_events WHERE session_id=$session AND terminal_outcome IS NOT NULL ORDER BY event_id;";
                Add(facts, "$session", Id(sessionId));
                using var reader = facts.ExecuteReader();
                while (reader.Read())
                {
                    var outcome = reader.GetString(0);
                    hasClean |= outcome == "clean";
                    hasFailed |= outcome == "failed";
                    hasNeutral |= outcome == "neutral";
                    var occurredAt = ParseTimestamp(reader.GetString(1));
                    if (endedAt is null || occurredAt > endedAt) endedAt = occurredAt;
                }
            }
            var expectedStatus = hasFailed ? "failed" : hasClean ? "completed" : hasNeutral ? "unknown" : "active";
            var expectedCompleteness = SessionWire.ToWire(CalculateDurableCompleteness(connection, transaction!, sessionId, endedAt is not null));
            using var aggregate = connection.CreateCommand();
            aggregate.Transaction = transaction;
            aggregate.CommandText = "SELECT status,ended_at,completeness FROM sessions WHERE session_id=$session;";
            Add(aggregate, "$session", Id(sessionId));
            using var row = aggregate.ExecuteReader();
            if (!row.Read()
                || !string.Equals(row.GetString(0), expectedStatus, StringComparison.Ordinal)
                || (endedAt is null ? !row.IsDBNull(1) : row.IsDBNull(1) || !string.Equals(row.GetString(1), CanonicalEventTimestamp(endedAt.Value), StringComparison.Ordinal))
                || !string.Equals(row.GetString(2), expectedCompleteness, StringComparison.Ordinal)
                || row.Read()) return false;
        }
        return true;
    }

    private static string? ReadExactOtelSourceSurface(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string traceId)
    {
        if (!TableExists(connection, transaction, "monitor_traces"))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT client_kind
            FROM monitor_traces
            WHERE trace_id=$trace_id;
            """;
        Add(command, "$trace_id", traceId);
        return command.ExecuteScalar() switch
        {
            "vscode-copilot-chat" => "vscode",
            "copilot-cli" => "copilot-cli",
            _ => null,
        };
    }

    private static IReadOnlyList<ObservedSessionRun> OrderRuns(IReadOnlyList<ObservedSessionRun> runs) =>
        TopologicalOrder(runs, run => run.RunId, run => run.ParentRunId);

    private static IReadOnlyList<ObservedSessionEvent> OrderEvents(IReadOnlyList<ObservedSessionEvent> events) =>
        TopologicalOrder(events, item => item.EventId, item => item.ParentEventId);

    private static IReadOnlyList<T> TopologicalOrder<T>(
        IReadOnlyList<T> items,
        Func<T, Guid> getId,
        Func<T, Guid?> getParentId)
    {
        if (items.Select(getId).Distinct().Count() != items.Count)
        {
            throw new InvalidOperationException("Session aggregate relationship graph is invalid.");
        }

        var remaining = items.ToDictionary(getId);
        var ordered = new List<T>(items.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(item => getParentId(item) is not Guid parentId || !remaining.ContainsKey(parentId))
                .OrderBy(getId)
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidOperationException("Session aggregate relationship graph contains a cycle.");
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                remaining.Remove(getId(item));
            }
        }

        return ordered;
    }

    private static IReadOnlyDictionary<Guid, Guid> ResolveCanonicalEventIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ObservedSessionEvent> events)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var group in events.GroupBy(item => (item.SourceAdapter, item.SourceEventId)))
        {
            var persistedId = ReadEventId(connection, transaction, group.Key.SourceAdapter, group.Key.SourceEventId);
            var canonicalId = persistedId ?? group.Min(item => item.EventId);
            foreach (var item in group)
            {
                result.Add(item.EventId, canonicalId);
            }
        }

        return result;
    }

    private static void ValidateBatch(SqliteConnection connection, SqliteTransaction transaction, SessionWriteBatch batch)
    {
        var sessionId = batch.Detail.Session.SessionId;
        var sessionIdText = Id(sessionId);
        var runIds = batch.Detail.Runs.Select(run => run.RunId).ToHashSet();
        var eventIds = batch.Detail.Events.Select(item => item.EventId).ToHashSet();

        if (batch.Detail.NativeIds.Any(nativeId => nativeId.SessionId != sessionId)
            || batch.Detail.Runs.Any(run => run.SessionId != sessionId)
            || batch.Detail.Events.Any(item => item.SessionId != sessionId)
            || batch.Content.Any(content => !eventIds.Contains(content.EventId)))
        {
            throw OwnershipViolation();
        }

        foreach (var nativeId in batch.Detail.NativeIds)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_native_ids WHERE source_surface=$first AND native_session_id=$second COLLATE BINARY;",
                sessionIdText,
                ("$first", SessionWire.ToWire(nativeId.SourceSurface)),
                ("$second", nativeId.NativeSessionId),
                identityConflict: nativeId.SourceSurface == SessionSourceSurface.ClaudeCode);
        }

        foreach (var run in batch.Detail.Runs)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_runs WHERE run_id=$first;",
                sessionIdText,
                ("$first", Id(run.RunId)),
                identityConflict: run.SourceSurface == SessionSourceSurface.ClaudeCode);
            if (run.ParentRunId is not null && !runIds.Contains(run.ParentRunId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_runs", "run_id", run.ParentRunId.Value, sessionIdText);
            }
        }

        foreach (var item in batch.Detail.Events)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_events WHERE event_id=$first;",
                sessionIdText,
                ("$first", Id(item.EventId)),
                identityConflict: item.SourceSurface == SessionSourceSurface.ClaudeCode);
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_events WHERE source_adapter=$first AND source_event_id=$second;",
                sessionIdText,
                ("$first", item.SourceAdapter),
                ("$second", item.SourceEventId),
                identityConflict: item.SourceSurface == SessionSourceSurface.ClaudeCode);

            if (item.RunId is not null && !runIds.Contains(item.RunId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_runs", "run_id", item.RunId.Value, sessionIdText);
            }

            if (item.ParentEventId is not null && !eventIds.Contains(item.ParentEventId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_events", "event_id", item.ParentEventId.Value, sessionIdText);
            }
        }
    }

    private static void EnsureReferenceOwnedBySession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        Guid id,
        string expectedSessionId)
    {
        EnsureExistingOwnerMatches(
            connection,
            transaction,
            $"SELECT session_id FROM {table} WHERE {idColumn}=$first;",
            expectedSessionId,
            ("$first", Id(id)),
            requireExisting: true);
    }

    private static void EnsureExistingOwnerMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string expectedSessionId,
        (string Name, object? Value) first,
        (string Name, object? Value)? second = null,
        bool requireExisting = false,
        bool identityConflict = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, first.Name, first.Value);
        if (second is not null) Add(command, second.Value.Name, second.Value.Value);
        var owner = command.ExecuteScalar() as string;
        if ((requireExisting && owner is null)
            || (owner is not null && !string.Equals(owner, expectedSessionId, StringComparison.Ordinal)))
        {
            throw identityConflict ? new SessionIdentityConflictException() : OwnershipViolation();
        }
    }

    private static InvalidOperationException OwnershipViolation() =>
        new("Session aggregate ownership validation failed.");

    public ObservedSession? Resolve(SessionSourceSurface sourceSurface, string nativeSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeSessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,s.status,s.completeness,s.repository,s.workspace,s.started_at,s.ended_at,s.last_seen_at,s.raw_retention_state,s.created_at,s.updated_at
            FROM session_native_ids n JOIN sessions s ON s.session_id=n.session_id
            WHERE n.source_surface=$source_surface AND n.native_session_id=$native_session_id COLLATE BINARY;
            """;
        Add(command, "$source_surface", SessionWire.ToWire(sourceSurface));
        Add(command, "$native_session_id", nativeSessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public IReadOnlyList<ObservedSession> ListMostRecent(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions ORDER BY last_seen_at DESC, session_id DESC LIMIT $limit;
            """;
        Add(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<ObservedSession>();
        while (reader.Read()) result.Add(ReadSession(reader));
        return result;
    }

    public SessionDetail? GetDetail(Guid sessionId)
    {
        using var connection = Open();
        var session = ReadSession(connection, sessionId);
        if (session is null) return null;

        var nativeIds = new List<SessionNativeId>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids WHERE session_id=$id ORDER BY observed_at,source_surface,native_session_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) nativeIds.Add(new(Guid.Parse(reader.GetString(0)), SessionWire.ParseSourceSurface(reader.GetString(1)), reader.GetString(2), SessionWire.ParseBindingKind(reader.GetString(3)), ParseTimestamp(reader.GetString(4))));
        }

        var runs = new List<ObservedSessionRun>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,status,started_at,ended_at,input_tokens,output_tokens,total_tokens FROM session_runs WHERE session_id=$id ORDER BY started_at,run_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) runs.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableSurface(reader, 2), NullableString(reader, 3), NullableString(reader, 4), NullableGuid(reader, 5), NullableString(reader, 6),
                SessionWire.ParseStatus(reader.GetString(7)), NullableTimestamp(reader, 8), NullableTimestamp(reader, 9), NullableInt64(reader, 10), NullableInt64(reader, 11), NullableInt64(reader, 12)));
        }

        var events = new List<ObservedSessionEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind FROM session_events WHERE session_id=$id ORDER BY occurred_at,event_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) events.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableGuid(reader, 2), NullableSurface(reader, 3), NullableGuid(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), ParseTimestamp(reader.GetString(10)), SessionWire.ParseContentState(reader.GetString(11)),
                NullableString(reader, 12), NullableString(reader, 13), NullableString(reader, 14), NullableString(reader, 15), ParseMatchKind(reader, 16)));
        }

        return new(session, nativeIds, runs, events);
    }

    bool ICurrentSessionEligibilityStore.IsCurrentSessionEligible(Guid sessionId)
    {
        using var connection = Open();
        return IsCurrentSessionEligible(connection, transaction: null, sessionId);
    }

    public SessionHumanEvaluation? GetHumanEvaluation(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,verdict,recorded_at FROM session_human_evaluation WHERE session_id=$id;";
        Add(command, "$id", Id(sessionId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseTimestamp(reader.GetString(2)))
            : null;
    }

    public void UpsertHumanEvaluation(SessionHumanEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_human_evaluation(session_id,verdict,recorded_at)
            VALUES($session_id,$verdict,$recorded_at)
            ON CONFLICT(session_id) DO UPDATE SET verdict=excluded.verdict,recorded_at=excluded.recorded_at;
            """;
        Add(command, "$session_id", Id(evaluation.SessionId));
        Add(command, "$verdict", evaluation.Verdict);
        Add(command, "$recorded_at", Timestamp(evaluation.RecordedAt));
        command.ExecuteNonQuery();
    }

    public void ClearHumanEvaluation(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_human_evaluation WHERE session_id=$id;";
        Add(command, "$id", Id(sessionId));
        command.ExecuteNonQuery();
    }

    public async ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken)
    {
        if (retentionContext is null)
        {
            return new(SessionContentReadDisposition.Denied, null);
        }
        var detail = GetDetail(sessionId);
        var eventRow = detail?.Events.FirstOrDefault(item => item.EventId == eventId);
        if (eventRow is null || eventRow.ContentState is SessionContentState.NotCaptured or SessionContentState.Redacted or SessionContentState.Unsupported)
        {
            return new(SessionContentReadDisposition.NotFound, null);
        }

        var catalog = new RetentionCatalogStore(retentionContext, timeProvider);
        var request = new RetentionReadRequest(
            new(retentionContext.StoreInstanceId, RetentionStoreKind.SessionEventContent, Id(eventId)),
            RetentionReadKind.Access,
            timeProvider.GetUtcNow(),
            ExpectedRevision: null);
        var result = await catalog.ReadAsync(request, async (connection, transaction, grant, token) =>
        {
            using var command = connection.CreateCommand();
            ConfigureContentReadMaterializationCommand(
                command,
                transaction,
                grant,
                retentionContext.StoreInstanceId,
                sessionId,
                eventId);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            return new SessionEventContent(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)));
        }, cancellationToken).ConfigureAwait(false);

        if (result.Lease is { } lease)
            return new(SessionContentReadDisposition.Granted, new SessionContentReadLease(lease.Value, lease.DisposeAsync));
        return result.Disposition == RetentionReadDisposition.Busy
            ? new(SessionContentReadDisposition.Busy, null)
            : new(SessionContentReadDisposition.Denied, null);
    }

    internal static void ConfigureContentReadMaterializationCommand(
        SqliteCommand command,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        string storeInstanceId,
        Guid sessionId,
        Guid eventId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeInstanceId);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.event_id,c.content_kind,c.content_json,c.captured_at,c.expires_at
            FROM session_event_content c
            JOIN session_events e ON e.event_id=c.event_id
            JOIN retention_items i ON i.item_id=$retention_read_item_id
                AND i.store_instance_id=$retention_store_instance_id
                AND i.store_kind='session_event_content'
                AND i.source_item_id=c.event_id
                AND i.revision=$retention_read_revision
            JOIN retention_leases l ON l.item_id=i.item_id
                AND l.lease_kind=$retention_read_lease_kind
                AND l.owner=$retention_read_lease_owner
                AND l.generation=$retention_read_lease_generation
                AND l.expires_at=$retention_read_lease_expires_at
            WHERE c.event_id=$event_id AND e.session_id=$session_id
                AND c.retention_owner_token=$retention_read_source_token;
            """;
        Add(command, "$event_id", Id(eventId));
        Add(command, "$session_id", Id(sessionId));
        Add(command, "$retention_store_instance_id", storeInstanceId);
        grant.BindAdmissionSelectorCapability(command);
    }

    public SessionRawRetentionState GetRawRetentionState(Guid sessionId)
    {
        if (retentionContext is null)
        {
            return SessionRawRetentionState.NotCaptured;
        }
        using var connection = Open();
        return SessionWire.ParseRawRetentionState(RetentionCatalogStore.ProjectSessionRawRetentionState(
            connection,
            transaction: null,
            retentionContext.StoreInstanceId,
            Id(sessionId),
            timeProvider.GetUtcNow()));
    }
    public SessionProjectionState? GetProjectionState(string projectorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectorKey);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT projector_key,projection_cursor,unsupported_event_version_count,updated_at FROM session_projection_state WHERE projector_key=$key;";
        Add(command, "$key", projectorKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), NullableInt64(reader, 1), reader.GetInt64(2), ParseTimestamp(reader.GetString(3)))
            : null;
    }

    public void UpsertProjectionState(SessionProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_state(projector_key,projection_cursor,unsupported_event_version_count,updated_at)
            VALUES($key,$cursor,$unsupported,$updated_at)
            ON CONFLICT(projector_key) DO UPDATE SET projection_cursor=excluded.projection_cursor,
            unsupported_event_version_count=excluded.unsupported_event_version_count,updated_at=excluded.updated_at;
            """;
        Add(command, "$key", state.ProjectorKey);
        Add(command, "$cursor", state.ProjectionCursor);
        Add(command, "$unsupported", state.UnsupportedEventVersionCount);
        Add(command, "$updated_at", Timestamp(state.UpdatedAt));
        command.ExecuteNonQuery();
    }

    public void CreateObjectiveEvaluation(ObjectiveEvaluationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!ObjectiveEvaluationValidation.IsValid(receipt)) throw new ArgumentException("Invalid objective evaluation.", nameof(receipt));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!IsCurrentSessionEligible(connection, transaction, receipt.SessionId)
            || !ExactReceiptReferenceScope(connection, transaction, receipt))
            throw new ArgumentException("Objective evidence is not exact.", nameof(receipt));
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO objective_evaluations(objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at) VALUES($id,$session,$run,$trace,$result,$severity,$evaluator,$version,$criterion,$case,$recorded);";
            Add(command, "$id", Id(receipt.ObjectiveEvaluationId)); Add(command, "$session", Id(receipt.SessionId)); Add(command, "$run", Id(receipt.RunId)); Add(command, "$trace", receipt.TraceId); Add(command, "$result", receipt.Result == ObjectiveResult.Pass ? "pass" : "fail"); Add(command, "$severity", receipt.Severity == ObjectiveSeverity.Normal ? "normal" : "severe"); Add(command, "$evaluator", receipt.EvaluatorId); Add(command, "$version", receipt.EvaluatorVersion); Add(command, "$criterion", receipt.CriterionId); Add(command, "$case", receipt.CaseKey); Add(command, "$recorded", Timestamp(receipt.RecordedAt));
            command.ExecuteNonQuery();
        }
        foreach (var (evidence, index) in receipt.Evidence.Select((value, index) => (value, index)))
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO objective_evaluation_evidence(objective_evaluation_id,evidence_order,kind,reference_id) VALUES($id,$order,$kind,$reference);";
            Add(command, "$id", Id(receipt.ObjectiveEvaluationId)); Add(command, "$order", index); Add(command, "$kind", evidence.Kind); Add(command, "$reference", evidence.ReferenceId); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<ObjectiveEvaluationReceipt> ListObjectiveEvaluations(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at FROM objective_evaluations WHERE session_id=$session ORDER BY recorded_at,objective_evaluation_id;";
        Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader();
        var rows = new List<ObjectiveEvaluationReceipt>();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            rows.Add(new(id, Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4) == "pass" ? ObjectiveResult.Pass : ObjectiveResult.Fail, reader.GetString(5) == "normal" ? ObjectiveSeverity.Normal : ObjectiveSeverity.Severe, reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), Evidence(connection, id), ParseTimestamp(reader.GetString(10))));
        }
        return rows;
    }

    EffectCandidateSnapshot IEffectCurrentUseStore.ReadEffectCandidateSnapshot(Guid proposalId, Guid applyId, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var application = ReadCurrentEffectApplication(connection, transaction, proposalId, applyId);
        var sessions = new List<EffectCandidateSessionSnapshot>();
        if (application is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SessionCurrentUseEligibilitySqlV1.EligibleSessionIdsCte + """
                SELECT session.session_id,session.status,session.completeness,session.repository,session.workspace,
                       session.started_at,session.ended_at,session.last_seen_at,session.raw_retention_state,
                       session.created_at,session.updated_at,
                       EXISTS(
                           SELECT 1 FROM session_native_ids native
                           WHERE native.session_id=session.session_id AND native.binding_kind='native'
                       ),
                       CASE WHEN current_session.session_id IS NULL THEN 0 ELSE 1 END,
                        EXISTS(SELECT 1 FROM session_human_evaluation human WHERE human.session_id=session.session_id)
                FROM sessions session
                LEFT JOIN current_session_use_eligibility current_session ON current_session.session_id=session.session_id
                ORDER BY session.last_seen_at DESC,session.session_id DESC
                LIMIT $limit;
                """;
            Add(command, "$limit", limit);
            using var reader = command.ExecuteReader();
            var candidateRows = new List<(ObservedSession Session, bool ExactBound, bool CurrentEligible, bool HumanEvidenceAvailable)>();
            while (reader.Read())
            {
                candidateRows.Add((
                    ReadSession(reader),
                    reader.GetInt32(11) != 0,
                    reader.GetInt32(11) != 0 && reader.GetInt32(12) != 0,
                    reader.GetInt32(13) != 0));
            }
            reader.Close();
            foreach (var row in candidateRows)
            {
                sessions.Add(new(
                    row.Session,
                    row.ExactBound,
                    row.CurrentEligible,
                    row.HumanEvidenceAvailable || ExactObjectives(connection, transaction, row.Session.SessionId).Count != 0));
            }
        }

        comparisonCheckpoint?.Invoke("after_effect_candidate_database_snapshot");
        transaction.Commit();
        return new(application, sessions);
    }

    public EffectReceipt RecordEffectComparison(EffectComparisonRequest request, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateComparisonRequest(request);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var proposal = ReadImprovementProposal(connection, transaction, request.ProposalId);
        if (proposal is null || proposal.Revision != request.ProposalRevision || proposal.Status != ImprovementProposalStatus.Recommended)
            throw new InvalidOperationException("Proposal revision is stale.");

        var apply = ReadActiveApply(connection, transaction, request);
        if (apply is null)
            throw new InvalidOperationException("Application is not active.");
        comparisonCheckpoint?.Invoke("after_active_apply_read");

        var facts = new List<SessionEffectFacts>();
        var sessionFacts = new List<(Guid SessionId, string? EffectiveQuality, bool SevereFailure)>();
        var capturedEvidence = new List<(Guid SessionId, string Kind, string ReferenceId, string? RecordedAt, string? HumanVerdict)>();
        foreach (var item in request.Sessions.Where(item => item.Classification is "pre" or "post"))
        {
            var session = ReadComparisonSession(connection, transaction, item.SessionId);
            if (session is null || !IsComparable(session.Value))
                throw new InvalidOperationException("Comparison evidence is stale.");
            if (item.Classification == "pre" && session.Value.EndedAt > apply.Value.AppliedAt || item.Classification == "post" && session.Value.StartedAt < apply.Value.AppliedAt)
                throw new ArgumentException("Session crosses application boundary.", nameof(request));

            var evidence = new List<string>();
            var qualityPass = true;
            var hasDecisiveQuality = false;
            var severe = false;
            using (var human = connection.CreateCommand())
            {
                human.Transaction = transaction;
                human.CommandText = "SELECT verdict,recorded_at FROM session_human_evaluation WHERE session_id=$session;";
                Add(human, "$session", Id(item.SessionId));
                using var reader = human.ExecuteReader();
                if (reader.Read())
                {
                    var id = "human:" + Id(item.SessionId);
                    evidence.Add(id);
                    capturedEvidence.Add((item.SessionId, "human", Id(item.SessionId), reader.GetString(1), reader.GetString(0)));
                    hasDecisiveQuality = true;
                    qualityPass &= reader.GetString(0) == "expected";
                }
            }
            foreach (var objective in ExactObjectives(connection, transaction, item.SessionId))
            {
                evidence.Add(Id(objective.ObjectiveEvaluationId));
                capturedEvidence.Add((item.SessionId, "objective", Id(objective.ObjectiveEvaluationId), Timestamp(objective.RecordedAt), null));
                hasDecisiveQuality = true;
                qualityPass &= objective.Result == ObjectiveResult.Pass;
                severe |= objective.Result == ObjectiveResult.Fail && objective.Severity == ObjectiveSeverity.Severe;
                foreach (var reference in objective.Evidence)
                    capturedEvidence.Add((item.SessionId, "objective_" + reference.Kind, reference.ReferenceId, null, null));
            }
            var duration = session.Value.StartedAt is { } started && session.Value.EndedAt is { } ended ? (long?)(ended - started).TotalMilliseconds : null;
            var tokens = SessionTokens(connection, transaction, item.SessionId);
            facts.Add(new(item.SessionId, item.Classification, item.CaseKey, qualityPass, severe, duration, tokens, evidence));
            sessionFacts.Add((item.SessionId, hasDecisiveQuality ? qualityPass ? "pass" : "fail" : "missing", severe));
        }

        var result = EffectVerdictEngine.Evaluate(new(true, facts.Where(item => item.Side == "pre").ToArray(), facts.Where(item => item.Side == "post").ToArray(), []));
        var comparisonId = Guid.CreateVersion7();
        var cohortRevision = NextCohortRevision(connection, transaction, request.ProposalId, request.ApplyId);
        Execute(connection, transaction, "INSERT INTO effect_comparisons(comparison_id,cohort_revision,proposal_id,proposal_revision,apply_id,recorded_at) VALUES($id,$cohort,$proposal,$revision,$apply,$recorded);", ("$id", Id(comparisonId)), ("$cohort", cohortRevision), ("$proposal", Id(request.ProposalId)), ("$revision", request.ProposalRevision), ("$apply", Id(request.ApplyId)), ("$recorded", Timestamp(recordedAt)));
        foreach (var (item, order) in request.Sessions.Select((value, index) => (value, index)))
        {
            var fact = sessionFacts.FirstOrDefault(value => value.SessionId == item.SessionId);
            Execute(connection, transaction, "INSERT INTO effect_comparison_sessions(comparison_id,session_id,classification,case_key,exclusion_reason,session_order,effective_quality,severe_failure) VALUES($comparison,$session,$classification,$case,$reason,$order,$quality,$severe);", ("$comparison", Id(comparisonId)), ("$session", Id(item.SessionId)), ("$classification", item.Classification), ("$case", item.CaseKey), ("$reason", item.ExclusionReason), ("$order", order), ("$quality", fact.EffectiveQuality), ("$severe", fact.SevereFailure ? 1 : 0));
        }
        foreach (var (evidence, order) in capturedEvidence.Select((value, index) => (value, index)))
            Execute(connection, transaction, "INSERT INTO effect_comparison_evidence(comparison_id,evidence_order,session_id,kind,reference_id,recorded_at,human_verdict) VALUES($comparison,$order,$session,$kind,$reference,$recorded,$human_verdict);", ("$comparison", Id(comparisonId)), ("$order", order), ("$session", Id(evidence.SessionId)), ("$kind", evidence.Kind), ("$reference", evidence.ReferenceId), ("$recorded", evidence.RecordedAt), ("$human_verdict", evidence.HumanVerdict));
        comparisonCheckpoint?.Invoke("after_cohort_session_evidence_insert");
        Execute(connection, transaction, "INSERT INTO effect_receipts(comparison_id,verdict,result_json,recorded_at) VALUES($comparison,$verdict,$result,$recorded);", ("$comparison", Id(comparisonId)), ("$verdict", VerdictText(result.Verdict)), ("$result", System.Text.Json.JsonSerializer.Serialize(result)), ("$recorded", Timestamp(recordedAt)));
        comparisonCheckpoint?.Invoke("after_effect_receipt_insert");
        if (result.Verdict == EffectVerdict.Improved)
        {
            var changed = Execute(connection, transaction, "UPDATE improvement_proposals SET status='verified',verified_at=$time,updated_at=$time WHERE proposal_id=$proposal AND revision=$revision AND status='recommended';", ("$time", Timestamp(recordedAt)), ("$proposal", Id(request.ProposalId)), ("$revision", request.ProposalRevision));
            if (changed != 1) throw new InvalidOperationException("Proposal revision is stale.");
        }
        transaction.Commit();
        return new(comparisonId, cohortRevision, request.ProposalId, request.ProposalRevision, request.ApplyId, result, "active", recordedAt);
    }

    public IReadOnlyList<EffectReceipt> ListEffectReceipts(Guid proposalId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var stored = new List<EffectReceipt>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT comparison.comparison_id,comparison.cohort_revision,comparison.proposal_id,
                       comparison.proposal_revision,comparison.apply_id,receipt.result_json,comparison.recorded_at
                FROM effect_comparisons comparison
                JOIN effect_receipts receipt ON receipt.comparison_id=comparison.comparison_id
                WHERE comparison.proposal_id=$proposal
                ORDER BY comparison.recorded_at,comparison.comparison_id;
                """;
            Add(command, "$proposal", Id(proposalId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                stored.Add(new(
                    Guid.Parse(reader.GetString(0)), reader.GetInt32(1), Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
                    Guid.Parse(reader.GetString(4)), System.Text.Json.JsonSerializer.Deserialize<EffectVerdictResult>(reader.GetString(5))!,
                    "active", ParseTimestamp(reader.GetString(6))));
            }
        }

        var receipts = stored.Select(receipt => receipt with
        {
            VerificationState = IsCurrentEffectApplicationDatabase(
                    connection, transaction, receipt.ProposalId, receipt.ProposalRevision, receipt.ApplyId)
                && AreEffectSessionsCurrent(connection, transaction, receipt.ComparisonId)
                    ? "active"
                    : "invalidated",
        }).ToArray();
        comparisonCheckpoint?.Invoke("after_effect_receipt_list_snapshot");
        transaction.Commit();
        return receipts;
    }

    public EffectComparisonDetail? GetEffectComparison(Guid comparisonId)
        => ((IEffectCurrentUseStore)this).ReadEffectComparisonSnapshot(comparisonId)?.Detail;

    EffectComparisonSnapshot? IEffectCurrentUseStore.ReadEffectComparisonSnapshot(Guid comparisonId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        EffectReceipt receipt;
        using (var receiptCommand = connection.CreateCommand())
        {
            receiptCommand.Transaction = transaction;
            receiptCommand.CommandText = """
                SELECT comparison.cohort_revision,comparison.proposal_id,comparison.proposal_revision,
                       comparison.apply_id,receipt.result_json,comparison.recorded_at
                FROM effect_comparisons comparison
                JOIN effect_receipts receipt ON receipt.comparison_id=comparison.comparison_id
                WHERE comparison.comparison_id=$comparison;
                """;
            Add(receiptCommand, "$comparison", Id(comparisonId));
            using var reader = receiptCommand.ExecuteReader();
            if (!reader.Read()) return null;
            receipt = new(
                comparisonId, reader.GetInt32(0), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                Guid.Parse(reader.GetString(3)), System.Text.Json.JsonSerializer.Deserialize<EffectVerdictResult>(reader.GetString(4))!,
                "active", ParseTimestamp(reader.GetString(5)));
        }

        var application = ReadCurrentEffectApplication(connection, transaction, receipt.ProposalId, receipt.ApplyId);
        var databaseCurrent = application is not null
            && application.Receipt.ProposalRevision == receipt.ProposalRevision
            && AreEffectSessionsCurrent(connection, transaction, receipt.ComparisonId);
        receipt = receipt with { VerificationState = databaseCurrent ? "active" : "invalidated" };
        comparisonCheckpoint?.Invoke("after_effect_receipt_snapshot");

        var sessions = new List<EffectComparisonSession>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,classification,case_key,exclusion_reason,effective_quality,severe_failure FROM effect_comparison_sessions WHERE comparison_id=$comparison ORDER BY session_order;";
            Add(command, "$comparison", Id(comparisonId)); using var rows = command.ExecuteReader();
            while (rows.Read()) sessions.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4), rows.GetInt32(5) != 0));
        }
        var evidence = new List<EffectComparisonEvidence>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,kind,reference_id,recorded_at,human_verdict FROM effect_comparison_evidence WHERE comparison_id=$comparison ORDER BY evidence_order;";
            Add(command, "$comparison", Id(comparisonId)); using var rows = command.ExecuteReader();
            while (rows.Read()) evidence.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? null : ParseTimestamp(rows.GetString(3)), rows.IsDBNull(4) ? null : rows.GetString(4)));
        }
        transaction.Commit();
        return new(new(receipt, sessions, evidence), application);
    }

    private static IReadOnlyList<ObjectiveEvaluationEvidence> Evidence(SqliteConnection connection, Guid id) =>
        Evidence(connection, transaction: null, id);

    private static IReadOnlyList<ObjectiveEvaluationEvidence> Evidence(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT kind,reference_id FROM objective_evaluation_evidence WHERE objective_evaluation_id=$id ORDER BY evidence_order;"; Add(command, "$id", Id(id)); using var reader = command.ExecuteReader(); var result = new List<ObjectiveEvaluationEvidence>(); while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1))); return result;
    }

    private static bool ExactReceiptReferenceScope(SqliteConnection connection, SqliteTransaction transaction, ObjectiveEvaluationReceipt receipt)
    {
        using var scope = connection.CreateCommand(); scope.Transaction = transaction;
        scope.CommandText = "SELECT 1 FROM session_runs WHERE session_id=$session AND run_id=$run AND trace_id=$trace;";
        Add(scope, "$session", Id(receipt.SessionId)); Add(scope, "$run", Id(receipt.RunId)); Add(scope, "$trace", receipt.TraceId);
        if (scope.ExecuteScalar() is null) return false;
        foreach (var evidence in receipt.Evidence)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = evidence.Kind switch
            {
                "run" => "SELECT 1 FROM session_runs WHERE session_id=$session AND run_id=$reference AND run_id=$run AND trace_id=$trace;",
                "event" => "SELECT 1 FROM session_events WHERE session_id=$session AND event_id=$reference AND run_id=$run AND trace_id=$trace;",
                "trace" => "SELECT 1 FROM session_runs WHERE session_id=$session AND run_id=$run AND trace_id=$reference AND trace_id=$trace;",
                "gate" when evidence.ReferenceId == "terminal" => "SELECT 1 FROM session_events WHERE session_id=$session AND terminal_outcome IN ('clean','failed','neutral') AND terminal_policy_version=1;",
                "gate" when evidence.ReferenceId == "error" => "SELECT 1 FROM session_events WHERE session_id=$session AND run_id=$run AND trace_id=$trace AND status='error';",
                _ => "SELECT 0;",
            };
            Add(command, "$session", Id(receipt.SessionId)); Add(command, "$run", Id(receipt.RunId)); Add(command, "$trace", receipt.TraceId); Add(command, "$reference", evidence.ReferenceId);
            if (command.ExecuteScalar() is not 1L) return false;
        }
        return true;
    }

    public IReadOnlyList<ImprovementProposal> ListImprovementProposals(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT proposal_id FROM improvement_proposal_sessions WHERE session_id=$session_id ORDER BY source_order,proposal_id;";
        Add(command, "$session_id", Id(sessionId));
        using var reader = command.ExecuteReader();
        var proposalIds = new List<Guid>();
        while (reader.Read())
        {
            proposalIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return proposalIds.Select(proposalId => ReadImprovementProposal(connection, proposalId)
            ?? throw new InvalidOperationException("Improvement proposal was not found.")).ToArray();
    }

    public ImprovementProposal? GetImprovementProposal(Guid proposalId)
    {
        using var connection = Open();
        return ReadImprovementProposal(connection, proposalId);
    }

    public void CreateImprovementProposal(ImprovementProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Status != ImprovementProposalStatus.Candidate)
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidStatus);
        }
        if (proposal.RecommendedAt is not null || proposal.VerifiedAt is not null)
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidShape);
        }

        ValidateProposalShape(proposal);
        writeCheckpoint?.Invoke("before_proposal_create_transaction");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var sessionId in proposal.SourceSessionIds)
        {
            if (!SessionExists(connection, transaction, sessionId))
                throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotFound);
        }
        foreach (var sessionId in proposal.SourceSessionIds)
        {
            if (!IsCurrentSessionEligible(connection, transaction, sessionId))
                throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotExactBound);
        }

        var evidence = ResolveProposalEvidence(connection, transaction, proposal);
        if (evidence.Any(result => result.State == ProposalEvidenceResolution.Missing))
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotFound);
        if (evidence.Any(result => result.State == ProposalEvidenceResolution.OutOfScope))
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotExactBound);

        Execute(connection, transaction, """
            INSERT INTO improvement_proposals(proposal_id,revision,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at,verified_at)
            VALUES($proposal_id,$revision,$status,$target_kind,$target_label,$title,$summary,$expected_effect,$risk_note,$created_at,$updated_at,$recommended_at,$verified_at);
            """,
            ("$proposal_id", Id(proposal.ProposalId)), ("$revision", proposal.Revision), ("$status", ProposalStatus(proposal.Status)),
            ("$target_kind", proposal.TargetKind), ("$target_label", proposal.TargetLabel), ("$title", proposal.Title),
            ("$summary", proposal.Summary), ("$expected_effect", proposal.ExpectedEffect), ("$risk_note", proposal.RiskNote),
            ("$created_at", Timestamp(proposal.CreatedAt)), ("$updated_at", Timestamp(proposal.UpdatedAt)),
            ("$recommended_at", Timestamp(proposal.RecommendedAt)), ("$verified_at", Timestamp(proposal.VerifiedAt)));

        for (var index = 0; index < proposal.SourceSessionIds.Count; index++)
        {
            Execute(connection, transaction, "INSERT INTO improvement_proposal_sessions(proposal_id,session_id,source_order) VALUES($proposal_id,$session_id,$source_order);",
                ("$proposal_id", Id(proposal.ProposalId)), ("$session_id", Id(proposal.SourceSessionIds[index])), ("$source_order", index));
        }

        for (var index = 0; index < proposal.EvidenceReferences.Count; index++)
        {
            var reference = proposal.EvidenceReferences[index];
            Execute(connection, transaction, "INSERT INTO improvement_proposal_evidence(proposal_id,evidence_order,kind,reference_id) VALUES($proposal_id,$evidence_order,$kind,$reference_id);",
                ("$proposal_id", Id(proposal.ProposalId)), ("$evidence_order", index), ("$kind", reference.Kind), ("$reference_id", reference.ReferenceId));
        }

        transaction.Commit();
    }

    public ImprovementProposal UpdateImprovementProposalStatus(Guid proposalId, ImprovementProposalStatus status, DateTimeOffset updatedAt)
    {
        RejectVerified(status);
        if (status is not ImprovementProposalStatus.Candidate and not ImprovementProposalStatus.Recommended)
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidStatus);

        writeCheckpoint?.Invoke("before_proposal_status_transaction");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var proposal = ReadImprovementProposal(connection, transaction, proposalId)
            ?? throw new ImprovementProposalStoreException(ImprovementProposalFailure.ProposalNotFound);
        RejectVerified(proposal.Status);

        if (proposal.Status != status && Exists(connection, transaction,
            "SELECT 1 FROM proposal_apply_pending WHERE proposal_id=$proposal_id AND operation_kind='apply';",
            ("$proposal_id", Id(proposalId))))
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidStatus);
        }

        if (status == ImprovementProposalStatus.Recommended)
        {
            ValidatePromotion(connection, transaction, proposal, proposalId);
        }

        var rows = Execute(connection, transaction, """
            UPDATE improvement_proposals
            SET status=$status, revision=revision + CASE WHEN status <> $status THEN 1 ELSE 0 END, updated_at=$updated_at, recommended_at=$recommended_at
            WHERE proposal_id=$proposal_id AND (status=$status OR NOT EXISTS(SELECT 1 FROM proposal_apply_pending WHERE proposal_id=$proposal_id AND operation_kind='apply'));
            """,
            ("$status", ProposalStatus(status)), ("$updated_at", Timestamp(updatedAt)),
            ("$recommended_at", status == ImprovementProposalStatus.Recommended ? Timestamp(updatedAt) : null),
            ("$proposal_id", Id(proposalId)));
        if (rows != 1) throw new InvalidOperationException("Improvement proposal lifecycle update affected an unexpected row count.");
        var updated = ReadImprovementProposal(connection, transaction, proposalId)
            ?? throw new InvalidOperationException("Updated improvement proposal could not be read.");
        transaction.Commit();
        return updated;
    }

    private static void ValidateProposalShape(ImprovementProposal proposal)
    {
        if (!IsUuidVersion7(proposal.ProposalId)
            || !IsOneOf(proposal.TargetKind, "skill", "agent", "instructions", "template", "hook_config")
            || !IsBounded(proposal.TargetLabel, 200)
            || !IsBounded(proposal.Title, 200)
            || !IsBounded(proposal.Summary, 2000)
            || !IsBounded(proposal.ExpectedEffect, 1000)
            || !IsBounded(proposal.RiskNote, 1000)
            || proposal.SourceSessionIds is null
            || proposal.SourceSessionIds.Count == 0
            || proposal.SourceSessionIds.Any(sessionId => !IsUuidVersion7(sessionId))
            || proposal.SourceSessionIds.Distinct().Count() != proposal.SourceSessionIds.Count
            || proposal.EvidenceReferences is null
            || proposal.EvidenceReferences.Count is < 1 or > 10
            || proposal.EvidenceReferences.Any(reference => reference is null
                || !IsOneOf(reference.Kind, "event", "run", "trace", "gate")
                || !IsBounded(reference.ReferenceId, 512)
                || reference.Kind is "event" or "run" && !Guid.TryParse(reference.ReferenceId, out _)
                || reference.Kind == "gate" && !IsOneOf(reference.ReferenceId, "terminal", "error")))
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidShape);
        }
    }

    private static void ValidatePromotion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImprovementProposal proposal,
        Guid proposalId)
    {
        ValidateProposalShape(proposal);
        var evidence = ResolveProposalEvidence(connection, transaction, proposal);
        if (evidence.Any(result => result.State == ProposalEvidenceResolution.Missing))
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotFound);

        foreach (var sessionId in proposal.SourceSessionIds)
        {
            if (!SessionExists(connection, transaction, sessionId))
                throw new ImprovementProposalStoreException(ImprovementProposalFailure.EvidenceNotFound);
        }

        var evidencedSourceSessions = evidence
            .SelectMany(result => result.SourceSessionIds)
            .ToHashSet();
        if (proposal.SourceSessionIds.Count < 2
            || proposal.SourceSessionIds.Distinct().Count() != proposal.SourceSessionIds.Count
            || proposal.EvidenceReferences.Count == 0
            || evidence.Any(result => result.State == ProposalEvidenceResolution.OutOfScope)
            || evidencedSourceSessions.Count < 2
            || proposal.SourceSessionIds.Any(sessionId => !IsCurrentSessionEligible(connection, transaction, sessionId)))
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InsufficientRecommendationEvidence);
        }

        foreach (var sessionId in proposal.SourceSessionIds)
        {
            using var recommendationCommand = connection.CreateCommand();
            recommendationCommand.Transaction = transaction;
            recommendationCommand.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM improvement_proposals proposal
                    JOIN improvement_proposal_sessions source ON source.proposal_id=proposal.proposal_id
                    WHERE source.session_id=$session_id AND proposal.status='recommended' AND proposal.proposal_id <> $proposal_id
                );
                """;
            Add(recommendationCommand, "$session_id", Id(sessionId));
            Add(recommendationCommand, "$proposal_id", Id(proposalId));
            if (Convert.ToInt64(recommendationCommand.ExecuteScalar()) != 0)
            {
                throw new ImprovementProposalStoreException(ImprovementProposalFailure.RecommendationAlreadyExists);
            }
        }
    }

    private static IReadOnlyList<ProposalEvidenceResult> ResolveProposalEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImprovementProposal proposal) =>
        proposal.EvidenceReferences
            .Select(reference => ResolveProposalEvidence(connection, transaction, proposal.SourceSessionIds, reference))
            .ToArray();

    private static ProposalEvidenceResult ResolveProposalEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> sourceSessionIds,
        ImprovementProposalEvidenceReference reference)
    {
        if (reference.Kind is "event" or "run")
        {
            if (!Guid.TryParse(reference.ReferenceId, out var referenceId))
                throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidShape);

            var sessionColumn = reference.Kind == "event" ? "event_id" : "run_id";
            var table = reference.Kind == "event" ? "session_events" : "session_runs";
            if (!Exists(connection, transaction, $"SELECT 1 FROM {table} WHERE {sessionColumn}=$reference_id;", ("$reference_id", Id(referenceId))))
                return new(ProposalEvidenceResolution.Missing, []);

            var relatedSessions = sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                $"SELECT 1 FROM {table} WHERE {sessionColumn}=$reference_id AND session_id=$session_id;",
                ("$reference_id", Id(referenceId)), ("$session_id", Id(sessionId)))).ToArray();
            return new(relatedSessions.Length == 0 ? ProposalEvidenceResolution.OutOfScope : ProposalEvidenceResolution.Related, relatedSessions);
        }

        if (reference.Kind == "trace")
        {
            if (!Exists(connection, transaction, "SELECT 1 FROM session_runs WHERE trace_id=$trace_id;", ("$trace_id", reference.ReferenceId)))
                return new(ProposalEvidenceResolution.Missing, []);

            var relatedSessions = sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_runs WHERE trace_id=$trace_id AND session_id=$session_id;",
                ("$trace_id", reference.ReferenceId), ("$session_id", Id(sessionId)))).ToArray();
            return new(relatedSessions.Length == 0 ? ProposalEvidenceResolution.OutOfScope : ProposalEvidenceResolution.Related, relatedSessions);
        }

        if (reference.Kind != "gate")
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidShape);

        var gateSessions = reference.ReferenceId switch
        {
            "terminal" => sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_events WHERE session_id=$session_id AND terminal_outcome IN ('clean','failed','neutral') AND terminal_policy_version=1;",
                ("$session_id", Id(sessionId)))).ToArray(),
            "error" => sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_events WHERE session_id=$session_id AND status='error';",
                ("$session_id", Id(sessionId)))).ToArray(),
            _ => throw new ImprovementProposalStoreException(ImprovementProposalFailure.InvalidShape),
        };

        if (reference.ReferenceId == "error"
            && !Exists(connection, transaction, "SELECT 1 FROM session_events WHERE status='error';"))
        {
            return new(ProposalEvidenceResolution.Missing, []);
        }

        return new(gateSessions.Length == 0 ? ProposalEvidenceResolution.OutOfScope : ProposalEvidenceResolution.Related, gateSessions);
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return command.ExecuteScalar() is not null;
    }

    private static bool SessionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId) =>
        Exists(connection, transaction, "SELECT 1 FROM sessions WHERE session_id=$session;", ("$session", Id(sessionId)));

    private static bool IsCurrentSessionEligible(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId) =>
        Exists(connection, transaction, CurrentSessionEligibilitySql, ("$session", Id(sessionId)));

    private enum ProposalEvidenceResolution { Related, Missing, OutOfScope }

    private sealed record ProposalEvidenceResult(
        ProposalEvidenceResolution State,
        IReadOnlyList<Guid> SourceSessionIds);

    private static readonly string CurrentSessionEligibilitySql = SessionCurrentUseEligibilitySqlV1.EligibleSessionIdsCte + """
        SELECT 1
        FROM current_session_use_eligibility current_session
        WHERE current_session.session_id=$session
          AND EXISTS(
              SELECT 1 FROM session_native_ids current_native
              WHERE current_native.session_id=current_session.session_id
                AND current_native.binding_kind='native'
          );
        """;

    private static bool IsOneOf(string? value, params string[] values) => value is not null && values.Contains(value, StringComparer.Ordinal);
    private static bool IsBounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool IsUuidVersion7(Guid value) => value != Guid.Empty && value.ToString("D")[14] == '7';

    private static void WriteSession(SqliteConnection connection, SqliteTransaction transaction, ObservedSession value) =>
        Execute(connection, transaction, """
            INSERT INTO sessions(session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,$status,$completeness,$repository,$workspace,$started_at,$ended_at,$last_seen_at,$raw_retention_state,$created_at,$updated_at)
            ON CONFLICT(session_id) DO UPDATE SET
            status=CASE WHEN sessions.status IN ('completed','failed') THEN sessions.status ELSE excluded.status END,
            completeness=CASE
                WHEN CASE sessions.completeness WHEN 'full' THEN 4 WHEN 'rich' THEN 3 WHEN 'partial' THEN 2 ELSE 1 END
                   >= CASE excluded.completeness WHEN 'full' THEN 4 WHEN 'rich' THEN 3 WHEN 'partial' THEN 2 ELSE 1 END
                THEN sessions.completeness ELSE excluded.completeness END,
            repository=COALESCE(sessions.repository,excluded.repository),workspace=COALESCE(sessions.workspace,excluded.workspace),
            started_at=COALESCE(sessions.started_at,excluded.started_at),ended_at=COALESCE(sessions.ended_at,excluded.ended_at),
            last_seen_at=MAX(sessions.last_seen_at,excluded.last_seen_at),raw_retention_state=excluded.raw_retention_state,
            updated_at=MAX(sessions.updated_at,excluded.updated_at);
            """,
            ("$session_id", Id(value.SessionId)), ("$status", SessionWire.ToWire(value.Status)), ("$completeness", SessionWire.ToWire(value.Completeness)),
            ("$repository", value.Repository), ("$workspace", value.Workspace), ("$started_at", Timestamp(value.StartedAt)), ("$ended_at", Timestamp(value.EndedAt)),
            ("$last_seen_at", Timestamp(value.LastSeenAt)), ("$raw_retention_state", SessionWire.ToWire(value.RawRetentionState)),
            ("$created_at", Timestamp(value.CreatedAt)), ("$updated_at", Timestamp(value.UpdatedAt)));

    private static Guid? ReadEventId(SqliteConnection connection, SqliteTransaction transaction, string adapter, string sourceEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_id FROM session_events WHERE source_adapter=$adapter AND source_event_id=$source_event_id;";
        Add(command, "$adapter", adapter);
        Add(command, "$source_event_id", sourceEventId);
        return command.ExecuteScalar() is string value ? Guid.Parse(value) : null;
    }

    private static ImprovementProposal? ReadImprovementProposal(SqliteConnection connection, Guid proposalId) =>
        ReadImprovementProposal(connection, transaction: null, proposalId);

    private static ImprovementProposal? ReadImprovementProposal(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid proposalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT proposal_id,revision,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at,verified_at
            FROM improvement_proposals WHERE proposal_id=$proposal_id;
            """;
        Add(command, "$proposal_id", Id(proposalId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var proposal = new
        {
            ProposalId = Guid.Parse(reader.GetString(0)),
            Revision = reader.GetInt32(1),
            Status = ParseProposalStatus(reader.GetString(2)),
            TargetKind = reader.GetString(3),
            TargetLabel = reader.GetString(4),
            Title = reader.GetString(5),
            Summary = reader.GetString(6),
            ExpectedEffect = reader.GetString(7),
            RiskNote = reader.GetString(8),
            CreatedAt = ParseTimestamp(reader.GetString(9)),
            UpdatedAt = ParseTimestamp(reader.GetString(10)),
            RecommendedAt = NullableTimestamp(reader, 11),
            VerifiedAt = NullableTimestamp(reader, 12),
        };
        reader.Close();
        var sourceSessions = ReadImprovementProposalSourceSessions(connection, transaction, proposalId);
        var evidenceReferences = ReadImprovementProposalEvidenceReferences(connection, transaction, proposalId);
        return new ImprovementProposal(
            proposal.ProposalId, proposal.Revision, proposal.Status, proposal.TargetKind, proposal.TargetLabel, proposal.Title, proposal.Summary,
            proposal.ExpectedEffect, proposal.RiskNote, sourceSessions, evidenceReferences, proposal.CreatedAt, proposal.UpdatedAt,
            proposal.RecommendedAt, proposal.VerifiedAt);
    }

    private static IReadOnlyList<Guid> ReadImprovementProposalSourceSessions(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid proposalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM improvement_proposal_sessions WHERE proposal_id=$proposal_id ORDER BY source_order;";
        Add(command, "$proposal_id", Id(proposalId));
        using var reader = command.ExecuteReader();
        var sourceSessions = new List<Guid>();
        while (reader.Read()) sourceSessions.Add(Guid.Parse(reader.GetString(0)));
        return sourceSessions;
    }

    private static IReadOnlyList<ImprovementProposalEvidenceReference> ReadImprovementProposalEvidenceReferences(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid proposalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind,reference_id FROM improvement_proposal_evidence WHERE proposal_id=$proposal_id ORDER BY evidence_order;";
        Add(command, "$proposal_id", Id(proposalId));
        using var reader = command.ExecuteReader();
        var evidenceReferences = new List<ImprovementProposalEvidenceReference>();
        while (reader.Read()) evidenceReferences.Add(new(reader.GetString(0), reader.GetString(1)));
        return evidenceReferences;
    }

    private static ObservedSession? ReadSession(SqliteConnection connection, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at FROM sessions WHERE session_id=$id;";
        Add(command, "$id", Id(sessionId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static ObservedSession ReadSession(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), SessionWire.ParseStatus(reader.GetString(1)), SessionWire.ParseCompleteness(reader.GetString(2)),
        NullableString(reader, 3), NullableString(reader, 4), NullableTimestamp(reader, 5), NullableTimestamp(reader, 6), ParseTimestamp(reader.GetString(7)),
        SessionWire.ParseRawRetentionState(reader.GetString(8)), ParseTimestamp(reader.GetString(9)), ParseTimestamp(reader.GetString(10)));

    private static string Id(Guid value) => value.ToString("D");
    private static void RejectVerified(ImprovementProposalStatus status)
    {
        if (status == ImprovementProposalStatus.Verified)
        {
            throw new ImprovementProposalStoreException(ImprovementProposalFailure.VerificationOwnedByComparison);
        }
    }

    private static string ProposalStatus(ImprovementProposalStatus status) => status switch
    {
        ImprovementProposalStatus.Candidate => "candidate",
        ImprovementProposalStatus.Recommended => "recommended",
        ImprovementProposalStatus.Verified => "verified",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static ImprovementProposalStatus ParseProposalStatus(string status) => status switch
    {
        "candidate" => ImprovementProposalStatus.Candidate,
        "recommended" => ImprovementProposalStatus.Recommended,
        "verified" => ImprovementProposalStatus.Verified,
        _ => throw new InvalidOperationException("Unsupported improvement proposal status."),
    };
    private static string Timestamp(DateTimeOffset value) => value.ToString("O");
    private static string? Timestamp(DateTimeOffset? value) => value?.ToString("O");
    private static string CanonicalEventTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? MatchKind(SessionMatchKind? value) => value switch
    {
        null => null,
        SessionMatchKind.ExactNative => "exact_native",
        SessionMatchKind.ExplicitLink => "explicit_link",
        SessionMatchKind.TraceContinuity => "trace_continuity",
        SessionMatchKind.ConversationId => "conversation_id",
        SessionMatchKind.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SessionMatchKind? ParseMatchKind(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : reader.GetString(ordinal) switch
        {
            "exact_native" => SessionMatchKind.ExactNative,
            "explicit_link" => SessionMatchKind.ExplicitLink,
            "trace_continuity" => SessionMatchKind.TraceContinuity,
            "conversation_id" => SessionMatchKind.ConversationId,
            "none" => SessionMatchKind.None,
            _ => throw new InvalidOperationException("Unsupported Session event match kind."),
        };
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? NullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static SessionSourceSurface? NullableSurface(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : SessionWire.ParseSourceSurface(reader.GetString(ordinal));

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private SqliteConnection Open(bool enforceForeignKeys = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = Math.Max(1, checked((busyTimeoutMilliseconds + 999) / 1000)),
        }.ToString());
        connection.Open();
        Execute(connection, enforceForeignKeys ? "PRAGMA foreign_keys=ON;" : "PRAGMA foreign_keys=OFF;");
        Execute(connection, $"PRAGMA busy_timeout={busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
        if (statementObserver is not null)
            SQLitePCL.raw.sqlite3_trace(connection.Handle, statementObserver, null);
        return connection;
    }

    private static int? ReadSessionSchemaVersion(SqliteConnection connection)
    {
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='schema_version';";
        if (table.ExecuteScalar() is null) return null;
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_version WHERE component='session';";
        var value = version.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    internal static void ValidateSchemaBeforeInitialization(SqliteConnection connection)
    {
        var preflightVersion = ReadSessionSchemaVersion(connection);
        if (preflightVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidOperationException("Unsupported Session schema version.");
        ValidateExistingSchemaBeforeInitialization(connection, preflightVersion);
    }

    internal static bool IsCurrentSchemaValid(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        try
        {
            return SessionSchemaV11Validator.IsValid(
                    connection, transaction, CreateCanonicalVersionTwelveSchema, CurrentSchemaVersion)
                && ValidateCurrentSessionRows(connection, transaction);
        }
        catch (SqliteException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static void ValidateExistingSchemaBeforeInitialization(SqliteConnection connection, int? version)
    {
        if (version is null) return;
        if (version <= VersionTenSchemaVersion)
        {
            if (HasUnexpectedVersionTenColumns(connection, null))
                throw new InvalidOperationException("Unsupported incomplete Session schema version 10.");
            foreach (var column in VersionElevenProvenanceColumns)
                if (Columns(connection, null, "session_events").Contains(column)
                    && !IsNullableTextColumn(connection, null, "session_events", column))
                    throw new InvalidOperationException($"Invalid session_events.{column} migration column.");
        }
        else if (version == VersionElevenSchemaVersion)
        {
            SessionSchemaV11Validator.Validate(
                connection,
                CreateCanonicalVersionTwelveSchema,
                VersionElevenSchemaVersion);
        }
        else if (version == VersionThirteenSchemaVersion)
        {
            if (!SessionSchemaV11Validator.IsCurrentV13SchemaValidSelectOnly(connection, null))
                throw new InvalidOperationException("Unsupported incomplete Session schema version 13.");
        }
        else if (version == CurrentSchemaVersion && !IsCurrentSchemaValid(connection, null))
        {
            throw new InvalidOperationException("Unsupported incomplete Session schema version 14.");
        }
        EnsureForeignKeysValid(connection, null);
    }

    private static void CreateCanonicalVersionTwelveSchema(SqliteConnection connection)
    {
        Execute(connection, SchemaVersionSql);
        Execute(connection, SchemaSql);
        Execute(connection, HumanEvaluationSchemaSql);
        Execute(connection, ImprovementProposalSchemaSql);
        Execute(connection, ProposalApplySchemaSql);
        Execute(connection, ObjectiveEvaluationSchemaSql);
        Execute(connection, EffectComparisonSchemaSql);
        Execute(connection, $"INSERT INTO schema_version(component,version) VALUES('session',{CurrentSchemaVersion});");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition)
    {
        using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$column;";
        Add(columns, "$column", column);
        if (columns.ExecuteScalar() is not null) return;
        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static void AddProposalRevisionColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumnIfMissing(connection, transaction, "improvement_proposals", "revision", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(connection, transaction, "improvement_proposal_sessions", "proposal_revision", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(connection, transaction, "proposal_apply_drafts", "proposal_revision", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(connection, transaction, "proposal_applies", "proposal_revision", "INTEGER NOT NULL DEFAULT 1");
    }

    private static void MigrateToVersionEleven(SqliteConnection connection, SqliteTransaction transaction, SqliteCommand command)
    {
        AddNullableTextColumnForMigration(connection, transaction, "session_events", "source_application_version");
        AddNullableTextColumnForMigration(connection, transaction, "session_events", "adapter_version");
        AddNullableTextColumnForMigration(connection, transaction, "session_events", "schema_fingerprint");
        AddNullableTextColumnForMigration(connection, transaction, "session_events", "normalization_version");
        Execute(connection, transaction, """
            CREATE TEMP TABLE session_native_ids_v10 AS SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids;
            CREATE TEMP TABLE session_runs_v10 AS SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status FROM session_runs;
            CREATE TEMP TABLE session_events_v10 AS SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version FROM session_events;
            DROP TABLE session_events;
            DROP TABLE session_runs;
            DROP TABLE session_native_ids;
            """);
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        Execute(connection, transaction, """
            INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at) SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids_v10;
            INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status) SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status FROM session_runs_v10;
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version) SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version FROM session_events_v10;
            DROP TABLE session_events_v10;
            DROP TABLE session_runs_v10;
            DROP TABLE session_native_ids_v10;
            """);
    }

    private static void MigrateToVersionTwelve(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddMatchKindColumnForMigration(connection, transaction);
    }

    internal static void MigrateToVersionThirteen(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!TableExists(connection, transaction, "session_event_content"))
        {
            return;
        }
        if (ColumnExists(connection, transaction, "session_event_content", "retention_owner_token"))
        {
            EnsureSessionContentOwnerTokenTrigger(connection, transaction);
            return;
        }

        Execute(connection, transaction, """
            CREATE TABLE session_event_content_v13 (
                event_id TEXT PRIMARY KEY,
                content_kind TEXT NOT NULL,
                content_json TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                retention_owner_token BLOB NOT NULL CHECK(typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32),
                FOREIGN KEY (event_id) REFERENCES session_events(event_id) ON DELETE CASCADE
            );
            INSERT INTO session_event_content_v13(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
            SELECT event_id,content_kind,content_json,captured_at,expires_at,randomblob(32) FROM session_event_content;
            DROP TABLE session_event_content;
            ALTER TABLE session_event_content_v13 RENAME TO session_event_content;
            """);
        EnsureSessionContentOwnerTokenTrigger(connection, transaction);
    }

    private static void MigrateToVersionFourteen(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset migrationNow)
    {
        if (!ColumnExists(connection, transaction, "session_events", "terminal_outcome"))
        {
            Execute(connection, transaction, "PRAGMA legacy_alter_table=ON;");
            try
            {
                Execute(connection, transaction, """
                ALTER TABLE session_events RENAME TO session_events_v13;
                CREATE TABLE session_events (
                    event_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    run_id TEXT NULL,
                    source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
                    parent_event_id TEXT NULL,
                    trace_id TEXT NULL,
                    status TEXT NULL,
                    source_adapter TEXT NOT NULL,
                    source_event_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
                    source_application_version TEXT NULL,
                    adapter_version TEXT NULL,
                    schema_fingerprint TEXT NULL,
                    normalization_version TEXT NULL,
                    match_kind TEXT NULL CHECK (match_kind IS NULL OR match_kind IN ('exact_native','explicit_link','trace_continuity','conversation_id','none')),
                    terminal_outcome TEXT NULL,
                    terminal_policy_version INTEGER NULL,
                    CHECK (
                        (terminal_outcome IS NULL AND terminal_policy_version IS NULL)
                        OR
                        (
                            typeof(terminal_outcome) = 'text'
                            AND terminal_outcome IN ('clean', 'failed', 'neutral')
                            AND typeof(terminal_policy_version) = 'integer'
                            AND terminal_policy_version = 1
                        )
                    ),
                    UNIQUE (source_adapter, source_event_id),
                    UNIQUE (session_id, event_id),
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
                    FOREIGN KEY (session_id, run_id) REFERENCES session_runs(session_id, run_id),
                    FOREIGN KEY (session_id, parent_event_id) REFERENCES session_events(session_id, event_id)
                );
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                    occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind,
                    terminal_outcome,terminal_policy_version)
                SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                    occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind,
                    NULL,NULL
                FROM session_events_v13;
                DROP TABLE session_events_v13;
                """);
            }
            finally
            {
                Execute(connection, transaction, "PRAGMA legacy_alter_table=OFF;");
            }
        }

        var events = new List<VersionThirteenMigrationEvent>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state FROM session_events ORDER BY event_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableGuid(reader, 2),
                    reader.IsDBNull(3) ? null : SessionWire.ParseSourceSurface(reader.GetString(3)),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
            }
        }

        foreach (var item in events)
        {
            SessionEventContent? content = null;
            if (RequiresMigrationDiscriminatorContent(item))
            {
                var authorized = RetentionCatalogStore.ReadAuthorizedSessionEventContentForMigration(
                    connection,
                    transaction,
                    Id(item.EventId),
                    Id(item.SessionId),
                    item.RunId is null ? null : Id(item.RunId.Value),
                    item.SourceAdapter,
                    item.SourceEventId,
                    item.ContentState,
                    migrationNow);
                if (authorized is not null)
                    content = new(item.EventId, authorized.ContentKind, authorized.ContentJson, authorized.CapturedAt, authorized.ExpiresAt);
            }
            var outcome = ClassifyMigrationTerminalOutcome(
                item,
                content);
            if (outcome is not null
                && (!DateTimeOffset.TryParseExact(
                        item.OccurredAt,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var occurredAt)
                    || !string.Equals(item.OccurredAt, CanonicalEventTimestamp(occurredAt), StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Invalid Session terminal occurred_at during migration.");
            }
            Execute(connection, transaction,
                "UPDATE session_events SET terminal_outcome=$outcome,terminal_policy_version=$policy WHERE event_id=$event;",
                ("$outcome", outcome), ("$policy", outcome is null ? null : 1), ("$event", Id(item.EventId)));
        }

        var sessions = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(Guid.Parse(reader.GetString(0)));
        }
        foreach (var sessionId in sessions) ReduceSessionOutcomeAndCompleteness(connection, transaction, sessionId);
    }

    private static bool RequiresMigrationDiscriminatorContent(VersionThirteenMigrationEvent item) =>
        item.SourceAdapter == "copilot-sdk-stream"
            && item.SourceSurface == SessionSourceSurface.CopilotSdk
            && item.Type == "session.shutdown"
        || item.SourceAdapter == "copilot-compatible-hook"
            && item.SourceSurface is SessionSourceSurface.CopilotCli or SessionSourceSurface.VisualStudioCode or SessionSourceSurface.HookUnknown
            && item.Type == "SessionEnd"
        || item.SourceAdapter == "claude-code-hook"
            && item.SourceSurface == SessionSourceSurface.ClaudeCode
            && item.Type == "SessionEnd";

    private static void EnsureSessionContentOwnerTokenTrigger(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, "CREATE TRIGGER IF NOT EXISTS retention_session_event_content_token_immutable BEFORE UPDATE OF retention_owner_token ON session_event_content WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$column;";
        Add(command, "$column", column);
        return command.ExecuteScalar() is not null;
    }

    private static SessionContentRegistration ReadSessionContentForRegistration(SqliteConnection connection, SqliteTransaction transaction, Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.event_id,c.content_kind,c.content_json COLLATE BINARY,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token
            FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id
            WHERE c.event_id=$event_id;
            """;
        Add(command, "$event_id", Id(eventId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetFieldValue<byte[]>(9) is not { Length: 32 } token)
            throw new InvalidOperationException("Session content capture conflict.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7), reader.GetString(8), token);
    }

    private sealed record SessionContentRegistration(string EventId, string ContentKind, string ContentJson,
        DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt, string SessionId, string? RunId, string SourceAdapter,
        string SourceEventId, byte[] OwnerToken);

    private sealed record VersionThirteenMigrationEvent(
        Guid EventId,
        Guid SessionId,
        Guid? RunId,
        SessionSourceSurface? SourceSurface,
        string SourceAdapter,
        string SourceEventId,
        string Type,
        string OccurredAt,
        string ContentState);

    private static void AddMatchKindColumnForMigration(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,\"notnull\",dflt_value FROM pragma_table_info('session_events') WHERE name='match_kind';";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            reader.Close();
            Execute(connection, transaction, "ALTER TABLE session_events ADD COLUMN match_kind TEXT NULL CHECK (match_kind IS NULL OR match_kind IN ('exact_native','explicit_link','trace_continuity','conversation_id','none'));");
            return;
        }
        if (!string.Equals(reader.GetString(0), "TEXT", StringComparison.OrdinalIgnoreCase)
            || reader.GetInt32(1) != 0
            || !reader.IsDBNull(2))
            throw new InvalidOperationException("Invalid session_events.match_kind migration column.");
    }

    private static void AddNullableTextColumnForMigration(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT type,\"notnull\",dflt_value FROM pragma_table_info('{table}') WHERE name=$column;";
        Add(command, "$column", column);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            reader.Close();
            Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;");
            return;
        }
        if (!string.Equals(reader.GetString(0), "TEXT", StringComparison.OrdinalIgnoreCase)
            || reader.GetInt32(1) != 0
            || !reader.IsDBNull(2))
            throw new InvalidOperationException($"Invalid {table}.{column} migration column.");
    }

    private static bool IsNullableTextColumn(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT type,\"notnull\",dflt_value FROM pragma_table_info('{table}') WHERE name=$column;";
        Add(command, "$column", column);
        using var reader = command.ExecuteReader();
        return reader.Read()
            && string.Equals(reader.GetString(0), "TEXT", StringComparison.OrdinalIgnoreCase)
            && reader.GetInt32(1) == 0
            && reader.IsDBNull(2);
    }

    private static void EnsureForeignKeysValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException("Session schema migration produced invalid foreign keys.");
    }

    private static void RepairKnownStampedVersionTenShape(SqliteConnection connection, SqliteTransaction transaction, SqliteCommand command)
    {
        if (HasUnexpectedVersionTenColumns(connection, transaction))
            throw new InvalidOperationException("Unsupported incomplete Session schema version 10.");
        var revisionColumns = new HashSet<(string Table, string Column)>
        {
            ("improvement_proposals", "revision"),
            ("improvement_proposal_sessions", "proposal_revision"),
            ("proposal_apply_drafts", "proposal_revision"),
            ("proposal_applies", "proposal_revision"),
        };
        var v4Missing = revisionColumns.Append(("proposal_apply_pending", "__table__")).ToHashSet();
        if (MatchesVersionTenShape(connection, transaction, v4Missing))
        {
            command.CommandText = ProposalApplyPendingSchemaSql;
            command.ExecuteNonQuery();
            AddProposalRevisionColumns(connection, transaction);
            return;
        }
        if (MatchesVersionTenShape(connection, transaction, revisionColumns))
        {
            AddProposalRevisionColumns(connection, transaction);
            return;
        }
        var v6Missing = new HashSet<(string Table, string Column)> { ("improvement_proposal_sessions", "proposal_revision") };
        if (MatchesVersionTenShape(connection, transaction, v6Missing))
        {
            AddProposalRevisionColumns(connection, transaction);
            return;
        }
        if (!MatchesVersionTenShape(connection, transaction, new HashSet<(string Table, string Column)>()))
            throw new InvalidOperationException("Unsupported incomplete Session schema version 10.");
    }

    private static bool HasUnexpectedVersionTenColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        foreach (var (table, requiredColumns) in VersionTenRequiredColumns)
        {
            if (!TableExists(connection, transaction, table)) continue;
            var allowed = requiredColumns.ToHashSet(StringComparer.Ordinal);
            if (table == "session_events")
                allowed.UnionWith(["source_application_version", "adapter_version", "schema_fingerprint", "normalization_version", "match_kind"]);
            if (table == "session_event_content")
                allowed.Add("retention_owner_token");
            if (Columns(connection, transaction, table).Except(allowed).Any()) return true;
        }
        return false;
    }

    private static bool MatchesVersionTenShape(SqliteConnection connection, SqliteTransaction transaction, IReadOnlySet<(string Table, string Column)> missing)
    {
        foreach (var (table, columns) in VersionTenRequiredColumns)
        {
            if (missing.Contains((table, "__table__")))
            {
                if (TableExists(connection, transaction, table)) return false;
                continue;
            }
            if (!TableExists(connection, transaction, table)) return false;
            var actual = Columns(connection, transaction, table);
            foreach (var column in columns)
                if (actual.Contains(column) == missing.Contains((table, column))) return false;
        }
        return true;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table;";
        Add(command, "$table", table); return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> Columns(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader(); var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) columns.Add(reader.GetString(0)); return columns;
    }

    private static readonly IReadOnlyDictionary<string, string[]> VersionTenRequiredColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["schema_version"] = ["component", "version"],
        ["sessions"] = ["session_id", "status", "completeness", "repository", "workspace", "started_at", "ended_at", "last_seen_at", "raw_retention_state", "created_at", "updated_at"],
        ["session_native_ids"] = ["session_id", "source_surface", "native_session_id", "binding_kind", "observed_at"],
        ["session_runs"] = ["run_id", "session_id", "source_surface", "native_run_id", "trace_id", "parent_run_id", "model", "started_at", "ended_at", "input_tokens", "output_tokens", "total_tokens", "status"],
        ["session_events"] = ["event_id", "session_id", "run_id", "source_surface", "parent_event_id", "trace_id", "status", "source_adapter", "source_event_id", "type", "occurred_at", "content_state"],
        ["session_event_content"] = ["event_id", "content_kind", "content_json", "captured_at", "expires_at"],
        ["session_projection_state"] = ["projector_key", "projection_cursor", "unsupported_event_version_count", "updated_at"],
        ["session_human_evaluation"] = ["session_id", "verdict", "recorded_at"],
        ["improvement_proposals"] = ["proposal_id", "revision", "status", "target_kind", "target_label", "title", "summary", "expected_effect", "risk_note", "created_at", "updated_at", "recommended_at", "verified_at"],
        ["improvement_proposal_sessions"] = ["proposal_id", "proposal_revision", "session_id", "source_order"],
        ["improvement_proposal_evidence"] = ["proposal_id", "evidence_order", "kind", "reference_id"],
        ["proposal_apply_drafts"] = ["draft_id", "proposal_id", "proposal_revision", "root_id", "selection_revision", "approval_digest", "state", "created_at", "updated_at"],
        ["proposal_apply_files"] = ["draft_id", "file_order", "base_sha256", "replacement_sha256"],
        ["proposal_apply_hunks"] = ["draft_id", "hunk_id", "selected", "replacement_sha256"],
        ["proposal_apply_revisions"] = ["draft_id", "selection_revision", "approval_digest", "approved_at"],
        ["proposal_applies"] = ["apply_id", "draft_id", "proposal_revision", "state", "created_at"],
        ["proposal_apply_audit"] = ["audit_id", "apply_id", "draft_id", "proposal_id", "root_id", "actor_kind", "state", "error_code", "file_count", "recorded_at"],
        ["proposal_apply_pending"] = ["apply_id", "draft_id", "proposal_id", "root_id", "actor_kind", "file_count", "operation_kind", "recorded_at"],
        ["objective_evaluations"] = ["objective_evaluation_id", "session_id", "run_id", "trace_id", "result", "severity", "evaluator_id", "evaluator_version", "criterion_id", "case_key", "recorded_at"],
        ["objective_evaluation_evidence"] = ["objective_evaluation_id", "evidence_order", "kind", "reference_id"],
        ["effect_comparisons"] = ["comparison_id", "cohort_revision", "proposal_id", "proposal_revision", "apply_id", "recorded_at"],
        ["effect_comparison_sessions"] = ["comparison_id", "session_id", "classification", "case_key", "exclusion_reason", "session_order", "effective_quality", "severe_failure"],
        ["effect_comparison_evidence"] = ["comparison_id", "evidence_order", "session_id", "kind", "reference_id", "recorded_at", "human_verdict"],
        ["effect_receipts"] = ["comparison_id", "verdict", "result_json", "recorded_at"],
    };

    private static void ValidateComparisonRequest(EffectComparisonRequest request)
    {
        if (request.ProposalId == Guid.Empty || request.ApplyId == Guid.Empty || request.ProposalRevision < 1 || request.Sessions is not { Count: > 0 }) throw new ArgumentException("Invalid comparison request.", nameof(request));
        if (request.Sessions.Any(item => item is null || item.SessionId == Guid.Empty) || request.Sessions.Select(item => item.SessionId).Distinct().Count() != request.Sessions.Count) throw new ArgumentException("A session can have one classification.", nameof(request));
        foreach (var item in request.Sessions)
        {
            if (item.Classification is "pre" or "post")
            {
                if (!ObjectiveEvaluationValidation.IdentifierValue(item.CaseKey, 200) || item.ExclusionReason is not null) throw new ArgumentException("Invalid included cohort session.", nameof(request));
            }
            else if (item.Classification == "excluded")
            {
                if (!string.IsNullOrEmpty(item.CaseKey) || item.ExclusionReason is not ("not_comparable" or "wrong_case" or "missing_evidence" or "overlaps_application" or "user_excluded")) throw new ArgumentException("Invalid excluded cohort session.", nameof(request));
            }
            else throw new ArgumentException("Invalid cohort classification.", nameof(request));
        }
    }

    private static bool IsCurrentEffectApplicationDatabase(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid proposalId,
        int proposalRevision,
        Guid applyId) =>
        ReadCurrentEffectApplication(connection, transaction, proposalId, applyId) is { } application
        && application.Receipt.ProposalRevision == proposalRevision;

    private static EffectCurrentApplicationSnapshot? ReadCurrentEffectApplication(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid proposalId,
        Guid applyId)
    {
        Guid draftId;
        int proposalRevision;
        int selectionRevision;
        DateTimeOffset appliedAt;
        int fileCount;
        Guid rootId;
        string approvalDigest;
        ProposalApplyState draftState;
        DateTimeOffset draftCreatedAt;
        DateTimeOffset draftUpdatedAt;
        string revisionDigest;
        DateTimeOffset approvedAt;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT application.draft_id,application.proposal_revision,draft.selection_revision,
                       application.created_at,
                       (SELECT COUNT(*) FROM proposal_apply_files file WHERE file.draft_id=draft.draft_id),
                       draft.root_id,draft.approval_digest,draft.state,draft.created_at,draft.updated_at,
                       revision.approval_digest,revision.approved_at
                FROM proposal_applies application
                JOIN proposal_apply_drafts draft ON draft.draft_id=application.draft_id
                JOIN improvement_proposals proposal ON proposal.proposal_id=draft.proposal_id
                JOIN proposal_apply_revisions revision
                  ON revision.draft_id=draft.draft_id
                 AND revision.selection_revision=draft.selection_revision
                WHERE application.apply_id=$apply
                  AND draft.proposal_id=$proposal
                  AND application.state='applied'
                  AND application.proposal_revision=draft.proposal_revision
                  AND proposal.revision=application.proposal_revision
                  AND revision.approved_at IS NOT NULL
                  AND NOT EXISTS(
                      SELECT 1 FROM proposal_apply_pending pending
                      WHERE pending.apply_id=application.apply_id
                  );
                """;
            Add(command, "$apply", Id(applyId));
            Add(command, "$proposal", Id(proposalId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            draftId = Guid.Parse(reader.GetString(0));
            proposalRevision = reader.GetInt32(1);
            selectionRevision = reader.GetInt32(2);
            appliedAt = ParseTimestamp(reader.GetString(3));
            fileCount = reader.GetInt32(4);
            rootId = Guid.Parse(reader.GetString(5));
            approvalDigest = reader.GetString(6);
            draftState = ParseApplyState(reader.GetString(7));
            draftCreatedAt = ParseTimestamp(reader.GetString(8));
            draftUpdatedAt = ParseTimestamp(reader.GetString(9));
            revisionDigest = reader.GetString(10);
            approvedAt = ParseTimestamp(reader.GetString(11));
        }

        if (!string.Equals(approvalDigest, revisionDigest, StringComparison.Ordinal)) return null;
        var files = new List<(string BaseSha256, string ReplacementSha256)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT base_sha256,replacement_sha256 FROM proposal_apply_files WHERE draft_id=$draft ORDER BY file_order;";
            Add(command, "$draft", Id(draftId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) files.Add((reader.GetString(0), reader.GetString(1)));
        }
        if (files.Count != fileCount) return null;

        var hunks = new List<(string HunkId, bool Selected, string ReplacementSha256)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT hunk_id,selected,replacement_sha256 FROM proposal_apply_hunks WHERE draft_id=$draft ORDER BY hunk_id;";
            Add(command, "$draft", Id(draftId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) hunks.Add((reader.GetString(0), reader.GetInt32(1) != 0, reader.GetString(2)));
        }

        var receipt = new ProposalApplicationReceipt(
            applyId, draftId, proposalId, proposalRevision, selectionRevision,
            appliedAt, fileCount, "applied", "active");
        var draft = new ProposalApplyDraftMetadata(
            draftId, proposalId, proposalRevision, rootId, selectionRevision,
            approvalDigest, draftState, fileCount, draftCreatedAt, draftUpdatedAt);
        var revision = new ProposalApplyRevisionMetadata(
            draftId, selectionRevision, revisionDigest, approvedAt);
        return new(receipt, new(draft, revision, files, hunks));
    }

    private static bool AreEffectSessionsCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid comparisonId)
    {
        var sessionIds = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id FROM effect_comparison_sessions WHERE comparison_id=$comparison AND classification IN ('pre','post');";
            Add(command, "$comparison", Id(comparisonId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessionIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return sessionIds.All(sessionId => IsCurrentSessionEligible(connection, transaction, sessionId));
    }

    private static (DateTimeOffset AppliedAt, Guid DraftId)? ReadActiveApply(SqliteConnection connection, SqliteTransaction transaction, EffectComparisonRequest request)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT a.created_at,a.draft_id FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE a.apply_id=$apply AND d.proposal_id=$proposal AND a.proposal_revision=$revision AND a.state='applied' AND NOT EXISTS(SELECT 1 FROM proposal_apply_pending p WHERE p.apply_id=a.apply_id);";
        Add(command, "$apply", Id(request.ApplyId)); Add(command, "$proposal", Id(request.ProposalId)); Add(command, "$revision", request.ProposalRevision);
        using var reader = command.ExecuteReader(); return reader.Read() ? (ParseTimestamp(reader.GetString(0)), Guid.Parse(reader.GetString(1))) : null;
    }

    private static (DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, bool CurrentEligible)? ReadComparisonSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT started_at,ended_at FROM sessions WHERE session_id=$session;";
        Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        DateTimeOffset? startedAt = reader.IsDBNull(0) ? null : ParseTimestamp(reader.GetString(0));
        DateTimeOffset? endedAt = reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1));
        reader.Close();
        return (startedAt, endedAt, IsCurrentSessionEligible(connection, transaction, sessionId));
    }

    private static bool IsComparable((DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, bool CurrentEligible) session) =>
        session.CurrentEligible
        && session.StartedAt is not null
        && session.EndedAt is not null
        && session.EndedAt >= session.StartedAt;

    private static IReadOnlyList<ObjectiveEvaluationReceipt> ExactObjectives(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at FROM objective_evaluations WHERE session_id=$session ORDER BY recorded_at,objective_evaluation_id;";
        Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader(); var result = new List<ObjectiveEvaluationReceipt>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4) == "pass" ? ObjectiveResult.Pass : ObjectiveResult.Fail, reader.GetString(5) == "normal" ? ObjectiveSeverity.Normal : ObjectiveSeverity.Severe, reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), [], ParseTimestamp(reader.GetString(10))));
        reader.Close();
        return result
            .Select(receipt => receipt with { Evidence = Evidence(connection, transaction, receipt.ObjectiveEvaluationId) })
            .Where(receipt => ObjectiveEvaluationValidation.IsValid(receipt) && ExactReceiptReferenceScope(connection, transaction, receipt))
            .ToArray();
    }

    private static long? SessionTokens(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT total_tokens FROM session_runs WHERE session_id=$session;"; Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader(); long total = 0; var found = false;
        while (reader.Read()) { found = true; if (reader.IsDBNull(0)) return null; total += reader.GetInt64(0); }
        return found ? total : null;
    }

    private static int NextCohortRevision(SqliteConnection connection, SqliteTransaction transaction, Guid proposalId, Guid applyId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT COALESCE(MAX(cohort_revision),0)+1 FROM effect_comparisons WHERE proposal_id=$proposal AND apply_id=$apply;"; Add(command, "$proposal", Id(proposalId)); Add(command, "$apply", Id(applyId)); return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string VerdictText(EffectVerdict verdict) => verdict switch { EffectVerdict.Improved => "improved", EffectVerdict.NoChange => "no_change", EffectVerdict.Regressed => "regressed", _ => "insufficient_evidence" };

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
            session_id TEXT PRIMARY KEY,
            status TEXT NOT NULL CHECK (status IN ('active','completed','failed','unknown')),
            completeness TEXT NOT NULL CHECK (completeness IN ('unbound','partial','rich','full')),
            repository TEXT NULL,
            workspace TEXT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            last_seen_at TEXT NOT NULL,
            raw_retention_state TEXT NOT NULL CHECK (raw_retention_state IN ('expiring','expired_pending_deletion','not_captured')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_native_ids (
            session_id TEXT NOT NULL,
            source_surface TEXT NOT NULL CHECK (source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
            native_session_id TEXT NOT NULL,
            binding_kind TEXT NOT NULL CHECK (binding_kind IN ('native','explicit_resume','explicit_handoff','trace_context')),
            observed_at TEXT NOT NULL,
            PRIMARY KEY (source_surface, native_session_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS session_runs (
            run_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
            native_run_id TEXT NULL,
            trace_id TEXT NULL,
            parent_run_id TEXT NULL,
            model TEXT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            input_tokens INTEGER NULL CHECK (input_tokens IS NULL OR input_tokens >= 0),
            output_tokens INTEGER NULL CHECK (output_tokens IS NULL OR output_tokens >= 0),
            total_tokens INTEGER NULL CHECK (total_tokens IS NULL OR total_tokens >= 0),
            status TEXT NOT NULL CHECK (status IN ('active','completed','failed','unknown')),
            UNIQUE (session_id, run_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
            FOREIGN KEY (session_id, parent_run_id) REFERENCES session_runs(session_id, run_id)
        );

        CREATE TABLE IF NOT EXISTS session_events (
            event_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            run_id TEXT NULL,
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
            parent_event_id TEXT NULL,
            trace_id TEXT NULL,
            status TEXT NULL,
            source_adapter TEXT NOT NULL,
            source_event_id TEXT NOT NULL,
            type TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
            source_application_version TEXT NULL,
            adapter_version TEXT NULL,
            schema_fingerprint TEXT NULL,
            normalization_version TEXT NULL,
            match_kind TEXT NULL CHECK (match_kind IS NULL OR match_kind IN ('exact_native','explicit_link','trace_continuity','conversation_id','none')),
            terminal_outcome TEXT NULL,
            terminal_policy_version INTEGER NULL,
            CHECK (
                (terminal_outcome IS NULL AND terminal_policy_version IS NULL)
                OR
                (
                    typeof(terminal_outcome) = 'text'
                    AND terminal_outcome IN ('clean', 'failed', 'neutral')
                    AND typeof(terminal_policy_version) = 'integer'
                    AND terminal_policy_version = 1
                )
            ),
            UNIQUE (source_adapter, source_event_id),
            UNIQUE (session_id, event_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
            FOREIGN KEY (session_id, run_id) REFERENCES session_runs(session_id, run_id),
            FOREIGN KEY (session_id, parent_event_id) REFERENCES session_events(session_id, event_id)
        );

        CREATE TABLE IF NOT EXISTS session_event_content (
            event_id TEXT PRIMARY KEY,
            content_kind TEXT NOT NULL,
            content_json TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            retention_owner_token BLOB NOT NULL CHECK(typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32),
            FOREIGN KEY (event_id) REFERENCES session_events(event_id) ON DELETE CASCADE
        );

        CREATE TRIGGER IF NOT EXISTS retention_session_event_content_token_immutable
        BEFORE UPDATE OF retention_owner_token ON session_event_content
        WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token
        BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;

        CREATE TABLE IF NOT EXISTS session_projection_state (
            projector_key TEXT PRIMARY KEY,
            projection_cursor INTEGER NULL CHECK (projection_cursor IS NULL OR projection_cursor >= 0),
            unsupported_event_version_count INTEGER NOT NULL CHECK (unsupported_event_version_count >= 0),
            updated_at TEXT NOT NULL
        );
        """;

    private const string HumanEvaluationSchemaSql = """
        CREATE TABLE IF NOT EXISTS session_human_evaluation (
            session_id TEXT PRIMARY KEY,
            verdict TEXT NOT NULL CHECK (verdict IN ('expected','problem')),
            recorded_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );
        """;

    private const string ObjectiveEvaluationSchemaSql = """
        CREATE TABLE IF NOT EXISTS objective_evaluations (
            objective_evaluation_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            trace_id TEXT NOT NULL,
            result TEXT NOT NULL CHECK (result IN ('pass','fail')),
            severity TEXT NOT NULL CHECK (severity IN ('normal','severe')),
            evaluator_id TEXT NOT NULL,
            evaluator_version TEXT NOT NULL,
            criterion_id TEXT NOT NULL,
            case_key TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT,
            FOREIGN KEY (run_id) REFERENCES session_runs(run_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS objective_evaluation_evidence (
            objective_evaluation_id TEXT NOT NULL,
            evidence_order INTEGER NOT NULL CHECK (evidence_order >= 0),
            kind TEXT NOT NULL CHECK (kind IN ('run','event','trace','gate')),
            reference_id TEXT NOT NULL,
            PRIMARY KEY (objective_evaluation_id,evidence_order),
            FOREIGN KEY (objective_evaluation_id) REFERENCES objective_evaluations(objective_evaluation_id) ON DELETE RESTRICT
        );
        """;

    private const string EffectComparisonSchemaSql = """
        CREATE TABLE IF NOT EXISTS effect_comparisons (
            comparison_id TEXT PRIMARY KEY,
            cohort_revision INTEGER NOT NULL CHECK (cohort_revision > 0),
            proposal_id TEXT NOT NULL,
            proposal_revision INTEGER NOT NULL CHECK (proposal_revision > 0),
            apply_id TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            UNIQUE(proposal_id,apply_id,cohort_revision),
            FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(proposal_id) ON DELETE RESTRICT,
            FOREIGN KEY (apply_id) REFERENCES proposal_applies(apply_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS effect_comparison_sessions (
            comparison_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            classification TEXT NOT NULL CHECK (classification IN ('pre','post','excluded')),
            case_key TEXT NOT NULL,
            exclusion_reason TEXT NULL CHECK (exclusion_reason IS NULL OR exclusion_reason IN ('not_comparable','wrong_case','missing_evidence','overlaps_application','user_excluded')),
            session_order INTEGER NOT NULL CHECK (session_order >= 0),
            effective_quality TEXT NULL CHECK (effective_quality IS NULL OR effective_quality IN ('pass','fail','missing')),
            severe_failure INTEGER NOT NULL DEFAULT 0 CHECK (severe_failure IN (0,1)),
            PRIMARY KEY(comparison_id,session_id),
            UNIQUE(comparison_id,session_order),
            FOREIGN KEY(comparison_id) REFERENCES effect_comparisons(comparison_id) ON DELETE RESTRICT,
            FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS effect_comparison_evidence (
            comparison_id TEXT NOT NULL,
            evidence_order INTEGER NOT NULL CHECK (evidence_order >= 0),
            session_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            reference_id TEXT NOT NULL,
            recorded_at TEXT NULL,
            human_verdict TEXT NULL CHECK (human_verdict IS NULL OR human_verdict IN ('expected','problem')),
            PRIMARY KEY(comparison_id,evidence_order),
            FOREIGN KEY(comparison_id) REFERENCES effect_comparisons(comparison_id) ON DELETE RESTRICT,
            FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS effect_receipts (
            comparison_id TEXT PRIMARY KEY,
            verdict TEXT NOT NULL CHECK (verdict IN ('improved','no_change','regressed','insufficient_evidence')),
            result_json TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            FOREIGN KEY(comparison_id) REFERENCES effect_comparisons(comparison_id) ON DELETE RESTRICT
        );
        """;

    private const string ImprovementProposalSchemaSql = """
        CREATE TABLE IF NOT EXISTS improvement_proposals (
            proposal_id TEXT PRIMARY KEY,
            revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
            status TEXT NOT NULL CHECK (status IN ('candidate','recommended','verified')),
            target_kind TEXT NOT NULL,
            target_label TEXT NOT NULL,
            title TEXT NOT NULL,
            summary TEXT NOT NULL,
            expected_effect TEXT NOT NULL,
            risk_note TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            recommended_at TEXT NULL,
            verified_at TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS improvement_proposal_sessions (
            proposal_id TEXT NOT NULL,
            proposal_revision INTEGER NOT NULL DEFAULT 1 CHECK (proposal_revision > 0),
            session_id TEXT NOT NULL,
            source_order INTEGER NOT NULL CHECK (source_order >= 0),
            PRIMARY KEY (proposal_id, session_id),
            UNIQUE (proposal_id, source_order),
            FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(proposal_id) ON DELETE CASCADE,
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS improvement_proposal_evidence (
            proposal_id TEXT NOT NULL,
            evidence_order INTEGER NOT NULL CHECK (evidence_order >= 0),
            kind TEXT NOT NULL,
            reference_id TEXT NOT NULL,
            PRIMARY KEY (proposal_id, evidence_order),
            FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(proposal_id) ON DELETE CASCADE
        );
        """;

    private const string ProposalApplySchemaSql = """
        CREATE TABLE IF NOT EXISTS proposal_apply_drafts (
            draft_id TEXT PRIMARY KEY,
            proposal_id TEXT NOT NULL,
            proposal_revision INTEGER NOT NULL DEFAULT 1 CHECK (proposal_revision > 0),
            root_id TEXT NOT NULL,
            selection_revision INTEGER NOT NULL CHECK (selection_revision > 0),
            approval_digest TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('draft','approved','applied','rolled_back','failed')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (proposal_id) REFERENCES improvement_proposals(proposal_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS proposal_apply_files (
            draft_id TEXT NOT NULL,
            file_order INTEGER NOT NULL CHECK (file_order >= 0),
            base_sha256 TEXT NOT NULL,
            replacement_sha256 TEXT NOT NULL,
            PRIMARY KEY (draft_id,file_order),
            FOREIGN KEY (draft_id) REFERENCES proposal_apply_drafts(draft_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS proposal_apply_hunks (
            draft_id TEXT NOT NULL,
            hunk_id TEXT NOT NULL,
            selected INTEGER NOT NULL CHECK (selected IN (0,1)),
            replacement_sha256 TEXT NOT NULL,
            PRIMARY KEY (draft_id,hunk_id),
            FOREIGN KEY (draft_id) REFERENCES proposal_apply_drafts(draft_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS proposal_apply_revisions (
            draft_id TEXT NOT NULL,
            selection_revision INTEGER NOT NULL CHECK (selection_revision > 0),
            approval_digest TEXT NOT NULL,
            approved_at TEXT NULL,
            PRIMARY KEY (draft_id,selection_revision),
            FOREIGN KEY (draft_id) REFERENCES proposal_apply_drafts(draft_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS proposal_applies (
            apply_id TEXT PRIMARY KEY,
            draft_id TEXT NOT NULL,
            proposal_revision INTEGER NOT NULL DEFAULT 1 CHECK (proposal_revision > 0),
            state TEXT NOT NULL CHECK (state IN ('applied','rolled_back','failed')),
            created_at TEXT NOT NULL,
            FOREIGN KEY (draft_id) REFERENCES proposal_apply_drafts(draft_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS proposal_apply_audit (
            audit_id INTEGER PRIMARY KEY,
            apply_id TEXT NULL,
            draft_id TEXT NULL,
            proposal_id TEXT NOT NULL,
            root_id TEXT NOT NULL,
            actor_kind TEXT NOT NULL CHECK (actor_kind='local_user'),
            state TEXT NOT NULL,
            error_code TEXT NULL,
            file_count INTEGER NOT NULL CHECK (file_count >= 0),
            recorded_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS proposal_apply_pending (
            apply_id TEXT PRIMARY KEY,
            draft_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            root_id TEXT NOT NULL,
            actor_kind TEXT NOT NULL CHECK (actor_kind='local_user'),
            file_count INTEGER NOT NULL CHECK (file_count >= 0),
            operation_kind TEXT NOT NULL CHECK (operation_kind IN ('apply','rollback')),
            recorded_at TEXT NOT NULL
        );
        """;

    private const string ProposalApplyPendingSchemaSql = """
        CREATE TABLE IF NOT EXISTS proposal_apply_pending (
            apply_id TEXT PRIMARY KEY,
            draft_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            root_id TEXT NOT NULL,
            actor_kind TEXT NOT NULL CHECK (actor_kind='local_user'),
            file_count INTEGER NOT NULL CHECK (file_count >= 0),
            operation_kind TEXT NOT NULL CHECK (operation_kind IN ('apply','rollback')),
            recorded_at TEXT NOT NULL
        );
        """;

    private static string ApplyState(ProposalApplyState state) => state switch { ProposalApplyState.Draft => "draft", ProposalApplyState.Approved => "approved", ProposalApplyState.Applied => "applied", ProposalApplyState.RolledBack => "rolled_back", ProposalApplyState.Failed => "failed", _ => throw new ArgumentOutOfRangeException(nameof(state)) };
    private static ProposalApplyState ParseApplyState(string value) => value switch { "draft" => ProposalApplyState.Draft, "approved" => ProposalApplyState.Approved, "applied" => ProposalApplyState.Applied, "rolled_back" => ProposalApplyState.RolledBack, "failed" => ProposalApplyState.Failed, _ => throw new InvalidOperationException("Invalid proposal apply state.") };
}
