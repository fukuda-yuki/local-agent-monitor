namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalRepositoryCatalogConstants
{
    internal const string ComponentName = "local_repository_catalog";
    internal const int Version = 1;
    internal const string ProjectorKey = "local-repository-catalog-v1";
    internal const string ProjectorVersion = "local-repository-catalog:1";
}

internal enum LocalRepositoryCatalogCauseKind
{
    UserOperation,
    SourceContext,
    SourceReconciliation,
}
