using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace CopilotAgentObservability.Telemetry;

[JsonConverter(typeof(JsonStringEnumConverter<SourceCompatibilityState>))]
public enum SourceCompatibilityState
{
    [JsonStringEnumMemberName("supported")] Supported,
    [JsonStringEnumMemberName("supported_with_unknown_fields")] SupportedWithUnknownFields,
    [JsonStringEnumMemberName("schema_drift_detected")] SchemaDriftDetected,
    [JsonStringEnumMemberName("unsupported_source_version")] UnsupportedSourceVersion,
    [JsonStringEnumMemberName("recognized_record_drop_detected")] RecognizedRecordDropDetected,
    [JsonStringEnumMemberName("adapter_failure")] AdapterFailure,
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceCaptureContentState>))]
public enum SourceCaptureContentState
{
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("not_captured")] NotCaptured,
    [JsonStringEnumMemberName("redacted")] Redacted,
    [JsonStringEnumMemberName("unsupported")] Unsupported,
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceUnknownKind>))]
public enum SourceUnknownKind
{
    [JsonStringEnumMemberName("span")] Span,
    [JsonStringEnumMemberName("event")] Event,
    [JsonStringEnumMemberName("attribute")] Attribute,
}

public enum SourceStructuralEnvelope
{
    Request,
    ResourceSpans,
    Resource,
    ScopeSpans,
    Scope,
    Span,
    Event,
    Link,
    Status,
    KeyValue,
    AnyValue,
    ArrayValue,
    KeyValueList,
    EntityRef,
}

public enum SourceStructuralRole
{
    Envelope,
    KnownField,
    SpanName,
    EventName,
    AttributeKey,
    SchemaUrl,
    UnknownJsonProperty,
    UnknownProtobufField,
}

public enum SourceStructuralType
{
    Object,
    Array,
    String,
    Bool,
    Int,
    Double,
    Bytes,
    Null,
    Span,
    Event,
    Attribute,
    Varint,
    Fixed32,
    Fixed64,
    LengthDelimited,
}

public static class SourceCompatibilityReasonCodes
{
    public const string UnknownFieldsObserved = "unknown_fields_observed";
    public const string UnsupportedSourceVersion = "unsupported_source_version";
    public const string SchemaDriftDetected = "schema_drift_detected";
    public const string RecognizedRecordDropDetected = "recognized_record_drop_detected";
    public const string AdapterParseFailure = "adapter_parse_failure";
    public const string AdapterException = "adapter_exception";

    public static IReadOnlyList<string> CanonicalOrder { get; } = Array.AsReadOnly(new[]
    {
        UnknownFieldsObserved,
        UnsupportedSourceVersion,
        SchemaDriftDetected,
        RecognizedRecordDropDetected,
        AdapterParseFailure,
        AdapterException,
    });
}

public static class SourceCompatibilityNextActions
{
    public const string None = "none";
    public const string ReviewUnknownFields = "review_unknown_fields";
    public const string UseCompatibleSourceOrUpdateAdapter = "use_compatible_source_or_update_adapter";
    public const string CaptureFixtureAndReviewMapping = "capture_fixture_and_review_mapping";
    public const string RestoreMappingOrUpdateVersionedGolden = "restore_mapping_or_update_versioned_golden";
    public const string ValidatePayloadAndProtocol = "validate_payload_and_protocol";
    public const string InspectSanitizedAdapterFailure = "inspect_sanitized_adapter_failure";
}

public sealed class SourceReasonSet
{
    private static readonly HashSet<string> Vocabulary = new(SourceCompatibilityReasonCodes.CanonicalOrder, StringComparer.Ordinal);

    private SourceReasonSet(IEnumerable<string> values)
    {
        Values = Array.AsReadOnly(values.ToArray());
    }

    public IReadOnlyList<string> Values { get; }

    public static SourceReasonSet Create(IEnumerable<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reasonCode in reasonCodes)
        {
            if (reasonCode is null || !Vocabulary.Contains(reasonCode))
            {
                throw new ArgumentException("Reason codes must use the canonical source-compatibility vocabulary.", nameof(reasonCodes));
            }
            requested.Add(reasonCode);
        }

        return new SourceReasonSet(SourceCompatibilityReasonCodes.CanonicalOrder.Where(requested.Contains));
    }

    internal static SourceReasonSet Empty { get; } = new([]);
}

public sealed class SourceCompatibilityDecision
{
    private SourceCompatibilityDecision(SourceCompatibilityState state, SourceReasonSet reasons, string nextAction)
    {
        State = state;
        Reasons = reasons;
        NextAction = nextAction;
    }

    public SourceCompatibilityState State { get; }
    public SourceReasonSet Reasons { get; }
    public IReadOnlyList<string> ReasonCodes => Reasons.Values;
    public string NextAction { get; }

    internal static SourceCompatibilityDecision ForState(SourceCompatibilityState state) => state switch
    {
        SourceCompatibilityState.Supported => new(state, SourceReasonSet.Empty, SourceCompatibilityNextActions.None),
        SourceCompatibilityState.SupportedWithUnknownFields => CreateSingle(
            state, SourceCompatibilityReasonCodes.UnknownFieldsObserved, SourceCompatibilityNextActions.ReviewUnknownFields),
        SourceCompatibilityState.UnsupportedSourceVersion => CreateSingle(
            state, SourceCompatibilityReasonCodes.UnsupportedSourceVersion, SourceCompatibilityNextActions.UseCompatibleSourceOrUpdateAdapter),
        SourceCompatibilityState.SchemaDriftDetected => CreateSingle(
            state, SourceCompatibilityReasonCodes.SchemaDriftDetected, SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping),
        SourceCompatibilityState.RecognizedRecordDropDetected => CreateSingle(
            state, SourceCompatibilityReasonCodes.RecognizedRecordDropDetected, SourceCompatibilityNextActions.RestoreMappingOrUpdateVersionedGolden),
        SourceCompatibilityState.AdapterFailure => throw new ArgumentException("Adapter failure requires an exact failure reason.", nameof(state)),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    internal static SourceCompatibilityDecision ForAdapterFailure(string reasonCode) => reasonCode switch
    {
        SourceCompatibilityReasonCodes.AdapterParseFailure => CreateSingle(
            SourceCompatibilityState.AdapterFailure, reasonCode, SourceCompatibilityNextActions.ValidatePayloadAndProtocol),
        SourceCompatibilityReasonCodes.AdapterException => CreateSingle(
            SourceCompatibilityState.AdapterFailure, reasonCode, SourceCompatibilityNextActions.InspectSanitizedAdapterFailure),
        _ => throw new ArgumentException("Adapter failure reason is invalid.", nameof(reasonCode)),
    };

    private static SourceCompatibilityDecision CreateSingle(SourceCompatibilityState state, string reason, string action) =>
        new(state, SourceReasonSet.Create([reason]), action);
}

public sealed class SourceOccurrenceCount
{
    public const int Maximum = 1_000_000;

    private SourceOccurrenceCount(int value) => Value = value;

    public int Value { get; }

    public static SourceOccurrenceCount Create(int value)
    {
        if (value is < 1 or > Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Occurrence count must be between 1 and {Maximum}.");
        }
        return new SourceOccurrenceCount(value);
    }
}

public sealed class SourceStructuralNameToken : IEquatable<SourceStructuralNameToken>
{
    private static readonly Regex HashedNamePattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex JsonPropertyPattern = new(
        "^json:(?<envelope>[a-z_]+):property:(?<token>sha256:[0-9a-f]{64}):type:(?<type>[a-z0-9_]+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonKnownPattern = new(
        "^json:(?<envelope>[a-z_]+):known:(?<field>[a-z0-9_.]+):actual:(?<type>[a-z0-9_]+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ProtobufPattern = new(
        "^protobuf:(?<envelope>[a-z_]+):field:(?<field>[1-9][0-9]*):wire:(?<wire>varint|fixed64|length_delimited|fixed32)$",
        RegexOptions.CultureInvariant);

    private SourceStructuralNameToken(string value, TokenKind kind)
    {
        Value = value;
        Kind = kind;
    }

    public string Value { get; }
    private TokenKind Kind { get; }
    internal bool IsHashedName => Kind == TokenKind.HashedName;
    internal bool IsKnownFixed => Kind == TokenKind.KnownFixed;
    internal bool IsUnknownSafe => Kind is TokenKind.HashedName or TokenKind.JsonUnknown or TokenKind.ProtobufUnknown;
    internal bool IsJsonUnknown => Kind == TokenKind.JsonUnknown;
    internal bool IsProtobufUnknown => Kind == TokenKind.ProtobufUnknown;

    public static SourceStructuralNameToken ParseCanonical(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HashedNamePattern.IsMatch(value))
        {
            return new SourceStructuralNameToken(value, TokenKind.HashedName);
        }
        if (SourceStructuralVocabulary.IsKnownFixedToken(value))
        {
            return new SourceStructuralNameToken(value, TokenKind.KnownFixed);
        }
        if (IsValidJsonUnknown(value))
        {
            return new SourceStructuralNameToken(value, TokenKind.JsonUnknown);
        }
        if (IsValidProtobufUnknown(value))
        {
            return new SourceStructuralNameToken(value, TokenKind.ProtobufUnknown);
        }
        throw new ArgumentException("Structural name token is not canonical.", nameof(value));
    }

    public static SourceStructuralNameToken FromProducerName(SourceStructuralRole role, string rawName)
    {
        if (!Enum.IsDefined(role) || role is not (SourceStructuralRole.SpanName or SourceStructuralRole.EventName or
            SourceStructuralRole.AttributeKey or SourceStructuralRole.SchemaUrl))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        ArgumentNullException.ThrowIfNull(rawName);
        var bytes = Encoding.UTF8.GetBytes($"source-structure-v1\0{SourceStructuralVocabulary.RoleWire(role)}\0{rawName}");
        return new SourceStructuralNameToken($"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}", TokenKind.HashedName);
    }

    public bool Equals(SourceStructuralNameToken? other) => other is not null && StringComparer.Ordinal.Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is SourceStructuralNameToken other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;

    private static bool IsValidJsonUnknown(string value)
    {
        var property = JsonPropertyPattern.Match(value);
        if (property.Success)
        {
            return SourceStructuralVocabulary.IsEnvelope(property.Groups["envelope"].Value) &&
                SourceStructuralVocabulary.IsType(property.Groups["type"].Value);
        }

        var known = JsonKnownPattern.Match(value);
        return known.Success &&
            SourceStructuralVocabulary.IsEnvelope(known.Groups["envelope"].Value) &&
            SourceStructuralVocabulary.IsKnownFieldForEnvelope(
                known.Groups["field"].Value, known.Groups["envelope"].Value) &&
            SourceStructuralVocabulary.IsType(known.Groups["type"].Value);
    }

    private static bool IsValidProtobufUnknown(string value)
    {
        var match = ProtobufPattern.Match(value);
        return match.Success && SourceStructuralVocabulary.IsEnvelope(match.Groups["envelope"].Value);
    }

    private enum TokenKind
    {
        HashedName,
        KnownFixed,
        JsonUnknown,
        ProtobufUnknown,
    }
}

public sealed class SourceUnknownIdentity
{
    private static readonly Regex SampleReferencePattern = new("^sample:v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private SourceUnknownIdentity(
        SourceUnknownKind kind,
        SourceStructuralNameToken name,
        SourceOccurrenceCount count,
        DateTimeOffset firstObservedAt,
        DateTimeOffset lastObservedAt,
        string opaqueSampleReference)
    {
        Kind = kind;
        Name = name;
        Count = count;
        FirstObservedAt = firstObservedAt;
        LastObservedAt = lastObservedAt;
        OpaqueSampleReference = opaqueSampleReference;
    }

    public SourceUnknownKind Kind { get; }
    public SourceStructuralNameToken Name { get; }
    public SourceOccurrenceCount Count { get; }
    public DateTimeOffset FirstObservedAt { get; }
    public DateTimeOffset LastObservedAt { get; }
    public string OpaqueSampleReference { get; }

    public static SourceUnknownIdentity Create(
        SourceUnknownKind kind,
        SourceStructuralNameToken name,
        SourceOccurrenceCount count,
        DateTimeOffset firstObservedAt,
        DateTimeOffset lastObservedAt,
        string opaqueSampleReference)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(count);
        if (!name.IsUnknownSafe)
        {
            throw new ArgumentException("Unknown names must be keyed hashes or fixed transport identifiers.", nameof(name));
        }
        if (firstObservedAt > lastObservedAt)
        {
            throw new ArgumentException("First observed time must not be after last observed time.", nameof(firstObservedAt));
        }
        if (opaqueSampleReference is null || !SampleReferencePattern.IsMatch(opaqueSampleReference))
        {
            throw new ArgumentException("Sample reference must be a monitor-generated opaque token.", nameof(opaqueSampleReference));
        }
        return new SourceUnknownIdentity(kind, name, count, firstObservedAt, lastObservedAt, opaqueSampleReference);
    }

    internal static SourceUnknownIdentity Aggregate(
        SourceUnknownKind kind,
        SourceStructuralNameToken name,
        int count,
        DateTimeOffset firstObservedAt,
        DateTimeOffset lastObservedAt,
        string opaqueSampleReference) =>
        new(kind, name, SourceOccurrenceCount.Create(count), firstObservedAt, lastObservedAt, opaqueSampleReference);
}

public sealed class SourceStructuralOccurrence
{
    private SourceStructuralOccurrence(
        SourceStructuralEnvelope envelope,
        SourceStructuralRole role,
        SourceStructuralNameToken name,
        SourceStructuralType structuralType,
        SourceOccurrenceCount count,
        SourceUnknownIdentity? unknown)
    {
        Envelope = envelope;
        Role = role;
        Name = name;
        StructuralType = structuralType;
        Count = count;
        Unknown = unknown;
    }

    public SourceStructuralEnvelope Envelope { get; }
    public SourceStructuralRole Role { get; }
    public SourceStructuralNameToken Name { get; }
    public SourceStructuralType StructuralType { get; }
    public SourceOccurrenceCount Count { get; }
    public SourceUnknownIdentity? Unknown { get; }

    public static SourceStructuralOccurrence Create(
        SourceStructuralEnvelope envelope,
        SourceStructuralRole role,
        SourceStructuralNameToken name,
        SourceStructuralType structuralType,
        SourceOccurrenceCount count,
        SourceUnknownIdentity? unknown = null)
    {
        if (!Enum.IsDefined(envelope))
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        if (!Enum.IsDefined(structuralType))
        {
            throw new ArgumentOutOfRangeException(nameof(structuralType));
        }
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(count);

        SourceStructuralVocabulary.ValidateOccurrence(envelope, role, name, structuralType, unknown);
        if (unknown is not null && (unknown.Count.Value != count.Value || !unknown.Name.Equals(name)))
        {
            throw new ArgumentException("Unknown metadata must describe the same structural occurrence.", nameof(unknown));
        }
        return new SourceStructuralOccurrence(envelope, role, name, structuralType, count, unknown);
    }

    internal byte[] EncodeIdentity()
    {
        var fields = new[]
        {
            "trace",
            SourceStructuralVocabulary.EnvelopeWire(Envelope),
            SourceStructuralVocabulary.RoleWire(Role),
            Name.Value,
            SourceStructuralVocabulary.TypeWire(StructuralType),
        };
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("source-schema-identity-v1\0"));
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    internal SourceStructuralOccurrence With(SourceOccurrenceCount count, SourceUnknownIdentity? unknown) =>
        new(Envelope, Role, Name, StructuralType, count, unknown);
}

public sealed class SourceStructuralInventory
{
    private const int RetainedUnknownLimit = 256;

    private SourceStructuralInventory(
        string schemaFingerprint,
        string inventoryHash,
        SourceStructuralOccurrence[] structuralOccurrences,
        SourceUnknownIdentity[] retainedUnknownIdentities,
        int overflowDistinctCount,
        int overflowOccurrenceCount,
        bool hasRequiredTraceSignal,
        long unknownSpanCount,
        long unknownEventCount,
        long unknownAttributeCount)
    {
        SchemaFingerprint = schemaFingerprint;
        InventoryHash = inventoryHash;
        StructuralOccurrences = Array.AsReadOnly(structuralOccurrences);
        RetainedUnknownIdentities = Array.AsReadOnly(retainedUnknownIdentities);
        OverflowDistinctCount = overflowDistinctCount;
        OverflowOccurrenceCount = overflowOccurrenceCount;
        HasRequiredTraceSignal = hasRequiredTraceSignal;
        UnknownSpanCount = unknownSpanCount;
        UnknownEventCount = unknownEventCount;
        UnknownAttributeCount = unknownAttributeCount;
    }

    public string SchemaFingerprint { get; }
    public string InventoryHash { get; }
    public IReadOnlyList<SourceStructuralOccurrence> StructuralOccurrences { get; }
    public IReadOnlyList<SourceUnknownIdentity> RetainedUnknownIdentities { get; }
    public int OverflowDistinctCount { get; }
    public int OverflowOccurrenceCount { get; }
    public bool HasRequiredTraceSignal { get; }
    public bool HasUnknownFields => UnknownSpanCount != 0 || UnknownEventCount != 0 || UnknownAttributeCount != 0;
    public long UnknownSpanCount { get; }
    public long UnknownEventCount { get; }
    public long UnknownAttributeCount { get; }

    public static SourceStructuralInventory Create(
        IEnumerable<SourceStructuralOccurrence> fullStructuralOccurrences,
        bool hasRequiredTraceSignal)
    {
        ArgumentNullException.ThrowIfNull(fullStructuralOccurrences);
        var input = fullStructuralOccurrences.ToArray();
        if (input.Length == 0 || input.Any(item => item is null))
        {
            throw new ArgumentException("A structural inventory requires non-null full-set occurrences.", nameof(fullStructuralOccurrences));
        }

        var groups = input.GroupBy(item => Convert.ToHexString(item.EncodeIdentity()), StringComparer.Ordinal);
        var canonical = new List<SourceStructuralOccurrence>();
        foreach (var group in groups)
        {
            var items = group.ToArray();
            var first = items[0];
            var total = Math.Min(SourceOccurrenceCount.Maximum, items.Sum(item => (long)item.Count.Value));
            var unknownItems = items.Where(item => item.Unknown is not null).Select(item => item.Unknown!).ToArray();
            if (unknownItems.Length != 0 && unknownItems.Length != items.Length)
            {
                throw new ArgumentException("One structural identity cannot mix recognized and unknown classifications.", nameof(fullStructuralOccurrences));
            }

            SourceUnknownIdentity? unknown = null;
            if (unknownItems.Length != 0)
            {
                if (unknownItems.Any(item => item.Kind != unknownItems[0].Kind || !item.Name.Equals(unknownItems[0].Name)))
                {
                    throw new ArgumentException("One structural identity cannot have conflicting unknown metadata.", nameof(fullStructuralOccurrences));
                }
                unknown = SourceUnknownIdentity.Aggregate(
                    unknownItems[0].Kind,
                    unknownItems[0].Name,
                    checked((int)total),
                    unknownItems.Min(item => item.FirstObservedAt),
                    unknownItems.Max(item => item.LastObservedAt),
                    unknownItems.Select(item => item.OpaqueSampleReference).Order(StringComparer.Ordinal).First());
            }
            canonical.Add(first.With(SourceOccurrenceCount.Create(checked((int)total)), unknown));
        }

        canonical.Sort((left, right) => UnsignedBytes.Compare(left.EncodeIdentity(), right.EncodeIdentity()));
        var unknowns = canonical.Where(item => item.Unknown is not null).Select(item => item.Unknown!).ToArray();
        var retained = unknowns.Take(RetainedUnknownLimit).ToArray();
        var overflow = unknowns.Skip(RetainedUnknownLimit).ToArray();
        return new SourceStructuralInventory(
            ComputeHash("source-schema-fingerprint-v1", canonical, includeCounts: false),
            ComputeHash("source-inventory-hash-v1", canonical, includeCounts: true),
            canonical.ToArray(),
            retained,
            overflow.Length,
            checked((int)Math.Min(SourceOccurrenceCount.Maximum, overflow.Sum(item => (long)item.Count.Value))),
            hasRequiredTraceSignal,
            unknowns.Where(item => item.Kind == SourceUnknownKind.Span).Sum(item => (long)item.Count.Value),
            unknowns.Where(item => item.Kind == SourceUnknownKind.Event).Sum(item => (long)item.Count.Value),
            unknowns.Where(item => item.Kind == SourceUnknownKind.Attribute).Sum(item => (long)item.Count.Value));
    }

    private static string ComputeHash(string domain, IReadOnlyList<SourceStructuralOccurrence> entries, bool includeCounts)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"{domain}\0"));
        Span<byte> uintBuffer = stackalloc byte[4];
        Span<byte> ulongBuffer = stackalloc byte[8];
        foreach (var entry in entries)
        {
            var identity = entry.EncodeIdentity();
            BinaryPrimitives.WriteUInt32BigEndian(uintBuffer, checked((uint)identity.Length));
            stream.Write(uintBuffer);
            stream.Write(identity);
            if (includeCounts)
            {
                BinaryPrimitives.WriteUInt64BigEndian(ulongBuffer, checked((ulong)entry.Count.Value));
                stream.Write(ulongBuffer);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static class UnsignedBytes
    {
        public static int Compare(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
    }
}

public sealed record DecodedOtlpTracePayload(string PayloadJson, SourceStructuralInventory StructuralInventory);

public sealed class SourceObservationBatchDraft
{
    private SourceObservationBatchDraft(
        string ingestBatchId,
        string sourceSurface,
        string? sourceApplicationVersion,
        string sourceAdapter,
        string adapterVersion,
        SourceStructuralInventory inventory,
        SourceCompatibilityDecision decision,
        SourceCaptureContentState captureContentState,
        DateTimeOffset observedAt)
    {
        IngestBatchId = ingestBatchId;
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
        SourceAdapter = sourceAdapter;
        AdapterVersion = adapterVersion;
        Inventory = inventory;
        Decision = decision;
        CaptureContentState = captureContentState;
        ObservedAt = observedAt;
    }

    public string IngestBatchId { get; }
    public string SourceSurface { get; }
    public string? SourceApplicationVersion { get; }
    public string SourceAdapter { get; }
    public string AdapterVersion { get; }
    public SourceStructuralInventory Inventory { get; }
    public string SchemaFingerprint => Inventory.SchemaFingerprint;
    public string InventoryHash => Inventory.InventoryHash;
    public SourceCompatibilityDecision Decision { get; }
    public SourceCompatibilityState CompatibilityState => Decision.State;
    public IReadOnlyList<string> ReasonCodes => Decision.ReasonCodes;
    public string NextAction => Decision.NextAction;
    public SourceCaptureContentState CaptureContentState { get; }
    public DateTimeOffset ObservedAt { get; }

    public static SourceObservationBatchDraft Create(
        string ingestBatchId,
        string sourceSurface,
        string? sourceApplicationVersion,
        string sourceAdapter,
        string adapterVersion,
        SourceStructuralInventory inventory,
        SourceCompatibilityDecision decision,
        SourceCaptureContentState captureContentState,
        DateTimeOffset observedAt)
    {
        SourceMetadata.ValidateRequired(ingestBatchId, nameof(ingestBatchId));
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        SourceMetadata.ValidateRequired(sourceAdapter, nameof(sourceAdapter));
        SourceMetadata.ValidateRequired(adapterVersion, nameof(adapterVersion));
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(decision);
        if (!Enum.IsDefined(captureContentState))
        {
            throw new ArgumentOutOfRangeException(nameof(captureContentState));
        }
        if (decision.State == SourceCompatibilityState.AdapterFailure)
        {
            throw new ArgumentException("Successful batch observations cannot carry adapter failure decisions.", nameof(decision));
        }
        return new SourceObservationBatchDraft(
            ingestBatchId, sourceSurface, sourceApplicationVersion, sourceAdapter, adapterVersion,
            inventory, decision, captureContentState, observedAt);
    }
}

public sealed class SourceUnknownObservationDraft
{
    private SourceUnknownObservationDraft(SourceObservationBatchDraft parent, SourceUnknownIdentity identity)
    {
        Kind = identity.Kind;
        Name = identity.Name.Value;
        Count = identity.Count.Value;
        SourceVersionLabel = parent.SourceApplicationVersion;
        FirstObservedAt = identity.FirstObservedAt;
        LastObservedAt = identity.LastObservedAt;
        OpaqueSampleReference = identity.OpaqueSampleReference;
    }

    public SourceUnknownKind Kind { get; }
    public string Name { get; }
    public int Count { get; }
    public string? SourceVersionLabel { get; }
    public DateTimeOffset FirstObservedAt { get; }
    public DateTimeOffset LastObservedAt { get; }
    public string OpaqueSampleReference { get; }

    public static SourceUnknownObservationDraft Create(SourceObservationBatchDraft parent, SourceUnknownIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(identity);
        if (!parent.Inventory.RetainedUnknownIdentities.Any(item =>
            item.Kind == identity.Kind && item.Name.Equals(identity.Name) && item.Count.Value == identity.Count.Value))
        {
            throw new ArgumentException("Unknown child must come from its parent inventory snapshot.", nameof(identity));
        }
        return new SourceUnknownObservationDraft(parent, identity);
    }
}

public sealed class SourceAdapterFailureDraft
{
    private SourceAdapterFailureDraft(
        string observationId,
        string? ingestBatchId,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string? sourceAdapter,
        string? adapterVersion,
        SourceCaptureContentState? captureContentState,
        DateTimeOffset observedAt,
        SourceCompatibilityDecision decision)
    {
        ObservationId = observationId;
        IngestBatchId = ingestBatchId;
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
        SourceAdapter = sourceAdapter;
        AdapterVersion = adapterVersion;
        CaptureContentState = captureContentState;
        ObservedAt = observedAt;
        Decision = decision;
    }

    public string ObservationId { get; }
    public string? IngestBatchId { get; }
    public string? SourceSurface { get; }
    public string? SourceApplicationVersion { get; }
    public string? SourceAdapter { get; }
    public string? AdapterVersion { get; }
    public string? SchemaFingerprint => null;
    public string? InventoryHash => null;
    public SourceCompatibilityDecision Decision { get; }
    public SourceCompatibilityState CompatibilityState => Decision.State;
    public IReadOnlyList<string> ReasonCodes => Decision.ReasonCodes;
    public string NextAction => Decision.NextAction;
    public SourceCaptureContentState? CaptureContentState { get; }
    public DateTimeOffset ObservedAt { get; }

    public static SourceAdapterFailureDraft CreateParseFailure(
        string observationId,
        string? ingestBatchId,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string? sourceAdapter,
        string? adapterVersion,
        SourceCaptureContentState? captureContentState,
        DateTimeOffset observedAt) =>
        Create(observationId, ingestBatchId, sourceSurface, sourceApplicationVersion, sourceAdapter, adapterVersion,
            captureContentState, observedAt, SourceCompatibilityReasonCodes.AdapterParseFailure);

    public static SourceAdapterFailureDraft CreateAdapterException(
        string observationId,
        string? ingestBatchId,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string? sourceAdapter,
        string? adapterVersion,
        SourceCaptureContentState? captureContentState,
        DateTimeOffset observedAt) =>
        Create(observationId, ingestBatchId, sourceSurface, sourceApplicationVersion, sourceAdapter, adapterVersion,
            captureContentState, observedAt, SourceCompatibilityReasonCodes.AdapterException);

    private static SourceAdapterFailureDraft Create(
        string observationId,
        string? ingestBatchId,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string? sourceAdapter,
        string? adapterVersion,
        SourceCaptureContentState? captureContentState,
        DateTimeOffset observedAt,
        string reasonCode)
    {
        SourceMetadata.ValidateRequired(observationId, nameof(observationId));
        SourceMetadata.ValidateOptional(ingestBatchId, nameof(ingestBatchId));
        SourceMetadata.ValidateOptional(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        SourceMetadata.ValidateOptional(sourceAdapter, nameof(sourceAdapter));
        SourceMetadata.ValidateOptional(adapterVersion, nameof(adapterVersion));
        if (captureContentState is not null && !Enum.IsDefined(captureContentState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(captureContentState));
        }
        return new SourceAdapterFailureDraft(
            observationId, ingestBatchId, sourceSurface, sourceApplicationVersion, sourceAdapter, adapterVersion,
            captureContentState, observedAt, SourceCompatibilityDecision.ForAdapterFailure(reasonCode));
    }
}

internal static class SourceMetadata
{
    public static void ValidateRequired(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Source metadata must be non-empty, bounded, and control-character free.", parameterName);
        }
    }

    public static void ValidateOptional(string? value, string parameterName)
    {
        if (value is not null && !IsValid(value))
        {
            throw new ArgumentException("Source metadata must be bounded and control-character free when present.", parameterName);
        }
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 256 && value.All(character => !char.IsControl(character));
}

internal static class SourceStructuralVocabulary
{
    private static readonly IReadOnlyDictionary<SourceStructuralEnvelope, string> Envelopes =
        new ReadOnlyDictionary<SourceStructuralEnvelope, string>(new Dictionary<SourceStructuralEnvelope, string>
        {
            [SourceStructuralEnvelope.Request] = "request",
            [SourceStructuralEnvelope.ResourceSpans] = "resource_spans",
            [SourceStructuralEnvelope.Resource] = "resource",
            [SourceStructuralEnvelope.ScopeSpans] = "scope_spans",
            [SourceStructuralEnvelope.Scope] = "scope",
            [SourceStructuralEnvelope.Span] = "span",
            [SourceStructuralEnvelope.Event] = "event",
            [SourceStructuralEnvelope.Link] = "link",
            [SourceStructuralEnvelope.Status] = "status",
            [SourceStructuralEnvelope.KeyValue] = "key_value",
            [SourceStructuralEnvelope.AnyValue] = "any_value",
            [SourceStructuralEnvelope.ArrayValue] = "array_value",
            [SourceStructuralEnvelope.KeyValueList] = "key_value_list",
            [SourceStructuralEnvelope.EntityRef] = "entity_ref",
        });

    private static readonly IReadOnlyDictionary<SourceStructuralRole, string> Roles =
        new ReadOnlyDictionary<SourceStructuralRole, string>(new Dictionary<SourceStructuralRole, string>
        {
            [SourceStructuralRole.Envelope] = "envelope",
            [SourceStructuralRole.KnownField] = "known_field",
            [SourceStructuralRole.SpanName] = "span_name",
            [SourceStructuralRole.EventName] = "event_name",
            [SourceStructuralRole.AttributeKey] = "attribute_key",
            [SourceStructuralRole.SchemaUrl] = "schema_url",
            [SourceStructuralRole.UnknownJsonProperty] = "unknown_json_property",
            [SourceStructuralRole.UnknownProtobufField] = "unknown_protobuf_field",
        });

    private static readonly IReadOnlyDictionary<SourceStructuralType, string> Types =
        new ReadOnlyDictionary<SourceStructuralType, string>(new Dictionary<SourceStructuralType, string>
        {
            [SourceStructuralType.Object] = "object",
            [SourceStructuralType.Array] = "array",
            [SourceStructuralType.String] = "string",
            [SourceStructuralType.Bool] = "bool",
            [SourceStructuralType.Int] = "int",
            [SourceStructuralType.Double] = "double",
            [SourceStructuralType.Bytes] = "bytes",
            [SourceStructuralType.Null] = "null",
            [SourceStructuralType.Span] = "span",
            [SourceStructuralType.Event] = "event",
            [SourceStructuralType.Attribute] = "attribute",
            [SourceStructuralType.Varint] = "varint",
            [SourceStructuralType.Fixed32] = "fixed32",
            [SourceStructuralType.Fixed64] = "fixed64",
            [SourceStructuralType.LengthDelimited] = "length_delimited",
        });

    private static readonly IReadOnlyDictionary<string, (SourceStructuralEnvelope Envelope, SourceStructuralType Type)> KnownFields =
        new ReadOnlyDictionary<string, (SourceStructuralEnvelope, SourceStructuralType)>(
            new Dictionary<string, (SourceStructuralEnvelope, SourceStructuralType)>(StringComparer.Ordinal)
            {
                ["request.resource_spans"] = (SourceStructuralEnvelope.Request, SourceStructuralType.Array),
                ["resource_spans.resource"] = (SourceStructuralEnvelope.ResourceSpans, SourceStructuralType.Object),
                ["resource_spans.scope_spans"] = (SourceStructuralEnvelope.ResourceSpans, SourceStructuralType.Array),
                ["resource_spans.schema_url"] = (SourceStructuralEnvelope.ResourceSpans, SourceStructuralType.String),
                ["resource.attributes"] = (SourceStructuralEnvelope.Resource, SourceStructuralType.Array),
                ["resource.dropped_attributes_count"] = (SourceStructuralEnvelope.Resource, SourceStructuralType.Int),
                ["resource.entity_refs"] = (SourceStructuralEnvelope.Resource, SourceStructuralType.Array),
                ["scope_spans.scope"] = (SourceStructuralEnvelope.ScopeSpans, SourceStructuralType.Object),
                ["scope_spans.spans"] = (SourceStructuralEnvelope.ScopeSpans, SourceStructuralType.Array),
                ["scope_spans.schema_url"] = (SourceStructuralEnvelope.ScopeSpans, SourceStructuralType.String),
                ["scope.name"] = (SourceStructuralEnvelope.Scope, SourceStructuralType.String),
                ["scope.version"] = (SourceStructuralEnvelope.Scope, SourceStructuralType.String),
                ["scope.attributes"] = (SourceStructuralEnvelope.Scope, SourceStructuralType.Array),
                ["scope.dropped_attributes_count"] = (SourceStructuralEnvelope.Scope, SourceStructuralType.Int),
                ["span.trace_id"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Bytes),
                ["span.span_id"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Bytes),
                ["span.trace_state"] = (SourceStructuralEnvelope.Span, SourceStructuralType.String),
                ["span.parent_span_id"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Bytes),
                ["span.name"] = (SourceStructuralEnvelope.Span, SourceStructuralType.String),
                ["span.kind"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.start_time_unix_nano"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.end_time_unix_nano"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.attributes"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Array),
                ["span.dropped_attributes_count"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.events"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Array),
                ["span.dropped_events_count"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.links"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Array),
                ["span.dropped_links_count"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["span.status"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Object),
                ["span.flags"] = (SourceStructuralEnvelope.Span, SourceStructuralType.Int),
                ["event.time_unix_nano"] = (SourceStructuralEnvelope.Event, SourceStructuralType.Int),
                ["event.name"] = (SourceStructuralEnvelope.Event, SourceStructuralType.String),
                ["event.attributes"] = (SourceStructuralEnvelope.Event, SourceStructuralType.Array),
                ["event.dropped_attributes_count"] = (SourceStructuralEnvelope.Event, SourceStructuralType.Int),
                ["link.trace_id"] = (SourceStructuralEnvelope.Link, SourceStructuralType.Bytes),
                ["link.span_id"] = (SourceStructuralEnvelope.Link, SourceStructuralType.Bytes),
                ["link.trace_state"] = (SourceStructuralEnvelope.Link, SourceStructuralType.String),
                ["link.attributes"] = (SourceStructuralEnvelope.Link, SourceStructuralType.Array),
                ["link.dropped_attributes_count"] = (SourceStructuralEnvelope.Link, SourceStructuralType.Int),
                ["link.flags"] = (SourceStructuralEnvelope.Link, SourceStructuralType.Int),
                ["status.message"] = (SourceStructuralEnvelope.Status, SourceStructuralType.String),
                ["status.code"] = (SourceStructuralEnvelope.Status, SourceStructuralType.Int),
                ["key_value.key"] = (SourceStructuralEnvelope.KeyValue, SourceStructuralType.String),
                ["key_value.value"] = (SourceStructuralEnvelope.KeyValue, SourceStructuralType.Object),
                ["key_value.key_strindex"] = (SourceStructuralEnvelope.KeyValue, SourceStructuralType.Int),
                ["any_value.string"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.String),
                ["any_value.bool"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Bool),
                ["any_value.int"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Int),
                ["any_value.double"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Double),
                ["any_value.array"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Object),
                ["any_value.kvlist"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Object),
                ["any_value.bytes"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Bytes),
                ["any_value.string_strindex"] = (SourceStructuralEnvelope.AnyValue, SourceStructuralType.Int),
                ["array_value.values"] = (SourceStructuralEnvelope.ArrayValue, SourceStructuralType.Array),
                ["key_value_list.values"] = (SourceStructuralEnvelope.KeyValueList, SourceStructuralType.Array),
                ["entity_ref.schema_url"] = (SourceStructuralEnvelope.EntityRef, SourceStructuralType.String),
                ["entity_ref.type"] = (SourceStructuralEnvelope.EntityRef, SourceStructuralType.String),
                ["entity_ref.id_keys"] = (SourceStructuralEnvelope.EntityRef, SourceStructuralType.Array),
                ["entity_ref.description_keys"] = (SourceStructuralEnvelope.EntityRef, SourceStructuralType.Array),
            });

    public static string EnvelopeWire(SourceStructuralEnvelope envelope) => Envelopes[envelope];
    public static string RoleWire(SourceStructuralRole role) => Roles[role];
    public static string TypeWire(SourceStructuralType type) => Types[type];
    public static bool IsEnvelope(string value) => Envelopes.Values.Contains(value, StringComparer.Ordinal);
    public static bool IsType(string value) => Types.Values.Contains(value, StringComparer.Ordinal);
    public static bool IsKnownFixedToken(string value) =>
        Envelopes.Values.Contains(value, StringComparer.Ordinal) || KnownFields.ContainsKey(value);
    public static bool IsKnownFieldForEnvelope(string field, string envelope) =>
        KnownFields.TryGetValue(field, out var definition) && StringComparer.Ordinal.Equals(EnvelopeWire(definition.Envelope), envelope);

    public static void ValidateOccurrence(
        SourceStructuralEnvelope envelope,
        SourceStructuralRole role,
        SourceStructuralNameToken name,
        SourceStructuralType structuralType,
        SourceUnknownIdentity? unknown)
    {
        switch (role)
        {
            case SourceStructuralRole.Envelope when name.Value == EnvelopeWire(envelope) && structuralType == SourceStructuralType.Object && unknown is null:
                return;
            case SourceStructuralRole.KnownField when name.IsKnownFixed && KnownFields.TryGetValue(name.Value, out var field) &&
                field == (envelope, structuralType) && unknown is null:
                return;
            case SourceStructuralRole.SpanName when envelope == SourceStructuralEnvelope.Span && structuralType == SourceStructuralType.String &&
                name.IsHashedName && (unknown is null || unknown.Kind == SourceUnknownKind.Span):
                return;
            case SourceStructuralRole.EventName when envelope == SourceStructuralEnvelope.Event && structuralType == SourceStructuralType.String &&
                name.IsHashedName && (unknown is null || unknown.Kind == SourceUnknownKind.Event):
                return;
            case SourceStructuralRole.AttributeKey when envelope is SourceStructuralEnvelope.KeyValue or SourceStructuralEnvelope.EntityRef &&
                structuralType == SourceStructuralType.String && name.IsHashedName && (unknown is null || unknown.Kind == SourceUnknownKind.Attribute):
                return;
            case SourceStructuralRole.SchemaUrl when envelope is SourceStructuralEnvelope.ResourceSpans or SourceStructuralEnvelope.ScopeSpans or SourceStructuralEnvelope.EntityRef &&
                structuralType == SourceStructuralType.String && name.IsHashedName && unknown is null:
                return;
            case SourceStructuralRole.UnknownJsonProperty when name.IsJsonUnknown && unknown?.Kind == SourceUnknownKind.Attribute &&
                TokenMatches(name.Value, envelope, structuralType):
                return;
            case SourceStructuralRole.UnknownProtobufField when name.IsProtobufUnknown && unknown?.Kind == SourceUnknownKind.Attribute &&
                TokenMatches(name.Value, envelope, structuralType):
                return;
            default:
                throw new ArgumentException("Structural occurrence does not match the canonical descriptor vocabulary.");
        }
    }

    private static bool TokenMatches(string token, SourceStructuralEnvelope envelope, SourceStructuralType structuralType)
    {
        var parts = token.Split(':');
        return parts.Length >= 5 &&
            StringComparer.Ordinal.Equals(parts[1], EnvelopeWire(envelope)) &&
            StringComparer.Ordinal.Equals(parts[^1], TypeWire(structuralType));
    }
}
