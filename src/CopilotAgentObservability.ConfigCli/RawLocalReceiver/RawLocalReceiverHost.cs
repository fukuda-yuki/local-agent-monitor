using System.Net;

namespace CopilotAgentObservability.ConfigCli;

internal static class RawLocalReceiverHost
{
    public static int Run(RawLocalReceiverOptions options, TextWriter output)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(ToListenerPrefix(options.Url));
        listener.Start();
        output.WriteLine($"Raw local receiver listening on {options.Url}.");
        output.WriteLine($"Raw store: {options.DatabasePath}");
        output.WriteLine("Press Ctrl+C to stop.");

        var stopping = false;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping = true;
            listener.Stop();
        };

        try
        {
            while (!stopping)
            {
                var context = listener.GetContext();
                HandleContext(context, options.DatabasePath);
            }
        }
        catch (HttpListenerException) when (stopping)
        {
            // Expected when listener.Stop() is called during GetContext().
        }

        output.WriteLine("Raw local receiver stopped.");
        return 0;
    }

    private static void HandleContext(HttpListenerContext context, string databasePath)
    {
        var request = context.Request;
        using var bodyStream = new MemoryStream();
        request.InputStream.CopyTo(bodyStream);

        var handlerResponse = RawLocalReceiverHandler.Handle(new RawLocalReceiverRequest(
            Method: request.HttpMethod,
            Path: request.Url?.AbsolutePath ?? string.Empty,
            ContentType: request.ContentType,
            Body: bodyStream.ToArray(),
            DatabasePath: databasePath,
            ReceivedAt: DateTimeOffset.UtcNow));

        var response = context.Response;
        response.StatusCode = handlerResponse.StatusCode;
        response.ContentType = handlerResponse.ContentType;
        var responseBytes = Encoding.UTF8.GetBytes(handlerResponse.Body);
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = responseBytes.Length;
        response.OutputStream.Write(responseBytes);
        response.Close();
    }

    private static string ToListenerPrefix(string url)
    {
        return url.EndsWith("/", StringComparison.Ordinal) ? url : $"{url}/";
    }
}
