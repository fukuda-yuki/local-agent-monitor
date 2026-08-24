using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public enum SkillInvocationV2CompatibilityDisposition
{
    Accepted,
    Revoked
}

public sealed record SkillInvocationV2CompatibilityTuple(
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint);

public sealed record SkillInvocationV2CompatibilityRegistryEntry(
    SkillInvocationV2CompatibilityTuple Tuple,
    SkillInvocationV2CompatibilityDisposition Disposition);

public sealed record SkillInvocationV2CompatibilityRegistryRevision(
    int Revision,
    IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> Entries);

public sealed class SkillInvocationV2ArtifactRegistry
{
    private const string SchemaResourceName = "CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2.Artifacts.github-copilot-sdk.skill-invoked.v1.schema.json";
    private const string SidecarResourceName = "CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2.Artifacts.github-copilot-sdk.skill-invoked.v1.schema.sha256";
    private const string RegistryR0001ResourceName = "CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2.Artifacts.compatibility-registry-r0001.json";
    private const string RegistryR0002ResourceName = "CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2.Artifacts.compatibility-registry-r0002.json";
    private const string SchemaFingerprintValue = "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c";
    private const string RegistryR0001Fingerprint = "3ae5d255647edad6e23f077c3e9042be50d593211cd9a90d6c9f7210c53bfdda";
    private const string RegistryR0002Fingerprint = "e3da4e7334f4e1645de315820181d2752f71ddb9aeba4355a659d185165daaf6";
    private const int SchemaByteLength = 980;
    private const int SidecarByteLength = 65;
    private const int RegistryR0001ByteLength = 431;
    private const int RegistryR0002ByteLength = 771;
    private static readonly Lazy<SkillInvocationV2ArtifactRegistry> Current = new(LoadEmbedded);

    private readonly ReadOnlyCollection<SkillInvocationV2CompatibilityRegistryEntry> entries;
    private readonly ReadOnlyCollection<SkillInvocationV2CompatibilityRegistryRevision> history;

    private SkillInvocationV2ArtifactRegistry(
        int currentRevision,
        IReadOnlyList<SkillInvocationV2CompatibilityRegistryRevision> history,
        IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> entries)
    {
        CurrentRevision = currentRevision;
        this.entries = Array.AsReadOnly(entries.ToArray());
        this.history = Array.AsReadOnly(history.ToArray());
    }

    public int CurrentRevision { get; }

    public string SchemaFingerprint => SchemaFingerprintValue;

    public IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> Entries => entries;

    public IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> CurrentEntries => entries;

    public IReadOnlyList<SkillInvocationV2CompatibilityRegistryRevision> History => history;

    public static SkillInvocationV2ArtifactRegistry Load() => Current.Value;

    public bool IsAccepted(SkillInvocationV2CompatibilityTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        return entries.Any(entry => entry.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted && entry.Tuple == tuple);
    }

    private static SkillInvocationV2ArtifactRegistry LoadEmbedded()
    {
        var assembly = typeof(SkillInvocationV2ArtifactRegistry).Assembly;
        return LoadForArtifactValidation(
            ReadResource(assembly, SchemaResourceName),
            ReadResource(assembly, SidecarResourceName),
            new Dictionary<int, byte[]>
            {
                [1] = ReadResource(assembly, RegistryR0001ResourceName),
                [2] = ReadResource(assembly, RegistryR0002ResourceName)
            });
    }

    private static SkillInvocationV2ArtifactRegistry LoadForArtifactValidation(
        byte[] schemaBytes,
        byte[] sidecarBytes,
        IReadOnlyDictionary<int, byte[]> registryHistory)
    {
        ArgumentNullException.ThrowIfNull(schemaBytes);
        ArgumentNullException.ThrowIfNull(sidecarBytes);
        ArgumentNullException.ThrowIfNull(registryHistory);

        AssertExactTextArtifact(schemaBytes, SchemaByteLength, SchemaFingerprintValue);
        if (!sidecarBytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(SchemaFingerprintValue + "\n"))
            || sidecarBytes.Length != SidecarByteLength)
        {
            throw InvalidArtifact();
        }

        if (registryHistory.Count != 2
            || !registryHistory.TryGetValue(1, out var registryR0001)
            || !registryHistory.TryGetValue(2, out var registryR0002)
            || registryR0001 is null
            || registryR0002 is null)
        {
            throw InvalidArtifact();
        }

        ValidateArtifactStructureForTesting(schemaBytes, sidecarBytes, registryHistory);
        AssertExactTextArtifact(registryR0001, RegistryR0001ByteLength, RegistryR0001Fingerprint);
        AssertExactTextArtifact(registryR0002, RegistryR0002ByteLength, RegistryR0002Fingerprint);
        var r0001Entries = ParseRegistry(registryR0001, expectedRevision: 1, SchemaFingerprintValue);
        var r0002Entries = ParseRegistry(registryR0002, expectedRevision: 2, SchemaFingerprintValue);
        var historicalTuple = new SkillInvocationV2CompatibilityTuple(
                "1.0.65",
                "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
                "github-copilot-sdk.skill-invoked.normalize.v1",
                "github-copilot-sdk.skill-invoked.v1",
                SchemaFingerprintValue);
        var firstTuple = historicalTuple with { NormalizationVersion = "github-copilot-sdk.skill-invoked.normalize.v2" };
        var secondTuple = firstTuple with { SourceApplicationVersion = "1.0.75" };
        if (r0001Entries.Count != 1
            || r0001Entries[0] != new SkillInvocationV2CompatibilityRegistryEntry(historicalTuple, SkillInvocationV2CompatibilityDisposition.Accepted)
            || r0002Entries.Count != 2
            || r0002Entries[0] != new SkillInvocationV2CompatibilityRegistryEntry(firstTuple, SkillInvocationV2CompatibilityDisposition.Accepted)
            || r0002Entries[1] != new SkillInvocationV2CompatibilityRegistryEntry(secondTuple, SkillInvocationV2CompatibilityDisposition.Accepted))
        {
            throw InvalidArtifact();
        }

        var history = new[]
        {
            new SkillInvocationV2CompatibilityRegistryRevision(1, Array.AsReadOnly(r0001Entries.ToArray())),
            new SkillInvocationV2CompatibilityRegistryRevision(2, Array.AsReadOnly(r0002Entries.ToArray()))
        };
        return new SkillInvocationV2ArtifactRegistry(2, history, r0002Entries);
    }

    internal static void ValidateArtifactStructureForTesting(
        byte[] schemaBytes,
        byte[] sidecarBytes,
        IReadOnlyDictionary<int, byte[]> registryHistory)
    {
        ArgumentNullException.ThrowIfNull(schemaBytes);
        ArgumentNullException.ThrowIfNull(sidecarBytes);
        ArgumentNullException.ThrowIfNull(registryHistory);

        AssertCanonicalText(schemaBytes);
        var schemaFingerprint = Sha256(schemaBytes);
        if (!sidecarBytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(schemaFingerprint + "\n")))
        {
            throw InvalidArtifact();
        }

        if (registryHistory.Count == 0)
        {
            throw InvalidArtifact();
        }

        var revisions = registryHistory.Keys.OrderBy(revision => revision).ToArray();
        var parsedHistory = new List<(int Revision, IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> Entries)>();
        for (var index = 0; index < revisions.Length; index++)
        {
            var revision = revisions[index];
            if (revision != index + 1 || !registryHistory.TryGetValue(revision, out var registryBytes) || registryBytes is null)
            {
                throw InvalidArtifact();
            }

            parsedHistory.Add((revision, ParseRegistry(registryBytes, revision, schemaFingerprint)));
        }

        foreach (var current in parsedHistory)
        {
            foreach (var entry in current.Entries.Where(entry => entry.Disposition == SkillInvocationV2CompatibilityDisposition.Revoked))
            {
                if (!parsedHistory
                    .Where(previous => previous.Revision < current.Revision)
                    .SelectMany(previous => previous.Entries)
                    .Any(previous => previous.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted && previous.Tuple == entry.Tuple))
                {
                    throw InvalidArtifact();
                }
            }
        }
    }

    private static IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> ParseRegistry(
        byte[] registryBytes,
        int expectedRevision,
        string expectedSchemaFingerprint)
    {
        try
        {
            AssertCanonicalText(registryBytes);
            AssertNoDuplicateProperties(registryBytes);
            using var document = JsonDocument.Parse(registryBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            var root = document.RootElement;
            AssertObjectProperties(root, "schema_version", "registry_revision", "entries");
            if (ReadRequiredString(root, "schema_version") != "skill-sdk-compatibility-registry.v1"
                || !root.GetProperty("registry_revision").TryGetInt32(out var revision)
                || revision != expectedRevision)
            {
                throw InvalidArtifact();
            }

            var entriesElement = root.GetProperty("entries");
            if (entriesElement.ValueKind != JsonValueKind.Array || entriesElement.GetArrayLength() == 0)
            {
                throw InvalidArtifact();
            }

            var entries = new List<SkillInvocationV2CompatibilityRegistryEntry>();
            var tuples = new HashSet<SkillInvocationV2CompatibilityTuple>();
            foreach (var entryElement in entriesElement.EnumerateArray())
            {
                AssertObjectProperties(
                    entryElement,
                    "source_application_version",
                    "adapter_version",
                    "normalization_version",
                    "payload_schema",
                    "schema_fingerprint",
                    "disposition");

                var tuple = new SkillInvocationV2CompatibilityTuple(
                    ReadRequiredString(entryElement, "source_application_version"),
                    ReadRequiredString(entryElement, "adapter_version"),
                    ReadRequiredString(entryElement, "normalization_version"),
                    ReadRequiredString(entryElement, "payload_schema"),
                    ReadRequiredString(entryElement, "schema_fingerprint"));
                if (!IsLowercaseSha256(tuple.SchemaFingerprint) || tuple.SchemaFingerprint != expectedSchemaFingerprint || !tuples.Add(tuple))
                {
                    throw InvalidArtifact();
                }

                var disposition = ReadRequiredString(entryElement, "disposition") switch
                {
                    "accepted" => SkillInvocationV2CompatibilityDisposition.Accepted,
                    "revoked" => SkillInvocationV2CompatibilityDisposition.Revoked,
                    _ => throw InvalidArtifact()
                };
                entries.Add(new SkillInvocationV2CompatibilityRegistryEntry(tuple, disposition));
            }

            return entries;
        }
        catch (JsonException)
        {
            throw InvalidArtifact();
        }
    }

    private static void AssertExactTextArtifact(byte[] bytes, int expectedLength, string expectedHash)
    {
        AssertCanonicalText(bytes);
        if (bytes.Length != expectedLength || !string.Equals(Sha256(bytes), expectedHash, StringComparison.Ordinal))
        {
            throw InvalidArtifact();
        }
    }

    private static void AssertCanonicalText(byte[] bytes)
    {
        if (bytes.Length == 0
            || bytes[0] == 0xef && bytes.Length >= 3 && bytes[1] == 0xbb && bytes[2] == 0xbf
            || bytes[^1] != (byte)'\n'
            || bytes.Contains((byte)'\r'))
        {
            throw InvalidArtifact();
        }
    }

    private static void AssertNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var propertyNames = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    propertyNames.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    if (propertyNames.Count == 0 || !propertyNames.Peek().Add(reader.GetString()!))
                    {
                        throw InvalidArtifact();
                    }

                    break;
                case JsonTokenType.EndObject:
                    if (propertyNames.Count == 0)
                    {
                        throw InvalidArtifact();
                    }

                    propertyNames.Pop();
                    break;
            }
        }

        if (propertyNames.Count != 0)
        {
            throw InvalidArtifact();
        }
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidArtifact();
        }

        var actualProperties = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actualProperties.SetEquals(expectedProperties))
        {
            throw InvalidArtifact();
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw InvalidArtifact();
        }

        return value.GetString()!;
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw InvalidArtifact();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static InvalidOperationException InvalidArtifact() => new("Skill invocation v2 artifacts are unavailable.");
}
