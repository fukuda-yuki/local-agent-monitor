namespace CopilotAgentObservability.Pricing;

public static class PricingDisplayRounding
{
    public static decimal Round(decimal amount, int currencyMinorUnits)
    {
        if (currencyMinorUnits is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currencyMinorUnits),
                "Currency minor units must be between zero and six.");
        }

        return decimal.Round(amount, currencyMinorUnits, MidpointRounding.ToEven);
    }
}
