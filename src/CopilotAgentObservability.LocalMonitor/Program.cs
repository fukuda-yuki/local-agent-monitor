namespace CopilotAgentObservability.LocalMonitor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var parseResult = MonitorOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            Console.Error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        return await MonitorHost.RunAsync(parseResult.Options!, Console.Out, Console.Error, CancellationToken.None);
    }
}
