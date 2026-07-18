using System.Globalization;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;

namespace CopilotAgentObservability.ConfigCli.FirstTrace;

internal static class FirstTraceCli
{
    private const string CanonicalTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        FirstTraceOrchestrator? orchestrator = null)
    {
        var jsonOutput = args.Count(argument => argument == "--json") == 1;
        orchestrator ??= FirstTraceCompositionRoot.Create();
        if (!TryParse(args, out var options) || options is null)
        {
            return WriteResult(
                Invalid(options?.Command ?? (args.Length > 0 ? args[0] : "")),
                jsonOutput,
                output,
                error);
        }

        var envelope = orchestrator.Execute(options);
        return WriteResult(envelope, options.JsonOutput, output, error);
    }

    private static int WriteResult(
        FirstTraceEnvelope envelope,
        bool jsonOutput,
        TextWriter output,
        TextWriter error)
    {
        output.WriteLine(jsonOutput
            ? FirstTraceJson.Serialize(envelope)
            : FirstTraceHumanProjector.Project(envelope));
        if (!envelope.Success)
        {
            error.WriteLine(envelope.Code);
        }

        return envelope.Code switch
        {
            FirstTraceCodes.VerificationStarted
                or FirstTraceCodes.StatusReported
                or FirstTraceCodes.Completed
                or FirstTraceCodes.Cancelled => 0,
            FirstTraceCodes.Blocked
                or FirstTraceCodes.ActiveVerificationExists
                or FirstTraceCodes.NotReady
                or FirstTraceCodes.ExplicitEvidenceSelectionRequired => 3,
            FirstTraceCodes.InvalidArguments => 2,
            FirstTraceCodes.DoctorFailed => DoctorExitCode(envelope.Doctor),
            _ => 5,
        };
    }

    private static int DoctorExitCode(DoctorResult? result) => result?.Code switch
    {
        DoctorResultCode.InvalidArguments
            or DoctorResultCode.InvalidInput
            or DoctorResultCode.UnsupportedSchemaVersion => 2,
        DoctorResultCode.EvaluationCompleted
            or DoctorResultCode.PartialFactSnapshot => 3,
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

    private static FirstTraceEnvelope Invalid(string command) => new(
        command,
        Success: false,
        FirstTraceCodes.InvalidArguments,
        Adapter: null,
        SourceSurface: null,
        VerificationId: null,
        Doctor: null,
        EvaluationPreview: null,
        Guidance: [],
        Candidates: [],
        Truncated: false);

    private sealed record FirstTraceCliOptions(
        string Command,
        string DatabasePath,
        string? Adapter,
        string? VerificationId,
        int? ExpectedRevision,
        string? Endpoint,
        string? Interaction,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> EvidenceRefs,
        bool JsonOutput) : FirstTraceRequest(
            Command,
            DatabasePath,
            Adapter,
            VerificationId,
            ExpectedRevision,
            Endpoint,
            Interaction,
            ExpiresAt,
            EvidenceRefs);

    private static bool TryParseCommand(
        string[] args,
        int startIndex,
        string command,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional,
        bool repeatEvidence,
        out FirstTraceCliOptions? options)
    {
        options = null;
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var evidence = new List<string>();
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

            if (name == "--evidence" && repeatEvidence)
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    return false;
                }

                evidence.Add(args[++index]);
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

        if (required.Any(requiredName => !values.ContainsKey(requiredName)))
        {
            return false;
        }

        values.TryGetValue("--database", out var database);
        values.TryGetValue("--adapter", out var adapter);
        values.TryGetValue("--verification-id", out var verificationId);
        values.TryGetValue("--expected-revision", out var revisionValue);
        values.TryGetValue("--endpoint", out var endpoint);
        values.TryGetValue("--interaction", out var interaction);
        values.TryGetValue("--expires-at", out var expiresAtValue);

        if (!TryParseRevision(revisionValue, out var revision)
            || !TryParseTimestamp(expiresAtValue, out var expiresAt)
            || verificationId is not null && !DoctorValidation.IsUuidV7(verificationId)
            || evidence.Count > DoctorValidation.MaximumAcceptedEvidenceReferences
            || evidence.Distinct(StringComparer.Ordinal).Count() != evidence.Count
            || evidence.Any(reference => !DoctorValidation.IsValidEvidenceReference(reference)))
        {
            return false;
        }

        options = new FirstTraceCliOptions(
            command,
            database!,
            adapter,
            verificationId,
            revisionValue is null ? null : revision,
            endpoint,
            interaction,
            expiresAtValue is null ? null : expiresAt,
            evidence,
            jsonOutput);
        return true;
    }

    private static bool TryParseRevision(string? value, out int revision)
    {
        revision = 0;
        return value is null
            || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision > 0;
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value is null || DateTimeOffset.TryParseExact(
            value,
            CanonicalTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp) && timestamp.Offset == TimeSpan.Zero;
    }

    private static bool TryParse(string[] args, out FirstTraceCliOptions? options)
    {
        options = null;
        if (args.Length == 0)
        {
            return false;
        }

        return args[0] switch
        {
            "begin" => TryParseCommand(
                args,
                1,
                "begin",
                ["--database", "--adapter"],
                ["--endpoint", "--interaction", "--expires-at"],
                repeatEvidence: false,
                out options),
            "status" => TryParseCommand(
                args,
                1,
                "status",
                ["--database", "--verification-id"],
                ["--endpoint"],
                repeatEvidence: false,
                out options),
            "complete" => TryParseCommand(
                args,
                1,
                "complete",
                ["--database", "--verification-id", "--expected-revision"],
                ["--endpoint", "--evidence"],
                repeatEvidence: true,
                out options),
            "cancel" => TryParseCommand(
                args,
                1,
                "cancel",
                ["--database", "--verification-id", "--expected-revision"],
                [],
                repeatEvidence: false,
                out options),
            _ => false,
        };
    }
}
