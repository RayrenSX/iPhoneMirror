namespace IPhoneMirror.App.Updater;

internal readonly record struct SemanticVersion(
    int Major, int Minor, int Patch, string? Prerelease = null)
    : IComparable<SemanticVersion>
{
    internal bool IsPrerelease => !string.IsNullOrWhiteSpace(Prerelease);

    internal static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
            candidate = candidate[1..];
        var buildIndex = candidate.IndexOf('+');
        if (buildIndex >= 0) candidate = candidate[..buildIndex];
        string? prerelease = null;
        var prereleaseIndex = candidate.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = candidate[(prereleaseIndex + 1)..];
            candidate = candidate[..prereleaseIndex];
            if (!IsValidPrerelease(prerelease)) return false;
        }
        var parts = candidate.Split('.');
        if (parts.Length != 3 ||
            !TryParseNumber(parts[0], out var major) ||
            !TryParseNumber(parts[1], out var minor) ||
            !TryParseNumber(parts[2], out var patch))
            return false;
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    internal static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version :
            throw new FormatException($"Invalid semantic version: {value}");

    public int CompareTo(SemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;
        var left = Prerelease!.Split('.');
        var right = other.Prerelease!.Split('.');
        for (var index = 0; index < Math.Max(left.Length, right.Length); ++index)
        {
            if (index >= left.Length) return -1;
            if (index >= right.Length) return 1;
            var leftNumeric = left[index].All(char.IsAsciiDigit);
            var rightNumeric = right[index].All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric
                ? CompareNumericIdentifier(left[index], right[index])
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.Compare(left[index], right[index], StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? $"-{Prerelease}" : string.Empty);

    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    private static bool TryParseNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0') &&
            int.TryParse(value, out number) && number >= 0;
    }

    private static bool IsValidPrerelease(string value) =>
        value.Length > 0 && value.Split('.').All(identifier =>
            identifier.Length > 0 && identifier.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!identifier.All(char.IsAsciiDigit) || identifier.Length == 1 ||
             identifier[0] != '0'));
}
