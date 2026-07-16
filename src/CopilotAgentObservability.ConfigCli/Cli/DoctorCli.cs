using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli;

internal static partial class DoctorCli
{
    private const int MaximumInputBytes = 65_536;
    private const string CanonicalTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        IDoctorCliApplication? application = null)
    {
        var jsonOutput = args.Count(argument => argument == "--json") == 1;
        if (!DoctorCliOptions.TryParse(args, out var options) || options is null)
        {
            return WriteResult(Result(DoctorResultCode.InvalidArguments), jsonOutput, output, error);
        }

        application ??= SqliteDoctorCliApplication.Instance;
        DoctorResult result;
        try
        {
            result = options.Command switch
            {
                DoctorCliCommand.Evaluate => application.Evaluate(ReadFactSnapshot(options.InputPath!)),
                DoctorCliCommand.VerificationStart => application.Start(
                    options.DatabasePath!,
                    options.SourceSurface!,
                    options.SourceAdapter,
                    options.ExpiresAt!.Value),
                DoctorCliCommand.VerificationStatus => application.Status(
                    options.DatabasePath!,
                    options.VerificationId!),
                DoctorCliCommand.VerificationComplete => application.Complete(
                    options.DatabasePath!,
                    options.VerificationId!,
                    options.ExpectedRevision!.Value,
                    ReadCompletionInput(options.InputPath!)),
                DoctorCliCommand.VerificationCancel => application.Cancel(
                    options.DatabasePath!,
                    options.VerificationId!,
                    options.ExpectedRevision!.Value),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (DoctorUnsupportedSchemaException)
        {
            result = Result(DoctorResultCode.UnsupportedSchemaVersion);
        }
        catch (DoctorInputException)
        {
            result = Result(DoctorResultCode.InvalidInput);
        }
        catch
        {
            result = Result(DoctorResultCode.InternalError);
        }

        return WriteResult(result, options.JsonOutput, output, error);
    }

    private static DoctorFactSnapshot ReadFactSnapshot(string path)
    {
        try
        {
            var json = ReadBoundedUtf8(path);
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            RequireSupportedSchema(document.RootElement);
            ValidateFactSnapshotShape(document.RootElement);
            var snapshot = DoctorJson.DeserializeFactSnapshot(json);
            ValidateFactSnapshot(snapshot);
            if (snapshot.VerificationId is not null)
            {
                throw new DoctorInputException();
            }

            return snapshot;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException
            or DecoderFallbackException
            or DoctorInputException)
        {
            throw new DoctorInputException();
        }
    }

    private static DoctorCompletionInput ReadCompletionInput(string path)
    {
        try
        {
            var json = ReadBoundedUtf8(path);
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("fact_snapshot", out var snapshotElement)
                || snapshotElement.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("accepted_evidence_refs", out var referencesElement)
                || referencesElement.ValueKind != JsonValueKind.Array)
            {
                throw new DoctorInputException();
            }

            RequireSupportedSchema(snapshotElement);
            ValidateFactSnapshotShape(snapshotElement);
            var snapshot = DoctorJson.DeserializeFactSnapshot(snapshotElement.GetRawText());
            ValidateFactSnapshot(snapshot);
            if (snapshot.Observations.Count != 0)
            {
                throw new DoctorInputException();
            }

            var references = referencesElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                .ToArray();
            if (references.Length is < 1 or > 16
                || references.Any(reference => !DoctorValidation.IsValidEvidenceReference(reference))
                || references.Distinct(StringComparer.Ordinal).Count() != references.Length)
            {
                throw new DoctorInputException();
            }

            return new DoctorCompletionInput(snapshot, references!);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException
            or DecoderFallbackException
            or DoctorInputException)
        {
            throw new DoctorInputException();
        }
    }

    private static string ReadBoundedUtf8(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > MaximumInputBytes)
        {
            throw new DoctorInputException();
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, 0, count);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new DoctorInputException();
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void ValidateFactSnapshot(DoctorFactSnapshot snapshot)
    {
        if (!IsSourceToken(snapshot.SourceSurface)
            || snapshot.ExpectedSourceAdapter is not null && !IsSourceToken(snapshot.ExpectedSourceAdapter)
            || snapshot.VerificationId is not null && !IsCanonicalUuidV7(snapshot.VerificationId)
            || snapshot.Observations is null
            || snapshot.Observations.Count > 16
            || snapshot.Observations.Distinct().Count() != snapshot.Observations.Count
            || snapshot.Observations.Any(observation => observation is null
                || !string.Equals(observation.SourceSurface, snapshot.SourceSurface, StringComparison.Ordinal)
                || snapshot.ExpectedSourceAdapter is not null
                    && !string.Equals(observation.SourceAdapter, snapshot.ExpectedSourceAdapter, StringComparison.Ordinal)
                || observation.SourceAdapter is not null && !IsSourceToken(observation.SourceAdapter)
                || !DoctorValidation.IsValidEvidenceReference(observation.EvidenceRef))
            || snapshot.ExactSessionBinding is
                { Requirement: not ExactSessionBindingRequirement.NotRequired, Outcome: ExactSessionBindingOutcome.NotApplicable })
        {
            throw new DoctorInputException();
        }
    }

    private static void ValidateFactSnapshotShape(JsonElement root)
    {
        RequireProperties(
            root,
            [
                "schema_version",
                "source_surface",
                "observed_at",
                "observations",
                "install_and_source_version",
                "process_receiver_and_port",
                "source_effective_configuration",
                "endpoint_reachability",
                "protocol_and_signal_compatibility",
                "source_version_and_schema_diagnostics",
                "last_ingest",
                "raw_persistence",
                "projection",
                "exact_session_binding",
                "completeness_and_content",
                "restart_or_new_process",
            ],
            ["expected_source_adapter", "verification_id"]);

        RequireNullableFamily(root, "install_and_source_version", "monitor_install", "source_version", "source_feature");
        RequireNullableFamily(root, "process_receiver_and_port", "monitor_process", "receiver_bind", "port_owner");
        RequireNullableFamily(root, "source_effective_configuration", "endpoint_alignment");
        RequireNullableFamily(root, "endpoint_reachability", "reachability");
        RequireNullableFamily(root, "protocol_and_signal_compatibility", "protocol", "trace_signal");
        RequireNullableFamily(root, "source_version_and_schema_diagnostics", "compatibility", "schema");
        RequireNullableFamily(root, "last_ingest", "outcome");
        RequireNullableFamily(root, "raw_persistence", "outcome");
        RequireNullableFamily(root, "projection", "outcome");
        RequireNullableFamily(root, "exact_session_binding", "requirement", "outcome");
        RequireNullableFamily(root, "completeness_and_content", "completeness", "content_capture", "raw_access");
        RequireNullableFamily(root, "restart_or_new_process", "requirement");

        var observations = root.GetProperty("observations");
        if (observations.ValueKind != JsonValueKind.Array)
        {
            throw new DoctorInputException();
        }

        foreach (var observation in observations.EnumerateArray())
        {
            RequireExactProperties(
                observation,
                "source_surface",
                "source_adapter",
                "evidence_class",
                "evidence_kind",
                "evidence_ref",
                "observed_at");
        }
    }

    private static void RequireNullableFamily(JsonElement root, string propertyName, params string[] propertyNames)
    {
        var family = root.GetProperty(propertyName);
        if (family.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireExactProperties(family, propertyNames);
    }

    private static void RequireSupportedSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(schemaElement.GetString()))
        {
            throw new DoctorInputException();
        }

        if (!string.Equals(schemaElement.GetString(), DoctorSchemaVersions.FactsV1, StringComparison.Ordinal))
        {
            throw new DoctorUnsupportedSchemaException();
        }
    }

    private static void RequireProperties(
        JsonElement element,
        IReadOnlyCollection<string> requiredPropertyNames,
        IReadOnlyCollection<string> optionalPropertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DoctorInputException();
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (requiredPropertyNames.Any(propertyName => !actual.Contains(propertyName))
            || actual.Any(propertyName => !requiredPropertyNames.Contains(propertyName)
                && !optionalPropertyNames.Contains(propertyName)))
        {
            throw new DoctorInputException();
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DoctorInputException();
        }

        var expected = propertyNames.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || actual.Any(propertyName => !expected.Contains(propertyName)))
        {
            throw new DoctorInputException();
        }
    }

    private static bool IsSourceToken(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || !IsLowerAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character => IsLowerAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsCanonicalUuidV7(string value) =>
        value.Length == 36
        && Guid.TryParseExact(value, "D", out var parsed)
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static int WriteResult(DoctorResult result, bool jsonOutput, TextWriter output, TextWriter error)
    {
        output.WriteLine(jsonOutput ? DoctorJson.SerializeResult(result) : DoctorHumanProjector.Project(result));
        if (!result.Success)
        {
            error.WriteLine(ToWireValue(result.Code));
        }

        return result.Code switch
        {
            DoctorResultCode.EvaluationCompleted
                when result.Evaluation?.PrimaryState?.StateCode == DoctorStateCode.FirstTraceReady => 0,
            DoctorResultCode.VerificationStarted
                or DoctorResultCode.VerificationActive
                or DoctorResultCode.VerificationCompleted
                or DoctorResultCode.VerificationCancelled => 0,
            DoctorResultCode.EvaluationCompleted or DoctorResultCode.PartialFactSnapshot => 3,
            DoctorResultCode.InvalidArguments
                or DoctorResultCode.InvalidInput
                or DoctorResultCode.UnsupportedSchemaVersion => 2,
            DoctorResultCode.VerificationNotFound
                or DoctorResultCode.VerificationStale
                or DoctorResultCode.VerificationExpired
                or DoctorResultCode.VerificationAlreadyCancelled
                or DoctorResultCode.VerificationAlreadyCompleted
                or DoctorResultCode.ExpectedSourceMismatch
                or DoctorResultCode.EvidenceNotFound
                or DoctorResultCode.EvidenceExpired => 4,
            DoctorResultCode.DoctorStoreBusy
                or DoctorResultCode.DoctorStoreUnavailable
                or DoctorResultCode.InternalError => 5,
            _ => 5,
        };
    }

    private static DoctorResult Result(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, Success: false, code, Evaluation: null, Verification: null);

    private static string ToWireValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private enum DoctorCliCommand
    {
        Evaluate,
        VerificationStart,
        VerificationStatus,
        VerificationComplete,
        VerificationCancel,
    }

    private sealed record DoctorCliOptions(
        DoctorCliCommand Command,
        string? DatabasePath,
        string? InputPath,
        string? SourceSurface,
        string? SourceAdapter,
        string? VerificationId,
        int? ExpectedRevision,
        DateTimeOffset? ExpiresAt,
        bool JsonOutput)
    {
        public static bool TryParse(string[] args, out DoctorCliOptions? options)
        {
            options = null;
            if (args.Length == 0)
            {
                return false;
            }

            if (args[0] == "evaluate")
            {
                return TryParseCommand(args, 1, DoctorCliCommand.Evaluate, ["--input"], [], out options);
            }

            if (args.Length < 2 || args[0] != "verification")
            {
                return false;
            }

            return args[1] switch
            {
                "start" => TryParseCommand(
                    args,
                    2,
                    DoctorCliCommand.VerificationStart,
                    ["--database", "--source-surface", "--expires-at"],
                    ["--source-adapter"],
                    out options),
                "status" => TryParseCommand(
                    args,
                    2,
                    DoctorCliCommand.VerificationStatus,
                    ["--database", "--verification-id"],
                    [],
                    out options),
                "complete" => TryParseCommand(
                    args,
                    2,
                    DoctorCliCommand.VerificationComplete,
                    ["--database", "--verification-id", "--expected-revision", "--input"],
                    [],
                    out options),
                "cancel" => TryParseCommand(
                    args,
                    2,
                    DoctorCliCommand.VerificationCancel,
                    ["--database", "--verification-id", "--expected-revision"],
                    [],
                    out options),
                _ => false,
            };
        }

        private static bool TryParseCommand(
            string[] args,
            int startIndex,
            DoctorCliCommand command,
            IReadOnlyList<string> requiredOptions,
            IReadOnlyList<string> optionalOptions,
            out DoctorCliOptions? options)
        {
            options = null;
            var allowed = requiredOptions.Concat(optionalOptions).ToHashSet(StringComparer.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var jsonOutput = false;
            for (var index = startIndex; index < args.Length; index++)
            {
                var name = args[index];
                if (name == "--json")
                {
                    if (jsonOutput)
                    {
                        return false;
                    }

                    jsonOutput = true;
                    continue;
                }

                if (!allowed.Contains(name)
                    || values.ContainsKey(name)
                    || index + 1 >= args.Length
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    return false;
                }

                values.Add(name, args[++index]);
            }

            if (requiredOptions.Any(required => !values.ContainsKey(required)))
            {
                return false;
            }

            values.TryGetValue("--database", out var databasePath);
            values.TryGetValue("--input", out var inputPath);
            values.TryGetValue("--source-surface", out var sourceSurface);
            values.TryGetValue("--source-adapter", out var sourceAdapter);
            values.TryGetValue("--verification-id", out var verificationId);
            values.TryGetValue("--expected-revision", out var revisionValue);
            values.TryGetValue("--expires-at", out var expiresAtValue);
            var revision = 0;
            var expiresAt = default(DateTimeOffset);
            if (sourceSurface is not null && !IsSourceToken(sourceSurface)
                || sourceAdapter is not null && !IsSourceToken(sourceAdapter)
                || verificationId is not null && !IsCanonicalUuidV7(verificationId)
                || revisionValue is not null && (!int.TryParse(revisionValue, NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision <= 0)
                || expiresAtValue is not null && !DateTimeOffset.TryParseExact(
                    expiresAtValue,
                    CanonicalTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out expiresAt))
            {
                return false;
            }

            options = new DoctorCliOptions(
                command,
                databasePath,
                inputPath,
                sourceSurface,
                sourceAdapter,
                verificationId,
                revisionValue is null ? null : revision,
                expiresAtValue is null ? null : expiresAt,
                jsonOutput);
            return true;
        }
    }

    private sealed class DoctorInputException : Exception;

    private sealed class DoctorUnsupportedSchemaException : Exception;
}
