using System.Text.Json;

namespace CopilotAgentObservability.Alerts;

public static class AlertCenterReceiptConsumerV1
{
    public static AlertCenterReceiptProjectionV1 Validate(ReadOnlySpan<byte> canonicalReceipt) =>
        new(AlertReceiptConsumerV1.ValidateCanonicalReceipt(canonicalReceipt));
}

public sealed class AlertCenterReceiptProjectionV1
{
    internal AlertCenterReceiptProjectionV1(AlertReceipt receipt)
    {
        AlertId = receipt.AlertId;
        EvaluationId = receipt.EvaluationId;
        RuleId = receipt.RuleId;
        RuleVersion = receipt.RuleVersion;
        Severity = receipt.Severity;
        InitialState = receipt.InitialState;
        SourceSurface = receipt.SourceSurface;
        SourceVersion = receipt.SourceVersion;
        SessionId = receipt.SessionId;
        TraceId = receipt.TraceId;
        Evidence = Array.AsReadOnly(receipt.Evidence.Select(item => item with { }).ToArray());
        ObservedValues = Array.AsReadOnly(receipt.ObservedValues.Select(item => item with { }).ToArray());
        EffectiveThresholds = Array.AsReadOnly(receipt.EffectiveThresholds.Select(item => item with { }).ToArray());
        ConfigurationVersion = receipt.ConfigurationVersion;
        ConfigurationHash = receipt.ConfigurationHash;
        RequiredCapabilities = Array.AsReadOnly(receipt.RequiredCapabilities.ToArray());
        Completeness = receipt.Completeness;
        CompletenessReasons = Array.AsReadOnly(receipt.CompletenessReasons.ToArray());
        FirstObservedAt = receipt.FirstObservedAt;
        LastObservedAt = receipt.LastObservedAt;
        EvaluationInputHash = receipt.EvaluationInputHash;
        Summary = receipt.Summary;
    }

    public string AlertId { get; }
    public string EvaluationId { get; }
    public string RuleId { get; }
    public string RuleVersion { get; }
    public AlertSeverity Severity { get; }
    public AlertInitialState InitialState { get; }
    public string SourceSurface { get; }
    public string SourceVersion { get; }
    public string SessionId { get; }
    public string? TraceId { get; }
    public IReadOnlyList<AlertEvidenceReference> Evidence { get; }
    public IReadOnlyList<AlertObservedValue> ObservedValues { get; }
    public IReadOnlyList<AlertObservedValue> EffectiveThresholds { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public IReadOnlyList<string> RequiredCapabilities { get; }
    public AlertCompleteness Completeness { get; }
    public IReadOnlyList<string> CompletenessReasons { get; }
    public DateTimeOffset FirstObservedAt { get; }
    public DateTimeOffset LastObservedAt { get; }
    public string EvaluationInputHash { get; }
    public string Summary { get; }
}

public static class AlertSuppressionConsumerV1
{
    private const int MaximumCanonicalBytes = 8_388_608;
    private const int MaximumJsonDepth = 3;
    private static readonly string[] Properties =
        ["evaluation_id", "rule_id", "rule_version", "code", "missing_capabilities"];

    public static AlertSuppressionProjectionV1 Validate(ReadOnlySpan<byte> canonicalSuppression)
    {
        if (canonicalSuppression.Length is 0 or > MaximumCanonicalBytes)
        {
            throw new AlertSuppressionConsumerException();
        }

        try
        {
            using var document = JsonDocument.Parse(
                canonicalSuppression.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var root = document.RootElement;
            RequireObjectShape(root);
            var evaluationId = RequiredString(root, "evaluation_id");
            var ruleId = RequiredString(root, "rule_id");
            var ruleVersion = RequiredString(root, "rule_version");
            var code = RequiredString(root, "code");
            var missingCapabilities = RequiredTokens(root, "missing_capabilities");
            if (!CanonicalHash(evaluationId)
                || !AlertValidation.IsToken(ruleId)
                || !AlertValidation.IsToken(ruleVersion)
                || !AlertValidation.IsToken(code))
            {
                throw new AlertSuppressionFormatException();
            }

            var suppression = new AlertSuppression(
                evaluationId,
                ruleId,
                ruleVersion,
                code,
                Array.AsReadOnly(missingCapabilities));
            if (!canonicalSuppression.SequenceEqual(AlertCanonicalJson.SerializeSuppression(suppression)))
            {
                throw new AlertSuppressionFormatException();
            }

            return new AlertSuppressionProjectionV1(suppression);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw new AlertSuppressionConsumerException();
        }
    }

    private static void RequireObjectShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AlertSuppressionFormatException();
        }

        var seen = 0u;
        foreach (var property in root.EnumerateObject())
        {
            var index = Array.IndexOf(Properties, property.Name);
            if (index < 0)
            {
                throw new AlertSuppressionFormatException();
            }

            var flag = 1u << index;
            if ((seen & flag) != 0)
            {
                throw new AlertSuppressionFormatException();
            }

            seen |= flag;
        }

        if (seen != (1u << Properties.Length) - 1)
        {
            throw new AlertSuppressionFormatException();
        }
    }

    private static string RequiredString(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new AlertSuppressionFormatException();
    }

    private static string[] RequiredTokens(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new AlertSuppressionFormatException();
        }

        var tokens = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null).ToArray();
        if (tokens.Any(item => !AlertValidation.IsToken(item)))
        {
            throw new AlertSuppressionFormatException();
        }

        var result = tokens.Cast<string>().ToArray();
        if (!result.SequenceEqual(result.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new AlertSuppressionFormatException();
        }

        return result;
    }

    private static bool CanonicalHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class AlertSuppressionProjectionV1
{
    internal AlertSuppressionProjectionV1(AlertSuppression suppression)
    {
        EvaluationId = suppression.EvaluationId;
        RuleId = suppression.RuleId;
        RuleVersion = suppression.RuleVersion;
        Code = suppression.Code;
        MissingCapabilities = Array.AsReadOnly(suppression.MissingCapabilities.ToArray());
    }

    public string EvaluationId { get; }
    public string RuleId { get; }
    public string RuleVersion { get; }
    public string Code { get; }
    public IReadOnlyList<string> MissingCapabilities { get; }
}

public sealed class AlertSuppressionConsumerException : Exception
{
    internal AlertSuppressionConsumerException()
        : base("Alert suppression is invalid.")
    {
    }

    public string Code { get; } = "invalid_alert_suppression";
}

internal sealed class AlertSuppressionFormatException : Exception;
