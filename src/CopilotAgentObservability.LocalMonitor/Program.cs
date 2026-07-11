namespace CopilotAgentObservability.LocalMonitor;

using CopilotAgentObservability.LocalMonitor.HookForwarding;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], HookForwardCommand.CommandName, StringComparison.Ordinal))
        {
            return await HookForwardCommand.RunAsync(
                args[1..],
                Console.In,
                Console.Out,
                Console.Error,
                handler: null,
                CancellationToken.None);
        }

        var parseResult = MonitorOptions.Parse(args);
        if (parseResult.Error is not null)
        {
            Console.Error.WriteLine($"error: {parseResult.Error}");
            return 1;
        }

        return await MonitorHost.RunAsync(parseResult.Options!, Console.Out, Console.Error, CancellationToken.None);
    }
}
