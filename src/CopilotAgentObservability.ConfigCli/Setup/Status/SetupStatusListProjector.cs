using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Status;

internal static class SetupStatusListProjector
{
    private const int MaximumChangeSets = 100;

    private static readonly Regex CanonicalAdapterIdentifier = new(
        "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant);

    public static SetupCommandResult Project(
        SetupCommandResult statusResult,
        IReadOnlyList<SetupLedgerChangeSet> changeSets,
        string? adapterFilter,
        string? changeSetIdFilter,
        Func<SetupLedgerChangeSet, SetupChangeSetStatusResult> projectChangeSet)
    {
        ArgumentNullException.ThrowIfNull(statusResult);
        ArgumentNullException.ThrowIfNull(changeSets);
        ArgumentNullException.ThrowIfNull(projectChangeSet);

        if (statusResult.Command != SetupCommand.Status)
        {
            throw new ArgumentException("A status result is required.", nameof(statusResult));
        }

        ValidateAdapterFilter(adapterFilter);
        var changeSetId = ParseChangeSetIdFilter(changeSetIdFilter);
        var eligible = changeSets
            .Select(RequireValidChangeSet)
            .Where(changeSet => adapterFilter is null ||
                string.Equals(changeSet.Adapter, adapterFilter, StringComparison.Ordinal))
            .Where(changeSet => changeSetId is null || changeSet.ChangeSetId == changeSetId)
            .OrderBy(Priority)
            .ThenByDescending(changeSet => changeSet.UpdatedAt)
            .ThenBy(changeSet => changeSet.ChangeSetId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var returned = eligible
            .Take(MaximumChangeSets)
            .Select(projectChangeSet)
            .ToArray();

        return statusResult with
        {
            Adapter = adapterFilter,
            ChangeSets = returned,
            Truncated = eligible.Length > returned.Length,
        };
    }

    private static SetupLedgerChangeSet RequireValidChangeSet(SetupLedgerChangeSet? changeSet)
    {
        if (changeSet is null ||
            !IsCanonicalAdapterIdentifier(changeSet.Adapter) ||
            !IsUuidV7(changeSet.ChangeSetId) ||
            !Enum.IsDefined(changeSet.State))
        {
            throw new FormatException();
        }

        return changeSet;
    }

    private static void ValidateAdapterFilter(string? adapterFilter)
    {
        if (adapterFilter is not null && !IsCanonicalAdapterIdentifier(adapterFilter))
        {
            throw new FormatException();
        }
    }

    private static bool IsCanonicalAdapterIdentifier(string? value) =>
        value is { Length: >= 1 and <= 128 } && CanonicalAdapterIdentifier.IsMatch(value);

    private static Guid? ParseChangeSetIdFilter(string? changeSetIdFilter)
    {
        if (changeSetIdFilter is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(changeSetIdFilter, "D", out var changeSetId) ||
            !string.Equals(changeSetId.ToString("D"), changeSetIdFilter, StringComparison.Ordinal) ||
            !IsUuidV7(changeSetId))
        {
            throw new FormatException();
        }

        return changeSetId;
    }

    private static int Priority(SetupLedgerChangeSet changeSet) => changeSet.State switch
    {
        SetupChangeSetState.Partial or
        SetupChangeSetState.Applying or
        SetupChangeSetState.Compensating or
        SetupChangeSetState.RollingBack => 0,
        SetupChangeSetState.Planned => 1,
        SetupChangeSetState.Applied or
        SetupChangeSetState.NoChanges or
        SetupChangeSetState.Restored or
        SetupChangeSetState.RolledBack => 2,
        _ => throw new FormatException(),
    };

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}
