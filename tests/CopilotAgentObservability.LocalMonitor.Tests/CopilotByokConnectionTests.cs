using CopilotAgentObservability.LocalMonitor.LocalAi;
using GitHub.Copilot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Affected")]
public sealed class CopilotByokConnectionTests
{
    private const string ProviderId = "14da1935-7eb4-4685-aaa9-e24fad400e03";
    private const string ModelId = "gpt-5.6-luna";
    private const string SelectionId = ProviderId + "/" + ModelId;

    [Fact]
    public void ListModels_MissingRegistry_IsEmptyAndBindReportsMissingRegistry()
    {
        using var home = new TempCopilotHome();
        var connection = new CopilotByokConnectionV1(new CopilotCliByokRegistryV1(home.Path), new MapResolver());
        Assert.False(connection.RegistryPresent);
        Assert.Empty(connection.ListModels());
        Assert.Equal(CopilotByokBindStatusV1.MissingRegistry, connection.Bind(SelectionId).Status);
    }

    [Fact]
    public void ListModels_AndBind_UseTheSameSelectionWhenCredentialResolves()
    {
        using var home = new TempCopilotHome();
        home.WriteRegistry();
        var resolver = new MapResolver { [ProviderId] = "synthetic-key" };
        var connection = new CopilotByokConnectionV1(new CopilotCliByokRegistryV1(home.Path), resolver);
        var listed = Assert.Single(connection.ListModels());
        Assert.Equal(SelectionId, listed.SelectionId);
        Assert.Equal("openai", listed.ProviderType);
        Assert.Equal("https://example.invalid/v1", listed.BaseUrl);
        Assert.True(connection.IsByokSelection(SelectionId));
        var bound = connection.Bind(SelectionId);
        Assert.Equal(CopilotByokBindStatusV1.Bound, bound.Status);
        Assert.Equal(SelectionId, bound.Model?.SelectionId);
        Assert.Equal("openai", bound.Provider?.Type);
        Assert.Equal(ModelId, bound.Provider?.ModelId);
        Assert.Equal(ModelId, bound.Provider?.WireModel);
        Assert.Equal("synthetic-key", bound.Provider?.ApiKey);
        Assert.Equal("responses", bound.Provider?.WireApi);
    }

    [Fact]
    public void Bind_RegistryPresentWithoutCredential_IsCredentialUnavailable()
    {
        using var home = new TempCopilotHome();
        home.WriteRegistry();
        var connection = new CopilotByokConnectionV1(new CopilotCliByokRegistryV1(home.Path), new MapResolver());
        Assert.Equal(SelectionId, Assert.Single(connection.ListModels()).SelectionId);
        var bound = connection.Bind(SelectionId);
        Assert.Equal(CopilotByokBindStatusV1.CredentialUnavailable, bound.Status);
        Assert.Null(bound.Provider);
    }

    [Fact]
    public void ListModels_SkipsGitHubHostedProviderRows()
    {
        using var home = new TempCopilotHome();
        home.WriteRegistry(includeGitHub: true);
        var connection = new CopilotByokConnectionV1(new CopilotCliByokRegistryV1(home.Path), new MapResolver());
        Assert.Equal(SelectionId, Assert.Single(connection.ListModels()).SelectionId);
    }

    private sealed class MapResolver : Dictionary<string, string>, ICopilotByokCredentialResolverV1
    {
        public bool TryResolveApiKey(string providerId, out string? apiKey) => TryGetValue(providerId, out apiKey);
    }

    private sealed class TempCopilotHome : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cao-byok-" + Guid.NewGuid().ToString("N"));

        public TempCopilotHome() => Directory.CreateDirectory(Path);

        public void WriteRegistry(bool includeGitHub = false)
        {
            var database = System.IO.Path.Combine(Path, "data.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE model_providers (
                  id TEXT PRIMARY KEY,
                  name TEXT,
                  created_at TEXT,
                  updated_at TEXT,
                  type TEXT,
                  settings_json TEXT,
                  account_id TEXT
                );
                CREATE TABLE provider_models (
                  id TEXT PRIMARY KEY,
                  provider_id TEXT,
                  model_id TEXT,
                  wire_model TEXT,
                  display_name TEXT,
                  max_prompt_tokens INTEGER,
                  max_output_tokens INTEGER,
                  wire_api_override TEXT,
                  created_at TEXT,
                  updated_at TEXT,
                  supported_reasoning_efforts TEXT
                );
                INSERT INTO model_providers(id, name, type, settings_json)
                VALUES ($id, 'Open-AI-API', 'openai', '{"authKind":"api_key","baseUrl":"https://example.invalid/v1","wireApi":"responses","headersJson":"{}"}');
                INSERT INTO provider_models(id, provider_id, model_id, display_name)
                VALUES ($modelRow, $id, $model, 'gpt-5.6-luna');
                """;
            command.Parameters.AddWithValue("$id", ProviderId);
            command.Parameters.AddWithValue("$modelRow", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$model", ModelId);
            command.ExecuteNonQuery();
            if (!includeGitHub) return;
            using var github = connection.CreateCommand();
            github.CommandText = """
                INSERT INTO model_providers(id, name, type, settings_json)
                VALUES ('github_copilot:test', 'GitHub Copilot', 'github_copilot', '{"authKind":"none","baseUrl":null,"wireApi":"responses"}');
                INSERT INTO provider_models(id, provider_id, model_id, display_name)
                VALUES ($row, 'github_copilot:test', 'gpt-5', 'gpt-5');
                """;
            github.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            github.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
