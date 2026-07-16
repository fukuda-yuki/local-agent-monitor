using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli;

internal static class DoctorCli
{
    private const int MaximumInputBytes = 65_536;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryParseEvaluate(args, out var inputPath, out var jsonOutput))
        {
            return WriteResult(
                new DoctorResult(
                    DoctorSchemaVersions.ResultV1,
                    Success: false,
                    DoctorResultCode.InvalidArguments,
                    Evaluation: null,
                    Verification: null),
                jsonOutput: true,
                output,
                error);
        }

        DoctorResult result;
        try
        {
            var input = ReadBoundedUtf8(inputPath!);
            result = DoctorEvaluator.Evaluate(DoctorJson.DeserializeFactSnapshot(input));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            result = new DoctorResult(
                DoctorSchemaVersions.ResultV1,
                Success: false,
                DoctorResultCode.InvalidInput,
                Evaluation: null,
                Verification: null);
        }

        return WriteResult(result, jsonOutput, output, error);
    }

    private static bool TryParseEvaluate(string[] args, out string? inputPath, out bool jsonOutput)
    {
        inputPath = null;
        jsonOutput = false;
        if (args.Length is < 3 or > 4
            || args[0] != "evaluate"
            || args[1] != "--input"
            || string.IsNullOrWhiteSpace(args[2]))
        {
            return false;
        }

        inputPath = args[2];
        if (args.Length == 4)
        {
            if (args[3] != "--json")
            {
                return false;
            }

            jsonOutput = true;
        }

        return true;
    }

    private static string ReadBoundedUtf8(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > MaximumInputBytes || stream.ReadByte() != -1)
        {
            throw new JsonException("Doctor input is too large.");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, 0, count);
    }

    private static int WriteResult(DoctorResult result, bool jsonOutput, TextWriter output, TextWriter error)
    {
        output.WriteLine(jsonOutput ? DoctorJson.SerializeResult(result) : DoctorHumanProjector.Project(result));
        if (!result.Success)
        {
            error.WriteLine(ToWireValue(result.Code));
        }

        return result.Code switch
        {
            DoctorResultCode.EvaluationCompleted
                when result.Evaluation?.PrimaryState?.StateCode == DoctorStateCode.FirstTraceReady => 0,
            DoctorResultCode.EvaluationCompleted or DoctorResultCode.PartialFactSnapshot => 3,
            DoctorResultCode.InvalidArguments or DoctorResultCode.InvalidInput or DoctorResultCode.UnsupportedSchemaVersion => 2,
            DoctorResultCode.DoctorStoreBusy or DoctorResultCode.DoctorStoreUnavailable or DoctorResultCode.InternalError => 5,
            _ => result.Success ? 0 : 4
        };
    }

    private static string ToWireValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
