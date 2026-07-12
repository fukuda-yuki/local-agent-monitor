using System.Collections.ObjectModel;

namespace CopilotAgentObservability.Telemetry;

internal enum OtlpProtobufWireType
{
    Varint,
    Fixed64,
    LengthDelimited,
    Fixed32,
}

internal enum OtlpJsonRepresentation
{
    Object,
    Array,
    String,
    Boolean,
    Number,
    DecimalString,
}

internal enum OtlpTraceFieldDisposition
{
    Value,
    ChildEnvelope,
    ProducerName,
    TraceIgnored,
}

internal sealed record OtlpTraceField(
    SourceStructuralEnvelope Envelope,
    string JsonName,
    SourceStructuralType SemanticType,
    int ProtobufTag,
    OtlpProtobufWireType ProtobufWireType,
    string FieldCode,
    SourceStructuralEnvelope? ChildEnvelope,
    SourceStructuralRole? ProducerRole,
    OtlpJsonRepresentation JsonRepresentation,
    OtlpTraceFieldDisposition Disposition);

internal static class OtlpTraceSchema
{
    public const string Release = "v1.10.0";
    public const string Commit = "ca839c51f706f5d53bfb46f06c3e90c3af3a52c6";

    private static readonly OtlpTraceField[] Descriptor =
    [
        Child(SourceStructuralEnvelope.Request, "resourceSpans", SourceStructuralType.Array, 1, OtlpProtobufWireType.LengthDelimited, "request.resource_spans", SourceStructuralEnvelope.ResourceSpans, OtlpJsonRepresentation.Array),
        Child(SourceStructuralEnvelope.ResourceSpans, "resource", SourceStructuralType.Object, 1, OtlpProtobufWireType.LengthDelimited, "resource_spans.resource", SourceStructuralEnvelope.Resource, OtlpJsonRepresentation.Object),
        Child(SourceStructuralEnvelope.ResourceSpans, "scopeSpans", SourceStructuralType.Array, 2, OtlpProtobufWireType.LengthDelimited, "resource_spans.scope_spans", SourceStructuralEnvelope.ScopeSpans, OtlpJsonRepresentation.Array),
        Producer(SourceStructuralEnvelope.ResourceSpans, "schemaUrl", SourceStructuralType.String, 3, OtlpProtobufWireType.LengthDelimited, "resource_spans.schema_url", SourceStructuralRole.SchemaUrl, OtlpJsonRepresentation.String),
        Child(SourceStructuralEnvelope.Resource, "attributes", SourceStructuralType.Array, 1, OtlpProtobufWireType.LengthDelimited, "resource.attributes", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Resource, "droppedAttributesCount", SourceStructuralType.Int, 2, OtlpProtobufWireType.Varint, "resource.dropped_attributes_count", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.Resource, "entityRefs", SourceStructuralType.Array, 3, OtlpProtobufWireType.LengthDelimited, "resource.entity_refs", SourceStructuralEnvelope.EntityRef, OtlpJsonRepresentation.Array),
        Child(SourceStructuralEnvelope.ScopeSpans, "scope", SourceStructuralType.Object, 1, OtlpProtobufWireType.LengthDelimited, "scope_spans.scope", SourceStructuralEnvelope.Scope, OtlpJsonRepresentation.Object),
        Child(SourceStructuralEnvelope.ScopeSpans, "spans", SourceStructuralType.Array, 2, OtlpProtobufWireType.LengthDelimited, "scope_spans.spans", SourceStructuralEnvelope.Span, OtlpJsonRepresentation.Array),
        Producer(SourceStructuralEnvelope.ScopeSpans, "schemaUrl", SourceStructuralType.String, 3, OtlpProtobufWireType.LengthDelimited, "scope_spans.schema_url", SourceStructuralRole.SchemaUrl, OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Scope, "name", SourceStructuralType.String, 1, OtlpProtobufWireType.LengthDelimited, "scope.name", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Scope, "version", SourceStructuralType.String, 2, OtlpProtobufWireType.LengthDelimited, "scope.version", OtlpJsonRepresentation.String),
        Child(SourceStructuralEnvelope.Scope, "attributes", SourceStructuralType.Array, 3, OtlpProtobufWireType.LengthDelimited, "scope.attributes", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Scope, "droppedAttributesCount", SourceStructuralType.Int, 4, OtlpProtobufWireType.Varint, "scope.dropped_attributes_count", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Span, "traceId", SourceStructuralType.Bytes, 1, OtlpProtobufWireType.LengthDelimited, "span.trace_id", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Span, "spanId", SourceStructuralType.Bytes, 2, OtlpProtobufWireType.LengthDelimited, "span.span_id", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Span, "traceState", SourceStructuralType.String, 3, OtlpProtobufWireType.LengthDelimited, "span.trace_state", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Span, "parentSpanId", SourceStructuralType.Bytes, 4, OtlpProtobufWireType.LengthDelimited, "span.parent_span_id", OtlpJsonRepresentation.String),
        Producer(SourceStructuralEnvelope.Span, "name", SourceStructuralType.String, 5, OtlpProtobufWireType.LengthDelimited, "span.name", SourceStructuralRole.SpanName, OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Span, "kind", SourceStructuralType.Int, 6, OtlpProtobufWireType.Varint, "span.kind", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Span, "startTimeUnixNano", SourceStructuralType.Int, 7, OtlpProtobufWireType.Fixed64, "span.start_time_unix_nano", OtlpJsonRepresentation.DecimalString),
        Value(SourceStructuralEnvelope.Span, "endTimeUnixNano", SourceStructuralType.Int, 8, OtlpProtobufWireType.Fixed64, "span.end_time_unix_nano", OtlpJsonRepresentation.DecimalString),
        Child(SourceStructuralEnvelope.Span, "attributes", SourceStructuralType.Array, 9, OtlpProtobufWireType.LengthDelimited, "span.attributes", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Span, "droppedAttributesCount", SourceStructuralType.Int, 10, OtlpProtobufWireType.Varint, "span.dropped_attributes_count", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.Span, "events", SourceStructuralType.Array, 11, OtlpProtobufWireType.LengthDelimited, "span.events", SourceStructuralEnvelope.Event, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Span, "droppedEventsCount", SourceStructuralType.Int, 12, OtlpProtobufWireType.Varint, "span.dropped_events_count", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.Span, "links", SourceStructuralType.Array, 13, OtlpProtobufWireType.LengthDelimited, "span.links", SourceStructuralEnvelope.Link, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Span, "droppedLinksCount", SourceStructuralType.Int, 14, OtlpProtobufWireType.Varint, "span.dropped_links_count", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.Span, "status", SourceStructuralType.Object, 15, OtlpProtobufWireType.LengthDelimited, "span.status", SourceStructuralEnvelope.Status, OtlpJsonRepresentation.Object),
        Value(SourceStructuralEnvelope.Span, "flags", SourceStructuralType.Int, 16, OtlpProtobufWireType.Fixed32, "span.flags", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Event, "timeUnixNano", SourceStructuralType.Int, 1, OtlpProtobufWireType.Fixed64, "event.time_unix_nano", OtlpJsonRepresentation.DecimalString),
        Producer(SourceStructuralEnvelope.Event, "name", SourceStructuralType.String, 2, OtlpProtobufWireType.LengthDelimited, "event.name", SourceStructuralRole.EventName, OtlpJsonRepresentation.String),
        Child(SourceStructuralEnvelope.Event, "attributes", SourceStructuralType.Array, 3, OtlpProtobufWireType.LengthDelimited, "event.attributes", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Event, "droppedAttributesCount", SourceStructuralType.Int, 4, OtlpProtobufWireType.Varint, "event.dropped_attributes_count", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Link, "traceId", SourceStructuralType.Bytes, 1, OtlpProtobufWireType.LengthDelimited, "link.trace_id", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Link, "spanId", SourceStructuralType.Bytes, 2, OtlpProtobufWireType.LengthDelimited, "link.span_id", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Link, "traceState", SourceStructuralType.String, 3, OtlpProtobufWireType.LengthDelimited, "link.trace_state", OtlpJsonRepresentation.String),
        Child(SourceStructuralEnvelope.Link, "attributes", SourceStructuralType.Array, 4, OtlpProtobufWireType.LengthDelimited, "link.attributes", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Value(SourceStructuralEnvelope.Link, "droppedAttributesCount", SourceStructuralType.Int, 5, OtlpProtobufWireType.Varint, "link.dropped_attributes_count", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Link, "flags", SourceStructuralType.Int, 6, OtlpProtobufWireType.Fixed32, "link.flags", OtlpJsonRepresentation.Number),
        Value(SourceStructuralEnvelope.Status, "message", SourceStructuralType.String, 2, OtlpProtobufWireType.LengthDelimited, "status.message", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.Status, "code", SourceStructuralType.Int, 3, OtlpProtobufWireType.Varint, "status.code", OtlpJsonRepresentation.Number),
        Producer(SourceStructuralEnvelope.KeyValue, "key", SourceStructuralType.String, 1, OtlpProtobufWireType.LengthDelimited, "key_value.key", SourceStructuralRole.AttributeKey, OtlpJsonRepresentation.String),
        Child(SourceStructuralEnvelope.KeyValue, "value", SourceStructuralType.Object, 2, OtlpProtobufWireType.LengthDelimited, "key_value.value", SourceStructuralEnvelope.AnyValue, OtlpJsonRepresentation.Object),
        Value(SourceStructuralEnvelope.AnyValue, "stringValue", SourceStructuralType.String, 1, OtlpProtobufWireType.LengthDelimited, "any_value.string", OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.AnyValue, "boolValue", SourceStructuralType.Bool, 2, OtlpProtobufWireType.Varint, "any_value.bool", OtlpJsonRepresentation.Boolean),
        Value(SourceStructuralEnvelope.AnyValue, "intValue", SourceStructuralType.Int, 3, OtlpProtobufWireType.Varint, "any_value.int", OtlpJsonRepresentation.DecimalString),
        Value(SourceStructuralEnvelope.AnyValue, "doubleValue", SourceStructuralType.Double, 4, OtlpProtobufWireType.Fixed64, "any_value.double", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.AnyValue, "arrayValue", SourceStructuralType.Object, 5, OtlpProtobufWireType.LengthDelimited, "any_value.array", SourceStructuralEnvelope.ArrayValue, OtlpJsonRepresentation.Object),
        Child(SourceStructuralEnvelope.AnyValue, "kvlistValue", SourceStructuralType.Object, 6, OtlpProtobufWireType.LengthDelimited, "any_value.kvlist", SourceStructuralEnvelope.KeyValueList, OtlpJsonRepresentation.Object),
        Value(SourceStructuralEnvelope.AnyValue, "bytesValue", SourceStructuralType.Bytes, 7, OtlpProtobufWireType.LengthDelimited, "any_value.bytes", OtlpJsonRepresentation.String),
        Ignored(SourceStructuralEnvelope.AnyValue, "stringValueStrindex", SourceStructuralType.Int, 8, OtlpProtobufWireType.Varint, "any_value.string_strindex", OtlpJsonRepresentation.Number),
        Child(SourceStructuralEnvelope.ArrayValue, "values", SourceStructuralType.Array, 1, OtlpProtobufWireType.LengthDelimited, "array_value.values", SourceStructuralEnvelope.AnyValue, OtlpJsonRepresentation.Array),
        Child(SourceStructuralEnvelope.KeyValueList, "values", SourceStructuralType.Array, 1, OtlpProtobufWireType.LengthDelimited, "key_value_list.values", SourceStructuralEnvelope.KeyValue, OtlpJsonRepresentation.Array),
        Ignored(SourceStructuralEnvelope.KeyValue, "keyStrindex", SourceStructuralType.Int, 3, OtlpProtobufWireType.Varint, "key_value.key_strindex", OtlpJsonRepresentation.Number),
        Producer(SourceStructuralEnvelope.EntityRef, "schemaUrl", SourceStructuralType.String, 1, OtlpProtobufWireType.LengthDelimited, "entity_ref.schema_url", SourceStructuralRole.SchemaUrl, OtlpJsonRepresentation.String),
        Value(SourceStructuralEnvelope.EntityRef, "type", SourceStructuralType.String, 2, OtlpProtobufWireType.LengthDelimited, "entity_ref.type", OtlpJsonRepresentation.String),
        Producer(SourceStructuralEnvelope.EntityRef, "idKeys", SourceStructuralType.Array, 3, OtlpProtobufWireType.LengthDelimited, "entity_ref.id_keys", SourceStructuralRole.AttributeKey, OtlpJsonRepresentation.Array),
        Producer(SourceStructuralEnvelope.EntityRef, "descriptionKeys", SourceStructuralType.Array, 4, OtlpProtobufWireType.LengthDelimited, "entity_ref.description_keys", SourceStructuralRole.AttributeKey, OtlpJsonRepresentation.Array),
    ];

    private static readonly IReadOnlyDictionary<SourceStructuralEnvelope, IReadOnlyDictionary<string, OtlpTraceField>> FieldsByEnvelope =
        new ReadOnlyDictionary<SourceStructuralEnvelope, IReadOnlyDictionary<string, OtlpTraceField>>(
            Descriptor.GroupBy(field => field.Envelope).ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, OtlpTraceField>)new ReadOnlyDictionary<string, OtlpTraceField>(
                    group.ToDictionary(field => field.JsonName, StringComparer.Ordinal))));

    public static IReadOnlyList<OtlpTraceField> Fields { get; } = Array.AsReadOnly(Descriptor);

    public static bool TryGetField(SourceStructuralEnvelope envelope, string jsonName, out OtlpTraceField field)
    {
        if (FieldsByEnvelope.TryGetValue(envelope, out var fields) && fields.TryGetValue(jsonName, out var found))
        {
            field = found;
            return true;
        }

        field = null!;
        return false;
    }

    private static OtlpTraceField Value(
        SourceStructuralEnvelope envelope,
        string jsonName,
        SourceStructuralType type,
        int tag,
        OtlpProtobufWireType wireType,
        string fieldCode,
        OtlpJsonRepresentation representation) =>
        new(envelope, jsonName, type, tag, wireType, fieldCode, null, null, representation, OtlpTraceFieldDisposition.Value);

    private static OtlpTraceField Child(
        SourceStructuralEnvelope envelope,
        string jsonName,
        SourceStructuralType type,
        int tag,
        OtlpProtobufWireType wireType,
        string fieldCode,
        SourceStructuralEnvelope child,
        OtlpJsonRepresentation representation) =>
        new(envelope, jsonName, type, tag, wireType, fieldCode, child, null, representation, OtlpTraceFieldDisposition.ChildEnvelope);

    private static OtlpTraceField Producer(
        SourceStructuralEnvelope envelope,
        string jsonName,
        SourceStructuralType type,
        int tag,
        OtlpProtobufWireType wireType,
        string fieldCode,
        SourceStructuralRole role,
        OtlpJsonRepresentation representation) =>
        new(envelope, jsonName, type, tag, wireType, fieldCode, null, role, representation, OtlpTraceFieldDisposition.ProducerName);

    private static OtlpTraceField Ignored(
        SourceStructuralEnvelope envelope,
        string jsonName,
        SourceStructuralType type,
        int tag,
        OtlpProtobufWireType wireType,
        string fieldCode,
        OtlpJsonRepresentation representation) =>
        new(envelope, jsonName, type, tag, wireType, fieldCode, null, null, representation, OtlpTraceFieldDisposition.TraceIgnored);
}
