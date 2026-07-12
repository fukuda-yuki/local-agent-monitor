using CopilotAgentObservability.Telemetry;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class SourceCompatibilityTests
{
    private const string KnownFingerprint = "6fb52ac3b851209a6a269c23f956f68f4300a0f6fd92f4bd3ca3860df0a1d76d";
    private const string KnownInventoryHash = "747aa8aadfc45ad2f30abbf4cec80c22cb67e6fa23e5ac5efeda4d0c0c16bd26";
    private const string OtherFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SampleReference = "sample:v1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SpanNameToken = "sha256:d995ea39e1dc64c2cfed6f64c64075d32cac8c88e163423da5dbbf90525518b4";

    [Fact]
    public void OtlpTraceSchema_PinsEveryV110DescriptorRowAndDisposition()
    {
        Assert.Equal("v1.10.0", OtlpTraceSchema.Release);
        Assert.Equal("ca839c51f706f5d53bfb46f06c3e90c3af3a52c6", OtlpTraceSchema.Commit);

        var actual = OtlpTraceSchema.Fields.Select(field => string.Join('|',
            field.Envelope,
            field.JsonName,
            field.SemanticType,
            field.ProtobufTag,
            field.ProtobufWireType,
            field.FieldCode,
            field.ChildEnvelope?.ToString() ?? "-",
            field.ProducerRole?.ToString() ?? "-",
            field.JsonRepresentation,
            field.Disposition));

        Assert.Equal([
            "Request|resourceSpans|Array|1|LengthDelimited|request.resource_spans|ResourceSpans|-|Array|ChildEnvelope",
            "ResourceSpans|resource|Object|1|LengthDelimited|resource_spans.resource|Resource|-|Object|ChildEnvelope",
            "ResourceSpans|scopeSpans|Array|2|LengthDelimited|resource_spans.scope_spans|ScopeSpans|-|Array|ChildEnvelope",
            "ResourceSpans|schemaUrl|String|3|LengthDelimited|resource_spans.schema_url|-|SchemaUrl|String|ProducerName",
            "Resource|attributes|Array|1|LengthDelimited|resource.attributes|KeyValue|-|Array|ChildEnvelope",
            "Resource|droppedAttributesCount|Int|2|Varint|resource.dropped_attributes_count|-|-|Number|Value",
            "Resource|entityRefs|Array|3|LengthDelimited|resource.entity_refs|EntityRef|-|Array|ChildEnvelope",
            "ScopeSpans|scope|Object|1|LengthDelimited|scope_spans.scope|Scope|-|Object|ChildEnvelope",
            "ScopeSpans|spans|Array|2|LengthDelimited|scope_spans.spans|Span|-|Array|ChildEnvelope",
            "ScopeSpans|schemaUrl|String|3|LengthDelimited|scope_spans.schema_url|-|SchemaUrl|String|ProducerName",
            "Scope|name|String|1|LengthDelimited|scope.name|-|-|String|Value",
            "Scope|version|String|2|LengthDelimited|scope.version|-|-|String|Value",
            "Scope|attributes|Array|3|LengthDelimited|scope.attributes|KeyValue|-|Array|ChildEnvelope",
            "Scope|droppedAttributesCount|Int|4|Varint|scope.dropped_attributes_count|-|-|Number|Value",
            "Span|traceId|Bytes|1|LengthDelimited|span.trace_id|-|-|String|Value",
            "Span|spanId|Bytes|2|LengthDelimited|span.span_id|-|-|String|Value",
            "Span|traceState|String|3|LengthDelimited|span.trace_state|-|-|String|Value",
            "Span|parentSpanId|Bytes|4|LengthDelimited|span.parent_span_id|-|-|String|Value",
            "Span|name|String|5|LengthDelimited|span.name|-|SpanName|String|ProducerName",
            "Span|kind|Int|6|Varint|span.kind|-|-|Number|Value",
            "Span|startTimeUnixNano|Int|7|Fixed64|span.start_time_unix_nano|-|-|DecimalString|Value",
            "Span|endTimeUnixNano|Int|8|Fixed64|span.end_time_unix_nano|-|-|DecimalString|Value",
            "Span|attributes|Array|9|LengthDelimited|span.attributes|KeyValue|-|Array|ChildEnvelope",
            "Span|droppedAttributesCount|Int|10|Varint|span.dropped_attributes_count|-|-|Number|Value",
            "Span|events|Array|11|LengthDelimited|span.events|Event|-|Array|ChildEnvelope",
            "Span|droppedEventsCount|Int|12|Varint|span.dropped_events_count|-|-|Number|Value",
            "Span|links|Array|13|LengthDelimited|span.links|Link|-|Array|ChildEnvelope",
            "Span|droppedLinksCount|Int|14|Varint|span.dropped_links_count|-|-|Number|Value",
            "Span|status|Object|15|LengthDelimited|span.status|Status|-|Object|ChildEnvelope",
            "Span|flags|Int|16|Fixed32|span.flags|-|-|Number|Value",
            "Event|timeUnixNano|Int|1|Fixed64|event.time_unix_nano|-|-|DecimalString|Value",
            "Event|name|String|2|LengthDelimited|event.name|-|EventName|String|ProducerName",
            "Event|attributes|Array|3|LengthDelimited|event.attributes|KeyValue|-|Array|ChildEnvelope",
            "Event|droppedAttributesCount|Int|4|Varint|event.dropped_attributes_count|-|-|Number|Value",
            "Link|traceId|Bytes|1|LengthDelimited|link.trace_id|-|-|String|Value",
            "Link|spanId|Bytes|2|LengthDelimited|link.span_id|-|-|String|Value",
            "Link|traceState|String|3|LengthDelimited|link.trace_state|-|-|String|Value",
            "Link|attributes|Array|4|LengthDelimited|link.attributes|KeyValue|-|Array|ChildEnvelope",
            "Link|droppedAttributesCount|Int|5|Varint|link.dropped_attributes_count|-|-|Number|Value",
            "Link|flags|Int|6|Fixed32|link.flags|-|-|Number|Value",
            "Status|message|String|2|LengthDelimited|status.message|-|-|String|Value",
            "Status|code|Int|3|Varint|status.code|-|-|Number|Value",
            "KeyValue|key|String|1|LengthDelimited|key_value.key|-|AttributeKey|String|ProducerName",
            "KeyValue|value|Object|2|LengthDelimited|key_value.value|AnyValue|-|Object|ChildEnvelope",
            "AnyValue|stringValue|String|1|LengthDelimited|any_value.string|-|-|String|Value",
            "AnyValue|boolValue|Bool|2|Varint|any_value.bool|-|-|Boolean|Value",
            "AnyValue|intValue|Int|3|Varint|any_value.int|-|-|DecimalString|Value",
            "AnyValue|doubleValue|Double|4|Fixed64|any_value.double|-|-|Number|Value",
            "AnyValue|arrayValue|Object|5|LengthDelimited|any_value.array|ArrayValue|-|Object|ChildEnvelope",
            "AnyValue|kvlistValue|Object|6|LengthDelimited|any_value.kvlist|KeyValueList|-|Object|ChildEnvelope",
            "AnyValue|bytesValue|Bytes|7|LengthDelimited|any_value.bytes|-|-|String|Value",
            "AnyValue|stringValueStrindex|Int|8|Varint|any_value.string_strindex|-|-|Number|TraceIgnored",
            "ArrayValue|values|Array|1|LengthDelimited|array_value.values|AnyValue|-|Array|ChildEnvelope",
            "KeyValueList|values|Array|1|LengthDelimited|key_value_list.values|KeyValue|-|Array|ChildEnvelope",
            "KeyValue|keyStrindex|Int|3|Varint|key_value.key_strindex|-|-|Number|TraceIgnored",
            "EntityRef|schemaUrl|String|1|LengthDelimited|entity_ref.schema_url|-|SchemaUrl|String|ProducerName",
            "EntityRef|type|String|2|LengthDelimited|entity_ref.type|-|-|String|Value",
            "EntityRef|idKeys|Array|3|LengthDelimited|entity_ref.id_keys|-|AttributeKey|Array|ProducerName",
            "EntityRef|descriptionKeys|Array|4|LengthDelimited|entity_ref.description_keys|-|AttributeKey|Array|ProducerName",
        ], actual);
    }

    [Fact]
    public void Build_UnknownJsonFieldAtEachEnvelope_IsCapturedAndSanitized()
    {
        var inventory = OtlpJsonStructuralWalker.Build(AllEnvelopeUnknownJson, DateTimeOffset.UnixEpoch);

        Assert.True(inventory.HasRequiredTraceSignal);
        Assert.Equal(0, inventory.UnknownSpanCount);
        Assert.Equal(0, inventory.UnknownEventCount);
        Assert.Equal(14, inventory.UnknownAttributeCount);
        Assert.Equal([
            "json:any_value:property:sha256:90fedde721df533f2c7c727d27a5749c8bd6702fd0abc9c67d9ba0792dcfd86d:type:string",
            "json:array_value:property:sha256:290fcc2f12b15435468ad14abee74035303ab77615c77db5c85864f481c33f10:type:string",
            "json:entity_ref:property:sha256:d5d12dabd49e6b8ba2c98828d4e034524dee8cd91fe581397fc5ed1f227d812c:type:string",
            "json:event:property:sha256:e2e2696ffbb2afa254a03e4b4a1dba0aabacaa785dbb0d9137c3d669e9495adb:type:string",
            "json:key_value:property:sha256:8d412e8710d6d86992d9277761d4982f51df92ead8a6cf6089b83e05293e7444:type:string",
            "json:key_value_list:property:sha256:851e9677c5e81949dbaf80124f4659397ddeefd78313aa3f7c7dc4ff4398dfa9:type:string",
            "json:link:property:sha256:66685c8f5f3f3c93c922f029776b27c418fd764192fa9ad05b379307c65bf1dd:type:string",
            "json:request:property:sha256:0b40998b977fbb764a9e9a24ba7c10903b8bd1029dc04875bc57c8aa4f5525df:type:string",
            "json:resource:property:sha256:958183853e05083174099ae1396b4e12315f0a79e73372e83737b7a0ce11c9be:type:string",
            "json:resource_spans:property:sha256:12b699bbb63d4fa4919f4224aa9ea839c2e314030a0014d15203e62563f5339e:type:string",
            "json:scope:property:sha256:e96d9bc57218ba765752cc7dd7be72df4481c64705ff1e5177d39259c551d8cb:type:string",
            "json:scope_spans:property:sha256:7794887b95d13bccd951f337a3e327a7a3bc033fb3b494517ebe1e4e3e42276c:type:string",
            "json:span:property:sha256:29131b9607dcff8c7f26b573e50a4c989f0ba2e73ddfafeb5c49464785e8d8a1:type:string",
            "json:status:property:sha256:f924276c473f2779d199a76b9f3d3f4dfbd54d793b3f81d47393f8c1875dd5f5:type:string",
        ], inventory.RetainedUnknownIdentities.Select(item => item.Name.Value).Order(StringComparer.Ordinal));

        var serialized = JsonSerializer.Serialize(inventory);
        Assert.DoesNotContain("alice@example.test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("marker.", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_KnownFieldWithWrongJsonType_IsUnknown()
    {
        var inventory = OtlpJsonStructuralWalker.Build(AllKnownFieldsWrongTypeJson, DateTimeOffset.UnixEpoch);
        var expected = WrongTypeFieldCodes
            .Select(item => item.Split('|'))
            .Select(parts => $"json:{parts[0]}:known:{parts[1]}:actual:null")
            .Order(StringComparer.Ordinal);

        Assert.True(inventory.HasRequiredTraceSignal);
        Assert.Equal(0, inventory.UnknownSpanCount);
        Assert.Equal(0, inventory.UnknownEventCount);
        Assert.Equal(59, inventory.UnknownAttributeCount);
        Assert.Equal(expected, inventory.RetainedUnknownIdentities.Select(item => item.Name.Value).Order(StringComparer.Ordinal));
        Assert.Contains(inventory.StructuralOccurrences, item => item.Name.Value == "any_value.string_strindex" && item.Unknown is null);
        Assert.Contains(inventory.StructuralOccurrences, item => item.Name.Value == "key_value.key_strindex" && item.Unknown is null);

        Assert.False(OtlpJsonStructuralWalker.Build("""{"resourceSpans":null}""", DateTimeOffset.UnixEpoch).HasRequiredTraceSignal);
        Assert.False(OtlpJsonStructuralWalker.Build(
            """{"resourceSpans":[{"scopeSpans":[{"spans":[false]}]}]}""", DateTimeOffset.UnixEpoch).HasRequiredTraceSignal);
        var optionalMismatch = OtlpJsonStructuralWalker.Build(
            """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":false}]}]}]}""", DateTimeOffset.UnixEpoch);
        Assert.True(optionalMismatch.HasRequiredTraceSignal);
        Assert.Equal(1, optionalMismatch.UnknownAttributeCount);
    }

    [Fact]
    public void Build_DecimalStringsRequireCanonicalLexicalForm()
    {
        var commonMalformed = new[] { "secret-not-number", " 1", "+1", "1.0", "1e3" };
        var cases = new[]
        {
            new DecimalFieldCase(
                "startTimeUnixNano",
                "json:span:known:span.start_time_unix_nano:actual:string",
                "34917ff16f134b2d43240c3f2bebbe074f59f2635868a5e63999ae2007fbb5b2",
                "ecb822efb8b44a2ec42a48d189cef898e0792bee82a8d183147c90162a3ba891",
                [.. commonMalformed, "-1", "18446744073709551616"]),
            new DecimalFieldCase(
                "endTimeUnixNano",
                "json:span:known:span.end_time_unix_nano:actual:string",
                "54aba4fcec123d0f00824c20dafd8b69a4b270bead009e06599bbe849401d99a",
                "3a7a312d86178f360edb5dc65bfbe387e15b74df03b9215260ac99277bbefc66",
                [.. commonMalformed, "-1", "18446744073709551616"]),
            new DecimalFieldCase(
                "event.timeUnixNano",
                "json:event:known:event.time_unix_nano:actual:string",
                "8ab340e4f81ce976843da87d51ef4868fc46e0ff9809831cb6a264d13ff61098",
                "e3f1028b3bf6132c4a689b66e35c794dcb20c6cff5264d5dde344433bf456396",
                [.. commonMalformed, "-1", "18446744073709551616"]),
            new DecimalFieldCase(
                "intValue",
                "json:any_value:known:any_value.int:actual:string",
                "d6a36f3e73a2ecf0219b72a983a1e3b23f55d090862c56c0e38646a77d493408",
                "084f945e17cadcbe73723bda0b522a731f5b01d1738932cfb269e0a8e4151272",
                [.. commonMalformed, "9223372036854775808", "-9223372036854775809"]),
        };

        foreach (var testCase in cases)
        {
            foreach (var malformed in testCase.MalformedValues)
            {
                var inventory = OtlpJsonStructuralWalker.Build(
                    BuildDecimalFieldJson(testCase.JsonField, malformed), DateTimeOffset.UnixEpoch);

                Assert.Equal(1, inventory.UnknownAttributeCount);
                Assert.Equal(testCase.UnknownIdentity, Assert.Single(inventory.RetainedUnknownIdentities).Name.Value);
                Assert.Equal(testCase.SchemaFingerprint, inventory.SchemaFingerprint);
                Assert.Equal(testCase.InventoryHash, inventory.InventoryHash);
                Assert.DoesNotContain(JsonSerializer.Serialize(malformed), JsonSerializer.Serialize(inventory), StringComparison.Ordinal);
            }
        }

        var boundaries = OtlpJsonStructuralWalker.Build(ValidDecimalBoundaryJson, DateTimeOffset.UnixEpoch);
        Assert.False(boundaries.HasUnknownFields);
        Assert.Contains(boundaries.StructuralOccurrences, item => item.Name.Value == "span.start_time_unix_nano" && item.Count.Value == 1);
        Assert.Contains(boundaries.StructuralOccurrences, item => item.Name.Value == "span.end_time_unix_nano" && item.Count.Value == 1);
        Assert.Contains(boundaries.StructuralOccurrences, item => item.Name.Value == "event.time_unix_nano" && item.Count.Value == 1);
        Assert.Contains(boundaries.StructuralOccurrences, item => item.Name.Value == "any_value.int" && item.Count.Value == 2);
    }

    [Fact]
    public void Build_InvalidRepeatedElements_AreKnownWrongTypeUnknowns()
    {
        var inventory = OtlpJsonStructuralWalker.Build(InvalidRepeatedElementJson, DateTimeOffset.UnixEpoch);

        Assert.True(inventory.HasRequiredTraceSignal);
        Assert.Equal(15, inventory.UnknownAttributeCount);
        Assert.Equal([
            "json:array_value:known:array_value.values:actual:bool",
            "json:entity_ref:known:entity_ref.description_keys:actual:double",
            "json:entity_ref:known:entity_ref.id_keys:actual:bool",
            "json:event:known:event.attributes:actual:bool",
            "json:key_value_list:known:key_value_list.values:actual:bool",
            "json:link:known:link.attributes:actual:bool",
            "json:request:known:request.resource_spans:actual:bool",
            "json:resource:known:resource.attributes:actual:bool",
            "json:resource:known:resource.entity_refs:actual:bool",
            "json:resource_spans:known:resource_spans.scope_spans:actual:bool",
            "json:scope:known:scope.attributes:actual:bool",
            "json:scope_spans:known:scope_spans.spans:actual:bool",
            "json:span:known:span.attributes:actual:bool",
            "json:span:known:span.events:actual:bool",
            "json:span:known:span.links:actual:bool",
        ], inventory.RetainedUnknownIdentities.Select(item => item.Name.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(inventory.StructuralOccurrences, item => item.Name.Value == "scope.attributes" && item.Unknown is null);
        Assert.DoesNotContain(inventory.StructuralOccurrences, item => item.Name.Value == "entity_ref.description_keys" && item.Unknown is null);
        Assert.Contains(inventory.StructuralOccurrences, item => item.Name.Value == "request.resource_spans" && item.Count.Value == 1);
        Assert.Contains(inventory.StructuralOccurrences, item => item.Name.Value == "entity_ref.id_keys" && item.Count.Value == 1);
    }

    [Fact]
    public void Build_AllProducerNamesAreHashed()
    {
        var inventory = OtlpJsonStructuralWalker.Build(ProducerNameJson, DateTimeOffset.UnixEpoch);
        var producerTokens = inventory.StructuralOccurrences
            .Where(item => item.Role is SourceStructuralRole.SpanName or SourceStructuralRole.EventName or
                SourceStructuralRole.AttributeKey or SourceStructuralRole.SchemaUrl)
            .Select(item => item.Name.Value)
            .Order(StringComparer.Ordinal);

        Assert.Equal([
            "sha256:0328362eb8689a86b1f7db7e602ec8b5acf01b5743c996e0692b41782c420885",
            "sha256:2b665d62e8019a50fea8046bfb1f9c9ea7300de84888cd22b94b29696d838ce5",
            "sha256:376099db5d52dd47785bdd04157439b8b95a3e120c02ded46141a5605d048839",
            "sha256:bd4e5e35b041d8bd0d7191e1c07e22878c044677eb00ec8f763594ef310c2bde",
            "sha256:c3b4b95ce3c3008d1a1697203af77f0c3b70524b96a3d7c4ac05a1aa44601906",
            "sha256:c89dee60a060422b6fa5d1d524dc0078f5aed26cf2527c34afc1c562653ddd6b",
            "sha256:cecfd4592092db5636bd3d1e9d65aa2a4a990a0bfa18027d35860e79defaa5a3",
            "sha256:d2f670a8182ca2260b52b6ad3c10d00cef277597e95dc3722889f9a340475a98",
            "sha256:dc6651148024139ffdb006ac4f59c2c76bc7296ee2a62bb970f2c5369b070a5e",
            "sha256:eacf8298240520f6379e46320ac7b5ee79e4dc107f7bb9a3ff02755536bb0d7c",
            "sha256:faff47d6528b5ba27ac02742186659440d2f3ed268147362aa798673b8dbafe4",
        ], producerTokens);

        var serialized = JsonSerializer.Serialize(inventory);
        foreach (var literal in new[]
        {
            "semantic.key", "user.email", "eyJhbGciOiJIUzI1NiJ9.payload.signature", "C:\\Users\\alice\\secret.txt",
            "span.alice@example.test", "event.eyJhbGciOiJIUzI1NiJ9", "https://schema.example/private",
            "https://scope-schema.example/private", "https://entity.example/schema", "entity.id", "entity.description",
        })
        {
            Assert.DoesNotContain(literal, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_ValueChangesDoNotChangeEitherHash()
    {
        const string first = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":1,"traceId":"first"}]}]}]}""";
        const string second = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":2,"traceId":"second"}]}]}]}""";

        var firstInventory = OtlpJsonStructuralWalker.Build(first, DateTimeOffset.UnixEpoch);
        var secondInventory = OtlpJsonStructuralWalker.Build(second, DateTimeOffset.UnixEpoch);

        Assert.Equal("c7aa90b94a05ecc07077d5e45f7c51eca33fb353301e7d42afbe7aca3ba61fd7", firstInventory.SchemaFingerprint);
        Assert.Equal("98722a6e1e1be6e64e73e69c420434fa6df1fa5a0a10e797c3d4f8de9e6a7a15", firstInventory.InventoryHash);
        Assert.Equal(firstInventory.SchemaFingerprint, secondInventory.SchemaFingerprint);
        Assert.Equal(firstInventory.InventoryHash, secondInventory.InventoryHash);
    }

    [Fact]
    public void Build_NameOrTypeChangeChangesSchemaFingerprint()
    {
        const string baseline = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":1}]}]}]}""";
        const string changedName = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"beta","kind":1}]}]}]}""";
        const string changedType = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":"one"}]}]}]}""";

        var original = OtlpJsonStructuralWalker.Build(baseline, DateTimeOffset.UnixEpoch);
        var name = OtlpJsonStructuralWalker.Build(changedName, DateTimeOffset.UnixEpoch);
        var type = OtlpJsonStructuralWalker.Build(changedType, DateTimeOffset.UnixEpoch);

        Assert.Equal("076eff06b32922fe65a58b0f989ff1b65a0fd8100cc29530bc88231e5ba14bd4", original.SchemaFingerprint);
        Assert.Equal("b7aece21c0e011279e62bb6973c8986c674d3dbecd181611a0333eec5cceb661", name.SchemaFingerprint);
        Assert.Equal("658f2203cad2e925de97b83ac795f8379d63af251669725d14a94cbec47ffad4", type.SchemaFingerprint);
        Assert.Contains(name.StructuralOccurrences, item => item.Name.Value == "sha256:c175e59a6310d331db4dde00a8dce97af342c126d010a3df179712b2a6736ce0");
        Assert.Contains(type.StructuralOccurrences, item => item.Name.Value == "json:span:known:span.kind:actual:string");
        Assert.DoesNotContain("alpha", JsonSerializer.Serialize(original), StringComparison.Ordinal);
        Assert.DoesNotContain("beta", JsonSerializer.Serialize(name), StringComparison.Ordinal);
        Assert.DoesNotContain("one", JsonSerializer.Serialize(type), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OrderAndCountSemanticsAreIndependent()
    {
        const string ordered = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":1}]}]}]}""";
        const string reordered = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"kind":2,"name":"alpha"}]}]}]}""";
        const string duplicate = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"alpha","kind":1},{"kind":2,"name":"alpha"}]}]}]}""";

        var first = OtlpJsonStructuralWalker.Build(ordered, DateTimeOffset.UnixEpoch);
        var second = OtlpJsonStructuralWalker.Build(reordered, DateTimeOffset.UnixEpoch);
        var counted = OtlpJsonStructuralWalker.Build(duplicate, DateTimeOffset.UnixEpoch);

        Assert.Equal("076eff06b32922fe65a58b0f989ff1b65a0fd8100cc29530bc88231e5ba14bd4", first.SchemaFingerprint);
        Assert.Equal("9a86c4fe92e8190d5d6341aa949e9ce20fb47e87ec7bba6015406edfd6e14861", first.InventoryHash);
        Assert.Equal(first.SchemaFingerprint, second.SchemaFingerprint);
        Assert.Equal(first.InventoryHash, second.InventoryHash);
        Assert.Equal(first.SchemaFingerprint, counted.SchemaFingerprint);
        Assert.Equal("35415ec3ab66df9933f7fec1925ca44f4ad88297636ef38c313440ac55cb78c3", counted.InventoryHash);
    }

    [Fact]
    public void Build_OverflowStillHashesFullStructuralSet()
    {
        var first = OtlpJsonStructuralWalker.Build(BuildOverflowJson("variant-a"), DateTimeOffset.UnixEpoch);
        var second = OtlpJsonStructuralWalker.Build(BuildOverflowJson("variant-b"), DateTimeOffset.UnixEpoch);

        Assert.Equal(256, first.RetainedUnknownIdentities.Count);
        Assert.Equal(1, first.OverflowDistinctCount);
        Assert.Equal(1, first.OverflowOccurrenceCount);
        Assert.Equal(first.RetainedUnknownIdentities.Count, second.RetainedUnknownIdentities.Count);
        Assert.Equal(first.OverflowDistinctCount, second.OverflowDistinctCount);
        Assert.Equal(first.OverflowOccurrenceCount, second.OverflowOccurrenceCount);
        Assert.Equal("cc71b4f64971286b92664a0a44f57a6f6f9042cba5db0adb855570cda9532654", first.SchemaFingerprint);
        Assert.Equal("c1278013a1f235b7c54c6cc99c9378268f63277f983de465a55d0f1a04ba8b56", first.InventoryHash);
        Assert.Equal("a4276b6342fb0ce3b83adc7dea7a2cb8205c3821130efba1067ccb195451b5f9", second.SchemaFingerprint);
        Assert.Equal("bc8ef0397e2a138afde68595c0e18530ed6a455329686c020b251e654f1561ac", second.InventoryHash);
    }

    [Fact]
    public void Build_AggregateUnknownCountsIgnoreRetainedRowLimit()
    {
        var inventory = OtlpJsonStructuralWalker.Build(BuildAggregateUnknownJson(), DateTimeOffset.UnixEpoch);

        Assert.Equal(0, inventory.UnknownSpanCount);
        Assert.Equal(0, inventory.UnknownEventCount);
        Assert.Equal(775, inventory.UnknownAttributeCount);
        Assert.Equal(256, inventory.RetainedUnknownIdentities.Count);
        Assert.Equal(519, inventory.OverflowDistinctCount);
        Assert.Equal(519, inventory.OverflowOccurrenceCount);
        Assert.Equal("22d44529e9256364fca7c78bd43abed14ed2c265262795e3096b49786018a524", inventory.SchemaFingerprint);
        Assert.Equal("1e985795d492ce6a8699d5fe896189f5da9c815b5401f9ca959a18fa91a1e896", inventory.InventoryHash);
        Assert.DoesNotContain("alice@example.test", JsonSerializer.Serialize(inventory), StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryFactory_RejectsInvalidAndDefensivelyCopies()
    {
        var occurrences = new List<SourceStructuralOccurrence> { RequestEnvelope() };

        var inventory = SourceStructuralInventory.Create(occurrences, hasRequiredTraceSignal: true);
        occurrences.Clear();

        Assert.Equal(KnownFingerprint, inventory.SchemaFingerprint);
        Assert.Equal(KnownInventoryHash, inventory.InventoryHash);
        Assert.Single(inventory.StructuralOccurrences);
        Assert.False(inventory.HasUnknownFields);
        Assert.Equal(0, inventory.UnknownSpanCount);
        Assert.Equal(0, inventory.UnknownEventCount);
        Assert.Equal(0, inventory.UnknownAttributeCount);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<SourceStructuralOccurrence>>(inventory.StructuralOccurrences).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceStructuralOccurrence.Create(
            (SourceStructuralEnvelope)999,
            SourceStructuralRole.Envelope,
            SourceStructuralNameToken.ParseCanonical("request"),
            SourceStructuralType.Object,
            SourceOccurrenceCount.Create(1)));
        Assert.Throws<ArgumentException>(() => SourceStructuralNameToken.ParseCanonical("alice@example.test"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceOccurrenceCount.Create(0));
    }

    [Fact]
    public void Registry_RejectsDuplicateAndConflictingEvidence()
    {
        var fingerprints = new List<VerifiedSourceFingerprintEvidence>
        {
            VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
        };
        var profiles = new List<SourceRecognitionProfileEvidence>
        {
            SourceRecognitionProfileEvidence.Create("claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(2)),
        };
        var registry = VerifiedSourceFingerprintRegistry.Create(fingerprints, [], profiles);
        fingerprints.Clear();
        profiles.Clear();

        Assert.True(registry.IsKnownFingerprint("claude-code", KnownFingerprint));
        Assert.True(registry.TryGetRecognitionProfile("claude-code", "1.0", KnownFingerprint, out var profile));
        Assert.Equal(2, profile.ExpectedRecognizedCount.Value);
        Assert.Throws<ArgumentException>(() => VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
            ], [], []));
        Assert.Throws<ArgumentException>(() => VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", OtherFingerprint),
            ], [], []));
        Assert.Throws<ArgumentException>(() => VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint)],
            [IncompatibleSourceVersionEvidence.Create("claude-code", "1.0")],
            []));
        Assert.Throws<ArgumentException>(() => VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint)],
            [],
            [
                SourceRecognitionProfileEvidence.Create("claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(1)),
                SourceRecognitionProfileEvidence.Create("claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(2)),
            ]));
        Assert.Throws<ArgumentException>(() => VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
                VerifiedSourceFingerprintEvidence.Create("claude-code", "2.0", KnownFingerprint),
            ],
            [],
            [
                SourceRecognitionProfileEvidence.Create("claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(1)),
                SourceRecognitionProfileEvidence.Create("claude-code", "2.0", KnownFingerprint, SourceOccurrenceCount.Create(2)),
            ]));
    }

    [Fact]
    public void ObservationFactory_StateReasonAndCaptureAreClosed()
    {
        var inventory = SourceStructuralInventory.Create([RequestEnvelope()], hasRequiredTraceSignal: true);
        var registry = Registry(expectedRecognizedCount: 1);
        var decision = SourceCompatibilityEvaluator.Assess("claude-code", "unverified", inventory, 1, registry);

        var observation = SourceObservationBatchDraft.Create(
            "batch-1", "claude-code", "unverified", "claude-code-otel", "adapter-1",
            inventory, decision, SourceCaptureContentState.Available, DateTimeOffset.UnixEpoch);
        var parseFailure = SourceAdapterFailureDraft.CreateParseFailure(
            "failure-1", null, "claude-code", null, "claude-code-otel", "adapter-1", null, DateTimeOffset.UnixEpoch);
        var exceptionFailure = SourceAdapterFailureDraft.CreateAdapterException(
            "failure-2", null, null, null, null, null, SourceCaptureContentState.Redacted, DateTimeOffset.UnixEpoch);

        Assert.Equal("supported", JsonSerializer.Serialize(observation.CompatibilityState).Trim('"'));
        Assert.Empty(observation.ReasonCodes);
        Assert.Equal("none", observation.NextAction);
        Assert.Equal(KnownFingerprint, observation.SchemaFingerprint);
        Assert.Equal(KnownInventoryHash, observation.InventoryHash);
        Assert.Equal("adapter_parse_failure", Assert.Single(parseFailure.ReasonCodes));
        Assert.Equal("validate_payload_and_protocol", parseFailure.NextAction);
        Assert.Equal("adapter_exception", Assert.Single(exceptionFailure.ReasonCodes));
        Assert.Equal("inspect_sanitized_adapter_failure", exceptionFailure.NextAction);
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceObservationBatchDraft.Create(
            "batch-1", "claude-code", null, "claude-code-otel", "adapter-1", inventory, decision,
            (SourceCaptureContentState)999, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceAdapterFailureDraft.CreateParseFailure(
            "failure-3", null, null, null, null, null, (SourceCaptureContentState)999, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void UnknownFactory_ValidatesNameCountTimeAndReference()
    {
        var first = DateTimeOffset.UnixEpoch;
        var last = first.AddSeconds(1);
        var unknown = SourceUnknownIdentity.Create(
            SourceUnknownKind.Span,
            SourceStructuralNameToken.ParseCanonical(SpanNameToken),
            SourceOccurrenceCount.Create(1),
            first,
            last,
            SampleReference);

        Assert.Equal("span", JsonSerializer.Serialize(unknown.Kind).Trim('"'));
        Assert.Equal(SpanNameToken, unknown.Name.Value);
        Assert.Equal(1, unknown.Count.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceUnknownIdentity.Create(
            (SourceUnknownKind)999, unknown.Name, unknown.Count, first, last, SampleReference));
        Assert.Throws<ArgumentException>(() => SourceUnknownIdentity.Create(
            SourceUnknownKind.Attribute, SourceStructuralNameToken.ParseCanonical("span.name"), unknown.Count, first, last, SampleReference));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceOccurrenceCount.Create(1_000_001));
        Assert.Throws<ArgumentException>(() => SourceUnknownIdentity.Create(
            SourceUnknownKind.Span, unknown.Name, unknown.Count, last, first, SampleReference));
        Assert.Throws<ArgumentException>(() => SourceUnknownIdentity.Create(
            SourceUnknownKind.Span, unknown.Name, unknown.Count, first, last, "sample:v1:not-hex"));
    }

    [Fact]
    public void ReasonSet_DeduplicatesAndOrdersHardCodedVocabulary()
    {
        var reasons = SourceReasonSet.Create([
            "adapter_exception",
            "schema_drift_detected",
            "unknown_fields_observed",
            "adapter_parse_failure",
            "schema_drift_detected",
            "recognized_record_drop_detected",
            "unsupported_source_version",
        ]);

        Assert.Equal([
            "unknown_fields_observed",
            "unsupported_source_version",
            "schema_drift_detected",
            "recognized_record_drop_detected",
            "adapter_parse_failure",
            "adapter_exception",
        ], reasons.Values);
        Assert.Throws<ArgumentException>(() => SourceReasonSet.Create(["caller_defined_reason"]));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(reasons.Values).Add("adapter_exception"));
    }

    [Fact]
    public void Assess_VersionAndFingerprintPolicy()
    {
        var known = SourceStructuralInventory.Create([RequestEnvelope()], hasRequiredTraceSignal: true);
        var unknownFingerprint = SourceStructuralInventory.Create([
            RequestEnvelope(),
            SourceStructuralOccurrence.Create(
                SourceStructuralEnvelope.Span,
                SourceStructuralRole.SpanName,
                SourceStructuralNameToken.ParseCanonical(SpanNameToken),
                SourceStructuralType.String,
                SourceOccurrenceCount.Create(1)),
        ], hasRequiredTraceSignal: true);
        var missingSignal = SourceStructuralInventory.Create([RequestEnvelope()], hasRequiredTraceSignal: false);
        var registry = Registry(expectedRecognizedCount: 1);

        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "unverified", known, 1, registry),
            SourceCompatibilityState.Supported, null, "none");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "1.0", unknownFingerprint, 1, registry),
            SourceCompatibilityState.SchemaDriftDetected, "schema_drift_detected", "capture_fixture_and_review_mapping");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "blocked", known, 1, registry),
            SourceCompatibilityState.UnsupportedSourceVersion, "unsupported_source_version", "use_compatible_source_or_update_adapter");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", null, missingSignal, 1, registry),
            SourceCompatibilityState.UnsupportedSourceVersion, "unsupported_source_version", "use_compatible_source_or_update_adapter");
    }

    [Fact]
    public void Assess_CombinedConditionsFollowCanonicalPrecedence()
    {
        var unknown = SourceUnknownIdentity.Create(
            SourceUnknownKind.Attribute,
            SourceStructuralNameToken.ParseCanonical("protobuf:span:field:99:wire:varint"),
            SourceOccurrenceCount.Create(1),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            SampleReference);
        var unknownOccurrence = SourceStructuralOccurrence.Create(
            SourceStructuralEnvelope.Span,
            SourceStructuralRole.UnknownProtobufField,
            unknown.Name,
            SourceStructuralType.Varint,
            unknown.Count,
            unknown);
        var driftWithUnknown = SourceStructuralInventory.Create([RequestEnvelope(), unknownOccurrence], hasRequiredTraceSignal: true);
        var missingSignalWithDrift = SourceStructuralInventory.Create([RequestEnvelope(), unknownOccurrence], hasRequiredTraceSignal: false);
        var registry = Registry(expectedRecognizedCount: 2);
        var knownUnknownRegistry = VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint),
                VerifiedSourceFingerprintEvidence.Create("claude-code", "2.0", driftWithUnknown.SchemaFingerprint),
            ],
            [IncompatibleSourceVersionEvidence.Create("claude-code", "blocked")],
            [SourceRecognitionProfileEvidence.Create("claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(2))]);

        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "1.0", driftWithUnknown, 1, registry),
            SourceCompatibilityState.RecognizedRecordDropDetected,
            "recognized_record_drop_detected",
            "restore_mapping_or_update_versioned_golden");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "blocked", missingSignalWithDrift, 1, registry),
            SourceCompatibilityState.UnsupportedSourceVersion,
            "unsupported_source_version",
            "use_compatible_source_or_update_adapter");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "2.0", driftWithUnknown, 2, knownUnknownRegistry),
            SourceCompatibilityState.SupportedWithUnknownFields,
            "unknown_fields_observed",
            "review_unknown_fields");
        AssertDecision(
            SourceCompatibilityEvaluator.Assess("claude-code", "unverified", driftWithUnknown, 2, registry),
            SourceCompatibilityState.SchemaDriftDetected,
            "schema_drift_detected",
            "capture_fixture_and_review_mapping");
    }

    private const string AllEnvelopeUnknownJson = """
        {
          "request.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.request.secret",
          "resourceSpans": [{
            "resource_spans.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.resource_spans.secret",
            "schemaUrl": "https://schema.example/private",
            "resource": {
              "resource.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.resource.secret",
              "attributes": [{
                "key_value.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.key_value.secret",
                "key": "semantic.key",
                "value": {
                  "any_value.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.any_value.secret",
                  "arrayValue": {
                    "array_value.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.array_value.secret",
                    "values": [{
                      "kvlistValue": {
                        "key_value_list.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.key_value_list.secret",
                        "values": [{}]
                      }
                    }]
                  }
                }
              }],
              "entityRefs": [{
                "entity_ref.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.entity_ref.secret",
                "schemaUrl": "https://entity.example/schema",
                "type": "service",
                "idKeys": ["entity.id"],
                "descriptionKeys": ["entity.description"]
              }]
            },
            "scopeSpans": [{
              "scope_spans.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.scope_spans.secret",
              "schemaUrl": "https://scope-schema.example/private",
              "scope": {
                "scope.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.scope.secret",
                "name": "scope marker"
              },
              "spans": [{
                "span.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.span.secret",
                "name": "span.alice@example.test",
                "events": [{
                  "event.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.event.secret",
                  "name": "event.eyJhbGciOiJIUzI1NiJ9"
                }],
                "links": [{
                  "link.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.link.secret"
                }],
                "status": {
                  "status.alice@example.test.eyJhbGciOiJIUzI1NiJ9": "marker.status.secret"
                }
              }]
            }]
          }]
        }
        """;

    private const string ProducerNameJson = """
        {
          "resourceSpans": [{
            "schemaUrl": "https://schema.example/private",
            "resource": {
              "attributes": [
                { "key": "semantic.key", "value": { "stringValue": "secret one" } },
                { "key": "user.email", "value": { "stringValue": "secret two" } },
                { "key": "eyJhbGciOiJIUzI1NiJ9.payload.signature", "value": { "stringValue": "secret three" } },
                { "key": "C:\\Users\\alice\\secret.txt", "value": { "stringValue": "secret four" } }
              ],
              "entityRefs": [{
                "schemaUrl": "https://entity.example/schema",
                "type": "service",
                "idKeys": ["entity.id"],
                "descriptionKeys": ["entity.description"]
              }]
            },
            "scopeSpans": [{
              "schemaUrl": "https://scope-schema.example/private",
              "spans": [{
                "name": "span.alice@example.test",
                "events": [{ "name": "event.eyJhbGciOiJIUzI1NiJ9" }]
              }]
            }]
          }]
        }
        """;

    private const string AllKnownFieldsWrongTypeJson = """
        {
          "resourceSpans": null,
          "resourceSpans": [{
            "resource": null,
            "resource": {
              "attributes": null,
              "attributes": [{
                "key": null,
                "key": "outer.key",
                "value": null,
                "value": {
                  "stringValue": null,
                  "boolValue": null,
                  "intValue": null,
                  "doubleValue": null,
                  "arrayValue": null,
                  "arrayValue": {
                    "values": null,
                    "values": [{}]
                  },
                  "kvlistValue": null,
                  "kvlistValue": {
                    "values": null,
                    "values": [{}]
                  },
                  "bytesValue": null,
                  "stringValueStrindex": null,
                  "stringValueStrindex": 7
                },
                "keyStrindex": null,
                "keyStrindex": 8
              }],
              "droppedAttributesCount": null,
              "entityRefs": null,
              "entityRefs": [{
                "schemaUrl": null,
                "type": null,
                "idKeys": null,
                "descriptionKeys": null
              }]
            },
            "scopeSpans": null,
            "scopeSpans": [{
              "scope": null,
              "scope": {
                "name": null,
                "version": null,
                "attributes": null,
                "attributes": [{}],
                "droppedAttributesCount": null
              },
              "spans": null,
              "spans": [{
                "traceId": null,
                "spanId": null,
                "traceState": null,
                "parentSpanId": null,
                "name": null,
                "kind": null,
                "startTimeUnixNano": null,
                "endTimeUnixNano": null,
                "attributes": null,
                "attributes": [{}],
                "droppedAttributesCount": null,
                "events": null,
                "events": [{
                  "timeUnixNano": null,
                  "name": null,
                  "attributes": null,
                  "attributes": [{}],
                  "droppedAttributesCount": null
                }],
                "droppedEventsCount": null,
                "links": null,
                "links": [{
                  "traceId": null,
                  "spanId": null,
                  "traceState": null,
                  "attributes": null,
                  "attributes": [{}],
                  "droppedAttributesCount": null,
                  "flags": null
                }],
                "droppedLinksCount": null,
                "status": null,
                "status": {
                  "message": null,
                  "code": null
                },
                "flags": null
              }],
              "schemaUrl": null
            }],
            "schemaUrl": null
          }]
        }
        """;

    private const string ValidDecimalBoundaryJson = """
        {
          "resourceSpans": [{
            "resource": {
              "attributes": [
                { "value": { "intValue": "-9223372036854775808" } },
                { "value": { "intValue": "9223372036854775807" } }
              ]
            },
            "scopeSpans": [{
              "spans": [{
                "startTimeUnixNano": "0",
                "endTimeUnixNano": "18446744073709551615",
                "events": [{ "timeUnixNano": "18446744073709551615" }]
              }]
            }]
          }]
        }
        """;

    private const string InvalidRepeatedElementJson = """
        {
          "resourceSpans": [false, {
            "resource": {
              "attributes": [false, {
                "value": {
                  "arrayValue": { "values": [false, {}] },
                  "kvlistValue": { "values": [false, {}] }
                }
              }],
              "entityRefs": [false, {
                "idKeys": ["valid.key", false],
                "descriptionKeys": [1]
              }]
            },
            "scopeSpans": [false, {
              "scope": { "attributes": [false] },
              "spans": [false, {
                "attributes": [false],
                "events": [false, { "attributes": [false] }],
                "links": [false, { "attributes": [false] }]
              }]
            }]
          }]
        }
        """;

    private static readonly string[] WrongTypeFieldCodes =
    [
        "request|request.resource_spans",
        "resource_spans|resource_spans.resource", "resource_spans|resource_spans.scope_spans", "resource_spans|resource_spans.schema_url",
        "resource|resource.attributes", "resource|resource.dropped_attributes_count", "resource|resource.entity_refs",
        "scope_spans|scope_spans.scope", "scope_spans|scope_spans.spans", "scope_spans|scope_spans.schema_url",
        "scope|scope.name", "scope|scope.version", "scope|scope.attributes", "scope|scope.dropped_attributes_count",
        "span|span.trace_id", "span|span.span_id", "span|span.trace_state", "span|span.parent_span_id", "span|span.name", "span|span.kind",
        "span|span.start_time_unix_nano", "span|span.end_time_unix_nano", "span|span.attributes", "span|span.dropped_attributes_count",
        "span|span.events", "span|span.dropped_events_count", "span|span.links", "span|span.dropped_links_count", "span|span.status", "span|span.flags",
        "event|event.time_unix_nano", "event|event.name", "event|event.attributes", "event|event.dropped_attributes_count",
        "link|link.trace_id", "link|link.span_id", "link|link.trace_state", "link|link.attributes", "link|link.dropped_attributes_count", "link|link.flags",
        "status|status.message", "status|status.code",
        "key_value|key_value.key", "key_value|key_value.value",
        "any_value|any_value.string", "any_value|any_value.bool", "any_value|any_value.int", "any_value|any_value.double",
        "any_value|any_value.array", "any_value|any_value.kvlist", "any_value|any_value.bytes", "any_value|any_value.string_strindex",
        "array_value|array_value.values", "key_value_list|key_value_list.values", "key_value|key_value.key_strindex",
        "entity_ref|entity_ref.schema_url", "entity_ref|entity_ref.type", "entity_ref|entity_ref.id_keys", "entity_ref|entity_ref.description_keys",
    ];

    private static string BuildOverflowJson(string finalVariant)
    {
        var builder = new StringBuilder("{");
        for (var index = 0; index < 257; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }
            var name = index == 256
                ? $"overflow.256.{finalVariant}@example.test"
                : $"overflow.{index:D3}.alice@example.test";
            builder.Append(JsonSerializer.Serialize(name)).Append(':').Append(JsonSerializer.Serialize("marker.secret"));
        }
        return builder.Append(",\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{}]}]}]}").ToString();
    }

    private static string BuildAggregateUnknownJson()
    {
        var builder = new StringBuilder("{");
        AppendUnknownProperties(builder, "attribute", 260);
        builder.Append(",\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{");
        AppendUnknownProperties(builder, "span", 257);
        builder.Append(",\"events\":[{");
        AppendUnknownProperties(builder, "event", 258);
        return builder.Append("}]}]}]}]}").ToString();
    }

    private static void AppendUnknownProperties(StringBuilder builder, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }
            builder.Append(JsonSerializer.Serialize($"{prefix}.{index:D3}.alice@example.test"))
                .Append(':')
                .Append(JsonSerializer.Serialize("marker.secret"));
        }
    }

    private static string BuildDecimalFieldJson(string jsonField, string value)
    {
        var encoded = JsonSerializer.Serialize(value);
        return jsonField switch
        {
            "startTimeUnixNano" or "endTimeUnixNano" =>
                $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{{\"{jsonField}\":{encoded}}}]}}]}}]}}",
            "event.timeUnixNano" =>
                $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{{\"events\":[{{\"timeUnixNano\":{encoded}}}]}}]}}]}}]}}",
            "intValue" =>
                $"{{\"resourceSpans\":[{{\"resource\":{{\"attributes\":[{{\"value\":{{\"intValue\":{encoded}}}}}]}}}}]}}",
            _ => throw new ArgumentOutOfRangeException(nameof(jsonField)),
        };
    }

    private sealed record DecimalFieldCase(
        string JsonField,
        string UnknownIdentity,
        string SchemaFingerprint,
        string InventoryHash,
        IReadOnlyList<string> MalformedValues);

    private static SourceStructuralOccurrence RequestEnvelope() => SourceStructuralOccurrence.Create(
        SourceStructuralEnvelope.Request,
        SourceStructuralRole.Envelope,
        SourceStructuralNameToken.ParseCanonical("request"),
        SourceStructuralType.Object,
        SourceOccurrenceCount.Create(1));

    private static VerifiedSourceFingerprintRegistry Registry(int expectedRecognizedCount) =>
        VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("claude-code", "1.0", KnownFingerprint)],
            [IncompatibleSourceVersionEvidence.Create("claude-code", "blocked")],
            [SourceRecognitionProfileEvidence.Create(
                "claude-code", "1.0", KnownFingerprint, SourceOccurrenceCount.Create(expectedRecognizedCount))]);

    private static void AssertDecision(
        SourceCompatibilityDecision actual,
        SourceCompatibilityState expectedState,
        string? expectedReason,
        string expectedAction)
    {
        Assert.Equal(expectedState, actual.State);
        if (expectedReason is null)
        {
            Assert.Empty(actual.ReasonCodes);
        }
        else
        {
            Assert.Equal(expectedReason, Assert.Single(actual.ReasonCodes));
        }
        Assert.Equal(expectedAction, actual.NextAction);
    }
}
