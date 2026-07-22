using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.Alerts;

public static class AlertEvaluationConsumerV1
{
    private const int MaximumCanonicalBytes = 8_388_608;
    private const int MaximumJsonDepth = 8;
    private static readonly string[] RootProperties =
    [
        "schema_version", "evaluation_id", "input_hash", "configuration_version", "configuration_hash",
        "receipts", "suppressions", "rejected_matches",
    ];
    private static readonly string[] RejectedMatchProperties = ["rule_id", "rule_version", "code"];

    public static AlertEvaluationProjectionV1 Validate(ReadOnlySpan<byte> canonicalEvaluation)
    {
        var evaluation = ValidateCanonicalEvaluation(canonicalEvaluation);
        return new AlertEvaluationProjectionV1(
            evaluation.EvaluationId,
            evaluation.InputHash,
            evaluation.ConfigurationVersion,
            evaluation.ConfigurationHash,
            evaluation.Receipts.Count,
            evaluation.Suppressions.Count);
    }

    internal static AlertEvaluationResult ValidateCanonicalEvaluation(ReadOnlySpan<byte> canonicalEvaluation)
    {
        if (canonicalEvaluation.Length is 0 or > MaximumCanonicalBytes)
        {
            throw new AlertEvaluationConsumerException();
        }

        try
        {
            using var document = JsonDocument.Parse(
                canonicalEvaluation.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var root = document.RootElement;
            RequireExactProperties(root, RootProperties);
            var receipts = ParseReceipts(root.GetProperty("receipts"));
            var suppressions = ParseSuppressions(root.GetProperty("suppressions"));
            var rejectedMatches = ParseRejectedMatches(root.GetProperty("rejected_matches"));
            var evaluation = new AlertEvaluationResult(
                RequiredString(root, "schema_version"),
                RequiredString(root, "evaluation_id"),
                RequiredString(root, "input_hash"),
                RequiredString(root, "configuration_version"),
                RequiredString(root, "configuration_hash"),
                Array.AsReadOnly(receipts),
                Array.AsReadOnly(suppressions),
                Array.AsReadOnly(rejectedMatches));
            if (!IsValid(evaluation)
                || !canonicalEvaluation.SequenceEqual(AlertCanonicalJson.SerializeEvaluation(evaluation)))
            {
                throw new AlertEvaluationFormatException();
            }

            return evaluation;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw new AlertEvaluationConsumerException();
        }
    }

    private static bool IsValid(AlertEvaluationResult evaluation) =>
        evaluation.SchemaVersion == AlertContractVersions.Evaluation
        && CanonicalHash(evaluation.EvaluationId)
        && CanonicalHash(evaluation.InputHash)
        && AlertValidation.IsToken(evaluation.ConfigurationVersion)
        && CanonicalHash(evaluation.ConfigurationHash)
        && evaluation.Receipts.All(receipt =>
            receipt.EvaluationId == evaluation.EvaluationId
            && receipt.EvaluationInputHash == evaluation.InputHash
            && receipt.ConfigurationVersion == evaluation.ConfigurationVersion
            && receipt.ConfigurationHash == evaluation.ConfigurationHash)
        && evaluation.Receipts.Select(receipt => receipt.AlertId).Distinct(StringComparer.Ordinal).Count() == evaluation.Receipts.Count
        && evaluation.Suppressions.All(suppression => suppression.EvaluationId == evaluation.EvaluationId);

    private static AlertReceipt[] ParseReceipts(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        return element.EnumerateArray()
            .Select(item => AlertReceiptConsumerV1.ValidateCanonicalReceipt(RawBytes(item)))
            .ToArray();
    }

    private static AlertSuppression[] ParseSuppressions(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        return element.EnumerateArray()
            .Select(item => AlertSuppressionConsumerV1.ValidateCanonicalSuppression(RawBytes(item)))
            .ToArray();
    }

    private static AlertRejectedMatch[] ParseRejectedMatches(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        return element.EnumerateArray().Select(item =>
        {
            RequireExactProperties(item, RejectedMatchProperties);
            var rejected = new AlertRejectedMatch(
                RequiredString(item, "rule_id"),
                RequiredString(item, "rule_version"),
                RequiredString(item, "code"));
            if (!AlertValidation.IsToken(rejected.RuleId)
                || !AlertValidation.IsToken(rejected.RuleVersion)
                || !AlertValidation.IsToken(rejected.Code))
            {
                throw new AlertEvaluationFormatException();
            }

            return rejected;
        }).ToArray();
    }

    private static byte[] RawBytes(JsonElement element) => Encoding.UTF8.GetBytes(element.GetRawText());

    private static bool CanonicalHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw new AlertEvaluationFormatException();
    }

    private static void RequireExactProperties(JsonElement element, IReadOnlyList<string> expected)
    {
        RequireKind(element, JsonValueKind.Object);
        var seen = 0u;
        foreach (var property in element.EnumerateObject())
        {
            var index = IndexOf(expected, property.Name);
            if (index < 0)
            {
                throw new AlertEvaluationFormatException();
            }

            var flag = 1u << index;
            if ((seen & flag) != 0)
            {
                throw new AlertEvaluationFormatException();
            }

            seen |= flag;
        }

        if (seen != (1u << expected.Count) - 1)
        {
            throw new AlertEvaluationFormatException();
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind)
    {
        if (element.ValueKind != kind) throw new AlertEvaluationFormatException();
    }
}

public sealed class AlertEvaluationConsumerException : Exception
{
    internal AlertEvaluationConsumerException()
        : base("Alert evaluation is invalid.")
    {
    }

    public string Code { get; } = "invalid_alert_evaluation";
}

internal sealed class AlertEvaluationFormatException : Exception;
