namespace CopilotAgentObservability.ConfigCli;

internal sealed record DiagnosisOutputPublication(string TargetPath, ReadOnlyMemory<byte> Content)
{
    public override string ToString() => GetType().FullName!;
}

internal enum AtomicDiagnosisOutputCheckpoint
{
    AfterFirstPublish,
}

internal sealed class AtomicDiagnosisOutputPublisher(
    Action<AtomicDiagnosisOutputCheckpoint>? checkpoint = null,
    Func<string, string>? stagingPathFactory = null)
{
    internal const string PublicationFailedMessage = "Diagnosis output publication failed.";
    internal const string CleanupFailedMessage = "Diagnosis output publication cleanup failed.";
    internal const string InvalidRequestMessage = "Diagnosis output publication request is invalid.";

    public void Publish(DiagnosisOutputPublication? csv, DiagnosisOutputPublication? json)
    {
        PlannedOutput[] outputs;

        try
        {
            outputs = new[] { csv, json }
                .Where(output => output is not null)
                .Cast<DiagnosisOutputPublication>()
                .Select(CreatePlannedOutput)
                .ToArray();
            ValidateRequest(outputs);
        }
        catch
        {
            throw new InvalidOperationException(InvalidRequestMessage);
        }

        if (outputs.Any(output => File.Exists(output.TargetPath) || Directory.Exists(output.TargetPath)))
        {
            throw new InvalidOperationException(PublicationFailedMessage);
        }

        var stagingFiles = new List<string>();
        var publishedTargets = new List<PublishedOutput>();

        try
        {
            foreach (var output in outputs)
            {
                var stagingPath = CreateSiblingStagingPath(output.TargetPath);
                using var stream = new FileStream(stagingPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                });
                stagingFiles.Add(stagingPath);
                stream.Write(output.Content.Span);
                stream.Flush(flushToDisk: true);
                output.StagingPath = stagingPath;
            }

            for (var index = 0; index < outputs.Length; index++)
            {
                var output = outputs[index];
                File.Move(output.StagingPath!, output.TargetPath, overwrite: false);
                publishedTargets.Add(PublishedOutput.Create(output));

                if (index == 0 && outputs.Length > 1)
                {
                    checkpoint?.Invoke(AtomicDiagnosisOutputCheckpoint.AfterFirstPublish);
                }
            }
        }
        catch
        {
            throw new InvalidOperationException(
                Cleanup(stagingFiles, publishedTargets)
                    ? PublicationFailedMessage
                    : CleanupFailedMessage);
        }
    }

    private static void ValidateRequest(IReadOnlyList<PlannedOutput> outputs)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targets = new HashSet<string>(comparer);

        foreach (var output in outputs)
        {
            if (!targets.Add(output.TargetPath))
            {
                throw new InvalidOperationException();
            }

            var parent = Path.GetDirectoryName(output.TargetPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                throw new InvalidOperationException();
            }
        }
    }

    private static PlannedOutput CreatePlannedOutput(DiagnosisOutputPublication output)
    {
        if (string.IsNullOrWhiteSpace(output.TargetPath))
        {
            throw new InvalidOperationException();
        }

        return new PlannedOutput(Path.GetFullPath(output.TargetPath), output.Content);
    }

    private string CreateSiblingStagingPath(string targetPath)
    {
        var stagingPath = stagingPathFactory?.Invoke(targetPath) ?? $"{targetPath}.staging-{Guid.NewGuid():N}";
        var canonicalStagingPath = Path.GetFullPath(stagingPath);
        var targetParent = Path.GetDirectoryName(targetPath);
        var stagingParent = Path.GetDirectoryName(canonicalStagingPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        if (string.IsNullOrEmpty(targetParent) || !comparer.Equals(targetParent, stagingParent) || comparer.Equals(targetPath, canonicalStagingPath))
        {
            throw new InvalidOperationException();
        }

        return canonicalStagingPath;
    }

    private static bool Cleanup(IEnumerable<string> stagingFiles, IEnumerable<PublishedOutput> publishedTargets)
    {
        var complete = true;

        foreach (var stagingFile in stagingFiles)
        {
            complete &= TryDelete(stagingFile);
        }

        foreach (var publishedTarget in publishedTargets)
        {
            complete &= TryDeletePublishedOutput(publishedTarget);
        }

        return complete;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeletePublishedOutput(PublishedOutput output)
    {
        try
        {
            if (!File.Exists(output.TargetPath))
            {
                return true;
            }

            using (var stream = new FileStream(output.TargetPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.None,
            }))
            {
                if (stream.Length != output.Length || !System.Security.Cryptography.SHA256.HashData(stream).AsSpan().SequenceEqual(output.Sha256))
                {
                    return true;
                }
            }

            // The trusted-local boundary accepts the small post-verification path race.
            File.Delete(output.TargetPath);
            return !File.Exists(output.TargetPath);
        }
        catch
        {
            return false;
        }
    }

    private sealed class PlannedOutput(string targetPath, ReadOnlyMemory<byte> content)
    {
        public string TargetPath { get; } = targetPath;
        public ReadOnlyMemory<byte> Content { get; } = content;
        public string? StagingPath { get; set; }
    }

    private sealed record PublishedOutput(string TargetPath, long Length, byte[] Sha256)
    {
        public static PublishedOutput Create(PlannedOutput output) =>
            new(output.TargetPath, output.Content.Length, System.Security.Cryptography.SHA256.HashData(output.Content.Span));
    }
}
