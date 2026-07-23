using System.Numerics;

namespace CopilotAgentObservability.Pricing;

internal static class PricingExactDecimal
{
    private static readonly BigInteger MaximumCoefficient =
        (BigInteger.One << 96) - BigInteger.One;

    internal static int Scale(decimal value)
    {
        var (coefficient, scale) = Decompose(value);
        coefficient = BigInteger.Abs(coefficient);
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        return scale;
    }

    internal static decimal Multiply(
        decimal left,
        decimal right,
        int additionalScale = 0)
    {
        var (leftCoefficient, leftScale) = Decompose(left);
        var (rightCoefficient, rightScale) = Decompose(right);
        return Create(
            leftCoefficient * rightCoefficient,
            checked(leftScale + rightScale + additionalScale));
    }

    internal static decimal Sum(IEnumerable<decimal> values)
    {
        var parts = values.Select(Decompose).ToArray();
        if (parts.Length == 0)
        {
            return 0m;
        }

        var scale = parts.Max(part => part.Scale);
        var coefficient = parts.Aggregate(
            BigInteger.Zero,
            (sum, part) =>
                sum + part.Coefficient * BigInteger.Pow(10, scale - part.Scale));
        return Create(coefficient, scale);
    }

    private static (BigInteger Coefficient, int Scale) Decompose(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = new BigInteger((uint)bits[0])
            | new BigInteger((uint)bits[1]) << 32
            | new BigInteger((uint)bits[2]) << 64;
        if ((bits[3] & int.MinValue) != 0)
        {
            coefficient = -coefficient;
        }

        return (coefficient, (bits[3] >> 16) & 0x7F);
    }

    private static decimal Create(BigInteger coefficient, int scale)
    {
        var negative = coefficient.Sign < 0;
        coefficient = BigInteger.Abs(coefficient);
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        if (scale > 28 || coefficient > MaximumCoefficient)
        {
            throw new ArgumentException(
                "Pricing arithmetic is not exactly representable within the v1 decimal contract.");
        }

        var low = (uint)(coefficient & uint.MaxValue);
        var middle = (uint)((coefficient >> 32) & uint.MaxValue);
        var high = (uint)((coefficient >> 64) & uint.MaxValue);
        return new decimal(
            (int)low,
            (int)middle,
            (int)high,
            negative,
            (byte)scale);
    }
}
