namespace CopilotAgentObservability.LocalMonitor.Tests;

using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.Reflection;

public sealed class RawHttpTerminalMigrationTests
{
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

        var terminalCalls = Count(source, ".TrySealRawResponse()")
            + Count(source, ".TryCompleteWithoutRaw()")
            + Count(source, ".TrySealRawReplayTransientPublication(");

        Assert.Equal(expectedTerminalCalls, terminalCalls);
    }

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public void HttpOwner_TerminalFailure_DiscardsBufferedEntityAndAbortsWithoutStartingResponse(
        string owner,
        int terminalValue)
    {
        _ = owner;
        var terminal = (RetentionRawTerminalResult)terminalValue;
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        var context = new DefaultHttpContext(features);
        var entity = "buffered-raw-entity";

        Assert.False(RawResponsePublication.AuthorizesRawDerivedPublication(terminal));
        entity = string.Empty;
        RawResponsePublication.Abort(context);

        Assert.Empty(entity);
        Assert.False(responseFeature.HasStarted);
        Assert.Empty(responseFeature.Headers);
        Assert.Equal(0, responseFeature.Body.Length);
    }

    [Fact]
    public async Task RazorLeaseTracker_SuccessReleasesExactlyOnceAfterResponseCompletion()
    {
        var responseFeature = new RecordingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpResponseFeature>(responseFeature);
        var context = new DefaultHttpContext(features);
        var releases = 0;
        var tracker = new RawRazorPageLeaseTracker();
        tracker.Add(new RecordingAsyncDisposable(() => releases++));

        tracker.TransferTo(context.Response);

        Assert.Equal(0, releases);
        await responseFeature.CompleteAsync();
        await tracker.DisposeAsync();
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task RazorLeaseTracker_DiscardReleasesExactlyOnceImmediately()
    {
        var releases = 0;
        var tracker = new RawRazorPageLeaseTracker();
        tracker.Add(new RecordingAsyncDisposable(() => releases++));

        await tracker.DisposeAsync();
        await tracker.DisposeAsync();

        Assert.Equal(1, releases);
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

    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> completed = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
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
}
