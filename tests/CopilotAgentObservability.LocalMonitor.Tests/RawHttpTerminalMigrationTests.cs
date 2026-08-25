namespace CopilotAgentObservability.LocalMonitor.Tests;

using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using System.Reflection;
using System.Text;

public sealed class RawHttpTerminalMigrationTests
{
    public static TheoryData<string, int> SafeProjectionTerminalFailures
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var owner in new[]
                     {
                         "monitor-traces", "monitor-summary", "monitor-trace-list",
                         "session-list", "session-detail",
                     })
            {
                data.Add(owner, (int)RetentionRawTerminalResult.Lost);
                data.Add(owner, (int)RetentionRawTerminalResult.Busy);
            }
            return data;
        }
    }

    [Theory]
    [InlineData("AuthorizesRawDerivedPublication", (int)RetentionRawTerminalResult.Sealed, true)]
    [InlineData("AuthorizesRawDerivedPublication", (int)RetentionRawTerminalResult.CompletedWithoutRaw, false)]
    [InlineData("AuthorizesFixedSafePublication", (int)RetentionRawTerminalResult.CompletedWithoutRaw, true)]
    [InlineData("AuthorizesFixedSafePublication", (int)RetentionRawTerminalResult.Sealed, false)]
    public void TerminalResult_AuthorizesOnlyItsNamedPublication(
        string predicateName,
        int terminalValue,
        bool expected)
    {
        var predicate = Assert.IsAssignableFrom<MethodInfo>(typeof(RawResponsePublication).GetMethod(
            predicateName,
            BindingFlags.Static | BindingFlags.NonPublic));

        Assert.Equal(expected, predicate.Invoke(null, [(RetentionRawTerminalResult)terminalValue]));
    }

    public static TheoryData<string, int> TerminalFailures
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var owner in new[]
                     {
                         "analysis-run", "raw-record", "span-detail", "prompt-label",
                         "session-content", "overview-page", "trace-list-page", "trace-detail-page",
                     })
            {
                data.Add(owner, (int)RetentionRawTerminalResult.Lost);
                data.Add(owner, (int)RetentionRawTerminalResult.Busy);
            }
            return data;
        }
    }

    [Theory]
    [InlineData("MonitorHost.cs", 5)]
    [InlineData("Sessions/SessionRoutes.cs", 2)]
    [InlineData("RawReplayRoutes.cs", 3)]
    [InlineData("Pages/Index.cshtml.cs", 1)]
    [InlineData("Pages/Traces.cshtml.cs", 1)]
    [InlineData("Pages/TraceDetail.cshtml.cs", 1)]
    [InlineData("Analysis/HistoricalEvidenceApplicationService.cs", 1)]
    [InlineData("Diagnostics/RepositoryMetadataDiagnosticsLoader.cs", 1)]
    public void CallerVisibleRawConsumers_UseTerminalAuthority(string relativePath, int expectedTerminalCalls)
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CopilotAgentObservability.LocalMonitor",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var terminalCalls = Count(source, ".TrySealRawResponse")
            + Count(source, ".TryCompleteWithoutRaw()")
            + Count(source, ".TrySealRawReplayTransientPublication(");

        Assert.Equal(expectedTerminalCalls, terminalCalls);
    }

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public async Task HttpOwner_TerminalFailure_DiscardsBufferedEntityAndAbortsWithoutStartingResponse(
        string owner,
        int terminalValue)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);
        var terminal = (RetentionRawTerminalResult)terminalValue;
        var terminalCount = 0;
        var releaseCount = 0;

        void OnTerminal()
        {
            terminalCount++;
            AssertZeroResponse(responseFeature);
        }

        if (owner is "overview-page" or "trace-list-page" or "trace-detail-page")
        {
            var prepared = await PrepareRazorOwnerAsync(
                owner,
                features,
                terminal,
                OnTerminal,
                () => releaseCount++);
            await prepared.Attached.ExecuteBufferedAsync(
                prepared.Context,
                () => prepared.Context.Response.WriteAsync("buffered-raw-entity"));
        }
        else
        {
            await ExecuteHttpOwnerAsync(
                owner,
                features,
                terminal,
                OnTerminal,
                () => releaseCount++);
        }

        Assert.Equal(1, abortFeature.AbortCount);
        AssertZeroResponse(responseFeature);
        Assert.Equal(1, terminalCount);
        Assert.Equal(1, releaseCount);
    }

    [Theory]
    [InlineData((int)RetentionReadDisposition.ConsumptionUnavailable, (int)RetentionRawTerminalResult.Lost)]
    [InlineData((int)RetentionReadDisposition.ConsumptionUnavailable, (int)RetentionRawTerminalResult.Busy)]
    [InlineData((int)RetentionReadDisposition.Busy, (int)RetentionRawTerminalResult.Lost)]
    [InlineData((int)RetentionReadDisposition.Busy, (int)RetentionRawTerminalResult.Busy)]
    public async Task RawRecordRoute_PostGrantFailureWithUnsuccessfulTerminalAbortsWithZeroResponse(
        int dispositionValue,
        int terminalValue)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);

        var store = await ExecuteHttpOwnerAsync(
            "raw-record",
            features,
            (RetentionRawTerminalResult)terminalValue,
            () => AssertZeroResponse(responseFeature),
            () => { },
            (RetentionReadDisposition)dispositionValue);

        Assert.Equal(1, abortFeature.AbortCount);
        AssertZeroResponse(responseFeature);
        Assert.Equal(1, store.TerminalCount);
        Assert.Equal(1, store.ReleaseCount);
    }

    [Theory]
    [InlineData((int)RetentionReadDisposition.ConsumptionUnavailable, StatusCodes.Status404NotFound, "raw_record_not_found")]
    [InlineData((int)RetentionReadDisposition.Busy, StatusCodes.Status503ServiceUnavailable, "persistence_busy")]
    public async Task RawRecordRoute_PostGrantFailurePublishesFixedSafeResultOnlyAfterCompletion(
        int dispositionValue,
        int expectedStatus,
        string expectedToken)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));

        var store = await ExecuteHttpOwnerAsync(
            "raw-record",
            features,
            RetentionRawTerminalResult.CompletedWithoutRaw,
            () => AssertZeroResponse(responseFeature),
            () => { },
            (RetentionReadDisposition)dispositionValue);

        Assert.Equal(expectedStatus, responseFeature.StatusCode);
        Assert.Contains(expectedToken, Encoding.UTF8.GetString(((MemoryStream)responseFeature.Body).ToArray()), StringComparison.Ordinal);
        Assert.Equal(1, store.TerminalCount);
        Assert.Equal(1, store.ReleaseCount);
    }

    [Theory]
    [MemberData(nameof(SafeProjectionTerminalFailures))]
    public async Task SafeProjectionRoute_PostGrantTerminalFailure_AbortsWithZeroResponse(
        string owner,
        int terminalValue)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);

        var store = await ExecuteSafeProjectionRouteAsync(
            owner,
            features,
            (RetentionRawTerminalResult)terminalValue);

        Assert.Equal(1, abortFeature.AbortCount);
        AssertZeroResponse(responseFeature);
        Assert.Equal(1, store.TerminalCount);
        Assert.Equal(1, store.ReleaseCount);
    }

    [Theory]
    [InlineData("monitor-traces")]
    [InlineData("monitor-summary")]
    [InlineData("monitor-trace-list")]
    public async Task MonitorSafeProjectionRoute_PreAdmissionBusy_KeepsExact503Bytes(string owner)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));

        var store = await ExecuteSafeProjectionRouteAsync(
            owner,
            features,
            RetentionRawTerminalResult.CompletedWithoutRaw,
            RetentionReadDisposition.Busy);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, responseFeature.StatusCode);
        Assert.Equal("application/json", responseFeature.Headers.ContentType);
        Assert.Equal(
            "{\"accepted\":false,\"error\":\"persistence_busy\",\"message\":\"The local monitor raw store is busy.\"}",
            Encoding.UTF8.GetString(((MemoryStream)responseFeature.Body).ToArray()));
        Assert.Equal(0, store.TerminalCount);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Theory]
    [InlineData("session-list")]
    [InlineData("session-detail")]
    public async Task SessionSafeProjectionRoute_PreAdmissionBusy_KeepsExact503Bytes(string owner)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));

        var store = await ExecuteSafeProjectionRouteAsync(
            owner,
            features,
            RetentionRawTerminalResult.CompletedWithoutRaw,
            RetentionReadDisposition.Busy);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, responseFeature.StatusCode);
        Assert.Equal("application/json", responseFeature.Headers.ContentType);
        Assert.Equal(
            "{\"error\":\"session_store_busy\"}",
            Encoding.UTF8.GetString(((MemoryStream)responseFeature.Body).ToArray()));
        Assert.Equal(0, store.TerminalCount);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Theory]
    [InlineData((int)RetentionRawTerminalResult.Lost)]
    [InlineData((int)RetentionRawTerminalResult.Busy)]
    public async Task DiagnosticsPage_PostGrantTerminalFailure_DiscardsBufferedPageAndAbortsWithZeroResponse(
        int terminalValue)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);
        var store = new OwnerProjectionStore((RetentionRawTerminalResult)terminalValue, null, null);
        var health = new CopilotAgentObservability.LocalMonitor.Health.MonitorHealthState();
        health.MarkMigrationComplete();
        var services = new ServiceCollection()
            .AddSingleton<IMonitorProjectionStore>(store)
            .AddSingleton(health)
            .AddSingleton(new MonitorOptions("unused.db", "http://127.0.0.1:4320", false, 31_457_280))
            .BuildServiceProvider();
        var context = new DefaultHttpContext(features) { RequestServices = services };
        var model = new DiagnosticsModel { PageContext = new PageContext { HttpContext = context } };

        await model.OnGetAsync();
        Assert.True(RawRazorPageLeaseTracker.TryTake(context, out var attached));
        await attached.ExecuteBufferedAsync(context, () => context.Response.WriteAsync("buffered-diagnostics-page"));

        Assert.Equal(1, abortFeature.AbortCount);
        AssertZeroResponse(responseFeature);
        Assert.Equal(1, store.TerminalCount);
        Assert.Equal(1, store.ReleaseCount);
    }

    [Fact]
    public async Task DiagnosticsPage_PreAdmissionBusy_KeepsOrdinaryUnavailablePageArm()
    {
        var store = new OwnerProjectionStore(
            RetentionRawTerminalResult.CompletedWithoutRaw,
            null,
            null)
        {
            ReadDisposition = RetentionReadDisposition.Busy,
        };
        var health = new CopilotAgentObservability.LocalMonitor.Health.MonitorHealthState();
        health.MarkMigrationComplete();
        var services = new ServiceCollection()
            .AddSingleton<IMonitorProjectionStore>(store)
            .AddSingleton(health)
            .AddSingleton(new MonitorOptions("unused.db", "http://127.0.0.1:4320", false, 31_457_280))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var model = new DiagnosticsModel { PageContext = new PageContext { HttpContext = context } };

        await model.OnGetAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.True(model.RepositoryMetadata.Unavailable);
        Assert.Equal(0, model.RepositoryMetadata.AnalyzedRecordCount);
        Assert.Empty(model.RepositoryMetadata.StatusRows);
        Assert.Empty(model.RepositoryMetadata.InventoryRows);
        Assert.False(RawRazorPageLeaseTracker.TryTake(context, out _));
        Assert.Equal(0, store.TerminalCount);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Theory]
    [InlineData("overview-page")]
    [InlineData("trace-list-page")]
    [InlineData("trace-detail-page")]
    public async Task RazorOwner_SealedTerminalPublishesOnlyAfterExactEntityIsBufferedAndReleasesAfterSend(string owner)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        var events = new List<string>();
        responseFeature.Body = new RecordingStream(() => events.Add("send"));
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var prepared = await PrepareRazorOwnerAsync(
            owner,
            features,
            RetentionRawTerminalResult.Sealed,
            () =>
            {
                Assert.False(responseFeature.HasStarted);
                Assert.Empty(responseFeature.Headers);
                Assert.Equal(0, responseFeature.Body.Length);
                events.Add("terminal");
            },
            () => events.Add("release"));
        await prepared.Attached.ExecuteBufferedAsync(prepared.Context, async () =>
        {
            events.Add("buffer");
            prepared.Context.Response.StatusCode = StatusCodes.Status202Accepted;
            prepared.Context.Response.Headers["X-Entity"] = "exact";
            prepared.Context.Response.ContentType = "text/html; charset=utf-8";
            await prepared.Context.Response.WriteAsync("exact entity");
        });

        Assert.Equal(StatusCodes.Status202Accepted, responseFeature.StatusCode);
        Assert.Equal("exact", responseFeature.Headers["X-Entity"]);
        Assert.Equal("text/html; charset=utf-8", responseFeature.Headers.ContentType);
        Assert.Equal("exact entity", Encoding.UTF8.GetString(((MemoryStream)responseFeature.Body).ToArray()));
        Assert.Equal("buffer", events[0]);
        Assert.Equal("terminal", events[1]);
        Assert.All(events.Skip(2).Take(events.Count - 3), value => Assert.Equal("send", value));
        Assert.Equal("release", events[^1]);
        Assert.Equal(1, prepared.Store.TerminalCount);
        Assert.Equal(1, prepared.Store.ReleaseCount);
    }

    [Theory]
    [InlineData("overview-page", (int)RetentionRawTerminalResult.Lost)]
    [InlineData("overview-page", (int)RetentionRawTerminalResult.Busy)]
    [InlineData("trace-list-page", (int)RetentionRawTerminalResult.Lost)]
    [InlineData("trace-list-page", (int)RetentionRawTerminalResult.Busy)]
    [InlineData("trace-detail-page", (int)RetentionRawTerminalResult.Lost)]
    [InlineData("trace-detail-page", (int)RetentionRawTerminalResult.Busy)]
    public async Task RazorOwner_TerminalFailureDiscardsExactEntityAndReleasesAfterDiscard(string owner, int terminalValue)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);
        var events = new List<string>();
        responseFeature.Body = new RecordingStream(() => events.Add("send"));
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var prepared = await PrepareRazorOwnerAsync(
            owner,
            features,
            (RetentionRawTerminalResult)terminalValue,
            () =>
            {
                Assert.False(responseFeature.HasStarted);
                Assert.Empty(responseFeature.Headers);
                Assert.Equal(0, responseFeature.Body.Length);
                events.Add("terminal");
            },
            () => events.Add("release"));
        await prepared.Attached.ExecuteBufferedAsync(prepared.Context, async () =>
        {
            events.Add("buffer");
            prepared.Context.Response.StatusCode = StatusCodes.Status202Accepted;
            prepared.Context.Response.Headers["X-Entity"] = "must-not-escape";
            await prepared.Context.Response.WriteAsync("must-not-escape");
        });

        Assert.Equal(1, abortFeature.AbortCount);
        Assert.False(responseFeature.HasStarted);
        Assert.Empty(responseFeature.Headers);
        Assert.Equal(0, responseFeature.Body.Length);
        Assert.Equal(["buffer", "terminal", "release"], events);
        Assert.Equal(1, prepared.Store.TerminalCount);
        Assert.Equal(1, prepared.Store.ReleaseCount);
    }

    [Theory]
    [InlineData("overview-page")]
    [InlineData("trace-list-page")]
    [InlineData("trace-detail-page")]
    public async Task RazorOwner_RenderFailureAbortsWithoutTerminalOrResponseAndReleasesAfterDiscard(string owner)
    {
        var responseFeature = new RecordingResponseFeature();
        var abortFeature = new RecordingLifetimeFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        features.Set<IHttpRequestLifetimeFeature>(abortFeature);
        var prepared = await PrepareRazorOwnerAsync(owner, features, RetentionRawTerminalResult.Sealed);

        await prepared.Attached.ExecuteBufferedAsync(prepared.Context, () => throw new InvalidOperationException("render failed"));

        Assert.Equal(1, abortFeature.AbortCount);
        Assert.Equal(0, prepared.Store.TerminalCount);
        Assert.False(responseFeature.HasStarted);
        Assert.Empty(responseFeature.Headers);
        Assert.Equal(0, responseFeature.Body.Length);
        Assert.Equal(1, prepared.Store.ReleaseCount);
    }

    [Theory]
    [InlineData("overview-page")]
    [InlineData("trace-list-page")]
    [InlineData("trace-detail-page")]
    public async Task RazorOwner_AllSourceTerminalsWinBeforeSendAndEachSourceReleasesAfterSend(string owner)
    {
        _ = owner;
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        var events = new List<string>();
        responseFeature.Body = new RecordingStream(() => events.Add("send"));
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var context = new DefaultHttpContext(features);
        var tracker = new RawRazorPageLeaseTracker();
        tracker.Add(new RecordingAsyncDisposable(() => events.Add("release-1")), () => { events.Add("terminal-1"); return RetentionRawTerminalResult.Sealed; });
        tracker.Add(new RecordingAsyncDisposable(() => events.Add("release-2")), () => { events.Add("terminal-2"); return RetentionRawTerminalResult.Sealed; });
        tracker.Attach(context);
        Assert.True(RawRazorPageLeaseTracker.TryTake(context, out var attached));

        await attached.ExecuteBufferedAsync(context, () => context.Response.WriteAsync("entity"));

        var firstSend = events.IndexOf("send");
        Assert.True(firstSend > events.IndexOf("terminal-1"));
        Assert.True(firstSend > events.IndexOf("terminal-2"));
        Assert.Equal(1, events.Count(value => value == "release-1"));
        Assert.Equal(1, events.Count(value => value == "release-2"));
        Assert.True(events.IndexOf("release-1") > firstSend);
        Assert.True(events.IndexOf("release-2") > firstSend);
    }

    [Fact]
    public Task RazorEntitySpool_PublishesRawPageLargerThanFourMiB() =>
        AssertLargeEntityPublishesAsync(4 * 1024 * 1024 + 1);

    [Fact]
    public async Task RawRecordRoute_PublishesThirtyMiBRawPayload()
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var payload = new string('x', 30 * 1024 * 1024);

        await ExecuteHttpOwnerAsync(
            "raw-record",
            features,
            RetentionRawTerminalResult.Sealed,
            () => { },
            () => { },
            rawPayloadJson: payload);

        Assert.Equal(StatusCodes.Status200OK, responseFeature.StatusCode);
        Assert.True(responseFeature.Body.Length > payload.Length);
    }

    [Fact]
    public async Task SessionContentRoute_PublishesEightMiBContent()
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var content = new string('x', 8 * 1024 * 1024);

        await ExecuteHttpOwnerAsync(
            "session-content",
            features,
            RetentionRawTerminalResult.Sealed,
            () => { },
            () => { },
            sessionContentJson: content);

        Assert.Equal(StatusCodes.Status200OK, responseFeature.StatusCode);
        Assert.True(responseFeature.Body.Length > content.Length);
    }

    private static async Task AssertLargeEntityPublishesAsync(int byteCount)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var context = new DefaultHttpContext(features);
        var tracker = new RawRazorPageLeaseTracker();
        tracker.Add(new RecordingAsyncDisposable(() => { }), () => RetentionRawTerminalResult.Sealed);
        tracker.Attach(context);
        Assert.True(RawRazorPageLeaseTracker.TryTake(context, out var attached));
        var entity = new string('x', byteCount);

        await attached.ExecuteBufferedAsync(context, () => context.Response.WriteAsync(entity));

        Assert.Equal(byteCount, responseFeature.Body.Length);
    }

    [Theory]
    [InlineData("overview-page")]
    [InlineData("trace-list-page")]
    [InlineData("trace-detail-page")]
    public async Task RazorOwner_MapperReadsOnlyInsideUseReferenceAndRazorUsesClosedReferenceFreeProjection(string owner)
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));
        var prepared = await PrepareRazorOwnerAsync(owner, features, RetentionRawTerminalResult.Sealed);

        await prepared.Attached.ExecuteBufferedAsync(
            prepared.Context,
            () => prepared.Context.Response.WriteAsync(prepared.Projection));

        Assert.Throws<InvalidOperationException>(() => prepared.Store.LastLease!.AcquireValueReference());
        Assert.Equal("raw-derived", Encoding.UTF8.GetString(((MemoryStream)responseFeature.Body).ToArray()));
        Assert.Equal(1, prepared.Store.TerminalCount);
        Assert.Equal(1, prepared.Store.ReleaseCount);
    }

    private static async Task<(DefaultHttpContext Context, RawRazorPageLeaseTracker.Attached Attached, OwnerProjectionStore Store, string Projection)> PrepareRazorOwnerAsync(
        string owner,
        IFeatureCollection features,
        RetentionRawTerminalResult terminalResult,
        Action? onTerminal = null,
        Action? onRelease = null)
    {
        var store = new OwnerProjectionStore(terminalResult, onTerminal, onRelease);
        var services = new ServiceCollection()
            .AddSingleton<IMonitorProjectionStore>(store)
            .AddSingleton(new MonitorOptions("unused.db", "http://127.0.0.1:4320", false, 31_457_280))
            .AddSingleton(new MonitorOverviewService(store))
            .BuildServiceProvider();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Headers = new HeaderDictionary() });
        var context = new DefaultHttpContext(features) { RequestServices = services };
        var pageContext = new PageContext { HttpContext = context };
        string? projection = null;
        IActionResult result = owner switch
        {
            "overview-page" => await ExecuteOverviewAsync(),
            "trace-list-page" => await ExecuteTraceListAsync(),
            "trace-detail-page" => await ExecuteTraceDetailAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };

        Assert.IsType<PageResult>(result);
        Assert.True(RawRazorPageLeaseTracker.TryTake(context, out var attached));
        Assert.Equal("raw-derived", projection);
        return (context, attached, store, projection!);

        async Task<IActionResult> ExecuteOverviewAsync()
        {
            var model = new IndexModel { PageContext = pageContext };
            var pageResult = await model.OnGetAsync();
            projection = model.PromptFor(OwnerProjectionStore.TraceId);
            return pageResult;
        }

        async Task<IActionResult> ExecuteTraceListAsync()
        {
            var model = new TracesModel { PageContext = pageContext };
            var pageResult = await model.OnGetAsync();
            projection = model.PromptFor(OwnerProjectionStore.TraceId);
            return pageResult;
        }

        async Task<IActionResult> ExecuteTraceDetailAsync()
        {
            var model = new TraceDetailModel { PageContext = pageContext };
            var pageResult = await model.OnGetAsync(OwnerProjectionStore.TraceId);
            projection = model.PromptLabel;
            return pageResult;
        }
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0; index += fragment.Length)
        {
            count++;
        }
        return count;
    }

    private static void AssertZeroResponse(RecordingResponseFeature responseFeature)
    {
        Assert.False(responseFeature.HasStarted);
        Assert.Equal(StatusCodes.Status200OK, responseFeature.StatusCode);
        Assert.Empty(responseFeature.Headers);
        Assert.Equal(0, responseFeature.Body.Length);
    }

    private static async Task<OwnerProjectionStore> ExecuteHttpOwnerAsync(
        string owner,
        IFeatureCollection features,
        RetentionRawTerminalResult terminalResult,
        Action onTerminal,
        Action onRelease,
        RetentionReadDisposition? postGrantDisposition = null,
        string? rawPayloadJson = null,
        string? sessionContentJson = null)
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider).CreateSchema();
        var projectionStore = new OwnerProjectionStore(terminalResult, onTerminal, onRelease)
        {
            PostGrantDisposition = postGrantDisposition,
            RawPayloadJson = rawPayloadJson,
        };
        var analysisStore = new OwnerAnalysisStore(terminalResult, onTerminal, onRelease);
        var sessionStore = DispatchProxy.Create<ISessionStore, OwnerSessionStoreProxy>();
        var sessionProxy = Assert.IsAssignableFrom<OwnerSessionStoreProxy>(sessionStore);
        sessionProxy.TerminalResult = terminalResult;
        sessionProxy.OnTerminal = onTerminal;
        sessionProxy.OnRelease = onRelease;
        sessionProxy.ContentJson = sessionContentJson ?? "buffered-raw-entity";
        var options = new MonitorOptions(
            temp.DatabasePath,
            "http://127.0.0.1:0",
            false,
            MonitorOptions.DefaultMaxRequestBodyBytes);
        await using var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            AnalysisStore = analysisStore,
            ProjectionStore = projectionStore,
            SessionStore = sessionStore,
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartLocalRepositoryCatalogHostedService = false,
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
        });
        var (pattern, path, routeValues) = owner switch
        {
            "analysis-run" => (
                "/traces/{traceId}/analysis/runs/{runId:long}",
                "/traces/owner-flow-trace/analysis/runs/1",
                new RouteValueDictionary { ["traceId"] = OwnerProjectionStore.TraceId, ["runId"] = "1" }),
            "raw-record" => (
                "/traces/{rawRecordId:long}/raw",
                "/traces/1/raw",
                new RouteValueDictionary { ["rawRecordId"] = "1" }),
            "span-detail" => (
                "/traces/{traceId}/spans/{spanId}/detail",
                "/traces/owner-flow-trace/spans/owner-flow-span/detail",
                new RouteValueDictionary { ["traceId"] = OwnerProjectionStore.TraceId, ["spanId"] = "owner-flow-span" }),
            "prompt-label" => (
                "/traces/{traceId}/prompt-label",
                "/traces/owner-flow-trace/prompt-label",
                new RouteValueDictionary { ["traceId"] = OwnerProjectionStore.TraceId }),
            "session-content" => (
                "/sessions/{sessionId}/events/{eventId}/content",
                $"/sessions/{OwnerSessionStoreProxy.SessionId:D}/events/{OwnerSessionStoreProxy.EventId:D}/content",
                new RouteValueDictionary
                {
                    ["sessionId"] = OwnerSessionStoreProxy.SessionId.ToString("D"),
                    ["eventId"] = OwnerSessionStoreProxy.EventId.ToString("D"),
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => string.Equals(candidate.RoutePattern.RawText, pattern, StringComparison.Ordinal));
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Headers = new HeaderDictionary() });
        var context = new DefaultHttpContext(features)
        {
            RequestServices = app.Services,
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.RouteValues = routeValues;
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);
        return projectionStore;
    }

    private static async Task<OwnerProjectionStore> ExecuteSafeProjectionRouteAsync(
        string owner,
        IFeatureCollection features,
        RetentionRawTerminalResult terminalResult,
        RetentionReadDisposition? readDisposition = null)
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema();
        new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider).CreateSchema();
        var projectionStore = new OwnerProjectionStore(terminalResult, null, null)
        {
            ReadDisposition = readDisposition,
        };
        var sessionStore = DispatchProxy.Create<ISessionStore, OwnerSessionStoreProxy>();
        var options = new MonitorOptions(
            temp.DatabasePath,
            "http://127.0.0.1:0",
            false,
            MonitorOptions.DefaultMaxRequestBodyBytes);
        await using var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            ProjectionStore = projectionStore,
            SessionStore = sessionStore,
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartLocalRepositoryCatalogHostedService = false,
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
        });
        var (pattern, path, routeValues) = owner switch
        {
            "monitor-traces" => ("/api/monitor/traces", "/api/monitor/traces", new RouteValueDictionary()),
            "monitor-summary" => ("/api/monitor/summary", "/api/monitor/summary", new RouteValueDictionary()),
            "monitor-trace-list" => ("/api/monitor/trace-list", "/api/monitor/trace-list", new RouteValueDictionary()),
            "session-list" => ("/api/session-workspace/sessions", "/api/session-workspace/sessions", new RouteValueDictionary()),
            "session-detail" => (
                "/api/session-workspace/sessions/{sessionId}",
                $"/api/session-workspace/sessions/{OwnerSessionStoreProxy.SessionId:D}",
                new RouteValueDictionary { ["sessionId"] = OwnerSessionStoreProxy.SessionId.ToString("D") }),
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => string.Equals(candidate.RoutePattern.RawText, pattern, StringComparison.Ordinal));
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Headers = new HeaderDictionary() });
        var context = new DefaultHttpContext(features) { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.RouteValues = routeValues;
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);
        return projectionStore;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class RecordingAsyncDisposable(Action released) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            released();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public int AbortCount { get; private set; }
        public void Abort() => AbortCount++;
    }

    private sealed class OwnerProjectionStore(
        RetentionRawTerminalResult terminalResult,
        Action? onTerminal,
        Action? onRelease) : ProjectionStoreTestDouble
    {
        internal const string TraceId = "owner-flow-trace";
        private static readonly MonitorTraceRow Trace = new(
            1, TraceId, null, null, null, null, null, null, 1, 0, 0,
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", "2026-01-01T00:00:02Z",
            1, 1, 2, 1, 1, 1, "model", null, null, null, 0, 0, "ok");
        private static readonly RawTelemetryRecord Raw = new(
            1,
            RawTelemetrySources.RawOtlp,
            TraceId,
            DateTimeOffset.UnixEpoch,
            null,
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"owner-flow-trace\",\"attributes\":[{\"key\":\"gen_ai.prompt\",\"value\":{\"stringValue\":\"raw-derived\"}}]}]}]}]}");

        internal int TerminalCount { get; private set; }
        internal int ReleaseCount { get; private set; }
        internal RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>? LastLease { get; private set; }
        internal RetentionReadDisposition? ReadDisposition { get; init; }
        internal RetentionReadDisposition? PostGrantDisposition { get; init; }
        internal string? RawPayloadJson { get; init; }

        public override MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) => new([Trace], false);
        public override IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) => [Trace];
        public override IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) => [Trace];
        public override MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) => new([Trace], 1, 2);
        public override MonitorTraceRow? GetMonitorTrace(string traceId) => traceId == TraceId ? Trace : null;
        public override MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) =>
            traceId == TraceId && spanId == "owner-flow-span"
                ? new MonitorSpanRow(
                    1, 1, TraceId, spanId, null, 0, "chat", "llm", null, null, null, null, null,
                    null, "model", 1, 1, 2, null, null, null, "ok", null, null, null, 1,
                    "2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", "2026-01-01T00:00:02Z")
                : null;

        public override ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(
            long id,
            RetentionReadKind readKind,
            CancellationToken cancellationToken)
        {
            if (PostGrantDisposition is { } disposition)
            {
                var (grant, handle) = CreateHandle();
                return ValueTask.FromResult(RetentionReadResult<RawTelemetryRecord>.FromPostGrantDisposition(
                    disposition,
                    new RetentionReadLease<RawTelemetryRecord>(
                        RetentionRevisionFence.Create(),
                        grant,
                        handle,
                        cancellationToken)));
            }
            return ValueTask.FromResult(RetentionReadResult<RawTelemetryRecord>.FromHandle(
                CreateLease(
                    RawPayloadJson is null ? Raw : Raw with { PayloadJson = RawPayloadJson },
                    cancellationToken)));
        }

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(
            string traceId,
            int limit,
            RetentionReadKind readKind,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TraceId, traceId);
            if (ReadDisposition is { } disposition)
                return ValueTask.FromResult(RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.FromDisposition(disposition));
            LastLease = CreateBatchLease<IReadOnlyList<RawTelemetryRecord>>([Raw], cancellationToken);
            return ValueTask.FromResult(RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.FromHandle(LastLease));
        }

        public override IReadOnlyList<long> ListRecentRawRecordIdsForRepositoryMetadataDiagnostics(
            int limit,
            int maxPayloadBytes,
            int maxTotalPayloadBytes) => [Raw.Id!.Value];

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(
            IReadOnlyList<long> ids,
            RetentionReadKind readKind,
            CancellationToken cancellationToken)
        {
            if (ReadDisposition is { } disposition)
                return ValueTask.FromResult(RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.FromDisposition(disposition));
            LastLease = CreateBatchLease<IReadOnlyList<RawTelemetryRecord>>([Raw], cancellationToken);
            return ValueTask.FromResult(RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>.FromHandle(LastLease));
        }

        private RetentionReadLease<T> CreateLease<T>(T value, CancellationToken cancellationToken)
        {
            var (grant, handle) = CreateHandle();
            return new RetentionReadLease<T>(
                value,
                RetentionRevisionFence.Create(),
                grant,
                handle,
                cancellationToken);
        }

        private RetentionBatchReadLease<T> CreateBatchLease<T>(T value, CancellationToken cancellationToken)
        {
            var (grant, handle) = CreateHandle();
            return new RetentionBatchReadLease<T>(
                value,
                RetentionRevisionFence.Create(),
                [grant],
                handle,
                cancellationToken);
        }

        private (RetentionReadGrant Grant, RetentionCommittedReadHandle Handle) CreateHandle()
        {
            var now = TimeProvider.System.GetUtcNow();
            var grant = new RetentionReadGrant(
                new RetentionOwnershipKey("owner-flow-store", RetentionStoreKind.RawRecord, "owner-flow"),
                "owner-flow",
                1,
                RetentionLeaseKind.Access,
                "owner-flow-lease",
                1,
                now.AddMinutes(2),
                new byte[32]);
            var handle = new RetentionCommittedReadHandle(
                [grant],
                TimeProvider.System,
                _ =>
                {
                    ReleaseCount++;
                    onRelease?.Invoke();
                    return true;
                },
                terminalAuthority: (committed, operation) =>
                {
                    TerminalCount++;
                    onTerminal?.Invoke();
                    if (terminalResult == RetentionRawTerminalResult.Lost)
                    {
                        committed.LoseTerminalAttempt();
                        return terminalResult;
                    }
                    if (terminalResult == RetentionRawTerminalResult.Busy)
                    {
                        committed.FailTerminalAttempt();
                        return terminalResult;
                    }
                    Assert.True(committed.TryMoveTerminalAttemptToPending(operation));
                    return committed.PublishTerminal(operation);
            });
            Assert.True(handle.Activate());
            Assert.True(handle.Publish());
            return (grant, handle);
        }
    }

    private sealed class OwnerAnalysisStore(
        RetentionRawTerminalResult terminalResult,
        Action onTerminal,
        Action onRelease) : IMonitorAnalysisStore
    {
        private static readonly MonitorAnalysisRun Run = new(
            1,
            OwnerProjectionStore.TraceId,
            1,
            null,
            MonitorAnalysisFocus.Latency,
            MonitorAnalysisStatus.Succeeded,
            "2026-01-01T00:00:00Z",
            "2026-01-01T00:00:01Z",
            "2026-01-01T00:00:02Z");

        public void CreateSchema() { }
        public MonitorAnalysisRun? GetRun(long runId) => runId == 1 ? Run : null;
        public ValueTask<RetentionReadResult<AnalysisRunRawSnapshot>> ReadRawSnapshotAsync(long runId, CancellationToken cancellationToken)
        {
            var now = TimeProvider.System.GetUtcNow();
            var grant = new RetentionReadGrant(
                new RetentionOwnershipKey("owner-analysis-store", RetentionStoreKind.AnalysisRunRaw, "1"),
                "1",
                1,
                RetentionLeaseKind.Access,
                "owner-analysis-lease",
                1,
                now.AddMinutes(2),
                new byte[32]);
            var handle = new RetentionCommittedReadHandle(
                [grant],
                TimeProvider.System,
                _ =>
                {
                    onRelease();
                    return true;
                },
                terminalAuthority: (committed, operation) =>
                {
                    onTerminal();
                    if (terminalResult == RetentionRawTerminalResult.Lost)
                    {
                        committed.LoseTerminalAttempt();
                        return terminalResult;
                    }
                    committed.FailTerminalAttempt();
                    return terminalResult;
                });
            Assert.True(handle.Activate());
            Assert.True(handle.Publish());
            var lease = new RetentionReadLease<AnalysisRunRawSnapshot>(
                new AnalysisRunRawSnapshot("buffered-raw-entity", null, []),
                RetentionRevisionFence.Create(),
                grant,
                handle,
                cancellationToken);
            return ValueTask.FromResult(RetentionReadResult<AnalysisRunRawSnapshot>.FromHandle(lease));
        }

        public MonitorAnalysisStartResult StartRun(string traceId, long? rawRecordId, string? spanId, MonitorAnalysisFocus focus, DateTimeOffset requestedAt) => throw new NotSupportedException();
        public IReadOnlyList<MonitorAnalysisRun> ListRunsForTrace(string traceId, int limit) => throw new NotSupportedException();
        public void MarkRunning(long runId, DateTimeOffset startedAt) => throw new NotSupportedException();
        public RetentionRevisionFence AppendEvent(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string eventType, string message, DateTimeOffset occurredAt) => throw new NotSupportedException();
        public RetentionRevisionFence CompleteRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, DateTimeOffset completedAt) => throw new NotSupportedException();
        public RetentionRevisionFence CompleteInstructionDiagnosisRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, InstructionFindingHandoffV1 handoff, DateTimeOffset completedAt) => throw new NotSupportedException();
        public RetentionRevisionFence? FinishRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt) => throw new NotSupportedException();
        public MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt) => throw new NotSupportedException();
    }

    public class OwnerSessionStoreProxy : DispatchProxy
    {
        internal static readonly Guid SessionId = Guid.Parse("019c0000-0000-7000-8000-000000000001");
        internal static readonly Guid EventId = Guid.Parse("019c0000-0000-7000-8000-000000000002");
        internal RetentionRawTerminalResult TerminalResult { get; set; }
        internal Action OnTerminal { get; set; } = null!;
        internal Action OnRelease { get; set; } = null!;
        internal string ContentJson { get; set; } = "buffered-raw-entity";
        private static readonly DateTimeOffset ObservedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly ObservedSession Session = new(
            SessionId,
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            null,
            null,
            ObservedAt,
            ObservedAt,
            ObservedAt,
            SessionRawRetentionState.Expiring,
            ObservedAt,
            ObservedAt);
        private static readonly SessionDetail Detail = new(
            Session,
            [],
            [],
            [new ObservedSessionEvent(
                EventId,
                SessionId,
                null,
                SessionSourceSurface.ClaudeCode,
                null,
                OwnerProjectionStore.TraceId,
                "ok",
                "claude-code-otel",
                "owner-event",
                "user.message",
                ObservedAt,
                SessionContentState.Available,
                MatchKind: SessionMatchKind.ExactNative)]);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ISessionStore.CreateSchema) => null,
            nameof(ISessionStore.ListActiveProposalApplyDrafts) => Array.Empty<ProposalApplyDraftMetadata>(),
            nameof(ISessionStore.ListAppliedProposalApplyLinkages) => Array.Empty<ProposalApplyLinkage>(),
            nameof(ISessionStore.ListProposalApplyPending) => Array.Empty<ProposalApplyPendingOperation>(),
            nameof(ISessionStore.ListMostRecent) => new[] { Session },
            nameof(ISessionStore.GetDetail) => Detail,
            nameof(ISessionStore.GetRawRetentionState) => SessionRawRetentionState.Expiring,
            nameof(ISessionStore.GetHumanEvaluation) => null,
            nameof(ISessionStore.ReadContentAsync) => ReadContent(),
            nameof(ISessionStore.ReadGenericRouteContentAsync) => ReadGenericRouteContent(),
            _ => throw new NotSupportedException(targetMethod?.Name),
        };

        private ValueTask<SessionContentReadResult> ReadContent() =>
            ValueTask.FromResult(new SessionContentReadResult(
                SessionContentReadDisposition.Granted, CreateLease()));

        // The generic raw content route reads through its own operation, so the proxy serves the
        // same granted lease there: these tests are about the terminal seal, not the policy check.
        private ValueTask<SessionGenericRouteContentReadResult> ReadGenericRouteContent() =>
            ValueTask.FromResult(new SessionGenericRouteContentReadResult(
                SessionGenericRouteContentDisposition.Granted, CreateLease()));

        private SessionContentReadLease CreateLease()
        {
            var content = new SessionEventContent(
                EventId,
                "prompt",
                ContentJson,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(1));
            var referenceOpen = false;
            var lease = new SessionContentReadLease(
                () =>
                {
                    OnRelease();
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    referenceOpen = true;
                    return new SessionContentUseReference(() => content, () => referenceOpen = false);
                },
                () =>
                {
                    Assert.False(referenceOpen);
                    OnTerminal();
                    return TerminalResult switch
                    {
                        RetentionRawTerminalResult.Sealed => SessionContentTerminalResult.Sealed,
                        RetentionRawTerminalResult.Lost => SessionContentTerminalResult.Lost,
                        _ => SessionContentTerminalResult.Busy,
                    };
                },
                () => throw new NotSupportedException());
            return lease;
        }
    }

    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> completed = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new RecordingStream(() => { });
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) => completed.Add((callback, state));

        internal async Task CompleteAsync()
        {
            HasStarted = true;
            foreach (var registration in completed.AsEnumerable().Reverse())
            {
                await registration.Callback(registration.State);
            }
        }
    }

    private sealed class RecordingStream(Action onWrite) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            onWrite();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            onWrite();
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onWrite();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
