using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class OtlpJsonRecognizedPayloadBuilderTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [Theory]
    [MemberData(nameof(ValidRepresentationCases))]
    public void Matrix_ValidRepresentation_IsPreservedOrTraceIgnored(string fieldKey)
    {
        var field = Field(fieldKey);
        var inputValue = ValidValue(field, expected: false);
        var expectedProperties = field.TraceIgnored
            ? Array.Empty<TestProperty>()
            : [new(field.JsonName, ValidValue(field, expected: true))];

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(
            Wrap(field.Envelope, [new(field.JsonName, inputValue)], expected: false));

        AssertJsonEqual(Wrap(field.Envelope, expectedProperties, expected: true), actual);
    }

    [Theory]
    [MemberData(nameof(WrongKindCases))]
    public void Matrix_WrongJsonKind_IsOmitted(string fieldKey, string wrongKindName)
    {
        var field = Field(fieldKey);
        var wrongKind = Enum.Parse<TestJsonKind>(wrongKindName);
        var input = Wrap(field.Envelope, [new(field.JsonName, JsonForKind(wrongKind))], expected: false);

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        AssertJsonEqual(Wrap(field.Envelope, [], expected: true), actual);
    }

    [Theory]
    [MemberData(nameof(MalformedDecimalCases))]
    public void Matrix_MalformedDecimalString_IsOmitted(string fieldKey, string malformed)
    {
        var field = Field(fieldKey);
        var input = Wrap(field.Envelope, [new(field.JsonName, JsonSerializer.Serialize(malformed))], expected: false);

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        AssertJsonEqual(Wrap(field.Envelope, [], expected: true), actual);
        if (malformed.Length != 0)
        {
            Assert.DoesNotContain(malformed, actual, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(RepeatedElementCases))]
    public void Matrix_InvalidRepeatedElement_IsFilteredInPlace(
        string fieldKey,
        string wrongKindName,
        string placementName)
    {
        var field = Field(fieldKey);
        var wrongKind = Enum.Parse<TestJsonKind>(wrongKindName);
        var placement = Enum.Parse<TestPlacement>(placementName);
        var beforeMarker = $"before:{field.Key}";
        var afterMarker = $"after:{field.Key}";
        var beforeInput = MarkedRepeatedElement(field, beforeMarker, expected: false);
        var afterInput = MarkedRepeatedElement(field, afterMarker, expected: false);
        var invalid = JsonForKind(wrongKind);
        var elements = placement switch
        {
            TestPlacement.First => new[] { invalid, beforeInput, afterInput },
            TestPlacement.Middle => new[] { beforeInput, invalid, afterInput },
            TestPlacement.Last => new[] { beforeInput, afterInput, invalid },
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
        var input = Wrap(
            field.Envelope,
            [new(field.JsonName, $"[{string.Join(',', elements)}]")],
            expected: false);
        var beforeExpected = MarkedRepeatedElement(field, beforeMarker, expected: true);
        var afterExpected = MarkedRepeatedElement(field, afterMarker, expected: true);
        var expected = Wrap(
            field.Envelope,
            [new(field.JsonName, $"[{beforeExpected},{afterExpected}]")],
            expected: true);

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        AssertJsonEqual(expected, actual);
        Assert.True(
            actual.IndexOf(beforeMarker, StringComparison.Ordinal) <
            actual.IndexOf(afterMarker, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(actual);
        var repeated = GetEnvelope(document.RootElement, field.Envelope).GetProperty(field.JsonName);
        Assert.Equal(
            [beforeMarker, afterMarker],
            repeated.EnumerateArray().Select(item => ReadRepeatedMarker(field, item)));
    }

    [Theory]
    [MemberData(nameof(UnknownPropertyCases))]
    public void Matrix_UnknownProperty_IsOmitted(string envelopeName, string valueKindName)
    {
        var envelope = Enum.Parse<TestEnvelope>(envelopeName);
        var kind = Enum.Parse<TestJsonKind>(valueKindName);
        var input = Wrap(envelope, [new("unknownMarker", JsonForKind(kind))], expected: false);

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        AssertJsonEqual(Wrap(envelope, [], expected: true), actual);
        Assert.DoesNotContain("unknownMarker", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DuplicateOrderCases))]
    public void Matrix_DuplicateWrongAndValidOccurrences_AreFilteredIndependently(
        string fieldKey,
        bool validFirst)
    {
        var field = Field(fieldKey);
        var valid = new TestProperty(field.JsonName, ValidValue(field, expected: false));
        var wrong = new TestProperty(field.JsonName, JsonForKind(FirstWrongKind(field)));
        var properties = validFirst ? new[] { valid, wrong } : new[] { wrong, valid };
        var expectedProperties = field.TraceIgnored
            ? Array.Empty<TestProperty>()
            : [new(field.JsonName, ValidValue(field, expected: true))];

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(
            Wrap(field.Envelope, properties, expected: false));

        AssertJsonEqual(Wrap(field.Envelope, expectedProperties, expected: true), actual);
    }

    [Theory]
    [MemberData(nameof(AnyValueConsumerCases))]
    public void Matrix_AnyValueField_IsFilteredAtBothConsumers(string fieldKey, bool arrayConsumer)
    {
        var field = Field(fieldKey);
        var inputAnyValue = EnvelopeObject(
            TestEnvelope.AnyValue,
            [new(field.JsonName, ValidValue(field, expected: false))],
            expected: false);
        var expectedAnyValue = EnvelopeObject(
            TestEnvelope.AnyValue,
            [new(field.JsonName, ValidValue(field, expected: true))],
            expected: true);
        var input = WrapAnyValueConsumer(inputAnyValue, arrayConsumer, expected: false);
        var expected = WrapAnyValueConsumer(expectedAnyValue, arrayConsumer, expected: true);

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        AssertJsonEqual(expected, actual);
    }

    [Fact]
    public void ApprovedCaseCounts_AreExactlyPinned()
    {
        Assert.Equal(59, ValidRepresentationCases.Count());
        Assert.Equal(295, WrongKindCases.Count());
        Assert.Equal(28, MalformedDecimalCases.Count());
        Assert.Equal(225, RepeatedElementCases.Count());
        Assert.Equal(84, UnknownPropertyCases.Count());
        Assert.Equal(118, DuplicateOrderCases.Count());
        Assert.Equal(14, AnyValueConsumerCases.Count());
        Assert.Equal(823,
            ValidRepresentationCases.Count() +
            WrongKindCases.Count() +
            MalformedDecimalCases.Count() +
            RepeatedElementCases.Count() +
            UnknownPropertyCases.Count() +
            DuplicateOrderCases.Count() +
            AnyValueConsumerCases.Count());
    }

    [Fact]
    public void DescriptorOracle_MatchesProductionRowsAndFallbackAuthority()
    {
        var actual = OtlpTraceSchema.Fields.Select(field => string.Join('|',
            field.Envelope,
            field.JsonName,
            field.JsonRepresentation,
            field.ChildEnvelope?.ToString() ?? "-",
            field.Disposition,
            field.EmitEmptyArrayWhenAbsent));
        var expected = Fields.Select(field => string.Join('|',
            field.Envelope,
            field.JsonName,
            field.Representation,
            field.ChildEnvelope?.ToString() ?? "-",
            field.TraceIgnored ? "TraceIgnored" : field.Disposition.ToString(),
            field.EmitEmptyArrayWhenAbsent));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_EmitsExactlySixFallbackArraysAndNoOtherAbsentFields()
    {
        AssertJsonEqual("""{"resourceSpans":[]}""", OtlpJsonRecognizedPayloadBuilder.Build("{}"));
        AssertJsonEqual(
            """{"resourceSpans":[{"scopeSpans":[]}]}""",
            OtlpJsonRecognizedPayloadBuilder.Build("""{"resourceSpans":[{}]}"""));
        AssertJsonEqual(
            """{"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[]}]}]}""",
            OtlpJsonRecognizedPayloadBuilder.Build(
                """{"resourceSpans":[{"resource":{},"scopeSpans":[{}]}]}"""));
        AssertJsonEqual(
            """{"resourceSpans":[{"scopeSpans":[{"spans":[{"attributes":[{"value":{"arrayValue":{"values":[]},"kvlistValue":{"values":[]}}}]}]}]}]}""",
            OtlpJsonRecognizedPayloadBuilder.Build(
                """{"resourceSpans":[{"scopeSpans":[{"spans":[{"attributes":[{"value":{"arrayValue":{},"kvlistValue":{}}}]}]}]}]}"""));
    }

    [Fact]
    public void Build_DuplicateValidOccurrencesRemainDuplicatedInSourceOrder()
    {
        const string input = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{
              "name":"first",
              "name":"second",
              "kind":1,
              "kind":2
            }]}]}]}
            """;

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);
        using var document = JsonDocument.Parse(actual);
        var span = document.RootElement
            .GetProperty("resourceSpans")[0]
            .GetProperty("scopeSpans")[0]
            .GetProperty("spans")[0];

        Assert.Equal(["first", "second"], span.EnumerateObject()
            .Where(property => property.NameEquals("name"))
            .Select(property => property.Value.GetString()));
        Assert.Equal([1, 2], span.EnumerateObject()
            .Where(property => property.NameEquals("kind"))
            .Select(property => property.Value.GetInt32()));
    }

    [Fact]
    public void Build_DifferentKnownPropertiesRemainInExactSourceOrder()
    {
        const string input = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{
              "kind":2,
              "traceId":"trace-marker",
              "name":"name-marker",
              "spanId":"span-marker",
              "status":{"code":1,"message":"status-marker"}
            }]}]}]}
            """;

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);
        using var document = JsonDocument.Parse(actual);
        var span = GetEnvelope(document.RootElement, TestEnvelope.Span);
        var status = span.GetProperty("status");

        Assert.Equal(
            ["kind", "traceId", "name", "spanId", "status"],
            span.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["code", "message"],
            status.EnumerateObject().Select(property => property.Name));
        AssertPropertyOrder(span.GetRawText(), "kind", "traceId", "name", "spanId", "status");
        AssertPropertyOrder(status.GetRawText(), "code", "message");
    }

    [Fact]
    public void Build_UsesStrictUtf8AndPreservesUnicodeValues()
    {
        const string input = """{"resourceSpans":[{"schemaUrl":"urn:例:😀"}]}""";

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);
        var utf8 = StrictUtf8.GetBytes(actual);

        Assert.False(utf8.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(actual, StrictUtf8.GetString(utf8));
        using var document = JsonDocument.Parse(utf8);
        Assert.Equal("urn:例:😀", document.RootElement.GetProperty("resourceSpans")[0].GetProperty("schemaUrl").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("{not-json}")]
    public void Build_MalformedOrNonObjectRoot_ThrowsJsonException(string input)
    {
        Assert.ThrowsAny<JsonException>(() => OtlpJsonRecognizedPayloadBuilder.Build(input));
    }

    [Fact]
    public void Build_NullPayload_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => OtlpJsonRecognizedPayloadBuilder.Build(null!));
    }

    [Fact]
    public void Build_DoesNotMutateRawOrOriginalInputInventory()
    {
        const string input = """
            {"unknown":"raw-marker","resourceSpans":[{"scopeSpans":[{"spans":[{
              "name":"kept",
              "name":false,
              "startTimeUnixNano":"+1"
            }]}]}]}
            """;
        var before = OtlpJsonStructuralWalker.Build(input);

        var recognized = OtlpJsonRecognizedPayloadBuilder.Build(input);
        var after = OtlpJsonStructuralWalker.Build(input);

        Assert.Equal(before.SchemaFingerprint, after.SchemaFingerprint);
        Assert.Equal(before.InventoryHash, after.InventoryHash);
        Assert.Equal(3, after.UnknownAttributeCount);
        Assert.Contains("raw-marker", input, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-marker", recognized, StringComparison.Ordinal);
        Assert.DoesNotContain("+1", recognized, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"kept\"", recognized, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DecimalBoundariesAreExact()
    {
        const string input = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{
              "startTimeUnixNano":"18446744073709551615",
              "endTimeUnixNano":"-1",
              "events":[{"timeUnixNano":""}],
              "attributes":[
                {"value":{"intValue":"-9223372036854775808"}},
                {"value":{"intValue":"9223372036854775807"}},
                {"value":{"intValue":"9223372036854775808"}},
                {"value":{"intValue":"-9223372036854775809"}}
              ]
            }]}]}]}
            """;

        var actual = OtlpJsonRecognizedPayloadBuilder.Build(input);

        Assert.Contains("18446744073709551615", actual, StringComparison.Ordinal);
        Assert.Contains("-9223372036854775808", actual, StringComparison.Ordinal);
        Assert.Contains("9223372036854775807", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("\"endTimeUnixNano\"", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("\"timeUnixNano\"", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("-9223372036854775809", actual, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(actual);
        var values = document.RootElement.GetProperty("resourceSpans")[0]
            .GetProperty("scopeSpans")[0]
            .GetProperty("spans")[0]
            .GetProperty("attributes")
            .EnumerateArray()
            .Select(item => item.GetProperty("value"))
            .Where(value => value.TryGetProperty("intValue", out _))
            .Select(value => value.GetProperty("intValue").GetString());
        Assert.Equal(["-9223372036854775808", "9223372036854775807"], values);
    }

    public static IEnumerable<object[]> ValidRepresentationCases =>
        Fields.Select(descriptor => new object[] { descriptor.Key });

    public static IEnumerable<object[]> WrongKindCases =>
        from descriptor in Fields
        from kind in Enum.GetValues<TestJsonKind>()
        where kind != AcceptedKind(descriptor)
        select new object[] { descriptor.Key, kind.ToString() };

    public static IEnumerable<object[]> MalformedDecimalCases =>
        from descriptor in Fields.Where(candidate => candidate.Representation == TestRepresentation.DecimalString)
        from malformed in MalformedDecimals(descriptor)
        select new object[] { descriptor.Key, malformed };

    public static IEnumerable<object[]> RepeatedElementCases =>
        from descriptor in Fields.Where(candidate => candidate.Representation == TestRepresentation.Array)
        from kind in Enum.GetValues<TestJsonKind>()
        where kind != RepeatedElementKind(descriptor)
        from placement in Enum.GetValues<TestPlacement>()
        select new object[] { descriptor.Key, kind.ToString(), placement.ToString() };

    public static IEnumerable<object[]> UnknownPropertyCases =>
        from envelope in Enum.GetValues<TestEnvelope>()
        from kind in Enum.GetValues<TestJsonKind>()
        select new object[] { envelope.ToString(), kind.ToString() };

    public static IEnumerable<object[]> DuplicateOrderCases =>
        from descriptor in Fields
        from validFirst in new[] { true, false }
        select new object[] { descriptor.Key, validFirst };

    public static IEnumerable<object[]> AnyValueConsumerCases =>
        from descriptor in Fields.Where(candidate => candidate.Envelope == TestEnvelope.AnyValue && !candidate.TraceIgnored)
        from arrayConsumer in new[] { false, true }
        select new object[] { descriptor.Key, arrayConsumer };

    private static readonly TestField[] Fields =
    [
        Child(TestEnvelope.Request, "resourceSpans", TestRepresentation.Array, TestEnvelope.ResourceSpans, fallback: true),
        Child(TestEnvelope.ResourceSpans, "resource", TestRepresentation.Object, TestEnvelope.Resource),
        Child(TestEnvelope.ResourceSpans, "scopeSpans", TestRepresentation.Array, TestEnvelope.ScopeSpans, fallback: true),
        Producer(TestEnvelope.ResourceSpans, "schemaUrl", TestRepresentation.String),
        Child(TestEnvelope.Resource, "attributes", TestRepresentation.Array, TestEnvelope.KeyValue, fallback: true),
        Value(TestEnvelope.Resource, "droppedAttributesCount", TestRepresentation.Number),
        Child(TestEnvelope.Resource, "entityRefs", TestRepresentation.Array, TestEnvelope.EntityRef),
        Child(TestEnvelope.ScopeSpans, "scope", TestRepresentation.Object, TestEnvelope.Scope),
        Child(TestEnvelope.ScopeSpans, "spans", TestRepresentation.Array, TestEnvelope.Span, fallback: true),
        Producer(TestEnvelope.ScopeSpans, "schemaUrl", TestRepresentation.String),
        Value(TestEnvelope.Scope, "name", TestRepresentation.String),
        Value(TestEnvelope.Scope, "version", TestRepresentation.String),
        Child(TestEnvelope.Scope, "attributes", TestRepresentation.Array, TestEnvelope.KeyValue),
        Value(TestEnvelope.Scope, "droppedAttributesCount", TestRepresentation.Number),
        Value(TestEnvelope.Span, "traceId", TestRepresentation.String),
        Value(TestEnvelope.Span, "spanId", TestRepresentation.String),
        Value(TestEnvelope.Span, "traceState", TestRepresentation.String),
        Value(TestEnvelope.Span, "parentSpanId", TestRepresentation.String),
        Producer(TestEnvelope.Span, "name", TestRepresentation.String),
        Value(TestEnvelope.Span, "kind", TestRepresentation.Number),
        Value(TestEnvelope.Span, "startTimeUnixNano", TestRepresentation.DecimalString),
        Value(TestEnvelope.Span, "endTimeUnixNano", TestRepresentation.DecimalString),
        Child(TestEnvelope.Span, "attributes", TestRepresentation.Array, TestEnvelope.KeyValue),
        Value(TestEnvelope.Span, "droppedAttributesCount", TestRepresentation.Number),
        Child(TestEnvelope.Span, "events", TestRepresentation.Array, TestEnvelope.Event),
        Value(TestEnvelope.Span, "droppedEventsCount", TestRepresentation.Number),
        Child(TestEnvelope.Span, "links", TestRepresentation.Array, TestEnvelope.Link),
        Value(TestEnvelope.Span, "droppedLinksCount", TestRepresentation.Number),
        Child(TestEnvelope.Span, "status", TestRepresentation.Object, TestEnvelope.Status),
        Value(TestEnvelope.Span, "flags", TestRepresentation.Number),
        Value(TestEnvelope.Event, "timeUnixNano", TestRepresentation.DecimalString),
        Producer(TestEnvelope.Event, "name", TestRepresentation.String),
        Child(TestEnvelope.Event, "attributes", TestRepresentation.Array, TestEnvelope.KeyValue),
        Value(TestEnvelope.Event, "droppedAttributesCount", TestRepresentation.Number),
        Value(TestEnvelope.Link, "traceId", TestRepresentation.String),
        Value(TestEnvelope.Link, "spanId", TestRepresentation.String),
        Value(TestEnvelope.Link, "traceState", TestRepresentation.String),
        Child(TestEnvelope.Link, "attributes", TestRepresentation.Array, TestEnvelope.KeyValue),
        Value(TestEnvelope.Link, "droppedAttributesCount", TestRepresentation.Number),
        Value(TestEnvelope.Link, "flags", TestRepresentation.Number),
        Value(TestEnvelope.Status, "message", TestRepresentation.String),
        Value(TestEnvelope.Status, "code", TestRepresentation.Number),
        Producer(TestEnvelope.KeyValue, "key", TestRepresentation.String),
        Child(TestEnvelope.KeyValue, "value", TestRepresentation.Object, TestEnvelope.AnyValue),
        Value(TestEnvelope.AnyValue, "stringValue", TestRepresentation.String),
        Value(TestEnvelope.AnyValue, "boolValue", TestRepresentation.Boolean),
        Value(TestEnvelope.AnyValue, "intValue", TestRepresentation.DecimalString),
        Value(TestEnvelope.AnyValue, "doubleValue", TestRepresentation.Number),
        Child(TestEnvelope.AnyValue, "arrayValue", TestRepresentation.Object, TestEnvelope.ArrayValue),
        Child(TestEnvelope.AnyValue, "kvlistValue", TestRepresentation.Object, TestEnvelope.KeyValueList),
        Value(TestEnvelope.AnyValue, "bytesValue", TestRepresentation.String),
        Ignored(TestEnvelope.AnyValue, "stringValueStrindex", TestRepresentation.Number),
        Child(TestEnvelope.ArrayValue, "values", TestRepresentation.Array, TestEnvelope.AnyValue, fallback: true),
        Child(TestEnvelope.KeyValueList, "values", TestRepresentation.Array, TestEnvelope.KeyValue, fallback: true),
        Ignored(TestEnvelope.KeyValue, "keyStrindex", TestRepresentation.Number),
        Producer(TestEnvelope.EntityRef, "schemaUrl", TestRepresentation.String),
        Value(TestEnvelope.EntityRef, "type", TestRepresentation.String),
        Producer(TestEnvelope.EntityRef, "idKeys", TestRepresentation.Array),
        Producer(TestEnvelope.EntityRef, "descriptionKeys", TestRepresentation.Array),
    ];

    private static readonly IReadOnlyDictionary<TestEnvelope, string[]> FallbackArrays =
        new Dictionary<TestEnvelope, string[]>
        {
            [TestEnvelope.Request] = ["resourceSpans"],
            [TestEnvelope.ResourceSpans] = ["scopeSpans"],
            [TestEnvelope.Resource] = ["attributes"],
            [TestEnvelope.ScopeSpans] = ["spans"],
            [TestEnvelope.ArrayValue] = ["values"],
            [TestEnvelope.KeyValueList] = ["values"],
        };

    private static TestField Field(string key) => Assert.Single(Fields, field => field.Key == key);

    private static TestField Value(TestEnvelope envelope, string name, TestRepresentation representation) =>
        new(envelope, name, representation, TestDisposition.Value, null, false, false);

    private static TestField Child(
        TestEnvelope envelope,
        string name,
        TestRepresentation representation,
        TestEnvelope child,
        bool fallback = false) =>
        new(envelope, name, representation, TestDisposition.ChildEnvelope, child, false, fallback);

    private static TestField Producer(TestEnvelope envelope, string name, TestRepresentation representation) =>
        new(envelope, name, representation, TestDisposition.ProducerName, null, false, false);

    private static TestField Ignored(TestEnvelope envelope, string name, TestRepresentation representation) =>
        new(envelope, name, representation, TestDisposition.Value, null, true, false);

    private static string ValidValue(TestField field, bool expected) => field.Representation switch
    {
        TestRepresentation.Object => EnvelopeObject(field.ChildEnvelope!.Value, [], expected),
        TestRepresentation.Array => $"[{ValidRepeatedElement(field, expected)}]",
        TestRepresentation.String => JsonSerializer.Serialize($"value:{field.Key}"),
        TestRepresentation.Boolean => "true",
        TestRepresentation.Number => "7",
        TestRepresentation.DecimalString => field.Key == "AnyValue.intValue" ? "\"-7\"" : "\"7\"",
        _ => throw new ArgumentOutOfRangeException(nameof(field.Representation)),
    };

    private static string ValidRepeatedElement(TestField field, bool expected) =>
        field.ChildEnvelope is { } child
            ? EnvelopeObject(child, [], expected)
            : JsonSerializer.Serialize($"item:{field.Key}");

    private static string MarkedRepeatedElement(TestField field, string marker, bool expected)
    {
        if (field.ChildEnvelope is not { } child)
        {
            return JsonSerializer.Serialize(marker);
        }

        return EnvelopeObject(
            child,
            [new(MarkerPropertyName(child), JsonSerializer.Serialize(marker))],
            expected);
    }

    private static string MarkerPropertyName(TestEnvelope envelope) => envelope switch
    {
        TestEnvelope.ResourceSpans => "schemaUrl",
        TestEnvelope.ScopeSpans => "schemaUrl",
        TestEnvelope.KeyValue => "key",
        TestEnvelope.EntityRef => "type",
        TestEnvelope.Span => "name",
        TestEnvelope.Event => "name",
        TestEnvelope.Link => "traceState",
        TestEnvelope.AnyValue => "stringValue",
        _ => throw new ArgumentOutOfRangeException(nameof(envelope)),
    };

    private static string? ReadRepeatedMarker(TestField field, JsonElement item) =>
        field.ChildEnvelope is { } child
            ? item.GetProperty(MarkerPropertyName(child)).GetString()
            : item.GetString();

    private static TestJsonKind AcceptedKind(TestField field) => field.Representation switch
    {
        TestRepresentation.Object => TestJsonKind.Object,
        TestRepresentation.Array => TestJsonKind.Array,
        TestRepresentation.String or TestRepresentation.DecimalString => TestJsonKind.String,
        TestRepresentation.Boolean => TestJsonKind.Boolean,
        TestRepresentation.Number => TestJsonKind.Number,
        _ => throw new ArgumentOutOfRangeException(nameof(field.Representation)),
    };

    private static TestJsonKind RepeatedElementKind(TestField field) =>
        field.ChildEnvelope is null ? TestJsonKind.String : TestJsonKind.Object;

    private static TestJsonKind FirstWrongKind(TestField field) =>
        Enum.GetValues<TestJsonKind>().First(kind => kind != AcceptedKind(field));

    private static string JsonForKind(TestJsonKind kind) => kind switch
    {
        TestJsonKind.Object => "{}",
        TestJsonKind.Array => "[]",
        TestJsonKind.String => "\"wrong-kind\"",
        TestJsonKind.Boolean => "false",
        TestJsonKind.Number => "13",
        TestJsonKind.Null => "null",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IEnumerable<string> MalformedDecimals(TestField field) =>
    [
        "",
        "+1",
        " 1",
        "1-2",
        "1.0",
        "1e3",
        field.Key == "AnyValue.intValue" ? "9223372036854775808" : "18446744073709551616",
    ];

    private static string Wrap(
        TestEnvelope envelope,
        IReadOnlyList<TestProperty> properties,
        bool expected)
    {
        var currentEnvelope = envelope;
        var current = EnvelopeObject(envelope, properties, expected);
        while (currentEnvelope != TestEnvelope.Request)
        {
            (currentEnvelope, current) = currentEnvelope switch
            {
                TestEnvelope.ResourceSpans => (
                    TestEnvelope.Request,
                    EnvelopeObject(TestEnvelope.Request, [new("resourceSpans", $"[{current}]")], expected)),
                TestEnvelope.Resource => (
                    TestEnvelope.ResourceSpans,
                    EnvelopeObject(TestEnvelope.ResourceSpans, [new("resource", current)], expected)),
                TestEnvelope.ScopeSpans => (
                    TestEnvelope.ResourceSpans,
                    EnvelopeObject(TestEnvelope.ResourceSpans, [new("scopeSpans", $"[{current}]")], expected)),
                TestEnvelope.Scope => (
                    TestEnvelope.ScopeSpans,
                    EnvelopeObject(TestEnvelope.ScopeSpans, [new("scope", current)], expected)),
                TestEnvelope.Span => (
                    TestEnvelope.ScopeSpans,
                    EnvelopeObject(TestEnvelope.ScopeSpans, [new("spans", $"[{current}]")], expected)),
                TestEnvelope.Event => (
                    TestEnvelope.Span,
                    EnvelopeObject(TestEnvelope.Span, [new("events", $"[{current}]")], expected)),
                TestEnvelope.Link => (
                    TestEnvelope.Span,
                    EnvelopeObject(TestEnvelope.Span, [new("links", $"[{current}]")], expected)),
                TestEnvelope.Status => (
                    TestEnvelope.Span,
                    EnvelopeObject(TestEnvelope.Span, [new("status", current)], expected)),
                TestEnvelope.KeyValue => (
                    TestEnvelope.Span,
                    EnvelopeObject(TestEnvelope.Span, [new("attributes", $"[{current}]")], expected)),
                TestEnvelope.AnyValue => (
                    TestEnvelope.KeyValue,
                    EnvelopeObject(TestEnvelope.KeyValue, [new("value", current)], expected)),
                TestEnvelope.ArrayValue => (
                    TestEnvelope.AnyValue,
                    EnvelopeObject(TestEnvelope.AnyValue, [new("arrayValue", current)], expected)),
                TestEnvelope.KeyValueList => (
                    TestEnvelope.AnyValue,
                    EnvelopeObject(TestEnvelope.AnyValue, [new("kvlistValue", current)], expected)),
                TestEnvelope.EntityRef => (
                    TestEnvelope.Resource,
                    EnvelopeObject(TestEnvelope.Resource, [new("entityRefs", $"[{current}]")], expected)),
                _ => throw new ArgumentOutOfRangeException(nameof(envelope)),
            };
        }

        return current;
    }

    private static string WrapAnyValueConsumer(string anyValue, bool arrayConsumer, bool expected)
    {
        if (!arrayConsumer)
        {
            return Wrap(TestEnvelope.KeyValue, [new("value", anyValue)], expected);
        }

        var arrayValue = EnvelopeObject(
            TestEnvelope.ArrayValue,
            [new("values", $"[{anyValue}]")],
            expected);
        var outerAnyValue = EnvelopeObject(
            TestEnvelope.AnyValue,
            [new("arrayValue", arrayValue)],
            expected);
        return Wrap(TestEnvelope.KeyValue, [new("value", outerAnyValue)], expected);
    }

    private static JsonElement GetEnvelope(JsonElement request, TestEnvelope envelope)
    {
        if (envelope == TestEnvelope.Request)
        {
            return request;
        }

        var resourceSpans = request.GetProperty("resourceSpans")[0];
        if (envelope == TestEnvelope.ResourceSpans)
        {
            return resourceSpans;
        }
        if (envelope == TestEnvelope.Resource)
        {
            return resourceSpans.GetProperty("resource");
        }
        if (envelope == TestEnvelope.EntityRef)
        {
            return resourceSpans.GetProperty("resource").GetProperty("entityRefs")[0];
        }

        var scopeSpans = resourceSpans.GetProperty("scopeSpans")[0];
        if (envelope == TestEnvelope.ScopeSpans)
        {
            return scopeSpans;
        }
        if (envelope == TestEnvelope.Scope)
        {
            return scopeSpans.GetProperty("scope");
        }

        var span = scopeSpans.GetProperty("spans")[0];
        return envelope switch
        {
            TestEnvelope.Span => span,
            TestEnvelope.Event => span.GetProperty("events")[0],
            TestEnvelope.Link => span.GetProperty("links")[0],
            TestEnvelope.Status => span.GetProperty("status"),
            TestEnvelope.KeyValue => span.GetProperty("attributes")[0],
            TestEnvelope.AnyValue => span.GetProperty("attributes")[0].GetProperty("value"),
            TestEnvelope.ArrayValue => span.GetProperty("attributes")[0].GetProperty("value").GetProperty("arrayValue"),
            TestEnvelope.KeyValueList => span.GetProperty("attributes")[0].GetProperty("value").GetProperty("kvlistValue"),
            _ => throw new ArgumentOutOfRangeException(nameof(envelope)),
        };
    }

    private static void AssertPropertyOrder(string rawJson, params string[] propertyNames)
    {
        var priorIndex = -1;
        foreach (var propertyName in propertyNames)
        {
            var currentIndex = rawJson.IndexOf($"\"{propertyName}\"", StringComparison.Ordinal);
            Assert.True(currentIndex > priorIndex, $"Property {propertyName} was out of order in {rawJson}.");
            priorIndex = currentIndex;
        }
    }

    private static string EnvelopeObject(
        TestEnvelope envelope,
        IReadOnlyList<TestProperty> properties,
        bool expected)
    {
        var emitted = new List<TestProperty>(properties);
        if (expected && FallbackArrays.TryGetValue(envelope, out var fallbackNames))
        {
            foreach (var fallbackName in fallbackNames)
            {
                if (!emitted.Any(property => property.Name == fallbackName))
                {
                    emitted.Add(new(fallbackName, "[]"));
                }
            }
        }

        return $"{{{string.Join(',', emitted.Select(property =>
            $"{JsonSerializer.Serialize(property.Name)}:{property.RawJson}"))}}}";
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            $"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private sealed record TestField(
        TestEnvelope Envelope,
        string JsonName,
        TestRepresentation Representation,
        TestDisposition Disposition,
        TestEnvelope? ChildEnvelope,
        bool TraceIgnored,
        bool EmitEmptyArrayWhenAbsent)
    {
        public string Key => $"{Envelope}.{JsonName}";
    }

    private sealed record TestProperty(string Name, string RawJson);

    private enum TestEnvelope
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

    private enum TestRepresentation
    {
        Object,
        Array,
        String,
        Boolean,
        Number,
        DecimalString,
    }

    private enum TestDisposition
    {
        Value,
        ChildEnvelope,
        ProducerName,
    }

    private enum TestJsonKind
    {
        Object,
        Array,
        String,
        Boolean,
        Number,
        Null,
    }

    private enum TestPlacement
    {
        First,
        Middle,
        Last,
    }
}
