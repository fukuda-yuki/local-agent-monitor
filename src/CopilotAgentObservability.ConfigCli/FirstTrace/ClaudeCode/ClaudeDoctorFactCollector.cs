using System.Globalization;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Documents;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

internal sealed class ClaudeDoctorFactCollector
{
    private const string SourceSurface = "claude-code";
    private const string SourceAdapter = "claude-code-otel";
    private const string LivePath = "/health/live";
    private const int ProbeBudgetMilliseconds = 500;
    private const int MaximumProbeBodyBytes = 4096;
    private const int MaximumSettingsBytes = 1024 * 1024;
    private const string DefaultEndpoint = "http://127.0.0.1:4320";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyList<OwnedKey> OwnedKeys =
    [
        new("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
        new("OTEL_TRACES_EXPORTER", "otlp"),
        new("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf"),
        new("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", null),
        new("OTEL_LOG_USER_PROMPTS", "1"),
        new("OTEL_LOG_TOOL_DETAILS", "1"),
        new("OTEL_LOG_TOOL_CONTENT", "1"),
    ];

    private readonly ISetupPlatform platform;
    private readonly ISetupHttpProbe httpProbe;
    private readonly ISetupClock clock;
    private readonly string invocationDirectory;
    private readonly string managedFilePath;

    public ClaudeDoctorFactCollector(
        ISetupPlatform platform,
        ISetupHttpProbe? httpProbe = null,
        ISetupClock? clock = null,
        string? invocationDirectory = null,
        string? managedFilePath = null)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.httpProbe = httpProbe ?? platform.HttpProbe;
        this.clock = clock ?? platform.Clock;
        this.invocationDirectory = invocationDirectory ?? Environment.CurrentDirectory;
        this.managedFilePath = managedFilePath ?? DefaultManagedFilePath(platform);
    }

    public ClaudeDoctorFactInputs Collect(
        string databasePath,
        string? canonicalOrigin,
        DoctorVerification? verification = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var origin = NormalizeEndpoint(canonicalOrigin, out var originIsValid);
        var liveness = ProbeLiveness(origin, originIsValid);
        var readiness = ProbeReadiness(origin, originIsValid);
        var databaseExists = ReadDatabasePresence(databasePath);
        var sourceVersion = ReadSourceVersion();
        var effective = ResolveEffectiveValues(origin);
        var now = clock.UtcNow.ToUniversalTime();

        var database = databaseExists == true
            ? ReadDatabase(databasePath, verification, now)
            : DatabaseRead.Empty;
        var setupLedger = ReadSetupLedger(databasePath, databaseExists, now);

        return new ClaudeDoctorFactInputs(
            liveness,
            databaseExists,
            sourceVersion,
            origin,
            effective.Endpoint,
            effective.Protocol,
            effective.TelemetryGate,
            effective.ExporterGate,
            readiness,
            database.SourceCompatibility,
            database.Window,
            effective.ContentGate,
            database.RuntimeRawAccess,
            setupLedger);
    }

    private ClaudeLivenessProbeClassification ProbeLiveness(string origin, bool originIsValid)
    {
        if (!originIsValid)
        {
            return ClaudeLivenessProbeClassification.ProbeUnavailable;
        }

        SetupHttpProbeObservation observation;
        try
        {
            observation = httpProbe.Get(origin, LivePath, ProbeBudgetMilliseconds, MaximumProbeBodyBytes);
        }
        catch (Exception)
        {
            return ClaudeLivenessProbeClassification.ProbeUnavailable;
        }

        if (observation.Outcome == SetupHttpProbeOutcome.Refused)
        {
            return ClaudeLivenessProbeClassification.PositiveNoListener;
        }

        return IsLiveResponse(observation)
            ? ClaudeLivenessProbeClassification.MonitorLive
            : ClaudeLivenessProbeClassification.OtherForeign;
    }

    private bool? ProbeReadiness(string origin, bool originIsValid)
    {
        if (!originIsValid)
        {
            return null;
        }

        try
        {
            var execution = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);
            var result = ClaudeCodeReadinessProbe.Probe(
                new ProbePlatform(platform, httpProbe),
                origin,
                execution.Context);
            return result.Reachable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool? ReadDatabasePresence(string databasePath)
    {
        try
        {
            return platform.FileSystem.FileExists(databasePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private ClaudeSourceVersionClassification ReadSourceVersion()
    {
        var detection = ClaudeCodeVersionDetector.Detect(platform);
        return detection.IsSupported
            ? ClaudeSourceVersionClassification.Supported
            : detection.Detected
                ? ClaudeSourceVersionClassification.Unsupported
                : ClaudeSourceVersionClassification.Undetectable;
    }

    private EffectiveValues ResolveEffectiveValues(string origin)
    {
        var expected = OwnedKeys
            .Select(key => key.Key == "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"
                ? key with { Expected = $"{origin}/v1/traces" }
                : key)
            .ToArray();
        var values = expected.ToDictionary(
            key => key.Key,
            key => ResolveKey(key),
            StringComparer.Ordinal);

        return new EffectiveValues(
            ClassifyEndpoint(values["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]),
            ClassifyProtocol(values["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"]),
            ClassifyGate(values["CLAUDE_CODE_ENABLE_TELEMETRY"]),
            ClassifyGate(values["OTEL_TRACES_EXPORTER"]),
            ClassifyContentGate(values));
    }

    private ResolvedValue ResolveKey(OwnedKey key)
    {
        try
        {
            var process = platform.ProcessEnvironment.Get(key.Key);
            if (process is not null)
            {
                return new(process, false, key.Expected);
            }
        }
        catch (Exception)
        {
            return new(null, true, key.Expected);
        }

        foreach (var source in ReadSources())
        {
            if (source.State == SettingsState.Unreadable)
            {
                return new(null, true, key.Expected);
            }

            if (source.State == SettingsState.Conflict)
            {
                return new(null, false, key.Expected, true);
            }

            if (source.Values.TryGetValue(key.Key, out var value))
            {
                return new(value, false, key.Expected);
            }
        }

        return new(null, false, key.Expected);
    }

    private IReadOnlyList<SettingsSource> ReadSources()
    {
        var sources = new List<SettingsSource>();
        var managed = ReadManagedSettings();
        sources.Add(managed);
        if (managed.State == SettingsState.Unreadable)
        {
            return sources;
        }

        var local = ReadFileSettings(Path.Combine(invocationDirectory, ".claude", "settings.local.json"));
        sources.Add(local);
        if (local.State == SettingsState.Unreadable)
        {
            return sources;
        }

        var project = ReadFileSettings(Path.Combine(invocationDirectory, ".claude", "settings.json"));
        sources.Add(project);
        if (project.State == SettingsState.Unreadable)
        {
            return sources;
        }

        sources.Add(ReadFileSettings(Path.Combine(
            platform.OperatingSystem.UserProfile,
            ".claude",
            "settings.json")));
        return sources;
    }

    private SettingsSource ReadManagedSettings()
    {
        try
        {
            if (platform.OperatingSystem.Current == SetupPlanningOs.Windows)
            {
                var machine = ReadManagedObservation(SetupManagedLocation.ClaudeCodeWindowsMachinePolicy);
                if (machine.State != SettingsState.Absent)
                {
                    return machine;
                }

                var file = ReadFileSettings(managedFilePath);
                if (file.State != SettingsState.Absent)
                {
                    return file;
                }

                return ReadManagedObservation(SetupManagedLocation.ClaudeCodeWindowsUserPolicy);
            }

            return platform.OperatingSystem.Current == SetupPlanningOs.Linux
                ? ReadFileSettings(managedFilePath)
                : SettingsSource.Absent;
        }
        catch (Exception)
        {
            return SettingsSource.Unreadable;
        }
    }

    private SettingsSource ReadManagedObservation(SetupManagedLocation location)
    {
        var observation = platform.ManagedSettings.Read(location);
        return observation.Outcome switch
        {
            SetupManagedOutcome.Absent => SettingsSource.Absent,
            SetupManagedOutcome.Failed => SettingsSource.Unreadable,
            SetupManagedOutcome.Present when observation.IsComplete => ParseSettings(observation.Bytes),
            _ => SettingsSource.Unreadable,
        };
    }

    private SettingsSource ReadFileSettings(string path)
    {
        try
        {
            if (!platform.FileSystem.FileExists(path))
            {
                return SettingsSource.Absent;
            }

            var read = platform.FileSystem.ReadAtMostBytes(path, MaximumSettingsBytes);
            return read.IsComplete ? ParseSettings(read.Bytes) : SettingsSource.Unreadable;
        }
        catch (Exception)
        {
            return SettingsSource.Unreadable;
        }
    }

    private static SettingsSource ParseSettings(byte[] bytes)
    {
        try
        {
            var content = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("env", out var env))
            {
                if (env.ValueKind != JsonValueKind.Object)
                {
                    return SettingsSource.Unreadable;
                }

                foreach (var property in env.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return SettingsSource.Unreadable;
                    }

                    if (!values.TryAdd(property.Name, property.Value.GetString()!))
                    {
                        return SettingsSource.Conflict;
                    }
                }
            }

            _ = ClaudeSettingsDocument.Parse(content);
            return new(SettingsState.Present, values);
        }
        catch (Exception)
        {
            return SettingsSource.Unreadable;
        }
    }

    private DatabaseRead ReadDatabase(
        string databasePath,
        DoctorVerification? verification,
        DateTimeOffset now)
    {
        try
        {
            using var connection = OpenReadOnly(databasePath);
            var compatibility = ReadSourceCompatibility(connection);
            var runtime = ReadRuntimeRawAccess(connection);
            var window = verification is null
                ? null
                : ReadWindow(connection, verification);
            return new(compatibility, runtime, window);
        }
        catch (Exception)
        {
            return new(
                ClaudeSourceCompatibilityClassification.Unreadable,
                ClaudeRuntimeRawAccessClassification.Unreadable,
                verification is null ? null : EmptyWindow);
        }
    }

    private ClaudeSourceCompatibilityClassification ReadSourceCompatibility(SqliteConnection connection)
    {
        if (!TableExists(connection, "source_schema_observations"))
        {
            return ClaudeSourceCompatibilityClassification.Unreadable;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT compatibility_state, schema_fingerprint FROM source_schema_observations " +
            "WHERE source_surface=$surface AND source_adapter=$adapter ORDER BY id;";
        Add(command, "$surface", SourceSurface);
        Add(command, "$adapter", SourceAdapter);
        using var reader = command.ExecuteReader();
        var states = new List<(SourceCompatibilityState State, string? Fingerprint)>();
        while (reader.Read())
        {
            states.Add((ParseCompatibilityState(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        if (states.Count == 0)
        {
            return ClaudeSourceCompatibilityClassification.NoRows;
        }

        if (states.Any(row =>
            row.State is SourceCompatibilityState.UnsupportedSourceVersion or SourceCompatibilityState.AdapterFailure ||
            row.Fingerprint is null))
        {
            return ClaudeSourceCompatibilityClassification.Incompatible;
        }

        if (states.Any(row =>
            row.State is SourceCompatibilityState.SchemaDriftDetected or SourceCompatibilityState.RecognizedRecordDropDetected))
        {
            return ClaudeSourceCompatibilityClassification.Drift;
        }

        return ClaudeSourceCompatibilityClassification.Matching;
    }

    private ClaudeRuntimeRawAccessClassification ReadRuntimeRawAccess(SqliteConnection connection)
    {
        if (!TableExists(connection, "monitor_runtime_state"))
        {
            return ClaudeRuntimeRawAccessClassification.Absent;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_access FROM monitor_runtime_state WHERE id=1;";
        var value = command.ExecuteScalar() as string;
        return value switch
        {
            "available" => ClaudeRuntimeRawAccessClassification.Available,
            "sanitized_only" => ClaudeRuntimeRawAccessClassification.SanitizedOnly,
            null => ClaudeRuntimeRawAccessClassification.Absent,
            _ => ClaudeRuntimeRawAccessClassification.Unreadable,
        };
    }

    private ClaudeDoctorVerificationWindow ReadWindow(
        SqliteConnection connection,
        DoctorVerification verification)
    {
        var candidates = ReadCandidates(connection, verification.VerificationId);
        var records = ReadEligibleRecords(connection, verification);
        var accepted = records.Count != 0;
        var rejected = ReadRejectedIngestExists(connection, verification);
        var raw = candidates.Any(candidate => candidate.EvidenceKind == DoctorEvidenceKind.RawPersistence);
        var projection = candidates.Any(candidate => candidate.EvidenceKind == DoctorEvidenceKind.Projection);
        var bindingCandidates = candidates
            .Where(candidate => candidate.EvidenceKind == DoctorEvidenceKind.ExactSessionBinding)
            .ToArray();
        var (completeness, content) = ReadBoundSession(connection, bindingCandidates);

        return new(
            accepted,
            rejected,
            raw,
            projection,
            projection ? ClaudeProjectionEvidence.NotStarted : ReadProjectionEvidence(connection, records, candidates),
            bindingCandidates.Length != 0,
            completeness,
            content);
    }

    private IReadOnlyList<DoctorEvidenceCandidate> ReadCandidates(
        SqliteConnection connection,
        string verificationId)
    {
        if (!TableExists(connection, "doctor_verification_evidence"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT candidate_id,verification_id,source_surface,source_adapter,evidence_class,evidence_kind,evidence_ref,observed_at,expires_at " +
            "FROM doctor_verification_evidence WHERE verification_id=$verification_id " +
            "ORDER BY observed_at COLLATE BINARY,evidence_ref COLLATE BINARY,candidate_id COLLATE BINARY;";
        Add(command, "$verification_id", verificationId);
        using var reader = command.ExecuteReader();
        var candidates = new List<DoctorEvidenceCandidate>();
        while (reader.Read())
        {
            candidates.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseEvidenceClass(reader.GetString(4)),
                ParseEvidenceKind(reader.GetString(5)),
                reader.GetString(6),
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8))));
        }

        return candidates;
    }

    private IReadOnlyList<WindowRecord> ReadEligibleRecords(
        SqliteConnection connection,
        DoctorVerification verification)
    {
        if (!TableExists(connection, "raw_records") || !TableExists(connection, "source_schema_observations"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT r.id,r.received_at,r.payload_json FROM raw_records r " +
            "JOIN source_schema_observations o ON o.raw_record_id=r.id " +
            "WHERE o.source_surface=$surface AND o.source_adapter=$adapter " +
            "AND r.received_at >= $started_at AND r.received_at < $expires_at " +
            "ORDER BY r.received_at COLLATE BINARY,r.id;";
        Add(command, "$surface", SourceSurface);
        Add(command, "$adapter", SourceAdapter);
        Add(command, "$started_at", Timestamp(verification.StartedAt));
        Add(command, "$expires_at", Timestamp(verification.ExpiresAt));
        using var reader = command.ExecuteReader();
        var records = new List<WindowRecord>();
        while (reader.Read())
        {
            records.Add(new(reader.GetInt64(0), ParseTimestamp(reader.GetString(1)), reader.GetString(2)));
        }

        return records;
    }

    private bool ReadRejectedIngestExists(
        SqliteConnection connection,
        DoctorVerification verification)
    {
        if (!TableExists(connection, "source_schema_observations"))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM source_schema_observations WHERE source_surface=$surface " +
            "AND source_adapter=$adapter AND raw_record_id IS NULL AND compatibility_state='adapter_failure' " +
            "AND observed_at >= $started_at AND observed_at < $expires_at);";
        Add(command, "$surface", SourceSurface);
        Add(command, "$adapter", SourceAdapter);
        Add(command, "$started_at", Timestamp(verification.StartedAt));
        Add(command, "$expires_at", Timestamp(verification.ExpiresAt));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private ClaudeProjectionEvidence ReadProjectionEvidence(
        SqliteConnection connection,
        IReadOnlyList<WindowRecord> records,
        IReadOnlyList<DoctorEvidenceCandidate> candidates)
    {
        var rawCandidates = candidates
            .Where(candidate => candidate.EvidenceKind == DoctorEvidenceKind.RawPersistence)
            .Select(candidate => candidate.EvidenceRef)
            .ToHashSet(StringComparer.Ordinal);
        if (rawCandidates.Count == 0)
        {
            return ClaudeProjectionEvidence.NotStarted;
        }

        var selected = records
            .Where(record => ContainsCandidateIdentity(record.PayloadJson, rawCandidates))
            .ToArray();
        if (selected.Length == 0)
        {
            selected = records.ToArray();
        }

        var evidence = selected.Select(record => ReadRecordProjectionEvidence(connection, record)).ToArray();
        return evidence.Any(item => item == ClaudeProjectionEvidence.Failed)
            ? ClaudeProjectionEvidence.Failed
            : evidence.Any(item => item == ClaudeProjectionEvidence.Pending)
                ? ClaudeProjectionEvidence.Pending
                : ClaudeProjectionEvidence.NotStarted;
    }

    private static ClaudeProjectionEvidence ReadRecordProjectionEvidence(
        SqliteConnection connection,
        WindowRecord record)
    {
        if (!TableExists(connection, "monitor_ingestions"))
        {
            return ClaudeProjectionEvidence.NotStarted;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT span_projected_at FROM monitor_ingestions WHERE raw_record_id=$id;";
        Add(command, "$id", record.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return CanBuildRecordProjection(record)
                ? ClaudeProjectionEvidence.NotStarted
                : ClaudeProjectionEvidence.Failed;
        }

        if (!reader.IsDBNull(0))
        {
            return ClaudeProjectionEvidence.NotStarted;
        }

        try
        {
            _ = MonitorSpanProjectionBuilder.Build(ToProjectionRecord(record));
            return ClaudeProjectionEvidence.Pending;
        }
        catch (Exception)
        {
            return ClaudeProjectionEvidence.Failed;
        }
    }

    private static bool CanBuildRecordProjection(WindowRecord record)
    {
        try
        {
            _ = MonitorProjectionBuilder.Build(ToProjectionRecord(record));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static RawTelemetryRecord ToProjectionRecord(WindowRecord record) =>
        new(record.Id, RawTelemetrySources.RawOtlp, null, record.ReceivedAt,
            null, OtlpJsonRecognizedPayloadBuilder.Build(record.PayloadJson));

    private (ClaudeBoundSessionCompleteness Completeness, ClaudeAgreedContentState Content) ReadBoundSession(
        SqliteConnection connection,
        IReadOnlyList<DoctorEvidenceCandidate> candidates)
    {
        if (candidates.Count == 0 || !TableExists(connection, "sessions"))
        {
            return (ClaudeBoundSessionCompleteness.Unavailable, ClaudeAgreedContentState.None);
        }

        var bound = new List<(string TraceId, Guid SessionId, ClaudeBoundSessionCompleteness Completeness)>();
        foreach (var candidate in candidates)
        {
            if (!TryParseBindingReference(candidate.EvidenceRef, out var traceId, out var sessionId))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT completeness FROM sessions WHERE session_id=$session_id;";
            Add(command, "$session_id", sessionId.ToString("D"));
            var value = command.ExecuteScalar() as string;
            bound.Add((traceId, sessionId, value is null ? ClaudeBoundSessionCompleteness.Unavailable : ParseCompleteness(value)));
        }

        if (bound.Count == 0)
        {
            return (ClaudeBoundSessionCompleteness.Unavailable, ClaudeAgreedContentState.None);
        }

        var completeness = bound
            .Select(item => item.Completeness)
            .OrderByDescending(CompletenessRank)
            .First();
        var content = ReadAgreedContentState(connection, bound[0].TraceId);
        return (completeness, content);
    }

    private static ClaudeAgreedContentState ReadAgreedContentState(
        SqliteConnection connection,
        string traceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT o.capture_content_state FROM source_schema_observations o " +
            "JOIN raw_records r ON r.id=o.raw_record_id WHERE o.source_surface=$surface " +
            "AND o.source_adapter=$adapter AND r.trace_id=$trace_id COLLATE BINARY " +
            "AND o.capture_content_state IS NOT NULL;";
        Add(command, "$surface", SourceSurface);
        Add(command, "$adapter", SourceAdapter);
        Add(command, "$trace_id", traceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ClaudeAgreedContentState.None;
        }

        var state = reader.GetString(0);
        if (reader.Read())
        {
            return ClaudeAgreedContentState.Unreadable;
        }

        return state switch
        {
            "available" => ClaudeAgreedContentState.Available,
            "redacted" => ClaudeAgreedContentState.Redacted,
            "not_captured" => ClaudeAgreedContentState.NotCaptured,
            "unsupported" => ClaudeAgreedContentState.Unsupported,
            _ => ClaudeAgreedContentState.Unreadable,
        };
    }

    private ClaudeSetupLedgerClassification ReadSetupLedger(
        string databasePath,
        bool? databaseExists,
        DateTimeOffset now)
    {
        SetupOwnershipLedger ledger;
        try
        {
            var paths = new SetupRuntimePaths(platform);
            ledger = new SetupLedgerStore(platform, paths, new SetupPlanStore(platform, paths)).Load();
        }
        catch (Exception)
        {
            return ClaudeSetupLedgerClassification.Unreadable;
        }

        var applied = ledger.ChangeSets
            .Where(changeSet => changeSet.Adapter == SourceSurface && changeSet.State == SetupChangeSetState.Applied)
            .OrderByDescending(changeSet => changeSet.UpdatedAt)
            .FirstOrDefault();
        if (applied is null)
        {
            return ClaudeSetupLedgerClassification.NoAppliedChangeSet;
        }

        if (databaseExists != true)
        {
            return ClaudeSetupLedgerClassification.AwaitingAcceptedIngest;
        }

        try
        {
            using var connection = OpenReadOnly(databasePath);
            if (!TableExists(connection, "raw_records") || !TableExists(connection, "source_schema_observations"))
            {
                return ClaudeSetupLedgerClassification.Unreadable;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM raw_records r JOIN source_schema_observations o ON o.raw_record_id=r.id " +
                "WHERE o.source_surface=$surface AND o.source_adapter=$adapter AND r.received_at >= $applied " +
                "AND r.received_at <= $now);";
            Add(command, "$surface", SourceSurface);
            Add(command, "$adapter", SourceAdapter);
            Add(command, "$applied", Timestamp(applied.UpdatedAt));
            Add(command, "$now", Timestamp(now));
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0
                ? ClaudeSetupLedgerClassification.AcceptedIngestAfterApply
                : ClaudeSetupLedgerClassification.AwaitingAcceptedIngest;
        }
        catch (Exception)
        {
            return ClaudeSetupLedgerClassification.Unreadable;
        }
    }

    private static ClaudeEndpointValueClassification ClassifyEndpoint(ResolvedValue value) =>
        value.Unreadable
            ? ClaudeEndpointValueClassification.Unreadable
            : value.Conflict
                ? ClaudeEndpointValueClassification.Conflict
            : value.Value is null
                ? ClaudeEndpointValueClassification.Absent
                : value.Value == value.Expected
                    ? ClaudeEndpointValueClassification.Match
                    : ClaudeEndpointValueClassification.Different;

    private static ClaudeProtocolValueClassification ClassifyProtocol(ResolvedValue value) =>
        value.Unreadable
            ? ClaudeProtocolValueClassification.Unreadable
            : value.Conflict
                ? ClaudeProtocolValueClassification.Conflict
            : value.Value is null
                ? ClaudeProtocolValueClassification.Absent
                : value.Value == value.Expected
                    ? ClaudeProtocolValueClassification.HttpProtobuf
                    : ClaudeProtocolValueClassification.Different;

    private static ClaudeGateValueClassification ClassifyGate(ResolvedValue value) =>
        value.Unreadable
            ? ClaudeGateValueClassification.Unreadable
            : value.Conflict
                ? ClaudeGateValueClassification.Conflict
            : value.Value is null
                ? ClaudeGateValueClassification.Absent
                : value.Value == value.Expected
                    ? ClaudeGateValueClassification.Enabled
                    : ClaudeGateValueClassification.Disabled;

    private static ClaudeEffectiveContentGate ClassifyContentGate(
        IReadOnlyDictionary<string, ResolvedValue> values)
    {
        var content = values
            .Where(pair => pair.Key is "OTEL_LOG_USER_PROMPTS" or "OTEL_LOG_TOOL_DETAILS" or "OTEL_LOG_TOOL_CONTENT")
            .Select(pair => pair.Value)
            .ToArray();
        return content.Any(value => value.Unreadable || value.Conflict)
            ? ClaudeEffectiveContentGate.Unreadable
            : content.Any(value => value.Value == value.Expected)
                ? ClaudeEffectiveContentGate.Enabled
                : ClaudeEffectiveContentGate.Disabled;
    }

    private static string NormalizeEndpoint(string? input, out bool valid)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            valid = true;
            return DefaultEndpoint;
        }

        var result = SetupOptions.Parse([
            "setup", "plan", "--adapter", SourceSurface, "--target", "cli", "--endpoint", input]);
        if (result.Options is null)
        {
            valid = false;
            return input;
        }

        valid = true;
        return result.Options.Endpoint!;
    }

    private static bool IsLiveResponse(SetupHttpProbeObservation observation)
    {
        if (observation.Outcome != SetupHttpProbeOutcome.Response ||
            observation.StatusCode != 200 ||
            observation.TrustworthyContentLength is < 0 or > MaximumProbeBodyBytes ||
            observation.Body is null ||
            observation.Body.Length > MaximumProbeBodyBytes ||
            !observation.IsComplete)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(observation.Body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.EnumerateObject().Count() == 1 &&
                root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString() == "live";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DefaultManagedFilePath(ISetupPlatform platform) =>
        platform.OperatingSystem.Current == SetupPlanningOs.Windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json")
            : "/etc/claude-code/managed-settings.json";

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        Add(command, "$name", name);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool ContainsCandidateIdentity(string payload, IReadOnlySet<string> candidates)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
            {
                foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
                {
                    foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                    {
                        var traceId = OtlpSpanReader.ReadString(span, "traceId");
                        var spanId = OtlpSpanReader.ReadString(span, "spanId");
                        if (IsLowerHex(traceId, 32) && IsLowerHex(spanId, 16) &&
                            candidates.Contains($"claude-otel-raw-{traceId}-{spanId}"))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryParseBindingReference(string value, out string traceId, out Guid sessionId)
    {
        const string prefix = "claude-otel-binding-";
        traceId = string.Empty;
        sessionId = Guid.Empty;
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 32 + 1 + 36)
        {
            return false;
        }

        traceId = value.Substring(prefix.Length, 32);
        return IsLowerHex(traceId, 32) &&
            Guid.TryParseExact(value[(prefix.Length + 33)..], "D", out sessionId);
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int CompletenessRank(ClaudeBoundSessionCompleteness value) => value switch
    {
        ClaudeBoundSessionCompleteness.Full => 4,
        ClaudeBoundSessionCompleteness.Rich => 3,
        ClaudeBoundSessionCompleteness.Partial => 2,
        ClaudeBoundSessionCompleteness.Unbound => 1,
        _ => 0,
    };

    private static DoctorEvidenceClass ParseEvidenceClass(string value) => value switch
    {
        "real_source" => DoctorEvidenceClass.RealSource,
        "synthetic_probe" => DoctorEvidenceClass.SyntheticProbe,
        _ => throw new InvalidOperationException(),
    };

    private static DoctorEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "ingest" => DoctorEvidenceKind.Ingest,
        "raw_persistence" => DoctorEvidenceKind.RawPersistence,
        "projection" => DoctorEvidenceKind.Projection,
        "exact_session_binding" => DoctorEvidenceKind.ExactSessionBinding,
        "completeness_content" => DoctorEvidenceKind.CompletenessContent,
        _ => throw new InvalidOperationException(),
    };

    private static SourceCompatibilityState ParseCompatibilityState(string value) => value switch
    {
        "supported" => SourceCompatibilityState.Supported,
        "supported_with_unknown_fields" => SourceCompatibilityState.SupportedWithUnknownFields,
        "schema_drift_detected" => SourceCompatibilityState.SchemaDriftDetected,
        "unsupported_source_version" => SourceCompatibilityState.UnsupportedSourceVersion,
        "recognized_record_drop_detected" => SourceCompatibilityState.RecognizedRecordDropDetected,
        "adapter_failure" => SourceCompatibilityState.AdapterFailure,
        _ => throw new InvalidOperationException(),
    };

    private static ClaudeBoundSessionCompleteness ParseCompleteness(string value) => value switch
    {
        "unbound" => ClaudeBoundSessionCompleteness.Unbound,
        "partial" => ClaudeBoundSessionCompleteness.Partial,
        "rich" => ClaudeBoundSessionCompleteness.Rich,
        "full" => ClaudeBoundSessionCompleteness.Full,
        _ => ClaudeBoundSessionCompleteness.Unavailable,
    };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static readonly ClaudeDoctorVerificationWindow EmptyWindow = new(
        false,
        false,
        false,
        false,
        ClaudeProjectionEvidence.NotStarted,
        false,
        ClaudeBoundSessionCompleteness.Unavailable,
        ClaudeAgreedContentState.None);

    private sealed record OwnedKey(string Key, string? Expected);

    private sealed record ResolvedValue(
        string? Value,
        bool Unreadable,
        string? Expected,
        bool Conflict = false);

    private sealed record SettingsSource(SettingsState State, IReadOnlyDictionary<string, string> Values)
    {
        public static SettingsSource Absent { get; } = new(SettingsState.Absent, new Dictionary<string, string>());

        public static SettingsSource Unreadable { get; } = new(SettingsState.Unreadable, new Dictionary<string, string>());

        public static SettingsSource Conflict { get; } = new(SettingsState.Conflict, new Dictionary<string, string>());
    }

    private sealed record EffectiveValues(
        ClaudeEndpointValueClassification Endpoint,
        ClaudeProtocolValueClassification Protocol,
        ClaudeGateValueClassification TelemetryGate,
        ClaudeGateValueClassification ExporterGate,
        ClaudeEffectiveContentGate ContentGate);

    private sealed record WindowRecord(long Id, DateTimeOffset ReceivedAt, string PayloadJson);

    private sealed record DatabaseRead(
        ClaudeSourceCompatibilityClassification SourceCompatibility,
        ClaudeRuntimeRawAccessClassification RuntimeRawAccess,
        ClaudeDoctorVerificationWindow? Window)
    {
        public static DatabaseRead Empty { get; } = new(
            ClaudeSourceCompatibilityClassification.NoRows,
            ClaudeRuntimeRawAccessClassification.Absent,
            null);
    }

    private enum SettingsState
    {
        Absent,
        Present,
        Unreadable,
        Conflict,
    }

    private sealed class ProbePlatform(ISetupPlatform inner, ISetupHttpProbe httpProbe) : ISetupPlatform
    {
        public string LocalApplicationData => inner.LocalApplicationData;
        public SetupPathStyle PathStyle => inner.PathStyle;
        public ISetupFileSystem FileSystem => inner.FileSystem;
        public ISetupUserEnvironment UserEnvironment => inner.UserEnvironment;
        public ISetupProcessEnvironment ProcessEnvironment => inner.ProcessEnvironment;
        public ISetupClock Clock => inner.Clock;
        public ISetupIdentifierGenerator Identifiers => inner.Identifiers;
        public ISetupExecution Execution => inner.Execution;
        public ISetupOperatingSystem OperatingSystem => inner.OperatingSystem;
        public ISetupProcessRunner ProcessRunner => inner.ProcessRunner;
        public ISetupManagedSettingsSource ManagedSettings => inner.ManagedSettings;
        public ISetupHttpProbe HttpProbe => httpProbe;
    }
}
