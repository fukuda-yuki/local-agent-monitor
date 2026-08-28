using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalComparisonFactState
{
    Recorded,
    ExplicitZero,
    NotObserved,
    SourceUnsupported,
    CaptureGap,
    CertificationPending,
    NotCaptured,
    Expired,
    Deleted,
    ReadDenied,
    Inconsistent,
    ProjectionInvalid,
    TooLarge,
}

internal sealed record LocalComparisonScalarObservation
{
    public LocalComparisonScalarObservation(
        LocalComparisonFactState state,
        decimal? value)
    {
        if ((state == LocalComparisonFactState.Recorded && (value is null || value == 0m))
            || (state == LocalComparisonFactState.ExplicitZero && value != 0m)
            || (state is not LocalComparisonFactState.Recorded
                    and not LocalComparisonFactState.ExplicitZero
                && value is not null))
        {
            throw new ArgumentException("local_comparison_scalar_state_invalid");
        }

        State = state;
        Value = value;
    }

    public LocalComparisonFactState State { get; }
    public decimal? Value { get; }
}

internal sealed record LocalComparisonScalarSummary(
    int SessionCount,
    int AvailableCount,
    decimal? Median,
    decimal? Minimum,
    decimal? Maximum,
    decimal? Total);

internal sealed record LocalComparisonScalarDifference(
    decimal? Absolute,
    decimal? RelativePercent);

internal static class LocalComparisonScalarCalculator
{
    internal static LocalComparisonScalarSummary Summarize(
        IReadOnlyList<LocalComparisonScalarObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var values = observations
            .Where(static observation => observation.Value.HasValue)
            .Select(static observation => observation.Value!.Value)
            .Order()
            .ToArray();
        if (values.Length == 0)
        {
            return new(observations.Count, 0, null, null, null, null);
        }

        var middle = values.Length / 2;
        var median = values.Length % 2 == 0
            ? checked(values[middle - 1]
                + (values[middle] - values[middle - 1]) / 2m)
            : values[middle];
        return new(
            observations.Count,
            values.Length,
            median,
            values[0],
            values[^1],
            values.Aggregate(0m, static (sum, value) => checked(sum + value)));
    }

    internal static LocalComparisonScalarDifference Difference(
        decimal? cohortA,
        decimal? cohortB)
    {
        if (cohortA is null || cohortB is null)
            return new(null, null);
        var absolute = checked(cohortB.Value - cohortA.Value);
        decimal? relative = cohortA.Value > 0m
            ? Math.Round(
                checked(absolute / cohortA.Value * 100m),
                1,
                MidpointRounding.AwayFromZero)
            : null;
        return new(absolute, relative);
    }

    internal static string CanonicalDecimal(decimal value)
    {
        if (value == decimal.Zero)
            return "0";
        return value.ToString("0.#############################", CultureInfo.InvariantCulture);
    }
}

internal sealed record LocalComparisonSelection(
    byte[] Bytes,
    string Sha256,
    IReadOnlyList<string> CohortA,
    IReadOnlyList<string> CohortB);

internal static class LocalComparisonSelectionFrame
{
    private const string Domain =
        "copilot-agent-observability/local-comparison-selection/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static LocalComparisonSelection Create(
        IReadOnlyList<string> cohortA,
        IReadOnlyList<string> cohortB)
    {
        ArgumentNullException.ThrowIfNull(cohortA);
        ArgumentNullException.ThrowIfNull(cohortB);
        if (cohortA.Count is < 1 or > 199
            || cohortB.Count is < 1 or > 199
            || cohortA.Count + cohortB.Count > 200)
        {
            throw new ArgumentException("local_comparison_selection_invalid");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var a = Freeze(cohortA, seen);
        var b = Freeze(cohortB, seen);
        using var stream = new MemoryStream(256 + (a.Length + b.Length) * 40);
        WriteFrame(stream, Domain);
        WriteFrame(stream, "a");
        WriteFrame(stream, a.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var sessionId in a)
            WriteFrame(stream, sessionId);
        WriteFrame(stream, "b");
        WriteFrame(stream, b.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var sessionId in b)
            WriteFrame(stream, sessionId);
        var bytes = stream.ToArray();
        return new LocalComparisonSelection(
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Array.AsReadOnly(a),
            Array.AsReadOnly(b));
    }

    private static string[] Freeze(
        IReadOnlyList<string> source,
        HashSet<string> seen)
    {
        var result = new string[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var value = source[index];
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value)
                || !seen.Add(value))
            {
                throw new ArgumentException("local_comparison_selection_invalid");
            }
            result[index] = value;
        }
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    internal static void WriteFrame(Stream destination, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            length,
            checked((uint)bytes.Length));
        destination.Write(length);
        destination.Write(bytes);
    }

    internal static void WriteFrame(Stream destination, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            length,
            checked((uint)value.Length));
        destination.Write(length);
        destination.Write(value);
    }
}
