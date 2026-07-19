namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionMutationEvaluationInput
{
    public bool TokenValid { get; init; } = true;
    public bool TokenConsumed { get; init; }
    public bool TokenUnexpired { get; init; } = true;
    public bool BindingMatches { get; init; } = true;
    public bool TargetSetMatches { get; init; } = true;
    public bool PinVectorMatches { get; init; } = true;
    public bool RetentionMatches { get; init; } = true;
    public bool ConflictMatches { get; init; } = true;
    public bool VersionMatches { get; init; } = true;
}

public sealed record RetentionMutationEvaluationResult(
    RetentionMutationEvaluationCheck? FailedCheck,
    string? Code,
    IReadOnlyList<RetentionMutationEvaluationCheck> FailedChecks)
{
    public bool Passed => FailedCheck is null;
}

public static class RetentionMutationEvaluationOrder
{
    public static IReadOnlyList<RetentionMutationEvaluationCheck> Checks { get; } =
    [
        RetentionMutationEvaluationCheck.TokenValidity,
        RetentionMutationEvaluationCheck.TokenConsumption,
        RetentionMutationEvaluationCheck.Expiry,
        RetentionMutationEvaluationCheck.Binding,
        RetentionMutationEvaluationCheck.TargetSet,
        RetentionMutationEvaluationCheck.PinVector,
        RetentionMutationEvaluationCheck.Retention,
        RetentionMutationEvaluationCheck.Conflict,
        RetentionMutationEvaluationCheck.Version
    ];

    public static RetentionMutationEvaluationResult Evaluate(RetentionMutationEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var check in Checks)
        {
            var failed = check switch
            {
                RetentionMutationEvaluationCheck.TokenValidity => !input.TokenValid,
                RetentionMutationEvaluationCheck.TokenConsumption => input.TokenConsumed,
                RetentionMutationEvaluationCheck.Expiry => !input.TokenUnexpired,
                RetentionMutationEvaluationCheck.Binding => !input.BindingMatches,
                RetentionMutationEvaluationCheck.TargetSet => !input.TargetSetMatches,
                RetentionMutationEvaluationCheck.PinVector => !input.PinVectorMatches,
                RetentionMutationEvaluationCheck.Retention => !input.RetentionMatches,
                RetentionMutationEvaluationCheck.Conflict => !input.ConflictMatches,
                RetentionMutationEvaluationCheck.Version => !input.VersionMatches,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (failed)
            {
                var code = check switch
                {
                    RetentionMutationEvaluationCheck.TokenValidity => RetentionMutationErrorCodes.ConfirmationInvalid,
                    RetentionMutationEvaluationCheck.TokenConsumption => RetentionMutationErrorCodes.ConfirmationConsumed,
                    RetentionMutationEvaluationCheck.Expiry => RetentionMutationErrorCodes.ConfirmationExpired,
                    RetentionMutationEvaluationCheck.Binding => RetentionMutationErrorCodes.ConfirmationBindingMismatch,
                    RetentionMutationEvaluationCheck.TargetSet => RetentionMutationErrorCodes.ConfirmationTargetChanged,
                    RetentionMutationEvaluationCheck.PinVector => RetentionMutationErrorCodes.ConfirmationPinChanged,
                    RetentionMutationEvaluationCheck.Retention => RetentionMutationErrorCodes.ConfirmationRetentionChanged,
                    RetentionMutationEvaluationCheck.Conflict => RetentionMutationErrorCodes.ConfirmationConflictChanged,
                    RetentionMutationEvaluationCheck.Version => RetentionMutationErrorCodes.ConfirmationVersionChanged,
                    _ => throw new ArgumentOutOfRangeException()
                };
                return new(check, code, [check]);
            }
        }
        return new(null, null, []);
    }
}
