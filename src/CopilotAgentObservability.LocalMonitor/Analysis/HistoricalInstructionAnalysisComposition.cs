namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class HistoricalInstructionAnalysisCompositionV1
{
    private readonly HistoricalEvidenceApplicationServiceV1 extractionService;
    private readonly SqliteHistoricalInstructionAnalysisStoreV1 store;
    private readonly bool rawExecutionAllowed;
    private readonly TimeProvider timeProvider;

    internal HistoricalInstructionAnalysisCompositionV1(
        HistoricalEvidenceApplicationServiceV1 extractionService,
        SqliteHistoricalInstructionAnalysisStoreV1 store,
        bool rawExecutionAllowed,
        TimeProvider? timeProvider = null)
    {
        this.extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.rawExecutionAllowed = rawExecutionAllowed;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal HistoricalInstructionAnalysisApplicationServiceV1 CreateRunner(
        IHistoricalInstructionAnalysisProviderV1 provider)
    {
        if (!rawExecutionAllowed)
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidContract);
        return new(extractionService, store, provider, timeProvider);
    }

    internal HistoricalInstructionAnalysisReadV1? Get(long runId)
    {
        var read = store.Get(runId)?.ToRead();
        if (read is not null) _ = HistoricalInstructionAnalysisReadConsumerV1.Validate(read);
        return read;
    }
}
