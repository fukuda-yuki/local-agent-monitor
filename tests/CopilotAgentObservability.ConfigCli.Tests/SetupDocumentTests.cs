using CopilotAgentObservability.ConfigCli.Setup.Documents;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupDocumentTests
{
    private const string InvalidDocumentMessage = "Settings document is malformed or unsupported.";

    [Fact]
    public void Jsonc_ReplaceString_ChangesOnlyExactTopLevelValue()
    {
        const string source = "{\r\n  // preserved\r\n  \"github.copilot.chat.otel.otlpEndpoint\": \"http://old\\u002dhost:4318\",\r\n  \"nested\": { \"github.copilot.chat.otel.otlpEndpoint\": \"unchanged\", \"array\": [1, true, null] },\r\n  \"escaped\\\"key\": \"untouched\",\r\n}\r\n";

        var document = JsoncSettingsDocument.Parse(source);
        var updated = document.ReplaceString("github.copilot.chat.otel.otlpEndpoint", "http://127.0.0.1:4319");

        Assert.Equal(source.Replace("\"http://old\\u002dhost:4318\"", "\"http://127.0.0.1:4319\"", StringComparison.Ordinal), updated);
        Assert.True(JsoncSettingsDocument.Parse(updated).TryGetString("github.copilot.chat.otel.otlpEndpoint", out var value));
        Assert.Equal("http://127.0.0.1:4319", value);
    }

    [Fact]
    public void Jsonc_ReplaceBoolean_PreservesLfAndIsByteIdenticalWhenRepeated()
    {
        const string source = "{\n\t\"github.copilot.chat.otel.enabled\" : false, // keep\n\t\"other\": true\n}\n";

        var updated = JsoncSettingsDocument.Parse(source).ReplaceBoolean("github.copilot.chat.otel.enabled", true);
        var repeated = JsoncSettingsDocument.Parse(updated).ReplaceBoolean("github.copilot.chat.otel.enabled", true);

        Assert.Equal(source.Replace("false", "true", StringComparison.Ordinal), updated);
        Assert.Equal(updated, repeated);
    }

    [Fact]
    public void Jsonc_Read_UsesExactDottedKeyRatherThanNestedPath()
    {
        const string source = "{ \"a.b\": \"top\", \"a\": { \"b\": \"nested\" } }";

        var document = JsoncSettingsDocument.Parse(source);

        Assert.True(document.TryGetString("a.b", out var value));
        Assert.Equal("top", value);
        Assert.False(document.TryGetBoolean("a.b", out _));
    }

    [Theory]
    [InlineData("{\n  \"first\": true, // first comment\n  \"middle\": false,\n  \"last\": true\n}\n", "first", "{\n   // first comment\n  \"middle\": false,\n  \"last\": true\n}\n")]
    [InlineData("{\n  \"first\": true /* before comma */,\n  \"last\": true\n}\n", "first", "{\n   /* before comma */\n  \"last\": true\n}\n")]
    [InlineData("{\n  \"first\": true,\n  // before middle\n  \"middle\": false, // after middle\n  \"last\": true\n}\n", "middle", "{\n  \"first\": true,\n  // before middle\n   // after middle\n  \"last\": true\n}\n")]
    [InlineData("{\n  \"first\": true,\n  \"middle\": false,\n  // before last\n  \"last\": true // last comment\n}\n", "last", "{\n  \"first\": true,\n  \"middle\": false\n  // before last\n   // last comment\n}\n")]
    public void Jsonc_Remove_FirstMiddleOrLast_PreservesComments(string source, string key, string expected)
    {
        Assert.Equal(expected, JsoncSettingsDocument.Parse(source).Remove(key));
    }

    [Fact]
    public void Jsonc_Add_FollowsDetectedCrlfIndentAndColonFormatting()
    {
        const string source = "{\r\n    \"existing\" : true\r\n}\r\n";

        var updated = JsoncSettingsDocument.Parse(source).AddString("github.copilot.chat.otel.otlpEndpoint", "http://127.0.0.1:4319");

        Assert.Equal("{\r\n    \"existing\" : true,\r\n    \"github.copilot.chat.otel.otlpEndpoint\" : \"http://127.0.0.1:4319\"\r\n}\r\n", updated);
    }

    [Fact]
    public void Jsonc_AddBoolean_ToEmptySingleLineObject_IsValidAndStable()
    {
        var updated = JsoncSettingsDocument.Parse("{}").AddBoolean("enabled", true);

        Assert.Equal("{ \"enabled\": true }", updated);
        Assert.Equal(updated, JsoncSettingsDocument.Parse(updated).ReplaceBoolean("enabled", true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"key\": true,, }")]
    [InlineData("{ \"key\": \"unterminated }")]
    public void Jsonc_Parse_MalformedOrNonObject_FailsClosed(string source)
    {
        var exception = Assert.Throws<FormatException>(() => JsoncSettingsDocument.Parse(source));

        Assert.Equal(InvalidDocumentMessage, exception.Message);
    }

    [Fact]
    public void Jsonc_TargetDuplicate_FailsClosedBeforeMutation()
    {
        var document = JsoncSettingsDocument.Parse("{ \"target\": true, \"target\": false }");

        var exception = Assert.Throws<FormatException>(() => document.ReplaceBoolean("target", true));

        Assert.Equal(InvalidDocumentMessage, exception.Message);
    }

    [Theory]
    [MemberData(nameof(CodexTomlSamples))]
    public void Toml_Parse_AcceptsEveryConfigSamplesProducerOutput(string _, string source)
    {
        var document = TomlSettingsDocument.Parse(source);

        if (document.TryGetString("otel", "environment", out var environment))
        {
            Assert.Equal("dev", environment);
            Assert.True(document.TryGetBoolean("otel", "log_user_prompt", out var prompt));
            Assert.False(prompt);
        }
    }

    [Fact]
    public void Toml_ReplaceScalar_ChangesOnlyValueAndIsIdempotent()
    {
        const string source = "# before\r\n[\"otel\"]\r\nenvironment = \"dev\" # keep\r\nlog_user_prompt = false\r\nexporter = { otlp-http = { endpoint = \"http://localhost:4318/v1/logs\", protocol = \"binary\" } }\r\n";

        var changed = TomlSettingsDocument.Parse(source).ReplaceBoolean("otel", "log_user_prompt", true);
        var repeated = TomlSettingsDocument.Parse(changed).ReplaceBoolean("otel", "log_user_prompt", true);

        Assert.Equal(source.Replace("log_user_prompt = false", "log_user_prompt = true", StringComparison.Ordinal), changed);
        Assert.Equal(changed, repeated);
    }

    [Fact]
    public void Toml_AddAndRemove_PreserveUnrelatedLinesCommentsAndNewline()
    {
        const string source = "# header\n[otel]\nenvironment = \"dev\"\n# tail\n[other]\nvalue = true\n";

        var added = TomlSettingsDocument.Parse(source).AddBoolean("otel", "log_user_prompt", false);
        var removed = TomlSettingsDocument.Parse(added).Remove("otel", "environment");

        Assert.Equal("# header\n[otel]\nenvironment = \"dev\"\n# tail\nlog_user_prompt = false\n[other]\nvalue = true\n", added);
        Assert.Equal("# header\n[otel]\n# tail\nlog_user_prompt = false\n[other]\nvalue = true\n", removed);
    }

    [Fact]
    public void Toml_AddTable_WhenAbsent_PreservesExistingFinalLine()
    {
        const string source = "# existing without newline";

        var updated = TomlSettingsDocument.Parse(source).AddString("otel", "environment", "dev");

        Assert.Equal("# existing without newline\n[otel]\nenvironment = \"dev\"", updated);
    }

    [Fact]
    public void Toml_QuotedKeysAndNestedInlineTables_RoundTripWithoutRewriting()
    {
        const string source = "[\"otel\"]\n\"trace_exporter\" = { \"otlp-http\" = { endpoint = \"http://127.0.0.1:4319/v1/traces\", headers = { Authorization = \"Basic token\" } } } # untouched\n";

        var document = TomlSettingsDocument.Parse(source);

        Assert.Equal(source, document.Content);
        Assert.False(document.TryGetString("otel", "trace_exporter", out _));
    }

    [Theory]
    [InlineData("[otel]\nvalue = [\"x\"]\n")]
    [InlineData("[[otel]]\nvalue = true\n")]
    [InlineData("[otel]\nvalue = 1\n")]
    [InlineData("[otel]\nvalue = 1979-05-27T07:32:00Z\n")]
    [InlineData("[otel]\nvalue = \"\"\"multi\nline\"\"\"\n")]
    [InlineData("[otel]\na.b = true\n")]
    [InlineData("[otel.child]\nvalue = true\n")]
    [InlineData("[otel]\nvalue = \"bad\\qescape\"\n")]
    [InlineData("[otel]\nvalue = { nested = { ok = true }, broken }\n")]
    [InlineData("[otel]\nvalue = true\nvalue = false\n")]
    [InlineData("[otel]\nvalue = { duplicate = true, duplicate = false }\n")]
    public void Toml_Parse_UnsupportedMalformedAmbiguousOrDuplicate_FailsClosed(string source)
    {
        var exception = Assert.Throws<FormatException>(() => TomlSettingsDocument.Parse(source));

        Assert.Equal(InvalidDocumentMessage, exception.Message);
    }

    public static TheoryData<string, string> CodexTomlSamples => new()
    {
        { nameof(ConfigSamples.CreateLangfuseCodexAppConfigToml), ConfigSamples.CreateLangfuseCodexAppConfigToml() },
        { nameof(ConfigSamples.CreateCollectorCodexAppConfigToml), ConfigSamples.CreateCollectorCodexAppConfigToml() },
        { CollectionProfileOptions.RawOnly, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RawOnly) },
        { CollectionProfileOptions.DockerDesktopLangfuse, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.DockerDesktopLangfuse) },
        { CollectionProfileOptions.DockerDesktopCollectorLangfuse, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.DockerDesktopCollectorLangfuse) },
        { CollectionProfileOptions.Wsl2DockerLangfuse, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.Wsl2DockerLangfuse) },
        { CollectionProfileOptions.Wsl2DockerCollectorLangfuse, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.Wsl2DockerCollectorLangfuse) },
        { CollectionProfileOptions.RemoteManagedLangfuse, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RemoteManagedLangfuse) },
        { CollectionProfileOptions.RemoteManagedCollector, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RemoteManagedCollector) },
        { CollectionProfileOptions.RawLocalReceiver, ConfigSamples.CreateProfileCodexAppConfigToml(CollectionProfileOptions.RawLocalReceiver) },
    };
}
