namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// The three tagged termination causes for one current-file request. Root-generation/normal host
// shutdown is deliberately NOT a cancellation cause here: it closes admission and drains already-
// admitted leases, and is represented separately by the runtime-acquisition stage returning a
// shutdown-closed disposition rather than by any of these causes.
internal enum CurrentSkillTerminationCause
{
    None = 0,
    CallerAbort,
    RetentionLostOrBusy,
    RuntimeInvalidation
}

// Keeps caller abort, runtime-generation invalidation, and Retention grant loss/expiry as three
// tagged causes even though their work-cancellation tokens may be linked. No generic
// OperationCanceledException, shared-token propagation, exception message, or callback arrival
// order determines the public result: the cause is decided only by the explicit observable facts.
//
// Fixed priority when multiple facts are observable is caller abort -> Retention lost|busy ->
// runtime invalidation. The first two abort without a substitute response; only the runtime-
// invalidation arm can send the fixed runtime 503, and only after Retention completes without raw.
// Runtime invalidation cancels SDK/native work and the runtime capability only; it never cancels
// or releases the Retention handle.
internal static class CurrentSkillRequestTerminationV1
{
    internal static CurrentSkillTerminationCause ResolveCause(
        bool callerAborted,
        bool retentionLostOrBusy,
        bool runtimeInvalidated)
    {
        if (callerAborted)
        {
            return CurrentSkillTerminationCause.CallerAbort;
        }

        if (retentionLostOrBusy)
        {
            return CurrentSkillTerminationCause.RetentionLostOrBusy;
        }

        if (runtimeInvalidated)
        {
            return CurrentSkillTerminationCause.RuntimeInvalidation;
        }

        return CurrentSkillTerminationCause.None;
    }

    internal static bool AbortsWithoutResponse(CurrentSkillTerminationCause cause) =>
        cause is CurrentSkillTerminationCause.CallerAbort or CurrentSkillTerminationCause.RetentionLostOrBusy;

    internal static bool PermitsSubstituteRuntimeUnavailable(CurrentSkillTerminationCause cause) =>
        cause == CurrentSkillTerminationCause.RuntimeInvalidation;
}
