using System.Globalization;

namespace CopilotAgentObservability.LocalMonitor.Presentation;

internal enum FactState
{
    ObservedPositive,
    ObservedZero,
    NotObserved,
    Unsupported,
    CaptureGap,
    CertificationPending,
    RawNotCaptured,
    RawExpired,
    Inconsistent,
}

internal readonly record struct RecordedFactCount(ulong Value);

internal sealed record FactStateExplanation
{
    internal const int MaximumSourceTextLength = 80;
    internal const int MaximumReasonTextLength = 240;

    private enum ExplanationTextKind
    {
        Source,
        Reason,
    }

    internal FactStateExplanation(
        string? SourceText = null,
        string? ReasonText = null)
    {
        this.SourceText = Normalize(
            SourceText,
            MaximumSourceTextLength,
            nameof(SourceText),
            ExplanationTextKind.Source);
        this.ReasonText = Normalize(
            ReasonText,
            MaximumReasonTextLength,
            nameof(ReasonText),
            ExplanationTextKind.Reason);
    }

    internal string? SourceText { get; }

    internal string? ReasonText { get; }

    private static string? Normalize(
        string? value,
        int maximumLength,
        string parameterName,
        ExplanationTextKind textKind)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null)
        {
            return null;
        }
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Explanation text must be plain text.", parameterName);
        }
        var containsReservedToken = textKind switch
        {
            ExplanationTextKind.Source =>
                ReservedContractTokenBoundary.ContainsInSourceText(normalized),
            ExplanationTextKind.Reason =>
                ReservedContractTokenBoundary.ContainsInReasonText(normalized),
            _ => throw new ArgumentOutOfRangeException(nameof(textKind)),
        };
        if (containsReservedToken)
        {
            throw new ArgumentException(
                "Explanation text contains reserved contract vocabulary.",
                parameterName);
        }

        return normalized;
    }
}

internal sealed record FactStatePresentationRequest(
    FactState State,
    RecordedFactCount? RecordedCount = null,
    bool HasCompleteCoverageProof = false,
    FactStateExplanation? Explanation = null);

internal sealed class FactStatePresentation
{
    private const string NotObservedPrimary = "今回の記録にはありません";
    private const string NotObservedDetail =
        "この記録では呼び出しを確認できませんでした。実際に使われなかったとは断定できません。";

    private FactStatePresentation(
        string primaryText,
        string? detailText,
        bool allowsDerivedVisualization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryText);
        if (ReservedContractTokenBoundary.ContainsInRenderedText(primaryText)
            || (detailText is not null
                && ReservedContractTokenBoundary.ContainsInRenderedText(detailText)))
        {
            throw new ArgumentException(
                "Rendered presentation text contains reserved contract vocabulary.",
                nameof(primaryText));
        }

        PrimaryText = primaryText;
        DetailText = detailText;
        AllowsDerivedVisualization = allowsDerivedVisualization;
    }

    public string PrimaryText { get; }

    public string? DetailText { get; }

    public bool AllowsDerivedVisualization { get; }

    internal static FactStatePresentation Resolve(FactStatePresentationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = request.RecordedCount?.Value;
        Validate(request, count);

        var (primaryText, fixedDetailText, allowsDerivedVisualization) =
            request.State switch
        {
            FactState.ObservedPositive => (PositiveCountText(count!.Value), null, true),
            FactState.ObservedZero when !request.HasCompleteCoverageProof =>
                (NotObservedPrimary, NotObservedDetail, false),
            FactState.ObservedZero => ("0件", null, true),
            FactState.NotObserved => (NotObservedPrimary, NotObservedDetail, false),
            FactState.Unsupported => ("この取得元では記録できません", null, false),
            FactState.CaptureGap => ("記録が一部欠けています", null, false),
            FactState.CertificationPending =>
                (PositiveCountText(count!.Value), "安定して取得できるか未確認です。", true),
            FactState.RawNotCaptured => ("内容は記録されていません", null, false),
            FactState.RawExpired => ("保存期間を過ぎたため表示できません", null, false),
            FactState.Inconsistent =>
                ("内訳を表示できません", "記録された値に整合しない項目があります。", false),
            _ => throw InvalidRequest(),
        };

        return new(
            primaryText,
            Detail(fixedDetailText, request.Explanation),
            allowsDerivedVisualization);
    }

    private static void Validate(
        FactStatePresentationRequest request,
        ulong? count)
    {
        if (request.State != FactState.ObservedZero
            && request.HasCompleteCoverageProof)
        {
            throw InvalidRequest();
        }

        var explanation = request.Explanation;
        switch (request.State)
        {
            case FactState.ObservedPositive:
            case FactState.CertificationPending:
                if (count is null or 0)
                {
                    throw InvalidRequest();
                }
                break;
            case FactState.ObservedZero:
                if (count is not 0
                    || request.HasCompleteCoverageProof
                    && (explanation?.SourceText is null
                        || explanation.ReasonText is null))
                {
                    throw InvalidRequest();
                }
                break;
            case FactState.Unsupported:
                if (count is not null
                    || explanation?.SourceText is null
                    || explanation.ReasonText is null)
                {
                    throw InvalidRequest();
                }
                break;
            case FactState.CaptureGap:
            case FactState.RawNotCaptured:
            case FactState.RawExpired:
                if (count is not null
                    || explanation?.ReasonText is null)
                {
                    throw InvalidRequest();
                }
                break;
            case FactState.NotObserved:
            case FactState.Inconsistent:
                if (count is not null)
                {
                    throw InvalidRequest();
                }
                break;
            default:
                throw InvalidRequest();
        }
    }

    private static string PositiveCountText(ulong count) =>
        string.Concat(count.ToString(CultureInfo.InvariantCulture), "件を記録");

    private static string? Detail(
        string? fixedText = null,
        FactStateExplanation? explanation = null)
    {
        var parts = new List<string>(3);
        AddSentence(parts, fixedText);
        if (explanation?.SourceText is { } source)
        {
            parts.Add($"取得元: {source}。");
        }
        AddSentence(parts, explanation?.ReasonText);
        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static void AddSentence(List<string> parts, string? value)
    {
        if (value is null)
        {
            return;
        }

        parts.Add(value.EndsWith('。') ? value : $"{value}。");
    }

    private static ArgumentException InvalidRequest() =>
        new("The explicit fact state and presentation facts are inconsistent.", "request");
}

internal static class ReservedContractTokenBoundary
{
    private static readonly string[] WholeFieldTokens =
    [
        "ObservedPositive",
        "ObservedZero",
        "NotObserved",
        "Unsupported",
        "CaptureGap",
        "CertificationPending",
        "RawNotCaptured",
        "NotCaptured",
        "RawExpired",
        "Inconsistent",
        "ProjectionInvalid",
        "ExpiredPendingDeletion",
        "Malformed",
        "Oversized",
        "Redacted",
    ];

    private static readonly string[] IdentifierTokens =
    [
        "observed_positive",
        "observed-positive",
        "observed_zero",
        "observed-zero",
        "not_observed",
        "not-observed",
        "capture_gap",
        "capture-gap",
        "certification_pending",
        "certification-pending",
        "raw_not_captured",
        "raw-not-captured",
        "not_captured",
        "not-captured",
        "raw_expired",
        "raw-expired",
        "projection_invalid",
        "projection-invalid",
        "expired_pending_deletion",
        "expired-pending-deletion",
    ];

    internal static bool ContainsInSourceText(string value) =>
        WholeFieldTokens.Any(
            token => string.Equals(value, token, StringComparison.OrdinalIgnoreCase))
        || IdentifierTokens.Any(token => ContainsIdentifier(value, token));

    internal static bool ContainsInReasonText(string value) =>
        IdentifierTokens.Any(token => ContainsIdentifier(value, token))
        || WholeFieldTokens.Any(token => ContainsIdentifier(value, token));

    internal static bool ContainsInRenderedText(string value) =>
        ContainsInSourceText(value);

    private static bool ContainsIdentifier(
        string value,
        string token)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var index = value.IndexOf(
                token,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + token.Length;
            var startsAtBoundary = index == 0
                || !IsAsciiIdentifierCharacter(value[index - 1]);
            var endsAtBoundary = end == value.Length
                || !IsAsciiIdentifierCharacter(value[end]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            searchStart = index + 1;
        }

        return false;
    }

    private static bool IsAsciiIdentifierCharacter(char value) =>
        value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-';
}
