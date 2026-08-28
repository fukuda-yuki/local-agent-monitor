using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Pages;

internal sealed record LocalMonitorV1PageModel(
    LocalMonitorV1PrimaryRouteKind? RouteKind,
    LocalMonitorV1PrimaryPathResult? Path,
    LocalMonitorV1PageQuery? Query,
    string? PageState,
    string? RecoveryAction,
    string? ExplorerScope,
    string? ExplorerHeading)
{
    internal static LocalMonitorV1PageModel Success(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        string? explorerScope = null,
        string? explorerHeading = null) =>
        new(path.RouteKind, path, query, null, null, explorerScope, explorerHeading);

    internal static LocalMonitorV1PageModel Error(string pageState, string recoveryAction) =>
        new(null, null, null, pageState, recoveryAction, null, null);

    internal static LocalMonitorV1PageModel ResolvedError(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        string pageState,
        string recoveryAction) =>
        new(path.RouteKind, path, query, pageState, recoveryAction, null, null);
}
