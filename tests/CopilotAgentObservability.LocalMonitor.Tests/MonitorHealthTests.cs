using CopilotAgentObservability.LocalMonitor.Health;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorHealthTests
{
    private const int StallThreshold = 10;
    private const int LagThreshold = 60;

    private static readonly DateTimeOffset Start = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    // Fully healthy and caught up: loopback bound, migration complete, writer and
    // projection worker running, no backpressure, projection lag 0.
    private static MonitorHealthState ReadyState(MutableTimeProvider time)
    {
        var state = new MonitorHealthState(time);
        state.SetLoopbackBound(true);
        state.MarkMigrationComplete();
        state.SetWriterRunning(true);
        state.SetProjectionWorkerRunning(true);
        state.SetProjectionStatus(backlog: 0, oldestUnprocessedReceivedAt: null);
        return state;
    }

    private MonitorReadiness Evaluate(MonitorHealthState state) => state.Evaluate(StallThreshold, LagThreshold);

    [Fact]
    public void Evaluate_AllHealthyAndCaughtUp_IsReady()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);

        var readiness = Evaluate(state);

        Assert.True(readiness.IsReady);
        Assert.Equal("ready", readiness.Status);
        Assert.Empty(readiness.DegradedReasons);
        Assert.True(readiness.ProjectionWorkerRunning);
        Assert.Equal(0, readiness.ProjectionLagSeconds);
    }

    [Fact]
    public void Evaluate_ProjectionWorkerAbsent_IsNotReadyWithProjectionWorkerMissing()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetProjectionWorkerRunning(false);

        var readiness = Evaluate(state);

        Assert.False(readiness.IsReady);
        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("projection_worker_missing", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_ProjectionLagUnderThreshold_IsDegradedWithProjectionLag()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetProjectionStatus(backlog: 3, oldestUnprocessedReceivedAt: Start.AddSeconds(-(LagThreshold - 30)));

        var readiness = Evaluate(state);

        Assert.True(readiness.IsReady); // degraded maps to 200
        Assert.Equal("degraded", readiness.Status);
        Assert.Contains("projection_lag", readiness.DegradedReasons);
        Assert.DoesNotContain("projection_lag_exceeded", readiness.DegradedReasons);
        Assert.Equal(LagThreshold - 30, readiness.ProjectionLagSeconds);
        Assert.Equal(3, readiness.ProjectionBacklog);
    }

    [Fact]
    public void Evaluate_ProjectionLagAtThreshold_IsNotReadyWithProjectionLagExceeded()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetProjectionStatus(backlog: 5, oldestUnprocessedReceivedAt: Start.AddSeconds(-LagThreshold));

        var readiness = Evaluate(state);

        Assert.False(readiness.IsReady);
        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("projection_lag_exceeded", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_ProjectionLagHonorsOverrideThreshold()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetProjectionStatus(backlog: 1, oldestUnprocessedReceivedAt: Start.AddSeconds(-30));

        Assert.DoesNotContain("projection_lag_exceeded", state.Evaluate(StallThreshold, 60).DegradedReasons);
        Assert.Contains("projection_lag_exceeded", state.Evaluate(StallThreshold, 20).DegradedReasons);
    }

    [Fact]
    public void Evaluate_MomentaryBackpressureUnderThreshold_IsDegradedWithIngestionBackpressure()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.RecordBackpressure();

        time.Advance(TimeSpan.FromSeconds(StallThreshold - 1));
        var readiness = Evaluate(state);

        Assert.Equal("degraded", readiness.Status);
        Assert.Contains("ingestion_backpressure", readiness.DegradedReasons);
        Assert.DoesNotContain("ingestion_stalled", readiness.DegradedReasons);
        Assert.False(readiness.IngestionAccepting);
    }

    [Fact]
    public void Evaluate_SustainedBackpressureAtThreshold_IsNotReadyWithIngestionStalled()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.RecordBackpressure();

        time.Advance(TimeSpan.FromSeconds(StallThreshold));
        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("ingestion_stalled", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_IngestionStallHonorsOverrideThreshold()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.RecordBackpressure();

        time.Advance(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain("ingestion_stalled", state.Evaluate(10, LagThreshold).DegradedReasons);
        Assert.Contains("ingestion_stalled", state.Evaluate(3, LagThreshold).DegradedReasons);
    }

    [Fact]
    public void Evaluate_CommitSuccessClearsTheStallWindow()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.RecordBackpressure();
        time.Advance(TimeSpan.FromSeconds(StallThreshold));
        state.RecordCommitSuccess();

        var readiness = Evaluate(state);

        Assert.Equal("ready", readiness.Status);
        Assert.True(readiness.IngestionAccepting);
    }

    [Fact]
    public void Evaluate_MigrationFailure_IsNotReadyWithMigrationFailed()
    {
        var time = new MutableTimeProvider(Start);
        var state = new MonitorHealthState(time);
        state.SetLoopbackBound(true);
        state.MarkMigrationFailed();
        state.SetProjectionWorkerRunning(true);

        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("migration_failed", readiness.DegradedReasons);
        Assert.False(readiness.MigrationComplete);
    }

    [Fact]
    public void Evaluate_WriterNotRunning_IsNotReadyWithWriterNotRunning()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetWriterRunning(false);

        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("writer_not_running", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_LoopbackUnbound_IsNotReadyWithLoopbackUnbound()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetLoopbackBound(false);

        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("loopback_unbound", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_ProjectionStatusNeverObserved_IsNotReadyWithStatusUnknown()
    {
        var time = new MutableTimeProvider(Start);
        var state = new MonitorHealthState(time);
        state.SetLoopbackBound(true);
        state.MarkMigrationComplete();
        state.SetWriterRunning(true);
        state.SetProjectionWorkerRunning(true);
        // Deliberately no SetProjectionStatus: lag has never been observed.

        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("projection_status_unknown", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_ProjectionStatusUnavailableAfterObserved_IsNotReady()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time); // status observed ⇒ ready
        Assert.Equal("ready", Evaluate(state).Status);

        state.RecordProjectionStatusUnavailable();

        var readiness = Evaluate(state);
        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("projection_status_unknown", readiness.DegradedReasons);
    }

    [Fact]
    public void Evaluate_FatalWorkerError_IsNotReadyWithFatalError()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.MarkFatal();

        var readiness = Evaluate(state);

        Assert.Equal("not_ready", readiness.Status);
        Assert.Contains("fatal_error", readiness.DegradedReasons);
    }

    [Fact]
    public void Serialize_EmitsTheFullPinnedReadinessBody_ForReadyState()
    {
        var time = new MutableTimeProvider(Start);
        var state = ReadyState(time);
        state.SetProjectionStatus(backlog: 0, oldestUnprocessedReceivedAt: null);
        state.SetSpanProjectionStatus(backlog: 0, oldestUnprocessedReceivedAt: null);

        var json = MonitorReadinessJson.Serialize(Evaluate(state));

        Assert.Contains("\"status\":\"ready\"", json);
        Assert.Contains("\"checks\":", json);
        Assert.Contains("\"loopback_bound\":true", json);
        Assert.Contains("\"db_open\":true", json);
        Assert.Contains("\"migration_complete\":true", json);
        Assert.Contains("\"writer_running\":true", json);
        Assert.Contains("\"projection_worker_running\":true", json);
        Assert.Contains("\"ingestion_accepting\":true", json);
        Assert.Contains("\"projection_lag_seconds\":0", json);
        Assert.Contains("\"projection_backlog\":0", json);
        Assert.Contains("\"span_projection_lag_seconds\":0", json);
        Assert.Contains("\"span_projection_backlog\":0", json);
        Assert.Contains("\"projection_failure_count\":0", json);
        Assert.Contains("\"degraded_reasons\":[]", json);
    }
}
