using CopilotAgentObservability.LocalMonitor.ProposalApply;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal static class EffectComparisonRoutes
{
    public static void Map(WebApplication app, ISessionStore store, ProposalApplyService applies, TimeProvider timeProvider)
    {
        app.MapGet("/api/session-workspace/effect-comparisons/candidates", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!SessionRoutes.TryUuidV7(context.Request.Query["proposal_id"].ToString(), out var proposalId)
                || !SessionRoutes.TryUuidV7(context.Request.Query["apply_id"].ToString(), out var applyId)) { await SessionRoutes.Failure(context, 400, "invalid_comparison_request"); return; }
            if (!applies.TryGetCurrentApplication(proposalId, applyId, out var application)) { await SessionRoutes.Failure(context, 400, "application_not_active"); return; }
            var items = store.ListMostRecent(200).Select(session => Candidate(store, session, application.AppliedAt));
            await SessionRoutes.Json(context, new { proposal_id = proposalId, apply_id = applyId, proposal_revision = application.ProposalRevision, items });
        });

        app.MapPost("/api/session-workspace/effect-comparisons", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (MonitorHost.IsCrossSiteRequest(context)) { await SessionRoutes.Failure(context, 403, "cross_origin_forbidden"); return; }
            if (!MonitorHost.HasMonitorCsrfHeader(context)) { await SessionRoutes.Failure(context, 403, "csrf_required"); return; }
            if (!SessionRoutes.IsJson(context.Request)) { await SessionRoutes.Failure(context, 415, "unsupported_media_type"); return; }
            var body = await SessionRoutes.ReadBoundedBody(context.Request, SessionRoutes.MaximumBodyBytes, context.RequestAborted);
            if (body is null) { await SessionRoutes.Failure(context, 413, "request_too_large"); return; }
            if (!EffectComparisonRequestParser.TryParse(body, out var request)) { await SessionRoutes.Failure(context, 400, "invalid_comparison_request"); return; }
            try
            {
                var receipt = applies.RecordCurrentEffectComparison(request!, timeProvider.GetUtcNow());
                context.Response.StatusCode = 201;
                await SessionRoutes.JsonBody(context, Receipt(receipt));
            }
            catch (CurrentApplicationException) { await SessionRoutes.Failure(context, 400, "application_not_active"); }
            catch (ArgumentException) { await SessionRoutes.Failure(context, 400, "invalid_comparison_request"); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Proposal revision", StringComparison.Ordinal)) { await SessionRoutes.Failure(context, 400, "proposal_revision_stale"); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("Comparison evidence", StringComparison.Ordinal)) { await SessionRoutes.Failure(context, 400, "comparison_evidence_stale"); }
            catch (InvalidOperationException) { await SessionRoutes.Failure(context, 400, "cohort_not_confirmed"); }
        });

        app.MapGet("/api/session-workspace/effect-comparisons/{comparisonId}", async (string comparisonId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!SessionRoutes.TryUuidV7(comparisonId, out var id)) { await SessionRoutes.Failure(context, 400, "invalid_comparison_request"); return; }
            var detail = store.GetEffectComparison(id);
            if (detail is null) { await SessionRoutes.Failure(context, 404, "comparison_not_found"); return; }
            await SessionRoutes.Json(context, Detail(detail with { Receipt = applies.ProjectEffectReceipt(detail.Receipt) }));
        });
    }

    private static object Candidate(ISessionStore store, ObservedSession session, DateTimeOffset appliedAt)
    {
        var exact = store.GetDetail(session.SessionId)?.NativeIds.Any(item => item.BindingKind == SessionBindingKind.Native) == true;
        var terminal = session.Status is ObservedSessionStatus.Completed or ObservedSessionStatus.Failed;
        var full = session.Completeness == SessionCompleteness.Full;
        var pre = session.EndedAt is not null && session.EndedAt <= appliedAt;
        var post = session.StartedAt is not null && session.StartedAt >= appliedAt;
        var boundary = pre ? "pre" : post ? "post" : "not_eligible";
        var evidence = store.GetHumanEvaluation(session.SessionId) is not null || store.ListObjectiveEvaluations(session.SessionId).Count > 0;
        var reasons = new List<string>();
        if (!exact) reasons.Add("not_exact_bound");
        if (!terminal || !full) reasons.Add("not_comparable");
        if (!evidence) reasons.Add("missing_evidence");
        if (boundary == "not_eligible") reasons.Add("overlaps_application");
        return new { session_id = session.SessionId, status = SessionWire.ToWire(session.Status), completeness = SessionWire.ToWire(session.Completeness), started_at = session.StartedAt, ended_at = session.EndedAt, exact_bound = exact, evidence_available = evidence, boundary_eligibility = boundary, suggestion_reasons = reasons };
    }

    private static object Receipt(EffectReceipt receipt) => new { comparison_id = receipt.ComparisonId, cohort_revision = receipt.CohortRevision, proposal_id = receipt.ProposalId, proposal_revision = receipt.ProposalRevision, apply_id = receipt.ApplyId, verdict = Verdict(receipt.Result.Verdict), verification_state = receipt.VerificationState, recorded_at = receipt.RecordedAt };
    private static object Detail(EffectComparisonDetail detail) => new
    {
        receipt = Receipt(detail.Receipt),
        summary = Summary(detail.Receipt.Result),
        sessions = detail.Sessions.Select(item => new { session_id = item.SessionId, classification = item.Classification, case_key = item.CaseKey, exclusion_reason = item.ExclusionReason, effective_quality = item.EffectiveQuality, severe_failure = item.SevereFailure }),
        evidence = detail.Evidence.Select(Evidence),
        case_key_groups = detail.Sessions.Where(item => item.Classification is "pre" or "post").GroupBy(item => item.CaseKey).Select(group => new { case_key = group.Key, sessions = group.Select(item => item.SessionId), evidence = detail.Evidence.Where(item => group.Any(session => session.SessionId == item.SessionId)).Select(Evidence) })
    };
    private static object Evidence(EffectComparisonEvidence item) => new { session_id = item.SessionId, kind = item.Kind, reference_id = item.ReferenceId, recorded_at = item.RecordedAt, human_verdict = item.HumanVerdict };
    private static object Summary(EffectVerdictResult result) => new { verdict = Verdict(result.Verdict), pre_pass = result.PrePass, pre_count = result.PreCount, post_pass = result.PostPass, post_count = result.PostCount, pre_duration_median = result.PreDurationMedian, post_duration_median = result.PostDurationMedian, duration_delta = result.DurationDelta, pre_token_median = result.PreTokenMedian, post_token_median = result.PostTokenMedian, token_delta = result.TokenDelta, reasons = result.Reasons };
    private static string Verdict(EffectVerdict value) => value switch { EffectVerdict.Improved => "improved", EffectVerdict.NoChange => "no_change", EffectVerdict.Regressed => "regressed", _ => "insufficient_evidence" };
}
