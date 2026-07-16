using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class DoctorRoutesTests
{
    private const string VerificationId = "0190c7a0-0000-7000-8000-000000000001";

    [Fact]
    public async Task PostVerificationWithoutInjectedApplicationUsesMonitorDatabase()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);
        using var request = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""");

        using var response = await host.Client.SendAsync(request);

        AssertDoctorResponse(response, HttpStatusCode.Created);
        Assert.Equal("verification_started", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task DefaultProductionLifecycleProjectsSharedStoreOutcomesWithoutMutationDrift()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = await StartHostAsync(tempDirectory, timeProvider: time);
        var store = new SqliteDoctorVerificationStore(tempDirectory.DatabasePath, time);
        var verification = await StartVerificationAsync(host.Client, time);
        var kinds = Enum.GetValues<DoctorEvidenceKind>();
        var candidates = kinds.Select((kind, index) => new DoctorEvidenceCandidate(
            $"01890abc-def0-7000-8000-{index + 1:x12}",
            verification.VerificationId,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            DoctorEvidenceClass.RealSource,
            kind,
            $"receipt-{JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString())}",
            verification.StartedAt,
            verification.ExpiresAt)).ToArray();
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(candidate).Code);
        }
        var references = candidates.Select(candidate => candidate.EvidenceRef).ToArray();

        using var nonReady = await CompleteAsync(
            host.Client,
            verification.VerificationId,
            expectedRevision: 1,
            NonReadySnapshotJson(verification.VerificationId),
            references);
        AssertDoctorResponse(nonReady, HttpStatusCode.OK);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, (await ResultAsync(nonReady)).Code);
        AssertActiveUnchanged(store, verification.VerificationId);

        using var partial = await CompleteAsync(
            host.Client,
            verification.VerificationId,
            expectedRevision: 1,
            PartialSnapshotJson(verification.VerificationId),
            references);
        AssertDoctorResponse(partial, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(DoctorResultCode.PartialFactSnapshot, (await ResultAsync(partial)).Code);
        AssertActiveUnchanged(store, verification.VerificationId);

        using var stale = await CompleteAsync(
            host.Client,
            verification.VerificationId,
            expectedRevision: 2,
            ReadySnapshotJson(verification.VerificationId),
            references);
        AssertDoctorResponse(stale, HttpStatusCode.Conflict);
        Assert.Equal(DoctorResultCode.VerificationStale, (await ResultAsync(stale)).Code);
        AssertActiveUnchanged(store, verification.VerificationId);

        using var ready = await CompleteAsync(
            host.Client,
            verification.VerificationId,
            expectedRevision: 1,
            ReadySnapshotJson(verification.VerificationId),
            references);
        AssertDoctorResponse(ready, HttpStatusCode.OK);
        var readyResult = await ResultAsync(ready);
        Assert.Equal(DoctorResultCode.VerificationCompleted, readyResult.Code);
        Assert.Equal(DoctorStateCode.FirstTraceReady, readyResult.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(references, readyResult.Verification?.AcceptedEvidenceRefs);

        using var terminal = await CompleteAsync(
            host.Client,
            verification.VerificationId,
            expectedRevision: 2,
            ReadySnapshotJson(verification.VerificationId),
            references);
        AssertDoctorResponse(terminal, HttpStatusCode.Conflict);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCompleted, (await ResultAsync(terminal)).Code);
        var completed = Assert.IsType<DoctorVerification>(store.Get(verification.VerificationId).Verification);
        Assert.Equal(DoctorVerificationState.Completed, completed.State);
        Assert.Equal(2, completed.Revision);
        Assert.Equal(references, completed.AcceptedEvidenceRefs);

        var expiring = await StartVerificationAsync(host.Client, time, TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(1));
        using var expired = await host.Client.GetAsync($"/api/doctor/verifications/{expiring.VerificationId}");
        AssertDoctorResponse(expired, HttpStatusCode.Gone);
        var expiredResult = await ResultAsync(expired);
        Assert.Equal(DoctorResultCode.VerificationExpired, expiredResult.Code);
        Assert.Equal(DoctorVerificationState.Expired, expiredResult.Verification?.State);
        Assert.Equal(1, expiredResult.Verification?.Revision);
        Assert.Empty(expiredResult.Verification?.AcceptedEvidenceRefs ?? []);

        var cancelling = await StartVerificationAsync(host.Client, time);
        using var staleCancel = await CancelAsync(host.Client, cancelling.VerificationId, expectedRevision: 2);
        AssertDoctorResponse(staleCancel, HttpStatusCode.Conflict);
        Assert.Equal(DoctorResultCode.VerificationStale, (await ResultAsync(staleCancel)).Code);
        AssertActiveUnchanged(store, cancelling.VerificationId);
        using var cancelled = await CancelAsync(host.Client, cancelling.VerificationId, expectedRevision: 1);
        AssertDoctorResponse(cancelled, HttpStatusCode.OK);
        var cancelledResult = await ResultAsync(cancelled);
        Assert.Equal(DoctorResultCode.VerificationCancelled, cancelledResult.Code);
        Assert.Equal(DoctorVerificationState.Cancelled, cancelledResult.Verification?.State);
        Assert.Equal(2, cancelledResult.Verification?.Revision);
        Assert.Empty(cancelledResult.Verification?.AcceptedEvidenceRefs ?? []);
        using var cancelledConflict = await CancelAsync(host.Client, cancelling.VerificationId, expectedRevision: 2);
        AssertDoctorResponse(cancelledConflict, HttpStatusCode.Conflict);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCancelled, (await ResultAsync(cancelledConflict)).Code);
    }

    [Fact]
    public async Task DoctorLifecycleAndInitializationFailureDoNotChangeReadinessContract()
    {
        using var availableDirectory = new MonitorTempDirectory();
        using var unavailableDirectory = new MonitorTempDirectory();
        SqliteDoctorHttpApplication? production = null;
        await using var available = await StartHostAsync(
            availableDirectory,
            doctorApplicationFactory: (databasePath, timeProvider) =>
            {
                production = SqliteDoctorHttpApplication.Create(databasePath, timeProvider);
                return production;
            });
        await using var unavailable = await StartHostAsync(
            unavailableDirectory,
            doctorApplicationFactory: (_, _) => throw new InvalidOperationException("sqlite SECRET_PATH"));

        var baseline = await ReadinessAsync(available.Client);
        Assert.Equal(baseline, await ReadinessAsync(unavailable.Client));

        using var startRequest = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""");
        using var startedResponse = await available.Client.SendAsync(startRequest);
        var startedResult = DoctorJson.DeserializeResult(await startedResponse.Content.ReadAsStringAsync());
        var verification = Assert.IsType<DoctorVerification>(startedResult.Verification);
        Assert.Equal(baseline, await ReadinessAsync(available.Client));

        using var statusResponse = await available.Client.GetAsync($"/api/doctor/verifications/{verification.VerificationId}");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(baseline, await ReadinessAsync(available.Client));

        var kinds = Enum.GetValues<DoctorEvidenceKind>();
        var evidenceRefs = kinds
            .Select(kind => $"receipt-{JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString())}")
            .ToArray();
        for (var index = 0; index < kinds.Length; index++)
        {
            var candidate = new DoctorEvidenceCandidate(
                $"01890abc-def0-7000-8000-{index + 1:x12}",
                verification.VerificationId,
                "github-copilot",
                SourceAdapter: null,
                DoctorEvidenceClass.RealSource,
                kinds[index],
                evidenceRefs[index],
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 16, 0, 5, 0, TimeSpan.Zero));
            Assert.Equal(DoctorResultCode.VerificationActive, production!.ObserveCandidate(candidate).Code);
        }
        using var completeRequest = Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{verification.VerificationId}/complete",
            $$"""{"expected_revision":1,"fact_snapshot":{{ReadySnapshotJson(verification.VerificationId)}},"accepted_evidence_refs":{{JsonSerializer.Serialize(evidenceRefs)}}}""");
        using var completedResponse = await available.Client.SendAsync(completeRequest);
        Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
        Assert.Equal("verification_completed", await ReadCodeAsync(completedResponse));
        Assert.Equal(baseline, await ReadinessAsync(available.Client));

        using var cancelStartRequest = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""");
        using var cancelStartedResponse = await available.Client.SendAsync(cancelStartRequest);
        var cancelVerification = Assert.IsType<DoctorVerification>(
            DoctorJson.DeserializeResult(await cancelStartedResponse.Content.ReadAsStringAsync()).Verification);
        using var cancelRequest = Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{cancelVerification.VerificationId}/cancel",
            """{"expected_revision":1}""");
        using var cancelledResponse = await available.Client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
        Assert.Equal(baseline, await ReadinessAsync(available.Client));

        using var unavailableStart = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""");
        using var unavailableResponse = await unavailable.Client.SendAsync(unavailableStart);
        AssertDoctorResponse(unavailableResponse, HttpStatusCode.ServiceUnavailable);
        var unavailableBody = await unavailableResponse.Content.ReadAsStringAsync();
        Assert.Contains("doctor_store_unavailable", unavailableBody, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlite", unavailableBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET_PATH", unavailableBody, StringComparison.Ordinal);
        using var evaluationResponse = await unavailable.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent(NonReadySnapshotJson(verificationId: null)));
        AssertDoctorResponse(evaluationResponse, HttpStatusCode.OK);
        Assert.Equal(baseline, await ReadinessAsync(unavailable.Client));
    }

    [Fact]
    public async Task AllFiveRoutesUseTheInjectedApplication()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);

        using var evaluate = await host.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent(NonReadySnapshotJson(verificationId: null)));
        using var startRequest = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""");
        using var start = await host.Client.SendAsync(startRequest);
        using var status = await host.Client.GetAsync($"/api/doctor/verifications/{VerificationId}");
        using var completeRequest = Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{VerificationId}/complete",
            $$"""{"expected_revision":1,"fact_snapshot":{{NonReadySnapshotJson(VerificationId)}},"accepted_evidence_refs":["evidence:1"]}""");
        using var complete = await host.Client.SendAsync(completeRequest);
        using var cancelRequest = Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{VerificationId}/cancel",
            """{"expected_revision":1}""");
        using var cancel = await host.Client.SendAsync(cancelRequest);

        AssertDoctorResponse(evaluate, HttpStatusCode.OK);
        AssertDoctorResponse(start, HttpStatusCode.Created);
        AssertDoctorResponse(status, HttpStatusCode.OK);
        AssertDoctorResponse(complete, HttpStatusCode.OK);
        AssertDoctorResponse(cancel, HttpStatusCode.OK);
        Assert.Equal(1, application.EvaluateCalls);
        Assert.Equal(1, application.StartCalls);
        Assert.Equal(1, application.StatusCalls);
        Assert.Equal(1, application.CompleteCalls);
        Assert.Equal(1, application.CancelCalls);
    }

    public static TheoryData<DoctorResultCode, HttpStatusCode> StatusCases => new()
    {
        { DoctorResultCode.EvaluationCompleted, HttpStatusCode.OK },
        { DoctorResultCode.VerificationActive, HttpStatusCode.OK },
        { DoctorResultCode.VerificationCompleted, HttpStatusCode.OK },
        { DoctorResultCode.VerificationCancelled, HttpStatusCode.OK },
        { DoctorResultCode.InvalidArguments, HttpStatusCode.BadRequest },
        { DoctorResultCode.InvalidInput, HttpStatusCode.BadRequest },
        { DoctorResultCode.UnsupportedSchemaVersion, HttpStatusCode.BadRequest },
        { DoctorResultCode.VerificationNotFound, HttpStatusCode.NotFound },
        { DoctorResultCode.VerificationStale, HttpStatusCode.Conflict },
        { DoctorResultCode.VerificationAlreadyCancelled, HttpStatusCode.Conflict },
        { DoctorResultCode.VerificationAlreadyCompleted, HttpStatusCode.Conflict },
        { DoctorResultCode.ExpectedSourceMismatch, HttpStatusCode.Conflict },
        { DoctorResultCode.EvidenceNotFound, HttpStatusCode.Conflict },
        { DoctorResultCode.VerificationExpired, HttpStatusCode.Gone },
        { DoctorResultCode.EvidenceExpired, HttpStatusCode.Gone },
        { DoctorResultCode.PartialFactSnapshot, HttpStatusCode.UnprocessableEntity },
        { DoctorResultCode.DoctorStoreBusy, HttpStatusCode.ServiceUnavailable },
        { DoctorResultCode.DoctorStoreUnavailable, HttpStatusCode.ServiceUnavailable },
        { DoctorResultCode.InternalError, HttpStatusCode.InternalServerError },
    };

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task EveryApplicationOutcomeUsesTheFixedStatusAndResponseHeaders(
        DoctorResultCode code,
        HttpStatusCode expectedStatus)
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new FixedDoctorApplication(Error(code));
        await using var host = await StartHostAsync(tempDirectory, application);

        using var response = await host.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent(NonReadySnapshotJson(verificationId: null)));

        AssertDoctorResponse(response, expectedStatus);
        Assert.Equal(Wire(code), await ReadCodeAsync(response));
    }

    public static TheoryData<string, string> MutationCases => new()
    {
        {
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}"""
        },
        {
            $"/api/doctor/verifications/{VerificationId}/complete",
            $$"""{"expected_revision":1,"fact_snapshot":{{NonReadySnapshotJson(VerificationId)}},"accepted_evidence_refs":["evidence:1"]}"""
        },
        {
            $"/api/doctor/verifications/{VerificationId}/cancel",
            """{"expected_revision":1}"""
        },
    };

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task MutationRoutesRequireExactCsrfAndSameOrigin(string path, string body)
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);

        using var missingCsrf = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(body),
        };
        missingCsrf.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        using var missingResponse = await host.Client.SendAsync(missingCsrf);

        using var wrongCsrf = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(body),
        };
        wrongCsrf.Headers.TryAddWithoutValidation("x-monitor-csrf", "Local-Monitor");
        wrongCsrf.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        using var wrongResponse = await host.Client.SendAsync(wrongCsrf);

        using var crossSite = Mutation(HttpMethod.Post, path, body);
        crossSite.Headers.Remove("Sec-Fetch-Site");
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        using var crossSiteResponse = await host.Client.SendAsync(crossSite);

        AssertDoctorResponse(missingResponse, HttpStatusCode.Forbidden);
        AssertDoctorResponse(wrongResponse, HttpStatusCode.Forbidden);
        AssertDoctorResponse(crossSiteResponse, HttpStatusCode.Forbidden);
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task EvaluationAndStatusDoNotRequireCsrfOrSameOrigin()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);
        using var evaluationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/doctor/evaluations")
        {
            Content = JsonContent(NonReadySnapshotJson(verificationId: null)),
        };
        evaluationRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/doctor/verifications/{VerificationId}");
        statusRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var evaluation = await host.Client.SendAsync(evaluationRequest);
        using var status = await host.Client.SendAsync(statusRequest);

        AssertDoctorResponse(evaluation, HttpStatusCode.OK);
        AssertDoctorResponse(status, HttpStatusCode.OK);
        Assert.Equal(2, application.TotalCalls);
    }

    public static IEnumerable<object[]> InvalidTransportCases()
    {
        var snapshot = NonReadySnapshotJson(verificationId: null);
        yield return ["/api/doctor/evaluations", snapshot, false, "text/plain"];
        yield return ["/api/doctor/evaluations", "{", false, "application/json"];
        yield return ["/api/doctor/evaluations", string.Empty, false, "application/json"];
        yield return [
            "/api/doctor/evaluations",
            snapshot.Replace(
                "\"source_surface\":\"github-copilot\"",
                "\"source_surface\":\"github-copilot\",\"source_surface\":\"github-copilot\"",
                StringComparison.Ordinal),
            false,
            "application/json"];
        yield return [
            "/api/doctor/evaluations",
            snapshot.Insert(snapshot.LastIndexOf('}'), ",\"unknown\":true"),
            false,
            "application/json"];
        yield return [
            "/api/doctor/evaluations",
            snapshot.Replace("\"monitor_install\":\"installed\"", "\"monitor_install\":\"Installed\"", StringComparison.Ordinal),
            false,
            "application/json"];
        yield return [
            "/api/doctor/verifications",
            """{"source_surface":"GitHub-Copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z"}""",
            true,
            "application/json"];
        yield return [
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00Z"}""",
            true,
            "application/json"];
        yield return [
            "/api/doctor/verifications",
            """{"source_surface":"github-copilot","source_adapter":null,"expires_at":"2026-07-16T00:05:00.0000000Z","unknown":true}""",
            true,
            "application/json"];
        yield return [$"/api/doctor/verifications/not-a-uuid", string.Empty, false, null!];
        yield return [
            $"/api/doctor/verifications/{VerificationId}/complete",
            $$"""{"expected_revision":0,"fact_snapshot":{{NonReadySnapshotJson(VerificationId)}},"accepted_evidence_refs":["evidence:1"]}""",
            true,
            "application/json"];
        yield return [
            $"/api/doctor/verifications/{VerificationId}/complete",
            $$"""{"expected_revision":1,"fact_snapshot":{{NonReadySnapshotJson(VerificationId)}},"accepted_evidence_refs":[]}""",
            true,
            "application/json"];
        yield return [
            $"/api/doctor/verifications/{VerificationId}/complete",
            $$"""{"expected_revision":1,"fact_snapshot":{{NonReadySnapshotJson(VerificationId)}},"accepted_evidence_refs":["C:\\\\private\\\\doctor.db"]}""",
            true,
            "application/json"];
        yield return [
            $"/api/doctor/verifications/{VerificationId}/cancel",
            """{"expected_revision":0}""",
            true,
            "application/json"];
        yield return [
            $"/api/doctor/verifications/{VerificationId}/cancel",
            """{"expected_revision":1,"unknown":true}""",
            true,
            "application/json"];
    }

    [Theory]
    [MemberData(nameof(InvalidTransportCases))]
    public async Task InvalidTransportIsRejectedBeforeTheApplication(
        string path,
        string body,
        bool mutation,
        string? contentType)
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);
        using var request = new HttpRequestMessage(path.Contains("not-a-uuid", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post, path);
        if (contentType is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }
        if (mutation)
        {
            request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        }

        using var response = await host.Client.SendAsync(request);

        AssertDoctorResponse(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task EvaluationUsesTheExact64KiBPlusSentinelUtf8Boundary()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);
        var atLimit = PadToUtf8Length(NonReadySnapshotJson(verificationId: null), 65_536);
        var overLimit = atLimit + " ";

        using var acceptedRequest = ChunkedJson(atLimit);
        using var accepted = await host.Client.SendAsync(acceptedRequest);
        using var rejectedRequest = ChunkedJson(overLimit);
        using var rejected = await host.Client.SendAsync(rejectedRequest);

        AssertDoctorResponse(accepted, HttpStatusCode.OK);
        AssertDoctorResponse(rejected, HttpStatusCode.BadRequest);
        Assert.Equal(1, application.EvaluateCalls);
    }

    [Fact]
    public async Task DoctorBoundaryOverridesALowerGlobalRequestBodyLimit()
    {
        const int globalLimit = 1_024;
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication(evaluateSnapshot: true);
        await using var host = await StartHostAsync(tempDirectory, application, globalLimit);
        var atDoctorLimit = PadToUtf8Length(NonReadySnapshotJson(verificationId: null), 65_536);
        var overDoctorLimit = atDoctorLimit + " ";

        using var accepted = await host.Client.PostAsync("/api/doctor/evaluations", JsonContent(atDoctorLimit));
        using var rejected = await host.Client.PostAsync("/api/doctor/evaluations", JsonContent(overDoctorLimit));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        var rejectedBody = await rejected.Content.ReadAsStringAsync();

        AssertDoctorResponse(accepted, HttpStatusCode.OK);
        AssertDoctorResponse(rejected, HttpStatusCode.BadRequest);
        Assert.Equal("evaluation_completed", await ReadCodeAsync(accepted));
        Assert.Equal("invalid_input", await ReadCodeAsync(rejected));
        Assert.Contains("\"state_code\":\"monitor_not_running\"", acceptedBody, StringComparison.Ordinal);
        Assert.Equal(
            """{"schema_version":"doctor.v1","success":false,"code":"invalid_input","evaluation":null,"verification":null}""",
            rejectedBody);
        Assert.DoesNotContain("request_too_large", rejectedBody, StringComparison.Ordinal);
        Assert.Equal(1, application.EvaluateCalls);
    }

    [Fact]
    public async Task DoctorBoundaryDoesNotRaiseTheGlobalTraceRequestBodyLimit()
    {
        const int globalLimit = 1_024;
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory, maxRequestBodyBytes: globalLimit);
        using var content = new StringContent(new string('x', globalLimit + 1), Encoding.UTF8, "application/json");

        using var response = await host.Client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request_too_large", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8IsRejectedWithoutInvokingTheApplication()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);
        using var content = new ByteArrayContent([0xc3, 0x28]);
        content.Headers.ContentType = new("application/json");

        using var response = await host.Client.PostAsync("/api/doctor/evaluations", content);

        AssertDoctorResponse(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task DoctorRoutesRetainHostHeaderProtectionWithDoctorResponseHeaders()
    {
        using var tempDirectory = new MonitorTempDirectory();
        var application = new RecordingDoctorApplication();
        await using var host = await StartHostAsync(tempDirectory, application);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/doctor/evaluations")
        {
            Content = JsonContent(NonReadySnapshotJson(verificationId: null)),
        };
        request.Headers.Host = "example.com";

        using var response = await host.Client.SendAsync(request);

        AssertDoctorResponse(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_arguments", await ReadCodeAsync(response));
        Assert.Equal(0, application.TotalCalls);
    }

    [Fact]
    public async Task ValidNonReadyEvaluationRemainsHttp200()
    {
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(tempDirectory);

        using var response = await host.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent(NonReadySnapshotJson(verificationId: null)));
        var body = await response.Content.ReadAsStringAsync();

        AssertDoctorResponse(response, HttpStatusCode.OK);
        Assert.Contains("\"code\":\"evaluation_completed\"", body, StringComparison.Ordinal);
        Assert.Contains("\"state_code\":\"monitor_not_running\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedFailuresAndRejectedInputNeverLeakSensitiveValues()
    {
        var leakMarkers = new[]
        {
            "SECRET_PROMPT_TEXT_MARKER",
            "response-body-marker",
            "tool-argument-marker",
            "person@example.com",
            "Bearer credential-marker",
            @"C:\private\doctor.db",
            "SqliteException",
            "JsonException",
        };
        using var tempDirectory = new MonitorTempDirectory();
        await using var host = await StartHostAsync(
            tempDirectory,
            new ThrowingDoctorApplication(string.Join(" ", leakMarkers)));

        using var failure = await host.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent(NonReadySnapshotJson(verificationId: null)));
        using var rejected = await host.Client.PostAsync(
            "/api/doctor/evaluations",
            JsonContent("{\"source_surface\":\"SECRET_PROMPT_TEXT_MARKER person@example.com C:\\\\private\\\\doctor.db\"}"));
        var bodies = new[]
        {
            await failure.Content.ReadAsStringAsync(),
            await rejected.Content.ReadAsStringAsync(),
        };

        AssertDoctorResponse(failure, HttpStatusCode.InternalServerError);
        AssertDoctorResponse(rejected, HttpStatusCode.BadRequest);
        foreach (var body in bodies)
        {
            foreach (var marker in leakMarkers)
            {
                Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static Task<RunningMonitorHost> StartHostAsync(
        MonitorTempDirectory tempDirectory,
        IDoctorHttpApplication? doctorApplication = null,
        int maxRequestBodyBytes = MonitorOptions.DefaultMaxRequestBodyBytes,
        Func<string, TimeProvider, IDoctorHttpApplication>? doctorApplicationFactory = null,
        TimeProvider? timeProvider = null) =>
        MonitorTestHost.StartAsync(
            tempDirectory,
            maxRequestBodyBytes: maxRequestBodyBytes,
            testOptions: new MonitorHostTestOptions
            {
                DoctorApplication = doctorApplication,
                DoctorApplicationFactory = doctorApplicationFactory,
                TimeProvider = timeProvider
                    ?? new MutableTimeProvider(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero)),
                StartWriter = false,
                StartProjectionWorker = false,
                StartSessionWriter = false,
                StartSessionOtelEnrichment = false,
                UseUserSecrets = false,
            });

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage ChunkedJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/doctor/evaluations")
        {
            Content = new StreamContent(new NonSeekableMemoryStream(bytes)),
        };
        request.Content.Headers.ContentType = new("application/json");
        return request;
    }

    private static HttpRequestMessage Mutation(HttpMethod method, string path, string json)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private static void AssertDoctorResponse(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task<string> ResultCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("doctor.v1", document.RootElement.GetProperty("schema_version").GetString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("evaluation").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("verification").ValueKind);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("doctor.v1", document.RootElement.GetProperty("schema_version").GetString());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static string PadToUtf8Length(string json, int byteLength)
    {
        var currentLength = Encoding.UTF8.GetByteCount(json);
        Assert.True(currentLength <= byteLength);
        return json + new string(' ', byteLength - currentLength);
    }

    private static DoctorResult Error(DoctorResultCode code) =>
        new(DoctorSchemaVersions.ResultV1, Success: false, code, Evaluation: null, Verification: null);

    private static string Wire(DoctorResultCode code) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString());

    private static string NonReadySnapshotJson(string? verificationId) => $$"""
        {
          "schema_version":"doctor.facts.v1",
          "source_surface":"github-copilot",
          "expected_source_adapter":null,
          "observed_at":"2026-07-16T00:00:00.0000000Z",
          "verification_id":{{(verificationId is null ? "null" : $"\"{verificationId}\"")}},
          "observations":[],
          "install_and_source_version":{"monitor_install":"installed","source_version":"supported","source_feature":"available"},
          "process_receiver_and_port":{"monitor_process":"not_running","receiver_bind":"not_bound","port_owner":"none"},
          "source_effective_configuration":{"endpoint_alignment":"match"},
          "endpoint_reachability":{"reachability":"reachable"},
          "protocol_and_signal_compatibility":{"protocol":"http_protobuf","trace_signal":"enabled"},
          "source_version_and_schema_diagnostics":{"compatibility":"supported","schema":"matching"},
          "last_ingest":{"outcome":"none"},
          "raw_persistence":{"outcome":"not_persisted"},
          "projection":{"outcome":"not_started"},
          "exact_session_binding":{"requirement":"required","outcome":"unbound"},
          "completeness_and_content":{"completeness":"unbound","content_capture":"enabled","raw_access":"available"},
          "restart_or_new_process":{"requirement":"not_required"}
        }
        """;

    private static string ReadySnapshotJson(string verificationId) =>
        NonReadySnapshotJson(verificationId)
            .Replace("\"monitor_process\":\"not_running\",\"receiver_bind\":\"not_bound\",\"port_owner\":\"none\"", "\"monitor_process\":\"running\",\"receiver_bind\":\"bound\",\"port_owner\":\"monitor\"", StringComparison.Ordinal)
            .Replace("\"outcome\":\"none\"", "\"outcome\":\"accepted\"", StringComparison.Ordinal)
            .Replace("\"outcome\":\"not_persisted\"", "\"outcome\":\"persisted\"", StringComparison.Ordinal)
            .Replace("\"outcome\":\"not_started\"", "\"outcome\":\"completed\"", StringComparison.Ordinal)
            .Replace("\"requirement\":\"required\",\"outcome\":\"unbound\"", "\"requirement\":\"required\",\"outcome\":\"exact_bound\"", StringComparison.Ordinal)
            .Replace("\"completeness\":\"unbound\"", "\"completeness\":\"full\"", StringComparison.Ordinal);

    private static string PartialSnapshotJson(string verificationId) =>
        ReadySnapshotJson(verificationId).Replace(
            "\"install_and_source_version\":{\"monitor_install\":\"installed\",\"source_version\":\"supported\",\"source_feature\":\"available\"}",
            "\"install_and_source_version\":null",
            StringComparison.Ordinal);

    private static async Task<DoctorVerification> StartVerificationAsync(
        HttpClient client,
        TimeProvider timeProvider,
        TimeSpan? window = null)
    {
        using var request = Mutation(
            HttpMethod.Post,
            "/api/doctor/verifications",
            $$"""{"source_surface":"github-copilot","source_adapter":null,"expires_at":"{{CanonicalTimestamp(timeProvider.GetUtcNow().Add(window ?? TimeSpan.FromMinutes(5)))}}"}""");
        using var response = await client.SendAsync(request);
        AssertDoctorResponse(response, HttpStatusCode.Created);
        var result = await ResultAsync(response);
        Assert.Equal(DoctorResultCode.VerificationStarted, result.Code);
        return Assert.IsType<DoctorVerification>(result.Verification);
    }

    private static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        string verificationId,
        int expectedRevision,
        string snapshotJson,
        IReadOnlyList<string> evidenceRefs) =>
        client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{verificationId}/complete",
            $$"""{"expected_revision":{{expectedRevision}},"fact_snapshot":{{snapshotJson}},"accepted_evidence_refs":{{JsonSerializer.Serialize(evidenceRefs)}}}"""));

    private static Task<HttpResponseMessage> CancelAsync(
        HttpClient client,
        string verificationId,
        int expectedRevision) =>
        client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/doctor/verifications/{verificationId}/cancel",
            $$"""{"expected_revision":{{expectedRevision}}}"""));

    private static async Task<DoctorResult> ResultAsync(HttpResponseMessage response) =>
        DoctorJson.DeserializeResult(await response.Content.ReadAsStringAsync());

    private static void AssertActiveUnchanged(SqliteDoctorVerificationStore store, string verificationId)
    {
        var result = store.Get(verificationId);
        Assert.Equal(DoctorResultCode.VerificationActive, result.Code);
        var verification = Assert.IsType<DoctorVerification>(result.Verification);
        Assert.Equal(DoctorVerificationState.Active, verification.State);
        Assert.Equal(1, verification.Revision);
        Assert.Empty(verification.AcceptedEvidenceRefs);
    }

    private static string CanonicalTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<ReadinessContract> ReadinessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready");
        return new(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.CacheControl?.ToString(),
            Convert.ToHexString(await response.Content.ReadAsByteArrayAsync()));
    }

    private sealed record ReadinessContract(
        HttpStatusCode StatusCode,
        string? ContentType,
        string? CacheControl,
        string BodyHex);

    private sealed class RecordingDoctorApplication : IDoctorHttpApplication
    {
        private readonly bool evaluateSnapshot;

        public RecordingDoctorApplication(bool evaluateSnapshot = false)
        {
            this.evaluateSnapshot = evaluateSnapshot;
        }

        public int EvaluateCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int TotalCalls => EvaluateCalls + StartCalls + StatusCalls + CompleteCalls + CancelCalls;

        public DoctorResult Evaluate(DoctorFactSnapshot snapshot)
        {
            EvaluateCalls++;
            return evaluateSnapshot
                ? DoctorEvaluator.Evaluate(snapshot)
                : Result(DoctorResultCode.EvaluationCompleted);
        }

        public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt)
        {
            StartCalls++;
            return Result(DoctorResultCode.VerificationStarted, Verification(DoctorVerificationState.Active, 1));
        }

        public DoctorResult Status(string verificationId)
        {
            StatusCalls++;
            return Result(DoctorResultCode.VerificationActive, Verification(DoctorVerificationState.Active, 1));
        }

        public DoctorResult Complete(
            string verificationId,
            int expectedRevision,
            DoctorHttpCompletionInput input)
        {
            CompleteCalls++;
            return Result(DoctorResultCode.VerificationCompleted, Verification(DoctorVerificationState.Completed, 2));
        }

        public DoctorResult Cancel(string verificationId, int expectedRevision)
        {
            CancelCalls++;
            return Result(DoctorResultCode.VerificationCancelled, Verification(DoctorVerificationState.Cancelled, 2));
        }

        private static DoctorResult Result(DoctorResultCode code, DoctorVerification? verification = null) =>
            new(
                DoctorSchemaVersions.ResultV1,
                Success: true,
                code,
                Evaluation: code == DoctorResultCode.EvaluationCompleted
                    ? new DoctorEvaluation("github-copilot", PrimaryState: null, States: [], MissingFactFamilies: [])
                    : null,
                verification);

        private static DoctorVerification Verification(DoctorVerificationState state, int revision) =>
            new(
                VerificationId,
                "github-copilot",
                ExpectedSourceAdapter: null,
                state,
                revision,
                DateTimeOffset.Parse("2026-07-16T00:00:00.0000000Z"),
                DateTimeOffset.Parse("2026-07-16T00:05:00.0000000Z"),
                CompletedAt: state == DoctorVerificationState.Completed
                    ? DateTimeOffset.Parse("2026-07-16T00:01:00.0000000Z")
                    : null,
                CancelledAt: state == DoctorVerificationState.Cancelled
                    ? DateTimeOffset.Parse("2026-07-16T00:01:00.0000000Z")
                    : null,
                AcceptedEvidenceRefs: state == DoctorVerificationState.Completed ? ["evidence:1"] : []);
    }

    private sealed class FixedDoctorApplication(DoctorResult result) : IDoctorHttpApplication
    {
        public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => result;
        public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt) => result;
        public DoctorResult Status(string verificationId) => result;
        public DoctorResult Complete(string verificationId, int expectedRevision, DoctorHttpCompletionInput input) => result;
        public DoctorResult Cancel(string verificationId, int expectedRevision) => result;
    }

    private sealed class ThrowingDoctorApplication(string message) : IDoctorHttpApplication
    {
        public DoctorResult Evaluate(DoctorFactSnapshot snapshot) => throw new InvalidOperationException(message);
        public DoctorResult Start(string sourceSurface, string? sourceAdapter, DateTimeOffset expiresAt) => throw new InvalidOperationException(message);
        public DoctorResult Status(string verificationId) => throw new InvalidOperationException(message);
        public DoctorResult Complete(string verificationId, int expectedRevision, DoctorHttpCompletionInput input) => throw new InvalidOperationException(message);
        public DoctorResult Cancel(string verificationId, int expectedRevision) => throw new InvalidOperationException(message);
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
