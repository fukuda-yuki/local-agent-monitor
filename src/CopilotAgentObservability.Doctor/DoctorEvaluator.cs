namespace CopilotAgentObservability.Doctor;

public static class DoctorEvaluator
{
    public static DoctorResult Evaluate(DoctorFactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.Equals(snapshot.SchemaVersion, DoctorSchemaVersions.FactsV1, StringComparison.Ordinal))
        {
            return new DoctorResult(
                DoctorSchemaVersions.ResultV1,
                Success: false,
                DoctorResultCode.UnsupportedSchemaVersion,
                Evaluation: null,
                Verification: null);
        }

        if (snapshot.InstallAndSourceVersion?.MonitorInstall == MonitorInstallStatus.Installed
            && snapshot.ProcessReceiverAndPort?.MonitorProcess == MonitorProcessStatus.NotRunning)
        {
            var state = new DoctorState(
                DoctorSchemaVersions.ResultV1,
                DoctorStateCode.MonitorNotRunning,
                DoctorSeverity.Error,
                snapshot.SourceSurface,
                EvidenceRefs: [],
                ReasonCodes: [DoctorStateCode.MonitorNotRunning],
                DoctorNextAction.StartMonitor,
                DoctorRetryability.AfterAction,
                snapshot.ObservedAt,
                snapshot.VerificationId);
            return new DoctorResult(
                DoctorSchemaVersions.ResultV1,
                Success: true,
                DoctorResultCode.EvaluationCompleted,
                new DoctorEvaluation(snapshot.SourceSurface, state, [state], []),
                Verification: null);
        }

        return new DoctorResult(
            DoctorSchemaVersions.ResultV1,
            Success: false,
            DoctorResultCode.PartialFactSnapshot,
            new DoctorEvaluation(
                snapshot.SourceSurface,
                PrimaryState: null,
                States: [],
                MissingFactFamilies: ["process_receiver_and_port"]),
            Verification: null);
    }
}
