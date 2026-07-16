namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorCatalogTests
{
    public static TheoryData<DoctorStateCode, DoctorSeverity, DoctorRetryability, DoctorNextAction> Entries => new()
    {
        { DoctorStateCode.MonitorNotInstalled, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.InstallMonitor },
        { DoctorStateCode.MonitorNotRunning, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.StartMonitor },
        { DoctorStateCode.ReceiverNotBound, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.RestartMonitor },
        { DoctorStateCode.PortOwnedByForeignProcess, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.FreeOrChangePort },
        { DoctorStateCode.EndpointMismatch, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UpdateSourceEndpoint },
        { DoctorStateCode.ProtocolMismatch, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseHttpProtobuf },
        { DoctorStateCode.SignalDisabled, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.EnableTraceSignal },
        { DoctorStateCode.UnsupportedSourceVersion, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseSupportedSourceVersion },
        { DoctorStateCode.FeatureUnavailable, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.UseSupportedSourceSurface },
        { DoctorStateCode.AgentRestartRequired, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.RestartSourceProcess },
        { DoctorStateCode.EndpointUnreachable, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.VerifyEndpointReachability },
        { DoctorStateCode.PayloadRejected, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.InspectRejectedPayload },
        { DoctorStateCode.RawPersistedProjectionPending, DoctorSeverity.Warning, DoctorRetryability.Automatic, DoctorNextAction.WaitForProjection },
        { DoctorStateCode.ProjectionFailed, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.OpenProjectionDiagnostics },
        { DoctorStateCode.SessionUnbound, DoctorSeverity.Error, DoctorRetryability.AfterAction, DoctorNextAction.SelectExactSession },
        { DoctorStateCode.ContentCaptureDisabled, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.EnableContentCaptureIfDesired },
        { DoctorStateCode.SanitizedOnlyRawUnavailable, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.RestartWithoutSanitizedOnlyIfDesired },
        { DoctorStateCode.SchemaDriftDetected, DoctorSeverity.Warning, DoctorRetryability.AfterAction, DoctorNextAction.ReviewSourceDiagnostics },
        { DoctorStateCode.ReadyNoRealTrace, DoctorSeverity.Info, DoctorRetryability.AfterAction, DoctorNextAction.RunBoundedSourceInteraction },
        { DoctorStateCode.FirstTraceReady, DoctorSeverity.Info, DoctorRetryability.None, DoctorNextAction.OpenVerifiedTraceOrSession },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void Catalog_FixedEntry_HasCanonicalTuple(
        DoctorStateCode stateCode,
        DoctorSeverity severity,
        DoctorRetryability retryability,
        DoctorNextAction nextAction)
    {
        var entry = Assert.Single(DoctorCatalog.Entries, candidate => candidate.StateCode == stateCode);

        Assert.Equal(severity, entry.Severity);
        Assert.Equal(retryability, entry.Retryability);
        Assert.Equal(nextAction, entry.NextAction);
        Assert.Equal([stateCode], entry.ReasonCodes);
    }

    [Fact]
    public void Catalog_V1_HasExactlyTwentyEntriesInPrecedenceOrder()
    {
        Assert.Equal(Enum.GetValues<DoctorStateCode>(), DoctorCatalog.Entries.Select(entry => entry.StateCode));
        Assert.Equal(20, DoctorCatalog.Entries.Count);
    }
}
