using System.Runtime.InteropServices;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record CopilotByokModelV1(
    string SelectionId,
    string DisplayName,
    string ProviderId,
    string ProviderName,
    string ProviderType,
    string WireApi,
    string BaseUrl,
    string ModelId,
    string? AzureApiVersion);

internal enum CopilotByokBindStatusV1
{
    Bound,
    UnknownModel,
    MissingRegistry,
    CredentialUnavailable,
}

internal sealed record CopilotByokBindResultV1(
    CopilotByokBindStatusV1 Status,
    CopilotByokModelV1? Model,
    GitHub.Copilot.ProviderConfig? Provider);

internal interface ICopilotByokCredentialResolverV1
{
    bool TryResolveApiKey(string providerId, out string? apiKey);
}

internal interface ICopilotByokConnectionV1
{
    bool RegistryPresent { get; }
    IReadOnlyList<CopilotByokModelV1> ListModels();
    bool IsByokSelection(string selectionId);
    CopilotByokBindResultV1 Bind(string selectionId);
}

internal sealed class CopilotCliByokRegistryV1(string copilotHome)
{
    internal static CopilotCliByokRegistryV1 FromUserProfile() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"));

    internal bool DatabaseExists => File.Exists(Path.Combine(copilotHome, "data.db"));

    internal IReadOnlyList<CopilotByokModelV1> ListModels()
    {
        var database = Path.Combine(copilotHome, "data.db");
        if (!File.Exists(database)) return [];
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(database),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.id, p.name, p.type, p.settings_json, m.model_id, m.display_name, m.wire_model
                FROM provider_models m
                JOIN model_providers p ON p.id = m.provider_id
                WHERE p.type IS NOT NULL AND p.type <> 'github_copilot'
                ORDER BY p.name, m.display_name, m.model_id;
                """;
            using var reader = command.ExecuteReader();
            var models = new List<CopilotByokModelV1>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var providerId = reader.GetString(0);
                var providerName = reader.GetString(1);
                var providerType = reader.GetString(2);
                var settingsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                var modelId = reader.GetString(4);
                var displayName = reader.IsDBNull(5) ? modelId : reader.GetString(5);
                if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)
                    || providerId.Contains('/', StringComparison.Ordinal)
                    || !LocalAiModelIdentityV1.IsSupportedId(providerId)
                    || !LocalAiModelIdentityV1.IsSupportedId(modelId))
                    continue;
                var selection = providerId + "/" + modelId;
                if (!LocalAiModelIdentityV1.IsSupportedId(selection) || !seen.Add(selection)) continue;
                if (!TryReadSettings(settingsJson, out var baseUrl, out var wireApi, out var azureApiVersion))
                    continue;
                if (string.IsNullOrWhiteSpace(baseUrl)) continue;
                var label = string.IsNullOrWhiteSpace(displayName) ? modelId : displayName;
                models.Add(new(selection, providerName + " / " + label, providerId, providerName, providerType,
                    wireApi, baseUrl, modelId, azureApiVersion));
            }
            return models;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static bool TryReadSettings(string? json, out string baseUrl, out string wireApi, out string? azureApiVersion)
    {
        baseUrl = "";
        wireApi = "completions";
        azureApiVersion = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String)
                baseUrl = url.GetString() ?? "";
            if (root.TryGetProperty("wireApi", out var wire) && wire.ValueKind == JsonValueKind.String
                && wire.GetString() is { Length: > 0 } value
                && (value is "completions" or "responses"))
                wireApi = value;
            if (root.TryGetProperty("azureApiVersion", out var azure) && azure.ValueKind == JsonValueKind.String
                && azure.GetString() is { Length: > 0 } version)
                azureApiVersion = version;
            return !string.IsNullOrWhiteSpace(baseUrl);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class WindowsByokCredentialResolverV1 : ICopilotByokCredentialResolverV1
{
    internal static WindowsByokCredentialResolverV1 Instance { get; } = new();

    public bool TryResolveApiKey(string providerId, out string? apiKey)
    {
        apiKey = null;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(providerId)
            || !LocalAiModelIdentityV1.IsSupportedId(providerId))
            return false;
        return NativeCredentialRead.TryReadGeneric($"byok:{providerId}:apiKey.github-copilot-app", out apiKey)
            && !string.IsNullOrWhiteSpace(apiKey);
    }
}

internal sealed class CopilotByokConnectionV1(
    CopilotCliByokRegistryV1 registry,
    ICopilotByokCredentialResolverV1 credentials) : ICopilotByokConnectionV1
{
    public bool RegistryPresent => registry.DatabaseExists;

    public IReadOnlyList<CopilotByokModelV1> ListModels() => registry.ListModels();

    public bool IsByokSelection(string selectionId) =>
        ListModels().Any(model => string.Equals(model.SelectionId, selectionId, StringComparison.Ordinal));

    public CopilotByokBindResultV1 Bind(string selectionId)
    {
        if (!registry.DatabaseExists) return new(CopilotByokBindStatusV1.MissingRegistry, null, null);
        var model = ListModels().FirstOrDefault(item =>
            string.Equals(item.SelectionId, selectionId, StringComparison.Ordinal));
        if (model is null) return new(CopilotByokBindStatusV1.UnknownModel, null, null);
        if (!credentials.TryResolveApiKey(model.ProviderId, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return new(CopilotByokBindStatusV1.CredentialUnavailable, model, null);
        var provider = new GitHub.Copilot.ProviderConfig
        {
            Type = model.ProviderType,
            BaseUrl = model.BaseUrl,
            WireApi = model.WireApi,
            ApiKey = apiKey,
            ModelId = model.ModelId,
            WireModel = model.ModelId,
        };
        if (model.AzureApiVersion is not null)
            provider.Azure = new AzureOptions { ApiVersion = model.AzureApiVersion };
        return new(CopilotByokBindStatusV1.Bound, model, provider);
    }
}

file static class NativeCredentialRead
{
    private const int CredTypeGeneric = 1;

    internal static bool TryReadGeneric(string target, out string? value)
    {
        value = null;
        if (!CredReadW(target, CredTypeGeneric, 0, out var pointer) || pointer == nint.Zero) return false;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize <= 0) return false;
            var characters = credential.CredentialBlobSize / 2;
            value = Marshal.PtrToStringUni(credential.CredentialBlob, characters)?.TrimEnd('\0');
            return !string.IsNullOrEmpty(value);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out nint credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(nint buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
