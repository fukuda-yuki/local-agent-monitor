namespace CopilotAgentObservability.Doctor;

public sealed record DoctorCatalogEntry(
    DoctorStateCode StateCode,
    DoctorSeverity Severity,
    DoctorRetryability Retryability,
    DoctorNextAction NextAction,
    IReadOnlyList<DoctorStateCode> ReasonCodes);

public static class DoctorCatalog
{
    public static IReadOnlyList<DoctorCatalogEntry> Entries { get; } =
    [
        Entry(DoctorStateCode.MonitorNotInstalled, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.InstallMonitor),
        Entry(DoctorStateCode.MonitorNotRunning, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.StartMonitor),
        Entry(DoctorStateCode.ReceiverNotBound, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.RestartMonitor),
        Entry(DoctorStateCode.PortOwnedByForeignProcess, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.FreeOrChangePort),
        Entry(DoctorStateCode.EndpointMismatch, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UpdateSourceEndpoint),
        Entry(DoctorStateCode.ProtocolMismatch, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseHttpProtobuf),
        Entry(DoctorStateCode.SignalDisabled, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.EnableTraceSignal),
        Entry(DoctorStateCode.UnsupportedSourceVersion, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseSupportedSourceVersion),
        Entry(DoctorStateCode.FeatureUnavailable, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseSupportedSourceSurface),
        Entry(DoctorStateCode.AgentRestartRequired, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.RestartSourceProcess),
        Entry(DoctorStateCode.EndpointUnreachable, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.VerifyEndpointReachability),
        Entry(DoctorStateCode.PayloadRejected, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.InspectRejectedPayload),
        Entry(DoctorStateCode.RawPersistedProjectionPending, DoctorSeverity.Warning, DoctorRetryability.Automatic, DoctorNextAction.WaitForProjection),
        Entry(DoctorStateCode.ProjectionFailed, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.OpenProjectionDiagnostics),
        Entry(DoctorStateCode.SessionUnbound, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.SelectExactSession),
        Entry(DoctorStateCode.ContentCaptureDisabled, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.EnableContentCaptureIfDesired),
        Entry(DoctorStateCode.SanitizedOnlyRawUnavailable, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.RestartWithoutSanitizedOnlyIfDesired),
        Entry(DoctorStateCode.SchemaDriftDetected, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.ReviewSourceDiagnostics),
        Entry(DoctorStateCode.ReadyNoRealTrace, DoctorSeverity.Info, DoctorRetryability.AfterAction, DoctorNextAction.RunBoundedSourceInteraction),
        Entry(DoctorStateCode.FirstTraceReady, DoctorSeverity.Info, DoctorRetryability.None, DoctorNextAction.OpenVerifiedTraceOrSession),
    ];

    public static DoctorCatalogEntry Get(DoctorStateCode stateCode) => Entries[(int)stateCode];

    private static DoctorCatalogEntry Entry(
        DoctorStateCode stateCode,
        DoctorSeverity severity,
        DoctorRetryability retryability,
        DoctorNextAction nextAction) =>
        new(stateCode, severity, retryability, nextAction, [stateCode]);
}
