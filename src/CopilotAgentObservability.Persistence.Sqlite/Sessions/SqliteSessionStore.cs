using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SqliteSessionStore : ISessionStore
{
    private const int CurrentSchemaVersion = 9;
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;

    public SqliteSessionStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open(initialize: true);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT version FROM schema_version WHERE component = 'session';";
        var existingVersion = versionCommand.ExecuteScalar();
        if (existingVersion is not null && Convert.ToInt32(existingVersion) is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or CurrentSchemaVersion))
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
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
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
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 3)
        {
            command.CommandText = ProposalApplySchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 4)
        {
            command.CommandText = "ALTER TABLE proposal_apply_drafts ADD COLUMN updated_at TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00';";
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 5)
        {
            command.CommandText = ProposalApplyPendingSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 6)
        {
            command.CommandText = "ALTER TABLE improvement_proposals ADD COLUMN revision INTEGER NOT NULL DEFAULT 1; ALTER TABLE proposal_apply_drafts ADD COLUMN proposal_revision INTEGER NOT NULL DEFAULT 1; ALTER TABLE proposal_applies ADD COLUMN proposal_revision INTEGER NOT NULL DEFAULT 1;";
            command.ExecuteNonQuery();
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 7)
        {
            command.CommandText = ObjectiveEvaluationSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        else if (Convert.ToInt32(existingVersion) == 8)
        {
            command.CommandText = EffectComparisonSchemaSql;
            command.ExecuteNonQuery();
            Execute(connection, transaction, $"UPDATE schema_version SET version={CurrentSchemaVersion} WHERE component='session';");
        }
        transaction.Commit();
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
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ValidateBatch(connection, transaction, batch);
        var orderedRuns = OrderRuns(batch.Detail.Runs);
        var orderedEvents = OrderEvents(batch.Detail.Events);
        var canonicalEventIds = ResolveCanonicalEventIds(connection, transaction, batch.Detail.Events);
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
                ("$source_surface", run.SourceSurface is null ? null : SessionWire.ToWire(run.SourceSurface.Value)),
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
            Execute(connection, transaction, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES($event_id,$session_id,$run_id,$source_surface,$parent_event_id,$trace_id,$status,$source_adapter,$source_event_id,$type,$occurred_at,$content_state)
                ON CONFLICT(source_adapter,source_event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$session_id", Id(item.SessionId)), ("$run_id", item.RunId is null ? null : Id(item.RunId.Value)),
                ("$source_surface", item.SourceSurface is null ? null : SessionWire.ToWire(item.SourceSurface.Value)),
                ("$parent_event_id", parentEventId is null ? null : Id(parentEventId.Value)), ("$trace_id", item.TraceId), ("$status", item.Status),
                ("$source_adapter", item.SourceAdapter), ("$source_event_id", item.SourceEventId), ("$type", item.Type),
                ("$occurred_at", Timestamp(item.OccurredAt)), ("$content_state", SessionWire.ToWire(item.ContentState)));
        }

        foreach (var content in batch.Content)
        {
            var eventId = canonicalEventIds[content.EventId];
            Execute(connection, transaction, """
                INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at)
                VALUES($event_id,$content_kind,$content_json,$captured_at,$expires_at)
                ON CONFLICT(event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$content_kind", content.ContentKind), ("$content_json", content.ContentJson),
                ("$captured_at", Timestamp(content.CapturedAt)), ("$expires_at", Timestamp(content.ExpiresAt)));
        }

        transaction.Commit();
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
                ("$second", nativeId.NativeSessionId));
        }

        foreach (var run in batch.Detail.Runs)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_runs WHERE run_id=$first;",
                sessionIdText,
                ("$first", Id(run.RunId)));
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
                ("$first", Id(item.EventId)));
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_events WHERE source_adapter=$first AND source_event_id=$second;",
                sessionIdText,
                ("$first", item.SourceAdapter),
                ("$second", item.SourceEventId));

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
        bool requireExisting = false)
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
            throw OwnershipViolation();
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
            command.CommandText = "SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state FROM session_events WHERE session_id=$id ORDER BY occurred_at,event_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) events.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableGuid(reader, 2), NullableSurface(reader, 3), NullableGuid(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), ParseTimestamp(reader.GetString(10)), SessionWire.ParseContentState(reader.GetString(11))));
        }

        return new(session, nativeIds, runs, events);
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

    public SessionContentLookup? GetContent(Guid sessionId, Guid eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.event_id,c.content_kind,c.content_json,c.captured_at,c.expires_at
            FROM session_event_content c
            JOIN session_events e ON e.event_id=c.event_id
            WHERE e.session_id=$session_id AND e.event_id=$event_id;
            """;
        Add(command, "$session_id", Id(sessionId));
        Add(command, "$event_id", Id(eventId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var content = new SessionEventContent(
            Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)));
        return content.ExpiresAt > timeProvider.GetUtcNow()
            ? new(SessionContentState.Available, content)
            : new(SessionContentState.ExpiredPendingDeletion, null);
    }

    public SessionRawRetentionState GetRawRetentionState(Guid sessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
              WHEN EXISTS (
                SELECT 1 FROM session_event_content c
                JOIN session_events e ON e.event_id=c.event_id
                WHERE e.session_id=$session_id AND c.expires_at > $now
              ) THEN 'expiring'
              WHEN EXISTS (
                SELECT 1 FROM session_event_content c
                JOIN session_events e ON e.event_id=c.event_id
                WHERE e.session_id=$session_id
              ) THEN 'expired_pending_deletion'
              ELSE 'not_captured'
            END;
            """;
        Add(command, "$session_id", Id(sessionId));
        Add(command, "$now", Timestamp(timeProvider.GetUtcNow()));
        return SessionWire.ParseRawRetentionState((string)command.ExecuteScalar()!);
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
        if (!ExactReceiptScope(connection, transaction, receipt)) throw new ArgumentException("Objective evidence is not exact.", nameof(receipt));
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

    public EffectReceipt RecordEffectComparison(EffectComparisonRequest request, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateComparisonRequest(request);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var proposal = ReadImprovementProposal(connection, request.ProposalId);
        if (proposal is null || proposal.Revision != request.ProposalRevision || proposal.Status != ImprovementProposalStatus.Recommended)
            throw new InvalidOperationException("Proposal revision is stale.");

        var apply = ReadActiveApply(connection, transaction, request);
        if (apply is null)
            throw new InvalidOperationException("Application is not active.");

        var facts = new List<SessionEffectFacts>();
        var capturedEvidence = new List<(Guid SessionId, string Kind, string ReferenceId, string? RecordedAt)>();
        foreach (var item in request.Sessions.Where(item => item.Classification is "pre" or "post"))
        {
            var session = ReadComparisonSession(connection, transaction, item.SessionId);
            if (session is null || !IsComparable(session.Value))
                throw new InvalidOperationException("Comparison evidence is stale.");
            if (item.Classification == "pre" && session.Value.EndedAt > apply.Value.AppliedAt || item.Classification == "post" && session.Value.StartedAt < apply.Value.AppliedAt)
                throw new ArgumentException("Session crosses application boundary.", nameof(request));

            var evidence = new List<string>();
            var qualityPass = true;
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
                    capturedEvidence.Add((item.SessionId, "human", Id(item.SessionId), reader.GetString(1)));
                    qualityPass &= reader.GetString(0) == "expected";
                }
            }
            foreach (var objective in Objectives(connection, transaction, item.SessionId))
            {
                evidence.Add(Id(objective.ObjectiveEvaluationId));
                capturedEvidence.Add((item.SessionId, "objective", Id(objective.ObjectiveEvaluationId), Timestamp(objective.RecordedAt)));
                qualityPass &= objective.Result == ObjectiveResult.Pass;
                severe |= objective.Result == ObjectiveResult.Fail && objective.Severity == ObjectiveSeverity.Severe;
                foreach (var reference in objective.Evidence)
                    capturedEvidence.Add((item.SessionId, "objective_" + reference.Kind, reference.ReferenceId, null));
            }
            var duration = session.Value.StartedAt is { } started && session.Value.EndedAt is { } ended ? (long?)(ended - started).TotalMilliseconds : null;
            var tokens = SessionTokens(connection, transaction, item.SessionId);
            facts.Add(new(item.SessionId, item.Classification, item.CaseKey, qualityPass, severe, duration, tokens, evidence));
        }

        var result = EffectVerdictEngine.Evaluate(new(true, facts.Where(item => item.Side == "pre").ToArray(), facts.Where(item => item.Side == "post").ToArray(), []));
        var comparisonId = Guid.CreateVersion7();
        var cohortRevision = NextCohortRevision(connection, transaction, request.ProposalId, request.ApplyId);
        Execute(connection, transaction, "INSERT INTO effect_comparisons(comparison_id,cohort_revision,proposal_id,proposal_revision,apply_id,recorded_at) VALUES($id,$cohort,$proposal,$revision,$apply,$recorded);", ("$id", Id(comparisonId)), ("$cohort", cohortRevision), ("$proposal", Id(request.ProposalId)), ("$revision", request.ProposalRevision), ("$apply", Id(request.ApplyId)), ("$recorded", Timestamp(recordedAt)));
        foreach (var (item, order) in request.Sessions.Select((value, index) => (value, index)))
            Execute(connection, transaction, "INSERT INTO effect_comparison_sessions(comparison_id,session_id,classification,case_key,exclusion_reason,session_order) VALUES($comparison,$session,$classification,$case,$reason,$order);", ("$comparison", Id(comparisonId)), ("$session", Id(item.SessionId)), ("$classification", item.Classification), ("$case", item.CaseKey), ("$reason", item.ExclusionReason), ("$order", order));
        foreach (var (evidence, order) in capturedEvidence.Select((value, index) => (value, index)))
            Execute(connection, transaction, "INSERT INTO effect_comparison_evidence(comparison_id,evidence_order,session_id,kind,reference_id,recorded_at) VALUES($comparison,$order,$session,$kind,$reference,$recorded);", ("$comparison", Id(comparisonId)), ("$order", order), ("$session", Id(evidence.SessionId)), ("$kind", evidence.Kind), ("$reference", evidence.ReferenceId), ("$recorded", evidence.RecordedAt));
        Execute(connection, transaction, "INSERT INTO effect_receipts(comparison_id,verdict,result_json,recorded_at) VALUES($comparison,$verdict,$result,$recorded);", ("$comparison", Id(comparisonId)), ("$verdict", VerdictText(result.Verdict)), ("$result", System.Text.Json.JsonSerializer.Serialize(result)), ("$recorded", Timestamp(recordedAt)));
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
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.comparison_id,c.cohort_revision,c.proposal_id,c.proposal_revision,c.apply_id,r.result_json,c.recorded_at,CASE WHEN a.state='rolled_back' THEN 'invalidated' ELSE 'active' END FROM effect_comparisons c JOIN effect_receipts r ON r.comparison_id=c.comparison_id JOIN proposal_applies a ON a.apply_id=c.apply_id WHERE c.proposal_id=$proposal ORDER BY c.recorded_at,c.comparison_id;";
        Add(command, "$proposal", Id(proposalId)); using var reader = command.ExecuteReader(); var receipts = new List<EffectReceipt>();
        while (reader.Read()) receipts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), Guid.Parse(reader.GetString(2)), reader.GetInt32(3), Guid.Parse(reader.GetString(4)), System.Text.Json.JsonSerializer.Deserialize<EffectVerdictResult>(reader.GetString(5))!, reader.GetString(7), ParseTimestamp(reader.GetString(6))));
        return receipts;
    }

    private static IReadOnlyList<ObjectiveEvaluationEvidence> Evidence(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT kind,reference_id FROM objective_evaluation_evidence WHERE objective_evaluation_id=$id ORDER BY evidence_order;"; Add(command, "$id", Id(id)); using var reader = command.ExecuteReader(); var result = new List<ObjectiveEvaluationEvidence>(); while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1))); return result;
    }

    private static bool ExactReceiptScope(SqliteConnection connection, SqliteTransaction transaction, ObjectiveEvaluationReceipt receipt)
    {
        using var scope = connection.CreateCommand(); scope.Transaction = transaction;
        scope.CommandText = "SELECT 1 FROM sessions s JOIN session_runs r ON r.session_id=s.session_id WHERE s.session_id=$session AND r.run_id=$run AND s.completeness='full' AND s.status IN ('completed','failed') AND r.trace_id=$trace AND EXISTS (SELECT 1 FROM session_native_ids n WHERE n.session_id=s.session_id AND n.binding_kind='native');";
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
                "gate" when evidence.ReferenceId == "terminal" => "SELECT 1 FROM session_events WHERE session_id=$session AND run_id=$run AND trace_id=$trace AND type IN ('session.shutdown','session.task_complete','SessionEnd','Stop');",
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
        if (proposal.Status != ImprovementProposalStatus.Candidate
            || proposal.RecommendedAt is not null
            || proposal.VerifiedAt is not null)
        {
            throw new InvalidOperationException("Improvement proposals must be created as candidates without lifecycle timestamps.");
        }

        ValidateProposalShape(proposal);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        _ = ValidateProposalReferences(connection, transaction, proposal);
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

    public void UpdateImprovementProposalStatus(Guid proposalId, ImprovementProposalStatus status, DateTimeOffset updatedAt)
    {
        RejectVerified(status);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var proposal = ReadImprovementProposal(connection, proposalId)
            ?? throw new InvalidOperationException("Improvement proposal was not found.");
        RejectVerified(proposal.Status);

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
        if (rows != 1) throw new InvalidOperationException("Improvement proposal apply authorization is active.");
        transaction.Commit();
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
                || !IsBounded(reference.ReferenceId, 512)))
        {
            throw new InvalidOperationException("Improvement proposal is invalid.");
        }
    }

    private static void ValidatePromotion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImprovementProposal proposal,
        Guid proposalId)
    {
        ValidateProposalShape(proposal);
        if (proposal.SourceSessionIds.Count < 2
            || proposal.SourceSessionIds.Distinct().Count() != proposal.SourceSessionIds.Count
            || proposal.EvidenceReferences.Count == 0)
        {
            throw new InvalidOperationException("Improvement recommendations require distinct source sessions and evidence.");
        }

        var evidencedSourceSessions = ValidateProposalReferences(connection, transaction, proposal);
        if (evidencedSourceSessions.Count < 2)
        {
            throw new InvalidOperationException("Improvement recommendations require evidence from two distinct source sessions.");
        }
        foreach (var sessionId in proposal.SourceSessionIds)
        {
            using var sessionCommand = connection.CreateCommand();
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM sessions session
                    WHERE session.session_id=$session_id
                    AND session.status IN ('completed','failed')
                    AND EXISTS (
                        SELECT 1 FROM session_native_ids native
                        WHERE native.session_id=session.session_id AND native.binding_kind='native'
                    )
                );
                """;
            Add(sessionCommand, "$session_id", Id(sessionId));
            if (Convert.ToInt64(sessionCommand.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException("Improvement proposal source session is not eligible for recommendation.");
            }

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
                throw new InvalidOperationException("An improvement recommendation already exists for a source session.");
            }
        }
    }

    private static IReadOnlySet<Guid> ValidateProposalReferences(SqliteConnection connection, SqliteTransaction transaction, ImprovementProposal proposal)
    {
        var evidencedSourceSessions = new HashSet<Guid>();
        foreach (var reference in proposal.EvidenceReferences)
        {
            var resolvingSourceSessions = ResolveReferenceSourceSessions(connection, transaction, proposal.SourceSessionIds, reference);
            if (resolvingSourceSessions.Count == 0)
            {
                throw new InvalidOperationException("Improvement proposal evidence does not resolve to a source session.");
            }

            evidencedSourceSessions.UnionWith(resolvingSourceSessions);
        }

        return evidencedSourceSessions;
    }

    private static IReadOnlyList<Guid> ResolveReferenceSourceSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> sourceSessionIds,
        ImprovementProposalEvidenceReference reference)
    {
        var sessionColumn = reference.Kind == "event" ? "event_id" : "run_id";
        var table = reference.Kind == "event" ? "session_events" : "session_runs";
        if (reference.Kind is "event" or "run")
        {
            if (!Guid.TryParse(reference.ReferenceId, out var referenceId)) return [];
            return sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                $"SELECT 1 FROM {table} WHERE {sessionColumn}=$reference_id AND session_id=$session_id;",
                ("$reference_id", Id(referenceId)), ("$session_id", Id(sessionId)))).ToArray();
        }

        if (reference.Kind == "trace")
        {
            return sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_runs WHERE trace_id=$trace_id AND session_id=$session_id;",
                ("$trace_id", reference.ReferenceId), ("$session_id", Id(sessionId)))).ToArray();
        }

        return reference.ReferenceId switch
        {
            "terminal" => sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_events WHERE session_id=$session_id AND type IN ('session.shutdown','session.task_complete','SessionEnd','Stop');",
                ("$session_id", Id(sessionId)))).ToArray(),
            "error" => sourceSessionIds.Where(sessionId => Exists(connection, transaction,
                "SELECT 1 FROM session_events WHERE session_id=$session_id AND status='error';",
                ("$session_id", Id(sessionId)))).ToArray(),
            _ => [],
        };
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsOneOf(string? value, params string[] values) => value is not null && values.Contains(value, StringComparer.Ordinal);
    private static bool IsBounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool IsUuidVersion7(Guid value) => value != Guid.Empty && value.ToString("D")[14] == '7';

    private static void WriteSession(SqliteConnection connection, SqliteTransaction transaction, ObservedSession value) =>
        Execute(connection, transaction, """
            INSERT INTO sessions(session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,$status,$completeness,$repository,$workspace,$started_at,$ended_at,$last_seen_at,$raw_retention_state,$created_at,$updated_at)
            ON CONFLICT(session_id) DO UPDATE SET status=excluded.status,completeness=excluded.completeness,repository=excluded.repository,workspace=excluded.workspace,
            started_at=excluded.started_at,ended_at=excluded.ended_at,last_seen_at=excluded.last_seen_at,raw_retention_state=excluded.raw_retention_state,updated_at=excluded.updated_at;
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

    private static ImprovementProposal? ReadImprovementProposal(SqliteConnection connection, Guid proposalId)
    {
        using var command = connection.CreateCommand();
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
        var sourceSessions = ReadImprovementProposalSourceSessions(connection, proposalId);
        var evidenceReferences = ReadImprovementProposalEvidenceReferences(connection, proposalId);
        return new ImprovementProposal(
            proposal.ProposalId, proposal.Revision, proposal.Status, proposal.TargetKind, proposal.TargetLabel, proposal.Title, proposal.Summary,
            proposal.ExpectedEffect, proposal.RiskNote, sourceSessions, evidenceReferences, proposal.CreatedAt, proposal.UpdatedAt,
            proposal.RecommendedAt, proposal.VerifiedAt);
    }

    private static IReadOnlyList<Guid> ReadImprovementProposalSourceSessions(SqliteConnection connection, Guid proposalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM improvement_proposal_sessions WHERE proposal_id=$proposal_id ORDER BY source_order;";
        Add(command, "$proposal_id", Id(proposalId));
        using var reader = command.ExecuteReader();
        var sourceSessions = new List<Guid>();
        while (reader.Read()) sourceSessions.Add(Guid.Parse(reader.GetString(0)));
        return sourceSessions;
    }

    private static IReadOnlyList<ImprovementProposalEvidenceReference> ReadImprovementProposalEvidenceReferences(SqliteConnection connection, Guid proposalId)
    {
        using var command = connection.CreateCommand();
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
            throw new InvalidOperationException("Improvement proposal verification is owned by comparison.");
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

    private SqliteConnection Open(bool initialize = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        if (initialize)
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
        }

        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

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

    private static (DateTimeOffset AppliedAt, Guid DraftId)? ReadActiveApply(SqliteConnection connection, SqliteTransaction transaction, EffectComparisonRequest request)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT a.created_at,a.draft_id FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE a.apply_id=$apply AND d.proposal_id=$proposal AND a.proposal_revision=$revision AND a.state='applied' AND NOT EXISTS(SELECT 1 FROM proposal_apply_pending p WHERE p.apply_id=a.apply_id AND p.operation_kind='rollback');";
        Add(command, "$apply", Id(request.ApplyId)); Add(command, "$proposal", Id(request.ProposalId)); Add(command, "$revision", request.ProposalRevision);
        using var reader = command.ExecuteReader(); return reader.Read() ? (ParseTimestamp(reader.GetString(0)), Guid.Parse(reader.GetString(1))) : null;
    }

    private static (ObservedSessionStatus Status, SessionCompleteness Completeness, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, bool Exact)? ReadComparisonSession(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT status,completeness,started_at,ended_at,EXISTS(SELECT 1 FROM session_native_ids n WHERE n.session_id=s.session_id AND n.binding_kind='native') FROM sessions s WHERE session_id=$session;";
        Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (SessionWire.ParseStatus(reader.GetString(0)), SessionWire.ParseCompleteness(reader.GetString(1)), reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)), reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)), reader.GetInt32(4) != 0);
    }

    private static bool IsComparable((ObservedSessionStatus Status, SessionCompleteness Completeness, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, bool Exact) session) =>
        session.Exact && session.Completeness == SessionCompleteness.Full && session.Status is ObservedSessionStatus.Completed or ObservedSessionStatus.Failed && session.StartedAt is not null && session.EndedAt is not null;

    private static IReadOnlyList<ObjectiveEvaluationReceipt> Objectives(SqliteConnection connection, SqliteTransaction transaction, Guid sessionId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at FROM objective_evaluations WHERE session_id=$session ORDER BY recorded_at,objective_evaluation_id;";
        Add(command, "$session", Id(sessionId)); using var reader = command.ExecuteReader(); var result = new List<ObjectiveEvaluationReceipt>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4) == "pass" ? ObjectiveResult.Pass : ObjectiveResult.Fail, reader.GetString(5) == "normal" ? ObjectiveSeverity.Normal : ObjectiveSeverity.Severe, reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), Evidence(connection, Guid.Parse(reader.GetString(0))), ParseTimestamp(reader.GetString(10))));
        return result;
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
            source_surface TEXT NOT NULL CHECK (source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
            native_session_id TEXT NOT NULL,
            binding_kind TEXT NOT NULL CHECK (binding_kind IN ('native','explicit_resume','explicit_handoff','trace_context')),
            observed_at TEXT NOT NULL,
            PRIMARY KEY (source_surface, native_session_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS session_runs (
            run_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
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
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
            parent_event_id TEXT NULL,
            trace_id TEXT NULL,
            status TEXT NULL,
            source_adapter TEXT NOT NULL,
            source_event_id TEXT NOT NULL,
            type TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
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
            FOREIGN KEY (event_id) REFERENCES session_events(event_id) ON DELETE CASCADE
        );

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
