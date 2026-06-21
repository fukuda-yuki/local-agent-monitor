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

        while (true)
        {
            var context = listener.GetContext();
            HandleContext(context, options.DatabasePath);
        }
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
