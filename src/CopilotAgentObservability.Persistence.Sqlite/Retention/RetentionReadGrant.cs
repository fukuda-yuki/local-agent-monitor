using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionReadGrant
{
    private static readonly string[] AdmissionSelectorCapabilityParameterNames =
    [
        "$retention_read_source_token",
        "$retention_read_item_id",
        "$retention_read_revision",
        "$retention_read_lease_kind",
        "$retention_read_lease_owner",
        "$retention_read_lease_generation",
        "$retention_read_lease_expires_at",
    ];

    private static readonly string[] PostCommitGrantUsabilityParameterNames =
    [
        "$retention_read_source_token",
        "$retention_read_item_id",
        "$retention_read_lease_kind",
        "$retention_read_lease_owner",
        "$retention_read_lease_generation",
        "$retention_read_lease_expires_at",
    ];

    private readonly object leasePublicationGate = new();
    private readonly byte[] sourceToken;
    private DateTimeOffset publishedLeaseExpiresAt;

    internal RetentionReadGrant(
        RetentionOwnershipKey ownershipKey,
        string itemId,
        long admissionRevision,
        RetentionLeaseKind leaseKind,
        string leaseOwner,
        long leaseGeneration,
        DateTimeOffset leaseExpiresAt,
        byte[] sourceToken)
    {
        ArgumentNullException.ThrowIfNull(ownershipKey);
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(leaseOwner);
        ArgumentNullException.ThrowIfNull(sourceToken);
        if (sourceToken.Length != 32) throw new ArgumentException("Source token must be 32 bytes.", nameof(sourceToken));
        OwnershipKey = ownershipKey;
        ItemId = itemId;
        AdmissionRevision = admissionRevision;
        LeaseKind = leaseKind;
        LeaseOwner = leaseOwner;
        LeaseGeneration = leaseGeneration;
        LeaseExpiresAt = leaseExpiresAt;
        publishedLeaseExpiresAt = leaseExpiresAt;
        this.sourceToken = sourceToken.ToArray();
    }

    internal RetentionOwnershipKey OwnershipKey { get; }
    internal string ItemId { get; }
    internal long AdmissionRevision { get; }
    internal RetentionLeaseKind LeaseKind { get; }
    internal string LeaseOwner { get; }
    internal long LeaseGeneration { get; }
    internal DateTimeOffset LeaseExpiresAt { get; }

    internal void AdvanceExpiry(DateTimeOffset expiry)
    {
        using var publication = EnterLeasePublication();
        publication.AdvanceExpiry(expiry);
    }

    // Database participants acquire their immediate transaction before retaining this scope.
    internal LeasePublication EnterLeasePublication() => new(this);

    internal bool TryEnterLeasePublication(out LeasePublication publication)
    {
        if (!Monitor.TryEnter(leasePublicationGate))
        {
            publication = null!;
            return false;
        }

        publication = LeasePublication.FromEnteredGate(this);
        return true;
    }

    internal bool TryBindAdmissionSelectorCapability(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Monitor.TryEnter(leasePublicationGate))
            return false;

        using var publication = LeasePublication.FromEnteredGate(this);
        publication.BindAdmissionSelectorCapability(command);
        return true;
    }

    internal void BindAdmissionSelectorCapability(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var publication = EnterLeasePublication();
        publication.BindAdmissionSelectorCapability(command);
    }

    internal sealed class LeasePublication : IDisposable
    {
        private RetentionReadGrant? owner;

        internal LeasePublication(RetentionReadGrant owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            Monitor.Enter(owner.leasePublicationGate);
            this.owner = owner;
        }

        internal static LeasePublication FromEnteredGate(RetentionReadGrant owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            return new LeasePublication { owner = owner };
        }

        private LeasePublication() { }

        internal DateTimeOffset LeaseExpiresAt => Owner.publishedLeaseExpiresAt;

        internal void AdvanceExpiry(DateTimeOffset expiry) => Owner.publishedLeaseExpiresAt = expiry;

        internal void BindAdmissionSelectorCapability(SqliteCommand command)
        {
            RequireExecutableReferences(command, AdmissionSelectorCapabilityParameterNames);
            BindPostCommitGrantUsabilityParameters(command);
            var grant = Owner;
            command.Parameters.AddWithValue("$retention_read_revision", grant.AdmissionRevision);
        }

        // Post-commit operation use intentionally ignores mutable item revision and lifecycle state.
        internal void BindPostCommitGrantUsabilityCapability(SqliteCommand command)
        {
            RequireExecutableReferences(command, PostCommitGrantUsabilityParameterNames);
            BindPostCommitGrantUsabilityParameters(command);
        }

        private void BindPostCommitGrantUsabilityParameters(SqliteCommand command)
        {
            var grant = Owner;
            command.Parameters.AddWithValue("$retention_read_source_token", grant.sourceToken.ToArray());
            command.Parameters.AddWithValue("$retention_read_item_id", grant.ItemId);
            command.Parameters.AddWithValue("$retention_read_lease_kind", grant.LeaseKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$retention_read_lease_owner", grant.LeaseOwner);
            command.Parameters.AddWithValue("$retention_read_lease_generation", grant.LeaseGeneration);
            command.Parameters.AddWithValue("$retention_read_lease_expires_at", grant.publishedLeaseExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        private static void RequireExecutableReferences(SqliteCommand command, IReadOnlyList<string> parameterNames)
        {
            ArgumentNullException.ThrowIfNull(command);
            foreach (var parameterName in parameterNames)
            {
                if (!ContainsExecutableParameterReference(command.CommandText, parameterName))
                    throw new InvalidOperationException($"Selector does not consume required capability parameter {parameterName}.");
            }
        }

        private static bool ContainsExecutableParameterReference(string sql, string parameterName)
        {
            for (var index = 0; index < sql.Length;)
            {
                if (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
                {
                    index += 2;
                    while (index < sql.Length && sql[index] != '\n') index++;
                    continue;
                }

                if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < sql.Length && (sql[index] != '*' || sql[index + 1] != '/')) index++;
                    index = Math.Min(index + 2, sql.Length);
                    continue;
                }

                if (sql[index] is '\'' or '"' or '`' or '[')
                {
                    var opener = sql[index++];
                    var closer = opener == '[' ? ']' : opener;
                    while (index < sql.Length)
                    {
                        if (sql[index] != closer)
                        {
                            index++;
                            continue;
                        }

                        if (index + 1 < sql.Length && sql[index + 1] == closer)
                        {
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }
                    continue;
                }

                if (IsSqliteAlphabetic(sql[index]))
                {
                    do index++;
                    while (index < sql.Length && IsSqliteIdentifierCharacter(sql[index]));
                    continue;
                }

                if (TryReadSqliteNamedVariable(sql, index, out var variableEnd))
                {
                    if (sql[index] == '$'
                        && variableEnd - index == parameterName.Length
                        && sql.AsSpan(index, parameterName.Length).SequenceEqual(parameterName))
                        return true;

                    index = variableEnd;
                    continue;
                }

                index++;
            }

            return false;
        }

        private static bool TryReadSqliteNamedVariable(string sql, int start, out int end)
        {
            end = start;
            if (sql[start] is not ('$' or '@' or ':' or '#')) return false;

            var index = start + 1;
            var nameCharacterCount = 0;
            while (index < sql.Length && sql[index] != '\0')
            {
                if (IsSqliteIdentifierCharacter(sql[index]))
                {
                    nameCharacterCount++;
                    index++;
                    continue;
                }

                if (sql[index] == ':' && index + 1 < sql.Length && sql[index + 1] == ':')
                {
                    index += 2;
                    continue;
                }

                if (sql[index] == '(' && nameCharacterCount > 0)
                {
                    do index++;
                    while (index < sql.Length
                        && sql[index] != '\0'
                        && !IsSqliteWhitespace(sql[index])
                        && sql[index] != ')');
                    if (index < sql.Length && sql[index] == ')') index++;
                }
                break;
            }

            end = index;
            return nameCharacterCount > 0;
        }

        private static bool IsSqliteAlphabetic(char value) =>
            value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '_'
            || value >= '\u0080';

        private static bool IsSqliteIdentifierCharacter(char value) =>
            IsSqliteAlphabetic(value)
            || value is >= '0' and <= '9'
            or '$';

        private static bool IsSqliteWhitespace(char value) =>
            value is '\t' or '\n' or '\f' or '\r' or ' ';

        public void Dispose()
        {
            var grant = owner;
            owner = null;
            if (grant is not null) Monitor.Exit(grant.leasePublicationGate);
        }

        private RetentionReadGrant Owner => owner ?? throw new ObjectDisposedException(nameof(LeasePublication));
    }
}

internal readonly record struct RetentionGrantPublicationMember(RetentionReadGrant Grant, long FrontierOrdinal);

// Holds every publication scope of a frontier so callers keep one storable handle.
internal sealed class RetentionGrantPublicationSet : IDisposable
{
    private readonly IReadOnlyList<RetentionReadGrant> grants;
    private readonly IReadOnlyList<long> frontierOrdinals;
    private readonly IReadOnlyList<RetentionReadGrant.LeasePublication> scopes;
    private readonly IReadOnlyList<int> lockOrder;
    private readonly Action<long>? releaseObserverForTesting;
    private bool disposed;

    private RetentionGrantPublicationSet(
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> frontierOrdinals,
        IReadOnlyList<RetentionReadGrant.LeasePublication> scopes,
        IReadOnlyList<int> lockOrder,
        Action<long>? releaseObserverForTesting)
    {
        this.grants = grants;
        this.frontierOrdinals = frontierOrdinals;
        this.scopes = scopes;
        this.lockOrder = lockOrder;
        this.releaseObserverForTesting = releaseObserverForTesting;
    }

    internal static RetentionGrantPublicationSet EnterInOrder(
        IReadOnlyList<RetentionGrantPublicationMember> frontierMembers) =>
        EnterInOrder(frontierMembers, releaseObserverForTesting: null);

    internal static RetentionGrantPublicationSet EnterInOrder(
        IReadOnlyList<RetentionGrantPublicationMember> frontierMembers,
        Action<long>? releaseObserverForTesting)
    {
        var (members, lockOrder) = ValidateAndOrder(frontierMembers);

        var scopes = new RetentionReadGrant.LeasePublication[members.Length];
        var acquiredLockOrder = new List<int>(members.Length);
        try
        {
            foreach (var semanticIndex in lockOrder)
            {
                scopes[semanticIndex] = members[semanticIndex].Grant.EnterLeasePublication();
                acquiredLockOrder.Add(semanticIndex);
            }
        }
        catch
        {
            ReleaseScopesInReverse(
                scopes,
                members.Select(static member => member.FrontierOrdinal).ToArray(),
                acquiredLockOrder,
                releaseObserverForTesting);
            throw;
        }
        return new RetentionGrantPublicationSet(
            members.Select(static member => member.Grant).ToArray(),
            members.Select(static member => member.FrontierOrdinal).ToArray(),
            scopes,
            lockOrder,
            releaseObserverForTesting);
    }

    internal static bool TryEnterInOrder(
        IReadOnlyList<RetentionGrantPublicationMember> frontierMembers,
        out RetentionGrantPublicationSet publications)
    {
        var (members, lockOrder) = ValidateAndOrder(frontierMembers);
        var scopes = new RetentionReadGrant.LeasePublication[members.Length];
        var acquiredLockOrder = new List<int>(members.Length);
        foreach (var semanticIndex in lockOrder)
        {
            if (!members[semanticIndex].Grant.TryEnterLeasePublication(out scopes[semanticIndex]))
            {
                ReleaseScopesInReverse(
                    scopes,
                    members.Select(static member => member.FrontierOrdinal).ToArray(),
                    acquiredLockOrder,
                    releaseObserverForTesting: null);
                publications = null!;
                return false;
            }
            acquiredLockOrder.Add(semanticIndex);
        }

        publications = new RetentionGrantPublicationSet(
            members.Select(static member => member.Grant).ToArray(),
            members.Select(static member => member.FrontierOrdinal).ToArray(),
            scopes,
            lockOrder,
            releaseObserverForTesting: null);
        return true;
    }

    internal int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return scopes.Count;
        }
    }

    internal bool IsForGrant(int index, RetentionReadGrant grant)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ReferenceEquals(grants[index], grant);
    }

    internal DateTimeOffset LeaseExpiresAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return scopes[index].LeaseExpiresAt;
    }

    internal void AdvanceExpiry(int index, DateTimeOffset expiry)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        scopes[index].AdvanceExpiry(expiry);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseScopesInReverse(scopes, frontierOrdinals, lockOrder, releaseObserverForTesting);
    }

    private static void ReleaseScopesInReverse(
        IReadOnlyList<RetentionReadGrant.LeasePublication> acquiredScopes,
        IReadOnlyList<long> ordinals,
        IReadOnlyList<int> acquiredLockOrder,
        Action<long>? releaseObserverForTesting)
    {
        for (var index = acquiredLockOrder.Count - 1; index >= 0; index--)
        {
            var semanticIndex = acquiredLockOrder[index];
            acquiredScopes[semanticIndex].Dispose();
            releaseObserverForTesting?.Invoke(ordinals[semanticIndex]);
        }
    }

    private static (RetentionGrantPublicationMember[] Members, int[] LockOrder) ValidateAndOrder(
        IReadOnlyList<RetentionGrantPublicationMember> frontierMembers)
    {
        ArgumentNullException.ThrowIfNull(frontierMembers);
        var members = frontierMembers.ToArray();
        long? previousOrdinal = null;
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member.Grant);
            if (previousOrdinal is { } previous && member.FrontierOrdinal <= previous)
                throw new ArgumentException("Frontier ordinals must be strictly increasing.", nameof(frontierMembers));
            previousOrdinal = member.FrontierOrdinal;
            if (member.Grant.LeaseGeneration <= 0)
                throw new ArgumentException("Frontier members must have a positive lease generation.", nameof(frontierMembers));
            if (member.Grant.OwnershipKey.StoreInstanceId is null)
                throw new ArgumentException("Frontier members must have a store instance identifier.", nameof(frontierMembers));
            if (LeaseKindRank(member.Grant.LeaseKind) < 0)
                throw new ArgumentException("Frontier members contain an invalid lease kind.", nameof(frontierMembers));
        }

        var lockOrder = Enumerable.Range(0, members.Length).ToArray();
        Array.Sort(
            lockOrder,
            (left, right) => CompareLeaseTuples(members[left].Grant, members[right].Grant));
        for (var index = 1; index < lockOrder.Length; index++)
        {
            if (CompareLeaseTuples(
                    members[lockOrder[index - 1]].Grant,
                    members[lockOrder[index]].Grant) == 0)
                throw new ArgumentException("Frontier members contain a duplicate grant tuple.", nameof(frontierMembers));
        }

        return (members, lockOrder);
    }

    private static int CompareLeaseTuples(
        RetentionReadGrant left,
        RetentionReadGrant right)
    {
        var comparison = StringComparer.Ordinal.Compare(
            left.OwnershipKey.StoreInstanceId,
            right.OwnershipKey.StoreInstanceId);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(left.ItemId, right.ItemId);
        if (comparison != 0) return comparison;
        comparison = LeaseKindRank(left.LeaseKind).CompareTo(LeaseKindRank(right.LeaseKind));
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(left.LeaseOwner, right.LeaseOwner);
        return comparison != 0 ? comparison : left.LeaseGeneration.CompareTo(right.LeaseGeneration);
    }

    private static int LeaseKindRank(RetentionLeaseKind leaseKind) =>
        leaseKind switch
        {
            RetentionLeaseKind.Access => 0,
            RetentionLeaseKind.Operation => 1,
            RetentionLeaseKind.Deletion => 2,
            _ => -1,
        };
}
