using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Gate 8 native outcomes are total and use no exception-message mapping; composition maps them
// to the exact route results. Precedence after a target exists is unsafe -> raced -> missing ->
// other native failure -> oversized -> binary -> success.
internal enum CurrentSkillNativeOutcomeV1
{
    Success,
    Unsafe,
    Raced,
    Missing,
    OtherNativeFailure,
    Oversized,
    Binary
}

internal sealed class CurrentSkillNativeReadResultV1
{
    private CurrentSkillNativeReadResultV1(
        CurrentSkillNativeOutcomeV1 outcome,
        byte[]? body,
        byte[]? bodySha256,
        DateTimeOffset? readAt)
    {
        Outcome = outcome;
        Body = body;
        BodySha256 = bodySha256;
        ReadAt = readAt;
    }

    public CurrentSkillNativeOutcomeV1 Outcome { get; }

    public byte[]? Body { get; }

    public byte[]? BodySha256 { get; }

    public DateTimeOffset? ReadAt { get; }

    public static CurrentSkillNativeReadResultV1 Success(byte[] body, byte[] bodySha256, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(bodySha256);

        if (bodySha256.Length != 32)
        {
            throw new ArgumentException("The body digest must be exactly 32 bytes.", nameof(bodySha256));
        }

        return new CurrentSkillNativeReadResultV1(CurrentSkillNativeOutcomeV1.Success, body, bodySha256, readAt);
    }

    public static CurrentSkillNativeReadResultV1 Failure(CurrentSkillNativeOutcomeV1 outcome)
    {
        if (outcome == CurrentSkillNativeOutcomeV1.Success)
        {
            throw new ArgumentException("Success is not a failure outcome.", nameof(outcome));
        }

        return new CurrentSkillNativeReadResultV1(outcome, null, null, null);
    }
}

// One retained discovery root for the whole process generation: canonical path key, native
// identity captured at retention, and the noninheritable retained handle/fd. The generation
// owns the handle; request code re-proves it but never disposes it. Disposal happens only
// after the last generation lease is released at shutdown.
internal sealed class RetainedDiscoveryRootV1 : IDisposable
{
    private int disposed;

    public RetainedDiscoveryRootV1(
        DiscoveryRootKindV1 kind,
        SkillProducerPathKeyV1 pathKey,
        DiscoveryRootNativeIdentityV1 nativeIdentity,
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.IsInvalid)
        {
            throw new ArgumentException("The retained root handle must be valid.", nameof(handle));
        }

        Kind = kind;
        PathKey = pathKey;
        NativeIdentity = nativeIdentity;
        Handle = handle;
    }

    public DiscoveryRootKindV1 Kind { get; }

    public SkillProducerPathKeyV1 PathKey { get; }

    public DiscoveryRootNativeIdentityV1 NativeIdentity { get; }

    public SafeFileHandle Handle { get; }

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Handle.Dispose();
        }
    }
}

// The request-local read target derived by prefix segmentation of one eligible candidate path
// key against one retained root: it carries only the retained root handle/identity, role,
// relative segments, and expected revision, never strings rebuilt through path operations.
internal sealed class CurrentSkillReadTargetV1
{
    // Derived from the 4,096 strict UTF-8 byte input bound: n segments need at least 2n-1
    // bytes (one-byte segments plus one-byte separators), so no parsed key can carry more.
    internal const int MaximumRelativeSegments = 2_048;

    internal const string FinalSegmentFileName = "SKILL.md";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly char[] ForbiddenWindowsSegmentCharacters = ['<', '>', '"', '|', '?', '*', ':', '/', '\\'];

    public CurrentSkillReadTargetV1(
        RetainedDiscoveryRootV1 retainedRoot,
        IReadOnlyList<string> relativeSegments,
        string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(retainedRoot);
        ArgumentNullException.ThrowIfNull(relativeSegments);

        if (retainedRoot.IsDisposed)
        {
            throw new ArgumentException("The retained root must not be disposed.", nameof(retainedRoot));
        }

        if (relativeSegments.Count is 0 or > MaximumRelativeSegments)
        {
            throw new ArgumentException(
                $"The target requires 1..{MaximumRelativeSegments} relative segments.",
                nameof(relativeSegments));
        }

        for (var index = 0; index < relativeSegments.Count; index++)
        {
            ValidateSegment(relativeSegments[index], retainedRoot.PathKey.Platform, nameof(relativeSegments));
        }

        if (string.Equals(relativeSegments[^1], FinalSegmentFileName, StringComparison.Ordinal) is false)
        {
            throw new ArgumentException("The final relative segment must be the exact ordinal SKILL.md.", nameof(relativeSegments));
        }

        if (string.IsNullOrEmpty(expectedRevision))
        {
            throw new ArgumentException("The expected revision must be nonempty.", nameof(expectedRevision));
        }

        RetainedRoot = retainedRoot;
        RelativeSegments = relativeSegments;
        ExpectedRevision = expectedRevision;
    }

    public RetainedDiscoveryRootV1 RetainedRoot { get; }

    public DiscoveryRootKindV1 RootRole => RetainedRoot.Kind;

    public IReadOnlyList<string> RelativeSegments { get; }

    public string ExpectedRevision { get; }

    private static void ValidateSegment(string segment, SkillProducerPathKeyPlatform platform, string argumentName)
    {
        ArgumentNullException.ThrowIfNull(segment, argumentName);

        if (segment.Length == 0 || segment is "." or "..")
        {
            throw new ArgumentException("A relative segment must be nonempty and never . or ..", argumentName);
        }

        foreach (var rune in segment.EnumerateRunes())
        {
            if (rune.Value <= 0x1f || rune.Value == 0x7f)
            {
                throw new ArgumentException("A relative segment must not contain control characters.", argumentName);
            }
        }

        if (platform == SkillProducerPathKeyPlatform.Windows)
        {
            if (segment.Length > 255 || segment.IndexOfAny(ForbiddenWindowsSegmentCharacters) >= 0)
            {
                throw new ArgumentException("A Windows relative segment violates the producer path grammar.", argumentName);
            }
        }
        else
        {
            if (segment.Contains('/') || StrictUtf8.GetByteCount(segment) > 255)
            {
                throw new ArgumentException("A Linux relative segment violates the producer path grammar.", argumentName);
            }
        }
    }
}

internal interface ICurrentSkillNativeFileReaderV1
{
    CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken);
}

// Root preflight failures carry test-level granularity only; composition collapses every one of
// them to skill_discovery_root_configuration_invalid and never emits a root value or native fact.
internal enum DiscoveryRootOpenFailureV1
{
    InvalidSyntax,
    NotLocal,
    Unopenable,
    NotADirectory,
    ReparseRoot,
    FilesystemNotCertified,
    KernelUnsupported,
    StatxMaskIncomplete,
    Other
}

internal sealed class DiscoveryRootOpenResultV1
{
    private DiscoveryRootOpenResultV1(bool isSuccess, RetainedDiscoveryRootV1? root, DiscoveryRootOpenFailureV1? failure)
    {
        IsSuccess = isSuccess;
        Root = root;
        Failure = failure;
    }

    public bool IsSuccess { get; }

    public RetainedDiscoveryRootV1? Root { get; }

    public DiscoveryRootOpenFailureV1? Failure { get; }

    public static DiscoveryRootOpenResultV1 Succeeded(RetainedDiscoveryRootV1 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new DiscoveryRootOpenResultV1(true, root, null);
    }

    public static DiscoveryRootOpenResultV1 Failed(DiscoveryRootOpenFailureV1 failure) =>
        new(false, null, failure);
}

internal interface IDiscoveryRootOpenerV1
{
    DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind);

    bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root);
}
