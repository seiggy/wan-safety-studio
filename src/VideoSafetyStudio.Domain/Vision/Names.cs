using System.Text.RegularExpressions;

namespace VideoSafetyStudio.Domain.Vision;

public static partial class Names
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex FramePattern();

    public static string Check(string? value, string what = "name") =>
        value is not null && NamePattern().IsMatch(value)
            ? value
            : throw new ValidationException($"{what} must be 2-63 lowercase letters, digits or hyphens.");

    public static string CheckFrame(string? value) =>
        value is not null && FramePattern().IsMatch(value) ? value : throw new ValidationException("Invalid frame id.");
}
