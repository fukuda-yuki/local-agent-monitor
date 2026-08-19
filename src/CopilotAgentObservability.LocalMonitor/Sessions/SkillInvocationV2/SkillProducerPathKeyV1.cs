using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public enum SkillProducerPathKeyPlatform
{
    Windows,
    Linux
}

public enum SkillProducerPathKeyParseReason
{
    None,
    InputEmpty,
    ControlCharacter,
    UnpairedSurrogate,
    InputTooLarge,
    InvalidAnchor,
    InvalidSeparator,
    InvalidSegment,
    InvalidTrailingSeparator
}

public readonly struct SkillProducerPathKeyV1 : IEquatable<SkillProducerPathKeyV1>
{
    private const int MaximumInputUtf8Bytes = 4_096;
    private const int MaximumWindowsSegmentCodeUnits = 255;
    private const int MaximumLinuxSegmentUtf8Bytes = 255;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly char[] ForbiddenWindowsSegmentCharacters = ['<', '>', '"', '|', '?', '*', ':', '/', '\\'];

    private static readonly HashSet<string> ReservedWindowsStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string[] segments;
    private readonly char driveLetter;

    private SkillProducerPathKeyV1(SkillProducerPathKeyPlatform platform, char driveLetter, string[] segments, string key)
    {
        Platform = platform;
        this.driveLetter = driveLetter;
        this.segments = segments;
        Key = key;
    }

    public SkillProducerPathKeyPlatform Platform { get; }

    public string Key { get; }

    // Internal accessors for the Gate 8 retained-root opener and the current-file target
    // derivation: the canonical key string never goes back through string path operations.
    internal char DriveLetter => driveLetter;

    internal IReadOnlyList<string> Segments => segments;

    public static bool TryParse(
        string? input,
        SkillProducerPathKeyPlatform platform,
        out SkillProducerPathKeyV1 key,
        out SkillProducerPathKeyParseReason reason)
    {
        key = default;

        if (string.IsNullOrEmpty(input))
        {
            reason = SkillProducerPathKeyParseReason.InputEmpty;
            return false;
        }

        byte[] inputUtf8;
        try
        {
            inputUtf8 = StrictUtf8.GetBytes(input);
        }
        catch (EncoderFallbackException)
        {
            reason = SkillProducerPathKeyParseReason.UnpairedSurrogate;
            return false;
        }

        if (inputUtf8.Length > MaximumInputUtf8Bytes)
        {
            reason = SkillProducerPathKeyParseReason.InputTooLarge;
            return false;
        }

        foreach (var rune in input.EnumerateRunes())
        {
            if (rune.Value <= 0x1f || rune.Value == 0x7f)
            {
                reason = SkillProducerPathKeyParseReason.ControlCharacter;
                return false;
            }
        }

        return platform == SkillProducerPathKeyPlatform.Windows
            ? TryParseWindows(input, out key, out reason)
            : TryParseLinux(input, out key, out reason);
    }

    public bool Equals(SkillProducerPathKeyV1 other)
    {
        if (Platform != other.Platform || driveLetter != other.driveLetter || segments.Length != other.segments.Length)
        {
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!string.Equals(segments[index], other.segments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is SkillProducerPathKeyV1 other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Platform);
        hash.Add(driveLetter);
        foreach (var segment in segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public bool IsStrictDescendantOf(SkillProducerPathKeyV1 ancestor)
    {
        if (Platform != ancestor.Platform || driveLetter != ancestor.driveLetter || segments.Length <= ancestor.segments.Length)
        {
            return false;
        }

        for (var index = 0; index < ancestor.segments.Length; index++)
        {
            if (!string.Equals(segments[index], ancestor.segments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseWindows(string input, out SkillProducerPathKeyV1 key, out SkillProducerPathKeyParseReason reason)
    {
        key = default;

        // Checked ahead of anchor shape so "C:/x" reports the forbidden separator, not a malformed anchor.
        if (input.Contains('/'))
        {
            reason = SkillProducerPathKeyParseReason.InvalidSeparator;
            return false;
        }

        if (input.Length < 3 || !IsAsciiLetter(input[0]) || input[1] != ':' || input[2] != '\\')
        {
            reason = SkillProducerPathKeyParseReason.InvalidAnchor;
            return false;
        }

        var driveLetter = char.ToUpperInvariant(input[0]);
        var remainder = input[3..];
        if (remainder.Length == 0)
        {
            reason = SkillProducerPathKeyParseReason.None;
            key = new SkillProducerPathKeyV1(SkillProducerPathKeyPlatform.Windows, driveLetter, [], $"{driveLetter}:\\");
            return true;
        }

        if (remainder[^1] == '\\')
        {
            reason = SkillProducerPathKeyParseReason.InvalidTrailingSeparator;
            return false;
        }

        var segments = remainder.Split('\\');
        foreach (var segment in segments)
        {
            if (!IsValidWindowsSegment(segment))
            {
                reason = SkillProducerPathKeyParseReason.InvalidSegment;
                return false;
            }
        }

        reason = SkillProducerPathKeyParseReason.None;
        key = new SkillProducerPathKeyV1(SkillProducerPathKeyPlatform.Windows, driveLetter, segments, $"{driveLetter}:\\{string.Join('\\', segments)}");
        return true;
    }

    private static bool TryParseLinux(string input, out SkillProducerPathKeyV1 key, out SkillProducerPathKeyParseReason reason)
    {
        key = default;

        // Checked ahead of anchor shape so a stray backslash reports the forbidden separator, not a malformed anchor.
        if (input.Contains('\\'))
        {
            reason = SkillProducerPathKeyParseReason.InvalidSeparator;
            return false;
        }

        if (input[0] != '/')
        {
            reason = SkillProducerPathKeyParseReason.InvalidAnchor;
            return false;
        }

        var remainder = input[1..];
        if (remainder.Length == 0)
        {
            reason = SkillProducerPathKeyParseReason.None;
            key = new SkillProducerPathKeyV1(SkillProducerPathKeyPlatform.Linux, '\0', [], "/");
            return true;
        }

        if (remainder[^1] == '/')
        {
            reason = SkillProducerPathKeyParseReason.InvalidTrailingSeparator;
            return false;
        }

        var segments = remainder.Split('/');
        foreach (var segment in segments)
        {
            if (!IsValidLinuxSegment(segment))
            {
                reason = SkillProducerPathKeyParseReason.InvalidSegment;
                return false;
            }
        }

        reason = SkillProducerPathKeyParseReason.None;
        key = new SkillProducerPathKeyV1(SkillProducerPathKeyPlatform.Linux, '\0', segments, $"/{string.Join('/', segments)}");
        return true;
    }

    private static bool IsValidWindowsSegment(string segment)
    {
        if (segment.Length is 0 or > MaximumWindowsSegmentCodeUnits || segment == "." || segment == "..")
        {
            return false;
        }

        if (segment.IndexOfAny(ForbiddenWindowsSegmentCharacters) >= 0)
        {
            return false;
        }

        if (segment[^1] is '.' or ' ')
        {
            return false;
        }

        var dotIndex = segment.IndexOf('.');
        var stem = dotIndex < 0 ? segment : segment[..dotIndex];
        return !ReservedWindowsStems.Contains(stem);
    }

    private static bool IsValidLinuxSegment(string segment) =>
        segment.Length != 0 && segment != "." && segment != ".." && StrictUtf8.GetByteCount(segment) <= MaximumLinuxSegmentUtf8Bytes;

    private static bool IsAsciiLetter(char value) => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
