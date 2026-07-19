using CopilotAgentObservability.ConfigCli;
using System.Text;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class AtomicDiagnosisOutputPublisherTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"cao-diagnosis-output-{Guid.NewGuid():N}");

    public AtomicDiagnosisOutputPublisherTests()
    {
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void Publish_WritesBothRequestedOutputs()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");

        new AtomicDiagnosisOutputPublisher().Publish(Output(csv, "csv-content"), Output(json, "json-content"));

        Assert.Equal("csv-content", File.ReadAllText(csv));
        Assert.Equal("json-content", File.ReadAllText(json));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.staging-*"));
    }

    [Fact]
    public void Publish_RefusesExistingTargetWithoutChangingItOrPublishingOtherOutput()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");
        File.WriteAllText(csv, "existing");

        var exception = Assert.Throws<InvalidOperationException>(() => new AtomicDiagnosisOutputPublisher().Publish(Output(csv, "new"), Output(json, "json-content")));

        Assert.Equal(AtomicDiagnosisOutputPublisher.PublicationFailedMessage, exception.Message);
        Assert.Equal("existing", File.ReadAllText(csv));
        Assert.False(File.Exists(json));
    }

    [Fact]
    public void Publish_RollsBackBothOutputsWhenCheckpointFailsAfterFirstPublication()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");
        var publisher = new AtomicDiagnosisOutputPublisher(checkpoint: checkpoint =>
        {
            if (checkpoint == AtomicDiagnosisOutputCheckpoint.AfterFirstPublish)
            {
                throw new InvalidOperationException("injected");
            }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => publisher.Publish(Output(csv, "csv-content"), Output(json, "json-content")));

        Assert.Equal(AtomicDiagnosisOutputPublisher.PublicationFailedMessage, exception.Message);
        Assert.False(File.Exists(csv));
        Assert.False(File.Exists(json));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.staging-*"));
    }

    [Fact]
    public void Publish_PreservesReplacementOfFirstOutputDuringRollback()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");
        var publisher = new AtomicDiagnosisOutputPublisher(checkpoint: checkpoint =>
        {
            if (checkpoint == AtomicDiagnosisOutputCheckpoint.AfterFirstPublish)
            {
                File.Delete(csv);
                File.WriteAllText(csv, "replacement");
                throw new InvalidOperationException("injected");
            }
        });

        Assert.Throws<InvalidOperationException>(() => publisher.Publish(Output(csv, "csv-content"), Output(json, "json-content")));

        Assert.Equal("replacement", File.ReadAllText(csv));
        Assert.False(File.Exists(json));
        Assert.Empty(Directory.EnumerateFiles(directory, "diagnosis.json.staging-*"));
    }

    [Fact]
    public void Publish_ReturnsFixedCleanupFailureWhenOwnedOutputCannotBeRemoved()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");
        FileStream? heldOutput = null;
        var publisher = new AtomicDiagnosisOutputPublisher(checkpoint: checkpoint =>
        {
            if (checkpoint == AtomicDiagnosisOutputCheckpoint.AfterFirstPublish)
            {
                heldOutput = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new InvalidOperationException("injected");
            }
        });

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => publisher.Publish(Output(csv, "csv-content"), Output(json, "json-content")));

            Assert.Equal("Diagnosis output publication cleanup failed.", exception.Message);
            Assert.Equal("csv-content", File.ReadAllText(csv));
            Assert.False(File.Exists(json));
        }
        finally
        {
            heldOutput?.Dispose();
        }
    }

    [Fact]
    public void Publish_PreservesStagingCollisionAndLeavesNoOwnedStagingFile()
    {
        var csv = Path.Combine(directory, "diagnosis.csv");
        var json = Path.Combine(directory, "diagnosis.json");
        var collision = Path.Combine(directory, "collision.staging-fixed");
        File.WriteAllText(collision, "third-party");
        var publisher = new AtomicDiagnosisOutputPublisher(stagingPathFactory: _ => collision);

        Assert.Throws<InvalidOperationException>(() => publisher.Publish(Output(csv, "csv-content"), Output(json, "json-content")));

        Assert.Equal("third-party", File.ReadAllText(collision));
        Assert.False(File.Exists(csv));
        Assert.False(File.Exists(json));
        Assert.Single(Directory.EnumerateFiles(directory, "*.staging-*"));
    }

    [Fact]
    public void Publish_RejectsSameCanonicalTarget()
    {
        var target = Path.Combine(directory, "diagnosis.csv");
        var alternateSpelling = Path.Combine(directory, ".", "diagnosis.csv");

        var exception = Assert.Throws<InvalidOperationException>(() => new AtomicDiagnosisOutputPublisher().Publish(Output(target, "csv-content"), Output(alternateSpelling, "json-content")));

        Assert.Equal(AtomicDiagnosisOutputPublisher.InvalidRequestMessage, exception.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Publish_UsesSanitizedFailureWithoutPathOrContentLeakage()
    {
        var target = Path.Combine(directory, "private-diagnosis.csv");
        const string content = "private-output-content";
        File.WriteAllText(target, "existing");

        var exception = Assert.Throws<InvalidOperationException>(() => new AtomicDiagnosisOutputPublisher().Publish(Output(target, content), null));

        Assert.DoesNotContain(directory, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(content, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnosis.csv", exception.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(DiagnosisOutputPublication).FullName, Output(target, content).ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DiagnosisOutputPublication Output(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content));
}
